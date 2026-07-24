using System.Threading.Tasks;

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

		/// <summary>Loads (and caches) the data. Safe to call repeatedly.</summary>
		Task EnsureLoadedAsync();

		/// <summary>
		/// Normalized score in [0, 100] for a card by HearthDb dbf id,
		/// or null if this source has no rating for the card.
		/// </summary>
		double? GetNormalizedScore(int dbfId);
	}
}
