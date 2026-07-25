using System;
using System.Collections.Generic;
using System.IO;
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
	public class FirestoneArenaDataSource : IArenaDataSource, IClassWinRateSource, IMulliganStatsSource
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
			public Dictionary<CardClass, double> ClassWinRate { get; } // class -> estimated win-rate %
			public Dictionary<CardClass, Dictionary<int, MulliganCardStats>> Mulligan { get; }
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
				Dictionary<CardClass, double> classWinRate,
				Dictionary<CardClass, Dictionary<int, MulliganCardStats>> mulligan,
				Dictionary<CardClass, Dictionary<int, SourceScore>> classCardScore,
				Dictionary<int, CardClass> heroClass,
				bool isComplete)
			{
				CardScore = cardScore;
				ClassScore = classScore;
				ClassWinRate = classWinRate;
				Mulligan = mulligan;
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
		// Deck-level tally per class, for the estimated class win-rate. Separate from _loaded
		// because it counts DECKS containing the card, not draws, and the two must not be mixed.
		private readonly Dictionary<CardClass, (double Wins, double Games)> _deckTally =
			new Dictionary<CardClass, (double Wins, double Games)>();
		// Mulligan counters per class: same files, a different question, so a separate map.
		private readonly Dictionary<CardClass, Dictionary<int, MulliganTally>> _mulligan =
			new Dictionary<CardClass, Dictionary<int, MulliganTally>>();

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

		/// <summary>Real arena win-rates: every score carries the games behind it.</summary>
		public bool HasSamples => true;

		/// <summary>Per-class 0-100 tier scores (the hero-pick tier list), if loaded.</summary>
		public IReadOnlyDictionary<CardClass, double>? ClassScores => _data?.ClassScore;

		/// <summary>Per-class estimated arena win-rate in percentage points, if loaded.</summary>
		public IReadOnlyDictionary<CardClass, double>? ClassWinRates => _data?.ClassWinRate;

		/// <inheritdoc/>
		public MulliganCardStats? GetMulliganStats(CardClass cls, int dbfId)
		{
			var data = _data;
			if(data == null || !data.Mulligan.TryGetValue(cls, out var cards))
				return null;
			return cards.TryGetValue(CardIdentity.Canonical(dbfId), out var stats)
				? stats
				: (MulliganCardStats?)null;
		}

		public SourceScore? GetNormalizedScore(int dbfId, CardClass draftClass = CardClass.INVALID)
		{
			var data = _data; // read the published bundle once
			if(data == null)
				return null;

			// Hero pick: rank the offered classes instead of a single card. A tier is
			// backed by a whole class file, not one card's sample: no games discount.
			// Before canonicalizing, so a hero skin is not collapsed onto its base hero.
			if(data.HeroClass.TryGetValue(dbfId, out var heroClass))
				return data.ClassScore.TryGetValue(heroClass, out var cs)
					? new SourceScore(cs)
					: (SourceScore?)null;

			// The client reports whichever printing it has; the parsed maps are keyed by canonical.
			var canonical = CardIdentity.Canonical(dbfId);

			// Known drafted class: prefer the card's rate in that class's own file.
			if(draftClass != CardClass.INVALID
				&& data.ClassCardScore.TryGetValue(draftClass, out var perClass)
				&& perClass.TryGetValue(canonical, out var classScore))
				return classScore;

			return data.CardScore.TryGetValue(canonical, out var score) ? score : (SourceScore?)null;
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
				var tally = ParseClassDeckTally(json);
				var mulligan = ParseMulliganFile(json);
				lock(_classLock)
				{
					_loaded[cardClass] = rates;
					if(tally.Games > 0)
						_deckTally[cardClass] = tally;
					if(mulligan.Count > 0)
						_mulligan[cardClass] = mulligan;
					gotNew = true;
				}
			});
			await Task.WhenAll(fetches).ConfigureAwait(false);

			Dictionary<CardClass, Dictionary<int, (double Rate, int Games)>> snapshot;
			Dictionary<CardClass, (double Wins, double Games)> tallies;
			Dictionary<CardClass, Dictionary<int, MulliganTally>> mulliganSnapshot;
			lock(_classLock)
			{
				if(!gotNew)
				{
					if(_data == null)
						Log("no class files available (cache miss + download failed)");
					return;
				}
				snapshot = new Dictionary<CardClass, Dictionary<int, (double Rate, int Games)>>(_loaded);
				tallies = new Dictionary<CardClass, (double Wins, double Games)>(_deckTally);
				mulliganSnapshot = new Dictionary<CardClass, Dictionary<int, MulliganTally>>(_mulligan);
			}

			var data = BuildData(snapshot, tallies, mulliganSnapshot,
				isComplete: snapshot.Count >= _classes.Count);
			_data = data; // single volatile publication: contents + completeness together
			Log($"loaded {data.CardScore.Count} cards across {snapshot.Count}/{_classes.Count} classes");
		}

		// ---- scoring -------------------------------------------------------------

		private static Data BuildData(
			IReadOnlyDictionary<CardClass, Dictionary<int, (double Rate, int Games)>> perClass,
			IReadOnlyDictionary<CardClass, (double Wins, double Games)> deckTallies,
			IReadOnlyDictionary<CardClass, Dictionary<int, MulliganTally>> mulliganTallies,
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
					var fallback = pooledShrunk.TryGetValue(kv.Key, out var pooled) ? pooled : globalMean;
					// Same policy as HSReplay's path, from one implementation: see
					// ScoreMath.LeaveOneOutTarget for why the remainder is range-guarded.
					var target = ScoreMath.LeaveOneOutTarget(wins[kv.Key] / drawn[kv.Key],
						drawn[kv.Key], kv.Value.Rate, kv.Value.Games, fallback);
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
				ScoreMath.RecentreClassWinRates(deckTallies),
				BuildMulligan(mulliganTallies),
				classCardScore,
				HeroSkins.BuildClassMap(),
				isComplete);
		}

		/// <summary>
		/// One class file's DECK-level tally for the estimated class win-rate:
		/// <c>decksWithCardThenWin</c> over <c>decksWithCard</c>, summed over the class's cards.
		/// Deliberately a different quantity from the drawn rate the card scores use — the target
		/// is a whole deck's win-rate, and every game is counted once per card the deck contained.
		/// Duplicate card ids are summed rather than deduped: unlike a per-card rate, adding the
		/// same card twice only re-weights the pool slightly, while dropping the larger entry would
		/// bias it. The offset this introduces is removed by
		/// <see cref="ScoreMath.RecentreClassWinRates"/> anyway.
		/// </summary>
		private static (double Wins, double Games) ParseClassDeckTally(string json)
		{
			double wins = 0, games = 0;
			try
			{
				if(!(PayloadGuard.ParseObject(json)?["stats"] is JArray stats))
					return (0, 0);

				foreach(var entry in stats)
				{
					var s = entry["stats"];
					var decks = (double?)s?["decksWithCard"] ?? 0;
					if(decks <= 0)
						continue;
					wins += (double?)s?["decksWithCardThenWin"] ?? 0;
					games += decks;
				}
			}
			catch(Exception ex)
			{
				Log($"class deck tally parse failed: {ex.Message}");
				return (0, 0);
			}
			return (wins, games);
		}


		/// <summary>
		/// One class file's MULLIGAN tallies per canonical card: the keep decision and its outcome.
		///   - keep win-rate = <c>inHandAfterMulliganThenWin / inHandAfterMulligan</c>;
		///   - keep rate     = <c>keptInMulligan / drawnBeforeMulligan</c>.
		/// Raw counts only — shrinkage happens in <see cref="BuildMulligan"/> on the merged sample,
		/// because reprints must pool before anything is smoothed.
		/// </summary>
		private static Dictionary<int, MulliganTally> ParseMulliganFile(string json)
		{
			var result = new Dictionary<int, MulliganTally>();
			try
			{
				if(!(PayloadGuard.ParseObject(json)?["stats"] is JArray stats))
					return result;

				foreach(var entry in stats)
				{
					var cardId = (string?)entry["cardId"];
					if(string.IsNullOrEmpty(cardId))
						continue;
					if(!HearthDb.Cards.All.TryGetValue(cardId, out var dbCard) || dbCard.DbfId == 0)
						continue;
					var dbf = CardIdentity.Canonical(dbCard.DbfId);

					var s = entry["stats"];
					var kept = (double?)s?["inHandAfterMulligan"] ?? 0;
					var keptWins = (double?)s?["inHandAfterMulliganThenWin"] ?? 0;
					var keptCount = (double?)s?["keptInMulligan"] ?? 0;
					var offered = (double?)s?["drawnBeforeMulligan"] ?? 0;
					if(kept <= 0 && offered <= 0)
						continue;

					result.TryGetValue(dbf, out var seen);
					result[dbf] = new MulliganTally(seen.KeptGames + kept, seen.KeptWins + keptWins,
						seen.Kept + keptCount, seen.Offered + offered);
				}
			}
			catch(Exception ex)
			{
				Log($"mulligan parse failed: {ex.Message}");
			}
			return result;
		}

		/// <summary>Raw mulligan counts for one card in one class, before any smoothing.</summary>
		private readonly struct MulliganTally
		{
			public readonly double KeptGames;
			public readonly double KeptWins;
			public readonly double Kept;
			public readonly double Offered;
			public MulliganTally(double keptGames, double keptWins, double kept, double offered)
			{
				KeptGames = keptGames;
				KeptWins = keptWins;
				Kept = kept;
				Offered = offered;
			}
		}

		/// <summary>
		/// Turns raw per-class tallies into shrunk, displayable stats. Shrinkage is not optional
		/// here: measured on one payload, a class has between 69 and 268 cards with 30+ keep
		/// observations, and Warrior has only 13 above 100 — a raw rate off 30 games swings ~9pp on
		/// noise alone. Each card is pulled toward its CLASS's pooled rate, so a thin card says
		/// "roughly average for this class" instead of asserting an outlier.
		///
		/// Cards under <see cref="ScoreMath.MinGames"/> keep observations are dropped entirely: at the
		/// mulligan, no number is better than a number nobody can act on.
		/// </summary>
		private static Dictionary<CardClass, Dictionary<int, MulliganCardStats>> BuildMulligan(
			IReadOnlyDictionary<CardClass, Dictionary<int, MulliganTally>> perClass)
		{
			var result = new Dictionary<CardClass, Dictionary<int, MulliganCardStats>>(perClass.Count);
			foreach(var cls in perClass)
			{
				double poolWins = 0, poolGames = 0, poolKept = 0, poolOffered = 0;
				foreach(var t in cls.Value.Values)
				{
					poolWins += t.KeptWins;
					poolGames += t.KeptGames;
					poolKept += t.Kept;
					poolOffered += t.Offered;
				}
				if(poolGames <= 0)
					continue;
				var classKeepWr = 100.0 * poolWins / poolGames;
				var classKeepRate = poolOffered > 0 ? 100.0 * poolKept / poolOffered : 50.0;

				var cards = new Dictionary<int, MulliganCardStats>();
				foreach(var kv in cls.Value)
				{
					var t = kv.Value;
					if(t.KeptGames < ScoreMath.MinGames)
						continue;
					var keepWr = ScoreMath.Shrink(100.0 * t.KeptWins / t.KeptGames,
						(int)t.KeptGames, classKeepWr);
					var keepRate = t.Offered >= ScoreMath.MinGames
						? ScoreMath.Shrink(100.0 * t.Kept / t.Offered, (int)t.Offered, classKeepRate)
						: classKeepRate;
					cards[kv.Key] = new MulliganCardStats(keepWr, keepRate, (int)t.KeptGames, classKeepWr);
				}
				if(cards.Count > 0)
					result[cls.Key] = cards;
			}
			return result;
		}

		/// <summary>dbf -> (drawn win-rate %, drawn games) for one class file. No noise
		/// floor here — pooled and per-class floors are applied downstream, each on the
		/// sample that actually backs the estimate.</summary>
		private static Dictionary<int, (double Rate, int Games)> ParseClassFile(string json)
		{
			var result = new Dictionary<int, (double, int)>();
			try
			{
				if(!(PayloadGuard.ParseObject(json)?["stats"] is JArray stats))
					return result;

				foreach(var entry in stats)
				{
					var cardId = (string?)entry["cardId"];
					if(string.IsNullOrEmpty(cardId))
						continue;
					if(!HearthDb.Cards.All.TryGetValue(cardId, out var dbCard) || dbCard.DbfId == 0)
						continue;
					var dbf = CardIdentity.Canonical(dbCard.DbfId);

					var s = entry["stats"];
					var games = (int?)s?["drawn"] ?? 0;
					var winsCount = (double?)s?["drawnThenWin"] ?? 0;
					if(games <= 0)
						continue;

					// REPRINTS ARE SUMMED as counts. The feeds disagree on which printing to report,
					// so the same card arrives here (and from HSReplay) under different ids; keeping
					// only the larger entry discarded real games, and averaging the two RATES would
					// weight a thin printing like a thick one.
					if(result.TryGetValue(dbf, out var existing))
					{
						var totalGames = existing.Item2 + games;
						var totalWins = existing.Item1 / 100.0 * existing.Item2 + winsCount;
						result[dbf] = (totalWins / totalGames * 100.0, totalGames);
						continue;
					}

					result[dbf] = (winsCount / games * 100.0, games);
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
			// Both paths are BOUNDED: this is third-party input, and a few KB of gzip can expand to
			// gigabytes. An empty string reads downstream as "no usable data", which is already the
			// fail-soft path (cache, other source, heuristic).
			if(bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
				return PayloadGuard.Gunzip(bytes) ?? "";
			return bytes.Length > PayloadGuard.MaxPayloadBytes ? "" : Encoding.UTF8.GetString(bytes);
		}

		private static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] Firestone: {msg}");
	}
}
