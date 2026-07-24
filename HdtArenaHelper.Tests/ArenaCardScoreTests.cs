using Xunit;

namespace HdtArenaHelper.Tests
{
	public class ArenaCardScoreTests
	{
		[Fact]
		public void Round_trips_its_fields()
		{
			var s = new ArenaCardScore(dbfId: 42, includedWinrate: 53.5, includedPopularity: 4.2, games: 1200);

			Assert.Equal(42, s.DbfId);
			Assert.Equal(53.5, s.IncludedWinrate);
			Assert.Equal(4.2, s.IncludedPopularity);
			Assert.Equal(1200, s.Games);
		}

		[Fact]
		public void Games_defaults_to_null_when_omitted()
		{
			var s = new ArenaCardScore(1, 50.0, 1.0);

			Assert.Null(s.Games);
		}

		[Fact]
		public void DisplayScore_is_the_included_winrate()
		{
			var s = new ArenaCardScore(1, includedWinrate: 57.0, includedPopularity: null);

			Assert.Equal(57.0, s.DisplayScore);
		}

		[Fact]
		public void DisplayScore_is_zero_when_winrate_unknown()
		{
			var s = new ArenaCardScore(1, includedWinrate: null, includedPopularity: null);

			Assert.Equal(0, s.DisplayScore);
		}
	}
}
