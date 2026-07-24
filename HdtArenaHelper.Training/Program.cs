using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HearthDb;
using HearthDb.Enums;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: InternalsVisibleTo("HdtArenaHelper.Tests")]

namespace HdtArenaHelper.Training
{
	/// <summary>
	/// Offline weight-fitting tool for the heuristic. Fetches real arena win-rates
	/// (HSReplay + Firestone), builds a class-centered dual-source target from the DRAWN
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
		private const string HsReplayUrl =
			"https://hsreplay.net/api/v1/arena/card_stats/free/?format=json";
		private const string FirestoneUrlFmt =
			"https://static.zerotoheroes.com/api/arena/stats/cards/arena-underground/last-patch/{0}.gz.json";
		private const string UserAgent =
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
			"(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

		private const int MinGames = 100;      // per-source inclusion floor (matches training)
		private const int MinClassRows = 50;   // drop a class bucket with too few cards
		private const double Alpha = 10.0;      // ridge penalty

		private static readonly CardType[] Playable =
		{
			CardType.MINION, CardType.SPELL, CardType.WEAPON, CardType.LOCATION, CardType.HERO
		};

		private static int Main()
		{
			try
			{
				var trainingDir = FindTrainingDir();
				Console.WriteLine($"training dir: {trainingDir}");

				Console.WriteLine("fetching HSReplay arena card stats...");
				var hs = JObject.Parse(Download(HsReplayUrl));
				var pooled = BuildHsReplayPooled(hs);
				Console.WriteLine($"HSReplay pooled (card,class) rows: {pooled.Count}");

				Console.WriteLine("fetching Firestone per-class stats...");
				var (firestone, fsTiers) = BuildFirestone();
				Console.WriteLine($"Firestone (card,class) rows: {firestone.Count}");

				// The hero-pick tier ranking is the highest-leverage output and is NOT
				// covered by the per-card fit below: validate it leave-one-source-out on
				// every retrain (each source's tier list built the way the runtime does).
				BacktestClassTiers(HsReplayTiers(hs), fsTiers);

				// Rows present in BOTH sources; dual-source averaged target.
				var rows = new List<Row>();
				foreach(var p in pooled)
				{
					if(firestone.TryGetValue((p.CardId, p.Class), out var fsWrC))
						rows.Add(new Row(p.CardId, p.Class, (p.WrC + fsWrC) / 2.0, p.NumGames));
				}
				Console.WriteLine($"rows with both sources: {rows.Count}");
				if(rows.Count < 100)
				{
					Console.Error.WriteLine("too few dual-source rows; aborting.");
					return 1;
				}

				// Design matrix from the SHARED feature extraction.
				var featureRows = rows
					.Select(r => HeuristicArenaDataSource.BuildFeatures(Cards.All[r.CardId]))
					.ToList();
				var featureNames = featureRows
					.SelectMany(f => f.Keys)
					.Distinct()
					.OrderBy(k => k, StringComparer.Ordinal)
					.ToList();

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

				Console.WriteLine($"fitting ridge (alpha={Alpha}) on {rows.Count} rows, {featureNames.Count} features...");
				var (raw, icept) = Ridge.FitRidgeStandardized(x, y, w, Alpha);

				var generated = RoundWeights(featureNames, raw);
				var intercept = Math.Round(icept, 2);
				// Display anchor: the median raw score of the draftable pool, shipped in
				// the json so the plugin maps the median card to 50 without a hardcoded
				// constant that would go stale on the next re-fit.
				var anchor = ComputeAnchorMedianRaw(generated, intercept);
				Console.WriteLine($"display anchor (pool median raw): {anchor:+0.00;-0.00}");

				var generatedPath = Path.Combine(trainingDir, "arena_weights.generated.json");
				WriteWeights(generatedPath, intercept, anchor, generated);
				Console.WriteLine($"wrote {generatedPath}");

				var committedPath = Path.Combine(trainingDir, "arena_weights.json");
				Compare(committedPath, intercept, anchor, generated);
				PrintGoldenScores(generated, intercept, anchor);
				return 0;
			}
			catch(Exception ex)
			{
				Console.Error.WriteLine("FAILED: " + ex);
				return 1;
			}
		}

		// ---- HSReplay: pooled (card,class) class-centered drawn win-rate --------

		private readonly struct Pooled
		{
			public readonly string CardId;
			public readonly CardClass Class;
			public readonly double WrC;
			public readonly int NumGames;
			public Pooled(string cardId, CardClass cls, double wrC, int numGames)
			{
				CardId = cardId; Class = cls; WrC = wrC; NumGames = numGames;
			}
		}

		private readonly struct Row
		{
			public readonly string CardId;
			public readonly CardClass Class;
			public readonly double YAvg;
			public readonly int NumGames;
			public Row(string cardId, CardClass cls, double yAvg, int numGames)
			{
				CardId = cardId; Class = cls; YAvg = yAvg; NumGames = numGames;
			}
		}

		private static List<Pooled> BuildHsReplayPooled(JObject hs)
		{
			var result = new List<Pooled>();
			var data = (JObject?)hs["data"] ?? new JObject();
			foreach(var prop in data.Properties())
			{
				if(prop.Name == "ALL" || !(prop.Value is JArray bucket))
					continue;
				if(!Enum.TryParse<CardClass>(prop.Name, ignoreCase: true, out var cls))
					continue;

				// Keep the max-games entry per card, playable + resolvable, games >= floor.
				var best = new Dictionary<string, (double wr, int n)>();
				foreach(var c in bucket)
				{
					var cardId = (string?)c["card_id"];
					var wr = (double?)c["drawn_win_rate"] ?? (double?)c["win_rate"];
					var n = (int?)c["num_games"] ?? 0;
					if(string.IsNullOrEmpty(cardId) || wr == null || n < MinGames)
						continue;
					if(!Cards.All.TryGetValue(cardId, out var card) || !Playable.Contains(card.Type))
						continue;
					if(!best.TryGetValue(cardId!, out var cur) || n > cur.n)
						best[cardId!] = (wr.Value, n);
				}
				if(best.Count < MinClassRows)
					continue;

				var totalGames = best.Values.Sum(v => (double)v.n);
				var mean = best.Values.Sum(v => v.wr * v.n) / totalGames; // games-weighted
				foreach(var kv in best)
					result.Add(new Pooled(kv.Key, cls, kv.Value.wr - mean, kv.Value.n));
			}
			return result;
		}

		// ---- Firestone: class-centered drawn winrate (fraction -> pct points) --

		private static (Dictionary<(string, CardClass), double> Centered, Dictionary<CardClass, double> Tiers)
			BuildFirestone()
		{
			var tiers = new Dictionary<CardClass, double>();
			var result = new Dictionary<(string, CardClass), double>();
			foreach(CardClass cls in Enum.GetValues(typeof(CardClass)))
			{
				var name = cls.ToString().ToLowerInvariant();
				string json;
				try { json = Download(string.Format(FirestoneUrlFmt, name)); }
				catch { continue; } // class file may not exist
				JArray? stats;
				try { stats = (JArray?)JObject.Parse(json)["stats"]; }
				catch { continue; }
				if(stats == null)
					continue;

				var entries = new List<(string card, double wr, int drawn)>();
				foreach(var e in stats)
				{
					var cardId = (string?)e["cardId"];
					var s = e["stats"];
					var drawn = (int?)s?["drawn"] ?? 0;
					var wins = (int?)s?["drawnThenWin"] ?? 0;
					if(string.IsNullOrEmpty(cardId) || drawn < MinGames)
						continue;
					entries.Add((cardId!, wins / (double)drawn, drawn));
				}
				if(entries.Count < MinClassRows)
					continue;

				var totalDrawn = entries.Sum(t => (double)t.drawn);
				var mean = entries.Sum(t => t.wr * t.drawn) / totalDrawn; // draws-weighted
				foreach(var t in entries)
					result[(t.card, cls)] = (t.wr - mean) * 100.0; // to pct points, like HSReplay
				// Tier the way the runtime does: UNWEIGHTED mean of the class's card rates.
				tiers[cls] = entries.Average(t => t.wr) * 100.0;
			}
			return (result, tiers);
		}

		/// <summary>Class tiers from the HSReplay buckets, mirroring the runtime
		/// (unweighted mean of the class's card drawn win-rates, games floor applied).</summary>
		private static Dictionary<CardClass, double> HsReplayTiers(JObject hs)
		{
			var tiers = new Dictionary<CardClass, double>();
			var data = (JObject?)hs["data"] ?? new JObject();
			foreach(var prop in data.Properties())
			{
				if(prop.Name == "ALL" || !(prop.Value is JArray bucket))
					continue;
				if(!Enum.TryParse<CardClass>(prop.Name, ignoreCase: true, out var cls))
					continue;
				var rates = new List<double>();
				foreach(var c in bucket)
				{
					var wr = (double?)c["drawn_win_rate"] ?? (double?)c["win_rate"];
					var n = (int?)c["num_games"] ?? 0;
					if(wr != null && n >= MinGames)
						rates.Add(wr.Value);
				}
				if(rates.Count >= MinClassRows)
					tiers[cls] = rates.Average();
			}
			return tiers;
		}

		/// <summary>
		/// Leave-one-source-out check of the hero-pick tier RANKING: if the two sources
		/// don't rank the classes the same way, the tier list shown at the hero pick is
		/// not trustworthy for that patch — investigate before shipping a retrain.
		/// </summary>
		private static void BacktestClassTiers(
			Dictionary<CardClass, double> hsTiers, Dictionary<CardClass, double> fsTiers)
		{
			var common = hsTiers.Keys.Where(fsTiers.ContainsKey).ToList();
			if(common.Count < 6)
			{
				Console.WriteLine($"class-tier backtest skipped ({common.Count} common classes).");
				return;
			}

			var hsRank = Ranks(common, hsTiers);
			var fsRank = Ranks(common, fsTiers);
			double d2 = 0;
			foreach(var cls in common)
				d2 += Math.Pow(hsRank[cls] - fsRank[cls], 2);
			var n = common.Count;
			var rho = 1.0 - 6.0 * d2 / (n * ((double)n * n - 1));

			Console.WriteLine();
			Console.WriteLine($"class-tier ranking cross-source agreement (n={n}): Spearman={rho:0.00}");
			Console.WriteLine($"  HSReplay:  {string.Join(" > ", common.OrderByDescending(c => hsTiers[c]))}");
			Console.WriteLine($"  Firestone: {string.Join(" > ", common.OrderByDescending(c => fsTiers[c]))}");
			Console.WriteLine(rho >= 0.7
				? "  TIER GATE PASS: the sources agree on the hero-pick ranking."
				: "  TIER GATE WARNING: sources disagree — review the tier list before trusting the hero pick.");
		}

		private static Dictionary<CardClass, int> Ranks(
			IReadOnlyList<CardClass> classes, Dictionary<CardClass, double> tiers)
		{
			var ordered = classes.OrderByDescending(c => tiers[c]).ToList();
			var ranks = new Dictionary<CardClass, int>();
			for(var i = 0; i < ordered.Count; i++)
				ranks[ordered[i]] = i;
			return ranks;
		}

		// ---- output / comparison ----------------------------------------------

		/// <summary>round to 2 decimals, drop |w| &lt; 0.05.</summary>
		private static SortedDictionary<string, double> RoundWeights(IReadOnlyList<string> names, double[] raw)
		{
			var result = new SortedDictionary<string, double>(StringComparer.Ordinal);
			for(var j = 0; j < names.Count; j++)
			{
				var r = Math.Round(raw[j], 2);
				if(Math.Abs(r) >= 0.05)
					result[names[j]] = r;
			}
			return result;
		}

		private static void WriteWeights(string path, double intercept, double anchor,
			IDictionary<string, double> weights)
		{
			var obj = new JObject
			{
				["intercept"] = intercept,
				["anchor_median_raw"] = anchor,
				["weights"] = JObject.FromObject(weights),
				["target"] = "avg(HSReplay, Firestone) class-centered arena drawn winrate (pct pts)",
				["trained"] = "regenerated by HdtArenaHelper.Training, ridge alpha=10, sqrt(games) weights"
			};
			File.WriteAllText(path, obj.ToString(Formatting.Indented));
		}

		private static double ScoreRaw(IDictionary<string, double> weights, double intercept, Card card)
		{
			var score = intercept;
			foreach(var kv in HeuristicArenaDataSource.BuildFeatures(card))
				score += (weights.TryGetValue(kv.Key, out var w) ? w : 0.0) * kv.Value;
			return score;
		}

		/// <summary>Median raw score over the draftable pool (collectible, playable, not a HERO_ skin).</summary>
		private static double ComputeAnchorMedianRaw(IDictionary<string, double> weights, double intercept)
		{
			var raws = new List<double>();
			foreach(var kv in Cards.All)
			{
				var card = kv.Value;
				if(!card.Collectible || card.DbfId == 0 || !Playable.Contains(card.Type))
					continue;
				if(kv.Key.StartsWith("HERO_", StringComparison.Ordinal))
					continue;
				raws.Add(ScoreRaw(weights, intercept, card));
			}
			raws.Sort();
			var mid = raws.Count / 2;
			var median = raws.Count % 2 == 1 ? raws[mid] : (raws[mid - 1] + raws[mid]) / 2.0;
			return Math.Round(median, 2);
		}

		// The cards pinned by HeuristicArenaDataSourceTests: after adopting a re-fit,
		// paste these values over the test's golden literals (the manual touch is the
		// tripwire that proves a human looked at the new weights).
		private static readonly string[] GoldenCards =
		{
			"LOOT_413", "CS2_189", "EX1_093", "CS2_106", "CS2_029", "EX1_050",
			"GIL_828", "CS2_235", "EX1_046", "NEW1_030", "ICC_833",
		};

		private static void PrintGoldenScores(IDictionary<string, double> weights, double intercept, double anchor)
		{
			Console.WriteLine();
			Console.WriteLine("golden scores for HeuristicArenaDataSourceTests (paste on adopt):");
			foreach(var id in GoldenCards)
			{
				if(!Cards.All.TryGetValue(id, out var card))
				{
					Console.WriteLine($"  {id}: NOT FOUND in HearthDb — replace this golden card");
					continue;
				}
				var raw = ScoreRaw(weights, intercept, card);
				var norm = Math.Max(0, Math.Min(100, 50 + 15 * (raw - anchor)));
				Console.WriteLine(FormattableString.Invariant(
					$"  [InlineData(\"{id}\", {norm:0.00})] // {card.Name}"));
			}
		}

		private static void Compare(string committedPath, double genIntercept, double genAnchor,
			IDictionary<string, double> gen)
		{
			if(!File.Exists(committedPath))
			{
				Console.WriteLine("no committed arena_weights.json to compare against.");
				return;
			}
			var committed = JObject.Parse(File.ReadAllText(committedPath));
			var comWeights = committed["weights"]!.ToObject<Dictionary<string, double>>()!;
			var comIntercept = (double)committed["intercept"]!;
			var comAnchor = (double?)committed["anchor_median_raw"] ?? 0.0;

			var keys = comWeights.Keys.Union(gen.Keys).OrderBy(k => k, StringComparer.Ordinal);
			double maxDiff = Math.Max(Math.Abs(genIntercept - comIntercept), Math.Abs(genAnchor - comAnchor));
			var mismatches = Math.Abs(genAnchor - comAnchor) >= 0.01 ? 1 : 0;

			Console.WriteLine();
			Console.WriteLine("feature                  committed   generated     diff");
			Console.WriteLine($"{"intercept",-22}   {comIntercept,8:+0.00;-0.00}   {genIntercept,8:+0.00;-0.00}   {genIntercept - comIntercept,6:+0.00;-0.00}");
			Console.WriteLine($"{"anchor_median_raw",-22}   {comAnchor,8:+0.00;-0.00}   {genAnchor,8:+0.00;-0.00}   {genAnchor - comAnchor,6:+0.00;-0.00}{(Math.Abs(genAnchor - comAnchor) >= 0.01 ? "  <-- differs" : "")}");
			foreach(var k in keys)
			{
				comWeights.TryGetValue(k, out var cv);
				gen.TryGetValue(k, out var gv);
				var diff = gv - cv;
				if(Math.Abs(diff) > 1e-9)
					maxDiff = Math.Max(maxDiff, Math.Abs(diff));
				if(Math.Abs(diff) >= 0.01)
					mismatches++;
				var flag = Math.Abs(diff) >= 0.01 ? "  <-- differs" : "";
				Console.WriteLine($"{k,-22}   {cv,8:+0.00;-0.00}   {gv,8:+0.00;-0.00}   {diff,6:+0.00;-0.00}{flag}");
			}

			Console.WriteLine();
			Console.WriteLine($"max abs weight diff: {maxDiff:0.####}");
			Console.WriteLine($"weights differing by >= 0.01: {mismatches}");
			Console.WriteLine(mismatches == 0
				? "GATE PASS: generated weights match the committed ones within rounding."
				: "GATE: generated weights are close but not identical (see rows above).");
		}

		// ---- io helpers ---------------------------------------------------------

		private static string Download(string url)
		{
			// .NET's TLS/HTTP fingerprint gets 403'd by Cloudflare on hsreplay.net, while
			// curl (bundled with Windows 10+/Git) is allowed. Shell out to it for this
			// dev-only fetch; --compressed handles Firestone's gzipped payloads.
			var psi = new ProcessStartInfo("curl", $"-fsSL --compressed -A \"{UserAgent}\" \"{url}\"")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using(var proc = Process.Start(psi))
			{
				if(proc == null)
					throw new InvalidOperationException("could not start curl");
				var stdout = proc.StandardOutput.ReadToEnd();
				proc.WaitForExit();
				if(proc.ExitCode != 0)
					throw new InvalidOperationException($"curl failed for {url} (exit {proc.ExitCode})");
				return stdout;
			}
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

	/// <summary>
	/// Reproduces scikit-learn's StandardScaler().fit(X) -> Ridge(alpha).fit(sc.transform(X),
	/// y, sample_weight=w) -> de-standardized (raw, intercept), dense cholesky path.
	/// Verified against the sklearn 1.x source: unweighted population-std scaler,
	/// weighted centering for the intercept, A = XcᵀW Xc + alpha·I (alpha unscaled, raw
	/// weights), intercept unregularized.
	/// </summary>
	internal static class Ridge
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
	}
}
