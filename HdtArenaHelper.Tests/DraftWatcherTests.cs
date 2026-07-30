using HdtArenaHelper.CardDatabase;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class DraftWatcherTests
	{
		// The watcher's whole job is turning the client's card IDS into dbf ids, so the ids are the
		// subject here rather than an implementation detail — but they still come from the named pool,
		// so a fixture cannot claim to be one card and be another.
		private static readonly CardEntry Yeti = HSCard.ChillwindYeti;
		private static readonly CardEntry Raptor = HSCard.BloodfenRaptor;
		private static readonly CardEntry Murloc = HSCard.MurlocRaider;

		[Fact]
		public void ToDbfId_resolves_a_real_card_id()
		{
			var dbf = DraftWatcher.ToDbfId(Yeti.CardId);

			Assert.NotEqual(0, dbf);
			Assert.Equal(Yeti.DbfId, dbf);
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
			Assert.Equal(HearthDb.Enums.CardClass.WARRIOR,
				DraftWatcher.ToClass(HSHero.GarroshHellscream.CardId));
			Assert.Equal(HearthDb.Enums.CardClass.MAGE,
				DraftWatcher.ToClass(HSHero.JainaProudmoore.CardId));
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

		// ---- poll routing: which screen a poll is about ------------------------------
		//
		// The bug this pins showed nothing and logged nothing, so nothing but the routing can catch it.

		/// <summary>
		/// A FINISHED draft: the client keeps the last pick's three choices in memory while the session
		/// state moves on to MIDRUN. This is the case that was broken — the run screen was reported only
		/// when the choice zone was EMPTY, so after a 30th pick the deck panel and the player's own rating
		/// never appeared, for as long as they sat there.
		/// </summary>
		[Fact]
		public void A_finished_draft_reports_the_run_screen_even_with_the_last_pick_still_in_memory()
		{
			Assert.Equal(DraftWatcher.PollRoute.RunOrNothing,
				DraftWatcher.RouteFor(HearthMirror.Enums.ArenaSessionState.MIDRUN, 3));
		}

		/// <summary>
		/// The same, one state wider: a choice count says something about ANIMATION, never about which
		/// screen the player is on. Only the session state does, so stale choices may not change the route.
		/// </summary>
		[Theory]
		[InlineData(HearthMirror.Enums.ArenaSessionState.MIDRUN)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.INVALID)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.NO_RUN)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.REWARDS)]
		public void A_non_pick_state_routes_the_same_way_whatever_is_left_in_the_choice_zone(
			HearthMirror.Enums.ArenaSessionState state)
		{
			Assert.Equal(DraftWatcher.RouteFor(state, 0), DraftWatcher.RouteFor(state, 3));
		}

		[Theory]
		[InlineData(HearthMirror.Enums.ArenaSessionState.DRAFTING)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.REDRAFTING)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.MIDRUN_REDRAFT_PENDING)]
		public void Three_choices_in_a_draft_state_are_a_pick(HearthMirror.Enums.ArenaSessionState state)
		{
			Assert.Equal(DraftWatcher.PollRoute.Pick, DraftWatcher.RouteFor(state, 3));
		}

		/// <summary>A draft state with nothing offered is not a pick — it is the gap between two of them.</summary>
		[Fact]
		public void A_draft_state_with_no_choices_is_not_a_pick()
		{
			Assert.Equal(DraftWatcher.PollRoute.RunOrNothing,
				DraftWatcher.RouteFor(HearthMirror.Enums.ArenaSessionState.DRAFTING, 0));
		}

		/// <summary>Only 0 or 3 is a real choice list; anything between is an animation mid-flight.</summary>
		[Theory]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(4)]
		public void A_partial_choice_list_is_not_a_pick(int choiceCount)
		{
			Assert.Equal(DraftWatcher.PollRoute.PartialChoices,
				DraftWatcher.RouteFor(HearthMirror.Enums.ArenaSessionState.DRAFTING, choiceCount));
		}

		/// <summary>The deck-edit phase owns the poll whatever the choice zone holds.</summary>
		[Theory]
		[InlineData(0)]
		[InlineData(3)]
		public void The_deck_edit_phase_wins_over_any_choices(int choiceCount)
		{
			Assert.Equal(DraftWatcher.PollRoute.DeckEdit,
				DraftWatcher.RouteFor(HearthMirror.Enums.ArenaSessionState.EDITING_DECK, choiceCount));
		}

		/// <summary>
		/// An unreadable deck: a pick cannot be scored without the class and the drafted cards it carries,
		/// so that retries — but with no choices there is still a panel to drop and a state to log, and
		/// neither needs the deck.
		/// </summary>
		[Fact]
		public void An_unreadable_deck_retries_a_pick_but_still_tears_the_run_panel_down()
		{
			Assert.Equal(DraftWatcher.PollRoute.Retry, DraftWatcher.RouteFor(null, 3));
			Assert.Equal(DraftWatcher.PollRoute.RunOrNothing, DraftWatcher.RouteFor(null, 0));
		}

		// ---- deck-review plan (the redraft "Edit Your Deck" phase) --------------------

		private static System.Collections.Generic.IReadOnlyList<(string Id, int Count)> Pairs(
			params (string, int)[] cards) => cards;

		/// <summary>The same, from named cards — the string overload is for unresolvable ids only.</summary>
		private static System.Collections.Generic.IReadOnlyList<(string Id, int Count)> Pairs(
			params (CardEntry Card, int Count)[] cards)
			=> System.Linq.Enumerable.ToList(
				System.Linq.Enumerable.Select(cards, c => (c.Card.CardId, c.Count)));

		[Fact]
		public void Deck_edit_plan_takes_the_discard_count_from_the_redraft_cards()
		{
			// Verified on a live client: the deck reads 30/30 for the whole phase with the new cards
			// already inside it, so "deckSize - 30" is zero and cannot drive this. The number to cut
			// is the number that arrived.
			var plan = DraftWatcher.BuildDeckEditPlan(Pairs((Yeti, 30)), Pairs((Raptor, 5)));

			Assert.Equal(5, plan.Over);
			Assert.Equal(7, plan.Suggest); // the five to cut, plus a margin to choose from
		}

		[Fact]
		public void Deck_edit_plan_stays_up_while_the_phase_lasts()
		{
			// The client exposes no discard progress — the deck is 30/30 before and after selecting
			// cards — so the plan must NOT try to count down. An earlier version expected the deck to
			// shrink; when it did not, the panel stayed on screen for the rest of the run.
			var before = DraftWatcher.BuildDeckEditPlan(Pairs((Yeti, 30)), Pairs((Raptor, 5)));
			var after = DraftWatcher.BuildDeckEditPlan(Pairs((Yeti, 30)), Pairs((Raptor, 5)));

			Assert.Equal(before.Over, after.Over);
			Assert.True(after.Over > 0, "the panel must remain valid for the whole phase");
		}

		[Fact]
		public void Deck_edit_plan_falls_back_to_an_oversized_deck()
		{
			// If a future client build DOES report an oversized deck instead of a redraft list, the
			// count still comes out right rather than the panel vanishing.
			var plan = DraftWatcher.BuildDeckEditPlan(Pairs((Yeti, 30), (Raptor, 5)), null);

			Assert.Equal(35, plan.DeckSize);
			Assert.Equal(5, plan.Over);
		}

		[Fact]
		public void Deck_edit_plan_never_suggests_fewer_than_five()
		{
			var plan = DraftWatcher.BuildDeckEditPlan(Pairs((Yeti, 30)), Pairs((Raptor, 1)));
			Assert.Equal(1, plan.Over);
			Assert.Equal(5, plan.Suggest);
		}

		[Fact]
		public void A_discarded_card_leaves_the_plan_even_when_it_is_a_NEW_card()
		{
			// The live bug, reproduced. Discarding removes the card from the run deck but NOT from the
			// redraft list, which keeps reporting all five arriving cards for the whole phase. An
			// earlier version unioned the two lists, so a newly drafted card could never leave the
			// panel: the signature was identical before and after, no re-render fired, and the panel
			// kept ranking a card the player had already cut. Seen on a real client with Divine Toll
			// and Ivory Knight — always one of the new cards, which is exactly the overlap.
			var newCard = Murloc.DbfId;
			var redraft = Pairs((Murloc, 1), (Raptor, 4));

			var before = DraftWatcher.BuildDeckEditPlan(
				Pairs((Yeti, 29), (Murloc, 1)), redraft);
			var afterDiscard = DraftWatcher.BuildDeckEditPlan(Pairs((Yeti, 29)), redraft);

			Assert.True(before.ByDbf.ContainsKey(newCard));
			Assert.False(afterDiscard.ByDbf.ContainsKey(newCard),
				"a discarded card must leave the plan, or the panel contradicts the deck on screen");
		}

		[Fact]
		public void The_redraft_list_is_a_FALLBACK_when_no_run_deck_is_exposed()
		{
			// Why the union existed at all: ranking nothing is worse than ranking the arriving cards.
			// So the redraft list still drives the plan when the client gives no run deck — it just no
			// longer overrides one that IS there.
			var plan = DraftWatcher.BuildDeckEditPlan(null, Pairs((Murloc, 1), (Raptor, 4)));

			Assert.Equal(2, plan.ByDbf.Count);
			Assert.Equal(1, plan.ByDbf[Murloc.DbfId]);
		}

		[Fact]
		public void The_run_deck_decides_the_copy_count_when_both_lists_disagree()
		{
			// The count only drives the "xN" label, never the ranking — but it must come from the deck
			// that is real, not from whichever list happens to say more.
			var plan = DraftWatcher.BuildDeckEditPlan(
				Pairs((Yeti, 2), (Raptor, 28)), Pairs((Yeti, 3)));

			Assert.Equal(2, plan.ByDbf.Count);
			Assert.Equal(2, plan.ByDbf[Yeti.DbfId]);
		}

		[Fact]
		public void Deck_edit_plan_ignores_unresolvable_cards_and_empty_input()
		{
			Assert.Empty(DraftWatcher.BuildDeckEditPlan(null, null).ByDbf);
			Assert.Empty(DraftWatcher.BuildDeckEditPlan(Pairs(("NOT_A_CARD", 3)), null).ByDbf);
			// A zero/negative count still means one copy present, not a vanished card.
			Assert.Equal(1, DraftWatcher.BuildDeckEditPlan(Pairs((Yeti, 0)), null)
				.ByDbf[Yeti.DbfId]);
		}
	}
}
