using System.Globalization;
using System.IO;
using Xunit;

namespace HdtArenaHelper.Tests
{
	internal static class ArenaLeaderboardLookupExtensions
	{
		/// <summary>
		/// The one standing for a display name, or null when it is not cached. A convenience for the
		/// majority of cases, where the name belongs to exactly one player.
		///
		/// It ASSERTS that the name is not shared, deliberately: the production lookup returns a list
		/// precisely because a display name can belong to several players, and a test that silently took
		/// "the first" would be re-introducing the bug the list exists to prevent. Shared names have
		/// their own tests and go through <c>FindAll</c> explicitly.
		/// </summary>
		internal static ArenaLeaderboardEntry? TryGetKnown(
			this ArenaLeaderboardSource source, ArenaLeaderboardKind kind, string region, string name)
		{
			var all = source.FindAll(kind, region, name);
			Assert.True(all.Count <= 1,
				$"'{name}' resolved to {all.Count} players — use FindAll when the name is shared");
			return all.Count == 0 ? (ArenaLeaderboardEntry?)null : all[0];
		}
	}

	/// <summary>
	/// Drives <see cref="ArenaLeaderboardSource"/> entirely from SYNTHETIC payloads: no network call,
	/// no live leaderboard read. Every name, rating, season and page count below is invented fixture
	/// data chosen to make the rule under test obvious — deliberately NOT a real player, a real rating
	/// or a real page count, so nothing here can rot when the season turns over or the board resizes.
	/// The payload SHAPE is the only thing copied from the live endpoint, and it is what the parser
	/// under test exists to read.
	/// </summary>
	public class ArenaLeaderboardSourceTests
	{
		// Small enough to reason about: a 10-page board, so "last page" and "mid-crawl" are readable.
		private const int TotalPages = 10;
		private const int SeasonOne = 1;
		private const int SeasonTwo = 2;

		private static ArenaLeaderboardSource NewSource()
			=> new ArenaLeaderboardSource(NewCacheDir());

		private static string NewCacheDir()
			=> Path.Combine(Path.GetTempPath(), "ArenaHelperTests_" + System.Guid.NewGuid());

		private static string Page(int seasonId, int totalPages, params (string tag, int rank, double rating)[] rows)
		{
			var rowJson = string.Join(",", System.Array.ConvertAll(rows,
				r => $"{{\"rank\":{r.rank},\"accountid\":\"{r.tag}\"," +
					$"\"rating\":{r.rating.ToString(CultureInfo.InvariantCulture)}}}"));
			return $"{{\"seasonId\":{seasonId},\"leaderboard\":{{\"rows\":[{rowJson}]," +
				$"\"pagination\":{{\"totalPages\":{totalPages}}}}}}}";
		}

		[Fact]
		public void ApplyPage_merges_rows_and_resolves_by_tag()
		{
			var source = NewSource();
			var json = Page(SeasonOne, TotalPages, ("FirstPlayer", 1, 9.0), ("SecondPlayer", 5, 7.0));

			Assert.True(source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, json));

			var entry = source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "SecondPlayer");
			Assert.NotNull(entry);
			Assert.Equal(5, entry!.Value.Rank);
			Assert.Equal(7.0, entry.Value.Rating, 3);
		}

		[Fact]
		public void TryGetKnown_is_null_for_an_unseen_tag()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("FirstPlayer", 1, 9.0)));

			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "NeverCrawledPlayer"));
		}

		[Fact]
		public void TryGetKnown_is_null_for_an_unsupported_region()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("FirstPlayer", 1, 9.0)));

			// CHINA is deliberately excluded: the live endpoint silently serves EU's rows for "CN" and
			// for any unrecognized region alike, so a "CN" lookup would show a real rank under the
			// wrong player's name.
			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "CN", "FirstPlayer"));
		}

		[Fact]
		public void ApplyPage_keeps_arena_and_underground_arena_separate()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("SharedName", 1, 9.0)));
			source.ApplyPage(ArenaLeaderboardKind.UndergroundArena, "US", 1, Page(SeasonOne, TotalPages, ("SharedName", 40, 5000)));

			// The two boards run their own seasons and their own metric, so one name must resolve to
			// two independent standings.
			var arena = source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "SharedName");
			var underground = source.TryGetKnown(ArenaLeaderboardKind.UndergroundArena, "US", "SharedName");
			Assert.Equal(1, arena!.Value.Rank);
			Assert.Equal(40, underground!.Value.Rank);
			Assert.Equal(5000, underground.Value.Rating, 3);
		}

		[Fact]
		public void ApplyPage_a_season_rollover_clears_the_stale_map()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("OldSeasonPlayer", 1, 9.0)));
			Assert.NotNull(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "OldSeasonPlayer"));

			// A new season starts: the old ranks no longer mean anything and must not survive.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonTwo, TotalPages, ("NewSeasonPlayer", 1, 9.5)));

			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "OldSeasonPlayer"));
			Assert.NotNull(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "NewSeasonPlayer"));
		}

		[Fact]
		public void ProjectedRank_counts_the_players_rated_above_you()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1,
				Page(SeasonOne, TotalPages, ("Top", 1, 9.0), ("Middle", 2, 7.0), ("Low", 3, 5.0)));
			// A projection needs the WHOLE board: on a partial one the count of players above is
			// understated, which would flatter the player.
			Assert.Null(source.ProjectedRankFor(ArenaLeaderboardKind.Arena, "US", 6.0));

			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages,
				Page(SeasonOne, TotalPages, ("Bottom", 250, 1.0)));

			// 9.0 and 7.0 are above 6.0, so you would enter third.
			Assert.Equal(3, source.ProjectedRankFor(ArenaLeaderboardKind.Arena, "US", 6.0));
			// Above everyone.
			Assert.Equal(1, source.ProjectedRankFor(ArenaLeaderboardKind.Arena, "US", 9.5));
			// Below everyone: one place past the four cached players.
			Assert.Equal(5, source.ProjectedRankFor(ArenaLeaderboardKind.Arena, "US", 0.5));
		}

		[Fact]
		public void ProjectedRank_counts_every_holder_of_a_shared_name()
		{
			var source = NewSource();
			// Two players share a name, and BOTH sit above the queried rating: a projection that counted
			// names rather than players would place the player one rank too high.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1,
				Page(SeasonOne, TotalPages, ("SharedDisplayName", 1, 9.0), ("SharedDisplayName", 2, 8.0)));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages,
				Page(SeasonOne, TotalPages, ("Bottom", 250, 1.0)));

			Assert.Equal(3, source.ProjectedRankFor(ArenaLeaderboardKind.Arena, "US", 5.0));
		}

		[Fact]
		public void ApplyPage_rejects_malformed_json()
		{
			var source = NewSource();
			Assert.False(source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, "not json"));
		}

		[Fact]
		public void ApplyPage_advances_to_the_next_page()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("FirstPlayer", 1, 9.0)));

			Assert.Equal(2, source.NextPageFor(ArenaLeaderboardKind.Arena, "US"));
		}

		[Fact]
		public void ApplyPage_wraps_at_the_final_page_instead_of_stopping()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages, Page(SeasonOne, TotalPages, ("LastPlayer", 250, 0.5)));

			// The crawl has no terminal state. Stopping here would serve that season's ranks as current
			// forever, because a rollover is only detectable from a page a stopped crawl never fetches
			// again — and since the cursor is persisted, a restart would not recover either.
			Assert.Equal(1, source.NextPageFor(ArenaLeaderboardKind.Arena, "US"));
		}

		[Fact]
		public void ApplyPage_a_season_rollover_restarts_the_crawl_from_page_one()
		{
			var source = NewSource();
			// Mid-crawl: page 5 of the old season is in, so the next page would be 6.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 5, Page(SeasonOne, TotalPages, ("OldSeasonPlayer", 101, 5.9)));
			Assert.Equal(6, source.NextPageFor(ArenaLeaderboardKind.Arena, "US"));

			// The season turns over while we are on page 6. Clearing the map is not enough: the new
			// season's pages 1-5 have never been read, and marching on to page 7 would leave the top
			// ranks permanently missing for the whole season, since the crawl only moves forward.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 6, Page(SeasonTwo, TotalPages, ("NewSeasonPlayer", 126, 5.8)));

			Assert.Equal(1, source.NextPageFor(ArenaLeaderboardKind.Arena, "US"));
		}

		[Fact]
		public void A_second_pass_refreshes_a_rating_in_place()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("FirstPlayer", 1, 9.0)));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages, Page(SeasonOne, TotalPages, ("LastPlayer", 250, 0.5)));

			// Wrapped to page 1: the same rows arrive again with newer numbers, and the point of the
			// rolling crawl is that the cached standing MOVES rather than being frozen at first sight.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("FirstPlayer", 2, 8.5)));

			var entry = source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "FirstPlayer");
			Assert.Equal(2, entry!.Value.Rank);
			Assert.Equal(8.5, entry.Value.Rating, 3);
		}

		[Fact]
		public void ApplyPage_clamps_a_page_count_the_payload_inflates()
		{
			var source = NewSource();
			// totalPages drives the crawl loop, so an absurd value is a control-flow input, not a
			// display one: unclamped it would keep the crawl running for years.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, 2000000, ("FirstPlayer", 1, 9.0)));

			// Page 2 is still next; what matters is that the ceiling bounded MaxPage, which the wrap
			// below then proves by wrapping at the ceiling rather than at two million.
			Assert.Equal(2, source.NextPageFor(ArenaLeaderboardKind.Arena, "US"));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 5000, Page(SeasonOne, 2000000, ("FirstPlayer", 1, 9.0)));
			Assert.Equal(1, source.NextPageFor(ArenaLeaderboardKind.Arena, "US"));
		}

		[Fact]
		public void Persisted_progress_round_trips_so_a_restart_resumes()
		{
			var dir = NewCacheDir();
			var first = new ArenaLeaderboardSource(dir);
			first.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("CachedPlayer", 51, 6.6)));
			// Completing a pass is one of the two things that trigger a write; progress is NOT written
			// per page, because the map is rewritten whole and hundreds of rewrites per pass is what
			// that cost. Losing a few pages of cursor to a crash only re-reads them.
			first.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages, Page(SeasonOne, TotalPages, ("LastPlayer", 250, 0.5)));

			// A restart: a fresh instance over the same cache dir must resume rather than re-crawl.
			var resumed = new ArenaLeaderboardSource(dir);
			resumed.LoadPersisted(ArenaLeaderboardKind.Arena, "US");

			Assert.Equal(1, resumed.NextPageFor(ArenaLeaderboardKind.Arena, "US"));
			var entry = resumed.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "CachedPlayer");
			Assert.NotNull(entry);
			Assert.Equal(51, entry!.Value.Rank);
			Assert.Equal(6.6, entry.Value.Rating, 3);
		}

		[Fact]
		public void A_completed_pass_drops_a_player_who_left_the_board()
		{
			var source = NewSource();
			// Pass 1 sees both players.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("StaysOnBoard", 1, 9.0), ("LeavesBoard", 2, 8.9)));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages, Page(SeasonOne, TotalPages, ("LastPlayer", 250, 0.5)));
			Assert.NotNull(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "LeavesBoard"));

			// Pass 2: one of them is gone from the payload. Rows are overwritten rather than rebuilt, so
			// without pruning their old rank would be served forever as if they still held it.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, TotalPages, ("StaysOnBoard", 1, 9.1)));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages, Page(SeasonOne, TotalPages, ("LastPlayer", 250, 0.5)));

			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "LeavesBoard"));
			Assert.Equal(9.1, source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "StaysOnBoard")!.Value.Rating, 3);
		}

		[Fact]
		public void A_pass_finished_across_a_restart_does_not_prune_the_previous_run_rows()
		{
			// A board big enough that progress is written MID-pass (every 100 pages), which is what makes
			// a resume land in the middle of a pass rather than at its start.
			const int bigBoard = 150;
			var dir = NewCacheDir();

			var first = new ArenaLeaderboardSource(dir);
			first.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, Page(SeasonOne, bigBoard, ("EarlyPagePlayer", 1, 9.0)));
			for(var p = 2; p <= 100; p++)
				first.ApplyPage(ArenaLeaderboardKind.Arena, "US", p, Page(SeasonOne, bigBoard, ("Filler", p, 5.0)));

			// Restart, then finish the SAME pass. EarlyPagePlayer's page is never re-read, so only the
			// persisted pass stamp can tell the prune that the row is current. Without it the row looks
			// like it belongs to an older pass and most of the cache is deleted on the first wrap.
			var resumed = new ArenaLeaderboardSource(dir);
			resumed.LoadPersisted(ArenaLeaderboardKind.Arena, "US");
			Assert.Equal(101, resumed.NextPageFor(ArenaLeaderboardKind.Arena, "US"));
			for(var p = 101; p <= bigBoard; p++)
				resumed.ApplyPage(ArenaLeaderboardKind.Arena, "US", p, Page(SeasonOne, bigBoard, ("Filler", p, 5.0)));

			Assert.NotNull(resumed.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "EarlyPagePlayer"));
		}

		[Fact]
		public void A_shared_display_name_returns_every_holder_best_rank_first()
		{
			var source = NewSource();
			// Display names are NOT unique — the discriminator is what makes a BattleTag unique, and the
			// leaderboard publishes the name alone. Measured on a real board: 14 names of 1,910 repeat,
			// and their holders are far apart (one name at ranks 436, 1467 and 1864). Naming one of them
			// as the opponent would state a real rank under the wrong player's name, which is what the
			// region whitelist exists to refuse — so all of them come back, and the caller presents them
			// as alternatives. See REPORT.md 17.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1,
				Page(SeasonOne, TotalPages,
					("SharedDisplayName", 400, 4.0), ("UniqueName", 2, 8.0), ("SharedDisplayName", 1, 9.0)));

			var all = source.FindAll(ArenaLeaderboardKind.Arena, "US", "SharedDisplayName");
			Assert.Equal(2, all.Count);
			// Best rank first, whatever order the payload listed them in.
			Assert.Equal(1, all[0].Rank);
			Assert.Equal(400, all[1].Rank);
			Assert.Single(source.FindAll(ArenaLeaderboardKind.Arena, "US", "UniqueName"));
		}

		[Fact]
		public void A_shared_name_holds_holders_found_on_DIFFERENT_pages_of_one_pass()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1,
				Page(SeasonOne, TotalPages, ("SharedDisplayName", 1, 9.0)));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 2,
				Page(SeasonOne, TotalPages, ("SharedDisplayName", 50, 6.0)));

			Assert.Equal(2, source.FindAll(ArenaLeaderboardKind.Arena, "US", "SharedDisplayName").Count);
		}

		[Fact]
		public void A_name_that_stops_being_shared_goes_back_to_one_holder()
		{
			var source = NewSource();
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1,
				Page(SeasonOne, TotalPages, ("SharedDisplayName", 1, 9.0)));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 2,
				Page(SeasonOne, TotalPages, ("SharedDisplayName", 50, 6.0)));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages, Page(SeasonOne, TotalPages, ("LastPlayer", 250, 0.5)));
			Assert.Equal(2, source.FindAll(ArenaLeaderboardKind.Arena, "US", "SharedDisplayName").Count);

			// Next pass, only one holder is on the board. Sharing is a property of a PASS, so the entry
			// must collapse back to one rather than accumulate holders forever.
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1,
				Page(SeasonOne, TotalPages, ("SharedDisplayName", 1, 9.0)));
			source.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages, Page(SeasonOne, TotalPages, ("LastPlayer", 250, 0.5)));

			var all = source.FindAll(ArenaLeaderboardKind.Arena, "US", "SharedDisplayName");
			Assert.Single(all);
			Assert.Equal(1, all[0].Rank);
		}

		[Fact]
		public void Shared_holders_survive_a_restart()
		{
			var dir = NewCacheDir();
			var first = new ArenaLeaderboardSource(dir);
			first.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1,
				Page(SeasonOne, TotalPages, ("SharedDisplayName", 1, 9.0), ("SharedDisplayName", 400, 4.0)));
			first.ApplyPage(ArenaLeaderboardKind.Arena, "US", TotalPages, Page(SeasonOne, TotalPages, ("LastPlayer", 250, 0.5)));

			// Without persisting the extra holders, a restart mid-pass would forget the name is shared and
			// start answering with one player as though they were the only one.
			var resumed = new ArenaLeaderboardSource(dir);
			resumed.LoadPersisted(ArenaLeaderboardKind.Arena, "US");

			Assert.Equal(2, resumed.FindAll(ArenaLeaderboardKind.Arena, "US", "SharedDisplayName").Count);
		}

		[Fact]
		public void ApplyPage_skips_a_row_whose_number_is_the_wrong_TYPE()
		{
			var source = NewSource();
			// Newtonsoft's explicit JToken casts THROW on a wrong type instead of returning null, so a
			// feed that states a rating as a non-numeric string can kill the crawl task outright — and
			// net472 swallows an unobserved task exception, so it would die with no log line at all.
			// Policy is the same as PayloadGuard's: drop the row, keep the rest.
			var json = $"{{\"seasonId\":{SeasonOne},\"leaderboard\":{{\"rows\":[" +
				"{\"rank\":1,\"accountid\":\"StringRating\",\"rating\":\"not a number\"}," +
				"{\"rank\":\"NaN\",\"accountid\":\"StringRank\",\"rating\":7.0}," +
				"{\"rank\":{\"nested\":1},\"accountid\":\"ObjectRank\",\"rating\":7.0}," +
				"{\"rank\":0,\"accountid\":\"ZeroRank\",\"rating\":7.0}," +
				"{\"rank\":2,\"accountid\":\"CompleteRow\",\"rating\":7.5}]," +
				$"\"pagination\":{{\"totalPages\":{TotalPages}}}}}}}";

			Assert.True(source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, json));

			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "StringRating"));
			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "StringRank"));
			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "ObjectRank"));
			// A rank of 0 is not a rank: the board is 1-based, so this is a poisoned value, not a row.
			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "ZeroRank"));
			Assert.NotNull(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "CompleteRow"));
		}

		[Fact]
		public void ApplyPage_rejects_a_payload_whose_season_is_the_wrong_type()
		{
			var source = NewSource();
			// seasonId keys the whole rollover check, so an unreadable one must void the page rather
			// than be silently treated as season 0 — or as an exception.
			var json = "{\"seasonId\":\"fifty-six\",\"leaderboard\":{\"rows\":[" +
				"{\"rank\":1,\"accountid\":\"FirstPlayer\",\"rating\":9.0}]," +
				$"\"pagination\":{{\"totalPages\":{TotalPages}}}}}}}";

			Assert.False(source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, json));
		}

		[Fact]
		public void ApplyPage_skips_a_row_missing_a_field_and_keeps_the_rest()
		{
			var source = NewSource();
			// An invalid leaderboardId returns a DIFFERENT board whose rows carry no "rating" at all,
			// so a row missing a field is a real payload shape rather than a hypothetical one.
			var json = $"{{\"seasonId\":{SeasonOne},\"leaderboard\":{{\"rows\":[" +
				"{\"rank\":1,\"accountid\":\"RowWithNoRating\"}," +
				"{\"rank\":2,\"accountid\":\"CompleteRow\",\"rating\":7.5}]," +
				$"\"pagination\":{{\"totalPages\":{TotalPages}}}}}}}";

			Assert.True(source.ApplyPage(ArenaLeaderboardKind.Arena, "US", 1, json));

			Assert.Null(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "RowWithNoRating"));
			Assert.NotNull(source.TryGetKnown(ArenaLeaderboardKind.Arena, "US", "CompleteRow"));
		}
	}
}
