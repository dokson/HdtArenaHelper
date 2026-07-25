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
		// Kept separate from YAvg even though the two are now the same number: YAvg is "the target
		// the fit consumes" and WrHs is "what HSReplay reported". They coincided before too — YAvg
		// was the average of two feeds — and collapsing them would erase the distinction exactly
		// when a second source returns.
		public readonly double WrHs;
		public Row(string cardId, CardClass cls, double yAvg, int numGames, double wrHs)
		{
			CardId = cardId; Class = cls; YAvg = yAvg; NumGames = numGames;
			WrHs = wrHs;
		}
	}

	/// <summary>
	/// Turns the public win-rate feed into the (card, class) rows the fit consumes.
	///
	/// It used to take TWO feeds and cross-check the hero-pick tier ranking of one against the
	/// other — a leave-one-source-out gate that failed loudly when they disagreed. The second feed
	/// was withdrawn in 0.1.5 at its provider's request, so nothing validates the tier ranking now.
	/// Do not mistake the silence for agreement.
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

	}
}
