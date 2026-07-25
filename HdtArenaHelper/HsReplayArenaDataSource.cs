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
	///      empirical Bayes with a <see cref="ScoreMath.ShrinkGames"/>-game prior — so a 12-game
	///      card no longer asserts an extreme rate (and can't anchor the scale).
	///   3. Normalization: a logistic curve anchored at the robust CENTRE (median of the
	///      shrunk rates) with a robust SPREAD (MAD). The median card maps to 50 so the
	///      score is comparable with the heuristic source in the blend, and the scale is
	///      immune to outliers and stable across daily refreshes. (This replaces a
	///      min-max mapping that pinned 0 to a single noise card.)
	///
	/// The per-class buckets serve two purposes:
	///   - a class tier list (unweighted mean of the class's shrunk card rates); at the
	///     hero-pick step the offered HERO_* skins are rated by their class's tier, so
	///     the overlay doubles as a class picker;
	///   - per-class card scores: once the drafted class is known, a card is rated by its
	///     rate in THAT class's bucket (shrunk toward the card's ALL rate, so thin class
	///     samples glide to the class-agnostic estimate), normalized within the bucket.
	///     Cards missing from the bucket fall back to the ALL score.
	///
	/// A realistic browser User-Agent is required or Cloudflare serves a challenge.
	/// Cached with a 1-day TTL.
	/// </summary>
	public class HsReplayArenaDataSource : IArenaDataSource, IClassWinRateSource,
		IClassTribeAvailabilitySource
	{
		private const string Endpoint =
			"https://hsreplay.net/api/v1/arena/card_stats/free/?format=json";

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
			public Dictionary<int, SourceScore> CardScore { get; }   // dbfId -> score+games (ALL bucket)
			public Dictionary<CardClass, double> ClassScore { get; } // class -> 0..100 (tier list)
			public Dictionary<CardClass, double> ClassWinRate { get; } // class -> estimated win-rate %
			public Dictionary<CardClass, Dictionary<Race, double>> ClassTribeShare { get; } // class -> race -> %
			public Dictionary<CardClass, Dictionary<int, SourceScore>> ClassCardScore { get; }
			public Dictionary<int, CardClass> HeroClass { get; }     // hero-skin dbfId -> class

			public Data(
				Dictionary<int, ArenaCardScore> raw,
				Dictionary<int, SourceScore> cardScore,
				Dictionary<CardClass, double> classScore,
				Dictionary<CardClass, double> classWinRate,
				Dictionary<CardClass, Dictionary<Race, double>> classTribeShare,
				Dictionary<CardClass, Dictionary<int, SourceScore>> classCardScore,
				Dictionary<int, CardClass> heroClass)
			{
				Raw = raw;
				CardScore = cardScore;
				ClassScore = classScore;
				ClassWinRate = classWinRate;
				ClassTribeShare = classTribeShare;
				ClassCardScore = classCardScore;
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

		/// <summary>Real arena win-rates: every score carries the games behind it.</summary>
		public bool HasSamples => true;

		/// <summary>Raw per-card stats (drawn win-rate, popularity, games), if loaded.</summary>
		public ArenaCardScore? GetRaw(int dbfId)
		{
			var data = _data;
			return data != null && data.Raw.TryGetValue(dbfId, out var s) ? s : null;
		}

		/// <summary>Per-class 0-100 tier scores (the hero-pick tier list), if loaded.</summary>
		public IReadOnlyDictionary<CardClass, double>? ClassScores => _data?.ClassScore;

		/// <summary>Per-class estimated arena win-rate in percentage points, if loaded.</summary>
		public IReadOnlyDictionary<CardClass, double>? ClassWinRates => _data?.ClassWinRate;

		/// <inheritdoc/>
		public double? TribeShare(CardClass cls, Race race)
		{
			var data = _data;
			if(data == null || !data.ClassTribeShare.TryGetValue(cls, out var byRace))
				return null;
			return byRace.TryGetValue(race, out var share) ? share : (double?)null;
		}

		public SourceScore? GetNormalizedScore(int dbfId, CardClass draftClass = CardClass.INVALID)
		{
			var data = _data; // read the published bundle once
			if(data == null)
				return null;

			// Hero pick: rank the offered classes instead of a single card. A tier is
			// backed by a whole class bucket, not one card's sample: no games discount.
			if(data.HeroClass.TryGetValue(dbfId, out var heroClass))
				return data.ClassScore.TryGetValue(heroClass, out var cs)
					? new SourceScore(cs)
					: (SourceScore?)null;

			// Known drafted class: prefer the card's rate in that class's own bucket.
			if(draftClass != CardClass.INVALID
				&& data.ClassCardScore.TryGetValue(draftClass, out var perClass)
				&& perClass.TryGetValue(dbfId, out var classScore))
				return classScore;

			return data.CardScore.TryGetValue(dbfId, out var score) ? score : (SourceScore?)null;
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
			var json = ReadCache();
			var raw = json != null ? Parse(json) : new Dictionary<int, ArenaCardScore>();
			var source = "cache";

			if(raw.Count == 0)
			{
				if(json != null)
					Log("cached arena data unusable; downloading fresh");
				var downloaded = await DownloadAsync().ConfigureAwait(false);
				var downloadedRaw = downloaded != null ? Parse(downloaded) : new Dictionary<int, ArenaCardScore>();
				if(downloadedRaw.Count > 0)
				{
					json = downloaded;
					raw = downloadedRaw;
					source = "network";
					TryWriteCache(downloaded!);
				}
				else
				{
					// Download unavailable/empty: fall back to a stale (>TTL) cache rather than
					// showing nothing — day-old win-rates beat none when the network is down.
					var stale = ReadCache(requireFresh: false);
					raw = stale != null ? Parse(stale) : raw;
					if(raw.Count == 0)
					{
						Log("arena data unavailable (no cache + download blocked)");
						return;
					}
					json = stale;
					source = "stale cache";
					Log("using stale cache (download unavailable)");
				}
			}

			var globalMean = raw.Values
				.Where(s => s.DrawnWinrate.HasValue)
				.Select(s => s.DrawnWinrate!.Value)
				.DefaultIfEmpty(50.0)
				.Average();

			// dbf -> shrunk ALL rate: the class-agnostic score and the shrink fallback
			// for the thinner per-class rates below.
			var allShrunk = ShrinkAll(raw, globalMean);
			var classShrunk = ParseClassShrunk(json!, raw, allShrunk, globalMean);

			// ONE scale for every card score — anchored on the ALL pool. A pick can mix
			// class-bucket scores with ALL fallbacks, so normalizing each class bucket to
			// its own median would compare apples to oranges within the same pick.
			var center = ScoreMath.Median(allShrunk.Values);
			var sigma = ScoreMath.RobustSigma(allShrunk.Values, center);

			var classCardScore = new Dictionary<CardClass, Dictionary<int, SourceScore>>(classShrunk.Count);
			var classTier = new Dictionary<CardClass, double>(classShrunk.Count);
			foreach(var kv in classShrunk)
			{
				var rates = kv.Value.ToDictionary(e => e.Key, e => e.Value.Rate);
				classCardScore[kv.Key] = ScoreMath.ToScores(rates, center, sigma).ToDictionary(
					e => e.Key, e => new SourceScore(e.Value, kv.Value[e.Key].Games));
				// Unweighted mean rather than games-weighted: games-weighting is
				// popularity-weighting (popularity correlates ~0.5 with win-rate), which
				// biases each class's estimate upward by its most-played cards.
				classTier[kv.Key] = rates.Values.Average();
			}
			var classScores = classTier.Count == 0
				? new Dictionary<CardClass, double>()
				: ScoreMath.ToScores(classTier);
			var classWinRates = ParseClassWinRates(json!);
			var classTribeShares = ParseClassTribeShares(json!);
			var heroClass = HeroSkins.BuildClassMap();

			// The games behind each ALL score travel with it, so the blend can weight
			// this card's precision against the other sources'.
			var cardScore = ScoreMath.ToScores(allShrunk, center, sigma).ToDictionary(
				e => e.Key, e => new SourceScore(e.Value, raw[e.Key].Games));

			// Publish all maps at once through the single volatile reference.
			_data = new Data(raw, cardScore, classScores, classWinRates, classTribeShares,
				classCardScore, heroClass);
			Log($"loaded {raw.Count} cards, {classScores.Count} class tiers, {heroClass.Count} hero skins (source: {source})");
		}

		// ---- scoring -------------------------------------------------------------

		/// <summary>dbf -> ALL-bucket rate shrunk toward the global mean.</summary>
		private static Dictionary<int, double> ShrinkAll(
			IReadOnlyDictionary<int, ArenaCardScore> raw, double globalMean)
		{
			var shrunk = new Dictionary<int, double>(raw.Count);
			foreach(var kv in raw)
			{
				var s = kv.Value;
				shrunk[kv.Key] = ScoreMath.Shrink(s.DrawnWinrate ?? globalMean, s.Games ?? 0, globalMean);
			}
			return shrunk;
		}

		// ---- parsing / io --------------------------------------------------------

		private string? ReadCache(bool requireFresh = true)
		{
			try
			{
				if(!File.Exists(_cacheFile))
					return null;
				if(requireFresh && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile) > CacheMaxAge)
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
			try
			{
				// Atomic swap so a concurrent reader (a superseded warm-up loop still
				// finishing against an old source instance) sees the old or the new file,
				// never a torn write.
				var tmp = _cacheFile + ".tmp";
				File.WriteAllText(tmp, json);
				if(File.Exists(_cacheFile))
					File.Replace(tmp, _cacheFile, null);
				else
					File.Move(tmp, _cacheFile);
			}
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
					if(games < ScoreMath.MinGames)
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
		/// Per class, each card's rate in that class's bucket shrunk toward a
		/// leave-that-class-out prior (the card's ALL rate minus this bucket's games —
		/// shrinking toward a prior that CONTAINS the observation would double-count it;
		/// fallback: the shrunk ALL rate, then the global mean). Thin class samples glide
		/// to the class-agnostic estimate instead of asserting a noisy class-specific one.
		/// Backtested cross-source (target: the other source's class rate): the class
		/// estimator beats plain ALL — pooled Spearman 0.73 vs 0.53, MAE 3.4 vs 3.9.
		/// Cards below <see cref="ScoreMath.MinGames"/> excluded; duplicates keep most games.
		/// </summary>
		private static Dictionary<CardClass, Dictionary<int, (double Rate, int Games)>> ParseClassShrunk(
			string json, IReadOnlyDictionary<int, ArenaCardScore> raw,
			IReadOnlyDictionary<int, double> allShrunk, double globalMean)
		{
			var result = new Dictionary<CardClass, Dictionary<int, (double Rate, int Games)>>();
			try
			{
				var data = JObject.Parse(json)["data"] as JObject;
				if(data == null)
					return result;

				foreach(var prop in data.Properties())
				{
					if(prop.Name == "ALL" || !(prop.Value is JArray bucket))
						continue;
					if(!Enum.TryParse<CardClass>(prop.Name, ignoreCase: true, out var cls))
						continue;

					var shrunk = new Dictionary<int, (double Rate, int Games)>();
					foreach(var card in bucket)
					{
						var cardId = (string?)card["card_id"];
						if(string.IsNullOrEmpty(cardId))
							continue;
						if(!HearthDb.Cards.All.TryGetValue(cardId, out var dbCard) || dbCard.DbfId == 0)
							continue;

						var winrate = ReadWinrate(card);
						var n = (int?)card["num_games"] ?? 0;
						if(winrate == null || n < ScoreMath.MinGames)
							continue;
						if(shrunk.TryGetValue(dbCard.DbfId, out var seen) && seen.Games >= n)
							continue;

						shrunk[dbCard.DbfId] = (ScoreMath.Shrink(winrate.Value, n,
							LeaveClassOutTarget(dbCard.DbfId, winrate.Value, n, raw, allShrunk, globalMean)), n);
					}
					if(shrunk.Count > 0)
						result[cls] = shrunk;
				}
			}
			catch(Exception ex)
			{
				Log($"class bucket parse failed: {ex.Message}");
			}
			return result;
		}

		/// <summary>
		/// Per-class tribe availability: for each class, the share of its deck slots (popularity,
		/// which is exactly "% of that class's decks that ran the card") held by each tribe.
		///
		/// `popularity` is the right weight and `num_games` is not: the question is how much of a
		/// DECK is made of this tribe, not how many games those cards were involved in.
		///
		/// Amalgams (<see cref="Race.ALL"/>) are deliberately NOT counted for every tribe here, so
		/// availability is consistent with what the synergy engine accepts as a genuine member —
		/// counting them would report Murlocs as available to a class whose only "Murloc" is an
		/// amalgam, which is the same double-count that once disarmed the dead-card penalty.
		/// </summary>
		private static Dictionary<CardClass, Dictionary<Race, double>> ParseClassTribeShares(string json)
		{
			var result = new Dictionary<CardClass, Dictionary<Race, double>>();
			try
			{
				var data = JObject.Parse(json)["data"] as JObject;
				if(data == null)
					return result;

				foreach(var prop in data.Properties())
				{
					if(prop.Name == "ALL" || !(prop.Value is JArray bucket))
						continue;
					if(!Enum.TryParse<CardClass>(prop.Name, ignoreCase: true, out var cls))
						continue;

					var byRace = new Dictionary<Race, double>();
					double total = 0;
					foreach(var card in bucket)
					{
						var cardId = (string?)card["card_id"];
						if(string.IsNullOrEmpty(cardId))
							continue;
						if(!HearthDb.Cards.All.TryGetValue(cardId, out var dbCard))
							continue;
						var popularity = (double?)card["popularity"] ?? 0;
						if(popularity <= 0)
							continue;

						total += popularity;
						// Both races of a dual-tribe card count: it IS a member of each.
						foreach(var race in new[] { dbCard.Race, dbCard.SecondaryRace })
						{
							if(race == Race.INVALID || race == Race.ALL)
								continue;
							byRace.TryGetValue(race, out var seen);
							byRace[race] = seen + popularity;
						}
					}
					if(total <= 0 || byRace.Count == 0)
						continue;
					result[cls] = byRace.ToDictionary(kv => kv.Key, kv => 100.0 * kv.Value / total);
				}
			}
			catch(Exception ex)
			{
				Log($"class tribe share parse failed: {ex.Message}");
				return new Dictionary<CardClass, Dictionary<Race, double>>();
			}
			return result;
		}

		/// <summary>
		/// Per-class estimated arena win-rate, in percentage points. Uses INCLUSION
		/// <c>win_rate</c> weighted by <c>num_games</c>, not the drawn rate the card scores use:
		/// the target here is a whole deck's win-rate, and summing "games where this card was in
		/// the deck" x "win-rate of those games" over a class's cards recovers exactly that
		/// (each game counted once per card it contained). Unfiltered by
		/// <see cref="ScoreMath.MinGames"/> on purpose — a thin card contributes proportionally
		/// few games, so it cannot pull the pooled estimate.
		/// </summary>
		private static Dictionary<CardClass, double> ParseClassWinRates(string json)
		{
			var tallies = new Dictionary<CardClass, (double Wins, double Games)>();
			try
			{
				var data = JObject.Parse(json)["data"] as JObject;
				if(data == null)
					return new Dictionary<CardClass, double>();

				foreach(var prop in data.Properties())
				{
					// Skip ALL: it is the same games again, and including it would drag every
					// class's offset toward the pool it is supposed to be measured against.
					if(prop.Name == "ALL" || !(prop.Value is JArray bucket))
						continue;
					if(!Enum.TryParse<CardClass>(prop.Name, ignoreCase: true, out var cls))
						continue;

					double wins = 0, games = 0;
					foreach(var card in bucket)
					{
						var rate = (double?)card["win_rate"];
						var n = (int?)card["num_games"] ?? 0;
						if(rate == null || n <= 0)
							continue;
						wins += n * rate.Value / 100.0;
						games += n;
					}
					if(games > 0)
						tallies[cls] = (wins, games);
				}
			}
			catch(Exception ex)
			{
				Log($"class win-rate parse failed: {ex.Message}");
				return new Dictionary<CardClass, double>();
			}
			return ScoreMath.RecentreClassWinRates(tallies);
		}

		/// <summary>
		/// The card's ALL rate with this class's games subtracted out. Guarded: if the
		/// remainder is too thin (class cards ARE most of their ALL sample) or the buckets
		/// don't reconcile, fall back to the shrunk ALL rate.
		/// </summary>
		private static double LeaveClassOutTarget(int dbfId, double classRate, int classGames,
			IReadOnlyDictionary<int, ArenaCardScore> raw, IReadOnlyDictionary<int, double> allShrunk,
			double globalMean)
		{
			if(raw.TryGetValue(dbfId, out var all) && all.DrawnWinrate.HasValue)
			{
				var allGames = all.Games ?? 0;
				var looGames = allGames - classGames;
				if(looGames >= ScoreMath.MinGames)
				{
					var loo = (allGames * all.DrawnWinrate.Value - classGames * classRate) / looGames;
					if(loo >= 0 && loo <= 100)
						return loo;
				}
			}
			return allShrunk.TryGetValue(dbfId, out var allRate) ? allRate : globalMean;
		}

		private static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] {msg}");
	}
}
