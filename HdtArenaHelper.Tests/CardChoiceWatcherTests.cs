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
		private static readonly string PriestHero = HSHero.AnduinWrynn.CardId;
		// A real Priest hero power: the dual-class path reads the class off this when Hero is empty.
		private static readonly string PriestHeroPower = HSHeroPower.HolyTouch.CardId;

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

		// ---- the dedup/debounce gate ------------------------------------------------
		//
		// Measured on a live session before this existed: 8 of 35 in-game choices were exact repeats
		// of the trio before them, four within 6-12 seconds. The client drops IsVisible for a moment
		// mid-choice, and clearing on the first empty poll threw away the dedup key, so the same
		// Discover was announced again. None of that is visible from outside — the overlay just
		// reappears — which is why the state machine is separate and tested here.

		private static CardChoiceWatcher.ChoiceGate Gate()
			=> new CardChoiceWatcher.ChoiceGate(CardChoiceWatcher.PollsBeforeGone);

		[Fact]
		public void A_new_choice_is_announced_once()
		{
			var gate = Gate();

			Assert.True(gate.Announce("1,2,3"));
			Assert.False(gate.Announce("1,2,3"));
			Assert.False(gate.Announce("1,2,3"));
		}

		[Fact]
		public void A_flicker_does_not_re_announce_the_same_choice()
		{
			// The bug this exists for. One empty poll must not end the choice, or the very next poll
			// sees the same cards as new and the overlay fires again seconds later.
			var gate = Gate();
			gate.Announce("1,2,3");

			Assert.False(gate.Miss());                 // one empty poll: not gone
			Assert.False(gate.Announce("1,2,3"));      // and the dedup key survived it
		}

		[Fact]
		public void A_lasting_absence_ends_the_choice()
		{
			var gate = Gate();
			gate.Announce("1,2,3");

			for(var i = 1; i < CardChoiceWatcher.PollsBeforeGone; i++)
				Assert.False(gate.Miss());

			Assert.True(gate.Miss());                  // the threshold poll: gone, once
			Assert.False(gate.Miss());                 // and not again while nothing is showing
		}

		[Fact]
		public void After_a_choice_ends_the_SAME_cards_are_a_new_choice()
		{
			// The other direction, and the reason the debounce is a threshold rather than a mute:
			// a Discover really can offer the same three cards later, and once the first is over the
			// second must be announced.
			var gate = Gate();
			gate.Announce("1,2,3");
			for(var i = 0; i < CardChoiceWatcher.PollsBeforeGone; i++)
				gate.Miss();

			Assert.True(gate.Announce("1,2,3"));
		}

		[Fact]
		public void A_different_choice_is_announced_even_without_an_empty_poll()
		{
			// The client can swap one choice straight into another (a Discover that discovers again).
			var gate = Gate();
			gate.Announce("1,2,3");

			Assert.True(gate.Announce("4,5,6"));
		}
	}
}
