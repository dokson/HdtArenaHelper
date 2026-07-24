using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>Per-source contribution to a card's blended score.</summary>
	public class ScoreComponent
	{
		public string SourceName { get; }
		public double NormalizedScore { get; }
		/// <summary>Effective blend weight: the source's configured weight scaled by
		/// this card's sample confidence.</summary>
		public double Weight { get; }
		/// <summary>Games behind this source's estimate; null for model-based sources.</summary>
		public int? Games { get; }

		public ScoreComponent(string sourceName, double normalizedScore, double weight, int? games = null)
		{
			SourceName = sourceName;
			NormalizedScore = normalizedScore;
			Weight = weight;
			Games = games;
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

		/// <summary>The dominant synergy reason ("fills the 3-drop gap"), or null.</summary>
		public string? SynergyReason { get; }

		public bool HasData => Components.Count > 0;

		/// <summary>Below this many games behind the best sample the score is flagged
		/// low-confidence in the overlay (dim + asterisk).</summary>
		public const int LowConfidenceGames = 200;

		/// <summary>The largest sample behind any contributing source, or null when only
		/// model-based sources contributed.</summary>
		public int? MaxGames
		{
			get
			{
				int? max = null;
				foreach(var c in Components)
				{
					if(c.Games.HasValue && c.Games.Value > (max ?? -1))
						max = c.Games;
				}
				return max;
			}
		}

		/// <summary>True when no real win-rate sample of meaningful size backs this score
		/// (heuristic-only, or thin data): the overlay marks it so 50-ish scores from
		/// solid data and from guesswork stop looking identical.</summary>
		public bool IsLowConfidence => HasData && (MaxGames ?? 0) < LowConfidenceGames;

		public BlendedScore(double value, IReadOnlyList<ScoreComponent> components,
			double synergyBonus, string? synergyReason = null)
		{
			Value = value;
			Components = components;
			SynergyBonus = synergyBonus;
			SynergyReason = synergyReason;
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

		/// <summary>
		/// How many sources have data right now. Monotone during a session, so the
		/// overlay re-renders the current pick when a late source (e.g. a win-rate
		/// download) comes online — a bool latch would never fire again.
		/// </summary>
		public int LoadedSourceCount => _sources.Count(s => s.IsLoaded);

		public Task EnsureLoadedAsync()
			=> Task.WhenAll(_sources.Select(s => s.EnsureLoadedAsync()));

		/// <summary>
		/// Blended score for one offered card, given the cards already drafted (used for
		/// synergy) and the class being drafted (INVALID when unknown, e.g. at the hero
		/// pick). <paramref name="draftedDbfIds"/> may be empty.
		/// </summary>
		public BlendedScore Score(int dbfId, IReadOnlyCollection<int> draftedDbfIds,
			CardClass draftClass = CardClass.INVALID)
		{
			var components = new List<ScoreComponent>();
			double weightedSum = 0;
			double weightTotal = 0;

			foreach(var source in _sources)
			{
				var rated = source.GetNormalizedScore(dbfId, draftClass);
				if(rated == null)
					continue;
				// Per-card precision weighting: scale the source's configured weight by
				// its sample confidence, using the same n/(n+k) factor the shrinkage
				// applies — a 5000-game estimate must not average 50/50 against a
				// 30-game one. Model-based sources (no sample) keep their full weight:
				// their trust already lives in the configured weight.
				var weight = source.Weight * Confidence(rated.Value.Games);
				components.Add(new ScoreComponent(source.Name, rated.Value.Score, weight, rated.Value.Games));
				weightedSum += rated.Value.Score * weight;
				weightTotal += weight;
			}

			if(weightTotal <= 0)
				return BlendedScore.Empty;

			var baseScore = weightedSum / weightTotal;

			var synergy = _synergyEngine?.GetSynergy(dbfId, draftedDbfIds) ?? default;
			var value = System.Math.Max(0, System.Math.Min(100, baseScore + synergy.Bonus));

			return new BlendedScore(value, components, synergy.Bonus, synergy.TopReason);
		}

		/// <summary>
		/// Sample confidence in [0, 1]: n/(n+k) with the shared shrinkage prior k, so the
		/// blend discounts a source exactly as much as the shrinkage already distrusts
		/// its estimate. Model-based sources (no sample) return 1.0.
		/// </summary>
		private static double Confidence(int? games)
			=> games == null ? 1.0 : games.Value / (double)(games.Value + ScoreMath.ShrinkGames);
	}
}
