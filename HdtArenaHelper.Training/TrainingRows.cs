using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;

namespace HdtArenaHelper.Training
{
	internal readonly struct Pooled
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

	internal readonly struct Row
	{
		public readonly string CardId;
		public readonly CardClass Class;
		public readonly double YAvg;
		public readonly int NumGames;
		// The two sources kept separately as well as averaged: their agreement on a subset is
		// the only available estimate of how RELIABLE the target is there, which is what tells
		// a genuine model failure apart from attenuation by a noisy label.
		public readonly double WrHs;
		public readonly double WrFs;
		public Row(string cardId, CardClass cls, double yAvg, int numGames, double wrHs, double wrFs)
		{
			CardId = cardId; Class = cls; YAvg = yAvg; NumGames = numGames;
			WrHs = wrHs; WrFs = wrFs;
		}
	}

	/// <summary>
	/// Turns the two public win-rate feeds into the (card, class) rows the fit consumes, and
	/// cross-checks the hero-pick tier ranking of one source against the other.
	/// </summary>
	internal static class TrainingRows
	{
		internal static List<Pooled> BuildHsReplayPooled(JObject hs)
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
					if(string.IsNullOrEmpty(cardId) || wr == null || n < TrainingConfig.MinGames)
						continue;
					if(!Cards.All.TryGetValue(cardId, out var card) || !TrainingConfig.Playable.Contains(card.Type))
						continue;
					if(!best.TryGetValue(cardId!, out var cur) || n > cur.n)
						best[cardId!] = (wr.Value, n);
				}
				if(best.Count < TrainingConfig.MinClassRows)
					continue;

				var totalGames = best.Values.Sum(v => (double)v.n);
				var mean = best.Values.Sum(v => v.wr * v.n) / totalGames; // games-weighted
				foreach(var kv in best)
					result.Add(new Pooled(kv.Key, cls, kv.Value.wr - mean, kv.Value.n));
			}
			return result;
		}

		// ---- Firestone: class-centered drawn winrate (fraction -> pct points) --

		internal static (Dictionary<(string, CardClass), double> Centered, Dictionary<CardClass, double> Tiers)
			BuildFirestone()
		{
			var tiers = new Dictionary<CardClass, double>();
			var result = new Dictionary<(string, CardClass), double>();
			foreach(CardClass cls in Enum.GetValues(typeof(CardClass)))
			{
				var name = cls.ToString().ToLowerInvariant();
				string json;
				try { json = PayloadFetcher.Download(string.Format(TrainingConfig.FirestoneUrlFmt, name)); }
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
					if(string.IsNullOrEmpty(cardId) || drawn < TrainingConfig.MinGames)
						continue;
					entries.Add((cardId!, wins / (double)drawn, drawn));
				}
				if(entries.Count < TrainingConfig.MinClassRows)
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
		internal static Dictionary<CardClass, double> HsReplayTiers(JObject hs)
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
					if(wr != null && n >= TrainingConfig.MinGames)
						rates.Add(wr.Value);
				}
				if(rates.Count >= TrainingConfig.MinClassRows)
					tiers[cls] = rates.Average();
			}
			return tiers;
		}

		/// <summary>
		/// Leave-one-source-out check of the hero-pick tier RANKING: if the two sources
		/// don't rank the classes the same way, the tier list shown at the hero pick is
		/// not trustworthy for that patch — investigate before shipping a retrain.
		/// </summary>
		internal static void BacktestClassTiers(
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

		internal static Dictionary<CardClass, int> Ranks(
			IReadOnlyList<CardClass> classes, Dictionary<CardClass, double> tiers)
		{
			var ordered = classes.OrderByDescending(c => tiers[c]).ToList();
			var ranks = new Dictionary<CardClass, int>();
			for(var i = 0; i < ordered.Count; i++)
				ranks[ordered[i]] = i;
			return ranks;
		}
	}
}
