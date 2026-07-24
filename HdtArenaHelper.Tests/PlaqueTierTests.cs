using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// Pure presentation logic (the score->plaque-tier mapping) is unit-tested here; the
	/// WPF rendering and client geometry are verified on a live HDT client (they can't run
	/// headless).
	/// </summary>
	public class PlaqueTierTests
	{
		[Theory]
		[InlineData(100, 5)]
		[InlineData(80, 5)]
		[InlineData(79.9, 4)]
		[InlineData(65, 4)]
		[InlineData(64.9, 3)]
		[InlineData(50, 3)]
		[InlineData(49.9, 2)]
		[InlineData(40, 2)]
		[InlineData(39.9, 1)]
		[InlineData(0, 1)]
		[InlineData(-5, 1)]
		public void FromScore_buckets_the_blend_into_1_to_5(double score, int expectedLevel)
		{
			Assert.Equal(expectedLevel, PlaqueTier.FromScore(score));
		}
	}
}
