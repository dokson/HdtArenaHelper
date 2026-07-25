using Xunit;

namespace HdtArenaHelper.Training.Tests
{
	/// <summary>
	/// The shrink factor the runtime's <c>ModelOnlyShrink</c> is set from. Pinned because the number
	/// itself is not the point — the DERIVATION is: it must be a ratio of calibration slopes, must
	/// stay inside [0,1] whatever the fit produces, and must refuse to answer when either slope is
	/// unusable, so a bad refit cannot silently propose a shrink of 0 or 5.
	/// </summary>
	public class HoldoutReportTests
	{
		[Fact]
		public void Shrink_is_the_ratio_of_the_thin_slope_to_the_random_slope()
		{
			// The measured pair at the time of writing: 0.3102 / 0.9154 = 0.33887, which the runtime
			// constant rounds to 0.34.
			Assert.Equal(0.33887, HoldoutReport.ShrinkFromSlopes(0.3102, 0.9154), 5);
		}

		[Fact]
		public void Shrink_is_clamped_into_zero_one()
		{
			// A thin slope ABOVE the random one would mean the model does better where it has less
			// data — noise, not a licence to amplify. And a negative slope (anti-correlated
			// predictions) must not turn into a negative shrink, which would INVERT every score.
			Assert.Equal(1.0, HoldoutReport.ShrinkFromSlopes(1.5, 0.9), 6);
			Assert.Equal(0.0, HoldoutReport.ShrinkFromSlopes(-0.4, 0.9), 6);
		}

		[Fact]
		public void Shrink_refuses_to_answer_on_an_unusable_slope()
		{
			// NaN is the caller's signal to keep the committed constant. A zero denominator is the
			// dangerous case: dividing by it would yield Infinity and clamp to 1.0, i.e. "trust the
			// model fully" from a fit that measured nothing at all.
			Assert.True(double.IsNaN(HoldoutReport.ShrinkFromSlopes(double.NaN, 0.9)));
			Assert.True(double.IsNaN(HoldoutReport.ShrinkFromSlopes(0.3, double.NaN)));
			Assert.True(double.IsNaN(HoldoutReport.ShrinkFromSlopes(0.3, 0.0)));
		}

		[Fact]
		public void The_report_exposes_the_same_value_through_its_property()
		{
			var report = new HoldoutReport(0.28, 0.09, 4.5, 2.5, 0.3102, 0.9154);
			Assert.Equal(HoldoutReport.ShrinkFromSlopes(0.3102, 0.9154), report.MeasuredModelOnlyShrink, 6);
		}
	}
}
