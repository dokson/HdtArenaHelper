using System;
using System.Linq;
using Xunit;

namespace HdtArenaHelper.Numerics.Tests
{
	/// <summary>
	/// The descriptive statistics the trainer's conclusions rest on. Pinned against known-truth
	/// values, never against their own output. <see cref="Stats.RegressionSlope"/> gets the most
	/// attention: the runtime's model-only shrink constant is derived from it, and the mistake it
	/// exists to correct was using a CORRELATION where a slope was needed — so the tests state
	/// exactly the property that distinguishes the two.
	/// </summary>
	public class StatsTests
	{
		[Fact]
		public void Regression_slope_recovers_a_known_linear_relation()
		{
			var pred = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
			// truth = 0.5 * pred + 10: the slope is the number, the offset must not matter.
			var actual = pred.Select(p => 0.5 * p + 10).ToArray();
			Assert.Equal(0.5, Stats.RegressionSlope(pred, actual), 10);
		}

		[Fact]
		public void Regression_slope_is_asymmetric_where_a_correlation_is_not()
		{
			// THE distinction that matters. Spearman(a,b) == Spearman(b,a), so a correlation cannot
			// express "how much does truth move per unit of prediction" — it stays 1.0 no matter how
			// badly the prediction is scaled. The slope reports the 0.25 that a shrink factor needs.
			var pred = new[] { 1.0, 2.0, 3.0, 4.0 };
			var actual = pred.Select(p => 0.25 * p).ToArray();

			Assert.Equal(0.25, Stats.RegressionSlope(pred, actual), 10);
			Assert.Equal(4.0, Stats.RegressionSlope(actual, pred), 10);
			Assert.Equal(Stats.Spearman(pred, actual), Stats.Spearman(actual, pred), 10);
			Assert.Equal(1.0, Stats.Spearman(pred, actual), 10);
		}

		[Fact]
		public void Regression_slope_is_zero_when_the_prediction_carries_no_information()
		{
			// A model whose output is unrelated to truth must yield ~0, i.e. "keep none of the
			// claimed deviation" — the behaviour the thin-decile measurement is looking for.
			var pred = new[] { 1.0, 2.0, 3.0, 4.0 };
			var actual = new[] { 5.0, 5.0, 5.0, 5.0 };
			Assert.Equal(0.0, Stats.RegressionSlope(pred, actual), 10);
		}

		[Fact]
		public void Regression_slope_refuses_degenerate_input()
		{
			// No spread in the prediction: the slope is undefined, and returning 0 would read as
			// "the model is worthless" instead of "this cannot be measured".
			Assert.True(double.IsNaN(Stats.RegressionSlope(new[] { 2.0, 2.0, 2.0 }, new[] { 1.0, 5.0, 9.0 })));
			Assert.True(double.IsNaN(Stats.RegressionSlope(new[] { 1.0, 2.0 }, new[] { 1.0, 2.0 })));
			Assert.True(double.IsNaN(Stats.RegressionSlope(new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 2.0 })));
		}

		[Fact]
		public void Spearman_is_one_for_any_monotone_transform()
		{
			// Rank-based, so a nonlinear but order-preserving map must not move it. This is why the
			// trainer uses it to score rankings and NOT to derive a shrink.
			var a = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
			var b = a.Select(v => Math.Exp(v)).ToArray();
			Assert.Equal(1.0, Stats.Spearman(a, b), 10);
			Assert.Equal(-1.0, Stats.Spearman(a, a.Select(v => -v).ToArray()), 10);
		}

		[Fact]
		public void Ranks_average_over_ties()
		{
			// Values 10,20,20,30 -> ranks 1, 2.5, 2.5, 4. Without tie-averaging a tied group would
			// get an arbitrary order and the correlation would depend on input ordering.
			Assert.Equal(new[] { 1.0, 2.5, 2.5, 4.0 }, Stats.RankAverage(new[] { 10.0, 20.0, 20.0, 30.0 }));
		}

		[Fact]
		public void Std_dev_is_the_sample_form_and_undefined_below_two_points()
		{
			// n-1 denominator: values 2,4,4,4,5,5,7,9 have population sd 2 and sample sd ~2.138.
			Assert.Equal(2.13809, Stats.StdDev(new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 }), 5);
			Assert.True(double.IsNaN(Stats.StdDev(new[] { 3.0 })));
		}

		[Fact]
		public void Std_err_shrinks_with_the_square_root_of_n()
		{
			// Four copies of a sample must halve its standard error; a single observation has no
			// measurable error and must report 0 rather than NaN, because the trainer prints it.
			var one = new[] { 1.0, 2.0, 3.0, 4.0 };
			var four = one.Concat(one).Concat(one).Concat(one).ToArray();
			Assert.True(Stats.StdErr(four) < Stats.StdErr(one));
			Assert.Equal(0.0, Stats.StdErr(new[] { 7.0 }), 10);
		}
	}
}
