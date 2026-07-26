using System;
using System.Collections.Generic;
using System.Linq;
using HdtArenaHelper.Numerics;
using HearthDb;
using HearthDb.Enums;

namespace HdtArenaHelper.Training
{
	/// <summary>One coefficient's sampling behaviour under resampling.</summary>
	internal readonly struct CoefficientStability
	{
		public readonly double StdErr;
		/// <summary>Fraction of resamples whose sign matches the point estimate. For a sparse
		/// indicator this says far more than the standard error: 0.5 means "we cannot even tell
		/// which direction this effect points".</summary>
		public readonly double SignConsistency;
		public CoefficientStability(double stdErr, double signConsistency)
		{
			StdErr = stdErr;
			SignConsistency = signConsistency;
		}
	}

	internal sealed class BootstrapResult
	{
		public Dictionary<string, CoefficientStability> Coefficients { get; }
		/// <summary>
		/// The NULL distribution for the release gate: the same-class PICK-FLIP rate between each
		/// resampled fit and the point estimate. Resampling the SAME data flips this fraction of
		/// picks, so a weekly refit only means something when it flips more.
		/// </summary>
		public double[] NullFlipRate { get; }

		/// <summary>p95 of |display-score change| per card under resampling — reported for context:
		/// it is the number that makes an absolute-score gate impossible.</summary>
		public double PerCardShiftP95 { get; }

		public BootstrapResult(Dictionary<string, CoefficientStability> coefficients,
			double[] nullFlipRate, double perCardShiftP95)
		{
			Coefficients = coefficients;
			NullFlipRate = nullFlipRate;
			PerCardShiftP95 = perCardShiftP95;
		}

		/// <summary>The gate threshold: the flip rate resampling noise alone produces 95% of the time.</summary>
		public double MaterialityThreshold => Percentile(NullFlipRate, 0.95);

		internal static double Percentile(double[] values, double p)
		{
			if(values.Length == 0)
				return double.NaN;
			var sorted = values.OrderBy(v => v).ToArray();
			var idx = (int)Math.Floor(p * (sorted.Length - 1));
			return sorted[Math.Max(0, Math.Min(sorted.Length - 1, idx))];
		}
	}

	/// <summary>
	/// Case bootstrap over CARDS. Resampling rows would be wrong: a neutral card contributes one
	/// row per class, so rows are correlated in groups and row-resampling understates the standard
	/// errors of exactly the sparse coefficients in question. Each replicate refits from scratch,
	/// which is cheap here (closed-form normal equations on a few dozen features).
	///
	/// This is what turns "the weights moved by 0.44" — a number in mixed units, below the shipped
	/// rounding, and with no notion of noise — into a statement that can be acted on: a coefficient
	/// worth reporting is one whose magnitude clears its own sampling error.
	/// </summary>
	internal static class Bootstrap
	{
		/// <summary>Winner-margin thresholds the instability report is broken down by.</summary>
		private static readonly double[] GateMargins = { 0.0, 1.0, 3.0, 5.0, 10.0 };

		internal static BootstrapResult Run(double[][] x, double[] y, double[] w,
			IReadOnlyList<Row> rows, IReadOnlyList<string> featureNames, double alpha)
		{
			var cards = rows.Select(r => r.CardId).Distinct().ToList();
			var rowsOfCard = new Dictionary<string, List<int>>(cards.Count);
			for(var i = 0; i < rows.Count; i++)
			{
				if(!rowsOfCard.TryGetValue(rows[i].CardId, out var list))
					rowsOfCard[rows[i].CardId] = list = new List<int>();
				list.Add(i);
			}

			// The reference pool for the output-space gate: every draftable card, scored the way
			// the plugin scores it. Derived deterministically from HearthDb, so successive runs
			// compare like with like without committing a card list to the repo.
			var (pool, classes) = BuildPoolWithClasses(featureNames);
			var triples = BuildTriples(classes);

			var pointFit = Ridge.FitRidgeStandardized(x, y, w, alpha);
			var pointDisplay = DisplayScores(pool, pointFit.raw, pointFit.icept);

			var draws = new List<double[]>(TrainingConfig.BootstrapReplicates);
			var nullFlips = new List<double>(TrainingConfig.BootstrapReplicates);
			var nullShift = new List<double>(TrainingConfig.BootstrapReplicates);
			var nullFlipsByMargin = GateMargins.Select(_ => new List<double>()).ToArray();

			for(var b = 0; b < TrainingConfig.BootstrapReplicates; b++)
			{
				var rng = new Random(TrainingConfig.CvSeed + 5000 + b);
				var idx = new List<int>(rows.Count);
				for(var c = 0; c < cards.Count; c++)
					idx.AddRange(rowsOfCard[cards[rng.Next(cards.Count)]]);
				if(idx.Count < 100)
					continue;

				var (raw, icept) = Ridge.FitRidgeStandardized(
					idx.Select(i => x[i]).ToArray(),
					idx.Select(i => y[i]).ToArray(),
					idx.Select(i => w[i]).ToArray(),
					alpha);
				draws.Add(raw);

				var display = DisplayScores(pool, raw, icept);
				nullFlips.Add(PickFlipRate(triples, display, pointDisplay));
				for(var m = 0; m < GateMargins.Length; m++)
					nullFlipsByMargin[m].Add(PickFlipRate(triples, display, pointDisplay, GateMargins[m]));
				var shifts = new double[display.Length];
				for(var k = 0; k < display.Length; k++)
					shifts[k] = Math.Abs(display[k] - pointDisplay[k]);
				nullShift.Add(BootstrapResult.Percentile(shifts, 0.95));
			}

			var stability = new Dictionary<string, CoefficientStability>(featureNames.Count);
			for(var j = 0; j < featureNames.Count; j++)
			{
				var column = draws.Select(d => d[j]).ToList();
				var point = pointFit.raw[j];
				var sameSign = column.Count(v => Math.Sign(v) == Math.Sign(point));
				stability[featureNames[j]] = new CoefficientStability(
					Stats.StdDev(column),
					column.Count == 0 ? double.NaN : sameSign / (double)column.Count);
			}
			Console.WriteLine();
			Console.WriteLine("pick instability under resampling, by how clearly the model named a winner:");
			for(var m = 0; m < GateMargins.Length; m++)
			{
				var p95 = BootstrapResult.Percentile(nullFlipsByMargin[m].ToArray(), 0.95);
				Console.WriteLine($"  winner leads by > {GateMargins[m],4:0.0} pts: {p95,7:P2} of those picks flip");
			}

			return new BootstrapResult(stability, nullFlips.ToArray(),
				BootstrapResult.Percentile(nullShift.ToArray(), 0.95));
		}

		/// <summary>The pool plus each card's class, so triples can be drawn WITHIN a class.</summary>
		internal static (List<double[]> Vectors, List<CardClass> Classes) BuildPoolWithClasses(
			IReadOnlyList<string> featureNames)
		{
			var vectors = new List<double[]>();
			var classes = new List<CardClass>();
			foreach(var card in Cards.Collectible.Values)
			{
				if(Array.IndexOf(TrainingConfig.Playable, card.Type) < 0)
					continue;
				var f = HeuristicArenaDataSource.BuildFeatures(card);
				var vec = new double[featureNames.Count];
				for(var j = 0; j < featureNames.Count; j++)
					vec[j] = f.TryGetValue(featureNames[j], out var v) ? v : 0.0;
				vectors.Add(vec);
				classes.Add(card.Class);
			}
			return (vectors, classes);
		}

		/// <summary>
		/// Deterministic same-class triples: the unit the product actually decides. Gating on how
		/// far an individual card's score moves is hopeless — per-card estimation error is ~17
		/// display points at p95, so every refit looks like noise. But two cards scored by the SAME
		/// fit share their coefficient error, so it largely cancels in the comparison between them:
		/// the pick only flips when the model genuinely reorders the pair. That is both the tighter
		/// statistic and the one that maps 1:1 onto "would a user see a different recommendation".
		/// </summary>
		internal static int[][] BuildTriples(List<CardClass> classes)
		{
			var byClass = new Dictionary<CardClass, List<int>>();
			for(var i = 0; i < classes.Count; i++)
			{
				if(!byClass.TryGetValue(classes[i], out var list))
					byClass[classes[i]] = list = new List<int>();
				list.Add(i);
			}
			var eligible = byClass.Values.Where(v => v.Count >= 3).ToList();
			if(eligible.Count == 0)
				return new int[0][];

			var rng = new Random(TrainingConfig.CvSeed + 9000);
			var triples = new int[TrainingConfig.GateTriples][];
			for(var t = 0; t < triples.Length; t++)
			{
				var group = eligible[rng.Next(eligible.Count)];
				var a = rng.Next(group.Count);
				int b, c;
				do { b = rng.Next(group.Count); } while(b == a);
				do { c = rng.Next(group.Count); } while(c == a || c == b);
				triples[t] = new[] { group[a], group[b], group[c] };
			}
			return triples;
		}

		/// <summary>Fraction of triples whose best-scoring card differs between the two score sets.</summary>
		internal static double PickFlipRate(int[][] triples, double[] left, double[] right)
			=> PickFlipRate(triples, left, right, 0.0);

		/// <summary>
		/// Same, but counting only triples where <paramref name="right"/> (the reference) named a
		/// winner by more than <paramref name="minMargin"/> display points. A flip between two cards
		/// the model scores as near-equal is harmless — it is not claiming to tell them apart. A flip
		/// where it claimed a clear winner is not.
		/// </summary>
		internal static double PickFlipRate(int[][] triples, double[] left, double[] right,
			double minMargin)
		{
			var considered = 0;
			var flips = 0;
			foreach(var t in triples)
			{
				var refBest = ArgMax(right, t);
				if(minMargin > 0)
				{
					var runnerUp = double.NegativeInfinity;
					foreach(var i in t)
					{
						if(i != refBest && right[i] > runnerUp)
							runnerUp = right[i];
					}
					if(right[refBest] - runnerUp <= minMargin)
						continue;
				}
				considered++;
				if(ArgMax(left, t) != refBest)
					flips++;
			}
			return considered == 0 ? double.NaN : flips / (double)considered;
		}

		private static int ArgMax(double[] scores, int[] triple)
		{
			var best = triple[0];
			for(var i = 1; i < triple.Length; i++)
			{
				if(scores[triple[i]] > scores[best])
					best = triple[i];
			}
			return best;
		}

		/// <summary>
		/// The 0-100 scores the plugin would show, including the median anchor. The anchor is
		/// recomputed per fit exactly as the shipped json does it, so a fit that merely shifts
		/// every raw score by a constant produces NO display change — which is the whole point of
		/// gating in output space rather than on the coefficients.
		/// </summary>
		internal static double[] DisplayScores(List<double[]> pool, double[] raw, double intercept)
		{
			var rawScores = new double[pool.Count];
			for(var i = 0; i < pool.Count; i++)
			{
				var s = intercept;
				var vec = pool[i];
				for(var j = 0; j < raw.Length; j++) s += raw[j] * vec[j];
				rawScores[i] = s;
			}
			// Centre AND scale, exactly as the plugin maps it: a fit that merely shifts or rescales
			// every raw score produces no display change, which is what makes an output-space gate
			// measure the model rather than the fit's arbitrary units.
			var anchor = Median(rawScores);
			var deviations = rawScores.Select(r => Math.Abs(r - anchor)).ToArray();
			var mad = Median(deviations);
			var sigma = 1.4826 * mad;
			if(sigma <= 1e-9)
				sigma = 1.0;
			var display = new double[rawScores.Length];
			for(var i = 0; i < rawScores.Length; i++)
				display[i] = Math.Max(0, Math.Min(100, 50 + 15 * (rawScores[i] - anchor) / sigma));
			return display;
		}

		private static double Median(double[] v)
		{
			var s = v.OrderBy(t => t).ToArray();
			if(s.Length == 0)
				return 0;
			return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
		}
	}
}
