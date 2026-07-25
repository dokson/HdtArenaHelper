using System.Collections.Generic;
using HearthDb.Enums;

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
		/// <param name="offeredDbfId">The card being scored.</param>
		/// <param name="draftedDbfIds">Everything already in the deck, one entry per copy.</param>
		/// <param name="draftClass">
		/// The class being drafted, INVALID when unknown. Only used to ask how available a tribe is
		/// to that class before condemning a payoff as dead — in a class draft the tribe arrives
		/// with the class, which the class-blind version could not see.
		/// </param>
		SynergyResult GetSynergy(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds,
			CardClass draftClass = CardClass.INVALID);
	}

	/// <summary>
	/// A synergy verdict: points to add to the card's base score (can be negative) and a short
	/// human-readable label of the dominant contribution, or null when nothing meaningful fired.
	/// Bounded by the implementation: MetadataSynergyEngine clamps its FUZZY synergy to ±3 (a
	/// tie-breaker that never overrides a solid win-rate), with a separate, larger penalty for a
	/// hard-dead card (a tribal payoff drafted with none of its tribe) that can reorder a pick.
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
		public SynergyResult GetSynergy(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds,
			CardClass draftClass = CardClass.INVALID) => default;
	}
}
