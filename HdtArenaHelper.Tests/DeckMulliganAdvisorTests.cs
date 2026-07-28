using System.Collections.Generic;
using System.Linq;
using HdtArenaHelper.CardDatabase;
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

		// The advisor is called with dbf ids, so a fixture has to hand them over somewhere. This is
		// the only place that happens: the cards themselves are named, never numbered.
		private static int Dbf(CardEntry card) => card.DbfId;

		// The advisor reads the opponent's hero power as a HearthDb card (it classifies the printed
		// text), so the named pool supplies the identity and HearthDb the card.
		private static Card HeroPower(CardEntry card) => Cards.All[card.CardId];

		/// <summary>A deck of `count` copies of one card, padded to a believable arena deck size.</summary>
		private static List<int> DeckOf(params (CardEntry Card, int Count)[] cards)
			=> cards.SelectMany(c => Enumerable.Repeat(c.Card.DbfId, c.Count)).ToList();

		// Real cards, so the rules run against real metadata rather than a fixture that agrees with
		// them by construction — named through the generated pool, so no id appears here and the
		// comments only have to explain the ROLE, which is the part a reader cannot look up.

		// A 4-mana body used as filler.
		private static readonly CardEntry TwoDropBody = HSCard.ChillwindYeti;
		// 2 mana 3/2: on curve AND able to trade, so it exercises the keep rules with nothing else
		// interfering. This comment used to claim a 1-attack body was excluded by a
		// `MinContestingAttack` rule — no such rule exists anywhere in the source, and a 1/1 IS
		// treated as a cheap play today. Left as a note rather than deleted, because a comment
		// describing a rule that is not there is how the wrong fixture goes unnoticed.
		private static readonly CardEntry CheapBody = HSCard.BloodfenRaptor;
		// Vanilla on purpose: a 3-drop whose text could trip the removal rule would pass the coin
		// test for the wrong reason (Ironforge Rifleman's "Deal 1 damage" did exactly that).
		private static readonly CardEntry ThreeDropBody = HSCard.SpiderTank;
		private static readonly CardEntry Removal = HSCard.Fireball;
		private static readonly CardEntry BigBody = HSCard.Deathwing;
		private static readonly CardEntry CheapSpell = HSCard.ArcaneMissiles;
		private static readonly CardEntry CheapWeapon = HSCard.Tuskpiercer;
		private static readonly CardEntry CostlyWeapon = HSCard.CavalryHorn;
		// 2 mana 2/1: the same slot as CheapBody, but demoted by the one-health rule. Needed to tell
		// "an earlier card of this cost" apart from "an earlier card that was actually an early play".
		// It must be a body with NO deathrattle payoff: Loot Hoarder was used here and stopped working
		// as a control once cards that replace themselves on death became exempt — it cycles, so the
		// hero power that kills it costs the opponent a turn and gains them nothing.
		private static readonly CardEntry FragileTwoDrop = HSCard.BluegillWarrior;
		// 2 mana 2/1 whose Deathrattle draws: the ping trades their turn for your card.
		private static readonly CardEntry CyclingOneHealth = HSCard.LootHoarder;
		// 6 mana 3/5, Tradeable AND upgrades when traded ("Trade to upgrade!").
		private static readonly CardEntry UpgradingTopEnd = HSCard.WindUpEnforcer;
		// 6 mana, Tradeable but with NO trade upside: trading it only cycles. The control that keeps
		// the rule from reading as "keep every expensive Tradeable card".
		private static readonly CardEntry PlainTradeableTopEnd = HSCard.BestInShell_CORE;
		// 2 mana 1/1 whose Battlecry lands a second body. The statline says "dies to a hero power",
		// the card says "two minions for two mana", and only one of those is the play.
		private static readonly CardEntry OneHealthSummoner = HSCard.MazeGuide_CORE;

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
		public void A_slot_is_only_taken_by_an_earlier_card_that_was_itself_a_keep()
		{
			// Sharing a cost is not sharing a slot. Live on a real client this emptied a hand: a
			// 2-mana location wanting a board that did not exist yet was demoted, then still counted
			// as the 2-drop, so the hand's ONLY early play came back as a spare copy of nothing.
			var hand = new[] { Dbf(FragileTwoDrop), Dbf(CheapBody) };
			var verdicts = Advisor.Evaluate(hand, DeckOf((CheapBody, 30)),
				CardClass.PALADIN, onCoin: false);

			Assert.NotEqual(MulliganVerdict.Keep, verdicts[0].Verdict);
			Assert.Equal(MulliganVerdict.Keep, verdicts[1].Verdict);
		}

		[Fact]
		public void A_one_health_body_that_brings_a_second_body_is_still_a_keep()
		{
			// The one-health rule reads the printed statline, and for a card whose Battlecry summons
			// another minion the statline is not the play: the hero power that eats the 1/1 leaves
			// the other body standing. Pinned against a plain 1-health minion so the test measures
			// the exemption rather than the rule being gone.
			var deck = DeckOf((CheapBody, 30));
			var summoner = Advisor.Evaluate(new[] { Dbf(OneHealthSummoner) }, deck,
				CardClass.PALADIN, onCoin: false);
			var fragile = Advisor.Evaluate(new[] { Dbf(FragileTwoDrop) }, deck,
				CardClass.PALADIN, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, summoner[0].Verdict);
			Assert.NotEqual(MulliganVerdict.Keep, fragile[0].Verdict);
		}

		[Fact]
		public void A_one_health_body_that_CYCLES_ITSELF_is_still_a_keep()
		{
			// "Dies for free" is a claim about the OPPONENT's side of the trade, and it is false when
			// the ping hands you a card: Loot Hoarder draws on death, so removing it early costs them
			// a turn of hero power and gains them nothing. Same for Sinful Sous Chef, which is where
			// this was spotted live — the overlay printed "1 health, dies for free" over a card whose
			// whole point is that dying is fine. Pinned against a plain 1-health body so the test
			// measures the exemption and not the disappearance of the rule.
			var deck = DeckOf((CheapBody, 30));
			var cycles = Advisor.Evaluate(new[] { Dbf(CyclingOneHealth) }, deck,
				CardClass.PALADIN, onCoin: false);
			var fragile = Advisor.Evaluate(new[] { Dbf(FragileTwoDrop) }, deck,
				CardClass.PALADIN, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, cycles[0].Verdict);
			Assert.NotEqual(MulliganVerdict.Keep, fragile[0].Verdict);
		}

		[Fact]
		public void A_top_end_card_that_UPGRADES_when_traded_is_not_simply_too_slow()
		{
			// A Tradeable card has a turn-1 use when nothing else does: 1 mana puts it back in the deck
			// and draws a replacement, buying value from a mana that was going to be wasted. So the
			// top-end rule must not send it back as flatly "too slow" — but the verdict stays
			// Situational, never Keep, because trading CYCLES rather than contesting the board.
			// Both cards get the SAME mediocre score, so the only thing varying is the keyword. A
			// score is required for the control: with none, the top-end rule abstains by design and
			// the comparison would prove nothing.
			var deck = DeckOf((CheapBody, 30));
			var advisor = new DeckMulliganAdvisor();
			advisor.SetScoreSource(_ => 40.0);
			var tradeable = advisor.Evaluate(new[] { Dbf(UpgradingTopEnd) }, deck,
				CardClass.PALADIN, onCoin: false);
			var plain = advisor.Evaluate(new[] { Dbf(BigBody) }, deck, CardClass.PALADIN, onCoin: false);

			// The control that matters most: a card that is Tradeable but gains NOTHING by trading is
			// still a toss. Trading only cycles, and cycling an expensive card you did not want is
			// worse than the free replacement the mulligan already gives you. Without this assertion
			// the rule would read as "keep every expensive Tradeable card", which is not the claim.
			var cyclesOnly = advisor.Evaluate(new[] { Dbf(PlainTradeableTopEnd) }, deck,
				CardClass.PALADIN, onCoin: false);

			Assert.Equal(MulliganVerdict.Situational, tradeable[0].Verdict);
			Assert.Equal(MulliganVerdict.Toss, plain[0].Verdict);
			Assert.Equal(MulliganVerdict.Toss, cyclesOnly[0].Verdict);
			Assert.Contains("traded", tradeable[0].Reason ?? "");
		}

		[Fact]
		public void A_discount_aimed_at_ANOTHER_card_does_not_exempt_this_one()
		{
			// Found live: Alter Time is "Discover two Arcane spells from the past. They cost (2) less."
			// The discount is on what it FINDS, but the bare pattern read it as self-discounting and
			// exempted it from every top-end rule, so a 4-mana spell sitting behind a 3-drop in hand
			// came back with no verdict at all. A pronoun subject always points at another card.
			// Pinned against a genuinely self-discounting card so the test measures the distinction
			// rather than the exemption having been deleted.
			var advisor = new DeckMulliganAdvisor();
			advisor.SetScoreSource(_ => 40.0);
			var deck = DeckOf((CheapBody, 30));

			// Alter Time behind a cheaper play: the discount is not its own, so it goes back.
			var other = advisor.Evaluate(new[] { Dbf(ThreeDropBody), Dbf(HSCard.AlterTime) }, deck,
				CardClass.MAGE, onCoin: false);
			// Knight of the Wild really does reduce its OWN cost ("Costs (1) less for each Beast you've
			// summoned"), so it stays exempt — the printed 7 is not the cost you pay.
			var own = advisor.Evaluate(new[] { Dbf(ThreeDropBody), Dbf(HSCard.KnightOfTheWild) }, deck,
				CardClass.MAGE, onCoin: false);

			Assert.Equal(MulliganVerdict.Toss, other[1].Verdict);
			Assert.NotEqual(MulliganVerdict.Toss, own[1].Verdict);
		}

		[Fact]
		public void The_one_health_demotion_depends_on_the_OPPONENTS_hero_power()
		{
			// The comparative case, and the reason the rule was made conditional at all: the SAME 2/1,
			// the SAME deck, opposite opponents. Only Mage's Fireblast and the Death Knight's Charge
			// Ghoul kill a one-health body for nothing; a Warrior gaining armour answers it not at all,
			// so keeping the body is right there and wrong against the Mage. Keyed on the hero power
			// CARD, not the class, because a dual-class arena hero does not identify it.
			var hand = new[] { Dbf(FragileTwoDrop) };
			var deck = DeckOf((CheapBody, 30));

			var vsMage = Advisor.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false,
				HeroPower(HSHeroPower.Fireblast));
			var vsWarrior = Advisor.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false,
				HeroPower(HSHeroPower.ArmorUp));

			Assert.NotEqual(MulliganVerdict.Keep, vsMage[0].Verdict);
			Assert.Equal(MulliganVerdict.Keep, vsWarrior[0].Verdict);
			Assert.Contains("Fireblast", vsMage[0].Reason ?? "");
		}

		[Fact]
		public void A_hero_power_that_must_SWING_does_not_demote_the_body()
		{
			// Druid, Demon Hunter and Rogue can kill a one-health body, but only by swinging the hero
			// into it and eating its attack — the distinction that makes a 3/1 and a 2/1 different
			// cards to hold. Demon Claws is the hero power read from a real match while building this.
			var verdicts = Advisor.Evaluate(new[] { Dbf(FragileTwoDrop) }, DeckOf((CheapBody, 30)),
				CardClass.PALADIN, onCoin: false, HeroPower(HSHeroPower.DemonClaws));

			Assert.Equal(MulliganVerdict.Keep, verdicts[0].Verdict);
		}

		[Fact]
		public void An_UNKNOWN_hero_power_keeps_the_old_demotion()
		{
			// The fail-safe direction, pinned because it is a decision and not an accident: with no
			// hero power to read, the advice stays exactly what it was. Relaxing the rule on missing
			// data would tell a player to keep a fragile body against an opponent we merely failed to
			// read, and that error costs the board while the conservative one costs nothing.
			var verdicts = Advisor.Evaluate(new[] { Dbf(FragileTwoDrop) }, DeckOf((CheapBody, 30)),
				CardClass.PALADIN, onCoin: false, opponentHeroPower: null);

			Assert.NotEqual(MulliganVerdict.Keep, verdicts[0].Verdict);
			Assert.Equal("1 health, dies for free", verdicts[0].Reason);
		}

		[Fact]
		public void An_upgrading_card_goes_back_once_the_hand_HAS_a_turn_one_play()
		{
			// The other half, and the one that keeps the rule honest: the trade is only worth
			// something because it uses a mana you were going to waste. Hand the player an actual
			// 1-drop and the two compete for that same mana — now the expensive card goes back, since
			// a mulligan is free while a trade costs tempo. Same card, same deck, same score: only the
			// rest of the hand changes.
			var deck = DeckOf((CheapBody, 30));
			var advisor = new DeckMulliganAdvisor();
			advisor.SetScoreSource(_ => 40.0);

			var alone = advisor.Evaluate(new[] { Dbf(UpgradingTopEnd) }, deck,
				CardClass.PALADIN, onCoin: false);
			var withOneDrop = advisor.Evaluate(new[] { Dbf(UpgradingTopEnd), Dbf(CheapSpell) }, deck,
				CardClass.PALADIN, onCoin: false);

			Assert.Equal(MulliganVerdict.Situational, alone[0].Verdict);
			Assert.Equal(MulliganVerdict.Toss, withOneDrop[0].Verdict);
		}

		[Fact]
		public void A_three_drop_is_early_only_in_a_deck_that_cannot_curve_out()
		{
			// "Early" is a property of the deck, not of the mana cost. The comparative is the whole
			// point: identical card and hand, and the verdict flips on what the other 29 cards can
			// offer before turn 3. A flat "keep threes" rule would fire on both.
			var hand = new[] { Dbf(ThreeDropBody) };
			var topHeavy = Advisor.Evaluate(hand, DeckOf((BigBody, 29), (ThreeDropBody, 1)),
				CardClass.PALADIN, onCoin: false);
			var curvesOut = Advisor.Evaluate(hand, DeckOf((CheapBody, 20), (BigBody, 10)),
				CardClass.PALADIN, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, topHeavy[0].Verdict);
			Assert.NotEqual(MulliganVerdict.Keep, curvesOut[0].Verdict);
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
		public void DIVINE_SHIELD_answers_the_one_health_rule_before_it_is_asked()
		{
			// Seen live: Hardlight Protector, a 2/1 Mech WITH Divine Shield, was told "1 health, dies
			// to Ghoul Charge". It does not — the shield eats the first instance of damage whatever
			// its size, so the ping bounces and the Charge Ghoul trades itself for the shield. The
			// control is the same hero power against a plain 2/1, which really does die for free.
			var deck = DeckOf((CheapBody, 30));
			var ghoul = HeroPower(HSHeroPower.GhoulCharge);

			var shielded = Advisor.Evaluate(new[] { Dbf(HSCard.HardlightProtector) }, deck,
				CardClass.PALADIN, onCoin: false, ghoul);
			var bare = Advisor.Evaluate(new[] { Dbf(FragileTwoDrop) }, deck,
				CardClass.PALADIN, onCoin: false, ghoul);

			Assert.Equal(MulliganVerdict.Keep, shielded[0].Verdict);
			Assert.NotEqual(MulliganVerdict.Keep, bare[0].Verdict);
			Assert.Contains("1 health", bare[0].Reason ?? "");

			// Argent Squire is the canonical case — a 1-mana 1/1 that every player keeps precisely
			// because the shield makes its one health irrelevant. Pinned against the Mage ping too,
			// which is the other hero power that kills a bare one-health body for free.
			var squire = Advisor.Evaluate(new[] { Dbf(HSCard.ArgentSquire) }, deck,
				CardClass.PALADIN, onCoin: false, HeroPower(HSHeroPower.Fireblast));

			Assert.Equal(MulliganVerdict.Keep, squire[0].Verdict);
		}

		[Fact]
		public void A_top_end_card_that_UPGRADES_WHILE_HELD_is_not_simply_too_slow()
		{
			// Same shape as the trade-upside rule: holding it IS the plan, so the printed cost is not
			// the whole story. Infuse says it as a keyword; 13 Infuse cards cost 5 or more, which is
			// the only place this can fire. A score is set so the control is the top-end rule firing
			// rather than abstaining for want of data.
			var advisor = new DeckMulliganAdvisor();
			advisor.SetScoreSource(_ => 40.0);
			var deck = DeckOf((CheapBody, 30));

			var infused = advisor.Evaluate(new[] { Dbf(HSCard.StonebornAccuser) }, deck,
				CardClass.DEATHKNIGHT, onCoin: false);
			var plain = advisor.Evaluate(new[] { Dbf(BigBody) }, deck, CardClass.PALADIN, onCoin: false);

			Assert.NotEqual(MulliganVerdict.Toss, infused[0].Verdict);
			Assert.Contains("hold", infused[0].Reason ?? "");
			Assert.Equal(MulliganVerdict.Toss, plain[0].Verdict);
		}

		[Fact]
		public void A_card_that_upgrades_in_hand_OR_DECK_is_no_reason_to_hold_it()
		{
			// The veto, and the distinction the rule turns on. Lotus Troublemaker's counter ticks
			// "while in hand or deck", so keeping it buys nothing — it is written precisely so you do
			// not have to. Pinned at the text level because the two families read almost identically.
			Assert.True(DeckMulliganAdvisor.UpgradesWhileHeld("taunt infuse (3): gain +2/+2."));
			Assert.True(DeckMulliganAdvisor.UpgradesWhileHeld(
				"whenever a friendly minion dies while this is in your hand, gain +1/+1."));
			// The tooltip-wrap case, which is why the pattern uses \s+.
			Assert.True(DeckMulliganAdvisor.UpgradesWhileHeld("costs (1) less while this is\nin your hand."));

			Assert.False(DeckMulliganAdvisor.UpgradesWhileHeld(
				"shoot 1 time! (while in hand or deck play cards for 2 mana to shoot more!)"));
			Assert.False(DeckMulliganAdvisor.UpgradesWhileHeld("deal 3 damage to a minion."));
		}

		[Fact]
		public void A_cheap_spell_that_SUMMONS_is_an_early_play_like_a_body()
		{
			// Seen live: Mining Casualties ("Summon two 1/1 Silver Hand Recruits...") got no verdict
			// at all, because the early-keep rule asked what the card IS — minion, weapon, location —
			// rather than what it does on turn two. Two 1/1s on turn two is a two-drop however the
			// card is typed. The rule against cheap spells still stands for the cards it was written
			// for: removal and reach answer a board that does not exist yet.
			var deck = DeckOf((CheapBody, 30));
			var summons = Advisor.Evaluate(new[] { Dbf(HSCard.MiningCasualties) }, deck,
				CardClass.DEATHKNIGHT, onCoin: false);
			var removal = Advisor.Evaluate(new[] { Dbf(HSCard.ArcaneMissiles) }, deck,
				CardClass.MAGE, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, summons[0].Verdict);
			Assert.NotEqual(MulliganVerdict.Keep, removal[0].Verdict);
		}

		[Fact]
		public void The_summon_rule_reads_only_what_THIS_card_does()
		{
			// Two guards, both found by running the rule over the pool rather than by reasoning.
			// Quoted text belongs to the TOKEN: Mining Casualties' recruits carry a Deathrattle, and
			// reading it as the spell's own condition rejected the card the rule exists for.
			Assert.True(DeckMulliganAdvisor.DevelopsBoard(
				"summon two 1/1 silver hand recruits with \"deathrattle: summon a 1/1 frail ghoul\"."));
			// A summon that waits for a trigger is not a turn-two play.
			Assert.False(DeckMulliganAdvisor.DevelopsBoard(
				"deathrattle: summon two 1/1 recruits."));
			// Nor is the opponent's.
			Assert.False(DeckMulliganAdvisor.DevelopsBoard("your opponent summons a 2/2."));
			// A quest's text states what you must DO, and reads exactly like a summon — Jungle Giants,
			// Unite the Murlocs and Unseal the Vault all matched until the QUEST tag was checked at
			// the call site, which is why that guard is a tag and not a word.
		}

		[Fact]
		public void A_card_paid_for_in_HEALTH_gets_no_verdict()
		{
			// Blood Draw reads 3 mana and is castable on turn 1 for three life, so every tempo rule
			// here — all of them built on the printed cost — would be answering the wrong question.
			// The same abstention hero cards get, for the same reason. Silence rather than a guess:
			// weighing life against tempo needs data this advisor does not have.
			var verdicts = Advisor.Evaluate(new[] { Dbf(HSCard.BloodDraw) },
				DeckOf((CheapBody, 30)), CardClass.DEATHKNIGHT, onCoin: false);

			Assert.Equal(MulliganVerdict.Situational, verdicts[0].Verdict);
			Assert.Null(verdicts[0].Reason);

			// Pinned at the text level too, so the wording is the rule rather than one card.
			Assert.True(DeckMulliganAdvisor.PaysWithHealth(
				"discover a spell. this costs health instead of mana."));
			// The tooltip-wrap case, which is why the pattern uses \s+.
			Assert.True(DeckMulliganAdvisor.PaysWithHealth("this costs health\ninstead of mana."));
			Assert.False(DeckMulliganAdvisor.PaysWithHealth("deal 3 damage to your hero. draw a card."));
		}

		[Fact]
		public void A_cheap_body_that_needs_a_WEAPON_is_not_a_turn_one_play()
		{
			// Seen live: Air Guitarist ("Battlecry: Give your weapon +1 Durability") came back as a
			// plain turn-1 keep. It is a 1/1 whose text does nothing until a weapon exists, and on
			// turn 1 none can — you cannot equip before your first turn, which makes this stricter
			// than the friendly-board case it sits beside. The dependency list named only minion
			// things, so "your weapon" fell straight through it.
			var verdicts = Advisor.Evaluate(new[] { Dbf(HSCard.AirGuitarist) },
				DeckOf((CheapBody, 30)), CardClass.ROGUE, onCoin: false);

			Assert.NotEqual(MulliganVerdict.Keep, verdicts[0].Verdict);
			Assert.Contains("weapon", verdicts[0].Reason ?? "");
		}

		[Fact]
		public void A_cheap_body_that_EQUIPS_its_own_weapon_still_plays_on_turn_one()
		{
			// The veto, and the trap it exists for: a card that supplies its own dependency does not
			// depend on you for it — the same lesson the synergy engine's generation veto is built
			// on. No card at this cost does both today, so this pins the rule rather than a card.
			Assert.False(DeckMulliganAdvisor.DependsOnAnEquippedWeapon(
				"battlecry: equip a 1/3 weapon. give your weapon +1 attack."));
			Assert.True(DeckMulliganAdvisor.DependsOnAnEquippedWeapon(
				"battlecry: give your weapon +1 durability."));
			// The tooltip-line-break case, which is why the pattern uses \s+ and not a space.
			Assert.True(DeckMulliganAdvisor.DependsOnAnEquippedWeapon("give your\nweapon +2 attack."));
		}

		[Fact]
		public void A_hand_with_no_play_before_turn_four_goes_back_whole()
		{
			// Reproduces a live hand: a Mage going first held two 4-drops and a 5-drop and got three
			// abstentions. Each card was defensible on its own — 4 is neither "early" nor "top end",
			// and the 5-drop had no win-rate so the top-end rule stayed silent — while the hand as a
			// whole could not do anything until turn 4, which is the one call a player does not need
			// help with and the plugin still owed them.
			var hand = new[] { Dbf(TwoDropBody), Dbf(TwoDropBody), Dbf(CostlyWeapon) };
			var verdicts = Advisor.Evaluate(hand, DeckOf((CheapBody, 30)), CardClass.MAGE, onCoin: false);

			Assert.All(verdicts, v => Assert.Equal(MulliganVerdict.Toss, v.Verdict));
			Assert.All(verdicts, v => Assert.Contains("turn 4", v.Reason ?? ""));
		}

		[Fact]
		public void One_cheap_card_is_enough_to_stop_the_hand_being_dead()
		{
			// The control, and the boundary the rule turns on: the rule reads the HAND, so a single
			// early play makes the same expensive cards ordinary again — judged on their own merits by
			// the rules below it, never tossed for the hand's shape.
			var hand = new[] { Dbf(CheapBody), Dbf(TwoDropBody), Dbf(TwoDropBody) };
			var verdicts = Advisor.Evaluate(hand, DeckOf((CheapBody, 30)), CardClass.MAGE, onCoin: false);

			Assert.DoesNotContain("turn 4", verdicts[1].Reason ?? "");
		}

		[Fact]
		public void The_Coin_can_make_the_same_hand_playable()
		{
			// Effective turns, not printed costs: on the coin a 4-drop lands on turn 3, so the hand
			// has an early play and the rule must not fire. The same three cards, one card different
			// in what they cost to play.
			var hand = new[] { Dbf(TwoDropBody), Dbf(TwoDropBody), Dbf(CostlyWeapon) };
			var deck = DeckOf((CheapBody, 30));

			var first = Advisor.Evaluate(hand, deck, CardClass.MAGE, onCoin: false);
			var coined = Advisor.Evaluate(hand, deck, CardClass.MAGE, onCoin: true);

			Assert.Contains("turn 4", first[0].Reason ?? "");
			Assert.DoesNotContain("turn 4", coined[0].Reason ?? "");
		}

		[Fact]
		public void The_top_end_goes_back_unless_the_card_is_a_bomb()
		{
			// Tempo cuts both ways: a card that cannot be cast for five turns is one you would
			// rather draw later — unless it is the card that wins the game, which is why the
			// verdict has to be able to read a quality score and not only a mana cost.
			// The hand holds a cheap play on purpose, so the card under test is judged by the top-end
			// rule and not by the dead-hand rule below it — a hand of nothing but expensive cards goes
			// back whole for a reason that needs no score, which is a different rule and its own test.
			var hand = new[] { Dbf(CheapBody), Dbf(BigBody) };
			var deck = DeckOf((TwoDropBody, 30));

			var mediocre = new DeckMulliganAdvisor();
			mediocre.SetScoreSource(_ => 40.0);
			var plain = mediocre.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false);
			Assert.Equal(MulliganVerdict.Toss, plain[1].Verdict);

			// With no score at all the same card gets no verdict: an unmeasured expensive card is
			// as likely to be the reason you win as it is to be filler, and we cannot tell.
			var blind = Advisor.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false);
			Assert.Equal(MulliganVerdict.Situational, blind[1].Verdict);

			var bomb = new DeckMulliganAdvisor();
			bomb.SetScoreSource(_ => 95.0);
			var kept = bomb.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false);
			Assert.NotEqual(MulliganVerdict.Toss, kept[1].Verdict);
		}

		[Fact]
		public void A_below_average_cheap_card_is_judged_against_THIS_DECKS_slot()
		{
			// The comparative case, and it replaces an absolute one. "Below the pool median" answers
			// the DRAFT's question; the mulligan asks whether the card is your best play on that turn
			// given the other 27 cards. Seen live on Wild Pyromancer — genuinely below average for a
			// Mage, and still the right keep in a deck with nothing better at two mana, because a
			// mediocre turn-2 play beats an empty turn 2.
			//
			// Same weak card, same score, opposite decks: only what the deck can offer in that slot
			// changes. The score source rates the card in hand 20 and everything else 80, so the
			// deck's own 2-drops are unambiguously better where they exist.
			var weakCard = Dbf(CheapBody);
			var advisor = new DeckMulliganAdvisor();
			advisor.SetScoreSource(id => id == weakCard ? 20.0 : 80.0);

			var slotCovered = advisor.Evaluate(new[] { weakCard },
				DeckOf((FragileTwoDrop, 2), (BigBody, 28)), CardClass.PALADIN, onCoin: false);
			var nothingBetter = advisor.Evaluate(new[] { weakCard }, DeckOf((BigBody, 30)),
				CardClass.PALADIN, onCoin: false);

			Assert.NotEqual(MulliganVerdict.Keep, slotCovered[0].Verdict);
			Assert.Equal(MulliganVerdict.Keep, nothingBetter[0].Verdict);
			// The count travels with the verdict, so the log says why rather than merely what.
			Assert.Contains("better at 2", slotCovered[0].Reason ?? "");
		}

		[Fact]
		public void ONE_better_card_in_the_deck_does_not_cover_the_slot()
		{
			// Two, not one: a single better card among thirty is one you will probably not have drawn
			// by the turn in question, so demoting the card in hand for it trades a play you hold for
			// a play you might see.
			var weakCard = Dbf(CheapBody);
			var advisor = new DeckMulliganAdvisor();
			advisor.SetScoreSource(id => id == weakCard ? 20.0 : 80.0);

			var one = advisor.Evaluate(new[] { weakCard },
				DeckOf((FragileTwoDrop, 1), (BigBody, 29)), CardClass.PALADIN, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, one[0].Verdict);
		}

		[Fact]
		public void The_coin_changes_the_advice_for_the_same_hand_and_deck()
		{
			// The Coin moves the curve a turn, so it must be able to change a verdict: a 4-drop
			// lands on turn 3 with it, which is what makes it an early play in a deck that has
			// none. A version that ignored the Coin would be advising a different game.
			// Measured at the 4-drop rather than the 3-drop since the early window now reaches
			// turn 3 by itself in a deck this top-heavy — the Coin has to move a card that the
			// deck alone does not already reach, or the test would pass without reading it.
			var hand = new[] { Dbf(TwoDropBody) };
			var deck = DeckOf((BigBody, 29), (TwoDropBody, 1));

			var first = Advisor.Evaluate(hand, deck, CardClass.PALADIN, onCoin: false);
			var second = Advisor.Evaluate(hand, deck, CardClass.PALADIN, onCoin: true);

			Assert.NotEqual(MulliganVerdict.Keep, first[0].Verdict);
			Assert.Equal(MulliganVerdict.Keep, second[0].Verdict);
		}

		[Fact]
		public void A_cheap_summon_SPELL_counts_as_one_of_the_decks_early_plays()
		{
			// The class had two notions of an early play and they disagreed: judging one card, a cheap
			// spell that summons IS an early play (the Mining Casualties rule), while counting the
			// DECK's early plays saw only minions, weapons and locations — so the same card existed on
			// one side of the class and not the other. Pinned because the two must mean the same thing,
			// NOT because a wrong verdict was demonstrated: the one live case that looked like one was
			// traced against the wrong deck state, and the widened window was justified there.
			//
			// The fixture is the shape that separates the two definitions: thin in bodies, rich in
			// cheap summon-spells. The
			// control is the same deck with the spells replaced by removal, which really does leave
			// nothing to play on turn two and so must still widen the window.
			var summonSpells = DeckOf((CheapBody, 5), (BigBody, 12), (HSCard.MiningCasualties, 13));
			var deadSpells = DeckOf((CheapBody, 5), (BigBody, 12), (CheapSpell, 13));
			var hand = new[] { Dbf(ThreeDropBody) };

			var curvesOut = Advisor.Evaluate(hand, summonSpells, CardClass.DEATHKNIGHT, onCoin: false);
			var thin = Advisor.Evaluate(hand, deadSpells, CardClass.DEATHKNIGHT, onCoin: false);

			Assert.NotEqual(MulliganVerdict.Keep, curvesOut[0].Verdict);
			Assert.Equal(MulliganVerdict.Keep, thin[0].Verdict);
			// And when the window IS the reason, the reason says so rather than calling a 3-drop cheap.
			Assert.Contains("turn 3", thin[0].Reason ?? "");
		}

		[Fact]
		public void An_OUTCAST_card_is_judged_by_WHERE_IN_THE_HAND_it_sits()
		{
			// The first positional rule here, and the asymmetry is the whole content: Outcast fires
			// only from the left- or right-most card in hand, and those two edges are not the same
			// thing. LEFTMOST is stable — nothing arrives to its left, so the Outcast you see is the
			// one you get. RIGHTMOST is not: a card always arrives on the right before turn 1 (your
			// own draw going first, the Coin going second), so its Outcast is already gone. The MIDDLE
			// is dead now and only wakes up once the cards left of it have been played or thrown away.
			//
			// The same card, the same deck, three positions: only the index changes.
			var deck = DeckOf((BigBody, 28), (TwoDropBody, 2));
			var sage = Dbf(HSCard.WaywardSage);
			var filler = Dbf(BigBody);

			var left = Advisor.Evaluate(new[] { sage, filler, filler }, deck,
				CardClass.DEMONHUNTER, onCoin: false);
			var middle = Advisor.Evaluate(new[] { filler, sage, filler }, deck,
				CardClass.DEMONHUNTER, onCoin: false);
			var right = Advisor.Evaluate(new[] { filler, filler, sage }, deck,
				CardClass.DEMONHUNTER, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, left[0].Verdict);
			Assert.Equal(MulliganVerdict.Situational, middle[1].Verdict);
			Assert.Equal(MulliganVerdict.Situational, right[2].Verdict);
			Assert.Contains("Outcast", middle[1].Reason ?? "");
			Assert.Contains("Outcast", right[2].Reason ?? "");
			// The two edges are told apart in words, since the reason is the whole product here: one
			// says the draw takes the edge, the other names the cards that have to move first.
			Assert.NotEqual(middle[1].Reason, right[2].Reason);

			// The control that makes this about OUTCAST rather than about position: a card with no
			// Outcast at all is unaffected wherever it sits.
			var plainRight = Advisor.Evaluate(new[] { filler, filler, Dbf(CheapBody) }, deck,
				CardClass.DEMONHUNTER, onCoin: false);
			Assert.Equal(MulliganVerdict.Keep, plainRight[2].Verdict);
		}

		[Fact]
		public void The_OUTCAST_rule_reads_the_TAG_and_not_the_word()
		{
			// The pool is full of cards that NAME Outcast without having one — Illidari Studies
			// discovers them, Line Hopper discounts them, Redeemed Pariah pays you for playing them,
			// Glaivetar's reminder line merely mentions them. Measured through the rule: 35 collectible
			// cards carry the tag and 13 more only say the word, so a text match would attach a
			// positional demotion to all 13. Line Hopper is a plain 3-mana 3/4 and must keep its
			// verdict on the right edge exactly as any other body would.
			var deck = DeckOf((BigBody, 29), (TwoDropBody, 1));
			var filler = Dbf(BigBody);
			var verdicts = Advisor.Evaluate(new[] { filler, filler, Dbf(HSCard.LineHopper) }, deck,
				CardClass.DEMONHUNTER, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, verdicts[2].Verdict);
			Assert.DoesNotContain("Outcast", verdicts[2].Reason ?? "");

			// Paired with a card that really does carry the tag, in the same seat: without it this
			// asserts only that nothing happens, which a missing rule satisfies just as well.
			var tagged = Advisor.Evaluate(new[] { filler, filler, Dbf(HSCard.WaywardSage) }, deck,
				CardClass.DEMONHUNTER, onCoin: false);
			Assert.Equal(MulliganVerdict.Situational, tagged[2].Verdict);
		}

		[Fact]
		public void A_dead_OUTCAST_can_only_DEMOTE_a_keep_and_never_rescue_a_toss()
		{
			// The rule is one-way by construction, and it has to be: it is self-referential —
			// mulliganing rearranges the very positions it reads — so it may state a fact and must
			// never act on one. Demoting a Keep to Situational asserts nothing about what to do, and
			// so leaves the hand it measured alone; a Toss or a promotion would not.
			//
			// Midnight Wolf is a 6-mana Outcast minion: the top-end rule tosses it on a mediocre score,
			// and sitting on the dead right edge is not a reason to hold it. Pinned so that a version
			// reaching for "Situational whenever Outcast is off" fails here.
			var advisor = new DeckMulliganAdvisor();
			advisor.SetScoreSource(_ => 40.0);
			var deck = DeckOf((CheapBody, 30));
			var verdicts = advisor.Evaluate(
				new[] { Dbf(CheapBody), Dbf(CheapBody), Dbf(HSCard.MidnightWolf) }, deck,
				CardClass.DEMONHUNTER, onCoin: false);

			Assert.Equal(MulliganVerdict.Toss, verdicts[2].Verdict);
		}

		[Fact]
		public void A_position_claim_is_not_made_about_a_list_that_is_not_a_HAND()
		{
			// Gated on a real opening hand, the same gate the dead-hand rule uses and for the same
			// reason: a one- or two-card list is a caller isolating a single card, and "this card is in
			// the middle of your hand" is not something that can be said about it.
			var deck = DeckOf((BigBody, 28), (TwoDropBody, 2));
			var verdicts = Advisor.Evaluate(new[] { Dbf(BigBody), Dbf(HSCard.WaywardSage) }, deck,
				CardClass.DEMONHUNTER, onCoin: false);

			Assert.Equal(MulliganVerdict.Keep, verdicts[1].Verdict);
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
