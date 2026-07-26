using System;
using System.Collections.Generic;
using System.Linq;
using HdtArenaHelper.CardDatabase;
using Xunit;

namespace HdtArenaHelper.Numerics.Tests
{
	/// <summary>
	/// The committed card pool's own invariants — the ones checkable WITHOUT the card DB, which is
	/// why they live in the suite that runs with no HDT installed. Whether the pool still matches
	/// HearthDb is a different question, asked by the drift test in HdtArenaHelper.Tests.
	///
	/// Every assertion here is structural, never a count: the pool moves with each patch, so a test
	/// pinning "7367 cards" would fail on data rather than on a defect.
	/// </summary>
	public class CardDatabaseTests
	{
		private static IReadOnlyList<CardEntry> All => HdtArenaHelper.CardDatabase.CardDatabase.All;

		[Fact]
		public void Pool_is_populated()
		{
			// A generator that resolved nothing (HearthDb empty at the time — the failure mode this
			// project already had at OnLoad) would emit a valid, empty file that every other
			// assertion here passes.
			Assert.True(All.Count > 5000, $"only {All.Count} cards in the committed pool");
		}

		[Fact]
		public void Dbf_ids_are_unique()
		{
			// The dbf id is the join key everything else uses; a duplicate would make a lookup
			// ambiguous rather than wrong-and-obvious.
			var dupes = All.GroupBy(c => c.DbfId).Where(g => g.Count() > 1)
				.Select(g => $"{g.Key} ({string.Join(", ", g.Select(c => c.Name))})").ToList();

			Assert.Empty(dupes);
		}

		[Fact]
		public void Card_ids_are_unique()
		{
			var dupes = All.GroupBy(c => c.CardId, StringComparer.Ordinal)
				.Where(g => g.Count() > 1).Select(g => g.Key).ToList();

			Assert.Empty(dupes);
		}

		[Fact]
		public void Identity_fields_are_present()
		{
			Assert.Empty(All.Where(c => string.IsNullOrWhiteSpace(c.CardId)).Select(c => c.DbfId));
			Assert.Empty(All.Where(c => string.IsNullOrWhiteSpace(c.Name)).Select(c => c.CardId));
			Assert.Empty(All.Where(c => string.IsNullOrWhiteSpace(c.Set)).Select(c => c.CardId));
			Assert.Empty(All.Where(c => c.DbfId <= 0).Select(c => c.CardId));
		}

		[Fact]
		public void Stats_are_never_negative()
		{
			// The heuristic reads these straight into the stat curve, where a negative would not
			// throw — it would quietly shift a score.
			Assert.Empty(All.Where(c => c.Cost < 0 || c.Attack < 0 || c.Health < 0 || c.Durability < 0)
				.Select(c => c.Name));
		}

		[Fact]
		public void Hero_powers_are_included()
		{
			// They were excluded at first, by a filter that never fired: hero powers are not
			// COLLECTIBLE, so filtering the collectible set by type removed nothing and the exclusion
			// was an accident of the source query. They are in on purpose now — HeroPowerThreat
			// classifies them, and its fixtures are the reason the pool is committed at all.
			Assert.Contains(All, c => c.Type == "HERO_POWER");
			Assert.Equal("HERO_POWER", HSCard.Fireblast.Type);
		}

		[Fact]
		public void A_named_accessor_returns_the_card_it_is_named_after()
		{
			// The accessors are the interface every fixture uses, so a wrong one silently tests the
			// wrong card. Checked on cards whose name and stats are both fixed by the game's history.
			Assert.Equal("Chillwind Yeti", HSCard.ChillwindYeti.Name);
			Assert.Equal(4, HSCard.ChillwindYeti.Cost);
			Assert.Equal(4, HSCard.ChillwindYeti.Attack);
			Assert.Equal(5, HSCard.ChillwindYeti.Health);
			Assert.Equal("MINION", HSCard.ChillwindYeti.Type);

			// A reprint keeps the bare name for its canonical (lowest-dbf-id) printing and the others
			// take a set suffix, so both must resolve to a card of that name rather than one winning.
			Assert.Equal("Assassinate", HSCard.Assassinate.Name);
			Assert.Equal("Assassinate", HSCard.Assassinate_CORE.Name);
			Assert.NotEqual(HSCard.Assassinate.DbfId, HSCard.Assassinate_CORE.DbfId);
			Assert.True(HSCard.Assassinate.DbfId < HSCard.Assassinate_CORE.DbfId,
				"the bare name must be the canonical printing, or a fixture's card moves when a reprint lands");
		}

		[Fact]
		public void Get_by_dbf_id_agrees_with_the_named_accessor()
		{
			Assert.Equal(HSCard.Fireball.DbfId, HSCard.Get(HSCard.Fireball.DbfId).DbfId);
			Assert.Equal(HSCard.Fireball.Name, HSCard.Get(HSCard.Fireball.DbfId).Name);
		}

		[Fact]
		public void Ordering_is_total_and_reproducible()
		{
			// Set ordinal, then cost, then name, then id — the fourth key is what makes it TOTAL.
			// Without it two cards sharing set/cost/name fall back to dictionary enumeration order
			// and CI churns a 1.7 MB diff on a run where nothing changed.
			var expected = All
				.OrderBy(c => c.Set, StringComparer.Ordinal)
				.ThenBy(c => c.Cost)
				.ThenBy(c => c.Name, StringComparer.Ordinal)
				.ThenBy(c => c.CardId, StringComparer.Ordinal)
				.Select(c => c.CardId)
				.ToList();

			Assert.Equal(expected, All.Select(c => c.CardId).ToList());
		}

		[Fact]
		public void Text_carries_no_line_breaks()
		{
			// Card text arrives with the client's TOOLTIP LINE BREAKS as newlines (the bug this
			// project paid for twice). The generator flattens them, and it must stay that way: a
			// newline here would end a markdown row mid-card and break the emitted C# literal.
			Assert.Empty(All.Where(c => c.Text.Contains("\n") || c.Text.Contains("\r"))
				.Select(c => c.CardId));
		}

		[Theory]
		[InlineData(CardFlags.Elite)]
		[InlineData(CardFlags.Taunt)]
		[InlineData(CardFlags.DivineShield)]
		[InlineData(CardFlags.Windfury)]
		[InlineData(CardFlags.Poisonous)]
		[InlineData(CardFlags.Reborn)]
		[InlineData(CardFlags.Deathrattle)]
		[InlineData(CardFlags.Battlecry)]
		[InlineData(CardFlags.Combo)]
		[InlineData(CardFlags.Secret)]
		[InlineData(CardFlags.Aura)]
		[InlineData(CardFlags.Quest)]
		[InlineData(CardFlags.Questline)]
		[InlineData(CardFlags.Sidequest)]
		[InlineData(CardFlags.Tradeable)]
		public void Every_flag_is_carried_by_at_least_one_card(CardFlags flag)
		{
			// The guard against a silently broken tag read: a generator emitting CardFlags.None for
			// everything satisfies every other assertion in this file. Each of the fifteen tags was
			// measured non-empty in the pool this file was generated from, so an empty one is a
			// defect in the read, not a rotation — with one caveat, that a future rotation could
			// legitimately empty a narrow axis (Sidequest is the thinnest at single digits).
			Assert.Contains(All, c => c.Has(flag));
		}

		[Fact]
		public void Flag_axes_agree_with_the_type_they_describe()
		{
			// Secrets and quests are spells in Hearthstone's rules, not a free-form axis: a minion
			// carrying either would mean the tag read landed on the wrong entity.
			Assert.Empty(All.Where(c => c.Has(CardFlags.Secret) && c.Type != "SPELL").Select(c => c.Name));
			Assert.Empty(All.Where(c => c.Has(CardFlags.Deathrattle) && c.Type == "SPELL").Select(c => c.Name));
		}
	}
}
