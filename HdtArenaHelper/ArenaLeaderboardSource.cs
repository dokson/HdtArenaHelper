using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HdtArenaHelper
{
	/// <summary>Which of Blizzard's two arena leaderboards a lookup targets.</summary>
	public enum ArenaLeaderboardKind
	{
		/// <summary>Normal Arena. <c>rating</c> is the season's average wins per run (e.g. 8.37).</summary>
		Arena,
		/// <summary>Underground Arena. <c>rating</c> is an integer-valued rank score in the thousands
		/// (e.g. 8102), a different metric from Normal Arena's average wins — NOT comparable across
		/// kinds. Reported AS THE FEED STATES IT: the payload carries no scale or format for the column —
		/// its only description is the label "Rating" — so re-scaling it, however plausible a factor of
		/// 100 looks against values like 8102, would state an invented number rather than convert one.</summary>
		UndergroundArena,
	}

	/// <summary>One player's standing on a leaderboard.</summary>
	public readonly struct ArenaLeaderboardEntry
	{
		public int Rank { get; }
		/// <summary>Average wins per run (Arena) or the scaled rank score (Underground Arena) — see
		/// <see cref="ArenaLeaderboardKind"/>. Never re-scaled here: the caller knows which kind it asked for.</summary>
		public double Rating { get; }
		public ArenaLeaderboardEntry(int rank, double rating) { Rank = rank; Rating = rating; }
	}

	/// <summary>
	/// Looks up a player's rank on Blizzard's own public, official arena leaderboards
	/// (<c>hearthstone.blizzard.com/.../api/community/leaderboardsData</c>) — first-party data, not a
	/// third party's. Built for one purpose: showing the CURRENT ARENA OPPONENT's rank, if they happen
	/// to be on the leaderboard at all (the leaderboard only covers roughly the top 10,000 players per
	/// region, so most opponents will not resolve — that is expected, not a bug).
	///
	/// The endpoint has no name-search parameter and no page-size override: the only way to find one
	/// player is to already know (or discover) which page their rank falls on. Two designs were
	/// considered and rejected before this one:
	///
	///   - A full re-scan on every lookup (page 1 through ~391 per region) would cost hundreds of
	///     requests for the common case — an opponent who is NOT on the leaderboard — every single
	///     match. That is the opposite of "rare, single-shot" traffic.
	///   - A shared always-on scraping SERVER (the design <c>HDT_BGrank</c> uses for Battlegrounds:
	///     a paid VM re-scraping continuously) would mean hosting infrastructure this project has
	///     never had, and hitting Blizzard from one address on a tight schedule regardless of how many
	///     players are actually looking something up.
	///
	/// Instead this mirrors the desktop app <c>Arena-Tracker</c> (github.com/supertriodo/Arena-Tracker,
	/// <c>arenahandler.cpp</c>): each INSTALLED CLIENT slowly crawls the leaderboard for itself, one
	/// page at a time, in the background, and PERSISTS its progress to disk so a restart resumes
	/// instead of re-crawling. Only the region the player is CURRENTLY on is crawled — regions are
	/// separate shards, so the other two hold nobody this client can ever be matched against, which
	/// would make their pages pure traffic. A player the crawl has not reached is reported as "not
	/// found" rather than triggering an exhaustive scan, and there is NO per-player refresh: a rank is
	/// only ever as fresh as the last pass over its page. The crawl does not stop at the end of the
	/// board — it wraps and keeps going at a slower maintenance pace, which is what keeps ranks current
	/// and what lets a season rollover be noticed at all. Traffic scales with how long the client has
	/// been running, not with how many arena matches are played.
	///
	/// THREADING. <see cref="FindAll"/>, <see cref="EnsureCrawling"/> and <see cref="Dispose"/> are
	/// today all called from HDT's UI thread only (via OnUpdate, OnUnload and the plugin menu), and the
	/// crawl runs on the thread pool. That is what makes several otherwise-possible races unreachable, so
	/// it is an invariant rather than a coincidence: the first caller to look a player up from a
	/// background thread — an overlay row computing off-thread, say — has to re-examine the interleavings
	/// here, not assume they were designed for. What IS unconditionally safe: no I/O ever runs while
	/// <c>_lock</c> is held, and the lookup is a dictionary read.
	/// </summary>
	public class ArenaLeaderboardSource : IDisposable
	{
		private const string Endpoint = "https://hearthstone.blizzard.com/en-us/api/community/leaderboardsData";

		// The FIRST pass is paced to finish in minutes, so the feature works soon after install: until a
		// pass completes almost every opponent reports "not found", which is indistinguishable from a
		// broken plugin. Still one request at a time, never a burst.
		private static readonly TimeSpan FirstCrawlPageDelay = TimeSpan.FromMilliseconds(750);

		// After that the crawl does NOT stop: it wraps to page 1 and keeps going, so a rank is at worst
		// one pass old rather than as old as the install. This is the target duration of a maintenance
		// pass, and the per-page delay is DERIVED from it (see PageDelayFor) rather than written as a
		// number of seconds — the board's page count moves with the season, and a fixed delay would
		// silently change the refresh period every time it did.
		//
		// 24 hours, and shortening it needs an argument that survives the arithmetic. At 2 h, ~100 installs
		// in one region would make ~60x the hourly request volume of the single always-on scraping server
		// that Data sources & ethics rejects as impolite — from 100 addresses instead of one. A shorter
		// interval also buys nothing anyone can observe: the Arena column is average wins per run over a
		// season, which moves by ~0.01 per additional run, so a 24-h-old rank is not a worse number than a
		// 2-h-old one for a display-only label. Numbers in REPORT.md 17.
		private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromHours(24);

		// How long after the last arena match the crawl keeps running. Without this the crawl is tied to
		// HDT's UPTIME rather than to demand: after one arena match it kept pulling pages for the rest of
		// the session while the player was drafting, in Battlegrounds, or away from the machine — which
		// is the only traffic here that buys nothing at all, and it made the claim that "traffic scales
		// with how much the client has run" aspirational rather than true.
		private static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(30);

		// Floor under that derived delay: a board large enough to force a fast cadence must slow the
		// refresh down instead of hammering the endpoint to keep the 2-hour promise.
		private static readonly TimeSpan MinRefreshPageDelay = TimeSpan.FromSeconds(2);

		// Must exceed the pacing delay, or the pooled connection is dropped between pages. See the
		// constructor for why that costs real bytes rather than just a handshake.
		private static readonly TimeSpan MaxIdleTime = TimeSpan.FromMinutes(30);

		// Attempts per page before the pass is abandoned, counting the first. Bounded and small on purpose:
		// without any retry a single dropped connection ended the pass, which at a 24-hour pace means a
		// full board takes several play sessions to cover; with an unbounded one, an outage would turn the
		// crawl into a hammer. Only a TRANSIENT outcome is retried at all.
		private const int MaxFetchAttempts = 3;

		// Waited before a retry, multiplied by the attempt number. Long enough that a brief outage is over
		// and short enough to still finish the pass.
		private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(10);

		// A ceiling on the page count the PAYLOAD claims. totalPages drives the loop, so an absurd or
		// poisoned value would otherwise crawl for years. Generous against the real boards (hundreds of
		// pages) and still bounded.
		private const int MaxPageCeiling = 5000;

		// Blizzard's arena leaderboards cover only US/EU/AP. CHINA is excluded because the endpoint does
		// not reject it: `region=CN`, a garbage region and an empty one all silently return EU's rows,
		// echoing "region":"EU" back (verified against the live endpoint), so querying it would show a
		// real rank under the WRONG player's name. China has its own separate API host, not this one.
		// UNKNOWN is the same "say nothing" case for a different reason.
		private static readonly string[] SupportedRegions = { "US", "EU", "AP" };

		// Progress is written every this many pages rather than after every single one. The map is
		// rewritten whole each time, so per-page writes meant hundreds of rewrites of a file that grows
		// to most of a megabyte. Losing a few pages of CURSOR to a crash costs only re-reading them,
		// which the crawl does harmlessly, so durability here is worth far less than the I/O it cost.
		private const int PersistEveryPages = 100;

		/// <summary>A cached standing plus the crawl pass that last saw it. The pass stamp is what lets a
		/// player who has DROPPED OFF the board be removed: rows are overwritten as passes go by, so
		/// without it a stale rank would survive forever for someone no longer on the leaderboard.</summary>
		private readonly struct CachedEntry
		{
			/// <summary>The first standing seen for this display name in <see cref="Pass"/>.</summary>
			public ArenaLeaderboardEntry Entry { get; }
			public int Pass { get; }
			/// <summary>Further players publishing the SAME display name in the same pass, or null when
			/// the name is unique — which it is for ~98.5% of the board. Kept null rather than making
			/// every entry an array, so the common case allocates nothing.</summary>
			public ArenaLeaderboardEntry[]? Others { get; }

			public CachedEntry(ArenaLeaderboardEntry entry, int pass, ArenaLeaderboardEntry[]? others = null)
			{
				Entry = entry;
				Pass = pass;
				Others = others;
			}

			public bool IsShared => Others != null && Others.Length > 0;

			/// <summary>Every standing under this name, best rank first.</summary>
			public ArenaLeaderboardEntry[] All()
			{
				if(!IsShared)
					return new[] { Entry };
				var all = new ArenaLeaderboardEntry[Others!.Length + 1];
				all[0] = Entry;
				Array.Copy(Others, 0, all, 1, Others.Length);
				Array.Sort(all, (a, b) => a.Rank.CompareTo(b.Rank));
				return all;
			}

			public CachedEntry With(ArenaLeaderboardEntry additional)
			{
				var others = Others == null
					? new[] { additional }
					: Others.Concat(new[] { additional }).ToArray();
				return new CachedEntry(Entry, Pass, others);
			}
		}

		private sealed class RegionState
		{
			public int SeasonId;
			public int NextPage = 1; // 1-based, and it WRAPS: the crawl has no terminal state
			public int MaxPage;
			public int CompletedPasses; // 0 while the very first pass is still running
			public int PagesSincePersist;
			public DateTime LastCheckedUtc;
			public Dictionary<string, CachedEntry> Players { get; } =
				new Dictionary<string, CachedEntry>(StringComparer.Ordinal);

			/// <summary>The pass currently being crawled, 1-based. Persisted with each row, so a resume
			/// mid-board keeps stamping the same pass instead of pruning what the previous run stored.</summary>
			public int CurrentPass => CompletedPasses + 1;
		}

		/// <summary>Everything needed to write a cache file, copied OUT of the locked state so the
		/// serialization and the disk write happen with no lock held. Snapshotting is a cheap struct copy
		/// per player; serializing and writing are not, and doing them under this lock would make the
		/// UI-thread lookup wait on a disk write.</summary>
		private sealed class PersistSnapshot
		{
			public int SeasonId;
			public int NextPage;
			public int MaxPage;
			public int CompletedPasses;
			public long LastCheckedUtcTicks;
			public KeyValuePair<string, CachedEntry>[] Players = Array.Empty<KeyValuePair<string, CachedEntry>>();
		}

		private readonly Dictionary<(ArenaLeaderboardKind, string), RegionState> _state =
			new Dictionary<(ArenaLeaderboardKind, string), RegionState>();
		private readonly HashSet<(ArenaLeaderboardKind, string)> _crawlStarted =
			new HashSet<(ArenaLeaderboardKind, string)>();
		private readonly object _lock = new object();
		private readonly string _cacheDir;
		private readonly CancellationTokenSource _cts = new CancellationTokenSource();
		private bool _disposed;
		private DateTime _lastActivityUtc;
		/// <summary>The one (kind, region) allowed to crawl. The pacing budget is a politeness budget and
		/// so belongs to the CLIENT, not to each board: without this, a player who plays both Arena and
		/// The Underground ran two perpetual crawls and doubled the traffic, and the stated refresh period
		/// was per-board rather than a promise the client actually kept. A region switch mid-session had
		/// the same shape — it started a second crawl and never stopped the first.</summary>
		private (ArenaLeaderboardKind Kind, string Region)? _activePair;

		/// <summary>Why a page fetch did not produce a page, which decides whether retrying is legitimate
		/// or rude.</summary>
		internal enum FetchOutcome
		{
			Ok,
			/// <summary>A timeout, a dropped connection, a DNS hiccup or a 5xx — the server did not refuse
			/// us, the network or the server just failed. Worth one or two more attempts.</summary>
			Transient,
			/// <summary>A refusal or something we cannot parse: a 4xx, an unreadable payload, an unexpected
			/// exception. NEVER retried — a 4xx is Blizzard declining, and retrying into a refusal is the
			/// behaviour Data sources &amp; ethics forbids. Everything unknown lands here, so the failure
			/// mode is "stop", not "hammer".</summary>
			Permanent,
		}

		internal readonly struct PageResult
		{
			public JObject? Page { get; }
			public FetchOutcome Outcome { get; }
			public PageResult(JObject? page, FetchOutcome outcome) { Page = page; Outcome = outcome; }

			internal static PageResult Ok(JObject page) => new PageResult(page, FetchOutcome.Ok);
			internal static PageResult Transient() => new PageResult(null, FetchOutcome.Transient);
			internal static PageResult Permanent() => new PageResult(null, FetchOutcome.Permanent);
		}

		/// <summary>Fetches one already-parsed page. Injectable so the crawl loop can be driven without a
		/// network call — the pacing, the activity window, the hand-off between boards and the retry policy
		/// are all behaviour that is invisible from outside and therefore has to be pinned by a test.</summary>
		internal delegate Task<PageResult> PageFetcher(
			ArenaLeaderboardKind kind, string region, int page, CancellationToken token);

		private readonly Func<DateTime> _utcNow;
		private readonly PageFetcher _fetch;
		private readonly Func<TimeSpan, CancellationToken, Task> _delay;

		public ArenaLeaderboardSource(string cacheDir)
			: this(cacheDir, () => DateTime.UtcNow, DownloadPageAsync, (d, t) => Task.Delay(d, t))
		{
		}

		/// <summary>Test seam: a controlled clock, page source and delay, so a whole pass can be driven in
		/// milliseconds and the requested delays can be asserted rather than waited out.</summary>
		internal ArenaLeaderboardSource(
			string cacheDir, Func<DateTime> utcNow, PageFetcher fetch, Func<TimeSpan, CancellationToken, Task> delay)
		{
			_utcNow = utcNow;
			_fetch = fetch;
			_delay = delay;
			_cacheDir = cacheDir;
			Directory.CreateDirectory(_cacheDir);
			// Not per request. This is process-wide state shared with HDT and every other plugin, and the
			// |= is a read-modify-write on it; we accept that because the value is a bit we only ever add
			// and the construction path is UI-thread-only. A new instance is built on every re-enable, so
			// this runs more than once per process — it just has to stay idempotent.
			ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

			// Keep the pooled TLS connection alive ACROSS the pacing delay. The default idle timeout is
			// 100 s, and the derived delay is already 93.5 s on the smallest board — a 7% margin — so
			// without this a slower pace silently turns every page into a fresh TCP+TLS handshake, which
			// adds several KB of certificate exchange to a 16.5 KB page. Raising the pace is exactly the
			// change most likely to be made here, so this has to be raised with it.
			if(ServicePointManager.MaxServicePointIdleTime < (int)MaxIdleTime.TotalMilliseconds)
				ServicePointManager.MaxServicePointIdleTime = (int)MaxIdleTime.TotalMilliseconds;
		}

		/// <summary>
		/// EVERY standing published under this display name in the given region, best rank first — empty
		/// when the crawl has not reached them or they are not on the board at all. Never makes a network
		/// call: this is the instant path for the overlay.
		///
		/// Returns a LIST rather than one entry because the leaderboard's `accountid` carries no
		/// discriminator and a Blizzard display name is not unique, so a name can genuinely belong to
		/// several players. Measured, 14 of 1,910 names on a real board repeat, and their holders sit
		/// hundreds of ranks apart (REPORT.md 17). Picking one would state a real rank under the wrong
		/// player's name — the error the region whitelist exists to refuse — so the caller is handed all
		/// of them and must present them as alternatives rather than as a fact.
		/// </summary>
		public IReadOnlyList<ArenaLeaderboardEntry> FindAll(
			ArenaLeaderboardKind kind, string region, string battleTagName)
		{
			if(string.IsNullOrWhiteSpace(battleTagName) || !SupportedRegions.Contains(region))
				return Array.Empty<ArenaLeaderboardEntry>();
			lock(_lock)
			{
				if(!_state.TryGetValue((kind, region), out var state)
					|| !state.Players.TryGetValue(battleTagName, out var e))
					return Array.Empty<ArenaLeaderboardEntry>();
				return e.All();
			}
		}

		/// <summary>
		/// Where a given rating WOULD sit on this board — one plus the number of cached players rated above
		/// it — or null until a full pass has been read, since a partial board would understate the rank.
		///
		/// Costs no network at all: the board is already cached, so this is a count. It answers the question
		/// a player who is not listed actually has, and most are not listed: a placing needs a seasonal
		/// minimum of games, so a perfectly good rating can be absent (REPORT.md 17).
		///
		/// Only meaningful where the client's rating and the board's column are the SAME quantity, which is
		/// Underground. Normal Arena publishes average wins per run while the client exposes a rating, so
		/// nothing may be projected there — the caller decides, this method just counts.
		/// </summary>
		internal int? ProjectedRankFor(ArenaLeaderboardKind kind, string region, double rating)
		{
			lock(_lock)
			{
				if(!_state.TryGetValue((kind, region), out var state) || state.CompletedPasses == 0)
					return null;
				var above = 0;
				foreach(var cached in state.Players.Values)
				{
					foreach(var entry in cached.All())
					{
						if(entry.Rating > rating)
							above++;
					}
				}
				return above + 1;
			}
		}

		/// <summary>Has a FULL pass over this board completed? Until it has, an absent name may simply be on
		/// a page not yet read, and a caller must not report it as absent from the board.</summary>
		internal bool HasCompletedPass(ArenaLeaderboardKind kind, string region)
		{
			lock(_lock)
				return _state.TryGetValue((kind, region), out var s) && s.CompletedPasses > 0;
		}

		/// <summary>The page the crawl would fetch next for this region, or null if it has no state yet.
		/// 0 means "this season is fully crawled". Internal: the crawl's progress is invisible from
		/// outside, and it is exactly where a rollover bug hid until a test could read it.</summary>
		internal int? NextPageFor(ArenaLeaderboardKind kind, string region)
		{
			lock(_lock)
				return _state.TryGetValue((kind, region), out var s) ? (int?)s.NextPage : null;
		}

		/// <summary>
		/// Starts the background crawl for ONE (kind, region) pair, loading any persisted progress
		/// first. Safe to call repeatedly: while a crawl for that pair is running, further calls do
		/// nothing, and the pair is released when it stops so a later call can resume it — which is
		/// what recovers from a network failure without restarting HDT.
		///
		/// Only the region the player is CURRENTLY on is ever crawled. Hearthstone's regions are
		/// separate shards, so an arena opponent is always on the caller's own region: pages from the
		/// other two could never be matched against anybody, which makes them pure traffic.
		///
		/// Fire-and-forget: callers just want the crawl running, not its completion.
		/// </summary>
		public void EnsureCrawling(ArenaLeaderboardKind kind, string region)
		{
			if(!SupportedRegions.Contains(region))
			{
				Log($"region '{region}' is not one this leaderboard covers; not crawling");
				return;
			}
			CancellationToken token;
			lock(_lock)
			{
				if(_disposed)
				{
					Log($"{kind}/{region} not started: this source is disposed");
					return;
				}
				MarkActiveLocked(kind, region);
				if(!_crawlStarted.Add((kind, region)))
					return; // already running; saying so twice a second would be noise
				token = _cts.Token;
			}
			// A crawl that starts silently and only speaks when a pass completes is indistinguishable from
			// one that never started — which is exactly how a live diagnosis went wrong.
			Log($"{kind}/{region} crawl starting");
			// No token argument: Task.Run with an already-cancelled token skips the delegate entirely, so
			// the finally below would never run and the pair would stay in _crawlStarted for good. The
			// loop checks the token itself.
			Task.Run(async () =>
			{
				try
				{
					// Reading and parsing the cache is file I/O over a map that reaches most of a
					// megabyte, so it belongs here and not on the caller's thread — which is HDT's UI
					// thread, via OnUpdate. Cost: the very first lookup of a session can miss a rank
					// that IS on disk, because the load has not finished yet; the next match resolves it.
					LoadPersisted(kind, region);
					await CrawlRegionAsync(kind, region, token).ConfigureAwait(false);
				}
				catch(Exception ex)
				{
					// LOGGED, not dropped as an unobserved task exception: net472 swallows those, so an
					// unexpected throw in here would stop the crawl for the session with nothing in the
					// log to explain a feature that had silently gone dead. Same reason WarmData wraps.
					Log($"crawl stopped unexpectedly ({kind}/{region}): {ex}");
				}
				finally
				{
					lock(_lock)
						_crawlStarted.Remove((kind, region));
				}
			});
		}

		/// <summary>
		/// Crawls until cancelled. There is no terminal state on purpose: reaching the last page wraps
		/// back to page 1, so ranks stay at most one pass old and a season rollover is noticed by an
		/// ordinary pass. A crawl that stopped at completion could never do either — the cursor was
		/// persisted, so it stayed stopped across restarts too.
		/// </summary>
		/// <summary>
		/// Records that an arena match is happening and hands this pair the client's single crawl budget.
		/// Called with the lock held. Separate from <see cref="EnsureCrawling"/> so a test can drive the
		/// loop directly without also launching the fire-and-forget one.
		/// </summary>
		private void MarkActiveLocked(ArenaLeaderboardKind kind, string region)
		{
			// Arena activity, not HDT's uptime, is what the crawl is allowed to run on.
			_lastActivityUtc = _utcNow();
			// Any other pair still running sees it is no longer the active one and stops at its next step.
			_activePair = (kind, region);
		}

		/// <summary>Test seam for the above, since the production caller is fire-and-forget.</summary>
		internal void MarkActive(ArenaLeaderboardKind kind, string region)
		{
			lock(_lock)
				MarkActiveLocked(kind, region);
		}

		internal async Task CrawlRegionAsync(ArenaLeaderboardKind kind, string region, CancellationToken token)
		{
			while(!token.IsCancellationRequested)
			{
				int page;
				TimeSpan delay;
				lock(_lock)
				{
					if(_activePair != (kind, region))
					{
						Log($"{kind}/{region} crawl yielding: another board or region is the active one");
						return;
					}
					if(_utcNow() - _lastActivityUtc > ActivityWindow)
					{
						// No arena match for a while: stop rather than keep pulling pages nobody will
						// look anything up in. The pair is released, so the next match resumes from the
						// persisted cursor.
						Log($"{kind}/{region} crawl idle: no arena match within {ActivityWindow.TotalMinutes:0} min");
						return;
					}

					if(_state.TryGetValue((kind, region), out var s))
					{
						page = s.NextPage > 0 ? s.NextPage : 1;
						delay = PageDelayFor(s);
					}
					else
					{
						page = 1;
						delay = FirstCrawlPageDelay;
					}
				}

				var payload = await FetchWithRetryAsync(kind, region, page, token).ConfigureAwait(false);
				if(payload == null)
					return; // fail soft: the pair is released, so the next lookup resumes the crawl

				if(!ApplyPage(kind, region, page, payload))
					return;

				try
				{
					await _delay(delay, token).ConfigureAwait(false);
				}
				catch(OperationCanceledException)
				{
					return;
				}
			}
		}

		/// <summary>
		/// One page, retried only while the failure looks TRANSIENT and only up to
		/// <see cref="MaxFetchAttempts"/> times. Null means give up on the pass.
		///
		/// A permanent outcome returns immediately without a second request: a 4xx is Blizzard declining,
		/// and the rule this project already follows is to stop rather than work around a refusal.
		/// </summary>
		private async Task<JObject?> FetchWithRetryAsync(
			ArenaLeaderboardKind kind, string region, int page, CancellationToken token)
		{
			for(var attempt = 1; attempt <= MaxFetchAttempts; attempt++)
			{
				if(token.IsCancellationRequested)
					return null;

				var result = await _fetch(kind, region, page, token).ConfigureAwait(false);
				if(result.Outcome == FetchOutcome.Ok && result.Page != null)
					return result.Page;
				if(result.Outcome != FetchOutcome.Transient)
					return null;
				if(attempt == MaxFetchAttempts)
				{
					Log($"{kind}/{region} page {page} still failing after {MaxFetchAttempts} attempts; " +
						"ending this pass, the cursor is kept");
					return null;
				}

				try
				{
					await _delay(TimeSpan.FromTicks(RetryBackoff.Ticks * attempt), token).ConfigureAwait(false);
				}
				catch(OperationCanceledException)
				{
					return null;
				}
			}
			return null;
		}

		/// <summary>
		/// How long to wait before the next page. The first pass runs fast so the cache becomes useful
		/// quickly; afterwards the delay is derived so that ONE PASS takes
		/// <see cref="FullRefreshInterval"/>, which keeps the refresh period fixed as the board's page
		/// count changes instead of letting a hard-coded delay redefine it. Called with the lock held.
		/// </summary>
		private static TimeSpan PageDelayFor(RegionState state)
		{
			if(state.CompletedPasses == 0 || state.MaxPage <= 0)
				return FirstCrawlPageDelay;
			var perPage = TimeSpan.FromTicks(FullRefreshInterval.Ticks / state.MaxPage);
			return perPage < MinRefreshPageDelay ? MinRefreshPageDelay : perPage;
		}

		/// <summary>
		/// Stops every crawl, permanently for this instance. Called from the plugin's OnUnload and from
		/// the menu toggle: in-flight background work must not outlive the plugin, the lesson
		/// <see cref="SelfUpdater"/> documents for its own download, and a crawl that never terminates on
		/// its own makes it mandatory rather than tidy. A disposed source never crawls again, so both
		/// re-enable paths build a fresh one.
		///
		/// The <see cref="CancellationTokenSource"/> is deliberately NOT disposed: fire-and-forget crawl
		/// tasks still read its token as they wind down, and reading a disposed source's token throws.
		/// Nothing unmanaged leaks by leaving it — a CTS only materializes a wait handle if someone
		/// touches <c>WaitHandle</c>, and nothing here does.
		/// </summary>
		public void Dispose()
		{
			lock(_lock)
			{
				if(_disposed)
					return;
				_disposed = true;
			}
			// Cancel() runs every registration and aggregates their failures, so ToString() rather than
			// Message: the latter prints only "One or more errors occurred." and drops the actual cause.
			try { _cts.Cancel(); }
			catch(Exception ex) { Log($"crawl cancel failed: {ex}"); }
		}

		/// <summary>Merges one downloaded page into the cached map, and moves the crawl's cursor.
		/// Returns false only when the payload is unusable (malformed, or missing the rows/season it is
		/// keyed on), which stops the crawl; a season rollover returns TRUE, having discarded the stale
		/// map and pointed the cursor back at page 1. Internal so tests can drive it with synthetic
		/// JSON without a network call.</summary>
		internal bool ApplyPage(ArenaLeaderboardKind kind, string region, int page, string json)
		{
			JObject? obj;
			try { obj = PayloadGuard.ParseObject(json); }
			catch { obj = null; }
			return obj != null && ApplyPage(kind, region, page, obj);
		}

		/// <summary>The same, over an already-parsed payload — what the crawl uses, since it parses
		/// straight off the response stream rather than buffering the body first.</summary>
		private bool ApplyPage(ArenaLeaderboardKind kind, string region, int page, JObject obj)
		{
			var seasonId = ReadInt(obj["seasonId"]) ?? 0;
			var leaderboard = obj["leaderboard"] as JObject;
			var rows = leaderboard?["rows"] as JArray;
			var totalPages = ReadInt(leaderboard?["pagination"]?["totalPages"]) ?? 0;
			if(rows == null || seasonId <= 0)
				return false;

			PersistSnapshot? snapshot;
			lock(_lock)
			{
				if(!_state.TryGetValue((kind, region), out var state))
				{
					state = new RegionState();
					_state[(kind, region)] = state;
				}

				// A season rollover invalidates every rank already cached: ranks are only meaningful
				// within one season, and carrying last season's map forward would show a stale rank
				// as if it were current.
				var rollover = state.SeasonId != 0 && state.SeasonId != seasonId;
				if(rollover)
				{
					state.Players.Clear();
					state.MaxPage = 0;
					// A new season refills from scratch, so go back to the fast pace: the maintenance
					// pace exists to keep a COMPLETE map fresh, not to build an empty one.
					state.CompletedPasses = 0;
				}
				state.SeasonId = seasonId;
				if(totalPages > 0)
					state.MaxPage = Math.Min(totalPages, MaxPageCeiling);

				var pass = state.CurrentPass;
				foreach(var row in rows)
				{
					var tag = row["accountid"]?.Type == JTokenType.String ? (string?)row["accountid"] : null;
					var rank = ReadInt(row["rank"]);
					var rating = ReadNumber(row["rating"]);
					// A rank below 1 is not a rank — the board is 1-based — so it is a poisoned value
					// rather than a row. Dropped, never clamped: the same policy as PayloadGuard's.
					if(string.IsNullOrEmpty(tag) || rank == null || rank < 1 || rating == null)
						continue;

					// Seen ALREADY IN THIS PASS means two different players share this display name, so
					// both are kept and the caller decides how to present them. An entry stamped with an
					// EARLIER pass is just the same player again on a later pass, so it overwrites —
					// which is also what clears a name that has stopped being shared.
					var entry = new ArenaLeaderboardEntry(rank.Value, rating.Value);
					state.Players[tag!] = state.Players.TryGetValue(tag!, out var existing) && existing.Pass == pass
						? existing.With(entry)
						: new CachedEntry(entry, pass);
				}

				state.LastCheckedUtc = _utcNow();
				var passCompleted = false;
				if(rollover)
				{
					// Back to the TOP, whatever page the rollover was noticed on. Advancing from here
					// would leave the new season's earlier pages — its BEST ranks — unfetched until the
					// next wrap, and the map has just been emptied, so there is nothing to preserve.
					state.NextPage = 1;
				}
				else if(state.MaxPage > 0 && page >= state.MaxPage)
				{
					// End of a pass: wrap rather than stop, and count it — the pass counter is what
					// switches the pacing from "fill fast" to "keep fresh".
					state.NextPage = 1;
					state.CompletedPasses++;
					passCompleted = true;
					PruneUnseen(state, pass);
					// One line per pass, so a background process accused of using bandwidth can say what
					// it did with it — and so the shared-name rate is observable in the field rather than
					// assumed. Counted after the prune, over the pass that just finished.
					var shared = state.Players.Count(kv => kv.Value.IsShared);
					Log($"{kind}/{region} pass {state.CompletedPasses} complete: {state.MaxPage} pages, " +
						$"{state.Players.Count} names cached, {shared} shared by more than one player");
				}
				else
				{
					state.NextPage = page + 1;
				}

				state.PagesSincePersist++;
				var due = passCompleted || state.PagesSincePersist >= PersistEveryPages;
				if(due)
					state.PagesSincePersist = 0;
				snapshot = due ? SnapshotLocked(state) : null;
			}

			// Serializing and writing happen with NO lock held: the UI thread's lookup takes the same
			// lock, so doing this inside it would make that "instant" lookup wait on a disk write.
			if(snapshot != null)
				WriteSnapshot(kind, region, snapshot);
			return true;
		}

		/// <summary>
		/// A number the payload states, or null when the field is absent or is not a number.
		///
		/// This exists because Newtonsoft's explicit <c>JToken</c> casts THROW on a wrong type rather
		/// than returning null, and an exception on this path is not a dropped row — it escapes the crawl
		/// task, which net472 then swallows as an unobserved exception, killing the crawl with no log line
		/// at all. Verified: a rating stated as a non-numeric string raised <c>FormatException</c>.
		/// Dropping the field matches <see cref="PayloadGuard"/>'s policy — a poisoned value must not
		/// become a stated one.
		/// </summary>
		private static double? ReadNumber(JToken? token)
		{
			if(token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
				return null;
			try
			{
				var value = token.Value<double?>();
				return value == null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
					? (double?)null
					: value;
			}
			catch(Exception ex) when(ex is OverflowException || ex is FormatException || ex is InvalidCastException)
			{
				// An integer too large for a double, and anything else the conversion can be forced into.
				return null;
			}
		}

		/// <summary>The same, for a field that must be a whole number in <see cref="int"/> range.</summary>
		private static int? ReadInt(JToken? token)
		{
			var value = ReadNumber(token);
			return value == null || value.Value < int.MinValue || value.Value > int.MaxValue
				? (int?)null
				: (int)value.Value;
		}

		/// <summary>
		/// Drops players the pass just finished never saw — they have left the leaderboard, and their
		/// cached rank is now a rank they do not hold. Called with the lock held.
		///
		/// A player who RISES past the crawl's cursor mid-pass is missed too and gets dropped, then
		/// reappears on the next pass. That direction is deliberate: reporting "not found" for a pass is
		/// the harmless error, while keeping a rank we can no longer see would be asserting one.
		/// </summary>
		private static void PruneUnseen(RegionState state, int pass)
		{
			// Materialized first: removing from a dictionary while enumerating it throws.
			var stale = state.Players.Where(kv => kv.Value.Pass < pass).Select(kv => kv.Key).ToList();
			foreach(var tag in stale)
				state.Players.Remove(tag);
		}

		/// <summary>Copies the state out for persistence. Called with the lock held, and deliberately does
		/// no serialization: struct copies are cheap, building JSON is not.</summary>
		private static PersistSnapshot SnapshotLocked(RegionState state)
			=> new PersistSnapshot
			{
				SeasonId = state.SeasonId,
				NextPage = state.NextPage,
				MaxPage = state.MaxPage,
				CompletedPasses = state.CompletedPasses,
				LastCheckedUtcTicks = state.LastCheckedUtc.Ticks,
				Players = state.Players.ToArray(),
			};

		private static async Task<PageResult> DownloadPageAsync(
			ArenaLeaderboardKind kind, string region, int page, CancellationToken token)
		{
			var leaderboardId = kind == ArenaLeaderboardKind.UndergroundArena ? "undergroundarena" : "arena";
			var url = $"{Endpoint}?region={region}&leaderboardId={leaderboardId}&page={page}";
			try
			{
				// Unlike HSReplay, this endpoint is NOT behind Cloudflare's TLS-fingerprint block —
				// verified live: plain requests with no special header succeed. The .NET stack is enough;
				// no curl shell-out needed here.
				//
				// HttpWebRequest rather than WebClient for two reasons the payload forces: gzip has to be
				// asked for (every raw page repeats ~99.5% metadata — see REPORT.md), and the body has to
				// be read under a CEILING while it streams. WebClient buffers the whole response before
				// anyone can look at its size, which makes a byte cap applied afterwards no bound at all.
				//
				// The response is parsed STRAIGHT off the stream, so the ~291 KB decompressed body is
				// never materialized as a byte array and a ~582 KB string on the way to a JObject — three
				// large-object allocations per page, on the only path here that repeats indefinitely.
				var request = (HttpWebRequest)WebRequest.Create(url);
				request.UserAgent = "Mozilla/5.0";
				request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
				request.Timeout = 20000;
				request.ReadWriteTimeout = 20000;

				// Aborting is what makes an in-flight page cancellable, and it is the ONLY thing that does:
				// the response stream does not override ReadAsync, so the token passed down the parse path
				// is checked before a read starts and then the read blocks. Do not remove this
				// registration as redundant, or an unload would wait out the read timeout. If Abort throws,
				// Dispose's own handler logs it rather than this swallowing it.
				using(token.Register(request.Abort))
				using(var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
				using(var stream = response.GetResponseStream())
				{
					var parsed = await PayloadGuard.ParseObjectAsync(stream, token).ConfigureAwait(false);
					if(parsed != null)
						return PageResult.Ok(parsed);
					// A body we cannot parse is a format change or a poisoned payload, not a blip: another
					// request would fetch the same thing again.
					Log($"page fetch unusable ({region}, page {page}): payload rejected");
					return PageResult.Permanent();
				}
			}
			catch(WebException ex)
			{
				if(token.IsCancellationRequested)
					return PageResult.Permanent(); // unloading: not a failure worth a log line or a retry
				var transient = IsTransient(ex);
				Log($"page fetch failed ({region}, page {page}, " +
					$"{(transient ? "transient" : "not retryable")}): {ex.Message}");
				return transient ? PageResult.Transient() : PageResult.Permanent();
			}
			catch(Exception ex)
			{
				if(token.IsCancellationRequested)
					return PageResult.Permanent();
				// Unknown failures do NOT retry: the safe direction here is to stop, not to hammer.
				Log($"page fetch failed ({region}, page {page}, not retryable): {ex.Message}");
				return PageResult.Permanent();
			}
		}

		/// <summary>
		/// Is this worth another attempt? Only where the server did not refuse us: a timeout, a dropped or
		/// unresolvable connection, or a 5xx. A **4xx is Blizzard declining** — including the 429 and 403 a
		/// rate limiter would use — and retrying into a refusal is precisely what Data sources &amp; ethics
		/// tells this project not to do.
		/// </summary>
		private static bool IsTransient(WebException ex)
		{
			switch(ex.Status)
			{
				case WebExceptionStatus.Timeout:
				case WebExceptionStatus.ConnectFailure:
				case WebExceptionStatus.ConnectionClosed:
				case WebExceptionStatus.KeepAliveFailure:
				case WebExceptionStatus.NameResolutionFailure:
				case WebExceptionStatus.ProxyNameResolutionFailure:
				case WebExceptionStatus.ReceiveFailure:
				case WebExceptionStatus.SendFailure:
				case WebExceptionStatus.PipelineFailure:
					return true;
				case WebExceptionStatus.ProtocolError:
					// Server-side failures only. Anything the client is being told about itself is final.
					return ex.Response is HttpWebResponse http && (int)http.StatusCode >= 500;
				default:
					return false;
			}
		}

		// ---- persistence ----------------------------------------------------------

		private string CacheFile(ArenaLeaderboardKind kind, string region)
			=> Path.Combine(_cacheDir, $"arena_leaderboard_{kind}_{region}.json");

		/// <summary>Internal so tests can drive a resume without a network call.</summary>
		internal void LoadPersisted(ArenaLeaderboardKind kind, string region)
		{
			try
			{
				var path = CacheFile(kind, region);
				if(!File.Exists(path))
					return;
				var obj = PayloadGuard.ParseObject(File.ReadAllText(path));
				if(obj == null)
					return;

				// Read through the same guards as a network payload: a cache file is a file on a user's
				// disk, so a corrupted or edited one must not be able to throw either.
				var state = new RegionState
				{
					SeasonId = ReadInt(obj["seasonId"]) ?? 0,
					NextPage = Math.Max(ReadInt(obj["nextPage"]) ?? 1, 1),
					MaxPage = Math.Min(Math.Max(ReadInt(obj["maxPage"]) ?? 0, 0), MaxPageCeiling),
					CompletedPasses = Math.Max(ReadInt(obj["completedPasses"]) ?? 0, 0),
					// Clamped, not trusted: an out-of-range tick count would throw and cost the whole
					// resumed state — i.e. a full re-crawl — over one bad field.
					LastCheckedUtc = TicksToUtc((long?)ReadNumber(obj["lastCheckedUtcTicks"]) ?? 0L),
				};
				if(obj["players"] is JObject players)
				{
					foreach(var prop in players.Properties())
					{
						var rank = ReadInt(prop.Value["rank"]);
						var rating = ReadNumber(prop.Value["rating"]);
						if(rank == null || rank < 1 || rating == null)
							continue;
						// A row with no pass stamp comes from a cache written before pruning existed;
						// crediting it to the current pass keeps it until the next pass can judge it,
						// rather than deleting a whole cache the moment this version first runs.
						var pass = ReadInt(prop.Value["pass"]) ?? state.CurrentPass;
						ArenaLeaderboardEntry[]? others = null;
						if(prop.Value["others"] is JArray othersJson)
						{
							var parsed = new List<ArenaLeaderboardEntry>();
							foreach(var other in othersJson)
							{
								var otherRank = ReadInt(other["rank"]);
								var otherRating = ReadNumber(other["rating"]);
								if(otherRank != null && otherRank >= 1 && otherRating != null)
									parsed.Add(new ArenaLeaderboardEntry(otherRank.Value, otherRating.Value));
							}
							others = parsed.Count > 0 ? parsed.ToArray() : null;
						}
						state.Players[prop.Name] = new CachedEntry(
							new ArenaLeaderboardEntry(rank.Value, rating.Value), pass, others);
					}
				}
				lock(_lock)
				{
					// ONLY when nothing is live yet. A crawl is restarted whenever it fail-softs, so this
					// runs again mid-session — and overwriting would rewind the cursor to the last
					// persisted page (up to PersistEveryPages back), throw away every row learned since,
					// and resurrect rows a prune had removed. With frequent failures the crawl could
					// never advance past its own checkpoint.
					if(_state.ContainsKey((kind, region)))
					{
						Log($"{kind}/{region} already live in memory; keeping it over the cache file");
						return;
					}
					_state[(kind, region)] = state;
				}
				Log($"resumed {kind}/{region} crawl at page {state.NextPage} ({state.Players.Count} players cached)");
			}
			catch(Exception ex)
			{
				Log($"cache load failed ({kind}, {region}): {ex.Message}");
			}
		}

		private static DateTime TicksToUtc(long ticks)
			=> ticks < 0 || ticks > DateTime.MaxValue.Ticks
				? DateTime.MinValue
				: new DateTime(ticks, DateTimeKind.Utc);

		/// <summary>Writes a snapshot. Must be called with NO lock held: this is the slow half, and the
		/// lock it would otherwise hold is the one the UI-thread lookup takes.</summary>
		private void WriteSnapshot(ArenaLeaderboardKind kind, string region, PersistSnapshot state)
		{
			try
			{
				var obj = new JObject
				{
					["seasonId"] = state.SeasonId,
					["nextPage"] = state.NextPage,
					["maxPage"] = state.MaxPage,
					["completedPasses"] = state.CompletedPasses,
					["lastCheckedUtcTicks"] = state.LastCheckedUtcTicks,
				};
				var players = new JObject();
				foreach(var kv in state.Players)
				{
					var row = new JObject
					{
						["rank"] = kv.Value.Entry.Rank,
						["rating"] = kv.Value.Entry.Rating,
						["pass"] = kv.Value.Pass,
					};
					// Persisted, or a restart mid-pass would forget that a name is shared and answer with
					// one of the players as if it were the only one. Absent for ~98.5% of entries.
					if(kv.Value.IsShared)
					{
						var others = new JArray();
						foreach(var other in kv.Value.Others!)
							others.Add(new JObject { ["rank"] = other.Rank, ["rating"] = other.Rating });
						row["others"] = others;
					}
					players[kv.Key] = row;
				}
				obj["players"] = players;

				var path = CacheFile(kind, region);
				var tmp = path + ".tmp";
				File.WriteAllText(tmp, obj.ToString(Newtonsoft.Json.Formatting.None));
				if(File.Exists(path))
					File.Replace(tmp, path, null);
				else
					File.Move(tmp, path);
			}
			catch(Exception ex)
			{
				Log($"cache write failed ({kind}, {region}): {ex.Message}");
			}
		}

		private static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] leaderboard: {msg}");
	}
}
