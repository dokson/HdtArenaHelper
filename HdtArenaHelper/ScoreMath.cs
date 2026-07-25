using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// The statistical policy shared by every win-rate data source, so their scores stay calibrated
	/// against each other in the blend. Only HSReplay implements it since 0.1.5, but the policy
	/// stays in one place rather than being inlined there: the reason it was extracted (two copies
	/// had already drifted, one of them missing a range check) applies again the moment a second
	/// source is added.
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

		/// <summary>
		/// A class's estimated ARENA win-rate in real percentage points, from per-card tallies:
		/// pool the class's (wins, games) and re-centre so the whole pool sits at 50.
		///
		/// The re-centring is not cosmetic, it removes a measured bias. Weighting cards by their
		/// games is the only way to recover a deck-level rate from card-level data, but in arena a
		/// winning deck keeps playing (up to 12 wins) while a losing one stops at 3, so games-
		/// weighting oversamples winning decks. Measured on the live payload, the pooled rate comes
		/// out at 53.4% where a true average win-rate must be ~50. Subtracting the source's own
		/// pooled offset removes it, and the result lands within ~3pp of the figures HDT's paid
		/// helper shows for the same classes — the one external check available.
		///
		/// So this is a CALIBRATED ESTIMATE, not a published number: label it as such wherever it
		/// is shown. Only the offset is removed; the spread between classes is left untouched.
		/// </summary>
		public static Dictionary<CardClass, double> RecentreClassWinRates(
			IReadOnlyDictionary<CardClass, (double Wins, double Games)> tallies)
		{
			var result = new Dictionary<CardClass, double>(tallies.Count);
			double pooledWins = 0, pooledGames = 0;
			foreach(var kv in tallies)
			{
				if(kv.Value.Games <= 0)
					continue;
				pooledWins += kv.Value.Wins;
				pooledGames += kv.Value.Games;
			}
			if(pooledGames <= 0)
				return result;

			var offset = 100.0 * pooledWins / pooledGames - NeutralWinRate;
			foreach(var kv in tallies)
			{
				if(kv.Value.Games <= 0)
					continue;
				result[kv.Key] = 100.0 * kv.Value.Wins / kv.Value.Games - offset;
			}
			return result;
		}

		/// <summary>Where the whole pool must sit: every game is one deck's win and another's loss.</summary>
		public const double NeutralWinRate = 50.0;

		/// <summary>
		/// The prior a per-class rate should shrink toward: the card's OVERALL rate with that class's
		/// own games subtracted out. Shrinking toward a prior that still contains the observation
		/// would double-count it, which is the whole reason this exists.
		///
		/// Falls back to <paramref name="fallback"/> when the remainder is too thin to mean anything,
		/// or when the arithmetic lands outside [0, 100] — which can happen from rounding on tiny
		/// remainders and, since a downloaded feed is untrusted input, from a poisoned rate. That
		/// guard once existed in one source's copy of this policy and not in the other's: the same
		/// rule implemented twice had already drifted, which is precisely what this class prevents.
		/// </summary>
		public static double LeaveOneOutTarget(double totalRate, int totalGames,
			double subsetRate, int subsetGames, double fallback)
		{
			var remainingGames = totalGames - subsetGames;
			if(remainingGames < MinGames)
				return fallback;
			var remaining = (totalGames * totalRate - subsetGames * subsetRate) / remainingGames;
			return remaining >= 0 && remaining <= 100 ? remaining : fallback;
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
