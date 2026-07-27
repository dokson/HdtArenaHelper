using HdtArenaHelper.CardDatabase;
using HearthDb;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// CardText owns the one convention that two separate files got wrong: card text carries the
	/// client's tooltip line breaks as newlines, so a pattern written with a literal space stops
	/// matching wherever Blizzard happened to wrap. These tests pin the convention itself and the
	/// boundary between the two normalized forms — the second of which is load-bearing, since the
	/// heuristic's weights are fit against the exact bytes Normalized produces.
	/// </summary>
	public class CardTextTests
	{
		// "Summon a\n1-Cost minion from\nyour deck." — the card whose wrapping exposed the bug.
		private static readonly CardEntry WrappedCard = HSCard.SkydivingInstructor;
		// No text worth wrapping.
		private static readonly CardEntry PlainCard = HSCard.ChillwindYeti;
		// "Whenever your weapon is destroyed, equip a random Demon Hunter weapon" — a Demon Hunter
		// card that really does name its own class. The fixture this replaced was labelled
		// "Metamorphosis, names its own class" and was in fact Chaos Nova, whose whole text is "Deal
		// $4 damage to all minions": it names no class, so the assertion below passed vacuously and
		// would have kept passing with StripClassNames deleted. Found by naming the fixture, which is
		// the argument for naming fixtures.
		private static readonly CardEntry DemonHunterCard = HSCard.InstrumentSmasher;

		private static Card Card(CardEntry card) => Cards.All[card.CardId];

		[Fact]
		public void The_pool_really_does_wrap_card_text_mid_phrase()
		{
			// The premise every other test here rests on. If a HearthDb update ever stopped shipping
			// these newlines, the \s+ convention would still be correct but this suite would be
			// testing nothing — so the premise is asserted rather than assumed.
			Assert.Contains("\n", Card(WrappedCard).GetLocText(HearthDb.Enums.Locale.enUS));
		}

		[Fact]
		public void Normalized_strips_markup_and_lowercases()
		{
			var text = CardText.Normalized(Card(WrappedCard));

			Assert.DoesNotContain("<b>", text);
			Assert.DoesNotContain("[x]", text);
			Assert.Equal(text.ToLowerInvariant(), text);
		}

		[Fact]
		public void Normalized_KEEPS_the_line_breaks()
		{
			// Not an oversight, a constraint: the heuristic's ridge weights are fit against the
			// features these patterns extract from this exact text, so collapsing here would move
			// every golden score and require a refit. Patterns compensate with \s+ instead. Pinned
			// so that a future "tidy-up" has to notice it is a scoring change, not a cleanup.
			Assert.Contains("\n", CardText.Normalized(Card(WrappedCard)));
		}

		[Fact]
		public void Flattened_collapses_the_line_breaks()
		{
			var text = CardText.Flattened(Card(WrappedCard));

			Assert.DoesNotContain("\n", text);
			Assert.Contains("minion from your deck", text);
		}

		[Fact]
		public void Flattened_is_empty_rather_than_null_for_a_card_with_no_text()
		{
			// Every caller pattern-matches the result, so the no-text case must be a harmless empty
			// string: a null here would throw from inside a scoring path that is supposed to fail soft.
			Assert.Equal(string.Empty, CardText.Flattened(Card(HSHero.GarroshHellscream)));
		}

		[Fact]
		public void A_literal_space_pattern_MISSES_the_wrapped_card()
		{
			// The bug, reproduced against the real card. This is the control for the next test:
			// without it, WithFlexibleSpaces could be a no-op and its test would still pass.
			var text = CardText.Normalized(Card(WrappedCard));

			Assert.DoesNotMatch(@"\bfrom your deck\b", text);
		}

		[Fact]
		public void WithFlexibleSpaces_makes_the_same_pattern_match()
		{
			var text = CardText.Normalized(Card(WrappedCard));

			Assert.Matches(CardText.WithFlexibleSpaces(@"\bfrom your deck\b"), text);
		}

		[Fact]
		public void WithFlexibleSpaces_converts_every_space_not_just_the_first()
		{
			Assert.Equal(@"a\s+b\s+c", CardText.WithFlexibleSpaces("a b c"));
		}

		[Fact]
		public void WithFlexibleSpaces_leaves_a_pattern_without_spaces_alone()
		{
			Assert.Equal(@"\bsummon\b[^.]*", CardText.WithFlexibleSpaces(@"\bsummon\b[^.]*"));
		}

		[Fact]
		public void StripClassNames_removes_the_class_but_keeps_the_tribe()
		{
			// The trap this exists for: \bdemons?\b matched "Demon Hunter", so every DH card naming
			// its own class read as a Demon tribal payoff — and got exposed to the dead-card penalty
			// for not having drafted Demons. The card must stop looking like a Demon reference.
			var raw = CardText.Normalized(Card(DemonHunterCard));
			// The control, and the reason this test is worth anything: the fixture must actually
			// contain the class name, or stripping it proves nothing. The previous fixture did not.
			Assert.Matches(@"\bdemons?\b", raw);

			var text = CardText.StripClassNames(raw);

			Assert.DoesNotContain("demon hunter", text);
			Assert.DoesNotMatch(@"\bdemons?\b", text);
		}

		[Fact]
		public void StripClassNames_survives_the_class_name_being_split_by_a_line_break()
		{
			// The class name is two words, so it is exposed to the very wrapping this class is about.
			// Its own pattern already uses \s+; pinned so it stays that way.
			Assert.Equal("  card", CardText.StripClassNames("demon\nhunter card"));
		}

		[Fact]
		public void Flatten_reduces_any_whitespace_run_to_one_space()
		{
			Assert.Equal("a b c", CardText.Flatten("a \n b \t\t c"));
		}

		[Fact]
		public void Normalized_and_Flattened_agree_once_whitespace_is_removed()
		{
			// The two forms differ ONLY in whitespace and letter case. Pinning that keeps them from
			// quietly diverging into two different notions of "the card's text" — which is the split
			// that let the same bug be fixed in one file and left in another.
			var a = CardText.Flatten(CardText.Normalized(Card(PlainCard))).Trim();
			var b = CardText.Flatten(CardText.Flattened(Card(PlainCard))).Trim().ToLowerInvariant();

			Assert.Equal(a, b);
		}
	}
}
