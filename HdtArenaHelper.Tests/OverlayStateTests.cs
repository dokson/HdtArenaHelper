using System.Collections.Generic;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// Every test here is a bug that reached a live client and was found by a person looking at the screen.
	/// The rules lived inside <c>OnUpdate</c> next to WPF and HearthMirror calls, so none of them could be
	/// pinned; <see cref="OverlayState"/> exists to make them pinnable, and this is the pinning.
	///
	/// The arguments are the real event-arg types but their CONTENTS never matter — the state machine only
	/// ever looks at an object's type, because the type is what decides which teardown may drop which panel.
	/// </summary>
	public class OverlayStateTests
	{
		private const bool DataReady = true;

		private static RunSummaryEventArgs RunScreen()
			=> new RunSummaryEventArgs(new List<int>(), HearthDb.Enums.CardClass.MAGE, true, 3, 0);

		private static DeckReviewEventArgs DeckReview()
			=> new DeckReviewEventArgs(new List<DeckReviewCard>(), HearthDb.Enums.CardClass.MAGE, true, 5);

		[Fact]
		public void Nothing_showing_means_nothing_visible()
		{
			var state = new OverlayState();

			Assert.Null(state.ActiveScreen);
			Assert.False(state.WantVisible(DataReady));
		}

		[Fact]
		public void Data_not_ready_keeps_the_overlay_hidden_even_with_a_screen()
		{
			var state = new OverlayState();
			state.Show(RunScreen());

			Assert.False(state.WantVisible(dataReady: false));
			Assert.True(state.WantVisible(DataReady));
		}

		[Fact]
		public void Leaving_the_arena_screens_drops_the_run_panel()
		{
			var state = new OverlayState();
			state.Show(RunScreen());

			state.ArenaScreenLeft();

			// GHOST OVERLAY #1: nothing dropped the run panel when the client left the arena screens, so the
			// arena deck stats sat on top of a live Battlegrounds Duo game.
			Assert.False(state.WantVisible(DataReady));
		}

		[Fact]
		public void The_draft_panel_ending_does_NOT_drop_the_run_panel()
		{
			var state = new OverlayState();
			state.Show(RunScreen());

			state.DraftEnded();

			// THE REGRESSION: hanging the fix for ghost #1 on this event made the panel vanish for the rest of
			// the arena screen, because DraftEnded also fires on transitions the player stays in arena for —
			// and the watcher dedups, so it never raised the run summary again.
			Assert.True(state.WantVisible(DataReady));
			Assert.IsType<RunSummaryEventArgs>(state.ActiveScreen);
		}

		[Fact]
		public void The_run_screen_going_away_drops_the_run_panel_on_its_own()
		{
			var state = new OverlayState();
			state.Show(RunScreen());

			state.RunSummaryGone();

			// GHOST OVERLAY #2: switching between Underground and Normal Arena left the previous mode's run
			// panel up. The player is still in arena, so neither of the two events above can be the one that
			// clears it — hence a teardown of its own.
			Assert.False(state.WantVisible(DataReady));
		}

		[Fact]
		public void A_teardown_only_drops_its_OWN_kind_of_screen()
		{
			var state = new OverlayState();
			// The draft watcher and the in-game watchers poll on the same tick, so a teardown from one must
			// not wipe a screen the other has just put up.
			state.Show(DeckReview());
			state.RunSummaryGone();

			Assert.IsType<DeckReviewEventArgs>(state.ActiveScreen);
			Assert.True(state.WantVisible(DataReady));
		}

		[Fact]
		public void An_opponent_keeps_the_overlay_visible_with_no_screen_of_ours()
		{
			var state = new OverlayState();

			state.OpponentIdentified();

			// During a match there is no screen of ours outside a mulligan or a Discover, and the opponent's
			// standing belongs on screen for the whole game. This is the one rule that widens visibility, so
			// the next test is the one that keeps it honest.
			Assert.Null(state.ActiveScreen);
			Assert.True(state.WantVisible(DataReady));
		}

		[Fact]
		public void The_opponent_leaving_takes_that_visibility_away_again()
		{
			var state = new OverlayState();
			state.OpponentIdentified();

			state.OpponentGone();

			// Without this the standings flag would outlive its match — which is precisely how every ghost
			// overlay in this project has happened.
			Assert.False(state.WantVisible(DataReady));
		}

		[Fact]
		public void Leaving_the_arena_screens_does_not_touch_a_match_in_progress()
		{
			var state = new OverlayState();
			state.OpponentIdentified();

			// Starting a match IS leaving the arena screens, and it must not take the opponent's standings
			// down with the run panel.
			state.ArenaScreenLeft();

			Assert.True(state.WantVisible(DataReady));
		}

		[Fact]
		public void Re_entering_the_arena_screen_shows_the_run_panel_again()
		{
			var state = new OverlayState();
			state.Show(RunScreen());
			state.ArenaScreenLeft();
			Assert.False(state.WantVisible(DataReady));

			state.Show(RunScreen());

			// Leaving and coming back has to work, which on the watcher side needs the run signature cleared
			// too — hiding a panel that can never be shown again is the same bug as never hiding it.
			Assert.True(state.WantVisible(DataReady));
		}

		[Fact]
		public void Reset_clears_a_screen_and_a_match_alike()
		{
			var state = new OverlayState();
			state.Show(RunScreen());
			state.OpponentIdentified();

			state.Reset();

			// The plugin's (re)enable path, so nothing from a previous session bleeds into a new one.
			Assert.Null(state.ActiveScreen);
			Assert.False(state.MatchStandings);
			Assert.False(state.WantVisible(DataReady));
		}
	}
}
