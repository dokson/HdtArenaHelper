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
		/// The bonus plus, when meaningful, the dominant labeled reason (shown in the
		/// overlay so the user knows WHY a score was nudged).
		/// </summary>
		SynergyResult GetSynergy(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds);
	}

	/// <summary>
	/// A synergy verdict: points to add to the card's base score (can be negative;
	/// bounded by the implementation — MetadataSynergyEngine clamps to ±3 so synergy
	/// only breaks ties and never overrides a solid win-rate) and a short human-readable
	/// label of the dominant contribution, or null when nothing meaningful fired.
	/// </summary>
	public readonly struct SynergyResult
	{
		public double Bonus { get; }
		public string? TopReason { get; }

		public SynergyResult(double bonus, string? topReason = null)
		{
			Bonus = bonus;
			TopReason = topReason;
		}
	}

	/// <summary>No-op engine (kept for tests and as an off-switch).</summary>
	public sealed class NullSynergyEngine : ISynergyEngine
	{
		public SynergyResult GetSynergy(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds) => default;
	}
}
