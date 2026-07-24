using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HdtArenaHelper
{
	/// <summary>Per-source contribution to a card's blended score.</summary>
	public class ScoreComponent
	{
		public string SourceName { get; }
		public double NormalizedScore { get; }
		public double Weight { get; }

		public ScoreComponent(string sourceName, double normalizedScore, double weight)
		{
			SourceName = sourceName;
			NormalizedScore = normalizedScore;
			Weight = weight;
		}
	}

	/// <summary>The blended result for a single card.</summary>
	public class BlendedScore
	{
		/// <summary>Final 0-100 score (weighted mean of available sources + synergy).</summary>
		public double Value { get; }

		/// <summary>Per-source breakdown, for the tooltip / debug display.</summary>
		public IReadOnlyList<ScoreComponent> Components { get; }

		/// <summary>Deck-context synergy bonus already folded into <see cref="Value"/>.</summary>
		public double SynergyBonus { get; }

		public bool HasData => Components.Count > 0;

		public BlendedScore(double value, IReadOnlyList<ScoreComponent> components, double synergyBonus)
		{
			Value = value;
			Components = components;
			SynergyBonus = synergyBonus;
		}

		public static readonly BlendedScore Empty =
			new BlendedScore(0, new List<ScoreComponent>(), 0);
	}

	/// <summary>
	/// Merges several <see cref="IArenaDataSource"/> into one 0-100 score per card
	/// via a weighted mean over the sources that actually have data for the card
	/// (so a missing source lowers confidence, not the score). An optional synergy
	/// engine adds a deck-context bonus on top (see ISynergyEngine, wired in later).
	/// </summary>
	public class ScoreAggregator
	{
		private readonly List<IArenaDataSource> _sources;
		private ISynergyEngine? _synergyEngine;

		public ScoreAggregator(IEnumerable<IArenaDataSource> sources)
		{
			_sources = sources.Where(s => s.Weight > 0).ToList();
		}

		public void SetSynergyEngine(ISynergyEngine engine) => _synergyEngine = engine;

		public IReadOnlyList<IArenaDataSource> Sources => _sources;

		/// <summary>True once every weighted source has data available.</summary>
		public bool IsLoaded => _sources.All(s => s.IsLoaded);

		public Task EnsureLoadedAsync()
			=> Task.WhenAll(_sources.Select(s => s.EnsureLoadedAsync()));

		/// <summary>
		/// Blended score for one offered card, given the cards already drafted
		/// (used for synergy). <paramref name="draftedDbfIds"/> may be empty.
		/// </summary>
		public BlendedScore Score(int dbfId, IReadOnlyCollection<int> draftedDbfIds)
		{
			var components = new List<ScoreComponent>();
			double weightedSum = 0;
			double weightTotal = 0;

			foreach(var source in _sources)
			{
				var normalized = source.GetNormalizedScore(dbfId);
				if(normalized == null)
					continue;
				components.Add(new ScoreComponent(source.Name, normalized.Value, source.Weight));
				weightedSum += normalized.Value * source.Weight;
				weightTotal += source.Weight;
			}

			if(weightTotal <= 0)
				return BlendedScore.Empty;

			var baseScore = weightedSum / weightTotal;

			var synergyBonus = _synergyEngine?.GetSynergyBonus(dbfId, draftedDbfIds) ?? 0;
			var value = System.Math.Max(0, System.Math.Min(100, baseScore + synergyBonus));

			return new BlendedScore(value, components, synergyBonus);
		}
	}
}
