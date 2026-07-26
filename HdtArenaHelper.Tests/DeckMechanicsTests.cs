using System.Collections.Generic;
using System.Linq;
using HdtArenaHelper.CardDatabase;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// DeckMechanics is DESCRIPTIVE — it counts, it does not judge — so these tests pin counts
	/// exactly, unlike the synergy tests which may only pin directions. A count that cannot be
	/// checked against the deck on screen would be worthless to the player.
	/// </summary>
	public class DeckMechanicsTests
	{
		private static IReadOnlyCollection<int> Deck(params (CardEntry Card, int Copies)[] cards)
			=> cards.SelectMany(c => Enumerable.Repeat(c.Card.DbfId, c.Copies)).ToList();

		// Real cards on purpose: the counts come from the same metadata the scoring model reads, so a
		// fixture with invented cards would be testing the test. Named through the generated pool
		// rather than by id, so the card a fixture uses cannot disagree with the comment naming it —
		// the ROLE each one plays here is what still needs saying.
		private static readonly CardEntry PlainFourDrop = HSCard.ChillwindYeti;      // 4/5, no text
		private static readonly CardEntry PlainOneDrop = HSCard.ArgentSquire;        // the cheap end
		private static readonly CardEntry PlainThreeDrop = HSCard.SpiderTank;        // vanilla, no wording to trip on
		private static readonly CardEntry PlainSixDrop = HSCard.CairneBloodhoof;     // the expensive end
		private static readonly CardEntry DamageSpell = HSCard.ArcaneMissiles;
		private static readonly CardEntry CheapWeapon = HSCard.Tuskpiercer;
		private static readonly CardEntry HardRemoval = HSCard.Assassinate;          // destroys outright
		private static readonly CardEntry AoeSpell = HSCard.ArcaneExplosion;         // damages every enemy minion
		private static readonly CardEntry DrawSpell = HSCard.ArcaneIntellect;
		// Removal carried on a BODY, which is the case that made counting only spells misleading.
		private static readonly CardEntry DamageMinion = HSCard.BlowgillSniper;

		[Fact]
		public void An_empty_or_null_deck_describes_as_all_zeroes()
		{
			// Called from a render path, so it must never throw on the empty case.
			var nothing = DeckMechanics.Describe(null);

			Assert.Equal(0, nothing.Minions);
			Assert.Equal(7, nothing.MinionCurve.Count);
			Assert.All(nothing.MinionCurve, c => Assert.Equal(0, c));
			Assert.Equal(0, DeckMechanics.Describe(new int[0]).Draw);
		}

		[Fact]
		public void Card_types_are_counted_apart()
		{
			// Chillwind Yeti (minion), Arcane Missiles (spell), Tuskpiercer (weapon).
			var m = DeckMechanics.Describe(Deck((PlainFourDrop, 3), (DamageSpell, 2), (CheapWeapon, 1)));

			Assert.Equal(3, m.Minions);
			Assert.Equal(2, m.Spells);
			Assert.Equal(1, m.Weapons);
		}

		[Fact]
		public void The_curve_counts_MINIONS_ONLY()
		{
			// The lesson the synergy engine paid for: counting spells by cost made a few cheap spells
			// read as a full slot, so the curve is a count of BODIES. Ten 1-mana spells must leave the
			// curve empty — a deck of cheap removal still has nothing to play on turn one.
			var spellsOnly = DeckMechanics.Describe(Deck((DamageSpell, 10)));
			Assert.All(spellsOnly.MinionCurve, c => Assert.Equal(0, c));
			Assert.Equal(10, spellsOnly.Spells);

			// Chillwind Yeti is 4 mana: bucket index 3 (0-1, 2, 3, 4, 5, 6, 7+).
			var bodies = DeckMechanics.Describe(Deck((PlainFourDrop, 2)));
			Assert.Equal(2, bodies.MinionCurve[3]);
		}

		[Fact]
		public void The_curve_uses_the_SAME_buckets_as_the_synergy_engine()
		{
			// Both report "which slot is this" to the same player, from the same deck. Pinned because
			// two copies of the bucketing would drift the moment one of them changed.
			var cheap = DeckMechanics.Describe(Deck((PlainOneDrop, 1)));   // Argent Squire, 1 mana
			var huge = DeckMechanics.Describe(Deck((PlainSixDrop, 1)));    // Cairne, 6 mana

			Assert.Equal(1, cheap.MinionCurve[MetadataSynergyEngine.CostBucket(1)]);
			Assert.Equal(1, huge.MinionCurve[MetadataSynergyEngine.CostBucket(6)]);
		}

		[Fact]
		public void Hard_removal_and_damage_are_counted_SEPARATELY()
		{
			// "Destroy a minion" and "deal 3 damage" are not the same answer to a big minion, so they
			// are not one number. Assassinate (destroy) vs Arcane Missiles (damage).
			var destroy = DeckMechanics.Describe(Deck((HardRemoval, 1)));
			var damage = DeckMechanics.Describe(Deck((DamageSpell, 1)));

			Assert.Equal(1, destroy.HardRemoval);
			Assert.Equal(0, destroy.DamageCards);
			Assert.Equal(1, damage.DamageCards);
		}

		[Fact]
		public void Damage_on_a_MINION_counts_as_removal_too()
		{
			// The correction that came out of a real deck: counting only SPELLS reported "removal 0"
			// for a Paladin deck whose answers were all Battlecries, which is true to the letter and
			// useless to the player. Damaging a minion to kill it is removal whatever carries it.
			// Blowgill Sniper is a 2-mana 2/1 with "Battlecry: Deal 1 damage".
			var m = DeckMechanics.Describe(Deck((DamageMinion, 1)));

			Assert.Equal(1, m.Minions);
			Assert.Equal(1, m.DamageCards);
		}

		[Fact]
		public void AoE_and_draw_are_counted()
		{
			// Arcane Explosion (damage all enemy minions) and Arcane Intellect (draw 2).
			var aoe = DeckMechanics.Describe(Deck((AoeSpell, 1)));
			var draw = DeckMechanics.Describe(Deck((DrawSpell, 1)));

			Assert.Equal(1, aoe.Aoe);
			Assert.Equal(1, draw.Draw);
		}

		[Fact]
		public void Copies_are_counted_once_each_rather_than_deduplicated()
		{
			// The caller expands copies before describing, so three copies of a draw spell are three
			// cards that draw — a summary that deduplicated would understate every doubled card.
			var one = DeckMechanics.Describe(Deck((DrawSpell, 1)));
			var three = DeckMechanics.Describe(Deck((DrawSpell, 3)));

			Assert.Equal(1, one.Draw);
			Assert.Equal(3, three.Draw);
		}

		[Fact]
		public void An_unknown_id_is_skipped_rather_than_throwing()
		{
			// A panel must not die over one id HearthDb does not know.
			var m = DeckMechanics.Describe(new[] { -1, PlainFourDrop.DbfId });

			Assert.Equal(1, m.Minions);
		}

		[Fact]
		public void The_curve_profile_reads_the_mean_MINION_cost()
		{
			// Absolute thresholds, not relative to the class: a class's typical curve is a product of
			// the current card pool (Warrior has fielded dominant low-curve aggro decks), so anchoring
			// to a class baseline would encode this patch's meta as the game's shape. What is invariant
			// is the mana schedule, and that is what makes the mean meaningful at all.
			// Argent Squire (1), then a 3/4 mix averaging 3.5, then Cairne (6). The mix matters: a deck
			// of pure 3-drops averages 3.0 and reads AGGRO under these thresholds, which is the correct
			// consequence of them — the lowest class average measured on the live pool is 3.01.
			var cheap = DeckMechanics.Describe(Deck((PlainOneDrop, 20)));
			var mid = DeckMechanics.Describe(Deck((PlainThreeDrop, 10), (PlainFourDrop, 10)));
			var expensive = DeckMechanics.Describe(Deck((PlainSixDrop, 20)));

			Assert.Equal("aggro", cheap.Profile);
			Assert.Equal("midrange", mid.Profile);
			Assert.Equal("control", expensive.Profile);
		}

		[Fact]
		public void A_spell_only_deck_still_gets_a_profile()
		{
			// The profile reads the WHOLE deck, so a pile of one-mana spells sits at the aggressive end of
			// the cost axis and says so. This replaces a rule that keyed on minions, which read a
			// removal-and-AoE deck as midrange — and expensive spells are what a control deck is made of.
			//
			// The fixture is not a possible arena deck (every real one has minions), and that is fine:
			// what a test must keep reachable is the STATE it asserts, not the fixture that isolates it.
			// The state here — a profile computed over all card types — is what every real deck produces.
			var spellsOnly = DeckMechanics.Describe(Deck((DamageSpell, 20)));

			Assert.Equal("aggro", spellsOnly.Profile);
			Assert.Equal(0, spellsOnly.Minions);
		}

		[Fact]
		public void The_thinnest_slot_survives_a_perfectly_average_mean()
		{
			// Why the profile is not shown alone: a mean hides structure. This deck averages 3.5 and so
			// reads squarely midrange, while holding nothing at all below three mana — and the thinnest
			// slot is what makes that visible. Seen on a real deck that sat dead in the middle of
			// midrange while missing its three- and four-drops entirely.
			var flat = DeckMechanics.Describe(Deck((PlainThreeDrop, 10), (PlainFourDrop, 10)));

			Assert.Equal("midrange", flat.Profile);
			Assert.True(flat.ThinnestSlot >= 0, "a deck holding only two costs is short elsewhere");
			// The thinnest slot must be one the deck does NOT fill.
			Assert.NotEqual(MetadataSynergyEngine.CostBucket(3), flat.ThinnestSlot);
			Assert.NotEqual(MetadataSynergyEngine.CostBucket(4), flat.ThinnestSlot);
		}

		[Fact]
		public void The_log_line_names_every_count_it_reports()
		{
			var line = DeckMechanics.Describe(Deck((PlainFourDrop, 2), (DrawSpell, 1))).ToLine();

			Assert.Contains("curve", line);
			Assert.Contains("minions", line);
			Assert.Contains("removal", line);
			Assert.Contains("AoE", line);
			Assert.Contains("draw", line);
		}
	}
}
