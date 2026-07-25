using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Xunit;

namespace HdtArenaHelper.Training.Tests
{
	/// <summary>
	/// metrics.json is what CI gates on, so its FORMAT is a contract. Both properties tested here
	/// exist because the console log they replaced got them wrong: a dev machine printed "0,44"
	/// where a runner printed "0.44", and Formatting.Indented emits Environment.NewLine, which on
	/// Windows would write CRLF into a repo normalized to LF.
	/// </summary>
	public class RunMetricsTests
	{
		private static string WriteToTemp(RunMetrics metrics)
		{
			var path = Path.Combine(Path.GetTempPath(), "metrics_" + Guid.NewGuid().ToString("N") + ".json");
			metrics.Write(path);
			try
			{
				return File.ReadAllText(path);
			}
			finally
			{
				File.Delete(path);
			}
		}

		[Fact]
		public void Written_json_uses_LF_only()
		{
			var json = WriteToTemp(new RunMetrics { Alpha = 300, Rows = 10 });

			Assert.Contains("\n", json);
			Assert.DoesNotContain("\r", json);
		}

		[Fact]
		public void Decimals_are_invariant_regardless_of_the_current_culture()
		{
			// Run the write under a comma-decimal culture: the point is that the FILE does not
			// change, because CI parses it with an invariant reader.
			var previous = Thread.CurrentThread.CurrentCulture;
			try
			{
				Thread.CurrentThread.CurrentCulture = new CultureInfo("it-IT");
				var json = WriteToTemp(new RunMetrics { CvRho = 0.1924, ModelOnlyShrinkMeasured = 0.3388 });

				Assert.Contains("0.1924", json);
				Assert.Contains("0.3388", json);
				Assert.DoesNotContain("0,1924", json);
			}
			finally
			{
				Thread.CurrentThread.CurrentCulture = previous;
			}
		}

		[Fact]
		public void The_calibration_slopes_are_emitted_so_the_shrink_constant_can_be_re_derived()
		{
			// ScoreAggregator.ModelOnlyShrink must be re-derivable from a refit rather than argued
			// about: if these keys ever stop being written, the next person has no measurement and
			// the old correlation-ratio reasoning becomes tempting again.
			var json = WriteToTemp(new RunMetrics
			{
				HoldoutThinSlope = 0.31,
				HoldoutRandomSlope = 0.92,
				ModelOnlyShrinkMeasured = 0.34
			});

			Assert.Contains("holdout_thin_calibration_slope", json);
			Assert.Contains("holdout_random_calibration_slope", json);
			Assert.Contains("model_only_shrink_measured", json);
		}
	}
}
