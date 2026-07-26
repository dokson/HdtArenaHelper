using System.Collections.Generic;
using System.Linq;
using HdtArenaHelper.CardDatabase;
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

		private static int Dbf(CardEntry card) => card.DbfId;

		private static IReadOnlyCollection<int> Deck(params (CardEntry Card, int Copies)[] cards)
			=> cards.SelectMany(c => Enumerable.Repeat(c.Card.DbfId, c.Copies)).ToList();

		[Fact]
		public void Empty_deck_has_no_synergy()
		{
			Assert.Equal(0, Engine.GetSynergy(Dbf(HSCard.ChillwindYeti), new int[0]).Bonus);
		}

		[Fact]
		public void Unknown_card_has_no_synergy()
		{
			Assert.Equal(0, Engine.GetSynergy(-1, Deck((HSCard.ChillwindYeti, 10))).Bonus);
		}

		// A CONTROLLED pair, and the control is the whole test: both are 1-mana 1/1 neutrals with no
		// tribe, so curve, tribal and school all cancel and the only thing left varying is whether
		// the effect survives being summoned. A first attempt compared a 2-mana Murloc against a
		// 1-mana neutral and measured the curve rule instead — the numbers moved, for the wrong
		// reason, and the test would have "passed" a rule that did nothing.
		private static readonly CardEntry CheapBattlecry = HSCard.MoguCultist;         // 1 mana 1/1
		private static readonly CardEntry CheapDeathrattle = HSCard.PossessedVillager; // 1 mana 1/1

		[Fact]
		public void A_summon_from_deck_card_is_worth_less_in_a_Battlecry_deck()
		{
			// Hearthstone's rule, not a heuristic: a SUMMONED minion never triggers its Battlecry,
			// while a Deathrattle fires as normal. So Boogie Down ("Summon two 1-Cost minions from
			// your deck") fetches two real cards out of a Deathrattle deck and two blank bodies out
			// of a Battlecry one — a difference no win-rate feed can see, because it averages the
			// card over every deck that drafted it. Direction only; the size is capped and unpinned.
			var battlecry = Engine.GetSynergy(Dbf(HSCard.BoogieDown), Deck((CheapBattlecry, 8))).Bonus;
			var deathrattle = Engine.GetSynergy(Dbf(HSCard.BoogieDown), Deck((CheapDeathrattle, 8))).Bonus;

			Assert.True(deathrattle > battlecry);
		}

		[Fact]
		public void A_card_whose_tooltip_WRAPS_mid_phrase_is_still_matched()
		{
			// The regression the 218 green tests missed. Card text carries the client's own tooltip
			// line breaks as newlines, and CleanText does not collapse them: Skydiving Instructor
			// reads "Summon a\n1-Cost minion from\nyour deck", so a pattern with a literal space
			// silently skipped it while Boogie Down — identical effect, luckier wrapping — matched.
			// Measured on the live pool, that accident cost this rule 2 of the 6 cards it exists for.
			// Pinned on the wrapped card specifically, so a pattern that regresses to " " fails here.
			var battlecry = Engine.GetSynergy(Dbf(HSCard.SkydivingInstructor), Deck((CheapBattlecry, 8))).Bonus;
			var deathrattle = Engine.GetSynergy(Dbf(HSCard.SkydivingInstructor), Deck((CheapDeathrattle, 8))).Bonus;

			Assert.True(deathrattle > battlecry);
		}

		[Fact]
		public void A_card_that_summons_only_ITSELF_ignores_the_deck()
		{
			// Patches the Pirate fetches one known card — its own body — so the quality of the
			// surrounding minions says nothing about what it is worth. Found by running the pattern
			// over the live pool rather than by guessing, which is the check AGENTS.md asks for.
			var battlecry = Engine.GetSynergy(Dbf(HSCard.PatchesThePirate), Deck((CheapBattlecry, 8))).Bonus;
			var deathrattle = Engine.GetSynergy(Dbf(HSCard.PatchesThePirate), Deck((CheapDeathrattle, 8))).Bonus;

			Assert.Equal(battlecry, deathrattle);
		}

		[Fact]
		public void A_card_that_fetches_itself_BY_NAME_also_ignores_the_deck()
		{
			// Persistent Peddler summons a "Persistent Peddler" instead of saying "this", so matching
			// only the "summon this" wording missed it. Same situation as Patches: one known card.
			var battlecry = Engine.GetSynergy(Dbf(HSCard.PersistentPeddler), Deck((CheapBattlecry, 8))).Bonus;
			var deathrattle = Engine.GetSynergy(Dbf(HSCard.PersistentPeddler), Deck((CheapDeathrattle, 8))).Bonus;

			Assert.Equal(battlecry, deathrattle);
		}

		[Fact]
		public void A_summon_from_deck_card_with_no_cheap_limit_is_ignored()
		{
			// The rule reads the deck's CHEAP minions, so it may only fire on cards that actually
			// fetch cheap ones. Measured against the live pool, most do not: Maxima Blastenheimer
			// summons ANY minion, and judging it by the 1-2 bucket can invert the sign — a deck of
			// cheap Deathrattles and expensive Battlecries would read as friendly to a card that will
			// most likely summon one of the Battlecries.
			var battlecry = Engine.GetSynergy(Dbf(HSCard.MaximaBlastenheimer), Deck((CheapBattlecry, 8))).Bonus;
			var deathrattle = Engine.GetSynergy(Dbf(HSCard.MaximaBlastenheimer), Deck((CheapDeathrattle, 8))).Bonus;

			Assert.Equal(battlecry, deathrattle);
		}

		[Fact]
		public void Zero_cost_minions_count_as_cheap_bodies()
		{
			// "(2) or less" reaches cost 0, and a 0-mana Battlecry minion is exactly the blank body
			// this rule is about — excluding it would have quietly ignored 13 collectible minions.
			// Witch's Apprentice (0-mana, Battlecry) against Wisp (0-mana, no text).
			var battlecry = Engine.GetSynergy(Dbf(HSCard.BoogieDown), Deck((HSCard.WitchSApprentice, 8))).Bonus;
			var intact = Engine.GetSynergy(Dbf(HSCard.BoogieDown), Deck((HSCard.Wisp_PLACEHOLDER202204, 8))).Bonus;

			Assert.True(battlecry < intact);
		}

		[Fact]
		public void A_card_that_does_not_summon_from_the_deck_is_untouched()
		{
			// The rule must not leak onto ordinary cards: the Battlecry/Deathrattle mix of a deck is
			// irrelevant to a card that never reaches into it.
			var battlecry = Engine.GetSynergy(Dbf(HSCard.ChillwindYeti), Deck((CheapBattlecry, 8))).Bonus;
			var deathrattle = Engine.GetSynergy(Dbf(HSCard.ChillwindYeti), Deck((CheapDeathrattle, 8))).Bonus;

			Assert.Equal(battlecry, deathrattle);
		}

		[Fact]
		public void A_tribal_payoff_whose_tooltip_WRAPS_is_still_seen_as_dependent()
		{
			// The dependency patterns hit the same line-break trap as the summon rule, and the
			// dead-card lever is where it mattered: Corrosive Breath ("Deal $3 damage to a minion.
			// If you're holding a\nDragon, it also hits the enemy hero.") wrapped inside the very
			// clause that makes it dragon-dependent, so with zero dragons drafted it read as an
			// unconditional damage spell and dodged the penalty entirely. Measured on the pool, the
			// literal-space version hid 69 (card, tribe) dependency pairs this way.
			// DRG_006 specifically, and the id matters: the first version of this test used Frizz
			// Kindleroost, which the literal-space patterns ALREADY caught, so it passed with and
			// without the fix and proved nothing. Corrosive Breath is a card the measured diff shows
			// moving from 0.00 to -6.07 — verified by dumping the whole pool through both builds.
			// Judged late in the draft: the penalty is progress-scaled, so a near-complete deck is
			// both where it reaches full size and where the advice actually matters.
			var noDragons = Engine.GetSynergy(Dbf(HSCard.CorrosiveBreath), Deck((HSCard.ChillwindYeti, 26))).Bonus;
			var withDragons = Engine.GetSynergy(Dbf(HSCard.CorrosiveBreath), Deck((HSCard.ChillwindYeti, 22), (HSCard.TwilightDrake, 4))).Bonus;

			// Direction only, deliberately. The past-the-clamp assertion used to live here and no longer
			// belongs: Corrosive Breath's base line ("Deal $3 damage to a minion") still plays without a
			// dragon, so it now takes the REDUCED penalty of a card that loses a rider rather than the
			// full one of a dead card. That is the point of the base-line exemption, and it is pinned on
			// its own test — what this test is for is the tooltip wrapping.
			Assert.True(noDragons < withDragons);
			Assert.True(noDragons < 0, "the dragon rider must still cost something");
		}

		// Secret-dependent NEUTRAL cards: the population that matters, since a class card is only ever
		// offered to its own class. Sunreaver Spy is "Battlecry: If you control a Secret, gain +1/+1".
		private static readonly CardEntry SecretPayoffNeutral = HSCard.SunreaverSpy;
		private static readonly CardEntry AnySecret = HSCard.NobleSacrifice;

		[Fact]
		public void A_SECRET_payoff_is_dead_with_no_secrets_and_live_with_one()
		{
			// The gap this closes, found live: the engine modelled 12 tribes and 7 spell schools but
			// not Secrets, so Chatty Bartender ("if you control a Secret, deal 2 damage to all enemies")
			// took no penalty at all while Mirror Dimension's dragon clause did — in the same offered
			// triple. Membership is the SECRET tag, as objective as a Race.
			var noSecrets = Engine.GetSynergy(Dbf(SecretPayoffNeutral), Deck((HSCard.ChillwindYeti, 26))).Bonus;
			var withSecret = Engine.GetSynergy(Dbf(SecretPayoffNeutral),
				Deck((HSCard.ChillwindYeti, 25), (AnySecret, 1))).Bonus;

			Assert.True(noSecrets < withSecret);
		}

		[Fact]
		public void ONE_secret_clears_the_penalty_exactly_as_one_tribe_member_does()
		{
			// Same rule as a menagerie card: one live enabler clears the whole card. A Secret payoff
			// wants A Secret in play, not a critical mass of them.
			var withSecret = Engine.GetSynergy(Dbf(SecretPayoffNeutral),
				Deck((HSCard.ChillwindYeti, 25), (AnySecret, 1))).Bonus;

			Assert.True(withSecret > -MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void Anti_SECRET_tech_is_not_treated_as_depending_on_your_secrets()
		{
			// The same trap anti-tribe tech set: Eater of Secrets destroys the OPPONENT's Secrets, so
			// drafting no Secrets of your own says nothing about it. Verified over the pool — of the 27
			// neutral cards naming a Secret, the tech ones read as not-dependent through the existing
			// whitelist, which is why no new pattern was added for them.
			var noSecrets = Engine.GetSynergy(Dbf(HSCard.EaterOfSecrets), Deck((HSCard.ChillwindYeti, 26))).Bonus;

			Assert.True(noSecrets > -MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void A_card_naming_no_category_is_untouched_by_the_new_axis()
		{
			// The axis must not leak: a plain vanilla minion's score cannot depend on whether the deck
			// holds Secrets or Auras.
			var withSecret = Engine.GetSynergy(Dbf(HSCard.ChillwindYeti),
				Deck((HSCard.ChillwindYeti, 25), (AnySecret, 1))).Bonus;
			var without = Engine.GetSynergy(Dbf(HSCard.ChillwindYeti), Deck((HSCard.ChillwindYeti, 26))).Bonus;

			Assert.Equal(without, withSecret);
		}

		[Fact]
		public void A_spell_whose_BASE_LINE_still_plays_loses_only_part_of_the_penalty()
		{
			// Found live: Mirror Dimension ("Summon a 0/4 minion with Taunt. If you are holding a
			// Dragon, summon another") was taking the FULL dead-card penalty although it is a perfectly
			// good 1-mana Taunt without a single dragon. The body test only spoke for minions, so every
			// spell whose tribal clause is a RIDER read as structurally dead. Contrast Elemental
			// Evocation, whose entire text is the conditional ("The next Elemental you play costs (2)
			// less") and which really does nothing with no Elementals drafted.
			var rider = Engine.GetSynergy(Dbf(HSCard.MirrorDimension), Deck((HSCard.ChillwindYeti, 26))).Bonus;
			var whollyConditional = Engine.GetSynergy(Dbf(HSCard.ElementalEvocation), Deck((HSCard.ChillwindYeti, 26))).Bonus;

			Assert.True(rider < 0, "the rider still costs something — it is not free");
			Assert.True(rider > whollyConditional,
				"a card that keeps working must be penalized less than one that stops");
		}

		[Fact]
		public void A_clause_that_CONTINUES_the_previous_one_is_not_a_base_line()
		{
			// Ancient Mysteries is "Draw a Secret. It costs (0)." — the second half only modifies the
			// Secret the first half needed, so the card genuinely does nothing with none drafted. Without
			// this guard, lowering the clause-length floor (needed for Grave Digging's 12-character
			// "Draw 2 cards") exempted it and softened a penalty that was correct.
			var mysteries = Engine.GetSynergy(Dbf(HSCard.AncientMysteries), Deck((HSCard.ChillwindYeti, 26))).Bonus;

			Assert.True(mysteries < -MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void A_cost_reducer_for_the_NEXT_member_you_play_is_dependent()
		{
			// The Secret grammar gap: "the next Secret you play costs (0)" is as dependent as "if you
			// control a Secret", but the patterns were written for tribe wordings ("your Dragons") and
			// missed it. Measured across all 12 tribes plus both categories, the three added wordings
			// flag 8 distinct cards once the generation veto and own-membership guard have had their say.
			var noSecrets = Engine.GetSynergy(Dbf(HSCard.KabalLackey), Deck((HSCard.ChillwindYeti, 26))).Bonus;
			var withSecret = Engine.GetSynergy(Dbf(HSCard.KabalLackey),
				Deck((HSCard.ChillwindYeti, 25), (AnySecret, 1))).Bonus;

			Assert.True(noSecrets < withSecret);
		}

		[Fact]
		public void Tribal_payoff_rises_with_drafted_members()
		{
			// Coldlight Seer ("give your other Murlocs +2 Health") with murlocs drafted.
			var noMurlocs = Engine.GetSynergy(Dbf(HSCard.ColdlightSeer), Deck((HSCard.ChillwindYeti, 5))).Bonus;
			var fewMurlocs = Engine.GetSynergy(Dbf(HSCard.ColdlightSeer), Deck((HSCard.MurlocRaider, 2), (HSCard.ChillwindYeti, 3))).Bonus;
			var manyMurlocs = Engine.GetSynergy(Dbf(HSCard.ColdlightSeer), Deck((HSCard.MurlocRaider, 5))).Bonus;

			Assert.True(fewMurlocs > noMurlocs);
			Assert.True(manyMurlocs > fewMurlocs);
		}

		[Fact]
		public void Tribe_member_gains_from_drafted_payoffs()
		{
			// Plain murloc (Murloc Raider) with a payoff (Coldlight Seer) drafted vs not.
			var withPayoff = Engine.GetSynergy(Dbf(HSCard.MurlocRaider), Deck((HSCard.ColdlightSeer, 2), (HSCard.ChillwindYeti, 3))).Bonus;
			var withoutPayoff = Engine.GetSynergy(Dbf(HSCard.MurlocRaider), Deck((HSCard.ChillwindYeti, 5))).Bonus;

			Assert.True(withPayoff > withoutPayoff);
		}

		[Fact]
		public void Third_weapon_is_penalized()
		{
			// Arcanite Reaper offered with two Fiery War Axes already drafted: the weapon
			// malus must outweigh whatever the curve component contributes.
			var thirdWeapon = Engine.GetSynergy(Dbf(HSCard.ArcaniteReaper), Deck((HSCard.FieryWarAxe, 2), (HSCard.ChillwindYeti, 8))).Bonus;
			var firstWeapon = Engine.GetSynergy(Dbf(HSCard.ArcaniteReaper), Deck((HSCard.ChillwindYeti, 10))).Bonus;

			Assert.True(thirdWeapon < firstWeapon);
		}

		[Fact]
		public void Filling_a_curve_gap_beats_overloading_a_full_slot()
		{
			// Deck of ten 2-drops: another 2-drop (Bloodfen Raptor) overloads the slot, a
			// 5-drop (Nightblade) fills a hole.
			var deck = Deck((HSCard.RiverCrocolisk, 10)); // River Crocolisk, 2 mana
			var overload = Engine.GetSynergy(Dbf(HSCard.BloodfenRaptor), deck).Bonus;
			var gapFiller = Engine.GetSynergy(Dbf(HSCard.Nightblade), deck).Bonus;

			Assert.True(gapFiller > overload);
			Assert.True(overload < 0);
		}

		[Fact]
		public void Curve_pressure_grows_with_draft_progress()
		{
			// The same all-2-drops imbalance matters more at pick 21 than at pick 4.
			var early = Engine.GetSynergy(Dbf(HSCard.BloodfenRaptor), Deck((HSCard.RiverCrocolisk, 3))).Bonus;
			var late = Engine.GetSynergy(Dbf(HSCard.BloodfenRaptor), Deck((HSCard.RiverCrocolisk, 20))).Bonus;

			Assert.True(late < early);
		}

		[Fact]
		public void Spell_damage_enabler_pairs_with_damage_spells()
		{
			// Kobold Geomancer with Fireballs drafted vs a spell-less deck.
			var withBurn = Engine.GetSynergy(Dbf(HSCard.KoboldGeomancer), Deck((HSCard.Fireball, 3), (HSCard.ChillwindYeti, 5))).Bonus;
			var withoutBurn = Engine.GetSynergy(Dbf(HSCard.KoboldGeomancer), Deck((HSCard.ChillwindYeti, 8))).Bonus;

			Assert.True(withBurn > withoutBurn);
		}

		[Fact]
		public void Reasons_name_the_dominant_contribution()
		{
			// Murloc payoff with a murloc deck -> tribal is the loudest reason.
			var tribal = Engine.GetSynergy(Dbf(HSCard.ColdlightSeer), Deck((HSCard.MurlocRaider, 5)));
			Assert.Equal("Murloc synergy", tribal.TopReason);

			// A 2-drop into a deck of twenty 2-drops -> the curve complaint dominates.
			var crowded = Engine.GetSynergy(Dbf(HSCard.BloodfenRaptor), Deck((HSCard.RiverCrocolisk, 20)));
			Assert.Equal("crowds the 2-drop slot", crowded.TopReason);

			// Nothing meaningful fired -> no reason line at all.
			var quiet = Engine.GetSynergy(Dbf(HSCard.ChillwindYeti), Deck((HSCard.RiverCrocolisk, 1)));
			Assert.Null(quiet.TopReason);
		}

		[Fact]
		public void Fuzzy_bonus_is_always_clamped()
		{
			// Stack every positive fuzzy rule at once: murloc payoff + members + curve gap.
			var stacked = Engine.GetSynergy(Dbf(HSCard.ColdlightSeer), Deck((HSCard.MurlocRaider, 25))).Bonus;
			Assert.InRange(stacked, -MetadataSynergyEngine.MaxBonus, MetadataSynergyEngine.MaxBonus);

			// And every negative fuzzy one: third weapon into an overloaded slot. (No tribe is
			// referenced here, so the separate dead-card penalty does not apply.)
			var crowded = Engine.GetSynergy(Dbf(HSCard.FieryWarAxe), Deck((HSCard.FieryWarAxe, 27))).Bonus;
			Assert.InRange(crowded, -MetadataSynergyEngine.MaxBonus, MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void Quest_penalty_grows_and_exceeds_the_fuzzy_clamp_late()
		{
			// Supreme Archaeology is a real Quest (GameTag.QUEST): a 1-mana 0/0 build-around
			// that arena tempo rarely rewards — late in the draft the penalty must exceed
			// anything a fuzzy rule could produce, and it must grow with progress.
			var early = Engine.GetSynergy(Dbf(HSCard.SupremeArchaeology), Deck((HSCard.ChillwindYeti, 3))).Bonus;
			var late = Engine.GetSynergy(Dbf(HSCard.SupremeArchaeology), Deck((HSCard.ChillwindYeti, 25))).Bonus;
			Assert.True(late < early);
			Assert.True(late < -MetadataSynergyEngine.MaxBonus, $"expected a strong penalty, got {late}");
		}

		[Fact]
		public void A_card_that_merely_references_quests_is_not_a_quest()
		{
			// Questing Explorer's TEXT says "If you control a Quest, draw a card" but it is
			// a 2-mana 2/3 with no quest tag: it must never take the quest condemnation.
			var explorer = Engine.GetSynergy(Dbf(HSCard.QuestingExplorer), Deck((HSCard.ChillwindYeti, 20)));
			Assert.True(explorer.Bonus > -MetadataSynergyEngine.MaxBonus, $"over-penalized: {explorer.Bonus}");
			Assert.NotEqual("quest — too slow for arena", explorer.TopReason);
		}

		[Fact]
		public void Sidequests_take_a_lighter_penalty_than_full_quests()
		{
			// Clear the Way (GameTag.SIDEQUEST) is a cheap, fast build a normal curve often
			// completes anyway — nudged away, not condemned like Supreme Archaeology.
			var quest = Engine.GetSynergy(Dbf(HSCard.SupremeArchaeology), Deck((HSCard.ChillwindYeti, 20))).Bonus;
			var sidequest = Engine.GetSynergy(Dbf(HSCard.ClearTheWay), Deck((HSCard.ChillwindYeti, 20))).Bonus;
			Assert.True(sidequest > quest);
			Assert.True(sidequest < 0, $"a sidequest should still cost something: {sidequest}");
		}

		[Fact]
		public void Dead_tribal_payoff_is_penalized_beyond_the_fuzzy_clamp()
		{
			// Elemental Evocation is a spell whose ENTIRE text is the dependency ("The next Elemental
			// you play this turn costs (2) less"), so deep into an elemental-less draft its penalty must
			// exceed the ±MaxBonus a fuzzy rule could ever produce.
			//
			// This used to be pinned on Kill Command, and the swap records a real change of meaning:
			// "Deal 3 damage. If you control a Beast, deal 5 instead" keeps working with no Beasts, so it
			// now takes only the reduced penalty. Having no BODY stopped being the same thing as having
			// no FUNCTION — a spell can have a base line too, which is what the exemption added.
			var deadLate = Engine.GetSynergy(Dbf(HSCard.ElementalEvocation), Deck((HSCard.ChillwindYeti, 20))).Bonus;
			Assert.True(deadLate < -MetadataSynergyEngine.MaxBonus, $"expected a strong penalty, got {deadLate}");
		}

		[Fact]
		public void A_conditional_card_with_a_playable_body_is_not_condemned()
		{
			// Blackwing Corruptor without dragons is still a 5-mana 5/4: its conditional text
			// goes blank but the body plays, so it takes only a fraction of the dead penalty —
			// a nudge within the fuzzy range, never a veto.
			var bodied = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 20))).Bonus;
			Assert.True(bodied > -MetadataSynergyEngine.MaxBonus, $"body over-penalized: {bodied}");
			Assert.True(bodied < 0, $"the blank text should still cost something: {bodied}");
		}

		[Fact]
		public void One_live_tribe_clears_a_multi_tribe_card()
		{
			// The Curator draws a Beast, a Dragon AND a Murloc: with beasts drafted it is a
			// live card even with zero dragons/murlocs — one live tribe clears it.
			var withBeasts = Engine.GetSynergy(Dbf(HSCard.TheCurator), Deck((HSCard.BloodfenRaptor, 5), (HSCard.ChillwindYeti, 10))).Bonus;
			var withNothing = Engine.GetSynergy(Dbf(HSCard.TheCurator), Deck((HSCard.ChillwindYeti, 15))).Bonus;
			Assert.True(withBeasts > withNothing);
			Assert.True(withBeasts > -MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void Dead_payoff_penalty_vanishes_once_the_tribe_is_drafted()
		{
			var withoutDragons = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 10))).Bonus;
			// Azure Drake is a Dragon: with it drafted the payoff is live, so no dead penalty.
			var withDragons = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.AzureDrake, 3), (HSCard.ChillwindYeti, 7))).Bonus;
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
			private readonly GameTag _tag;
			private readonly double? _categoryShare;

			// GameTag has no INVALID member, so "no tag configured" is a nullable field.
			private readonly bool _hasTag;

			public FakeAvailability(Race race, double? share,
				GameTag? tag = null, double? categoryShare = null)
			{
				_race = race;
				_share = share;
				_hasTag = tag != null;
				_tag = tag ?? default(GameTag);
				_categoryShare = categoryShare;
			}

			public double? TribeShare(CardClass cls, Race race)
				=> race == _race ? _share : null;

			// Null for anything the test did not set, which is the interface's "unknown" and must
			// leave the penalty exactly as it was.
			public double? CategoryShare(CardClass cls, GameTag tag)
				=> _hasTag && tag == _tag ? _categoryShare : null;
		}

		private static MetadataSynergyEngine EngineWith(Race race, double? share)
		{
			var engine = new MetadataSynergyEngine();
			engine.SetTribeAvailability(new FakeAvailability(race, share));
			return engine;
		}

		// Blackwing Corruptor with no Dragons, mid-draft, in a class the feed describes.
		private static double DeadDragonPayoff(MetadataSynergyEngine engine, CardClass cls)
			=> engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 15)), cls).Bonus;

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
			var blind = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 15))).Bonus;
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
			var blind = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 15))).Bonus;
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
			var blind = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 15))).Bonus;
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
			var early = engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 8)), CardClass.WARRIOR);
			var late = engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 28)), CardClass.WARRIOR);
			Assert.True(late.Bonus < early.Bonus);
		}

		[Fact]
		public void Dead_payoff_penalty_grows_with_draft_progress()
		{
			var early = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 3))).Bonus;
			var late = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 20))).Bonus;
			Assert.True(late < early);
		}

		[Fact]
		public void Dead_payoff_reason_names_the_missing_tribe()
		{
			var dead = Engine.GetSynergy(Dbf(HSCard.BlackwingCorruptor), Deck((HSCard.ChillwindYeti, 20)));
			Assert.Equal("no Dragons for this card", dead.TopReason);
		}

		[Fact]
		public void A_tribe_member_is_not_a_dead_card_when_first_of_its_tribe()
		{
			// Azure Drake IS a Dragon: being the first dragon in the deck must not self-penalize.
			var drake = Engine.GetSynergy(Dbf(HSCard.AzureDrake), Deck((HSCard.ChillwindYeti, 20))).Bonus;
			Assert.True(drake >= -MetadataSynergyEngine.MaxBonus);
		}

		[Fact]
		public void Cheap_spells_do_not_crowd_the_minion_curve()
		{
			// "Curve" in arena means a BODY on turn N. Counting every card by cost meant a deck
			// holding cheap spells read as a full 2-slot, so the engine penalized the 2-drop minions
			// the deck actually needed — backwards, and on a third of the pool (spells).
			var spellHeavy = Deck((HSCard.Fireball, 10)); // ten Fireballs, no minions at all
			var minionHeavy = Deck((HSCard.RiverCrocolisk, 10)); // ten River Crocolisks (2 mana)

			var afterSpells = Engine.GetSynergy(Dbf(HSCard.BloodfenRaptor), spellHeavy).Bonus;   // a 2-drop minion
			var afterMinions = Engine.GetSynergy(Dbf(HSCard.BloodfenRaptor), minionHeavy).Bonus;

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
			var intoMinions = Engine.GetSynergy(Dbf(HSCard.Fireball), Deck((HSCard.RiverCrocolisk, 15)));
			Assert.NotEqual("fills the 5-drop gap", intoMinions.TopReason);
			Assert.NotEqual("crowds the 5-drop slot", intoMinions.TopReason);
		}

		[Fact]
		public void A_third_location_crowds_the_slot()
		{
			// Only one Location occupies the zone at a time. Shipped as a feature, so it must
			// actually fire — an unconditional `return (0, null)` would otherwise go unnoticed.
			var first = Engine.GetSynergy(Dbf(HSCard.RubySanctum), Deck((HSCard.ChillwindYeti, 10))).Bonus;
			var third = Engine.GetSynergy(Dbf(HSCard.RubySanctum),
				Deck((HSCard.ChamberOfAspects, 1), (HSCard.ShrineOfTwilight, 1), (HSCard.ChillwindYeti, 8))).Bonus;
			// The reason line needs MinReasonPoints (0.5), and the penalty is 0.35 per extra
			// location capped at 0.5, so it only surfaces once the slot is properly crowded.
			var crowded = Engine.GetSynergy(Dbf(HSCard.RubySanctum),
				Deck((HSCard.ChamberOfAspects, 1), (HSCard.ShrineOfTwilight, 1), (HSCard.NespirahEnthralled, 1), (HSCard.ChillwindYeti, 7)));

			Assert.True(third < first, $"a third location must score below the first: {third} vs {first}");
			Assert.Equal("too many locations", crowded.TopReason);
		}

		[Fact]
		public void Location_crowding_is_the_gentlest_penalty()
		{
			// Locations are individually strong, so over-penalizing is the risk: it must stay
			// milder than the weapon rule it mirrors.
			var locations = Engine.GetSynergy(Dbf(HSCard.RubySanctum),
				Deck((HSCard.ChamberOfAspects, 1), (HSCard.ShrineOfTwilight, 1), (HSCard.ChillwindYeti, 8))).Bonus;
			var weapons = Engine.GetSynergy(Dbf(HSCard.FieryWarAxe), Deck((HSCard.FieryWarAxe, 2), (HSCard.ChillwindYeti, 8))).Bonus;

			Assert.True(locations > weapons, $"locations {locations} should be gentler than weapons {weapons}");
		}

		[Fact]
		public void Spell_school_payoff_rises_with_drafted_school_members()
		{
			// Erupting Volcano pays off "if you've played a Fire spell". The member side is a tag
			// comparison (Card.SpellSchool is an int), exactly the kind of cast that can silently
			// never match — so pin that drafted Fire spells actually move it.
			var noFire = Engine.GetSynergy(Dbf(HSCard.EruptingVolcano), Deck((HSCard.ChillwindYeti, 10))).Bonus;
			var withFire = Engine.GetSynergy(Dbf(HSCard.EruptingVolcano), Deck((HSCard.FlameLance, 4), (HSCard.ChillwindYeti, 6)));

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
			var atCap = Engine.GetSynergy(Dbf(HSCard.EruptingVolcano), Deck((HSCard.FlameLance, 4), (HSCard.ChillwindYeti, 16))).Bonus;
			var wayPast = Engine.GetSynergy(Dbf(HSCard.EruptingVolcano), Deck((HSCard.FlameLance, 20))).Bonus;
			var nine = Engine.GetSynergy(Dbf(HSCard.EruptingVolcano), Deck((HSCard.FlameLance, 9), (HSCard.ChillwindYeti, 11))).Bonus;

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
			var late = Engine.GetSynergy(Dbf(HSCard.AnimalCompanion), Deck((HSCard.ChillwindYeti, 25)));
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
			var dead = Engine.GetSynergy(Dbf(HSCard.ScaleReplica), Deck((HSCard.ChillwindYeti, 20)));
			Assert.True(dead.Bonus < -MetadataSynergyEngine.MaxBonus, $"expected dead: {dead.Bonus}");
			Assert.Equal("no Dragons for this card", dead.TopReason);
		}
	}
}
