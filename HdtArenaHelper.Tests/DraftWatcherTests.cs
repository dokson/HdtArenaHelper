using HearthDb;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class DraftWatcherTests
	{
		[Fact]
		public void ToDbfId_resolves_a_real_card_id()
		{
			var dbf = DraftWatcher.ToDbfId("CS2_182"); // Chillwind Yeti

			Assert.NotEqual(0, dbf);
			Assert.Equal(Cards.All["CS2_182"].DbfId, dbf);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("NOT_A_REAL_CARD_ID")]
		public void ToDbfId_returns_zero_for_missing_or_unknown_ids(string? cardId)
		{
			Assert.Equal(0, DraftWatcher.ToDbfId(cardId));
		}

		[Fact]
		public void ToClass_resolves_the_drafted_hero_class()
		{
			Assert.Equal(HearthDb.Enums.CardClass.WARRIOR, DraftWatcher.ToClass("HERO_01"));
			Assert.Equal(HearthDb.Enums.CardClass.MAGE, DraftWatcher.ToClass("HERO_08"));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("NOT_A_REAL_CARD_ID")]
		public void ToClass_returns_invalid_before_the_hero_pick(string? heroCardId)
		{
			Assert.Equal(HearthDb.Enums.CardClass.INVALID, DraftWatcher.ToClass(heroCardId));
		}

		// Choices linger in client memory outside the draft (landing screen, mid-run):
		// only draft/redraft states may show the overlay.
		[Theory]
		[InlineData(HearthMirror.Enums.ArenaSessionState.DRAFTING, true)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.REDRAFTING, true)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.MIDRUN_REDRAFT_PENDING, true)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.MIDRUN, false)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.EDITING_DECK, false)]
		[InlineData(HearthMirror.Enums.ArenaSessionState.INVALID, false)]
		public void Only_draft_and_redraft_states_are_active(HearthMirror.Enums.ArenaSessionState state, bool active)
		{
			Assert.Equal(active, DraftWatcher.IsActiveDraftState(state));
		}
	}
}
