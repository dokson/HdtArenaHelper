namespace HdtArenaHelper
{
	/// <summary>
	/// Aggregated arena stats for a single card, keyed by its HearthDb dbf id.
	/// </summary>
	public class ArenaCardScore
	{
		public int DbfId { get; }

		/// <summary>Winrate of games where this card was in the deck (0-100), or null if unknown.</summary>
		public double? IncludedWinrate { get; }

		/// <summary>How often the card is drafted (0-100), or null if unknown.</summary>
		public double? IncludedPopularity { get; }

		/// <summary>Sample size behind the win-rate, if known (used to prefer robust entries).</summary>
		public int? Games { get; }

		public ArenaCardScore(int dbfId, double? includedWinrate, double? includedPopularity, int? games = null)
		{
			DbfId = dbfId;
			IncludedWinrate = includedWinrate;
			IncludedPopularity = includedPopularity;
			Games = games;
		}

		/// <summary>
		/// A single 0-100 number to display/sort by. For now this is just the
		/// included winrate; a richer tier model would blend winrate, popularity
		/// and deck-context synergies here.
		/// </summary>
		public double DisplayScore => IncludedWinrate ?? 0;
	}
}
