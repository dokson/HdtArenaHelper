using System;
using Xunit;

namespace HdtArenaHelper.Numerics.Tests
{
	/// <summary>
	/// Validates the ported ridge solver (<see cref="Ridge.FitRidgeStandardized"/>)
	/// against known-truth properties — not against its own output — so it proves the
	/// scikit-learn port is numerically correct.
	/// </summary>
	public class RidgeTests
	{
		private static double[] Ones(int n)
		{
			var w = new double[n];
			for(var i = 0; i < n; i++) w[i] = 1.0;
			return w;
		}

		private static double L2(double[] v)
		{
			double s = 0;
			foreach(var x in v) s += x * x;
			return Math.Sqrt(s);
		}

		[Fact]
		public void Recovers_exact_ols_solution_at_negligible_alpha()
		{
			// y is exactly linear in the features; at alpha->0 with unit weights the ridge
			// must recover the true coefficients + intercept on the ORIGINAL feature scale.
			// Exercises standardize -> weighted-center -> solve -> de-standardize together.
			double[][] x =
			{
				new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 0.0, 2.0 },
				new[] { 3.0, 1.0 }, new[] { 2.0, 5.0 }, new[] { 4.0, 2.0 },
				new[] { 1.0, 3.0 }, new[] { 5.0, 4.0 }
			};
			var y = new double[x.Length];
			for(var i = 0; i < x.Length; i++)
				y[i] = 2.0 * x[i][0] - 3.0 * x[i][1] + 5.0;

			var (raw, icept) = Ridge.FitRidgeStandardized(x, y, Ones(x.Length), 1e-9);

			Assert.Equal(2.0, raw[0], 4);
			Assert.Equal(-3.0, raw[1], 4);
			Assert.Equal(5.0, icept, 4);
		}

		[Fact]
		public void Stronger_penalty_shrinks_coefficients()
		{
			double[][] x =
			{
				new[] { 0.0, 1.0 }, new[] { 1.0, 0.0 }, new[] { 2.0, 3.0 },
				new[] { 3.0, 1.0 }, new[] { 4.0, 5.0 }, new[] { 5.0, 2.0 }
			};
			var y = new[] { 1.0, 2.0, 5.0, 4.0, 9.0, 6.0 };
			var w = Ones(x.Length);

			var weak = Ridge.FitRidgeStandardized(x, y, w, 0.1);
			var strong = Ridge.FitRidgeStandardized(x, y, w, 1000.0);

			Assert.True(L2(strong.raw) < L2(weak.raw));
		}

		[Fact]
		public void Constant_feature_gets_zero_coefficient()
		{
			// A constant column has scale_ forced to 1 and a zero centered contribution,
			// so its coefficient must be exactly 0 (and never NaN).
			double[][] x =
			{
				new[] { 1.0, 7.0 }, new[] { 2.0, 7.0 }, new[] { 3.0, 7.0 },
				new[] { 4.0, 7.0 }, new[] { 5.0, 7.0 }
			};
			var y = new[] { 2.0, 4.0, 6.0, 8.0, 10.0 };

			var (raw, _) = Ridge.FitRidgeStandardized(x, y, Ones(x.Length), 10.0);

			Assert.False(double.IsNaN(raw[1]));
			Assert.Equal(0.0, raw[1], 9);
		}

		[Fact]
		public void Sample_weights_shift_the_fit()
		{
			// Single constant feature -> the model is just the weighted mean in the
			// (unregularized) intercept. Up-weighting the high-y rows must raise it.
			double[][] x = { new[] { 1.0 }, new[] { 1.0 }, new[] { 1.0 }, new[] { 1.0 } };
			var y = new[] { 0.0, 0.0, 10.0, 10.0 };

			var even = Ridge.FitRidgeStandardized(x, y, new[] { 1.0, 1.0, 1.0, 1.0 }, 1e-9);
			var tilted = Ridge.FitRidgeStandardized(x, y, new[] { 1.0, 1.0, 9.0, 9.0 }, 1e-9);

			Assert.Equal(5.0, even.icept, 4);        // unweighted mean of {0,0,10,10}
			Assert.True(tilted.icept > even.icept);  // pulled toward the up-weighted 10s
		}
	}
}
