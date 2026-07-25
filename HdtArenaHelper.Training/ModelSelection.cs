using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb;
using HearthDb.Enums;
using HdtArenaHelper.Numerics;

namespace HdtArenaHelper.Training
{
	/// <summary>
	/// Chooses the ridge penalty by cross-validation grouped BY CARD, and measures the model on
	/// the population it actually serves - cards with little or no win-rate data - instead of on
	/// the well-sampled cards it happens to be fitted on.
	/// </summary>
	internal readonly struct HoldoutReport
	{
		public readonly double RandomRho;
		public readonly double ThinRho;
		public readonly double ThinMae;
		public readonly double RestMae;
		/// <summary>Slope of held-out truth on prediction for the thin decile (see <see cref="ModelSelection.EvaluateHoldouts"/>).</summary>
		public readonly double ThinSlope;
		/// <summary>The same slope on the random holdout — the regime the display scale was fitted on.</summary>
		public readonly double RandomSlope;
		public HoldoutReport(double randomRho, double thinRho, double thinMae, double restMae,
			double thinSlope, double randomSlope)
		{
			RandomRho = randomRho; ThinRho = thinRho; ThinMae = thinMae; RestMae = restMae;
			ThinSlope = thinSlope; RandomSlope = randomSlope;
		}

		/// <summary>
		/// The shrink factor a model-only score should keep, measured rather than asserted:
		/// how much of a prediction's deviation survives on unmeasured cards, RELATIVE to the
		/// regime the display mapping was calibrated on. Both slopes are needed — the raw thin
		/// slope would double-count any miscalibration the display scale already carries.
		/// NaN when either slope is unusable, so the caller keeps the committed constant.
		/// </summary>
		public double MeasuredModelOnlyShrink => ShrinkFromSlopes(ThinSlope, RandomSlope);

		internal static double ShrinkFromSlopes(double thinSlope, double randomSlope)
			=> double.IsNaN(thinSlope) || double.IsNaN(randomSlope) || Math.Abs(randomSlope) < 1e-9
				? double.NaN
				: Math.Max(0.0, Math.Min(1.0, thinSlope / randomSlope));
	}

	internal static class ModelSelection
	{
		/// <summary>
		/// Grouped repeated CV over the alpha grid. Folds are grouped BY CARD, never by row: a
		/// neutral card contributes one row per class, so a random row split would put the same
		/// card on both sides and leak. The metric is Spearman computed WITHIN each class bucket
		/// and then averaged — the product decision is "rank three cards of one class", and a
		/// pooled correlation is partly a between-class composition statistic. Selection uses the
		/// 1-SE rule biased toward the LARGER alpha: the curve is expected to be flat over orders
		/// of magnitude, and in a flat region more shrinkage is free stability.
		/// </summary>
		internal static (double Alpha, double Rho, double Se) SelectAlphaByCv(double[][] x, double[] y, double[] w,
			IReadOnlyList<Row> rows)
		{
			var cards = rows.Select(r => r.CardId).Distinct().ToList();
			Console.WriteLine($"CV: {TrainingConfig.CvFolds}-fold grouped by card ({cards.Count} cards, " +
				$"{rows.Count} rows), {TrainingConfig.CvRepeats} repeats, {TrainingConfig.AlphaGrid.Length} alphas");

			var perAlpha = new List<double>[TrainingConfig.AlphaGrid.Length];
			for(var a = 0; a < TrainingConfig.AlphaGrid.Length; a++)
				perAlpha[a] = new List<double>();

			for(var rep = 0; rep < TrainingConfig.CvRepeats; rep++)
			{
				var rng = new Random(TrainingConfig.CvSeed + rep);
				var shuffled = cards.OrderBy(_ => rng.Next()).ToList();
				var foldOf = new Dictionary<string, int>(shuffled.Count);
				for(var i = 0; i < shuffled.Count; i++)
					foldOf[shuffled[i]] = i % TrainingConfig.CvFolds;

				for(var a = 0; a < TrainingConfig.AlphaGrid.Length; a++)
				{
					var pred = new double[rows.Count];
					for(var fold = 0; fold < TrainingConfig.CvFolds; fold++)
					{
						var trIdx = new List<int>();
						var teIdx = new List<int>();
						for(var i = 0; i < rows.Count; i++)
							(foldOf[rows[i].CardId] == fold ? teIdx : trIdx).Add(i);
						if(trIdx.Count == 0 || teIdx.Count == 0)
							continue;

						// Standardization and weighted centering happen inside the fit, so
						// calling it per fold keeps them inside the fold (no scaler leak).
						var (raw, icept) = Ridge.FitRidgeStandardized(
							trIdx.Select(i => x[i]).ToArray(),
							trIdx.Select(i => y[i]).ToArray(),
							trIdx.Select(i => w[i]).ToArray(),
							TrainingConfig.AlphaGrid[a]);
						foreach(var i in teIdx)
						{
							var s = icept;
							for(var j = 0; j < raw.Length; j++) s += raw[j] * x[i][j];
							pred[i] = s;
						}
					}
					perAlpha[a].Add(MeanWithinClassSpearman(rows, pred, y));
				}
			}

			Console.WriteLine();
			Console.WriteLine($"{"alpha",10}   {"withinClassRho",14}   {"se",6}");
			var means = new double[TrainingConfig.AlphaGrid.Length];
			var ses = new double[TrainingConfig.AlphaGrid.Length];
			for(var a = 0; a < TrainingConfig.AlphaGrid.Length; a++)
			{
				means[a] = perAlpha[a].Average();
				ses[a] = Stats.StdErr(perAlpha[a]);
				Console.WriteLine($"{TrainingConfig.AlphaGrid[a],10:0.###}   {means[a],14:0.0000}   {ses[a],6:0.0000}");
			}

			var bestIdx = 0;
			for(var a = 1; a < TrainingConfig.AlphaGrid.Length; a++)
				if(means[a] > means[bestIdx]) bestIdx = a;
			var threshold = means[bestIdx] - ses[bestIdx];
			var chosen = bestIdx;
			for(var a = TrainingConfig.AlphaGrid.Length - 1; a >= 0; a--)
			{
				if(means[a] >= threshold) { chosen = a; break; }
			}
			Console.WriteLine($"CV best alpha={TrainingConfig.AlphaGrid[bestIdx]:0.###} (rho={means[bestIdx]:0.0000}); " +
				$"1-SE rule picks alpha={TrainingConfig.AlphaGrid[chosen]:0.###} (rho={means[chosen]:0.0000}, " +
				$"threshold {threshold:0.0000})");
			return (TrainingConfig.AlphaGrid[chosen], means[chosen], ses[chosen]);
		}

		/// <summary>
		/// The measurement that actually matters, and the one that was missing. This model is a
		/// BACKSTOP: it only ever decides a pick for cards the win-rate feeds have no data for.
		/// But it is fitted and cross-validated on the cards that DO have data — by construction
		/// the popular, well-sampled, already-covered ones. That is textbook covariate shift, so
		/// a random-fold score answers the wrong question.
		///
		/// Proxies for "a card with no data", each evaluated against a random holdout of the same
		/// size so the comparison isolates the shift rather than the holdout size:
		///   - thinnest data: the bottom decile of cards by games,
		///   - an unseen release: leave-one-SET-out for the larger sets.
		/// A large gap versus the random baseline means the backstop is weaker exactly where it
		/// is load-bearing, and no amount of coefficient tuning fixes that.
		/// </summary>
		internal static HoldoutReport EvaluateHoldouts(double[][] x, double[] y, double[] w,
			IReadOnlyList<Row> rows, double alpha)
		{
			Console.WriteLine();
			Console.WriteLine("holdout evaluation (pooled Spearman on held-out rows, alpha=" +
				alpha.ToString("0.###") + "):");

			// Per-card games: a card contributes one row per class, so aggregate before ranking.
			var gamesByCard = rows
				.GroupBy(r => r.CardId)
				.ToDictionary(g => g.Key, g => g.Sum(r => (long)r.NumGames));
			var cards = gamesByCard.Keys.ToList();
			var holdoutSize = Math.Max(20, cards.Count / 10);

			// Baseline: random cards, same holdout size, averaged over repeats.
			var randomScores = new List<double>();
			for(var rep = 0; rep < TrainingConfig.CvRepeats; rep++)
			{
				var rng = new Random(TrainingConfig.CvSeed + 1000 + rep);
				var held = new HashSet<string>(cards.OrderBy(_ => rng.Next()).Take(holdoutSize));
				var rho = FitAndScoreHoldout(x, y, w, rows, alpha, held);
				if(!double.IsNaN(rho))
					randomScores.Add(rho);
			}
			var baseline = randomScores.Count == 0 ? double.NaN : randomScores.Average();
			Console.WriteLine($"  {"random cards (baseline)",-34} n={holdoutSize,4}  rho={baseline,7:0.0000}" +
				$"  (se {Stats.StdErr(randomScores):0.0000})");

			// Thinnest-data cards: the deployment population's closest available proxy.
			var thinnest = new HashSet<string>(
				cards.OrderBy(c => gamesByCard[c]).Take(holdoutSize));
			var thinRho = FitAndScoreHoldout(x, y, w, rows, alpha, thinnest);
			var maxThinGames = thinnest.Max(c => gamesByCard[c]);
			Console.WriteLine($"  {"lowest-games decile",-34} n={holdoutSize,4}  rho={thinRho,7:0.0000}" +
				$"  (up to {maxThinGames} games)");
			if(!double.IsNaN(thinRho) && !double.IsNaN(baseline))
				Console.WriteLine($"  -> covariate-shift gap vs baseline: {thinRho - baseline:+0.0000;-0.0000}");

			// LOST IN 0.1.5, and worth stating rather than quietly dropping. Is the thin-decile gap
			// a MODEL failure or an unreliable LABEL? A thin card's measured rate is itself noisy
			// (a few hundred games is ~3pp of standard error), and correlating against a noisy truth
			// attenuates rho however good the model is. Two independent feeds measuring the same
			// quantity let their agreement estimate the target's reliability, and disattenuating by
			// sqrt(reliability) separated the two explanations. With one feed there is no second
			// measurement, so the reliability of the label is no longer estimable at all: the thin
			// numbers below can no longer be told apart from a noisy target, and must be read as an
			// upper bound on how bad the model is, not as a measurement of it. Restoring this is one
			// of the concrete things a second (welcome) source would buy back.

			var thinRows = new List<int>();
			var thickRows = new List<int>();
			for(var i = 0; i < rows.Count; i++)
				(thinnest.Contains(rows[i].CardId) ? thinRows : thickRows).Add(i);

			// Third explanation to rule out: RANGE RESTRICTION. Rank correlation collapses when
			// the held-out group's true spread is narrow, even with a perfect model — and thin
			// cards are thin partly because they are unpopular, which correlates with being bad.
			// So report the target's spread and the model's absolute error side by side: a much
			// smaller spread with a comparable MAE means "cannot RANK within a narrow band",
			// which is far less alarming than "predicts the wrong value".
			var thinSd = Stats.StdDev(thinRows.Select(i => y[i]));
			var thickSd = Stats.StdDev(thickRows.Select(i => y[i]));
			var thinMae = HoldoutMae(x, y, w, rows, alpha, thinnest, true);
			var thickMae = HoldoutMae(x, y, w, rows, alpha, thinnest, false);
			Console.WriteLine($"  target spread / model error:");
			Console.WriteLine($"      thin  sd(y)={thinSd:0.000}  MAE={thinMae:0.000}");
			Console.WriteLine($"      rest  sd(y)={thickSd:0.000}  MAE={thickMae:0.000}");

			// CALIBRATION SLOPE — what the runtime's ModelOnlyShrink must actually be set from.
			// A rank correlation cannot answer "how much should we believe this number", and an
			// earlier version of that constant was derived from a ratio of correlations, which is
			// not a shrink factor at all. Regress held-out truth on prediction: the slope IS the
			// optimal linear shrink. Reported for the thin decile AND for the random holdout,
			// because the display mapping is calibrated on the measured regime — the constant the
			// runtime needs is the RATIO, or any miscalibration already in the display scale would
			// be applied twice.
			var thinSlope = HoldoutCalibrationSlope(x, y, w, rows, alpha, thinnest);
			var randomSlopes = new List<double>();
			for(var rep = 0; rep < TrainingConfig.CvRepeats; rep++)
			{
				var rng = new Random(TrainingConfig.CvSeed + 1000 + rep);
				var held = new HashSet<string>(cards.OrderBy(_ => rng.Next()).Take(holdoutSize));
				var slope = HoldoutCalibrationSlope(x, y, w, rows, alpha, held);
				if(!double.IsNaN(slope))
					randomSlopes.Add(slope);
			}
			var randomSlope = randomSlopes.Count == 0 ? double.NaN : randomSlopes.Average();
			Console.WriteLine("  calibration slope (truth regressed on prediction):");
			Console.WriteLine($"      thin decile {thinSlope,7:0.0000}   random holdout {randomSlope,7:0.0000}" +
				$"  (se {Stats.StdErr(randomSlopes):0.0000})");
			Console.WriteLine("      -> measured ModelOnlyShrink = " +
				$"{HoldoutReport.ShrinkFromSlopes(thinSlope, randomSlope):0.0000}" +
				" (thin/random; set the runtime constant from THIS, not from a correlation ratio)");

			// An unseen release: the honest test of "will this score next patch's cards".
			var setOf = cards.ToDictionary(c => c, c => Cards.All[c].Set);
			foreach(var g in setOf.GroupBy(kv => kv.Value)
				.Where(g => g.Count() >= 40)
				.OrderByDescending(g => g.Count())
				.Take(4))
			{
				var held = new HashSet<string>(g.Select(kv => kv.Key));
				var rho = FitAndScoreHoldout(x, y, w, rows, alpha, held);
				Console.WriteLine($"  leave-out set {g.Key,-20} n={held.Count,4}  rho={rho,7:0.0000}");
			}

			return new HoldoutReport(baseline, thinRho, thinMae, thickMae, thinSlope, randomSlope);
		}

		/// <summary>Fit on every card outside <paramref name="held"/>, score the held-out rows.</summary>
		internal static double FitAndScoreHoldout(double[][] x, double[] y, double[] w,
			IReadOnlyList<Row> rows, double alpha, HashSet<string> held)
		{
			var p = HoldoutPredictions(x, y, w, rows, alpha, held);
			// Pooled, not within-class: a decile-sized holdout has too few cards per class for a
			// per-class correlation to mean anything. y is already class-centered, so pooling is
			// defensible here — and the random baseline uses the SAME metric, which is the point.
			return p == null ? double.NaN : Stats.Spearman(p.Value.Pred, p.Value.Actual);
		}

		/// <summary>
		/// Slope of held-out TRUTH on PREDICTION: the factor by which a prediction's deviation has
		/// to be scaled to become the conditional mean of the truth. This is the quantity a shrink
		/// constant needs, and a rank correlation is not a substitute for it — a correlation is
		/// symmetric and unitless, a slope answers "the model says +5, how much does truth move".
		/// </summary>
		internal static double HoldoutCalibrationSlope(double[][] x, double[] y, double[] w,
			IReadOnlyList<Row> rows, double alpha, HashSet<string> held)
		{
			var p = HoldoutPredictions(x, y, w, rows, alpha, held);
			return p == null ? double.NaN : Stats.RegressionSlope(p.Value.Pred, p.Value.Actual);
		}

		/// <summary>Fit outside <paramref name="held"/>, return the held-out (prediction, truth) pairs.</summary>
		private static (double[] Pred, double[] Actual)? HoldoutPredictions(double[][] x, double[] y,
			double[] w, IReadOnlyList<Row> rows, double alpha, HashSet<string> held)
		{
			var trIdx = new List<int>();
			var teIdx = new List<int>();
			for(var i = 0; i < rows.Count; i++)
				(held.Contains(rows[i].CardId) ? teIdx : trIdx).Add(i);
			if(trIdx.Count < 100 || teIdx.Count < 10)
				return null;

			var (raw, icept) = Ridge.FitRidgeStandardized(
				trIdx.Select(i => x[i]).ToArray(),
				trIdx.Select(i => y[i]).ToArray(),
				trIdx.Select(i => w[i]).ToArray(),
				alpha);

			var pred = new double[teIdx.Count];
			var actual = new double[teIdx.Count];
			for(var t = 0; t < teIdx.Count; t++)
			{
				var i = teIdx[t];
				var s = icept;
				for(var j = 0; j < raw.Length; j++) s += raw[j] * x[i][j];
				pred[t] = s;
				actual[t] = y[i];
			}
			return (pred, actual);
		}

		/// <summary>
		/// Mean absolute error of the held-out-trained model, on the held-out cards
		/// (<paramref name="onHeld"/>) or on the training cards, for a like-for-like comparison
		/// that rank correlation cannot give when the two groups' spreads differ.
		/// </summary>
		internal static double HoldoutMae(double[][] x, double[] y, double[] w,
			IReadOnlyList<Row> rows, double alpha, HashSet<string> held, bool onHeld)
		{
			var trIdx = new List<int>();
			var evalIdx = new List<int>();
			for(var i = 0; i < rows.Count; i++)
			{
				var isHeld = held.Contains(rows[i].CardId);
				if(!isHeld)
					trIdx.Add(i);
				if(isHeld == onHeld)
					evalIdx.Add(i);
			}
			if(trIdx.Count < 100 || evalIdx.Count == 0)
				return double.NaN;

			var (raw, icept) = Ridge.FitRidgeStandardized(
				trIdx.Select(i => x[i]).ToArray(),
				trIdx.Select(i => y[i]).ToArray(),
				trIdx.Select(i => w[i]).ToArray(),
				alpha);

			double err = 0;
			foreach(var i in evalIdx)
			{
				var s = icept;
				for(var j = 0; j < raw.Length; j++) s += raw[j] * x[i][j];
				err += Math.Abs(s - y[i]);
			}
			return err / evalIdx.Count;
		}

		/// <summary>Spearman within each class bucket, averaged over buckets with enough cards.</summary>
		internal static double MeanWithinClassSpearman(
			IReadOnlyList<Row> rows, double[] pred, double[] y)
		{
			var byClass = new Dictionary<CardClass, List<int>>();
			for(var i = 0; i < rows.Count; i++)
			{
				if(!byClass.TryGetValue(rows[i].Class, out var list))
					byClass[rows[i].Class] = list = new List<int>();
				list.Add(i);
			}
			var rhos = new List<double>();
			foreach(var kv in byClass)
			{
				if(kv.Value.Count < TrainingConfig.MinClassRows)
					continue;
				var rho = Stats.Spearman(
					kv.Value.Select(i => pred[i]).ToArray(),
					kv.Value.Select(i => y[i]).ToArray());
				if(!double.IsNaN(rho))
					rhos.Add(rho);
			}
			return rhos.Count == 0 ? double.NaN : rhos.Average();
		}
	}
}
