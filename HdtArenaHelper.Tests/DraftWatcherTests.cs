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
	}
}
