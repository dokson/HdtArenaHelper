using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// The statistical policy shared by every win-rate data source (HSReplay, Firestone),
	/// so their scores stay calibrated against each other in the blend:
	///
	///   1. Shrinkage: empirical Bayes toward a target — thin samples glide to the prior,
	///      big samples barely move — so no low-sample outlier can assert an extreme rate.
	///   2. Normalization: a logistic anchored at the robust centre (median) with a robust
	///      spread (MAD). The median input maps to 50 and outliers saturate instead of
	///      stretching the scale, keeping it stable across daily refreshes.
	/// </summary>
	internal static class ScoreMath
	{
		/// <summary>Soft floor: below this many games a rate is pure noise even after shrinkage.</summary>
		public const int MinGames = 10;

		/// <summary>Empirical-Bayes prior strength, in pseudo-games.</summary>
		public const int ShrinkGames = 60;

		// Logistic slope that makes 100/(1+e^-1.702z) approximate the normal CDF, so a
		// value one robust-SD above the median scores ~85.
		private const double LogisticSlope = 1.702;

		/// <summary>Shrinks a rate toward <paramref name="target"/> by sample size: (n·wr + k·t) / (n + k).</summary>
		public static double Shrink(double winrate, int games, double target)
			=> (games * winrate + ShrinkGames * target) / (games + ShrinkGames);

		/// <summary>
		/// Maps values to 0-100 via the median/MAD-anchored logistic, so the median input
		/// is 50 and the scale is immune to outliers.
		/// </summary>
		public static Dictionary<TKey, double> ToScores<TKey>(IReadOnlyDictionary<TKey, double> values)
		{
			var center = Median(values.Values);
			return ToScores(values, center, RobustSigma(values.Values, center));
		}

		/// <summary>
		/// Same logistic, but on an EXPLICIT scale. Use this to score several groups
		/// (e.g. per-class rates and the class-agnostic pool) against one shared
		/// centre/spread, so their scores stay comparable within a single pick.
		/// </summary>
		public static Dictionary<TKey, double> ToScores<TKey>(
			IReadOnlyDictionary<TKey, double> values, double center, double sigma)
		{
			var result = new Dictionary<TKey, double>(values.Count);
			foreach(var kv in values)
				result[kv.Key] = Logistic((kv.Value - center) / sigma);
			return result;
		}

		public static double Logistic(double z)
			=> 100.0 / (1.0 + Math.Exp(-LogisticSlope * z));

		public static double Median(IEnumerable<double> values)
		{
			var sorted = values.OrderBy(v => v).ToList();
			if(sorted.Count == 0)
				return 0.0;
			var mid = sorted.Count / 2;
			return sorted.Count % 2 == 1
				? sorted[mid]
				: (sorted[mid - 1] + sorted[mid]) / 2.0;
		}

		/// <summary>Median absolute deviation, scaled to a normal-consistent SD.</summary>
		public static double RobustSigma(IEnumerable<double> values, double center)
		{
			var mad = Median(values.Select(v => Math.Abs(v - center)));
			var sigma = 1.4826 * mad;
			return sigma > 1e-9 ? sigma : 1.0; // degenerate spread -> everything near 50
		}
	}

	/// <summary>Hero-skin lookups shared by the win-rate sources (class-pick scoring).</summary>
	internal static class HeroSkins
	{
		/// <summary>Hero-skin dbfId -> class (HERO_01, HERO_01a, ...); build once HearthDb is ready.</summary>
		public static Dictionary<int, CardClass> BuildClassMap()
		{
			var map = new Dictionary<int, CardClass>();
			foreach(var kv in HearthDb.Cards.All)
			{
				if(kv.Key.StartsWith("HERO_", StringComparison.Ordinal) && kv.Value.DbfId != 0)
					map[kv.Value.DbfId] = kv.Value.Class;
			}
			return map;
		}
	}
}
