using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HearthDb;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HdtArenaHelper.Training
{
	/// <summary>
	/// Serializes the generated weights, diffs them against the committed file, and prints the
	/// golden scores a human pastes into the tests when adopting a re-fit.
	/// </summary>
	internal static class WeightsFile
	{
		/// <summary>round to 2 decimals, drop |w| &lt; 0.05.</summary>
		internal static SortedDictionary<string, double> RoundWeights(IReadOnlyList<string> names, double[] raw)
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

		internal static void WriteWeights(string path, double intercept, double anchor, double sigma,
			IDictionary<string, double> weights, double alpha, int rows, int cards)
		{
			var obj = new JObject
			{
				["intercept"] = intercept,
				["anchor_median_raw"] = anchor,
				["anchor_sigma_raw"] = sigma,
				["weights"] = JObject.FromObject(weights),
				["target"] = "HSReplay class-centered arena drawn winrate (pct pts)",
				// The provenance must state how this file was ACTUALLY fit. It used to hardcode
				// "alpha=10, sqrt(games) weights", which stopped being true the moment alpha became
				// cross-validated and the weights normalized — leaving the shipped model lying about
				// itself, with the real value only in the gitignored metrics.json.
				["fit_alpha"] = alpha,
				["fit_rows"] = rows,
				["fit_cards"] = cards,
				["trained"] = string.Format(CultureInfo.InvariantCulture,
					"HdtArenaHelper.Training: ridge alpha={0:0.###} (cross-validated, grouped by card), "
					+ "sqrt(games) sample weights normalized to mean 1, {1} (card,class) rows over "
					+ "{2} cards", alpha, rows, cards)
			};
			// LF explicitly: Formatting.Indented uses Environment.NewLine, which on Windows would
			// emit CRLF into a repo that normalizes to LF everywhere else.
			File.WriteAllText(path, obj.ToString(Formatting.Indented).Replace("\r\n", "\n"));
		}

		internal static double ScoreRaw(IDictionary<string, double> weights, double intercept, Card card)
		{
			var score = intercept;
			foreach(var kv in HeuristicArenaDataSource.BuildFeatures(card))
				score += (weights.TryGetValue(kv.Key, out var w) ? w : 0.0) * kv.Value;
			return score;
		}

		/// <summary>
		/// The pool's raw scores, used for both display anchors. Excludes HERO_* skins, which are
		/// scored from the class tier list rather than by this model.
		/// </summary>
		internal static List<double> PoolRawScores(IDictionary<string, double> weights, double intercept)
		{
			var raws = new List<double>();
			foreach(var kv in Cards.All)
			{
				var card = kv.Value;
				if(!card.Collectible || card.DbfId == 0 || !TrainingConfig.Playable.Contains(card.Type))
					continue;
				if(kv.Key.StartsWith("HERO_", StringComparison.Ordinal))
					continue;
				raws.Add(ScoreRaw(weights, intercept, card));
			}
			raws.Sort();
			return raws;
		}

		/// <summary>Median raw score of the pool: the display centre (median card -> 50).</summary>
		internal static double ComputeAnchorMedianRaw(IDictionary<string, double> weights, double intercept)
		{
			var raws = PoolRawScores(weights, intercept);
			var mid = raws.Count / 2;
			var median = raws.Count % 2 == 1 ? raws[mid] : (raws[mid - 1] + raws[mid]) / 2.0;
			return Math.Round(median, 2);
		}

		/// <summary>
		/// Robust SD (1.4826 x MAD) of the pool's raw scores: the display SCALE. Shipped so the
		/// plugin's 0-100 spread is a property of the card pool instead of of this fit's raw scale
		/// - without it a heavier-regularized re-fit silently compresses every shown score.
		/// </summary>
		internal static double ComputeAnchorSigmaRaw(IDictionary<string, double> weights,
			double intercept, double median)
		{
			var raws = PoolRawScores(weights, intercept);
			var deviations = raws.Select(r => Math.Abs(r - median)).OrderBy(d => d).ToList();
			if(deviations.Count == 0)
				return 1.0;
			var mid = deviations.Count / 2;
			var mad = deviations.Count % 2 == 1
				? deviations[mid]
				: (deviations[mid - 1] + deviations[mid]) / 2.0;
			var sigma = 1.4826 * mad;
			return sigma > 1e-9 ? Math.Round(sigma, 4) : 1.0;
		}

		// The cards pinned by HeuristicArenaDataSourceTests: after adopting a re-fit,
		// paste these values over the test's golden literals (the manual touch is the
		// tripwire that proves a human looked at the new weights).
		// NEW1_030 (Deathwing) earns its slot twice over: it is the LEGENDARY in the set, so it
		// pins that rarity is not a scoring input, and it is a card whose statline is genuinely
		// bad, so a model that quietly rewards the label shows up here as an inflated score.
		// ICC_833 (Frost Lich Jaina) was removed when the runtime stopped scoring HERO cards:
		// printing a golden for a card the plugin refuses to score invites pasting a literal
		// that can never match.
		internal static readonly string[] GoldenCards =
		{
			"LOOT_413", "CS2_189", "EX1_093", "CS2_106", "CS2_029", "EX1_050",
			"GIL_828", "CS2_235", "EX1_046", "NEW1_030",
		};

		internal static void PrintGoldenScores(IDictionary<string, double> weights, double intercept,
			double anchor, double sigma)
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
				var norm = Math.Max(0, Math.Min(100, 50 + 15 * (raw - anchor) / sigma));
				Console.WriteLine(FormattableString.Invariant(
					$"  [InlineData(\"{id}\", {norm:0.00})] // {card.Name}"));
			}
		}

		/// <summary>The committed weights, or null when there is nothing to compare against.</summary>
		internal static (Dictionary<string, double> Weights, double Intercept)? TryReadCommitted(string path)
		{
			if(!File.Exists(path))
				return null;
			var committed = JObject.Parse(File.ReadAllText(path));
			return (committed["weights"]!.ToObject<Dictionary<string, double>>()!,
				(double)committed["intercept"]!);
		}

		internal static void Compare(string committedPath, double genIntercept, double genAnchor,
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
	}
}
