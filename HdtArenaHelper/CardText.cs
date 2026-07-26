using System.Text.RegularExpressions;
using HearthDb;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// Card text prepared for pattern matching, and the whitespace convention every text pattern in
	/// this project must follow.
	///
	/// This class exists because the same bug was found twice, in two files, months apart. Card text
	/// carries the client's own TOOLTIP LINE BREAKS as newlines, so a pattern written with a literal
	/// space silently stops matching whenever Blizzard happens to wrap mid-phrase — and it fails
	/// SILENTLY, which is why both instances survived a green test suite. <see cref="DeckMulliganAdvisor"/>
	/// learned this and collapsed its whitespace; the heuristic and the synergy engine did not, and
	/// the synergy engine then paid for it again: measured against the live pool, a literal space cost
	/// the summon-from-deck rule 2 of the 6 cards it exists for, the tribal dependency set 69
	/// (card, tribe) pairs and the generation veto 14. One home for the convention means the next
	/// pattern cannot re-learn it the hard way.
	///
	/// Two normalized forms, deliberately NOT unified: see <see cref="Normalized"/>.
	/// </summary>
	internal static class CardText
	{
		private static readonly Regex Markup = new Regex(@"<[^>]+>|\[x\]", RegexOptions.Compiled);
		private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

		/// <summary>
		/// The scoring form: localized text, markup stripped, lower-cased — and newlines left ALONE.
		/// Shared by the heuristic and the synergy engine.
		///
		/// It does not collapse whitespace, and that is a constraint rather than an oversight: the
		/// heuristic's weights are ridge-fit against the features these very patterns extract, so
		/// collapsing here would move features, move every golden score, and require a refit. Patterns
		/// reading this form must therefore use <see cref="WithFlexibleSpaces"/> or write <c>\s+</c>
		/// themselves. Once the heuristic is refit, this and <see cref="Flattened"/> can converge.
		/// </summary>
		internal static string Normalized(Card card)
		{
			var text = card.GetLocText(Locale.enUS) ?? "";
			return Markup.Replace(text, " ").ToLowerInvariant();
		}

		/// <summary>
		/// The form with markup stripped and all whitespace collapsed to single spaces, so a pattern
		/// with plain spaces is safe against tooltip wrapping. Used where no fitted model depends on
		/// the exact bytes: the mulligan advisor, and name comparisons.
		/// </summary>
		internal static string Flattened(Card card)
			=> string.IsNullOrEmpty(card.Text)
				? string.Empty
				: Flatten(Markup.Replace(card.Text, " "));

		/// <summary>Collapse every whitespace run to a single space.</summary>
		internal static string Flatten(string text) => Whitespace.Replace(text, " ");

		/// <summary>
		/// Rewrite a pattern's literal spaces as <c>\s+</c>, so it matches across a tooltip line
		/// break. Apply this to any multi-word pattern read against <see cref="Normalized"/>.
		/// </summary>
		internal static string WithFlexibleSpaces(string pattern) => pattern.Replace(" ", @"\s+");

		/// <summary>
		/// Text with the class name "demon hunter" removed. Without this, every Demon Hunter card
		/// that names its own class matched <c>\bdemons?\b</c> and read as a Demon tribal payoff.
		/// </summary>
		internal static string StripClassNames(string text) => DemonHunterRe.Replace(text, " ");

		private static readonly Regex DemonHunterRe =
			new Regex(@"\bdemon\s+hunter\b", RegexOptions.Compiled);
	}
}
