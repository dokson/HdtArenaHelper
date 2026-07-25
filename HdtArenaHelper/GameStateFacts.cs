using System.Collections.Generic;
using HearthDb.Enums;
using HearthDb;

namespace HdtArenaHelper
{
	/// <summary>
	/// The bits of board state that matter for a choice, snapshotted so the rules below can be
	/// tested without a running game.
	/// </summary>
	public readonly struct GameStateSnapshot
	{
		/// <summary>Mana actually available right now (crystals minus spent, plus temporary).</summary>
		public int AvailableMana { get; }
		public int HandCount { get; }
		public int MaxHandSize { get; }
		public int FriendlyMinions { get; }
		public int MaxBoardSize { get; }
		/// <summary>False when the game state could not be read; every rule then stays silent.</summary>
		public bool IsValid { get; }

		public GameStateSnapshot(int availableMana, int handCount, int maxHandSize,
			int friendlyMinions, int maxBoardSize, bool isValid = true)
		{
			AvailableMana = availableMana;
			HandCount = handCount;
			MaxHandSize = maxHandSize;
			FriendlyMinions = friendlyMinions;
			MaxBoardSize = maxBoardSize;
			IsValid = isValid;
		}

		public static GameStateSnapshot Unknown => new GameStateSnapshot(0, 0, 0, 0, 0, isValid: false);
	}

	/// <summary>
	/// States what the BOARD says about an offered card, and deliberately does not score it.
	///
	/// The facts follow from the rules, so they need no fitting. Their VALUE in points does not exist
	/// in any public data, and hand-tuned card values measured worse than none (REPORT.md) — so the
	/// score stays what the win-rate says and the board speaks in words beside it. A 7-drop
	/// discovered on turn 3 is often still the right pick; that call is the player's.
	/// </summary>
	internal static class GameStateFacts
	{
		/// <summary>Hearthstone's own limits, not tuning knobs.</summary>
		private const int DefaultMaxHandSize = 10;
		private const int DefaultMaxBoardSize = 7;

		/// <summary>
		/// The one fact worth showing for this card, most consequential first, or null when the board
		/// has nothing to add. One line, because the overlay has one line and three simultaneous
		/// warnings would bury the one that matters.
		/// </summary>
		/// <param name="card">The offered card, or null when it did not resolve.</param>
		/// <param name="state">The board snapshot; an invalid one makes every rule silent.</param>
		/// <param name="manaSeparatesOptions">
		/// Whether the mana check tells the offered cards APART. Seen live: three options costing 2
		/// with 0 mana left each carried "needs 2 mana, you have 0" — true on all three, therefore
		/// useless on all three, and three identical warnings bury the fact that does discriminate.
		/// The rule earns its line only when one option is castable now and another is not.
		/// </param>
		internal static string? Describe(Card? card, GameStateSnapshot state,
			bool manaSeparatesOptions = true)
		{
			if(card == null || !state.IsValid)
				return null;

			// A card discovered into a full hand is DESTROYED — the most consequential thing the
			// board can say about a choice, and it applies whatever the card is.
			var maxHand = state.MaxHandSize > 0 ? state.MaxHandSize : DefaultMaxHandSize;
			if(state.HandCount >= maxHand)
				return "hand full — the card would be lost";

			var maxBoard = state.MaxBoardSize > 0 ? state.MaxBoardSize : DefaultMaxBoardSize;
			if(card.Type == CardType.MINION && state.FriendlyMinions >= maxBoard)
				return "board full — no room for a minion";

			if(manaSeparatesOptions && card.Cost > state.AvailableMana)
				return $"needs {card.Cost} mana, you have {state.AvailableMana}";

			return null;
		}

		/// <summary>
		/// Does the mana check separate these options, i.e. is at least one castable now and at
		/// least one not? When they all sit on the same side of the line it says nothing about the
		/// choice, only about the turn — which the player can already see.
		/// </summary>
		internal static bool ManaSeparates(IEnumerable<Card?> offered, GameStateSnapshot state)
		{
			if(!state.IsValid)
				return false;
			var castable = 0;
			var total = 0;
			foreach(var card in offered)
			{
				if(card == null)
					continue;
				total++;
				if(card.Cost <= state.AvailableMana)
					castable++;
			}
			return castable > 0 && castable < total;
		}
	}
}
