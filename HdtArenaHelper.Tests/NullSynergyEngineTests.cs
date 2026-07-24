using Xunit;

namespace HdtArenaHelper.Tests
{
	public class NullSynergyEngineTests
	{
		[Fact]
		public void Returns_zero_with_no_drafted_cards()
		{
			var engine = new NullSynergyEngine();

			Assert.Equal(0, engine.GetSynergy(offeredDbfId: 100, new int[0]).Bonus);
		}

		[Fact]
		public void Returns_zero_regardless_of_the_drafted_deck()
		{
			var engine = new NullSynergyEngine();

			Assert.Equal(0, engine.GetSynergy(100, new[] { 1, 2, 3, 100, 100 }).Bonus);
		}
	}
}
