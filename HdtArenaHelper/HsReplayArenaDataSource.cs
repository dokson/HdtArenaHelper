using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;

namespace HdtArenaHelper
{
	/// <summary>
	/// Real arena card win-rates from HSReplay's public, unauthenticated arena
	/// endpoint:
	///
	///   https://hsreplay.net/api/v1/arena/card_stats/free/?format=json
	///
	/// Response shape:
	///   { "data": { "ALL": [ { "card_id": "EX1_050", "drawn_win_rate": 49.7,
	///     "win_rate": 50.1, "popularity": 1.4, "num_games": 2027, ... }, ... ],
	///     "&lt;Class&gt;": [ ... ] } }
	///
	/// Scoring pipeline (per an independent statistical review; verified on live data):
	///   1. Signal: use <c>drawn_win_rate</c> (win-rate of games where the card was
	///      actually drawn), a less deck-strength-confounded measure than the deck's
	///      included <c>win_rate</c>. Falls back to <c>win_rate</c> if absent.
	///   2. Shrinkage: pull each card's rate toward the global mean by sample size —
	///      empirical Bayes with a <see cref="ShrinkGames"/>-game prior — so a 12-game
	///      card no longer asserts an extreme rate (and can't anchor the scale).
	///   3. Normalization: a logistic curve anchored at the robust CENTRE (median of the
	///      shrunk rates) with a robust SPREAD (MAD). The median card maps to 50 so the
	///      score is comparable with the heuristic source in the blend, and the scale is
	///      immune to outliers and stable across daily refreshes. (This replaces a
	///      min-max mapping that pinned 0 to a single noise card.)
	///
	/// The per-class buckets are scored the same way into a class tier list; at the
	/// hero-pick step the offered HERO_* skins are rated by their class's tier, so the
	/// overlay doubles as a class picker.
	///
	/// A realistic browser User-Agent is required or Cloudflare serves a challenge.
	/// Cached with a 1-day TTL.
	/// </summary>
	public class HsReplayArenaDataSource : IArenaDataSource
	{
		private const string Endpoint =
			"https://hsreplay.net/api/v1/arena/card_stats/free/?format=json";

		// Soft floor: below this a rate is pure noise even after shrinkage.
		private const int MinGames = 10;
		// Empirical-Bayes prior strength, in pseudo-games, toward the global mean.
		private const int ShrinkGames = 60;
		// Logistic slope that makes 100/(1+e^-1.702z) approximate the normal CDF, so a
		// card one robust-SD above the median scores ~85.
		private const double LogisticSlope = 1.702;
		private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

		// A full browser User-Agent alone is NOT enough: Cloudflare fingerprints the
		// TLS/HTTP ClientHello and 403s .NET's stack (WebClient and HttpClient alike,
		// regardless of headers — verified against the live endpoint), while curl is let
		// through. curl ships in %SystemRoot%\System32 on Windows 10 1803+ (every client
		// that can run Hearthstone), so it is our primary fetch path.
		private const string BrowserUserAgent =
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
			"(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

		private readonly string _cacheFile;

		/// <summary>
		/// The loaded scores as one immutable bundle, published through a single
		/// <c>volatile</c> reference so a reader on the poll thread never sees a half-written
		/// set (e.g. card scores live but class scores not yet visible).
		/// </summary>
		private sealed class Data
		{
			public Dictionary<int, ArenaCardScore> Raw { get; }
			public Dictionary<int, double> CardScore { get; }        // dbfId -> 0..100
			public Dictionary<CardClass, double> ClassScore { get; } // class -> 0..100
			public Dictionary<int, CardClass> HeroClass { get; }     // hero-skin dbfId -> class

			public Data(
				Dictionary<int, ArenaCardScore> raw,
				Dictionary<int, double> cardScore,
				Dictionary<CardClass, double> classScore,
				Dictionary<int, CardClass> heroClass)
			{
				Raw = raw;
				CardScore = cardScore;
				ClassScore = classScore;
				HeroClass = heroClass;
			}
		}

		private volatile Data? _data;

		public HsReplayArenaDataSource(string cacheDir, double weight = 1.0)
		{
			Directory.CreateDirectory(cacheDir);
			_cacheFile = Path.Combine(cacheDir, "hsreplay_arena.json");
			Weight = weight;
		}

		public string Name => "HSReplay Arena";
		public double Weight { get; }
		public bool IsLoaded => _data != null;

		/// <summary>Raw per-card stats (drawn win-rate, popularity, games), if loaded.</summary>
		public ArenaCardScore? GetRaw(int dbfId)
		{
			var data = _data;
			return data != null && data.Raw.TryGetValue(dbfId, out var s) ? s : null;
		}

		/// <summary>Per-class 0-100 tier scores (the hero-pick tier list), if loaded.</summary>
		public IReadOnlyDictionary<CardClass, double>? ClassScores => _data?.ClassScore;

		public double? GetNormalizedScore(int dbfId)
		{
			var data = _data; // read the published bundle once
			if(data == null)
				return null;

			// Hero pick: rank the offered classes instead of a single card.
			if(data.HeroClass.TryGetValue(dbfId, out var heroClass))
				return data.ClassScore.TryGetValue(heroClass, out var cs) ? cs : (double?)null;

			return data.CardScore.TryGetValue(dbfId, out var score) ? score : (double?)null;
		}

		public async Task EnsureLoadedAsync()
		{
			if(_data != null)
				return;

			// HearthDb maps card ids -> dbf ids; right after HDT starts it may not be
			// populated yet, and parsing before then resolves ZERO cards (which looked like
			// "no data until you hit Refresh"). Defer and let the caller retry.
			if(HearthDb.Cards.All.Count == 0)
			{
				Log("HearthDb not ready yet; deferring load");
				return;
			}

			// Prefer the persisted cache (so a restart doesn't re-download); fall back to a
			// fresh download only if the cache is missing, stale, or unparseable.
			var json = ReadFreshCache();
			var raw = json != null ? Parse(json) : new Dictionary<int, ArenaCardScore>();
			var source = "cache";

			if(raw.Count == 0)
			{
				if(json != null)
					Log("cached arena data unusable; downloading fresh");
				json = await DownloadAsync().ConfigureAwait(false);
				source = "network";
				if(json == null)
				{
					Log("arena data unavailable (cache miss + download blocked)");
					return;
				}
				raw = Parse(json);
				if(raw.Count == 0)
				{
					Log("downloaded arena data parsed to 0 cards; skipping");
					return;
				}
				TryWriteCache(json);
			}

			var globalMean = raw.Values
				.Where(s => s.IncludedWinrate.HasValue)
				.Select(s => s.IncludedWinrate!.Value)
				.DefaultIfEmpty(50.0)
				.Average();

			var classScores = ScoreClasses(json!, globalMean);
			var heroClass = BuildHeroClassMap();

			// Publish all maps at once through the single volatile reference.
			_data = new Data(raw, ScoreCards(raw, globalMean), classScores, heroClass);
			Log($"loaded {raw.Count} cards, {classScores.Count} class tiers, {heroClass.Count} hero skins (source: {source})");
		}

		/// <summary>Hero-skin dbfId -> class (HERO_01, HERO_01a, ...); built once HearthDb is ready.</summary>
		private static Dictionary<int, CardClass> BuildHeroClassMap()
		{
			var map = new Dictionary<int, CardClass>();
			foreach(var kv in HearthDb.Cards.All)
			{
				if(kv.Key.StartsWith("HERO_", StringComparison.Ordinal) && kv.Value.DbfId != 0)
					map[kv.Value.DbfId] = kv.Value.Class;
			}
			return map;
		}

		// ---- scoring -------------------------------------------------------------

		/// <summary>
		/// Shrinks a card's rate toward <paramref name="globalMean"/> by sample size
		/// (empirical Bayes): (n·wr + k·μ) / (n + k).
		/// </summary>
		private static double Shrink(double winrate, int games, double globalMean)
			=> (games * winrate + ShrinkGames * globalMean) / (games + ShrinkGames);

		private static Dictionary<int, double> ScoreCards(
			IReadOnlyDictionary<int, ArenaCardScore> raw, double globalMean)
		{
			var shrunk = new Dictionary<int, double>(raw.Count);
			foreach(var kv in raw)
			{
				var s = kv.Value;
				shrunk[kv.Key] = Shrink(s.IncludedWinrate ?? globalMean, s.Games ?? 0, globalMean);
			}
			return ToScores(shrunk);
		}

		private Dictionary<CardClass, double> ScoreClasses(string json, double globalMean)
		{
			var classWinrate = ParseClassWinrates(json, globalMean);
			return classWinrate.Count == 0
				? new Dictionary<CardClass, double>()
				: ToScores(classWinrate);
		}

		/// <summary>
		/// Maps values to 0-100 via a logistic anchored at the robust centre (median)
		/// with a robust spread (MAD), so the median input is 50 and outliers saturate
		/// without stretching the scale.
		/// </summary>
		private static Dictionary<TKey, double> ToScores<TKey>(IReadOnlyDictionary<TKey, double> values)
		{
			var center = Median(values.Values);
			var sigma = RobustSigma(values.Values, center);
			var result = new Dictionary<TKey, double>(values.Count);
			foreach(var kv in values)
				result[kv.Key] = Logistic((kv.Value - center) / sigma);
			return result;
		}

		private static double Logistic(double z)
			=> 100.0 / (1.0 + Math.Exp(-LogisticSlope * z));

		private static double Median(IEnumerable<double> values)
		{
			var sorted = values.OrderBy(v => v).ToList();
			if(sorted.Count == 0)
				return 0.0;
			var mid = sorted.Count / 2;
			return sorted.Count % 2 == 1
				? sorted[mid]
				: (sorted[mid - 1] + sorted[mid]) / 2.0;
		}

		/// <summary>Median absolute deviation, scaled to a normal-consistent SD.</summary>
		private static double RobustSigma(IEnumerable<double> values, double center)
		{
			var mad = Median(values.Select(v => Math.Abs(v - center)));
			var sigma = 1.4826 * mad;
			return sigma > 1e-9 ? sigma : 1.0; // degenerate spread -> everything near 50
		}

		// ---- parsing / io --------------------------------------------------------

		private string? ReadFreshCache()
		{
			try
			{
				if(!File.Exists(_cacheFile))
					return null;
				if(DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile) > CacheMaxAge)
					return null;
				return File.ReadAllText(_cacheFile);
			}
			catch(Exception ex)
			{
				Log($"cache read failed: {ex.Message}");
				return null;
			}
		}

		private void TryWriteCache(string json)
		{
			try { File.WriteAllText(_cacheFile, json); }
			catch(Exception ex) { Log($"cache write failed: {ex.Message}"); }
		}

		// Primary path is curl; the .NET stack is only a fail-soft fallback for the rare
		// box without curl (it will usually 403, but costs nothing and self-heals if
		// Cloudflare ever stops fingerprinting).
		private static async Task<string?> DownloadAsync()
			=> await DownloadWithCurlAsync().ConfigureAwait(false)
				?? await DownloadWithNetStackAsync().ConfigureAwait(false);

		private static async Task<string?> DownloadWithCurlAsync()
		{
			try
			{
				// -f: fail (nonzero exit) on HTTP >=400 so a Cloudflare block falls through
				// to the fallback; --compressed: gzip on the wire (~100 KB vs ~850 KB);
				// -sS: quiet but keep errors. Timeouts and a size cap keep a misbehaving
				// endpoint from hanging or flooding the user (the real payload is ~1 MB).
				// The args are interpolated (net472 has no ArgumentList), which is injection-
				// safe ONLY because Endpoint and BrowserUserAgent are const — keep them const.
				var psi = new ProcessStartInfo("curl",
					$"-fsSL --compressed --connect-timeout 10 --max-time 30 " +
					$"--max-filesize 16777216 -A \"{BrowserUserAgent}\" \"{Endpoint}\"")
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
					// curl emits UTF-8; decode it as such (the console codepage would corrupt
					// any non-ASCII byte, and this matches the WebClient fallback).
					StandardOutputEncoding = System.Text.Encoding.UTF8,
				};

				using(var proc = Process.Start(psi))
				{
					if(proc == null)
						return null;

					// Drain both pipes concurrently: a full stderr buffer would otherwise
					// deadlock against a blocking stdout read. WhenAll observes both tasks
					// even if one faults.
					var stdout = proc.StandardOutput.ReadToEndAsync();
					var stderr = proc.StandardError.ReadToEndAsync();
					await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

					// curl's --max-time bounds this, but don't trust it: kill and bail if the
					// process somehow outlives its own deadline rather than block forever.
					if(!proc.WaitForExit(35000))
					{
						try { proc.Kill(); } catch { /* already gone */ }
						Log("curl fetch timed out");
						return null;
					}

					if(proc.ExitCode != 0)
					{
						Log($"curl fetch failed (exit {proc.ExitCode})");
						return null;
					}
					return stdout.Result;
				}
			}
			catch(Exception ex)
			{
				Log($"curl fetch error: {ex.Message}");
				return null;
			}
		}

		private static async Task<string?> DownloadWithNetStackAsync()
		{
			try
			{
				ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
				using(var client = new WebClient())
				{
					client.Headers[HttpRequestHeader.UserAgent] = BrowserUserAgent;
					client.Headers[HttpRequestHeader.Accept] = "application/json";
					client.Encoding = System.Text.Encoding.UTF8;
					return await client.DownloadStringTaskAsync(Endpoint).ConfigureAwait(false);
				}
			}
			catch(Exception ex)
			{
				Log($"download failed: {ex.Message}");
				return null;
			}
		}

		/// <summary>Reads the drawn win-rate (fallback: included win-rate) as a percentage.</summary>
		private static double? ReadWinrate(JToken card)
			=> (double?)card["drawn_win_rate"] ?? (double?)card["win_rate"];

		private static Dictionary<int, ArenaCardScore> Parse(string json)
		{
			var result = new Dictionary<int, ArenaCardScore>();
			try
			{
				var bucket = (JObject.Parse(json)["data"] as JObject)?["ALL"] as JArray;
				if(bucket == null)
					return result;

				foreach(var card in bucket)
				{
					var cardId = (string?)card["card_id"];
					if(string.IsNullOrEmpty(cardId))
						continue;
					if(!HearthDb.Cards.All.TryGetValue(cardId, out var dbCard) || dbCard.DbfId == 0)
						continue;

					var games = (int?)card["num_games"] ?? 0;
					if(games < MinGames)
						continue;

					// One card id can map to several dbf ids only via variants; keep the
					// entry backed by the most games.
					if(result.TryGetValue(dbCard.DbfId, out var existing) && (existing.Games ?? 0) >= games)
						continue;

					result[dbCard.DbfId] = new ArenaCardScore(
						dbCard.DbfId, ReadWinrate(card), (double?)card["popularity"], games);
				}
			}
			catch(Exception ex)
			{
				Log($"parse failed: {ex.Message}");
			}
			return result;
		}

		/// <summary>
		/// Per class, the UNWEIGHTED mean of its cards' shrunk drawn win-rates (cards
		/// below <see cref="MinGames"/> excluded). Unweighted rather than games-weighted:
		/// games-weighting is popularity-weighting (popularity correlates ~0.5 with
		/// win-rate), which biases each class's estimate upward by its most-played cards.
		/// </summary>
		private static Dictionary<CardClass, double> ParseClassWinrates(string json, double globalMean)
		{
			var result = new Dictionary<CardClass, double>();
			try
			{
				var data = JObject.Parse(json)["data"] as JObject;
				if(data == null)
					return result;

				foreach(var prop in data.Properties())
				{
					if(prop.Name == "ALL" || !(prop.Value is JArray bucket))
						continue;
					if(!Enum.TryParse<CardClass>(prop.Name, out var cls))
						continue;

					var shrunk = new List<double>();
					foreach(var card in bucket)
					{
						var winrate = ReadWinrate(card);
						var games = (int?)card["num_games"] ?? 0;
						if(winrate == null || games < MinGames)
							continue;
						shrunk.Add(Shrink(winrate.Value, games, globalMean));
					}
					if(shrunk.Count > 0)
						result[cls] = shrunk.Average();
				}
			}
			catch(Exception ex)
			{
				Log($"class winrate parse failed: {ex.Message}");
			}
			return result;
		}

		private static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] {msg}");
	}
}
