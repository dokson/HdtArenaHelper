using System.Collections.Generic;
using System.Linq;
using HdtArenaHelper.CardDatabase;
using HearthDb.Enums;
using Xunit;
using MirrorCard = HearthMirror.Objects.Card;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The in-game choice decision, split out of HearthMirror so it can be tested. Null means "show
	/// nothing", and getting that wrong is invisible from outside: either the overlay never appears
	/// for a whole game, or a stale Discover sits on the board.
	/// </summary>
	public class CardChoiceWatcherTests
	{
		private static int Dbf(CardEntry card) => card.DbfId;

		private static readonly CardEntry Yeti = HSCard.ChillwindYeti;
		private static readonly CardEntry Raptor = HSCard.BloodfenRaptor;

		// The watcher is handed card ids by the client, so the fixtures produce ids — taken from the
		// named pool, never typed out.
		private static List<MirrorCard> Deck(params (CardEntry Card, int Copies)[] cards)
			=> cards.Select(c => new MirrorCard(c.Card.CardId, c.Copies, 0)).ToList();

		private static string[] Offered(params CardEntry[] cards)
			=> cards.Select(c => c.CardId).ToArray();

		// A Priest hero and a two-card deck: enough to be a valid arena run.
		private static readonly string PriestHero = HSCard.AnduinWrynn.CardId;
		// A real Priest hero power: the dual-class path reads the class off this when Hero is empty.
		private static readonly string PriestHeroPower = HSCard.HolyTouch.CardId;

		[Fact]
		public void No_offered_cards_means_nothing_to_show()
		{
			Assert.Null(CardChoiceWatcher.BuildChoicePlan(null, Deck(), PriestHero, null));
			Assert.Null(CardChoiceWatcher.BuildChoicePlan(new string[0], Deck(), PriestHero, null));
		}

		[Fact]
		public void Without_an_arena_deck_nothing_is_shown()
		{
			// Deliberate, and the reason it is a test: every number this plugin displays is an ARENA
			// win-rate. Outside an arena run there is no number it is entitled to show, so a missing
			// deck must return null rather than falling back to class-agnostic scores.
			Assert.Null(CardChoiceWatcher.BuildChoicePlan(
				Offered(Yeti), deckCards: null, hero: PriestHero, heroPower: null));
		}

		[Fact]
		public void One_unresolvable_id_voids_the_whole_choice()
		{
			// Not a taste call: plaques are laid out by INDEX and centred on the count, so scoring
			// 2 of 3 cards would centre two plaques over three cards and put every score on the
			// wrong one. Dropping the bad id and rendering the rest is worse than rendering nothing.
			// It is also transient — HearthDb may still be loading — so returning null lets the next
			// poll retry, where consuming the choice would have frozen it half-built.
			Assert.Null(CardChoiceWatcher.BuildChoicePlan(
				new[] { Yeti.CardId, "NOT_A_REAL_CARD_ID", Raptor.CardId }, Deck(), PriestHero, null));

			var whole = CardChoiceWatcher.BuildChoicePlan(
				Offered(Yeti, Raptor), Deck(), PriestHero, null);
			Assert.Equal(new[] { Dbf(Yeti), Dbf(Raptor) }, whole!.Args.OfferedDbfIds);
		}

		[Fact]
		public void Offered_order_is_preserved_and_is_the_dedup_key()
		{
			// Order is the client's, and it must survive: the overlay lays plaques out by index, so
			// reordering would put every score over the wrong card. The signature follows that order
			// so that re-polling the same choice is deduped while a genuinely new one is not.
			var plan = CardChoiceWatcher.BuildChoicePlan(
				Offered(Raptor, Yeti), Deck(), PriestHero, null);
			var reordered = CardChoiceWatcher.BuildChoicePlan(
				Offered(Yeti, Raptor), Deck(), PriestHero, null);

			Assert.Equal($"{Dbf(Raptor)},{Dbf(Yeti)}", plan!.Signature);
			Assert.NotEqual(plan.Signature, reordered!.Signature);
		}

		[Fact]
		public void The_class_comes_from_the_hero_and_falls_back_to_the_hero_power()
		{
			// Same rule as the draft watcher: dual-class arena leaves Deck.Hero empty and carries the
			// class on the hero power instead.
			var fromHero = CardChoiceWatcher.BuildChoicePlan(
				Offered(Yeti), Deck(), PriestHero, null);
			var fromPower = CardChoiceWatcher.BuildChoicePlan(
				Offered(Yeti), Deck(), null, PriestHeroPower);

			Assert.Equal(CardClass.PRIEST, fromHero!.Args.DeckClass);
			Assert.Equal(CardClass.PRIEST, fromPower!.Args.DeckClass);
		}

		[Fact]
		public void An_unknown_hero_leaves_the_class_invalid_rather_than_guessing()
		{
			var plan = CardChoiceWatcher.BuildChoicePlan(Offered(Yeti), Deck(), null, null);

			Assert.NotNull(plan);
			Assert.Equal(CardClass.INVALID, plan!.Args.DeckClass);
		}

		[Fact]
		public void The_deck_context_expands_copies_so_synergy_counts_them()
		{
			// The synergy engine counts members, so two copies must arrive as two entries — a deck
			// deduped to distinct cards would under-count every tribe and every curve slot.
			var plan = CardChoiceWatcher.BuildChoicePlan(
				Offered(Yeti), Deck((Raptor, 2), (Yeti, 1)), PriestHero, null);

			Assert.Equal(3, plan!.Args.DeckDbfIds.Count);
			Assert.Equal(2, plan.Args.DeckDbfIds.Count(id => id == Dbf(Raptor)));
		}
	}
}
