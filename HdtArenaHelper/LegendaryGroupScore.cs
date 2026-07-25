using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// Scores a "legendary group" pick — a legendary plus the 3-card package that comes with it, the
	/// first pick in BOTH The Arena and The Underground since the June-2025 rework.
	///
	/// Static and separate from the plugin on purpose: the tilt below is a scoring RULE and the
	/// provenance flag it forwards has been a live bug three times, and neither can be tested inside
	/// a private plugin method that needs HDT running.
	/// </summary>
	internal static class LegendaryGroupScore
	{
		/// <summary>
		/// How far the group's score leans toward its best card rather than its mean. The bomb is the
		/// part you cannot get later; the filler is the part you can.
		/// </summary>
		internal const double BestCardTilt = 0.35;

		internal static BlendedScore Score(ScoreAggregator aggregator, int legendaryDbfId,
			IReadOnlyList<int> packageDbfIds, IReadOnlyCollection<int> draftedDbfIds,
			CardClass draftClass)
		{
			var ids = new List<int> { legendaryDbfId };
			ids.AddRange(packageDbfIds);

			// Score each card against the drafted deck PLUS the rest of its own group. The package
			// is arriving together, so a tribal bundle (three Dragons behind a Dragon legendary)
			// makes its own payoffs live — synergy the engine already computes but never saw here,
			// because each card used to be scored against the drafted deck alone.
			var values = new List<double>();
			int? maxGames = null;
			// Whether ANY card in the group was backed by a win-rate feed. Forwarded explicitly: this
			// method SYNTHESIZES a component, and a synthesized one that forgets where its number
			// came from made the overlay announce "win-rate data unavailable" over three scored
			// legendary groups, and star every one of them as low-confidence.
			var fromSample = false;
			foreach(var id in ids)
			{
				var context = new List<int>(draftedDbfIds);
				foreach(var other in ids)
				{
					if(other != id)
						context.Add(other);
				}
				var s = aggregator.Score(id, context, draftClass);
				if(!s.HasData)
					continue;
				values.Add(s.Value);
				if(s.HasWinRateData)
					fromSample = true;
				// Carry the group's best sample so the confidence flag reflects the underlying data,
				// not the synthesized "group avg" component.
				if(s.MaxGames.HasValue && s.MaxGames.Value > (maxGames ?? -1))
					maxGames = s.MaxGames;
			}
			if(values.Count == 0)
				return BlendedScore.Empty;

			// A mean answers "average card quality added", which is the right quantity but the wrong
			// decision criterion for THIS pick: the first pick is the only guaranteed legendary of
			// the run, while ~29 later picks can supply average bodies. A plain mean therefore
			// prefers four solid cards over a bomb plus filler, which inverts how the choice
			// actually plays. Tilt toward the best card in the group without ignoring the rest.
			var mean = values.Average();
			var best = values.Max();
			var score = mean + BestCardTilt * (best - mean);
			var components = new List<ScoreComponent>
			{
				new ScoreComponent($"group {values.Count}/{ids.Count} (avg {mean:0.#}, best {best:0.#})",
					score, 1.0, maxGames, fromSample),
			};
			return new BlendedScore(score, components, 0);
		}
	}
}
