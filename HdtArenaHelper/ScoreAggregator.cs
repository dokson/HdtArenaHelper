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

		/// <summary>
		/// True when this came from an empirical win-rate source, whether or not a per-card sample
		/// size travelled with it. Games alone cannot answer that: a class TIER is real win-rate
		/// data backed by a whole bucket rather than one card, so it carries no games — and reading
		/// "no games" as "no data" made the hero pick claim its win-rates were unavailable while
		/// displaying three of them, and star every class as low-confidence.
		/// </summary>
		public bool FromSample { get; }

		// fromSample is REQUIRED, not defaulted: it silently inherited "false" three times, each time
		// a real display bug. Every caller states whether its number came from real games.
		public ScoreComponent(string sourceName, double normalizedScore, double weight,
			int? games, bool fromSample)
		{
			SourceName = sourceName;
			NormalizedScore = normalizedScore;
			Weight = weight;
			Games = games;
			FromSample = fromSample;
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

		/// <summary>True when at least one empirical win-rate source contributed.</summary>
		public bool HasWinRateData
		{
			get
			{
				foreach(var c in Components)
				{
					if(c.FromSample)
						return true;
				}
				return false;
			}
		}

		/// <summary>True when no real win-rate sample of meaningful size backs this score
		/// (heuristic-only, or thin data): the overlay marks it so 50-ish scores from
		/// solid data and from guesswork stop looking identical. A win-rate source that reports no
		/// per-card sample (the class tier at the hero pick) is NOT low confidence — it is backed by
		/// a whole class bucket.</summary>
		public bool IsLowConfidence => HasData && !HasWinRateData
			|| HasData && MaxGames != null && MaxGames < LowConfidenceGames;

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
		/// <summary>The score a card with no information maps to (the plaque's midpoint).</summary>
		public const double NeutralScore = 50.0;

		/// <summary>
		/// Fraction of a model-only score's deviation from <see cref="NeutralScore"/> that is kept
		/// when no empirical sample backs the card.
		///
		/// Measured as a REGRESSION SLOPE, which is the only thing a shrink factor can come from:
		/// regress held-out truth on the model's prediction and the slope is how much of a claimed
		/// deviation is real. On the thinnest-sampled decile that slope is 0.31, against 0.92 on a
		/// random holdout — the regime the display mapping was calibrated on — so the constant is
		/// the ratio, 0.34. Emitted every refit as `model_only_shrink_measured` in `metrics.json`;
		/// re-derive it there rather than reasoning about it.
		///
		/// The value barely moved (it was 0.35), but the ORIGINAL derivation was wrong: it divided
		/// two rank correlations, which is not a slope and answers a different question. Do not
		/// restore that reasoning just because the number it produced happened to land close.
		/// The random-holdout slope carries se 0.16, so anything in 0.30-0.40 is indistinguishable
		/// here — do not chase the third decimal between refits.
		/// </summary>
		public const double ModelOnlyShrink = 0.34;

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
			var rated = new List<(IArenaDataSource Source, SourceScore Score)>(_sources.Count);
			foreach(var source in _sources)
			{
				var score = source.GetNormalizedScore(dbfId, draftClass);
				if(score != null)
					rated.Add((source, score.Value));
			}

			// Per-card precision weighting: scale each EMPIRICAL source's configured weight by its
			// sample confidence, using the same n/(n+k) factor the shrinkage applies — a 5000-game
			// estimate must not average 50/50 against a 30-game one.
			//
			// Model-based sources (no sample) must then lose the SAME fraction collectively, or the
			// blend silently hands them authority as the real evidence thins: with the heuristic
			// held at its full configured weight, its share of a 0.5/0.5/0.5 blend went from the
			// intended 33% to 50% at 60 games and 67% at 20 — and the holdout measurements say the
			// heuristic is at its WORST exactly on thinly-sampled cards. Scaling it by the
			// empirical sources' collective confidence keeps the configured ratio at every sample
			// size, so thin data can never promote the model.
			// The denominator is every empirical source's CONFIGURED weight, whether or not it
			// rated THIS card — not just the ones that did. Using only the raters reopened the same
			// hole from the other side: a card covered by one feed instead of two halved the
			// denominator and pushed the model's share from a third to a half, and single-feed
			// coverage correlates with exactly the obscure, thin cards where the model is worst.
			// A source with no data for a card must lower confidence, never promote the model.
			double empiricalConfigured = 0, empiricalEffective = 0;
			foreach(var source in _sources)
			{
				if(!source.HasSamples)
					continue;
				empiricalConfigured += source.Weight;
				// A feed with no row for this card contributes nothing to the numerator: it is
				// missing evidence, which must lower confidence rather than hand weight elsewhere.
				var covered = rated.Any(r => ReferenceEquals(r.Source, source));
				if(covered)
				{
					var games = rated.First(r => ReferenceEquals(r.Source, source)).Score.Games;
					empiricalEffective += source.Weight * Confidence(games);
				}
			}
			// When NO empirical source has anything for this card, the model is all there is: the
			// factor must be 1 so it can still score (the shrink below then bounds what it claims).
			// Scaling it to 0 here — as a first attempt at closing the coverage hole did — zeroes
			// the only remaining weight, so weightTotal hits 0, the card returns BlendedScore.Empty
			// and shows as "—". That silently removed the backstop from exactly the new and obscure
			// cards it exists for, and made the shrink below unreachable in the shipped wiring.
			var modelFactor = empiricalConfigured > 0 && empiricalEffective > 0
				? empiricalEffective / empiricalConfigured
				: 1.0;

			var components = new List<ScoreComponent>(rated.Count);
			double weightedSum = 0;
			double weightTotal = 0;
			foreach(var (source, score) in rated)
			{
				var weight = score.Games == null
					? source.Weight * modelFactor
					: source.Weight * Confidence(score.Games);
				components.Add(new ScoreComponent(source.Name, score.Score, weight, score.Games,
					fromSample: source.HasSamples));
				weightedSum += score.Score * weight;
				weightTotal += weight;
			}

			if(weightTotal <= 0)
				return BlendedScore.Empty;

			var baseScore = weightedSum / weightTotal;

			// When NO empirical source has a sample for this card, the score rests entirely on
			// the offline model — and that is measured to be the one place it does not work.
			// Backtested on a held-out decile of thinnest-sampled cards (the closest available
			// proxy for "no data at all", and an optimistic one): rank correlation 0.09 vs 0.28
			// on a random holdout, and MAE 4.5 against a target spread of 5.2 — i.e. no better
			// than predicting a constant. Neither label noise nor range restriction explains it
			// (the two win-rate feeds still agree 0.43 there, and the thin group's spread is
			// LARGER, not smaller).
			//
			// So shrink an unmeasured card's score toward neutral instead of letting the model
			// state a confident number it cannot support. Shrinking is monotone, so the ordering
			// AMONG unmeasured cards is untouched — what it removes is their ability to outrank a
			// well-sampled card on the strength of a guess. The overlay already flags these as
			// low confidence; this stops them merely LOOKING uncertain while scoring as if sure.
			var hasSample = false;
			foreach(var c in components)
			{
				if(c.Games.HasValue)
				{
					hasSample = true;
					break;
				}
			}
			if(!hasSample)
				baseScore = NeutralScore + (baseScore - NeutralScore) * ModelOnlyShrink;

			var synergy = _synergyEngine?.GetSynergy(dbfId, draftedDbfIds, draftClass) ?? default;
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
