using HearthDb;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The board facts shown beside an in-game choice. They exist BECAUSE they need no fitting — the
	/// rules decide them — and the tests pin the two properties that keep them honest: the order of
	/// severity, and silence whenever the board cannot be read.
	/// </summary>
	public class GameStateFactsTests
	{
		// Chillwind Yeti (4-mana minion) and Flamestrike (7-mana spell): cost and type are all the
		// rules below look at.
		private static Card Minion4 => Cards.All["CS2_182"];
		private static Card Spell7 => Cards.All["CS2_032"];

		private static GameStateSnapshot Board(int mana, int hand = 5, int minions = 2)
			=> new GameStateSnapshot(mana, hand, maxHandSize: 10, friendlyMinions: minions,
				maxBoardSize: 7);

		[Fact]
		public void A_playable_card_on_an_open_board_says_nothing()
		{
			// Silence is the default: a note only appears when the board actually constrains the card.
			Assert.Null(GameStateFacts.Describe(Minion4, Board(mana: 4)));
		}

		[Fact]
		public void A_card_costing_more_than_your_mana_says_so_with_both_numbers()
		{
			// Both numbers, because "unplayable" alone hides how far off it is — one mana short on
			// turn 6 and five short on turn 2 are different decisions.
			var note = GameStateFacts.Describe(Spell7, Board(mana: 3));
			Assert.Equal("needs 7 mana, you have 3", note);
		}

		[Fact]
		public void A_full_board_is_reported_for_minions_and_not_for_spells()
		{
			var full = Board(mana: 10, minions: 7);
			Assert.Equal("board full — no room for a minion", GameStateFacts.Describe(Minion4, full));
			Assert.Null(GameStateFacts.Describe(Spell7, full));
		}

		[Fact]
		public void A_full_hand_outranks_every_other_fact()
		{
			// A card discovered into a full hand is destroyed, so it must win over "unplayable" and
			// "board full" — both of which merely delay the card.
			var hopeless = new GameStateSnapshot(availableMana: 0, handCount: 10, maxHandSize: 10,
				friendlyMinions: 7, maxBoardSize: 7);

			Assert.Equal("hand full — the card would be lost",
				GameStateFacts.Describe(Minion4, hopeless));
			Assert.Equal("hand full — the card would be lost",
				GameStateFacts.Describe(Spell7, hopeless));
		}

		[Fact]
		public void An_unreadable_board_says_nothing_at_all()
		{
			// The dangerous failure mode: an unread board reads as zero mana and would print
			// "needs 7 mana, you have 0" over a perfectly playable turn.
			Assert.Null(GameStateFacts.Describe(Spell7, GameStateSnapshot.Unknown));
			Assert.Null(GameStateFacts.Describe(null, Board(mana: 10)));
		}

		[Fact]
		public void Missing_limits_fall_back_to_the_games_own_rules()
		{
			// maxHandSize/maxBoardSize are 0 when HDT has not reported them yet; the fallback is
			// Hearthstone's own 10 and 7, not "no limit".
			var noLimits = new GameStateSnapshot(availableMana: 10, handCount: 10, maxHandSize: 0,
				friendlyMinions: 7, maxBoardSize: 0);

			Assert.Equal("hand full — the card would be lost",
				GameStateFacts.Describe(Minion4, noLimits));
			Assert.Equal("board full — no room for a minion", GameStateFacts.Describe(Minion4,
				new GameStateSnapshot(10, handCount: 3, maxHandSize: 0, friendlyMinions: 7, maxBoardSize: 0)));
		}
	}
}
