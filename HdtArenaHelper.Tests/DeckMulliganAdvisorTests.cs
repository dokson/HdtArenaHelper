using System.Collections.Generic;
using System.Linq;
using HearthDb;
using HearthDb.Enums;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The mulligan advisor. What is pinned here is the DIRECTION of each rule and the conditions
	/// under which it must stay silent — never a specific verdict string and never a threshold,
	/// for the same reason the synergy tests avoid magic numbers: the rules may be retuned, but a
	/// rule that fires on a deck fact which is not true must never ship.
	///
	/// Every case is built from a deck, because that is the entire premise: the same card is a keep
	/// in one deck and a toss in another, and a test that did not vary the deck would be testing a
	/// tier list.
	/// </summary>
	public class DeckMulliganAdvisorTests
	{
		private static readonly DeckMulliganAdvisor Advisor = new DeckMulliganAdvisor();

		private static int Dbf(string cardId) => Cards.All[cardId].DbfId;

		/// <summary>A deck of `count` copies of one card, padded to a believable arena deck size.</summary>
		private static List<int> DeckOf(params (string CardId, int Count)[] cards)
			=> cards.SelectMany(c => Enumerable.Repeat(Dbf(c.CardId), c.Count)).ToList();

		// Real cards, so the rules run against real metadata rather than a fixture that agrees
		// with them by construction.
		private const string TwoDropBody = "CS2_182";   // Chillwind Yeti, 4-mana body used as filler
		// 2 mana 3/2: on curve AND able to trade. A 1-attack body is deliberately not a "cheap
		// play" any more (see MinContestingAttack), so a 1/1 would test the wrong thing.
		private const string CheapBody = "CS2_172";     // Bloodfen Raptor
		// Vanilla on purpose: a 3-drop whose text could trip the removal rule would pass the coin
		// test for the wrong reason (Ironforge Rifleman's "Deal 1 damage" did exactly that).
		private const string ThreeDropBody = "GVG_044";  // Spider Tank, 3 mana, no text
		private const string Removal = "CS2_029";       // Fireball
		private const string BigBody = "NEW1_030";      // Deathwing, 10 mana
		private const string CheapSpell = "EX1_277";    // Arcane Missiles, 1 mana
		private const string CheapWeapon = "BAR_330";   // Tuskpiercer, 1 mana
		private const string CostlyWeapon = "AV_341";   // Cavalry Horn, 5 mana

		[Fact]
		public void Nothing_is_said_without_a_deck_to_judge_against()
		{
			// The whole method is "this card, against these 30" — with no deck there is no claim to
			// make, and a generic one would be the tier list this deliberately is not.
			var verdicts = Advisor.Evaluate(new[] { Dbf(CheapBody) }, new List<int>(),
				CardClass.PALADIN, onCoin: false);
			Assert.Empty(verdicts);
		}

		[Fact]
		public void An_unresolvable_card_voids_the_whole_hand()
		{
			// Verdicts are laid out by index: dropping one would move every later verdict onto its
			// neighbour, which is worse than showing nothing.
			var verdicts = Advisor.Evaluate(new[] { Dbf(CheapBody), 999999 },
				DeckOf((TwoDropBody, 30)), CardClass.PALADIN, onCoin: false);
			Assert.Empty(verdicts);
		}

		[Fact]
		public void One_verdict_per_card_in_hand_order()
		{
			var hand = new[] { Dbf(CheapBody), Dbf(Removal), Dbf(BigBody) };
			var verdicts = Advisor.Evaluate(hand, DeckOf((TwoDropBody, 30)),
				CardClass.MAGE, onCoin: false);
			Assert.Equal(hand.Length, verdicts.Count);
		}

		[Fact]
		public void A_cheap_body_is_kept_when_the_deck_has_almost_no_early_game()
		{
			// The rule that changes most decisions: with nothing early in the deck, the early play
			// in hand is the only one the game will offer.
			var verdicts = Advisor.Evaluate(new[] { Dbf(CheapBody) },
				DeckOf((BigBody, 25), (CheapBody, 1)), CardClass.PALADIN, onCoin: false);
			Assert.Equal(MulliganVerdict.Keep, verdicts[0].Verdict);
			Assert.NotNull(verdicts[0].Reason);
		}

		[Fact]
		public void The_deck_decides_whether_a_second_copy_is_still_a_keep()
		{
			// The comparative case, and the point of the whole design: identical cards, identical
			// hand, opposite decks. Holding two 2-drops is two early plays when the deck has none
			// left to give, and one early play plus a spare when it is full of them.
			var hand = new[] { Dbf(CheapBody), Dbf(CheapBody) };
			var scarce = Advisor.Evaluate(hand, DeckOf((BigBody, 28), (CheapBody, 2)),
				CardClass.PALADIN, onCoin: false);
			var plentiful = Advisor.Evaluate(hand, DeckOf((CheapBody, 30)),
				CardClass.PALADIN, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, scarce[1].Verdict);
			Assert.NotEqual(MulliganVerdict.Keep, plentiful[1].Verdict);
			// The FIRST copy is untouched either way — a rule that downgraded both would empty
			// the hand of the very plays it is trying to protect.
			Assert.Equal(MulliganVerdict.Keep, plentiful[0].Verdict);
		}

		[Fact]
		public void Scarce_removal_is_NOT_a_keep()
		{
			// The rule most likely to be re-proposed, pinned as absent on purpose. "Removal is
			// scarce in arena, so hold it" is intuitive and wrong at the mulligan: an opening hand
			// has no target, and this is a card to draw into rather than keep from turn zero.
			var verdicts = Advisor.Evaluate(new[] { Dbf(Removal) },
				DeckOf((TwoDropBody, 29), (Removal, 1)), CardClass.MAGE, onCoin: false);
			Assert.NotEqual(MulliganVerdict.Keep, verdicts[0].Verdict);
		}

		[Fact]
		public void A_cheap_spell_is_not_a_cheap_play()
		{
			// Removal, counters and reach are cards to draw INTO once a target exists; held from
			// turn zero they answer a board that is not there. Only a permanent buys the turn.
			var verdicts = Advisor.Evaluate(new[] { Dbf(CheapSpell) },
				DeckOf((TwoDropBody, 30)), CardClass.MAGE, onCoin: false);
			Assert.NotEqual(MulliganVerdict.Keep, verdicts[0].Verdict);
		}

		[Fact]
		public void A_cheap_weapon_is_kept_and_an_expensive_one_is_not()
		{
			// Weapons contest the board without spending a body, which is why the cheap ones are
			// near-automatic keeps — and why this must be checked by TYPE: most weapons have no
			// text at all, so a text-driven rule reads them as doing nothing.
			var deck = DeckOf((TwoDropBody, 30));
			var cheap = Advisor.Evaluate(new[] { Dbf(CheapWeapon) }, deck, CardClass.ROGUE, onCoin: false);
			var pricey = Advisor.Evaluate(new[] { Dbf(CostlyWeapon) }, deck, CardClass.ROGUE, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, cheap[0].Verdict);
			Assert.NotEqual(MulliganVerdict.Keep, pricey[0].Verdict);
		}

		[Fact]
		public void The_top_end_goes_back_unless_the_card_is_a_bomb()
		{
			// Tempo cuts both ways: a card that cannot be cast for five turns is one you would
			// rather draw later — unless it is the card that wins the game, which is why the
			// verdict has to be able to read a quality score and not only a mana cost.
			var hand = new[] { Dbf(BigBody) };
			var deck = DeckOf((TwoDropBody, 30));

			var mediocre = new DeckMulliganAdvisor();
			mediocre.SetScoreSource(_ => 40.0);
			var plain = mediocre.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false);
			Assert.Equal(MulliganVerdict.Toss, plain[0].Verdict);

			// With no score at all the same card gets no verdict: an unmeasured expensive card is
			// as likely to be the reason you win as it is to be filler, and we cannot tell.
			var blind = Advisor.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false);
			Assert.Equal(MulliganVerdict.Situational, blind[0].Verdict);

			var bomb = new DeckMulliganAdvisor();
			bomb.SetScoreSource(_ => 95.0);
			var kept = bomb.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false);
			Assert.NotEqual(MulliganVerdict.Toss, kept[0].Verdict);
		}

		[Fact]
		public void A_below_average_cheap_card_is_not_a_keep()
		{
			// Tempo is necessary, not sufficient: a cheap card that loses the turn back is not
			// worth the slot, and the arena score is what tells the two apart.
			var weak = new DeckMulliganAdvisor();
			weak.SetScoreSource(_ => 20.0);
			var verdicts = weak.Evaluate(new[] { Dbf(CheapBody) }, DeckOf((BigBody, 30)),
				CardClass.PALADIN, onCoin: false);

			Assert.NotEqual(MulliganVerdict.Keep, verdicts[0].Verdict);
		}

		[Fact]
		public void The_coin_changes_the_advice_for_the_same_hand_and_deck()
		{
			// The Coin moves the curve a turn, so it must be able to change a verdict: a 3-drop
			// lands on turn 2 with it, which is what makes it an early play in a deck that has
			// none. A version that ignored the Coin would be advising a different game.
			var hand = new[] { Dbf(ThreeDropBody) };
			var deck = DeckOf((BigBody, 29), (ThreeDropBody, 1));

			var first = Advisor.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false);
			var second = Advisor.Evaluate(hand, deck, CardClass.PALADIN, onCoin: true);

			Assert.NotEqual(MulliganVerdict.Keep, first[0].Verdict);
			Assert.Equal(MulliganVerdict.Keep, second[0].Verdict);
		}

		[Fact]
		public void Situational_is_the_default_rather_than_a_forced_call()
		{
			// A middling card in a balanced deck must produce no verdict at all. This is the test
			// that keeps the advisor honest: three confident calls on every hand would make the one
			// that matters invisible.
			var verdicts = Advisor.Evaluate(new[] { Dbf(TwoDropBody) },
				DeckOf((TwoDropBody, 10), (CheapBody, 10), (Removal, 10)),
				CardClass.MAGE, onCoin: false);
			Assert.Equal(MulliganVerdict.Situational, verdicts[0].Verdict);
			Assert.Null(verdicts[0].Reason);
		}
	}
}
