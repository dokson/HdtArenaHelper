using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HearthDb;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// Judges an opening hand from the drafted deck and the rules, and nothing else.
	///
	/// The premise, which is what makes it worth doing at all: a mulligan question is not "is this
	/// card good" — the draft score already answered that — but "is this card good IN THIS HAND,
	/// given the other 27 cards". Those are different questions and the second one is answerable
	/// locally, because the deck is fully known. Universal advice ("always keep two-drops") is what
	/// this deliberately does not do: keeping a 2-drop is obvious when the deck holds three and
	/// pointless when it holds nine.
	///
	/// Every verdict is ordinal and carries the deck fact behind it, never a number — see
	/// <see cref="MulliganCardVerdict"/> for why a percentage here would be invented rather than
	/// measured. The bar for speaking at all is high: <see cref="MulliganVerdict.Situational"/> is
	/// the default and the correct answer for most cards, because a wrong confident verdict at the
	/// mulligan costs a game while silence costs nothing.
	/// </summary>
	public class DeckMulliganAdvisor : IMulliganAdvisor
	{
		// Hearthstone's own numbers, not tuning knobs: an opening hand is 3 cards going first and
		// 4 going second, and turn 1 has one mana. Everything else below is derived from the deck.
		private const int EarlyCost = 2;
		private const int TopEndCost = 5;

		/// <summary>The first turn at which a card is "late" enough to lose to a cheaper play.</summary>
		private const int LateMidTurn = 4;

		/// <summary>
		/// The 0-100 score above which an expensive card stops being "too slow" and becomes the
		/// reason you are in the game at all. Arena is decided by bombs as often as by curve, and
		/// a card that wins the game on turn 6 is worth the two awkward turns before it — players
		/// hold those, and the tempo rule alone would mulligan them away.
		/// </summary>
		private const double BombScore = 70.0;

		private static bool IsBomb(Card card, Func<int, double?>? score)
		{
			var value = score?.Invoke(card.DbfId);
			return value.HasValue && value.Value >= BombScore;
		}

		/// <summary>
		/// Cards that reduce their own cost break the "printed cost = the turn it lands" assumption
		/// the tempo rules rest on. Soothsayer is the example that forced this: a 7-drop with
		/// Prepare is held BECAUSE it can be discounted into turn 4-5 and snowball from there, so
		/// judging it at 7 mana would mulligan away the plan.
		/// </summary>
		private static bool IsSelfDiscounting(Card card)
		{
			var text = CleanText(card);
			return text.Length > 0 && DiscountRe.IsMatch(text);
		}

		/// <summary>
		/// Cards whose effect needs a board you do not have yet. A Battlecry that returns, buffs or
		/// targets a friendly minion is blank on turn 1-2 and often actively bad — bouncing your
		/// own board to set up a combo costs you the tempo race you were keeping the card to win.
		/// </summary>
		private static bool NeedsExistingBoard(Card card)
		{
			var text = CleanText(card);
			return text.Length > 0 && NeedsBoardRe.IsMatch(text);
		}

		private static readonly Regex NeedsBoardRe = new Regex(
			// "friendly <anything>" covers the tribal wording too — a location that wants your
			// Beasts is as blank on turn 1 as one that wants your minions.
			@"\bfriendly\s+\w+|\banother minion\b|\bchoose a minion\b|\bminion you control\b" +
			@"|\badjacent\b|\byour\s+(minions|beasts|murlocs|dragons|demons|pirates|elementals" +
			@"|mechs|totems|undead|naga|quilboar|whelps)\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex DiscountRe = new Regex(
			@"\bprepare\b|\bcosts?\s*\(\d+\)\s*less\b|\bcosts?\s*\(0\)\b|\breduce.*\bcost\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		// Text patterns, precompiled once: the static Regex cache holds 15 entries and this class
		// plus the synergy engine would evict each other's patterns on every call otherwise.
		private static readonly Regex BoardImpactRe = new Regex(
			@"\bsummon\b|\bdeal[s]?\b|\bdestroy\b|\bgive[s]?\b|\btaunt\b|\bequip\b|\brestore\b" +
			@"|\bfreeze\b|\bmana crystal\b|\brefresh\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);
		// Cheap cards that hand you a CARD rather than a board presence. Treating these as "does
		// nothing" was the worst rule this class ever had: in arena a one-mana card that turns
		// into a real card is a play, not a wasted turn.
		private static readonly Regex GeneratesCardRe = new Regex(
			@"\bdiscover\b|\bdraw\b|\badd\b.*\b(to your hand|card)\b|\bcast\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private Func<int, double?>? _score;

		public void SetScoreSource(Func<int, double?>? scoreLookup) => _score = scoreLookup;

		/// <summary>
		/// The 0-100 arena score below which a cheap card stops being worth the turn it buys. Set
		/// at the pool median by construction (the display scale maps the median card to 50), so
		/// this reads as "at least an average card" rather than as a tuned threshold. Without a
		/// score — a card no win-rate covers — the tempo rules stand on their own.
		/// </summary>
		private const double PlayableScore = 50.0;

		public IReadOnlyList<MulliganCardVerdict> Evaluate(IReadOnlyList<int> handDbfIds,
			IReadOnlyList<int> deckDbfIds, CardClass deckClass, bool onCoin)
		{
			var empty = new MulliganCardVerdict[0];
			if(handDbfIds == null || handDbfIds.Count == 0 || deckDbfIds == null)
				return empty;

			var hand = handDbfIds.Select(Resolve).ToList();
			// All or nothing, for the same reason the draft overlay voids a partial choice: verdicts
			// are laid out by index, so one unresolved card shifts every later verdict onto its
			// neighbour. HearthDb is also empty for a moment at startup, and retrying beats lying.
			if(hand.Any(c => c == null))
				return empty;

			var deck = deckDbfIds.Select(Resolve).Where(c => c != null).Select(c => c!).ToList();
			// A deck we cannot see is the whole input: without it this is just a tier list again.
			if(deck.Count < 20)
				return empty;

			var verdicts = new List<MulliganCardVerdict>(hand.Count);
			for(var i = 0; i < hand.Count; i++)
				verdicts.Add(Judge(hand[i]!, i, hand!, deck, onCoin, _score));
			return verdicts;
		}

		/// <summary>
		/// The rules, in the order a strong player applies them: what the hand cannot do at all
		/// beats what a single card is worth. First match wins, and no rule may fire on a card the
		/// deck does not actually make true.
		/// </summary>
		private static MulliganCardVerdict Judge(Card card, int index, IReadOnlyList<Card> hand,
			IReadOnlyList<Card> deck, bool onCoin, Func<int, double?>? score)
		{
			var isBody = card.Type == CardType.MINION;

			// The Coin is a card, not a modifier: it moves every cost one turn earlier (a 3-drop
			// lands on turn 2), and it is itself a spell that other cards can care about. Modelling
			// it as an effective TURN rather than as one special-case rule is what keeps the rest of
			// the rules honest — otherwise "expensive" would mean the same thing on both sides of
			// the coin, which is exactly the mistake a universal mulligan chart makes.
			// Known simplification: the synergy side (a deck that wants a spell cast, a card that
			// wants a full board of mana) is not modelled here, only the tempo side.
			var turn = EffectiveTurn(card, onCoin);

			// HERO cards get no verdict, for the same reason the offline model refuses to score
			// them: their printed cost is not what they cost, and the tempo rules are built on the
			// printed cost. Judging them produced confident tosses on cards players hold.
			if(card.Type == CardType.HERO)
				return new MulliganCardVerdict(MulliganVerdict.Situational);

			// 1. TEMPO IS THE MACRO-RULE, and this is where an earlier version of this class had it
			//    backwards. It treated "cheap" as a condition attached to a thin deck; the mulligan
			//    is decided first by what the card DOES on turns one and two, and only then by the
			//    deck. A card that plays early gets kept whether or not the deck is short of them,
			//    because the cost of a dead opening hand does not depend on the rest of the deck.
			//    Cost 0 is excluded, and not as a rounding detail: cards whose real cost is set
			//    elsewhere (Zilliax's modules) report 0, and reading that as "the cheapest possible
			//    play" produced the single worst call this advisor has made.
			// A cheap SPELL is not tempo, whatever it does. Removal, counters and reach are cards
			// you want to draw INTO once there is a target; held from turn zero they answer a board
			// that does not exist yet. Only a permanent — a body, a weapon, a location — buys the
			// turn this rule is about.
			var isPermanent = card.Type == CardType.MINION || card.Type == CardType.WEAPON
				|| card.Type == CardType.LOCATION;

			if(card.Cost >= 1 && turn <= EarlyCost && isPermanent && AffectsBoard(card))
			{
				// The one deck-relative exception, kept here rather than as a later rule so it can
				// only ever DOWNGRADE a keep: holding a second copy of a slot the deck can refill
				// is not a second early play, it is one early play and a spare.
				var secondOfItsSlot = hand.Take(index).Count(c => c.Cost == card.Cost) >= 1
					&& deck.Count(c => c.Cost == card.Cost) >= 3;
				if(secondOfItsSlot)
					return new MulliganCardVerdict(MulliganVerdict.Situational,
						$"second {card.Cost}-drop");

				// Tempo is necessary, not sufficient. A cheap card the win-rate data rates below
				// the pool median buys a turn with a card that loses it back, and players do not
				// keep those — which is what separated the keeps from the misses when this rule was
				// cost-only. No score (a card nothing has measured) leaves the tempo rule standing.
				var quality = score?.Invoke(card.DbfId);
				if(quality.HasValue && quality.Value < PlayableScore)
					return new MulliganCardVerdict(MulliganVerdict.Situational,
						"weak for the slot");

				// A body whose effect needs a friendly board is not the turn-1 play it looks like.
				if(NeedsExistingBoard(card))
					return new MulliganCardVerdict(MulliganVerdict.Situational,
						"needs a board");

				// A Combo card played first is a vanilla body with its text switched off, and going
				// first there is nothing to combo off on turn 1-2. On the coin it is the opposite —
				// that case is a keep, handled below.
				if(!onCoin && card.Entity.GetTag(GameTag.COMBO) != 0)
					return new MulliganCardVerdict(MulliganVerdict.Situational,
						"Combo is off");

				// One health is not a body on turn 1-2: every class can remove it for free with a
				// hero power, several of them while developing their own board. Sparing the 3/1s
				// that trade up was tried and bought three more calls at the price of three more
				// wrong ones, which is not a trade this advisor makes.
				if(card.Type == CardType.MINION && card.Health <= 1)
					return new MulliganCardVerdict(MulliganVerdict.Situational,
						"1 health, dies for free");

				return new MulliganCardVerdict(MulliganVerdict.Keep,
					card.Type == CardType.WEAPON
						? "a cheap weapon trades on board without spending a body"
						: $"plays on turn {turn}");
			}

			// 3. A cheap QUEST or SIDEQUEST is a turn-1 play by construction: its reward is paid at
			//    the end of a countdown, so every turn it is not down is a turn of the reward lost.
			//    Read off the tag, never the text —
			//    the same rule the synergy engine's dead-card lever follows, because a card that
			//    merely mentions a quest is a normal card.
			if(card.Cost <= 1 && (card.Entity.GetTag(GameTag.QUEST) != 0
				|| card.Entity.GetTag(GameTag.SIDEQUEST) != 0
				|| card.Entity.GetTag(GameTag.QUESTLINE) != 0))
				return new MulliganCardVerdict(MulliganVerdict.Keep,
					"quest wants turn 1");

			// 4. Cheap is not the same as tempo. A one-mana card that leaves the board unchanged
			//    spends a turn without contesting anything; the cost alone says nothing.
			if(!isBody && turn <= EarlyCost && !AffectsBoard(card))
				return new MulliganCardVerdict(MulliganVerdict.Toss,
					"no board impact");

			// 5. The top end goes back, and this is the other half of the tempo rule: a card that
			//    cannot be cast for five turns is not a plan, it is a card you would rather draw
			//    later. Cards that DISCOUNT themselves are exempt — a printed cost is not the cost
			//    you pay when the card reduces it, and treating the two alike would mulligan away
			//    the payoff a player is holding precisely so it lands ahead of schedule.
			// The top end goes back only when we can see that it is not a bomb. With no score at
			// all — a card no win-rate covers — a slow card and a game-winner look identical from
			// here, and the honest move is to say nothing rather than mulligan away the reason the
			// player is in the game.
			if(turn >= TopEndCost && !IsSelfDiscounting(card))
			{
				var quality = score?.Invoke(card.DbfId);
				if(!quality.HasValue)
					return new MulliganCardVerdict(MulliganVerdict.Situational);
				if(quality.Value < BombScore)
					return new MulliganCardVerdict(MulliganVerdict.Toss,
						$"too slow (turn {turn})");
			}

			// 6. Marginal value at the expensive end: a fourth-turn card behind an earlier play in
			//    hand is the one you give back, since only one of them lands on curve. The bomb
			//    exemption has to repeat here — the rule above deliberately spares a game-winning
			//    card from the top-end toss, and this one would otherwise take it straight back.
			if(turn >= LateMidTurn && hand.Take(index).Any(c => EffectiveTurn(c, onCoin) < turn)
				&& !IsSelfDiscounting(card) && !IsBomb(card, score))
				return new MulliganCardVerdict(MulliganVerdict.Toss,
					"behind a cheaper play");

			// 5. The Coin as an ENABLER, not just a tempo shift. A Combo card is dead on turn 1
			//    going first and live on turn 1 with the Coin — the Coin is a card played before it,
			//    and in Rogue especially that is the difference between a keep and a mulligan. Read
			//    off the COMBO tag rather than the text, for the same reason the dead-card lever
			//    reads QUEST off its tag: a card that merely mentions combos is not a combo card.
			if(onCoin && card.Entity.GetTag(GameTag.COMBO) != 0)
				return new MulliganCardVerdict(MulliganVerdict.Keep,
					"the Coin enables Combo");

			return new MulliganCardVerdict(MulliganVerdict.Situational);
		}

		/// <summary>The turn this card first plays, which is one earlier with the Coin.</summary>
		private static int EffectiveTurn(Card card, bool onCoin)
			=> onCoin ? System.Math.Max(1, card.Cost - 1) : card.Cost;

		private static Card? Resolve(int dbfId) => Cards.GetFromDbfId(dbfId);

		/// <summary>
		/// Card text with markup stripped and newlines collapsed. Not cosmetic: the client wraps
		/// text mid-sentence, and `.` does not cross a newline, so "Add a random 1, 2,\nand 3-Cost
		/// Elemental to your hand" silently fails every pattern spanning two words — and fails
		/// SILENTLY, which is how it survived: the rule simply never matched and the card fell
		/// through to a verdict that looked plausible.
		/// </summary>
		private static string CleanText(Card card)
			=> string.IsNullOrEmpty(card.Text)
				? string.Empty
				: WhitespaceRe.Replace(MarkupRe.Replace(card.Text, " "), " ");

		private static readonly Regex MarkupRe = new Regex(@"<[^>]+>|\[x\]",
			RegexOptions.Compiled);
		private static readonly Regex WhitespaceRe = new Regex(@"\s+", RegexOptions.Compiled);


		/// <summary>
		/// Is this card doing something on an empty board on turn 1-2? A minion is — it is a body.
		/// So is a WEAPON, and that one has to be checked by TYPE rather than by text: most weapons
		/// have no text at all, and reading a blank text as "does nothing" is how a strong turn-1
		/// weapon ends up in TOSS. A spell has to say what it does, and generating a card counts.
		/// </summary>
		private static bool AffectsBoard(Card card)
		{
			// A LOCATION is a permanent you play onto the board and use every other turn, so a cheap
			// one is a real turn-1 play — same lesson as weapons, learned the same way: reading the
			// text alone put strong turn-1 locations into TOSS.
			if(card.Type == CardType.MINION || card.Type == CardType.WEAPON
				|| card.Type == CardType.LOCATION)
				return true;
			var text = CleanText(card);
			return text.Length > 0 && (BoardImpactRe.IsMatch(text) || GeneratesCardRe.IsMatch(text));
		}
	}
}
