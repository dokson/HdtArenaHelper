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
		/// A hand whose CHEAPEST card lands no earlier than this has no early game at all, and goes
		/// back whole. Four rather than three because a three-drop is a real curve play — the hand
		/// that must be dug out of is the one where the first thing you can do arrives on turn four.
		/// Not a tuning knob: it is the same turn <see cref="LateMidTurn"/> names, read from the hand
		/// instead of from one card.
		/// </summary>
		private const int NoEarlyPlayTurn = 4;

		/// <summary>
		/// The smallest real opening hand: 3 going first, 4 on the coin. Hearthstone's own number,
		/// and the same one <c>MulliganWatcher</c> reads the coin from. Rules that judge the HAND
		/// rather than a card are gated on it — a one- or two-card list is a caller isolating a
		/// single card, and "this hand has no early play" is not a claim you can make about it.
		/// </summary>
		private const int OpeningHandSize = 3;

		/// <summary>
		/// Below this many cheap permanents, a deck cannot be relied on to hand you a turn-1-2 play
		/// and its three-drop becomes the early game. Roughly a third of a deck's ~19 minions: above
		/// that a random opener holds a cheap body more often than not, so keeping a three-drop is
		/// keeping the slower of two cards you will both draw.
		/// </summary>
		private const int ThinEarlyGame = 6;

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
			if(text.Length == 0)
				return false;
			// Strip discounts aimed at ANOTHER card before asking whether this one discounts ITSELF.
			// Alter Time reads "Discover two Arcane spells from the past. They cost (2) less" — the
			// discount is on what it finds, not on Alter Time, but the bare pattern read it as
			// self-discounting and so exempted it from every top-end rule. Seen live: a 4-mana spell
			// sitting behind a 3-drop in hand came back Situational when it should have gone back.
			// Measured on the pool, 66 of the 454 cards the old check matched are this shape — a
			// pronoun subject ("It costs (1) less", "They cost (2) less") always points at another card.
			var own = OtherCardDiscountRe.Replace(text, " ");
			return DiscountRe.IsMatch(own);
		}

		private static readonly Regex OtherCardDiscountRe = new Regex(
			@"\b(they|it|them|those|these)\s+costs?\s*\(\d+\)\s*less\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

		/// <summary>
		/// Does this card put an extra body on the board by itself? Read off the text rather than a
		/// tag because the summon can hang off a Battlecry, a Deathrattle or nothing at all, and all
		/// three answer the only question here: is the printed statline the whole play.
		/// </summary>
		private static bool SummonsABody(Card card)
		{
			var text = CleanText(card);
			return text.Length > 0 && SummonsRe.IsMatch(text);
		}

		private static readonly Regex SummonsRe = new Regex(@"\bsummon\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>
		/// Does trading this card IMPROVE it, rather than merely cycle it? Gated on the TRADEABLE tag
		/// as well as the text, so a card that merely talks about trading is not swept in.
		/// </summary>
		private static bool HasTradeUpside(Card card)
		{
			if(card.Entity.GetTag(GameTag.TRADEABLE) == 0)
				return false;
			var text = CleanText(card);
			return text.Length > 0 && TradeUpsideRe.IsMatch(text);
		}

		private static readonly Regex TradeUpsideRe = new Regex(
			@"\btrade to upgrade\b|\bupgrades?\b[^.]*\bwhen traded\b"
			+ @"|\bafter you trade this\b|\bwhen you draw this\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>
		/// Does this card hand you something back when it DIES? Gated on the DEATHRATTLE tag and not
		/// on the text alone, because the payment has to be on death: a Battlecry that drew a card was
		/// already paid whether the body then lives or dies, so it says nothing about trading the body.
		/// A summoning deathrattle is already covered by <see cref="SummonsABody"/>.
		/// </summary>
		private static bool ReplacesItselfOnDeath(Card card)
		{
			if(card.Entity.GetTag(GameTag.DEATHRATTLE) == 0)
				return false;
			var text = CleanText(card);
			return text.Length > 0 && DeathValueRe.IsMatch(text);
		}

		/// <summary>
		/// Will this small body die to the opponent's hero power for nothing? Unknown hero power means
		/// YES, deliberately: the fail-safe direction is to keep warning, because relaxing the rule on
		/// missing data is the error that loses a board.
		/// </summary>
		private static bool DiesFreeToHeroPower(Card card, Card? opponentHeroPower)
			=> opponentHeroPower == null
				|| HeroPowerThreat.KillsForFree(opponentHeroPower, card.Health);

		// CleanText collapses whitespace, so plain spaces are safe here — unlike the synergy engine's
		// patterns, which read the un-collapsed form and must use \s+ (see CardText).
		private static readonly Regex DeathValueRe = new Regex(
			@"\badd\b[^.]*\bto your hand\b|\bdraw\b",
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
			IReadOnlyList<int> deckDbfIds, CardClass deckClass, bool onCoin,
			Card? opponentHeroPower = null)
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

			// Judged left to right, and the verdicts so far are an INPUT to the next card: the
			// "second of its slot" rule has to know which earlier cards actually turned out to be
			// early plays, not merely which ones share a cost.
			var verdicts = new List<MulliganCardVerdict>(hand.Count);
			for(var i = 0; i < hand.Count; i++)
				verdicts.Add(Judge(hand[i]!, i, hand!, deck, onCoin, _score, verdicts, opponentHeroPower));
			return verdicts;
		}

		/// <summary>
		/// The rules, in the order a strong player applies them: what the hand cannot do at all
		/// beats what a single card is worth. First match wins, and no rule may fire on a card the
		/// deck does not actually make true.
		/// </summary>
		private static MulliganCardVerdict Judge(Card card, int index, IReadOnlyList<Card> hand,
			IReadOnlyList<Card> deck, bool onCoin, Func<int, double?>? score,
			IReadOnlyList<MulliganCardVerdict> earlier, Card? opponentHeroPower)
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

			// How far "early" reaches is a property of the DECK, not of the game. A three-drop is
			// not an early play in the abstract, but a deck that cannot curve out has no earlier
			// one to draw into, and mulliganing away its first body chases cards it does not hold.
			// Widening the window rather than adding a second rule keeps every guard below — the
			// duplicate slot, the quality floor, the empty board, Combo, one health — applying to
			// the three-drop exactly as they do to the two-drop.
			var earlyWindow = CountEarlyPermanents(deck) < ThinEarlyGame ? EarlyCost + 1 : EarlyCost;

			if(card.Cost >= 1 && turn <= earlyWindow && isPermanent && AffectsBoard(card))
			{
				// The one deck-relative exception, kept here rather than as a later rule so it can
				// only ever DOWNGRADE a keep: holding a second copy of a slot the deck can refill
				// is not a second early play, it is one early play and a spare.
				// It counts earlier KEEPS, not earlier cards of the same cost. Sharing a cost with a
				// card that was itself demoted is not a duplicated slot: a hand of Dance Floor (a
				// 2-mana location wanting a board that does not exist yet) plus a real 2-drop holds
				// exactly one early play, and reading it as two threw away the only one.
				var secondOfItsSlot = deck.Count(c => c.Cost == card.Cost) >= 3
					&& earlier.Where((v, i) => v.Verdict == MulliganVerdict.Keep
						&& hand[i].Cost == card.Cost).Any();
				if(secondOfItsSlot)
					return new MulliganCardVerdict(MulliganVerdict.Situational,
						$"second {card.Cost}-drop");

				// Tempo is necessary, not sufficient: a cheap card the win-rate data rates below the
				// pool median buys a turn with a card that loses it back. But "below the pool median"
				// is an answer to the DRAFT's question, and asking it here contradicted this class's
				// own premise — the draft score already said whether the card is good, and the
				// mulligan asks whether it is your best play on that turn given the other 27 cards.
				// Seen live: Wild Pyromancer is genuinely below average for a Mage (48.8% drawn
				// against a 51.3% class median) and was demoted in a deck that had nothing better at
				// two mana, where a mediocre turn-2 play still beats an empty turn 2.
				//
				// So the score is a COMPARISON here, not a gate: it demotes only when the deck can
				// actually do better in this same slot. At the top end it stays an absolute judgement,
				// because there is no slot to compare within — you are deciding whether a card you
				// cannot cast for five turns is worth holding, and only its own quality answers that.
				var quality = score?.Invoke(card.DbfId);
				if(quality.HasValue && quality.Value < PlayableScore)
				{
					var better = CountBetterInSlot(deck, card, quality.Value, score);
					if(better >= BetterInSlotToDemote)
						return new MulliganCardVerdict(MulliganVerdict.Situational,
							$"weak for the slot ({better} better at {card.Cost})");
					return new MulliganCardVerdict(MulliganVerdict.Keep,
						$"your best {card.Cost}-drop");
				}

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
				// The exception is a card that brings a SECOND body: Maze Guide is a 1/1 whose
				// Battlecry lands a 2-drop beside it, so the hero power that eats the 1/1 still
				// leaves the board contested. The printed statline is not the play — the same
				// lesson weapons and locations already taught this class, one field over.
				// The second exception is a body that PAYS YOU WHEN IT DIES. "Dies for free" is a claim
				// about the opponent's side of the trade, and it is false when the ping hands you cards:
				// Sinful Sous Chef is a 1-mana 2/1 whose Deathrattle puts two Silver Hand Recruits in
				// your hand, so the hero power that kills it buys them nothing. Seen live, demoted with
				// the reason line "1 health, dies for free" printed over a card that does the opposite.
				// And the third condition is the OPPONENT's hero power, because "dies for free" was
				// always a claim about their side of the board. Verified live and derived from the card
				// rather than the class (dual-class heroes make the class the wrong question): of the
				// eleven basic hero powers only Mage's Fireblast and the Death Knight's Charge Ghoul
				// kill a one-health body for nothing. Druid, Demon Hunter and Rogue can kill it by
				// swinging the hero, but eat its attack doing so — which is exactly why a 3/1 and a 2/1
				// are different cards to hold — and the remaining six answer it not at all.
				//
				// An UNKNOWN hero power keeps the old demotion (KillsForFree returns false only when it
				// can prove the body survives). Relaxing a rule on missing data would advise keeping a
				// fragile body against an opponent we simply failed to read, and that error costs the
				// board while the conservative one costs nothing.
				if(card.Type == CardType.MINION && card.Health <= 1
					&& !SummonsABody(card) && !ReplacesItselfOnDeath(card)
					&& DiesFreeToHeroPower(card, opponentHeroPower))
					return new MulliganCardVerdict(MulliganVerdict.Situational,
						opponentHeroPower == null
							? "1 health, dies for free"
							: $"1 health, dies to {opponentHeroPower.Name}");

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

			// 4b. A hand that does NOTHING until turn 4 goes back whole, and this is the one rule that
			//     reads the hand rather than a card. Every other rule here is relative — "cheap for its
			//     slot", "behind a cheaper play", "top end" — so each of these cards looked defensible
			//     on its own while the hand as a whole had no play at all. Seen live: a Mage going
			//     first held two 4-drops and a 5-drop and got three abstentions, which is exactly the
			//     hand a player does not need help with and exactly the call the plugin owed them.
			//
			//     It sits ABOVE the top-end rule deliberately, because that one abstains when the card
			//     has no win-rate and would otherwise leave the most expensive card in a dead hand
			//     unjudged. This rule needs no data: "there is nothing to play before turn 4" is a
			//     fact about the hand, not an estimate, and the mulligan exists to dig for one.
			//
			//     Read in EFFECTIVE turns, so the Coin is already accounted for — a 4-drop on the coin
			//     is a turn-3 play and does not trigger this.
			//
			//     The two standing exemptions hold: a self-discounting card (its printed cost is not
			//     the cost you pay) and a MEASURED bomb. The bomb one is deliberate and narrow — it
			//     needs a real score above the bomb threshold, so the unscored expensive card that
			//     prompted this rule is still tossed, and only a card the data calls a game-winner
			//     survives a hand with no early game.
			//     Gated on a REAL opening hand — 3 cards going first, 4 on the coin, which is the same
			//     fact MulliganWatcher reads the coin from. "The hand has no early play" is a claim
			//     about a whole hand, and a shorter list is a caller isolating one card, not a hand.
			//
			//     A card with a trade upside COUNTS as an early play, for the same reason rule 5a
			//     exists: one mana turns it into a better card and draws a replacement, so a hand
			//     holding one is not doing nothing on turn 1.
			if(turn >= NoEarlyPlayTurn && !IsSelfDiscounting(card) && !IsBomb(card, score)
				&& hand.Count >= OpeningHandSize
				&& hand.All(c => EffectiveTurn(c, onCoin) >= NoEarlyPlayTurn && !HasTradeUpside(c)))
				return new MulliganCardVerdict(MulliganVerdict.Toss,
					$"nothing before turn {NoEarlyPlayTurn}");

			// 5. The top end goes back, and this is the other half of the tempo rule: a card that
			//    cannot be cast for five turns is not a plan, it is a card you would rather draw
			//    later. Cards that DISCOUNT themselves are exempt — a printed cost is not the cost
			//    you pay when the card reduces it, and treating the two alike would mulligan away
			//    the payoff a player is holding precisely so it lands ahead of schedule.
			// The top end goes back only when we can see that it is not a bomb. With no score at
			// all — a card no win-rate covers — a slow card and a game-winner look identical from
			// here, and the honest move is to say nothing rather than mulligan away the reason the
			// player is in the game.
			// 5a. A card that gets BETTER when you trade it has a real turn-1 play: one mana upgrades it
			//     and draws a replacement, so the printed cost is not the only thing you can do with it
			//     — the same reason self-discounting cards are exempt below. Wind-Up Enforcer is a
			//     6-mana 3/5 that the top-end rule was sending back as flatly "too slow", which is the
			//     call that started this.
			//
			//     Being TRADEABLE is NOT enough, and that distinction is the rule. A plain Tradeable
			//     card only cycles, and cycling an expensive card you did not want is worse than the
			//     free replacement a mulligan already gives you — so those still go back. Measured on
			//     the pool: 54 collectible Tradeable cards, of which only NINE carry a trade upside,
			//     and only two of those (Wind-Up Enforcer and Wind-Up Musician) are expensive enough to
			//     reach this rule at all. A narrow rule on purpose.
			//
			//     Four wordings, all found by reading the pool rather than by guessing — the first
			//     attempt matched two of them and missed Wicked Shipment, Blackwater Cutlass and Line
			//     Cook: "(Trade to upgrade!)", "Upgrades … when Traded!", "After you Trade this, …",
			//     and Line Cook's "When you draw this, get a copy of it" (a trade draws, so the upside
			//     is the same). Re-validate against the pool if this is touched.
			//
			//     Situational, NOT Keep: the upgrade is value, not board presence. And it applies ONLY
			//     when turn 1 is otherwise empty — the trade is worth something because it uses a mana
			//     that was going to be wasted, so once the hand holds a real turn-1 play the two compete
			//     for it and the expensive card goes back.
			if(turn >= TopEndCost && HasTradeUpside(card)
				&& !hand.Where((c, i) => i != index).Any(c => EffectiveTurn(c, onCoin) <= 1))
				return new MulliganCardVerdict(MulliganVerdict.Situational,
					"upgrades when traded, and turn 1 is free");

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

		/// <summary>
		/// How many cards at the SAME cost the deck holds that score better than this one. Same cost
		/// rather than the whole early window, deliberately: a deck rich in good 1-drops does not make
		/// its only 2-drop skippable, since the two are played on different turns — and it is the rule
		/// <c>secondOfItsSlot</c> already uses, so both read "slot" the same way.
		///
		/// Deck cards nothing has measured are skipped: an unscored card cannot be called better, and
		/// counting it would demote a real play in favour of an unknown one.
		/// </summary>
		private static int CountBetterInSlot(IReadOnlyList<Card> deck, Card card, double quality,
			Func<int, double?>? score)
		{
			if(score == null)
				return 0;
			var better = 0;
			foreach(var other in deck)
			{
				if(other.Cost != card.Cost || other.DbfId == card.DbfId)
					continue;
				var value = score(other.DbfId);
				if(value.HasValue && value.Value > quality)
					better++;
			}
			return better;
		}

		/// <summary>
		/// How many better cards at the same cost it takes before the deck counts as covering that
		/// slot. TWO, not one: a single better card among thirty is one you will probably not have
		/// drawn by the turn in question, so demoting the card in hand for it trades a play you hold
		/// for a play you might see.
		/// </summary>
		private const int BetterInSlotToDemote = 2;

		/// <summary>
		/// The deck's cheap PERMANENTS — the cards that can actually contest turns one and two.
		/// Spells are excluded for the same reason the synergy engine's curve rule excludes them:
		/// a deck full of cheap removal still has nothing to play on turn two.
		/// </summary>
		private static int CountEarlyPermanents(IReadOnlyList<Card> deck)
		{
			var count = 0;
			foreach(var card in deck)
			{
				if(card.Cost >= 1 && card.Cost <= EarlyCost
					&& (card.Type == CardType.MINION || card.Type == CardType.WEAPON
						|| card.Type == CardType.LOCATION))
					count++;
			}
			return count;
		}

		/// <summary>The turn this card first plays, which is one earlier with the Coin.</summary>
		private static int EffectiveTurn(Card card, bool onCoin)
			=> onCoin ? System.Math.Max(1, card.Cost - 1) : card.Cost;

		private static Card? Resolve(int dbfId) => Cards.GetFromDbfId(dbfId);

		/// <summary>
		/// Card text with markup stripped and newlines collapsed — <see cref="CardText.Flattened"/>,
		/// which now owns the convention. Collapsing is not cosmetic: the client wraps text
		/// mid-sentence, and `.` does not cross a newline, so "Add a random 1, 2,\nand 3-Cost
		/// Elemental to your hand" silently fails every pattern spanning two words — and fails
		/// SILENTLY, which is how it survived: the rule simply never matched and the card fell
		/// through to a verdict that looked plausible. The synergy engine then hit the same trap
		/// from the other side, which is why the rule lives in one place now.
		/// </summary>
		private static string CleanText(Card card) => CardText.Flattened(card);


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
