using System.Collections.Generic;

namespace HdtArenaHelper
{
	/// <summary>
	/// Computes a deck-context synergy bonus for an offered card, given the cards
	/// already drafted. Synergy rules are derived from objective card metadata
	/// (mechanics, tribes, spell schools, stat curve, keywords): the more the offered
	/// card fits what has already been drafted, the higher the bonus; anti-synergies
	/// (e.g. over-loading one slot with no payoff) produce a penalty.
	/// </summary>
	public interface ISynergyEngine
	{
		/// <summary>
		/// Points to add to the card's base score (can be negative), roughly on a
		/// -10..+15 scale so synergy nudges but does not dominate raw winrate.
		/// </summary>
		double GetSynergyBonus(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds);
	}

	/// <summary>No-op engine used until the full synergy port is wired in.</summary>
	public sealed class NullSynergyEngine : ISynergyEngine
	{
		public double GetSynergyBonus(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds) => 0;
	}
}
