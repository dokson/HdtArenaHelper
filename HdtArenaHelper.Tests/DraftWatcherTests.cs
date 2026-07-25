using HearthDb;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class DraftWatcherTests
	{
		[Fact]
		public void ToDbfId_resolves_a_real_card_id()
		{
			var dbf = DraftWatcher.ToDbfId("CS2_182"); // Chillwind Yeti

			Assert.NotEqual(0, dbf);
			Assert.Equal(Cards.All["CS2_182"].DbfId, dbf);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("NOT_A_REAL_CARD_ID")]
		public void ToDbfId_returns_zero_for_missing_or_unknown_ids(string? cardId)
		{
			Assert.Equal(0, DraftWatcher.ToDbfId(cardId));
		}

		[Fact]
		public void ToClass_resolves_the_drafted_hero_class()
		{
			Assert.Equal(HearthDb.Enums.CardClass.WARRIOR, DraftWatcher.ToClass("HERO_01"));
			Assert.Equal(HearthDb.Enums.CardClass.MAGE, DraftWatcher.ToClass("HERO_08"));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("NOT_A_REAL_CARD_ID")]
		public void ToClass_returns_invalid_before_the_hero_pick(string? heroCardId)
		{
			Assert.Equal(HearthDb.Enums.CardClass.INVALID, DraftWatcher.ToClass(heroCardId));
		}

		// Choices linger in client memory outside the draft (landing screen, mid-run):
		// only draft/redraft states may show the overlay.
		[Theory]
		[InlineData(HearthMirror.Enums.ArenaSessionState.DRAFTING, true)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.REDRAFTING, true)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.MIDRUN_REDRAFT_PENDING, true)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.MIDRUN, false)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.EDITING_DECK, false)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.INVALID, false)]
		public void Only_draft_and_redraft_states_are_active(HearthMirror.Enums.ArenaSessionState state, bool active)
		{
			Assert.Equal(active, DraftWatcher.IsActiveDraftState(state));
		}

		// ---- deck-review plan (the redraft "Edit Your Deck" phase) --------------------

		private static System.Collections.Generic.IReadOnlyList<(string Id, int Count)> Pairs(
			params (string, int)[] cards) => cards;

		[Fact]
		public void Deck_edit_plan_takes_the_discard_count_from_the_redraft_cards()
		{
			// Verified on a live client: the deck reads 30/30 for the whole phase with the new cards
			// already inside it, so "deckSize - 30" is zero and cannot drive this. The number to cut
			// is the number that arrived.
			var plan = DraftWatcher.BuildDeckEditPlan(Pairs(("CS2_182", 30)), Pairs(("CS2_172", 5)));

			Assert.Equal(5, plan.Over);
			Assert.Equal(7, plan.Suggest); // the five to cut, plus a margin to choose from
		}

		[Fact]
		public void Deck_edit_plan_stays_up_while_the_phase_lasts()
		{
			// The client exposes no discard progress — the deck is 30/30 before and after selecting
			// cards — so the plan must NOT try to count down. An earlier version expected the deck to
			// shrink; when it did not, the panel stayed on screen for the rest of the run.
			var before = DraftWatcher.BuildDeckEditPlan(Pairs(("CS2_182", 30)), Pairs(("CS2_172", 5)));
			var after = DraftWatcher.BuildDeckEditPlan(Pairs(("CS2_182", 30)), Pairs(("CS2_172", 5)));

			Assert.Equal(before.Over, after.Over);
			Assert.True(after.Over > 0, "the panel must remain valid for the whole phase");
		}

		[Fact]
		public void Deck_edit_plan_falls_back_to_an_oversized_deck()
		{
			// If a future client build DOES report an oversized deck instead of a redraft list, the
			// count still comes out right rather than the panel vanishing.
			var plan = DraftWatcher.BuildDeckEditPlan(Pairs(("CS2_182", 30), ("CS2_172", 5)), null);

			Assert.Equal(35, plan.DeckSize);
			Assert.Equal(5, plan.Over);
		}

		[Fact]
		public void Deck_edit_plan_never_suggests_fewer_than_five()
		{
			var plan = DraftWatcher.BuildDeckEditPlan(Pairs(("CS2_182", 30)), Pairs(("CS2_172", 1)));
			Assert.Equal(1, plan.Over);
			Assert.Equal(5, plan.Suggest);
		}

		[Fact]
		public void Deck_edit_plan_unions_both_lists_and_keeps_the_larger_count()
		{
			// Ranking either list alone misses cards; on overlap the larger copy count wins
			// (it only drives the "xN" label, never the ranking).
			var plan = DraftWatcher.BuildDeckEditPlan(
				Pairs(("CS2_182", 2), ("CS2_172", 30)), Pairs(("CS2_182", 3), ("CS2_168", 1)));

			Assert.Equal(3, plan.ByDbf.Count);
			Assert.Equal(3, plan.ByDbf[DraftWatcher.ToDbfId("CS2_182")]);
			Assert.Equal(1, plan.ByDbf[DraftWatcher.ToDbfId("CS2_168")]);
		}

		[Fact]
		public void Deck_edit_plan_ignores_unresolvable_cards_and_empty_input()
		{
			Assert.Empty(DraftWatcher.BuildDeckEditPlan(null, null).ByDbf);
			Assert.Empty(DraftWatcher.BuildDeckEditPlan(Pairs(("NOT_A_CARD", 3)), null).ByDbf);
			// A zero/negative count still means one copy present, not a vanished card.
			Assert.Equal(1, DraftWatcher.BuildDeckEditPlan(Pairs(("CS2_182", 0)), null)
				.ByDbf[DraftWatcher.ToDbfId("CS2_182")]);
		}
	}
}
