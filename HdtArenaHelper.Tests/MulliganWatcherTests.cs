using System.Collections.Generic;
using System.Linq;
using HearthDb;
using HearthDb.Enums;
using HearthMirror.Objects;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The mulligan decision, split out of HearthMirror so it can be tested. Null means "show
	/// nothing" — and here that matters more than elsewhere, because the overlay places one column
	/// per card: a partial or misordered hand does not degrade, it lies.
	/// </summary>
	public class MulliganWatcherTests
	{
		private static int Dbf(string cardId) => Cards.All[cardId].DbfId;

		private const string PriestHero = "HERO_09";

		private static List<MulliganState.MulliganCard> Hand(params (string CardId, int Zone)[] cards)
			=> cards.Select(c => new MulliganState.MulliganCard(c.Zone, c.CardId, default)).ToList();

		[Fact]
		public void The_hand_is_ordered_by_the_clients_zone_position()
		{
			// The overlay indexes columns by position, so the client's order is authoritative — not
			// the order the list happens to arrive in.
			var plan = MulliganWatcher.BuildMulliganPlan(
				Hand(("CS2_200", 3), ("CS2_182", 1), ("CS2_172", 2)), PriestHero, null);

			Assert.Equal(new[] { Dbf("CS2_182"), Dbf("CS2_172"), Dbf("CS2_200") },
				plan!.Args.HandDbfIds);
		}

		[Fact]
		public void An_unresolvable_card_voids_the_hand()
		{
			// Same rule as the in-game choice, same reason: dropping one card would slide every
			// number onto its neighbour. Not resolving is also transient while HearthDb loads, so
			// returning null lets the next poll retry.
			Assert.Null(MulliganWatcher.BuildMulliganPlan(
				Hand(("CS2_182", 1), ("NOT_A_REAL_CARD", 2)), PriestHero, null));
		}

		[Fact]
		public void Nothing_is_shown_without_a_hand()
		{
			Assert.Null(MulliganWatcher.BuildMulliganPlan(null, PriestHero, null));
			Assert.Null(MulliganWatcher.BuildMulliganPlan(Hand(), PriestHero, null));
		}

		[Fact]
		public void Nothing_is_shown_without_a_class()
		{
			// The keep statistics are per class. With no class the honest options are pooled numbers
			// — which answer a different question than the screen asks — or nothing, and nothing wins.
			Assert.Null(MulliganWatcher.BuildMulliganPlan(Hand(("CS2_182", 1)), null, null));
		}

		[Fact]
		public void The_class_falls_back_to_the_hero_power()
		{
			var plan = MulliganWatcher.BuildMulliganPlan(Hand(("CS2_182", 1)), null, "AV_207p");
			Assert.Equal(CardClass.PRIEST, plan!.Args.DeckClass);
		}

		[Fact]
		public void The_signature_follows_the_hand_so_a_redraw_is_not_deduped()
		{
			// Kept vs replaced cards are a different hand and must re-render; the same hand polled
			// twice a second must not.
			var first = MulliganWatcher.BuildMulliganPlan(
				Hand(("CS2_182", 1), ("CS2_172", 2)), PriestHero, null);
			var same = MulliganWatcher.BuildMulliganPlan(
				Hand(("CS2_182", 1), ("CS2_172", 2)), PriestHero, null);
			var redrawn = MulliganWatcher.BuildMulliganPlan(
				Hand(("CS2_182", 1), ("CS2_200", 2)), PriestHero, null);

			Assert.Equal(first!.Signature, same!.Signature);
			Assert.NotEqual(first.Signature, redrawn!.Signature);
		}
	}
}
