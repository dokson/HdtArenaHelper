namespace HdtArenaHelper
{
	/// <summary>
	/// Aggregated arena stats for a single card, keyed by its HearthDb dbf id.
	/// </summary>
	public class ArenaCardScore
	{
		public int DbfId { get; }

		/// <summary>
		/// Drawn win-rate: win-rate of games where this card was actually drawn (0-100),
		/// or null if unknown. Falls back to the deck-inclusion win-rate for the rare
		/// entry that lacks the drawn metric.
		/// </summary>
		public double? DrawnWinrate { get; }

		/// <summary>How often the card is drafted (0-100), or null if unknown.</summary>
		public double? IncludedPopularity { get; }

		/// <summary>Sample size behind the win-rate, if known (used to prefer robust entries).</summary>
		public int? Games { get; }

		public ArenaCardScore(int dbfId, double? drawnWinrate, double? includedPopularity, int? games = null)
		{
			DbfId = dbfId;
			DrawnWinrate = drawnWinrate;
			IncludedPopularity = includedPopularity;
			Games = games;
		}

		/// <summary>
		/// A single 0-100 number to display/sort by: the raw drawn win-rate. The blended
		/// tier score (shrinkage, normalization, synergy) lives in the scoring pipeline.
		/// </summary>
		public double DisplayScore => DrawnWinrate ?? 0;
	}
}
