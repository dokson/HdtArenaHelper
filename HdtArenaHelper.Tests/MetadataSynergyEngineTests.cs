using System.Collections.Generic;
using System.Linq;
using HearthDb;
using HearthDb.Enums;
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
		public void Fuzzy_bonus_is_always_clamped()
		{
			// Stack every positive fuzzy rule at once: murloc payoff + members + curve gap.
			var stacked = Engine.GetSynergy(Dbf("EX1_103"), Deck(("CS2_168", 25))).Bonus;
			Assert.InRange(stacked, -MetadataSynergyEngine.MaxBonus, MetadataSynergyEngine.MaxBonus);

			// And every negative fuzzy one: third weapon into an overloaded slot. (No tribe is
			// referenced here, so the separate dead-card penalty does not apply.)
			var crowded = Engine.GetSynergy(Dbf("CS2_106"), Deck(("CS2_106", 27))).Bonus;
			Assert.InRange(crowded, -MetadataSynergyEngine.MaxBonus, MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void Quest_penalty_grows_and_exceeds_the_fuzzy_clamp_late()
		{
			// Supreme Archaeology is a real Quest (GameTag.QUEST): a 1-mana 0/0 build-around
			// that arena tempo rarely rewards — late in the draft the penalty must exceed
			// anything a fuzzy rule could produce, and it must grow with progress.
			var early = Engine.GetSynergy(Dbf("ULD_140"), Deck(("CS2_182", 3))).Bonus;
			var late = Engine.GetSynergy(Dbf("ULD_140"), Deck(("CS2_182", 25))).Bonus;
			Assert.True(late < early);
			Assert.True(late < -MetadataSynergyEngine.MaxBonus, $"expected a strong penalty, got {late}");
		}

		[Fact]
		public void A_card_that_merely_references_quests_is_not_a_quest()
		{
			// Questing Explorer's TEXT says "If you control a Quest, draw a card" but it is
			// a 2-mana 2/3 with no quest tag: it must never take the quest condemnation.
			var explorer = Engine.GetSynergy(Dbf("ULD_157"), Deck(("CS2_182", 20)));
			Assert.True(explorer.Bonus > -MetadataSynergyEngine.MaxBonus, $"over-penalized: {explorer.Bonus}");
			Assert.NotEqual("quest — too slow for arena", explorer.TopReason);
		}

		[Fact]
		public void Sidequests_take_a_lighter_penalty_than_full_quests()
		{
			// Clear the Way (GameTag.SIDEQUEST) is a cheap, fast build a normal curve often
			// completes anyway — nudged away, not condemned like Supreme Archaeology.
			var quest = Engine.GetSynergy(Dbf("ULD_140"), Deck(("CS2_182", 20))).Bonus;
			var sidequest = Engine.GetSynergy(Dbf("DRG_251"), Deck(("CS2_182", 20))).Bonus;
			Assert.True(sidequest > quest);
			Assert.True(sidequest < 0, $"a sidequest should still cost something: {sidequest}");
		}

		[Fact]
		public void Dead_tribal_payoff_is_penalized_beyond_the_fuzzy_clamp()
		{
			// Kill Command is a SPELL referencing Beasts: it has no standalone body, so deep
			// into a beast-less draft its penalty must exceed the ±MaxBonus a fuzzy rule
			// could ever produce.
			var deadLate = Engine.GetSynergy(Dbf("EX1_539"), Deck(("CS2_182", 20))).Bonus;
			Assert.True(deadLate < -MetadataSynergyEngine.MaxBonus, $"expected a strong penalty, got {deadLate}");
		}

		[Fact]
		public void A_conditional_card_with_a_playable_body_is_not_condemned()
		{
			// Blackwing Corruptor without dragons is still a 5-mana 5/4: its conditional text
			// goes blank but the body plays, so it takes only a fraction of the dead penalty —
			// a nudge within the fuzzy range, never a veto.
			var bodied = Engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 20))).Bonus;
			Assert.True(bodied > -MetadataSynergyEngine.MaxBonus, $"body over-penalized: {bodied}");
			Assert.True(bodied < 0, $"the blank text should still cost something: {bodied}");
		}

		[Fact]
		public void One_live_tribe_clears_a_multi_tribe_card()
		{
			// The Curator draws a Beast, a Dragon AND a Murloc: with beasts drafted it is a
			// live card even with zero dragons/murlocs — one live tribe clears it.
			var withBeasts = Engine.GetSynergy(Dbf("KAR_061"), Deck(("CS2_172", 5), ("CS2_182", 10))).Bonus;
			var withNothing = Engine.GetSynergy(Dbf("KAR_061"), Deck(("CS2_182", 15))).Bonus;
			Assert.True(withBeasts > withNothing);
			Assert.True(withBeasts > -MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void Dead_payoff_penalty_vanishes_once_the_tribe_is_drafted()
		{
			var withoutDragons = Engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 10))).Bonus;
			// Azure Drake is a Dragon: with it drafted the payoff is live, so no dead penalty.
			var withDragons = Engine.GetSynergy(Dbf("BRM_034"), Deck(("EX1_284", 3), ("CS2_182", 7))).Bonus;
			Assert.True(withDragons > withoutDragons);
		}

		/// <summary>
		/// Availability feed stub: one tribe's share, so a test can say "this class runs plenty of
		/// Dragons" or "almost none" without touching the network.
		/// </summary>
		private sealed class FakeAvailability : IClassTribeAvailabilitySource
		{
			private readonly Race _race;
			private readonly double? _share;
			public FakeAvailability(Race race, double? share)
			{
				_race = race;
				_share = share;
			}
			public double? TribeShare(CardClass cls, Race race)
				=> race == _race ? _share : null;
		}

		private static MetadataSynergyEngine EngineWith(Race race, double? share)
		{
			var engine = new MetadataSynergyEngine();
			engine.SetTribeAvailability(new FakeAvailability(race, share));
			return engine;
		}

		// Blackwing Corruptor with no Dragons, mid-draft, in a class the feed describes.
		private static double DeadDragonPayoff(MetadataSynergyEngine engine, CardClass cls)
			=> engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 15)), cls).Bonus;

		// NOTE on what these can assert: GetSynergy returns the TOTAL (fuzzy synergy + the dead-card
		// penalty), and the fuzzy part is unchanged by availability. So the total can legitimately be
		// POSITIVE once the penalty is damped — an earlier version of these tests asserted the total
		// stayed negative and failed for that reason, not because the damping was wrong. Everything
		// below is therefore pinned RELATIVE to the class-blind result, which isolates the damping.

		[Fact]
		public void Availability_damps_the_dead_penalty_where_the_tribe_is_plentiful()
		{
			// Measured on the live pool: Dragons are ~9% of a Warrior's deck slots and ~1.7% of a
			// Hunter's. With 15 picks left the first will see Dragons and the second will not, so
			// the same card must not be condemned equally.
			var blind = Engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 15))).Bonus;
			var plentiful = DeadDragonPayoff(EngineWith(Race.DRAGON, 9.0), CardClass.WARRIOR);
			var scarce = DeadDragonPayoff(EngineWith(Race.DRAGON, 1.7), CardClass.HUNTER);

			Assert.True(plentiful > scarce);
			Assert.True(scarce > blind);
		}

		[Fact]
		public void Availability_can_only_reduce_the_dead_penalty_never_deepen_it()
		{
			// The one lever allowed past the fuzzy clamp: no measured input justifies making it
			// harsher, so no availability figure may score BELOW the class-blind penalty. Pinned as
			// a one-way bound over the whole range, not as a value.
			var blind = Engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 15))).Bonus;
			foreach(var share in new[] { 0.0, 1.7, 9.0, 50.0, 100.0 })
			{
				var damped = DeadDragonPayoff(EngineWith(Race.DRAGON, share), CardClass.PRIEST);
				Assert.True(damped >= blind - 1e-9, $"share {share} deepened the penalty");
			}
			// A tribe this class never runs must land exactly on the class-blind penalty.
			Assert.Equal(blind, DeadDragonPayoff(EngineWith(Race.DRAGON, 0.0), CardClass.PRIEST), 6);
		}

		[Fact]
		public void Availability_damping_saturates_instead_of_flipping_the_penalty()
		{
			// The floor is what stops the factor going NEGATIVE: without the clamp, an availability
			// high enough would turn the dead-card penalty into a growing BONUS — the card would
			// score better the more of a tribe it does not have. Saturation is the observable proof
			// the clamp is in place; the floor's absolute size is not readable through this API.
			var high = DeadDragonPayoff(EngineWith(Race.DRAGON, 60.0), CardClass.PRIEST);
			var absurd = DeadDragonPayoff(EngineWith(Race.DRAGON, 100.0), CardClass.PRIEST);
			Assert.Equal(high, absurd, 6);
		}

		[Fact]
		public void Availability_is_ignored_when_the_class_or_the_tribe_is_unknown()
		{
			// Null must be indistinguishable from the old behaviour: "no data" is not "zero".
			var blind = Engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 15))).Bonus;
			Assert.Equal(blind, DeadDragonPayoff(EngineWith(Race.DRAGON, 9.0), CardClass.INVALID), 6);
			Assert.Equal(blind, DeadDragonPayoff(EngineWith(Race.MURLOC, 9.0), CardClass.WARRIOR), 6);
			Assert.Equal(blind, DeadDragonPayoff(EngineWith(Race.DRAGON, null), CardClass.WARRIOR), 6);
		}

		[Fact]
		public void Availability_damping_fades_as_the_draft_runs_out_of_picks()
		{
			// The argument is "members are still coming", so it must weaken with picks left: at
			// pick 28 an abundant tribe no longer rescues the card.
			var engine = EngineWith(Race.DRAGON, 9.0);
			var early = engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 8)), CardClass.WARRIOR);
			var late = engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 28)), CardClass.WARRIOR);
			Assert.True(late.Bonus < early.Bonus);
		}

		[Fact]
		public void Dead_payoff_penalty_grows_with_draft_progress()
		{
			var early = Engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 3))).Bonus;
			var late = Engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 20))).Bonus;
			Assert.True(late < early);
		}

		[Fact]
		public void Dead_payoff_reason_names_the_missing_tribe()
		{
			var dead = Engine.GetSynergy(Dbf("BRM_034"), Deck(("CS2_182", 20)));
			Assert.Equal("no Dragons for this card", dead.TopReason);
		}

		[Fact]
		public void A_tribe_member_is_not_a_dead_card_when_first_of_its_tribe()
		{
			// Azure Drake IS a Dragon: being the first dragon in the deck must not self-penalize.
			var drake = Engine.GetSynergy(Dbf("EX1_284"), Deck(("CS2_182", 20))).Bonus;
			Assert.True(drake >= -MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void Cheap_spells_do_not_crowd_the_minion_curve()
		{
			// "Curve" in arena means a BODY on turn N. Counting every card by cost meant a deck
			// holding cheap spells read as a full 2-slot, so the engine penalized the 2-drop minions
			// the deck actually needed — backwards, and on a third of the pool (spells).
			var spellHeavy = Deck(("CS2_029", 10)); // ten Fireballs, no minions at all
			var minionHeavy = Deck(("CS2_120", 10)); // ten River Crocolisks (2 mana)

			var afterSpells = Engine.GetSynergy(Dbf("CS2_172"), spellHeavy).Bonus;   // a 2-drop minion
			var afterMinions = Engine.GetSynergy(Dbf("CS2_172"), minionHeavy).Bonus;

			Assert.True(afterSpells > afterMinions,
				$"spells must not crowd the minion curve: {afterSpells} vs {afterMinions}");
			Assert.True(afterSpells >= 0,
				$"a 2-drop minion into a spell-only deck must not be penalized, got {afterSpells}");
		}

		[Fact]
		public void The_curve_rule_ignores_non_minions_offered()
		{
			// A spell has no curve slot to fill or crowd, so the rule must stay silent about it
			// rather than reporting a gap the card cannot fill.
			var intoMinions = Engine.GetSynergy(Dbf("CS2_029"), Deck(("CS2_120", 15)));
			Assert.NotEqual("fills the 5-drop gap", intoMinions.TopReason);
			Assert.NotEqual("crowds the 5-drop slot", intoMinions.TopReason);
		}

		[Fact]
		public void A_third_location_crowds_the_slot()
		{
			// Only one Location occupies the zone at a time. Shipped as a feature, so it must
			// actually fire — an unconditional `return (0, null)` would otherwise go unnoticed.
			var first = Engine.GetSynergy(Dbf("CATA_301"), Deck(("CS2_182", 10))).Bonus;
			var third = Engine.GetSynergy(Dbf("CATA_301"),
				Deck(("CATA_477", 1), ("CATA_492", 1), ("CS2_182", 8))).Bonus;
			// The reason line needs MinReasonPoints (0.5), and the penalty is 0.35 per extra
			// location capped at 0.5, so it only surfaces once the slot is properly crowded.
			var crowded = Engine.GetSynergy(Dbf("CATA_301"),
				Deck(("CATA_477", 1), ("CATA_492", 1), ("CATA_527", 1), ("CS2_182", 7)));

			Assert.True(third < first, $"a third location must score below the first: {third} vs {first}");
			Assert.Equal("too many locations", crowded.TopReason);
		}

		[Fact]
		public void Location_crowding_is_the_gentlest_penalty()
		{
			// Locations are individually strong, so over-penalizing is the risk: it must stay
			// milder than the weapon rule it mirrors.
			var locations = Engine.GetSynergy(Dbf("CATA_301"),
				Deck(("CATA_477", 1), ("CATA_492", 1), ("CS2_182", 8))).Bonus;
			var weapons = Engine.GetSynergy(Dbf("CS2_106"), Deck(("CS2_106", 2), ("CS2_182", 8))).Bonus;

			Assert.True(locations > weapons, $"locations {locations} should be gentler than weapons {weapons}");
		}

		[Fact]
		public void Spell_school_payoff_rises_with_drafted_school_members()
		{
			// Erupting Volcano pays off "if you've played a Fire spell". The member side is a tag
			// comparison (Card.SpellSchool is an int), exactly the kind of cast that can silently
			// never match — so pin that drafted Fire spells actually move it.
			var noFire = Engine.GetSynergy(Dbf("CATA_584"), Deck(("CS2_182", 10))).Bonus;
			var withFire = Engine.GetSynergy(Dbf("CATA_584"), Deck(("AT_001", 4), ("CS2_182", 6)));

			Assert.True(withFire.Bonus > noFire,
				$"Fire spells must raise a Fire payoff: {withFire.Bonus} vs {noFire}");
			Assert.Equal("Fire spell synergy", withFire.TopReason);
		}

		[Fact]
		public void Spell_school_payoff_saturates_at_its_member_cap()
		{
			// Arena is minion-dense, so a school rarely reaches critical mass and the rule is
			// capped tight: past the member cap, more Fire spells must stop paying. Same drafted
			// count on both sides so the curve component cannot move between them.
			var atCap = Engine.GetSynergy(Dbf("CATA_584"), Deck(("AT_001", 4), ("CS2_182", 16))).Bonus;
			var wayPast = Engine.GetSynergy(Dbf("CATA_584"), Deck(("AT_001", 20))).Bonus;
			var nine = Engine.GetSynergy(Dbf("CATA_584"), Deck(("AT_001", 9), ("CS2_182", 11))).Bonus;

			Assert.True(nine <= atCap + 1e-9,
				$"past the member cap the school bonus must not keep growing: {nine} vs {atCap}");
			Assert.InRange(wayPast, -MetadataSynergyEngine.MaxBonus, MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void A_card_that_summons_its_own_tribe_is_never_a_dead_card()
		{
			// Animal Companion ("Summon a random Beast Companion") is a premium arena card that
			// brings its own Beast: it MENTIONS a tribe without depending on one, so the
			// dead-card lever must not touch it even deep into a beast-less draft.
			var late = Engine.GetSynergy(Dbf("NEW1_031"), Deck(("CS2_182", 25)));
			Assert.True(late.Bonus >= -MetadataSynergyEngine.MaxBonus,
				$"self-sufficient card condemned: {late.Bonus}");
			Assert.NotEqual("no Beasts for this card", late.TopReason);
		}

		[Fact]
		public void A_genuine_tribal_draw_payoff_is_still_a_dead_card()
		{
			// The guard above must not swallow the real case: Scale Replica ("Draw your lowest
			// and highest Cost Dragon") supplies no dragon of its own, so with none drafted it
			// stays a dead card. Pins that the self-sufficiency escape hatch stays narrow.
			var dead = Engine.GetSynergy(Dbf("TOY_387"), Deck(("CS2_182", 20)));
			Assert.True(dead.Bonus < -MetadataSynergyEngine.MaxBonus, $"expected dead: {dead.Bonus}");
			Assert.Equal("no Dragons for this card", dead.TopReason);
		}
	}
}
