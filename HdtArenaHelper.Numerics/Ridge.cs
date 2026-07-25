using System;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace HdtArenaHelper.Numerics
{
	/// <summary>
	/// Reproduces scikit-learn's StandardScaler().fit(X) -> Ridge(alpha).fit(sc.transform(X),
	/// y, sample_weight=w) -> de-standardized (raw, intercept), dense cholesky path.
	/// Verified against the sklearn 1.x source: unweighted population-std scaler,
	/// weighted centering for the intercept, A = XcᵀW Xc + alpha·I (alpha unscaled, raw
	/// weights), intercept unregularized.
	/// </summary>
	public static class Ridge
	{
		public static (double[] raw, double icept) FitRidgeStandardized(
			double[][] x, double[] y, double[] w, double alpha)
		{
			var n = x.Length;
			var p = x[0].Length;
			const double eps = 2.220446049250313e-16; // float64 machine epsilon (not double.Epsilon)

			// StandardScaler.fit: UNWEIGHTED population mean & std (ddof=0); constant col -> scale 1.
			var mean = new double[p];
			var scale = new double[p];
			for(var j = 0; j < p; j++)
			{
				double s = 0;
				for(var i = 0; i < n; i++) s += x[i][j];
				var mj = s / n;
				double ss = 0;
				for(var i = 0; i < n; i++) { var d = x[i][j] - mj; ss += d * d; }
				var varj = ss / n;
				mean[j] = mj;
				var upperBound = n * eps * varj + Math.Pow(n * mj * eps, 2);
				scale[j] = varj <= upperBound ? 1.0 : Math.Sqrt(varj);
			}

			// Ridge _preprocess_data: sample-weight-weighted centering of the standardized design.
			double wsum = 0;
			for(var i = 0; i < n; i++) wsum += w[i];
			var zbar = new double[p];
			for(var j = 0; j < p; j++)
			{
				double acc = 0;
				for(var i = 0; i < n; i++) acc += w[i] * ((x[i][j] - mean[j]) / scale[j]);
				zbar[j] = acc / wsum;
			}
			double ybar = 0;
			for(var i = 0; i < n; i++) ybar += w[i] * y[i];
			ybar /= wsum;

			// Normal equations on centered standardized data: A = XcᵀW Xc + alpha·I, b = XcᵀW yc.
			var a = new double[p, p];
			var b = new double[p];
			var z = new double[p];
			for(var i = 0; i < n; i++)
			{
				var wi = w[i];
				var yc = y[i] - ybar;
				for(var j = 0; j < p; j++)
					z[j] = (x[i][j] - mean[j]) / scale[j] - zbar[j];
				for(var j = 0; j < p; j++)
				{
					var wzj = wi * z[j];
					b[j] += wzj * yc;
					for(var k = j; k < p; k++) a[j, k] += wzj * z[k];
				}
			}
			for(var j = 0; j < p; j++)
			{
				a[j, j] += alpha; // unscaled ridge penalty; intercept is outside A (unregularized)
				for(var k = j + 1; k < p; k++) a[k, j] = a[j, k];
			}

			Vector<double> beta = DenseMatrix.OfArray(a).Cholesky().Solve(DenseVector.OfArray(b));

			var interceptStd = ybar;
			for(var j = 0; j < p; j++) interceptStd -= zbar[j] * beta[j];

			// De-standardize to the original feature scale.
			var raw = new double[p];
			var icept = interceptStd;
			for(var j = 0; j < p; j++)
			{
				raw[j] = beta[j] / scale[j];
				icept -= raw[j] * mean[j];
			}
			return (raw, icept);
		}

		/// <summary>
		/// How much regularization is ACTUALLY happening. alpha is added raw to the diagonal of
		/// XcᵀW·Xc, so its effect depends entirely on the scale of the sample weights: with
		/// unnormalized w = sqrt(games), mean(diag) runs to 1e4-1e5 and alpha=10 shrinks by
		/// ~1e-4, i.e. the fit is unpenalized weighted OLS while looking like a ridge. Logging
		/// the ratio and the effective degrees of freedom makes that class of bug self-evident.
		/// Diagnostic only — mirrors the preprocessing above without solving.
		/// </summary>
		public static (double MeanDiag, double DfEff, double Shrinkage) PenaltyDiagnostics(
			double[][] x, double[] w, double alpha)
		{
			var n = x.Length;
			var p = x[0].Length;
			var mean = new double[p];
			var scale = new double[p];
			for(var j = 0; j < p; j++)
			{
				double s = 0;
				for(var i = 0; i < n; i++) s += x[i][j];
				mean[j] = s / n;
				double ss = 0;
				for(var i = 0; i < n; i++) { var d = x[i][j] - mean[j]; ss += d * d; }
				var varj = ss / n;
				scale[j] = varj <= 0 ? 1.0 : Math.Sqrt(varj);
			}
			double wsum = 0;
			for(var i = 0; i < n; i++) wsum += w[i];
			var zbar = new double[p];
			for(var j = 0; j < p; j++)
			{
				double acc = 0;
				for(var i = 0; i < n; i++) acc += w[i] * ((x[i][j] - mean[j]) / scale[j]);
				zbar[j] = acc / wsum;
			}
			var a = new double[p, p];
			var z = new double[p];
			for(var i = 0; i < n; i++)
			{
				for(var j = 0; j < p; j++)
					z[j] = (x[i][j] - mean[j]) / scale[j] - zbar[j];
				for(var j = 0; j < p; j++)
				{
					var wzj = w[i] * z[j];
					for(var k = j; k < p; k++) a[j, k] += wzj * z[k];
				}
			}
			for(var j = 0; j < p; j++)
				for(var k = j + 1; k < p; k++) a[k, j] = a[j, k];

			double meanDiag = 0;
			for(var j = 0; j < p; j++) meanDiag += a[j, j];
			meanDiag /= p;

			// df_eff = Σ d_i/(d_i + alpha) over the eigenvalues of the Gram matrix: p means
			// "every parameter free" (no regularization), and it falls toward 0 as alpha grows.
			var evd = DenseMatrix.OfArray(a).Evd();
			double dfEff = 0;
			foreach(var d in evd.EigenValues)
			{
				var di = Math.Max(0, d.Real);
				dfEff += di / (di + alpha);
			}
			return (meanDiag, dfEff, alpha / (meanDiag + alpha));
		}
	}
}
