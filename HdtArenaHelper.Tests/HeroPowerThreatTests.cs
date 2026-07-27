using HdtArenaHelper.CardDatabase;
using HearthDb;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The hero power classifier. Every case is a REAL hero power card, because the whole point is
	/// that the answer is derived from printed text rather than from a hand-written class table — a
	/// fixture with invented text would be testing the regexes against themselves.
	///
	/// Classifications here were read off the whole pool (2138 hero powers) before being pinned, and
	/// two of them corrected a from-memory list: Paladin's Reinforce does NOT answer a body, and
	/// Death Knight's Ghoul does.
	/// </summary>
	public class HeroPowerThreatTests
	{
		// The classifier reads a HearthDb Card (it needs the entity's text), so the generated pool
		// supplies the identity and HearthDb the card. That is the only reason an id appears at all.
		private static Card Hp(CardEntry heroPower) => Cards.All[heroPower.CardId];

		[Fact]
		public void A_missing_hero_power_answers_nothing()
		{
			// The live read can come back empty (the entity may not exist yet), and that must be a
			// harmless "no information", never an exception or a claim.
			var (answer, free) = HeroPowerThreat.Classify(null);

			Assert.Equal(HeroPowerAnswer.None, answer);
			Assert.Equal(0, free);
			Assert.False(HeroPowerThreat.KillsForFree(null, 1));
		}

		[Fact]
		public void A_ping_kills_a_one_health_body_for_free()
		{
			// Fireblast: "Deal $1 damage". The cheapest answer in the game and the reason the
			// one-health rule exists at all.
			Assert.Equal(HeroPowerAnswer.DirectDamage, HeroPowerThreat.Classify(Hp(HSHeroPower.Fireblast)).Answer);
			Assert.True(HeroPowerThreat.KillsForFree(Hp(HSHeroPower.Fireblast), 1));
			// ...but only as far as its damage reaches: a 2-health body survives Fireblast.
			Assert.False(HeroPowerThreat.KillsForFree(Hp(HSHeroPower.Fireblast), 2));
			// The upgraded version reaches further, which is why the AMOUNT is read and not just the
			// presence of damage.
			Assert.True(HeroPowerThreat.KillsForFree(Hp(HSHeroPower.FireblastRank2), 2));
		}

		[Fact]
		public void Damage_restricted_to_the_enemy_HERO_answers_no_minion()
		{
			// Steady Shot and Ballista Shot hit the FACE only — confirmed by a player, and the data
			// alone could not say: HearthDb ships both cards' text twice, once face-restricted and once
			// as a bare "Deal $N damage.". An earlier version let the bare clause decide and put Hunter
			// among the classes that ping a minion, which would have told a player to mulligan a 2/1
			// that was actually safe. Reconciled by the repeated "Hero Power" label, which is how
			// HearthDb marks two renderings of one card.
			foreach(var power in new[] { HSHeroPower.SteadyShot, HSHeroPower.BallistaShot })
			{
				Assert.Equal(HeroPowerAnswer.None, HeroPowerThreat.Classify(Hp(power)).Answer);
				Assert.False(HeroPowerThreat.KillsForFree(Hp(power), 1));
			}
		}

		[Fact]
		public void One_sentence_holding_BOTH_an_aimed_and_a_face_hit_still_counts_the_aimed_one()
		{
			// The other half of the reconciliation, and the error direction that matters: Spread Shot
			// ("Deal $1 damage, then deal $1 damage to the enemy hero") really does aim one of them.
			// Reading the sentence whole credited the face restriction to both halves and reported a
			// harmless hero power — and under-reading a threat is the dangerous mistake here, since
			// this classifier exists to RELAX the one-health rule.
			// Named through the generated pool, which also made this case DETERMINISTIC: two cards are
			// called "Spread Shot" (Uldum's hero power and a duels one), and the name lookup this
			// replaced took whichever HearthDb enumerated first.
			var spreadShot = Hp(HSHeroPower.SpreadShot);
			Assert.Equal(HeroPowerAnswer.DirectDamage, HeroPowerThreat.Classify(spreadShot).Answer);
			Assert.True(HeroPowerThreat.KillsForFree(spreadShot, 1));
		}

		[Fact]
		public void A_CHARGE_token_answers_a_body_but_a_plain_token_does_not()
		{
			// The correction the pool made to a from-memory list. Death Knight's Ghoul has Charge, so
			// it trades the turn it arrives — and it dies at end of turn anyway, so the trade is free.
			// Paladin's Silver Hand Recruit has no Charge: it cannot attack until next turn, so it is
			// not an answer to the body sitting in front of it now.
			Assert.Equal(HeroPowerAnswer.ChargeToken, HeroPowerThreat.Classify(Hp(HSHeroPower.GhoulCharge)).Answer);
			Assert.True(HeroPowerThreat.KillsForFree(Hp(HSHeroPower.GhoulCharge), 1));
			Assert.Equal(HeroPowerAnswer.None, HeroPowerThreat.Classify(Hp(HSHeroPower.Reinforce)).Answer);
			Assert.False(HeroPowerThreat.KillsForFree(Hp(HSHeroPower.Reinforce), 1));
		}

		[Fact]
		public void A_hero_ATTACK_power_is_not_a_free_answer()
		{
			// The distinction that makes a 2/1 and a 3/1 different cards to hold: swinging the hero
			// into a body costs them its attack in face damage. So these classify as HeroAttack and
			// report ZERO free damage — the removal exists, it just is not free.
			// Druid, Demon Hunter, Rogue.
			foreach(var power in new[] { HSHeroPower.Shapeshift, HSHeroPower.DemonClaws, HSHeroPower.DaggerMastery })
			{
				Assert.Equal(HeroPowerAnswer.HeroAttack, HeroPowerThreat.Classify(Hp(power)).Answer);
				Assert.Equal(0, HeroPowerThreat.Classify(Hp(power)).FreeDamage);
				Assert.False(HeroPowerThreat.KillsForFree(Hp(power), 1));
			}
		}

		[Fact]
		public void Armour_healing_and_card_draw_answer_nothing()
		{
			// Warrior, Priest, Warlock, Shaman.
			foreach(var power in new[] { HSHeroPower.ArmorUp, HSHeroPower.LesserHeal, HSHeroPower.LifeTap, HSHeroPower.TotemicCall })
			{
				Assert.Equal(HeroPowerAnswer.None, HeroPowerThreat.Classify(Hp(power)).Answer);
				Assert.False(HeroPowerThreat.KillsForFree(Hp(power), 1));
			}
		}

		[Fact]
		public void A_body_with_no_health_is_never_reported_as_killable()
		{
			// Guards the caller: health comes from card data and a zero would otherwise make every
			// hero power look like an answer.
			Assert.False(HeroPowerThreat.KillsForFree(Hp(HSHeroPower.Fireblast), 0));
		}
	}
}
