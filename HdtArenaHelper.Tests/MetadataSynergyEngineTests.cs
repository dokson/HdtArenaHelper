using System.Collections.Generic;
using System.Linq;
using HearthDb;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The synergy rules are unvalidated heuristics BY DESIGN (no public per-deck data to
	/// fit them against), so these tests pin directions, caps and the total clamp — not
	/// exact magic numbers.
	/// </summary>
	public class MetadataSynergyEngineTests
	{
		private static readonly MetadataSynergyEngine Engine = new MetadataSynergyEngine();

		private static int Dbf(string cardId) => Cards.All[cardId].DbfId;

		private static IReadOnlyCollection<int> Deck(params (string CardId, int Copies)[] cards)
			=> cards.SelectMany(c => Enumerable.Repeat(Dbf(c.CardId), c.Copies)).ToList();

		[Fact]
		public void Empty_deck_has_no_synergy()
		{
			Assert.Equal(0, Engine.GetSynergy(Dbf("CS2_182"), new int[0]).Bonus);
		}

		[Fact]
		public void Unknown_card_has_no_synergy()
		{
			Assert.Equal(0, Engine.GetSynergy(-1, Deck(("CS2_182", 10))).Bonus);
		}

		[Fact]
		public void Tribal_payoff_rises_with_drafted_members()
		{
			// Coldlight Seer ("give your other Murlocs +2 Health") with murlocs drafted.
			var noMurlocs = Engine.GetSynergy(Dbf("EX1_103"), Deck(("CS2_182", 5))).Bonus;
			var fewMurlocs = Engine.GetSynergy(Dbf("EX1_103"), Deck(("CS2_168", 2), ("CS2_182", 3))).Bonus;
			var manyMurlocs = Engine.GetSynergy(Dbf("EX1_103"), Deck(("CS2_168", 5))).Bonus;

			Assert.True(fewMurlocs > noMurlocs);
			Assert.True(manyMurlocs > fewMurlocs);
		}

		[Fact]
		public void Tribe_member_gains_from_drafted_payoffs()
		{
			// Plain murloc (Murloc Raider) with a payoff (Coldlight Seer) drafted vs not.
			var withPayoff = Engine.GetSynergy(Dbf("CS2_168"), Deck(("EX1_103", 2), ("CS2_182", 3))).Bonus;
			var withoutPayoff = Engine.GetSynergy(Dbf("CS2_168"), Deck(("CS2_182", 5))).Bonus;

			Assert.True(withPayoff > withoutPayoff);
		}

		[Fact]
		public void Third_weapon_is_penalized()
		{
			// Arcanite Reaper offered with two Fiery War Axes already drafted: the weapon
			// malus must outweigh whatever the curve component contributes.
			var thirdWeapon = Engine.GetSynergy(Dbf("CS2_112"), Deck(("CS2_106", 2), ("CS2_182", 8))).Bonus;
			var firstWeapon = Engine.GetSynergy(Dbf("CS2_112"), Deck(("CS2_182", 10))).Bonus;

			Assert.True(thirdWeapon < firstWeapon);
		}

		[Fact]
		public void Filling_a_curve_gap_beats_overloading_a_full_slot()
		{
			// Deck of ten 2-drops: another 2-drop (Bloodfen Raptor) overloads the slot, a
			// 5-drop (Nightblade) fills a hole.
			var deck = Deck(("CS2_120", 10)); // River Crocolisk, 2 mana
			var overload = Engine.GetSynergy(Dbf("CS2_172"), deck).Bonus;
			var gapFiller = Engine.GetSynergy(Dbf("EX1_593"), deck).Bonus;

			Assert.True(gapFiller > overload);
			Assert.True(overload < 0);
		}

		[Fact]
		public void Curve_pressure_grows_with_draft_progress()
		{
			// The same all-2-drops imbalance matters more at pick 21 than at pick 4.
			var early = Engine.GetSynergy(Dbf("CS2_172"), Deck(("CS2_120", 3))).Bonus;
			var late = Engine.GetSynergy(Dbf("CS2_172"), Deck(("CS2_120", 20))).Bonus;

			Assert.True(late < early);
		}

		[Fact]
		public void Spell_damage_enabler_pairs_with_damage_spells()
		{
			// Kobold Geomancer with Fireballs drafted vs a spell-less deck.
			var withBurn = Engine.GetSynergy(Dbf("CS2_142"), Deck(("CS2_029", 3), ("CS2_182", 5))).Bonus;
			var withoutBurn = Engine.GetSynergy(Dbf("CS2_142"), Deck(("CS2_182", 8))).Bonus;

			Assert.True(withBurn > withoutBurn);
		}

		[Fact]
		public void Reasons_name_the_dominant_contribution()
		{
			// Murloc payoff with a murloc deck -> tribal is the loudest reason.
			var tribal = Engine.GetSynergy(Dbf("EX1_103"), Deck(("CS2_168", 5)));
			Assert.Equal("Murloc synergy", tribal.TopReason);

			// A 2-drop into a deck of twenty 2-drops -> the curve complaint dominates.
			var crowded = Engine.GetSynergy(Dbf("CS2_172"), Deck(("CS2_120", 20)));
			Assert.Equal("crowds the 2-drop slot", crowded.TopReason);

			// Nothing meaningful fired -> no reason line at all.
			var quiet = Engine.GetSynergy(Dbf("CS2_182"), Deck(("CS2_120", 1)));
			Assert.Null(quiet.TopReason);
		}

		[Fact]
		public void Total_bonus_is_always_clamped()
		{
			// Stack every positive rule at once: murloc payoff + members + curve gap.
			var stacked = Engine.GetSynergy(Dbf("EX1_103"), Deck(("CS2_168", 25))).Bonus;
			Assert.InRange(stacked, -MetadataSynergyEngine.MaxBonus, MetadataSynergyEngine.MaxBonus);

			// And every negative one: third weapon into an overloaded slot.
			var crowded = Engine.GetSynergy(Dbf("CS2_106"), Deck(("CS2_106", 27))).Bonus;
			Assert.InRange(crowded, -MetadataSynergyEngine.MaxBonus, MetadataSynergyEngine.MaxBonus);
		}
	}
}
