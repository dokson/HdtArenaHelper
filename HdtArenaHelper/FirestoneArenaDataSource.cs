using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;

namespace HdtArenaHelper
{
	/// <summary>
	/// Real arena card win-rates from Firestone's public per-class CDN files:
	///
	///   https://static.zerotoheroes.com/api/arena/stats/cards/arena-underground/last-patch/&lt;class&gt;.gz.json
	///
	/// Response shape (one file per class):
	///   { "lastUpdated": ..., "context": "mage", "stats": [
	///     { "cardId": "LOOT_413", "stats": { "drawn": 2242, "drawnThenWin": 1201, ... } }, ... ] }
	///
	/// The second REAL win-rate source next to HSReplay: it de-risks the blend (HSReplay
	/// is reached through a curl workaround that Cloudflare could close any day) and its
	/// independent sample reduces variance where both sources cover a card.
	///
	/// Signal: drawnThenWin / drawn — the same drawn-win-rate metric HSReplay uses, so the
	/// two sources measure the same quantity. Scoring follows the shared pipeline
	/// (<see cref="ScoreMath"/>): shrink -> median/MAD logistic, per class and pooled:
	///   - card score (class unknown): rate pooled across all class files;
	///   - card score (class known): the rate in that class's file, shrunk toward the
	///     card's pooled rate, normalized within the class;
	///   - class tier (hero pick): unweighted mean of the class's shrunk card rates.
	///
	/// Plain .NET download (a static CDN — no Cloudflare fingerprinting here), one file
	/// per class, each cached with a 1-day TTL; classes fail soft and independently.
	/// </summary>
	public class FirestoneArenaDataSource : IArenaDataSource
	{
		// The Underground pool is used for BOTH arena modes on purpose: a card's drawn
		// win-rate reflects its intrinsic quality, which is ~mode-invariant, and a classic
		// card missing from this pool just falls back to HSReplay / the heuristic.
		private const string EndpointTemplate =
			"https://static.zerotoheroes.com/api/arena/stats/cards/arena-underground/last-patch/{0}.gz.json";

		private static readonly string[] DefaultClasses =
		{
			"deathknight", "demonhunter", "druid", "hunter", "mage", "paladin",
			"priest", "rogue", "shaman", "warlock", "warrior",
		};

		private const string BrowserUserAgent =
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
			"(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

		private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

		private readonly string _cacheDir;
		private readonly IReadOnlyList<string> _classes;

		private sealed class Data
		{
			public Dictionary<int, SourceScore> CardScore { get; }   // dbfId -> score+draws (pooled)
			public Dictionary<CardClass, double> ClassScore { get; } // class -> 0..100 (tier list)
			public Dictionary<CardClass, Dictionary<int, SourceScore>> ClassCardScore { get; }
			public Dictionary<int, CardClass> HeroClass { get; }
			/// <summary>True when every class file backed this bundle. Lives INSIDE the
			/// bundle so completeness and contents publish atomically through the one
			/// volatile reference — a separate flag could be observed true against a
			/// stale partial bundle.</summary>
			public bool IsComplete { get; }

			public Data(
				Dictionary<int, SourceScore> cardScore,
				Dictionary<CardClass, double> classScore,
				Dictionary<CardClass, Dictionary<int, SourceScore>> classCardScore,
				Dictionary<int, CardClass> heroClass,
				bool isComplete)
			{
				CardScore = cardScore;
				ClassScore = classScore;
				ClassCardScore = classCardScore;
				HeroClass = heroClass;
				IsComplete = isComplete;
			}
		}

		private volatile Data? _data;

		// Classes parsed so far, kept across EnsureLoadedAsync calls so a class that
		// failed on one attempt (network blip) is retried without refetching the rest.
		private readonly object _classLock = new object();
		private readonly Dictionary<CardClass, Dictionary<int, (double Rate, int Games)>> _loaded =
			new Dictionary<CardClass, Dictionary<int, (double Rate, int Games)>>();

		/// <param name="cacheDir">Directory for the per-class cache files (1-day TTL).</param>
		/// <param name="weight">Relative blend weight; 0 disables the source.</param>
		/// <param name="classes">Class-file names to fetch; null = all 11 (tests inject a
		/// subset so no cache miss ever reaches the network).</param>
		public FirestoneArenaDataSource(string cacheDir, double weight = 1.0,
			IReadOnlyList<string>? classes = null)
		{
			Directory.CreateDirectory(cacheDir);
			_cacheDir = cacheDir;
			_classes = classes ?? DefaultClasses;
			Weight = weight;
		}

		public string Name => "Firestone Arena";
		public double Weight { get; }

		/// <summary>
		/// Complete only when EVERY class file has loaded — partial data is already
		/// published (and scoreable) before that, but reporting loaded too early would
		/// stop the caller's warm-up retries and freeze the missing classes on their
		/// fallback for the whole session.
		/// </summary>
		public bool IsLoaded => _data?.IsComplete == true;

		/// <summary>Per-class 0-100 tier scores (the hero-pick tier list), if loaded.</summary>
		public IReadOnlyDictionary<CardClass, double>? ClassScores => _data?.ClassScore;

		public SourceScore? GetNormalizedScore(int dbfId, CardClass draftClass = CardClass.INVALID)
		{
			var data = _data; // read the published bundle once
			if(data == null)
				return null;

			// Hero pick: rank the offered classes instead of a single card. A tier is
			// backed by a whole class file, not one card's sample: no games discount.
			if(data.HeroClass.TryGetValue(dbfId, out var heroClass))
				return data.ClassScore.TryGetValue(heroClass, out var cs)
					? new SourceScore(cs)
					: (SourceScore?)null;

			// Known drafted class: prefer the card's rate in that class's own file.
			if(draftClass != CardClass.INVALID
				&& data.ClassCardScore.TryGetValue(draftClass, out var perClass)
				&& perClass.TryGetValue(dbfId, out var classScore))
				return classScore;

			return data.CardScore.TryGetValue(dbfId, out var score) ? score : (SourceScore?)null;
		}

		public async Task EnsureLoadedAsync()
		{
			if(IsLoaded)
				return;

			// HearthDb maps card ids -> dbf ids; not populated right after HDT starts.
			// Defer and let the caller's warm-up loop retry.
			if(HearthDb.Cards.All.Count == 0)
			{
				Log("HearthDb not ready yet; deferring load");
				return;
			}

			// Fetch only the classes still missing; each file caches and fails
			// independently, so one bad file costs one class, not the source.
			List<string> missing;
			lock(_classLock)
			{
				missing = _classes
					.Where(cls => !Enum.TryParse<CardClass>(cls, ignoreCase: true, out var cc)
						|| !_loaded.ContainsKey(cc))
					.ToList();
			}

			var gotNew = false;
			var fetches = missing.Select(async cls =>
			{
				var (json, fromCache) = await LoadClassJsonAsync(cls).ConfigureAwait(false);
				if(json == null)
					return;
				if(!Enum.TryParse<CardClass>(cls, ignoreCase: true, out var cardClass))
					return;
				var rates = ParseClassFile(json);
				if(rates.Count == 0)
				{
					// A fresh-but-unusable cache file (torn write, CDN error page) would
					// otherwise wedge this class for the whole TTL: drop it so the next
					// warm-up attempt reaches the network instead of rereading garbage.
					if(fromCache)
						TryDeleteCache(cls);
					return;
				}
				lock(_classLock)
				{
					_loaded[cardClass] = rates;
					gotNew = true;
				}
			});
			await Task.WhenAll(fetches).ConfigureAwait(false);

			Dictionary<CardClass, Dictionary<int, (double Rate, int Games)>> snapshot;
			lock(_classLock)
			{
				if(!gotNew)
				{
					if(_data == null)
						Log("no class files available (cache miss + download failed)");
					return;
				}
				snapshot = new Dictionary<CardClass, Dictionary<int, (double Rate, int Games)>>(_loaded);
			}

			var data = BuildData(snapshot, isComplete: snapshot.Count >= _classes.Count);
			_data = data; // single volatile publication: contents + completeness together
			Log($"loaded {data.CardScore.Count} cards across {snapshot.Count}/{_classes.Count} classes");
		}

		// ---- scoring -------------------------------------------------------------

		private static Data BuildData(
			IReadOnlyDictionary<CardClass, Dictionary<int, (double Rate, int Games)>> perClass,
			bool isComplete)
		{
			// Pooled rate per card: total wins / total draws across all class files.
			// The noise floor applies HERE, on the pooled sample — a card drawn a few
			// times in each of several classes is still well-measured overall.
			var drawn = new Dictionary<int, int>();
			var wins = new Dictionary<int, double>(); // percent-games (rate * games)
			foreach(var cls in perClass.Values)
			{
				foreach(var kv in cls)
				{
					drawn.TryGetValue(kv.Key, out var d);
					wins.TryGetValue(kv.Key, out var w);
					drawn[kv.Key] = d + kv.Value.Games;
					wins[kv.Key] = w + kv.Value.Rate * kv.Value.Games;
				}
			}
			var pooledRate = drawn.Where(kv => kv.Value >= ScoreMath.MinGames)
				.ToDictionary(kv => kv.Key, kv => wins[kv.Key] / kv.Value);
			var globalMean = pooledRate.Count > 0 ? pooledRate.Values.Average() : 50.0;

			var pooledShrunk = pooledRate.ToDictionary(
				kv => kv.Key,
				kv => ScoreMath.Shrink(kv.Value, drawn[kv.Key], globalMean));

			// ONE scale for every card score, anchored on the pooled distribution, so
			// class-context scores and pooled fallbacks stay comparable within a pick.
			var center = ScoreMath.Median(pooledShrunk.Values);
			var sigma = ScoreMath.RobustSigma(pooledShrunk.Values, center);

			var classCardScore = new Dictionary<CardClass, Dictionary<int, SourceScore>>(perClass.Count);
			var classTier = new Dictionary<CardClass, double>(perClass.Count);
			foreach(var cls in perClass)
			{
				// Class rate shrunk toward a leave-that-class-out prior (the pooled rate
				// minus this class's games — a prior containing the observation would
				// double-count it), so thin class samples glide to the class-agnostic
				// estimate. Per-class noise floor applies here.
				var shrunk = new Dictionary<int, double>();
				foreach(var kv in cls.Value)
				{
					if(kv.Value.Games < ScoreMath.MinGames)
						continue;
					var looDrawn = drawn[kv.Key] - kv.Value.Games;
					double target;
					if(looDrawn >= ScoreMath.MinGames)
						target = (wins[kv.Key] - kv.Value.Rate * kv.Value.Games) / looDrawn;
					else if(pooledShrunk.TryGetValue(kv.Key, out var pooled))
						target = pooled;
					else
						target = globalMean;
					shrunk[kv.Key] = ScoreMath.Shrink(kv.Value.Rate, kv.Value.Games, target);
				}
				if(shrunk.Count == 0)
					continue;
				classCardScore[cls.Key] = ScoreMath.ToScores(shrunk, center, sigma).ToDictionary(
					e => e.Key, e => new SourceScore(e.Value, cls.Value[e.Key].Games));
				// Unweighted mean: games-weighting would bias each class upward by its
				// most-played cards (popularity correlates with win-rate).
				classTier[cls.Key] = shrunk.Values.Average();
			}

			// The draws behind each pooled score travel with it for precision weighting.
			var cardScore = ScoreMath.ToScores(pooledShrunk, center, sigma).ToDictionary(
				e => e.Key, e => new SourceScore(e.Value, drawn[e.Key]));

			return new Data(
				cardScore,
				ScoreMath.ToScores(classTier),
				classCardScore,
				HeroSkins.BuildClassMap(),
				isComplete);
		}

		/// <summary>dbf -> (drawn win-rate %, drawn games) for one class file. No noise
		/// floor here — pooled and per-class floors are applied downstream, each on the
		/// sample that actually backs the estimate.</summary>
		private static Dictionary<int, (double Rate, int Games)> ParseClassFile(string json)
		{
			var result = new Dictionary<int, (double, int)>();
			try
			{
				if(!(JObject.Parse(json)["stats"] is JArray stats))
					return result;

				foreach(var entry in stats)
				{
					var cardId = (string?)entry["cardId"];
					if(string.IsNullOrEmpty(cardId))
						continue;
					if(!HearthDb.Cards.All.TryGetValue(cardId, out var dbCard) || dbCard.DbfId == 0)
						continue;

					var s = entry["stats"];
					var games = (int?)s?["drawn"] ?? 0;
					var winsCount = (double?)s?["drawnThenWin"] ?? 0;
					if(games <= 0)
						continue;
					// Duplicate card ids: keep the entry backed by the most games.
					if(result.TryGetValue(dbCard.DbfId, out var existing) && existing.Item2 >= games)
						continue;

					result[dbCard.DbfId] = (winsCount / games * 100.0, games);
				}
			}
			catch(Exception ex)
			{
				Log($"class file parse failed: {ex.Message}");
			}
			return result;
		}

		// ---- io ------------------------------------------------------------------

		private string CacheFile(string cls) => Path.Combine(_cacheDir, $"firestone_{cls}.json");

		/// <returns>The class json and whether it came from the cache (a cached file that
		/// later fails to parse is deleted by the caller so the next attempt downloads).</returns>
		private async Task<(string? Json, bool FromCache)> LoadClassJsonAsync(string cls)
		{
			var cacheFile = CacheFile(cls);
			try
			{
				if(File.Exists(cacheFile) &&
				   DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) <= CacheMaxAge)
					return (File.ReadAllText(cacheFile), true);
			}
			catch(Exception ex)
			{
				Log($"cache read failed ({cls}): {ex.Message}");
			}

			var json = await DownloadAsync(cls).ConfigureAwait(false);
			if(json == null)
			{
				// Download failed: fall back to a stale (>TTL) cache for this class rather
				// than dropping it — day-old win-rates beat none when the network is down.
				try
				{
					if(File.Exists(cacheFile))
						return (File.ReadAllText(cacheFile), true);
				}
				catch(Exception ex) { Log($"stale cache read failed ({cls}): {ex.Message}"); }
				return (null, false);
			}

			try
			{
				// Atomic swap so a concurrent reader (a superseded warm-up loop still
				// finishing against old source instances) sees the old or the new file,
				// never a torn write.
				var tmp = cacheFile + ".tmp";
				File.WriteAllText(tmp, json);
				if(File.Exists(cacheFile))
					File.Replace(tmp, cacheFile, null);
				else
					File.Move(tmp, cacheFile);
			}
			catch(Exception ex) { Log($"cache write failed ({cls}): {ex.Message}"); }
			return (json, false);
		}

		private void TryDeleteCache(string cls)
		{
			try
			{
				File.Delete(CacheFile(cls));
				Log($"dropped unusable cache for {cls}; will re-download");
			}
			catch(Exception ex)
			{
				Log($"cache delete failed ({cls}): {ex.Message}");
			}
		}

		private static async Task<string?> DownloadAsync(string cls)
		{
			try
			{
				ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
				using(var client = new WebClient())
				{
					client.Headers[HttpRequestHeader.UserAgent] = BrowserUserAgent;
					client.Headers[HttpRequestHeader.AcceptEncoding] = "gzip";
					var bytes = await client
						.DownloadDataTaskAsync(string.Format(EndpointTemplate, cls))
						.ConfigureAwait(false);
					return Decode(bytes);
				}
			}
			catch(Exception ex)
			{
				Log($"download failed ({cls}): {ex.Message}");
				return null;
			}
		}

		// The CDN may hand us gzip bytes (we ask for them — ~10x smaller on the wire, and
		// the file is pre-compressed server-side) or plain JSON; detect by magic number
		// rather than trusting headers.
		private static string Decode(byte[] bytes)
		{
			if(bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
			{
				using(var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
				using(var reader = new StreamReader(gz, Encoding.UTF8))
					return reader.ReadToEnd();
			}
			return Encoding.UTF8.GetString(bytes);
		}

		private static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] Firestone: {msg}");
	}
}
