using System.Collections.Generic;
using System.Linq;
using HdtArenaHelper.CardDatabase;
using HearthDb.Enums;
using HearthMirror.Objects;
using Xunit;
using Card = HearthMirror.Objects.Card;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The mulligan decision, split out of HearthMirror so it can be tested. Null means "show
	/// nothing" — and here that matters more than elsewhere, because the overlay places one column
	/// per card: a partial or misordered hand does not degrade, it lies.
	/// </summary>
	public class MulliganWatcherTests
	{
		private static int Dbf(CardEntry card) => card.DbfId;

		private static readonly CardEntry Yeti = HSCard.ChillwindYeti;
		private static readonly CardEntry Raptor = HSCard.BloodfenRaptor;
		private static readonly CardEntry Ogre = HSCard.BoulderfistOgre;
		// The watcher takes the hero and hero power as the client reports them — card ids — so these
		// stay strings, named through the pool instead of typed out.
		private static readonly string PriestHero = HSHero.AnduinWrynn.CardId;
		private static readonly string PriestHeroPower = HSHeroPower.HolyTouch.CardId;

		// The watcher is handed CARD IDS by the client, so a fixture has to produce them. It takes
		// them from the named pool rather than as literals: the id is the client's currency, the name
		// is the reader's.
		private static List<MulliganState.MulliganCard> Hand(params (CardEntry Card, int Zone)[] cards)
			=> HandOf(cards.Select(c => (c.Card.CardId, c.Zone)).ToArray());

		private static List<MulliganState.MulliganCard> HandOf(params (string CardId, int Zone)[] cards)
			=> cards.Select(c => new MulliganState.MulliganCard(c.Zone, c.CardId, default)).ToList();

		/// <summary>Stands in for the run's 30 cards: the advice is judged against the deck.</summary>
		private static List<Card> Deck()
			=> new List<Card> { new Card(Yeti.CardId, 30, 0) };

		[Fact]
		public void The_hand_is_ordered_by_the_clients_zone_position()
		{
			// The overlay indexes columns by position, so the client's order is authoritative — not
			// the order the list happens to arrive in.
			var plan = MulliganWatcher.BuildMulliganPlan(
				Hand((Ogre, 3), (Yeti, 1), (Raptor, 2)), PriestHero, null, Deck());

			Assert.Equal(new[] { Dbf(Yeti), Dbf(Raptor), Dbf(Ogre) },
				plan!.Args.HandDbfIds);
		}

		[Fact]
		public void An_unresolvable_card_voids_the_hand()
		{
			// Same rule as the in-game choice, same reason: dropping one card would slide every
			// number onto its neighbour. Not resolving is also transient while HearthDb loads, so
			// returning null lets the next poll retry.
			Assert.Null(MulliganWatcher.BuildMulliganPlan(
				HandOf((Yeti.CardId, 1), ("NOT_A_REAL_CARD", 2)), PriestHero, null, Deck()));
		}

		[Fact]
		public void Nothing_is_shown_without_a_hand()
		{
			Assert.Null(MulliganWatcher.BuildMulliganPlan(null, PriestHero, null, Deck()));
			Assert.Null(MulliganWatcher.BuildMulliganPlan(Hand(), PriestHero, null, Deck()));
		}

		[Fact]
		public void Nothing_is_shown_without_a_class()
		{
			// The keep statistics are per class. With no class the honest options are pooled numbers
			// — which answer a different question than the screen asks — or nothing, and nothing wins.
			Assert.Null(MulliganWatcher.BuildMulliganPlan(Hand((Yeti, 1)), null, null, Deck()));
		}

		[Fact]
		public void The_class_falls_back_to_the_hero_power()
		{
			var plan = MulliganWatcher.BuildMulliganPlan(Hand((Yeti, 1)), null, PriestHeroPower, Deck());
			Assert.Equal(CardClass.PRIEST, plan!.Args.DeckClass);
		}

		[Fact]
		public void The_signature_follows_the_hand_so_a_redraw_is_not_deduped()
		{
			// Kept vs replaced cards are a different hand and must re-render; the same hand polled
			// twice a second must not.
			var first = MulliganWatcher.BuildMulliganPlan(
				Hand((Yeti, 1), (Raptor, 2)), PriestHero, null, Deck());
			var same = MulliganWatcher.BuildMulliganPlan(
				Hand((Yeti, 1), (Raptor, 2)), PriestHero, null, Deck());
			var redrawn = MulliganWatcher.BuildMulliganPlan(
				Hand((Yeti, 1), (Ogre, 2)), PriestHero, null, Deck());

			Assert.Equal(first!.Signature, same!.Signature);
			Assert.NotEqual(first.Signature, redrawn!.Signature);
		}
	}
}
