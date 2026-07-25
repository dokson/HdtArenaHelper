using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HearthDb;
using Newtonsoft.Json.Linq;
using HdtArenaHelper.Numerics;
using System.Runtime.CompilerServices;

// The trainer's deterministic pieces (weight rounding, metrics format, the shrink
// derivation) are internal but tested: HdtArenaHelper.Training.Tests covers them.
[assembly: InternalsVisibleTo("HdtArenaHelper.Training.Tests")]


namespace HdtArenaHelper.Training
{
	/// <summary>
	/// Offline weight-fitting tool for the heuristic. Fetches real arena win-rates
	/// (HSReplay), builds a class-centered target from the DRAWN
	/// win-rate (win-rate of games where the card was actually drawn — the same, less
	/// deck-strength-confounded metric the runtime sources score with), and fits the
	/// same ridge model the plugin scores with — reusing <c>HeuristicArenaDataSource.
	/// BuildFeatures</c> so training and inference share one feature definition.
	///
	/// It writes <c>arena_weights.generated.json</c> next to the committed
	/// <c>arena_weights.json</c> and prints a weight-by-weight comparison. It never
	/// overwrites the committed file.
	/// </summary>
	internal static class Program
	{
		private static int Main(string[] args)
		{
			try
			{
				var trainingDir = FindTrainingDir();
				Console.WriteLine($"training dir: {trainingDir}");

				PayloadFetcher.SnapshotDir = Path.Combine(trainingDir, ".snapshot");
				PayloadFetcher.Offline = args.Any(a => string.Equals(a, "--offline", StringComparison.OrdinalIgnoreCase));
				Console.WriteLine(PayloadFetcher.Offline
					? $"OFFLINE: refitting from the snapshot in {PayloadFetcher.SnapshotDir} (no network)"
					: $"live fetch; snapshotting payloads to {PayloadFetcher.SnapshotDir}");

				Console.WriteLine("fetching HSReplay arena card stats...");
				var hs = JObject.Parse(PayloadFetcher.Download(TrainingConfig.HsReplayUrl));
				var pooled = TrainingRows.BuildHsReplayPooled(hs);
				Console.WriteLine($"HSReplay pooled (card,class) rows: {pooled.Count}");

				// SINGLE SOURCE since 0.1.5, when the second win-rate feed was withdrawn at its
				// provider's request. Three things went with it, each worth naming rather than
				// quietly dropping:
				//   - the target was the AVERAGE of two independent measurements, so half of each
				//     source's sampling noise cancelled. It is now one measurement;
				//   - rows had to exist in BOTH feeds, which was an implicit quality filter. The
				//     population is now every HSReplay row, so thin rows that used to be excluded by
				//     the intersection are in the fit — the shrinkage and the sample-size weighting
				//     are what stands between them and the weights;
				//   - the leave-one-source-out backtest of the class tier list is gone outright.
				//     Nothing cross-checks the hero-pick ranking now.
				var rows = pooled
					.Select(p => new Row(p.CardId, p.Class, p.WrC, p.NumGames, p.WrC))
					.ToList();
				Console.WriteLine($"rows: {rows.Count} (single source)");
				if(rows.Count < 100)
				{
					Console.Error.WriteLine("too few rows; aborting.");
					return 1;
				}

				// Design matrix from the SHARED feature extraction.
				var featureRows = rows
					.Select(r => HeuristicArenaDataSource.BuildFeatures(Cards.All[r.CardId]))
					.ToList();
				var allNames = featureRows
					.SelectMany(f => f.Keys)
					.Distinct()
					.OrderBy(k => k, StringComparer.Ordinal)
					.ToList();

				// Support = rows where the feature is actually present and non-zero.
				var support = allNames.ToDictionary(
					k => k,
					k => featureRows.Count(f => f.TryGetValue(k, out var v) && v != 0));

				var featureNames = new List<string>();
				var dropped = new List<string>();
				foreach(var k in allNames)
				{
					if(TrainingConfig.RedundantFeatures.Contains(k))
						dropped.Add($"{k}(redundant)");
					else if(TrainingConfig.IsEstimatedIndicator(k) && support[k] < TrainingConfig.MinFeatureSupport)
						dropped.Add($"{k}(n={support[k]})");
					else
						featureNames.Add(k);
				}
				Console.WriteLine($"features: {featureNames.Count} fitted, {dropped.Count} dropped");
				Console.WriteLine("  dropped: " + string.Join(", ", dropped));

				var x = new double[rows.Count][];
				var y = new double[rows.Count];
				var w = new double[rows.Count];
				for(var i = 0; i < rows.Count; i++)
				{
					var f = featureRows[i];
					var vec = new double[featureNames.Count];
					for(var j = 0; j < featureNames.Count; j++)
						vec[j] = f.TryGetValue(featureNames[j], out var v) ? v : 0.0;
					x[i] = vec;
					y[i] = rows[i].YAvg;
					w[i] = Math.Sqrt(rows[i].NumGames);
				}

				// Normalize the sample weights to mean 1. Weighted least squares is invariant to
				// weight SCALE, but the ridge penalty is not: alpha is added raw to the diagonal
				// of XcᵀW·Xc, so unnormalized sqrt(games) weights inflated that diagonal by ~1e4
				// and made any sane alpha invisible. Worse, Σw grows every week as games
				// accumulate, so the effective regularization was silently drifting between
				// refits. Normalizing puts alpha on a stable, comparable scale.
				var wMean = w.Average();
				if(wMean > 0)
					for(var i = 0; i < w.Length; i++) w[i] /= wMean;

				var before = Ridge.PenaltyDiagnostics(x, w.Select(v => v * wMean).ToArray(), TrainingConfig.LegacyAlpha);
				Console.WriteLine($"penalty BEFORE normalizing weights: alpha={TrainingConfig.LegacyAlpha} vs mean(diag)=" +
					$"{before.MeanDiag:0.#}  -> shrinkage {before.Shrinkage:0.000000}, " +
					$"df_eff={before.DfEff:0.0}/{featureNames.Count}");

				var cv = ModelSelection.SelectAlphaByCv(x, y, w, rows);
				var alpha = cv.Alpha;

				var after = Ridge.PenaltyDiagnostics(x, w, alpha);
				Console.WriteLine($"penalty AFTER: alpha={alpha:0.###} vs mean(diag)={after.MeanDiag:0.#}" +
					$"  -> shrinkage {after.Shrinkage:0.000000}, df_eff={after.DfEff:0.0}/{featureNames.Count}");

				var holdout = ModelSelection.EvaluateHoldouts(x, y, w, rows, alpha);

				Console.WriteLine();
				Console.WriteLine($"fitting ridge (alpha={alpha:0.###}) on {rows.Count} rows, {featureNames.Count} features...");
				var (raw, icept) = Ridge.FitRidgeStandardized(x, y, w, alpha);

				var generated = WeightsFile.RoundWeights(featureNames, raw);
				var intercept = Math.Round(icept, 2);
				// Display anchor: the median raw score of the draftable pool, shipped in
				// the json so the plugin maps the median card to 50 without a hardcoded
				// constant that would go stale on the next re-fit.
				var anchor = WeightsFile.ComputeAnchorMedianRaw(generated, intercept);
				var sigma = WeightsFile.ComputeAnchorSigmaRaw(generated, intercept, anchor);
				Console.WriteLine($"display anchor (pool median raw): {anchor:+0.00;-0.00}, " +
					$"robust sigma: {sigma:0.0000} -> 15 pts per robust SD");

				var generatedPath = Path.Combine(trainingDir, "arena_weights.generated.json");
				WeightsFile.WriteWeights(generatedPath, intercept, anchor, sigma, generated,
					alpha, rows.Count, rows.Select(r => r.CardId).Distinct().Count());
				Console.WriteLine($"wrote {generatedPath}");

				var committedPath = Path.Combine(trainingDir, "arena_weights.json");
				WeightsFile.Compare(committedPath, intercept, anchor, generated);
				WeightsFile.PrintGoldenScores(generated, intercept, anchor, sigma);

				// Per-coefficient sampling error, and the noise floor for the release gate. Both come
				// from one case bootstrap over CARDS: it turns a raw weight diff (mixed units, no notion
				// of noise) into "did this move more than resampling the same data would".
				Console.WriteLine();
				Console.WriteLine($"bootstrapping {TrainingConfig.BootstrapReplicates} card-resampled refits...");
				var boot = Bootstrap.Run(x, y, w, rows, featureNames, alpha);
				ReportCoefficientStability(featureNames, raw, boot);

				var metrics = BuildMetrics(cv, holdout, boot, rows, featureNames, dropped,
					generated, intercept, committedPath);
				metrics.Write(Path.Combine(trainingDir, "metrics.json"));
				Console.WriteLine($"pick flips vs committed: {metrics.PickFlipRate:P2} of same-class " +
					$"triples (resampling noise floor {metrics.MaterialityThreshold:P2}); " +
					$"per-card score volatility p95 {metrics.PerCardShiftP95:0.0} pts");
				Console.WriteLine(metrics.MaterialChange
					? "MATERIAL: this refit changes more recommendations than noise would - worth a review."
					: "NOT MATERIAL: the recommendations are within resampling noise of the committed ones.");
				return 0;
			}
			catch(Exception ex)
			{
				Console.Error.WriteLine("FAILED: " + ex);
				return 1;
			}
		}

		// A coefficient is only worth reporting when its magnitude clears its own sampling error.
		// Printing the error beside the weight is what lets a reviewer see "-2.92 +/- 1.73" for what
		// it is, instead of having to intuit that three supporting cards cannot support that number.
		private static void ReportCoefficientStability(IReadOnlyList<string> featureNames,
			double[] raw, BootstrapResult boot)
		{
			Console.WriteLine();
			Console.WriteLine($"{"feature",-22}   {"weight",8}   {"se",6}   {"|w|/se",7}   signCons");
			for(var j = 0; j < featureNames.Count; j++)
			{
				var st = boot.Coefficients[featureNames[j]];
				var ratio = st.StdErr > 0 ? Math.Abs(raw[j]) / st.StdErr : double.NaN;
				var flag = ratio < 2 ? "  <-- within noise" : "";
				Console.WriteLine($"{featureNames[j],-22}   {raw[j],8:+0.00;-0.00}   {st.StdErr,6:0.00}" +
					$"   {ratio,7:0.0}   {st.SignConsistency,8:0.00}{flag}");
			}
			Console.WriteLine($"release-gate noise floor (p95 of the bootstrap null): " +
				$"{boot.MaterialityThreshold:P2} of picks flip on resampling alone");
		}

		private static RunMetrics BuildMetrics(
			(double Alpha, double Rho, double Se) cv, HoldoutReport holdout, BootstrapResult boot,
			IReadOnlyList<Row> rows, IReadOnlyList<string> featureNames, IReadOnlyList<string> dropped,
			IDictionary<string, double> generated, double intercept, string committedPath)
		{
			var m = new RunMetrics
			{
				Alpha = cv.Alpha,
				Rows = rows.Count,
				Cards = rows.Select(r => r.CardId).Distinct().Count(),
				FeaturesFitted = featureNames.Count,
				FeaturesDropped = dropped.ToList(),
				CvRho = cv.Rho,
				CvRhoSe = cv.Se,
				HoldoutRandomRho = holdout.RandomRho,
				HoldoutThinRho = holdout.ThinRho,
				HoldoutThinMae = holdout.ThinMae,
				HoldoutRestMae = holdout.RestMae,
				HoldoutThinSlope = holdout.ThinSlope,
				HoldoutRandomSlope = holdout.RandomSlope,
				ModelOnlyShrinkMeasured = holdout.MeasuredModelOnlyShrink,
				MaterialityThreshold = boot.MaterialityThreshold,
				PerCardShiftP95 = boot.PerCardShiftP95
			};
			foreach(var name in featureNames)
			{
				var st = boot.Coefficients[name];
				if(st.StdErr > 0 && Math.Abs(generated.TryGetValue(name, out var v) ? v : 0.0) < 2 * st.StdErr)
					m.UnreliableCoefficients.Add(name);
			}

			// Materiality in OUTPUT space: how far a shown 0-100 score moves versus the committed
			// weights. Comparing serialized files byte-for-byte never matches (the target is live
			// data), and comparing raw coefficients mixes units; this measures the only thing a user
			// can perceive, against a threshold the bootstrap derived rather than one we guessed.
			var committed = WeightsFile.TryReadCommitted(committedPath);
			// FAIL CLOSED. If the committed weights cannot be read (file moved, renamed, or its
			// json reshaped) there is nothing to compare against — and the old code left
			// material_change at its default `false`, so CI printed GATE PASS and suppressed the
			// PR. The one run where a retrain matters most was the one that went silent, green.
			// No comparison means "assume it matters".
			m.CommittedReadable = committed != null;
			if(committed == null)
			{
				m.MaterialChange = true;
				Console.Error.WriteLine(
					$"WARNING: could not read {committedPath}; treating this refit as material.");
			}
			else
			{
				var (pool, poolClasses) = Bootstrap.BuildPoolWithClasses(featureNames);
				var triples = Bootstrap.BuildTriples(poolClasses);
				double[] Vector(IDictionary<string, double> wts) => featureNames
					.Select(n => wts.TryGetValue(n, out var v) ? v : 0.0).ToArray();
				var now = Bootstrap.DisplayScores(pool, Vector(generated), intercept);
				var before = Bootstrap.DisplayScores(pool, Vector(committed.Value.Weights),
					committed.Value.Intercept);
				var shifts = new double[now.Length];
				for(var i = 0; i < now.Length; i++) shifts[i] = Math.Abs(now[i] - before[i]);
				m.DisplayShiftP95 = BootstrapResult.Percentile(shifts, 0.95);
				m.DisplayShiftMax = shifts.Length == 0 ? 0 : shifts.Max();
				m.PickFlipRate = Bootstrap.PickFlipRate(triples, now, before);
				// Deliberately NOT gated on MaterialityThreshold (the bootstrap null). That null is
				// ~31% of picks — see REPORT.md — so calibrating on it would mark every real refit
				// "not material" and silently keep stale weights, which is worse than PR noise. The
				// null is reported as context; the decision uses an absolute, interpretable rate.
				m.MaterialChange = m.PickFlipRate > TrainingConfig.MaterialFlipRate;
			}
			return m;
		}

		private static string FindTrainingDir()
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while(dir != null && !File.Exists(Path.Combine(dir.FullName, "HdtArenaHelper.sln")))
				dir = dir.Parent;
			if(dir == null)
				throw new DirectoryNotFoundException("could not locate repo root (HdtArenaHelper.sln).");
			return Path.Combine(dir.FullName, "HdtArenaHelper.Training");
		}
	}
}
