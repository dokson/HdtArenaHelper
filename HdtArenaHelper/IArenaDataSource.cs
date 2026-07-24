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
