using System.Collections.Generic;
using System.Threading.Tasks;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// A pluggable provider of arena card ratings (e.g. HSReplay, Firestone).
	///
	/// Each source is responsible for loading its own data and exposing a
	/// <b>normalized 0-100 score</b> per card so that heterogeneous metrics
	/// (winrate %, tier score, ...) can be blended by <see cref="ScoreAggregator"/>.
	/// </summary>
	public interface IArenaDataSource
	{
		/// <summary>Human-readable name shown in the score breakdown.</summary>
		string Name { get; }

		/// <summary>Relative weight when blending sources. 0 disables the source.</summary>
		double Weight { get; }

		/// <summary>True once <see cref="EnsureLoadedAsync"/> has data available.</summary>
		bool IsLoaded { get; }

		/// <summary>
		/// True when this source's scores carry an empirical sample size (a win-rate feed), false
		/// for a model that scores every card from metadata. The blend needs this per SOURCE, not
		/// per card: an empirical source with no data for one card must still lower that card's
		/// confidence, and must never be mistaken for a model source — otherwise a card covered by
		/// one feed instead of two silently promotes the model's share of the blend.
		/// </summary>
		bool HasSamples { get; }

		/// <summary>
		/// Loads (and caches) the data. Safe to call repeatedly, but implementations are
		/// NOT required to tolerate concurrent calls on the same instance: the plugin's
		/// warm-up loop is the sole caller and awaits each call before the next.
		/// </summary>
		Task EnsureLoadedAsync();

		/// <summary>
		/// Normalized score in [0, 100] for a card by HearthDb dbf id, or null if this
		/// source has no rating for the card. <paramref name="draftClass"/> is the class
		/// being drafted (INVALID when unknown, e.g. at the hero pick): sources with
		/// per-class data may rate the card in that class's context and should fall back
		/// to their class-agnostic rating otherwise.
		/// </summary>
		SourceScore? GetNormalizedScore(int dbfId, CardClass draftClass = CardClass.INVALID);
	}

	/// <summary>
	/// A source that can also estimate a class's ARENA win-rate in real percentage points, for
	/// the hero pick. Deliberately separate from <see cref="IArenaDataSource"/>: it is a display
	/// figure on a different scale from the blend's 0-100, and only the win-rate feeds can produce
	/// it — the offline model has no notion of a class's win-rate at all. The blend does NOT use
	/// it: measured, it ranks the classes the same as the pool-quality tier already does
	/// (Spearman 0.96), so it buys readability, not accuracy.
	/// </summary>
	public interface IClassWinRateSource
	{
		/// <summary>
		/// Class -> estimated win-rate in percentage points, or null until loaded. A calibrated
		/// estimate derived from per-card tallies, not a published figure — see
		/// <see cref="ScoreMath.RecentreClassWinRates"/> — so label it as an estimate on screen.
		/// </summary>
		IReadOnlyDictionary<CardClass, double>? ClassWinRates { get; }
	}

	/// <summary>
	/// How much of a class's card usage a tribe actually accounts for — "if I draft this Dragon
	/// payoff as a Hunter, will Dragons ever show up?". Measured from the win-rate feed's per-class
	/// popularity, so it is real data per patch rather than a hand-written table, and it is the one
	/// input to the synergy engine that public data CAN validate.
	/// </summary>
	public interface IClassTribeAvailabilitySource
	{
		/// <summary>
		/// Share of <paramref name="cls"/>'s deck slots held by <paramref name="race"/>, in percent,
		/// or null when unknown (not loaded, class absent, tribe never seen). Null must leave
		/// behaviour exactly as it was without this signal — the caller cannot tell "no data" from
		/// "zero availability" and must not guess.
		/// </summary>
		double? TribeShare(CardClass cls, Race race);
	}

	/// <summary>
	/// A source's rating for one card: the normalized 0-100 score plus the effective
	/// sample size behind it, so the blend can weight per-card precision (5000 games
	/// must not average 50/50 against 30) and the overlay can show confidence.
	/// </summary>
	public readonly struct SourceScore
	{
		/// <summary>Normalized score in [0, 100].</summary>
		public double Score { get; }

		/// <summary>
		/// Games behind the estimate the score was computed from, or null for a
		/// model-based source with no per-card sample (the offline heuristic).
		/// </summary>
		public int? Games { get; }

		public SourceScore(double score, int? games = null)
		{
			Score = score;
			Games = games;
		}
	}
}
