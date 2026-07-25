using System;
using System.Collections.Generic;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>What to do with one card in the opening hand. Ordinal on purpose.</summary>
	public enum MulliganVerdict
	{
		/// <summary>Nothing decisive to say — the default, and the honest answer most of the time.</summary>
		Situational,
		Keep,
		Toss,
	}

	/// <summary>
	/// A verdict plus the FACT behind it, in words.
	///
	/// There is deliberately no number here. A keep-percentage would have to come from somewhere,
	/// and the two candidates are both refused: another project's measured counters (removed in
	/// 0.1.5), or a scale we invented — which is the mistake REPORT.md already measured, where
	/// hand-tuned card values scored worse than nothing. What IS available is the drafted deck,
	/// and against it a statement like "your only 2-drop, the deck has two more" is checkable by
	/// the player in the moment. That is the whole design: facts the deck makes true, not scores.
	/// </summary>
	public readonly struct MulliganCardVerdict
	{
		public MulliganVerdict Verdict { get; }
		/// <summary>One short clause naming the deck fact behind the verdict, or null.</summary>
		public string? Reason { get; }

		public MulliganCardVerdict(MulliganVerdict verdict, string? reason = null)
		{
			Verdict = verdict;
			Reason = reason;
		}
	}

	/// <summary>
	/// Judges an opening hand against the deck it was drafted from.
	///
	/// Kept separate from <see cref="IMulliganStatsSource"/> — measured keep statistics and a
	/// deck-relative judgement are different claims and must not be blended into one badge. The
	/// same separation <see cref="ISynergyEngine"/> keeps from <see cref="IArenaDataSource"/>.
	/// </summary>
	public interface IMulliganAdvisor
	{
		/// <summary>
		/// Verdicts for each card of <paramref name="handDbfIds"/>, in the same order — the overlay
		/// lays out one column per card, so an out-of-order or short list would put every verdict
		/// on the wrong card. Returns an empty list when it has nothing it can safely say.
		/// </summary>
		IReadOnlyList<MulliganCardVerdict> Evaluate(IReadOnlyList<int> handDbfIds,
			IReadOnlyList<int> deckDbfIds, CardClass deckClass, bool onCoin);

		/// <summary>
		/// The plugin's own 0-100 arena score for a card, or null when unknown. Supplied rather than
		/// computed here: the advisor answers "does this card fit this hand", and how good the card
		/// is in the abstract is already measured elsewhere from real win-rates. Tempo alone keeps a
		/// bad one-drop; quality alone keeps a bomb you cannot cast. The verdict needs both.
		/// </summary>
		void SetScoreSource(Func<int, double?>? scoreLookup);
	}
}
