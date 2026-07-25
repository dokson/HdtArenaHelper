using System;
using System.Collections.Generic;
using System.Linq;

namespace HdtArenaHelper.Numerics
{
	/// <summary>
	/// Pure statistics: rank correlation and dispersion. No I/O and no model, which makes this
	/// the part of the trainer that is cheap to unit-test on its own.
	/// </summary>
	public static class Stats
	{
		public static double StdDev(IEnumerable<double> values)
		{
			var v = values.ToList();
			if(v.Count < 2)
				return double.NaN;
			var m = v.Average();
			return Math.Sqrt(v.Sum(t => (t - m) * (t - m)) / (v.Count - 1));
		}

		public static double StdErr(IReadOnlyCollection<double> v)
		{
			if(v.Count < 2)
				return 0;
			var m = v.Average();
			var ss = v.Sum(t => (t - m) * (t - m));
			return Math.Sqrt(ss / (v.Count - 1)) / Math.Sqrt(v.Count);
		}

		/// <summary>
		/// Slope of the least-squares regression of <paramref name="actual"/> ON
		/// <paramref name="pred"/> — the factor by which a prediction's deviation must be scaled to
		/// become the conditional mean of the truth. That is the shrinkage a miscalibrated
		/// predictor needs, and it is NOT a correlation: a correlation is symmetric and unitless,
		/// while this one answers "given the model says +5, how much does the truth actually move".
		/// </summary>
		public static double RegressionSlope(double[] pred, double[] actual)
		{
			if(pred.Length != actual.Length || pred.Length < 3)
				return double.NaN;
			var mp = pred.Average();
			var ma = actual.Average();
			double cov = 0, varp = 0;
			for(var i = 0; i < pred.Length; i++)
			{
				var d = pred[i] - mp;
				cov += d * (actual[i] - ma);
				varp += d * d;
			}
			return varp <= 0 ? double.NaN : cov / varp;
		}

		/// <summary>Spearman rank correlation with average ranks for ties.</summary>
		public static double Spearman(double[] a, double[] b)
		{
			if(a.Length != b.Length || a.Length < 3)
				return double.NaN;
			var ra = RankAverage(a);
			var rb = RankAverage(b);
			var ma = ra.Average();
			var mb = rb.Average();
			double num = 0, da = 0, db = 0;
			for(var i = 0; i < ra.Length; i++)
			{
				var x = ra[i] - ma;
				var yv = rb[i] - mb;
				num += x * yv;
				da += x * x;
				db += yv * yv;
			}
			return da <= 0 || db <= 0 ? double.NaN : num / Math.Sqrt(da * db);
		}

		public static double[] RankAverage(double[] v)
		{
			var idx = Enumerable.Range(0, v.Length).OrderBy(i => v[i]).ToArray();
			var ranks = new double[v.Length];
			var i0 = 0;
			while(i0 < idx.Length)
			{
				var i1 = i0;
				while(i1 + 1 < idx.Length && v[idx[i1 + 1]] == v[idx[i0]]) i1++;
				var avg = (i0 + i1) / 2.0 + 1;
				for(var k = i0; k <= i1; k++) ranks[idx[k]] = avg;
				i0 = i1 + 1;
			}
			return ranks;
		}
	}
}
