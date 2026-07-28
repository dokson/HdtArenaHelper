using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// Drives the crawl LOOP with a mocked clock, page source and delay, so the behaviour that is
	/// invisible from outside gets pinned: how fast it paces itself, when it stops, and which board is
	/// allowed to run. None of this could be tested against the real thing — the pacing is measured in
	/// hours and the pages come from Blizzard — so it was previously verified only by reading, which is
	/// exactly the standard this repo rejects elsewhere.
	///
	/// No network, no waiting: the fake delay records what was asked for and returns immediately, and the
	/// fake clock only moves when a test moves it.
	/// </summary>
	public class ArenaLeaderboardCrawlTests
	{
		private const int Board = 10;
		private const int Season = 1;

		private static string NewCacheDir()
			=> Path.Combine(Path.GetTempPath(), "ArenaHelperCrawlTests_" + Guid.NewGuid());

		/// <summary>A page of the fake board: one row whose rank is derived from the page, so a test can
		/// tell which pages were actually visited.</summary>
		private static ArenaLeaderboardSource.PageResult Ok(int page, int totalPages = Board, int season = Season)
			=> ArenaLeaderboardSource.PageResult.Ok(FakePage(page, totalPages, season));

		/// <summary>Ends the crawl in a test. Deliberately the PERMANENT outcome: a transient one would be
		/// retried, so using it to stop a test would quietly exercise the retry path instead.</summary>
		private static ArenaLeaderboardSource.PageResult Stop()
			=> ArenaLeaderboardSource.PageResult.Permanent();

		private static ArenaLeaderboardSource.PageResult Blip()
			=> ArenaLeaderboardSource.PageResult.Transient();

		private static JObject FakePage(int page, int totalPages = Board, int season = Season)
			=> JObject.Parse(
				$"{{\"seasonId\":{season},\"leaderboard\":{{\"rows\":[" +
				$"{{\"rank\":{page},\"accountid\":\"PlayerOnPage{page}\",\"rating\":" +
				$"{(9.0 - page * 0.1).ToString(CultureInfo.InvariantCulture)}}}]," +
				$"\"pagination\":{{\"totalPages\":{totalPages}}}}}}}");

		private sealed class Harness
		{
			/// <summary>
			/// A hard stop on how many pages any test may fetch. The crawl deliberately has no terminal
			/// state — it wraps forever — and the delays here complete instantly, so its only real stopping
			/// conditions are the activity window and the active-pair check. If a change removes one of
			/// those, this turns an endless spin into a failed assertion instead of a hung suite. Verified:
			/// with the activity window disabled the run does not terminate.
			/// </summary>
			private const int FetchBudget = 500;

			internal DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
			internal readonly List<int> PagesFetched = new List<int>();
			internal readonly List<TimeSpan> Delays = new List<TimeSpan>();
			internal Func<int, ArenaLeaderboardSource.PageResult> Respond =
				page => ArenaLeaderboardSource.PageResult.Ok(FakePage(page));
			internal ArenaLeaderboardSource Source { get; }

			internal Harness(string? cacheDir = null)
			{
				Source = new ArenaLeaderboardSource(
					cacheDir ?? NewCacheDir(),
					() => Now,
					(kind, region, page, token) =>
					{
						Assert.True(PagesFetched.Count < FetchBudget,
							$"crawl fetched {FetchBudget} pages without stopping — a stop condition is gone");
						PagesFetched.Add(page);
						return Task.FromResult(Respond(page));
					},
					(delay, token) =>
					{
						Delays.Add(delay);
						return Task.CompletedTask;
					});
			}

			/// <summary>Marks arena activity the way a real opponent sighting does, then runs the loop to
			/// completion. Deliberately NOT via EnsureCrawling: that launches its own fire-and-forget
			/// crawl, and two loops over one state interleave their page fetches.</summary>
			internal Task Crawl(ArenaLeaderboardKind kind = ArenaLeaderboardKind.Arena, string region = "US")
			{
				Source.MarkActive(kind, region);
				return Source.CrawlRegionAsync(kind, region, CancellationToken.None);
			}
		}

		[Fact]
		public async Task A_first_pass_walks_every_page_once_in_order()
		{
			var h = new Harness();
			h.Respond = page => h.PagesFetched.Count >= Board ? Stop() : Ok(page);

			await h.Crawl();

			Assert.Equal(Enumerable.Range(1, Board), h.PagesFetched.Take(Board));
		}

		[Fact]
		public async Task The_first_pass_is_paced_fast_so_the_cache_becomes_useful_quickly()
		{
			var h = new Harness();
			h.Respond = page => h.PagesFetched.Count >= Board ? Stop() : Ok(page);

			await h.Crawl();

			// Until a pass completes almost every opponent reports "not found", which is indistinguishable
			// from a broken plugin — so the first pass must not run at the slow maintenance pace.
			Assert.All(h.Delays.Take(Board - 1), d => Assert.True(d < TimeSpan.FromSeconds(5), $"{d} is not a fast pace"));
		}

		[Fact]
		public async Task After_the_first_pass_one_full_pass_takes_the_refresh_interval()
		{
			var h = new Harness();
			// Stop once the second pass has begun, so the delays recorded after the wrap are maintenance ones.
			h.Respond = page => h.PagesFetched.Count > Board + 2 ? Stop() : Ok(page);

			await h.Crawl();

			var maintenance = h.Delays.Skip(Board).ToList();
			Assert.NotEmpty(maintenance);
			// The pace is DERIVED so that one pass spans the refresh interval, rather than being a fixed
			// number of seconds that would silently redefine that interval whenever the board resizes.
			var expected = TimeSpan.FromTicks(TimeSpan.FromHours(24).Ticks / Board);
			Assert.All(maintenance, d => Assert.Equal(expected, d));
		}

		[Fact]
		public async Task The_crawl_stops_when_no_arena_match_has_happened_recently()
		{
			var h = new Harness();

			// Two pages in, the player stops playing arena: the clock jumps past the activity window.
			h.Respond = page =>
			{
				if(h.PagesFetched.Count == 2)
					h.Now += TimeSpan.FromHours(2);
				return Ok(page);
			};

			await h.Crawl();

			// Traffic has to follow DEMAND, not HDT's uptime: without this the crawl kept pulling pages for
			// the rest of the session while the player was drafting, in Battlegrounds, or away entirely.
			Assert.Equal(2, h.PagesFetched.Count);
		}

		[Fact]
		public async Task Activity_resumes_a_stopped_crawl_from_the_persisted_cursor()
		{
			var dir = NewCacheDir();
			var first = new Harness(dir);
			first.Respond = page =>
			{
				if(first.PagesFetched.Count == 3)
					first.Now += TimeSpan.FromHours(2);
				return Ok(page);
			};
			await first.Crawl();
			Assert.Equal(3, first.PagesFetched.Count);

			// A later match marks activity again. Progress is only written every 100 pages or at a pass
			// boundary, so resuming from page 1 here is correct — what must NOT happen is nothing at all.
			var second = new Harness(dir);
			second.Respond = page => second.PagesFetched.Count >= 2 ? Stop() : Ok(page);
			await second.Crawl();

			Assert.NotEmpty(second.PagesFetched);
		}

		[Fact]
		public async Task Another_board_taking_over_stops_the_first_one()
		{
			var h = new Harness();
			h.Respond = page =>
			{
				// Mid-crawl the player starts an Underground match, which claims the client's crawl budget.
				if(h.PagesFetched.Count == 2)
					h.Source.MarkActive(ArenaLeaderboardKind.UndergroundArena, "US");
				return Ok(page);
			};

			await h.Crawl();

			// A pacing budget is a politeness budget, so it belongs to the CLIENT: two boards crawling at
			// once would double the traffic and make the refresh interval a per-board promise instead.
			Assert.Equal(2, h.PagesFetched.Count);
		}

		[Fact]
		public async Task A_transient_blip_is_retried_and_the_pass_carries_on()
		{
			var h = new Harness();
			var blipsLeft = 2;
			h.Respond = page =>
			{
				if(page == 3 && blipsLeft-- > 0)
					return Blip();
				return h.PagesFetched.Count >= Board + 2 ? Stop() : Ok(page);
			};

			await h.Crawl();

			// Page 3 was asked for three times and the crawl carried on past it. Without a retry a single
			// dropped connection ended the pass, which at a 24-hour pace costs days of coverage.
			Assert.Equal(3, h.PagesFetched.Count(p => p == 3));
			Assert.Contains(4, h.PagesFetched);
		}

		[Fact]
		public async Task A_transient_failure_that_persists_gives_up_after_a_bounded_number_of_attempts()
		{
			var h = new Harness();
			h.Respond = page => page == 3 ? Blip() : Ok(page);

			await h.Crawl();

			// Bounded: an outage must not turn the crawl into a hammer. The cursor is persisted, so the
			// next arena match picks the pass back up.
			Assert.Equal(3, h.PagesFetched.Count(p => p == 3));
			Assert.DoesNotContain(4, h.PagesFetched);
		}

		[Fact]
		public async Task A_permanent_failure_is_NOT_retried()
		{
			var h = new Harness();
			h.Respond = page => page == 3 ? Stop() : Ok(page);

			await h.Crawl();

			// A 4xx or an unparseable body is Blizzard declining or a format change: asking again is rude
			// in the first case and pointless in the second. Exactly one request for that page.
			Assert.Equal(new[] { 1, 2, 3 }, h.PagesFetched);
		}

		[Fact]
		public async Task A_retry_waits_before_asking_again()
		{
			var h = new Harness();
			var blipped = false;
			h.Respond = page =>
			{
				if(page == 2 && !blipped)
				{
					blipped = true;
					return Blip();
				}
				return h.PagesFetched.Count >= 4 ? Stop() : Ok(page);
			};

			await h.Crawl();

			// The backoff has to be a real wait, not an immediate second request at the same host.
			Assert.Contains(h.Delays, d => d >= TimeSpan.FromSeconds(5));
		}

		[Fact]
		public async Task A_season_rollover_mid_pass_restarts_from_page_one()
		{
			var h = new Harness();
			h.Respond = page =>
			{
				if(h.PagesFetched.Count > 8)
					return Stop();
				// The season turns over on the fourth page fetched.
				return h.PagesFetched.Count >= 4 ? Ok(page, Board, Season + 1) : Ok(page);
			};

			await h.Crawl();

			// Advancing from where the rollover was noticed would leave the new season's best ranks
			// unfetched until the next wrap, so the cursor goes back to the top instead.
			Assert.Contains(1, h.PagesFetched.Skip(4));
		}
	}
}
