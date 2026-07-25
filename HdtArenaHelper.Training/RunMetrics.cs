using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace HdtArenaHelper.Training
{
	/// <summary>
	/// Machine-readable summary of one retrain, written next to the generated weights so CI can
	/// gate on values instead of scraping the console log. The log form was culture-dependent
	/// (a dev machine prints "0,44" where a runner prints "0.44") and would have mis-parsed
	/// silently one day; json with invariant formatting cannot.
	///
	/// The decision CI needs is <see cref="MaterialChange"/>: is this refit worth a human's
	/// attention? It is answered in OUTPUT space (how much would a shown score actually move)
	/// against a threshold derived from the bootstrap null, not from a hand-picked constant.
	/// </summary>
	internal sealed class RunMetrics
	{
		[JsonProperty("alpha")] public double Alpha { get; set; }
		[JsonProperty("rows")] public int Rows { get; set; }
		[JsonProperty("cards")] public int Cards { get; set; }
		[JsonProperty("features_fitted")] public int FeaturesFitted { get; set; }
		[JsonProperty("features_dropped")] public List<string> FeaturesDropped { get; set; } = new List<string>();

		[JsonProperty("cv_within_class_rho")] public double CvRho { get; set; }
		[JsonProperty("cv_within_class_rho_se")] public double CvRhoSe { get; set; }

		[JsonProperty("holdout_random_rho")] public double HoldoutRandomRho { get; set; }
		[JsonProperty("holdout_thin_rho")] public double HoldoutThinRho { get; set; }
		[JsonProperty("holdout_thin_mae")] public double HoldoutThinMae { get; set; }
		[JsonProperty("holdout_rest_mae")] public double HoldoutRestMae { get; set; }

		/// <summary>
		/// Slope of held-out truth regressed on prediction, thin decile and random holdout. The
		/// pair is what `ScoreAggregator.ModelOnlyShrink` must be set from — see
		/// <see cref="ModelOnlyShrinkMeasured"/>. Emitted so the constant can be re-derived from a
		/// refit instead of being taken on faith: the first version of it was a ratio of
		/// CORRELATIONS, which is not a shrink factor.
		/// </summary>
		[JsonProperty("holdout_thin_calibration_slope")] public double HoldoutThinSlope { get; set; }
		[JsonProperty("holdout_random_calibration_slope")] public double HoldoutRandomSlope { get; set; }
		/// <summary>thin/random slope ratio: the measured value for the runtime shrink constant.</summary>
		[JsonProperty("model_only_shrink_measured")] public double ModelOnlyShrinkMeasured { get; set; }

		/// <summary>p95 |change in shown 0-100 score| versus the committed weights.</summary>
		[JsonProperty("display_shift_p95")] public double DisplayShiftP95 { get; set; }
		/// <summary>Largest shown-score change versus the committed weights.</summary>
		[JsonProperty("display_shift_max")] public double DisplayShiftMax { get; set; }
		/// <summary>
		/// Fraction of same-class triples whose recommended pick changes versus the committed
		/// weights. This is the gate's statistic: it is what a user would actually notice, and
		/// because two cards scored by one fit share their coefficient error it is far tighter
		/// than any per-card score comparison.
		/// </summary>
		[JsonProperty("pick_flip_rate")] public double PickFlipRate { get; set; }
		/// <summary>The flip rate resampling the SAME data produces 95% of the time — the noise floor.</summary>
		[JsonProperty("materiality_threshold")] public double MaterialityThreshold { get; set; }
		/// <summary>True when the refit flipped more picks than resampling noise would.</summary>
		[JsonProperty("material_change")] public bool MaterialChange { get; set; }
		/// <summary>Per-card score volatility under resampling; context for why the gate is on flips.</summary>
		[JsonProperty("per_card_shift_p95")] public double PerCardShiftP95 { get; set; }

		/// <summary>
		/// False when the committed weights could not be read, i.e. nothing could be compared.
		/// <see cref="MaterialChange"/> is then forced true: a gate that cannot measure must not
		/// report "nothing changed".
		/// </summary>
		[JsonProperty("committed_readable")] public bool CommittedReadable { get; set; }

		/// <summary>Coefficients whose magnitude does not clear twice their own sampling error.</summary>
		[JsonProperty("unreliable_coefficients")] public List<string> UnreliableCoefficients { get; set; } = new List<string>();

		public void Write(string path)
		{
			var json = JsonConvert.SerializeObject(this, new JsonSerializerSettings
			{
				Formatting = Formatting.Indented,
				Culture = CultureInfo.InvariantCulture
			});
			// LF explicitly: Formatting.Indented uses Environment.NewLine, which on Windows would
			// write CRLF into a repo that normalizes to LF everywhere else.
			File.WriteAllText(path, json.Replace("\r\n", "\n"));
			Console.WriteLine($"wrote {path}");
		}
	}
}
