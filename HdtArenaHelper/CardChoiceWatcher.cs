using System;
using System.Collections.Generic;
using System.Linq;
using HearthMirror;
using HearthMirror.Enums;
using HearthMirror.Objects;

namespace HdtArenaHelper
{
	/// <summary>A Discover (or other in-game card choice) currently on screen.</summary>
	public class CardChoiceEventArgs : EventArgs
	{
		/// <summary>The offered cards, in the order the client lists them.</summary>
		public IReadOnlyList<int> OfferedDbfIds { get; }
		/// <summary>The class of the run's deck, so cards are scored in their class context.</summary>
		public HearthDb.Enums.CardClass DeckClass { get; }
		/// <summary>The run deck, as the synergy engine's "what you already have".</summary>
		public IReadOnlyList<int> DeckDbfIds { get; }

		public CardChoiceEventArgs(IReadOnlyList<int> offeredDbfIds,
			HearthDb.Enums.CardClass deckClass, IReadOnlyList<int> deckDbfIds)
		{
			OfferedDbfIds = offeredDbfIds;
			DeckClass = deckClass;
			DeckDbfIds = deckDbfIds;
		}
	}

	/// <summary>
	/// Detects in-game card choices — Discover and anything else the client presents through the
	/// same choice zone — by polling HearthMirror, and reports them so the overlay can score them
	/// with the SAME engine the draft uses.
	///
	/// Three gates, all deliberate:
	///   - the active scene must be GAMEPLAY. The draft has its own watcher; sharing one would mean
	///     a choice list left in client memory could paint over the wrong screen, which is the bug
	///     the draft path already had to fix twice.
	///   - the current MATCH must be an arena one (<see cref="GameWatcher.ArenaMatchOnly"/>). Every
	///     number this plugin shows is an arena win-rate; rendering it over a Standard game would
	///     present a statistic measured somewhere else as if it applied here.
	///   - the player must be in an ARENA run, for the deck that gives the choice its class context.
	///     Note this is NOT a substitute for the gate above: the run stays open across modes, which
	///     is how a Battlegrounds trinket choice once got scored as a Discover.
	/// </summary>
	public class CardChoiceWatcher : GameWatcher
	{
		public event EventHandler<CardChoiceEventArgs>? OnChoicesChanged;
		public event EventHandler? OnChoicesGone;

		private readonly ChoiceGate _gate = new ChoiceGate(PollsBeforeGone);

		/// <summary>
		/// Consecutive empty polls before a choice counts as GONE. The client drops
		/// <c>IsVisible</c> for a moment mid-choice, and clearing on the first empty poll reset the
		/// dedup key, so the very same Discover was rendered again seconds later. Measured on a live
		/// session: 8 of 35 in-game choices were exact repeats of the trio before them, four of those
		/// within 6-12 seconds — the window this closes. The longer repeats are a different bug, a
		/// list the client re-raises after the choice is over, and a debounce cannot fix that one.
		///
		/// Three at a 500 ms poll is ~1.5 s of tolerance: far longer than a flicker, far shorter than
		/// a player picking a card, so a genuinely new choice is never merged into the old one.
		/// </summary>
		internal const int PollsBeforeGone = 3;

		/// <summary>
		/// The dedup-and-debounce state machine, kept apart from HearthMirror so it can be tested —
		/// the same reason <see cref="BuildChoicePlan"/> is. Its behaviour is invisible from outside:
		/// getting it wrong either re-announces a choice the player already made or swallows a real
		/// one, and neither shows up until someone reads a log.
		/// </summary>
		internal sealed class ChoiceGate
		{
			private readonly int _pollsBeforeGone;
			private string? _signature;
			private bool _showing;
			private int _missed;

			internal ChoiceGate(int pollsBeforeGone) => _pollsBeforeGone = pollsBeforeGone;

			/// <summary>A choice is on screen. True when it is NEW and should be announced.</summary>
			internal bool Announce(string signature)
			{
				_missed = 0;
				if(_showing && signature == _signature)
					return false;

				_signature = signature;
				_showing = true;
				return true;
			}

			/// <summary>
			/// A poll saw no choice. True only once the absence has lasted, meaning the choice is
			/// really over and the "gone" event should fire. A single empty poll is a flicker: the
			/// dedup key must survive it, or the very same Discover is announced again.
			/// </summary>
			internal bool Miss()
			{
				if(!_showing)
				{
					_signature = null;
					return false;
				}

				if(++_missed < _pollsBeforeGone)
					return false;

				Reset();
				return true;
			}

			internal void Reset()
			{
				_signature = null;
				_showing = false;
				_missed = 0;
			}
		}

		/// <summary>Only ever live during a game; the base gate enforces it.</summary>
		protected override SceneMode Scene => SceneMode.GAMEPLAY;

		/// <summary>
		/// The scene gate alone is not enough here: Battlegrounds is GAMEPLAY too, and its hero/trinket
		/// choices arrive through this very zone.
		/// </summary>
		protected override bool ArenaMatchOnly => true;

		protected override void OnSceneLeft() => Clear();

		public override void Reset()
		{
			base.Reset();
			_gate.Reset();
		}

		protected override void PollCore()
		{
			var choices = Reflection.Client.GetCardChoices();
			var arenaInfo = Reflection.Client.GetArenaDeck();

			if(choices == null || !choices.IsVisible || choices.Cards == null)
			{
				Missed();
				return;
			}

			var plan = BuildChoicePlan(choices.Cards, arenaInfo?.Deck?.Cards,
				arenaInfo?.Deck?.Hero, arenaInfo?.Deck?.HeroPower);
			if(plan == null)
			{
				Missed();
				return;
			}

			// Dedup on the offered ids: unlike the draft's DraftChoices there is no Version field,
			// and re-scoring the same Discover twice a second would re-run the synergy engine over
			// the whole deck on the UI thread for nothing.
			if(!_gate.Announce(plan.Signature))
				return;

			Log($"card choice: {plan.Args.OfferedDbfIds.Count} offered, " +
				$"class={plan.Args.DeckClass}, deck={plan.Args.DeckDbfIds.Count}");
			OnChoicesChanged?.Invoke(this, plan.Args);
		}

		private void Missed()
		{
			if(_gate.Miss())
				OnChoicesGone?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>What one poll resolved: the event to fire, plus the dedup key for it.</summary>
		internal sealed class ChoicePlan
		{
			public CardChoiceEventArgs Args { get; }
			public string Signature { get; }
			public ChoicePlan(CardChoiceEventArgs args, string signature)
			{
				Args = args;
				Signature = signature;
			}
		}

		/// <summary>
		/// The decision, split out from HearthMirror so it can be tested — the same reason
		/// <see cref="DraftWatcher.BuildDeckEditPlan"/> is: null here means "show nothing", and
		/// getting that wrong either hides the overlay for a whole game or paints a stale Discover
		/// over the board, neither of which is visible from outside.
		///
		/// Null when there is nothing to show: no arena deck (this plugin only knows ARENA
		/// win-rates, so outside an arena run it has no number it is entitled to display), or any
		/// offered id that does not resolve. ALL of them must: plaques are laid out by index and
		/// centred on the count, so scoring 2 of 3 cards would put every plaque over the wrong card.
		/// Not resolving also happens transiently while HearthDb loads, and returning null lets the
		/// next poll retry instead of freezing a half-built choice.
		/// </summary>
		internal static ChoicePlan? BuildChoicePlan(IEnumerable<string>? offeredCardIds,
			IEnumerable<Card>? deckCards, string? hero, string? heroPower)
		{
			var ids = (offeredCardIds ?? Enumerable.Empty<string>()).ToList();
			var offered = ids.Select(DraftWatcher.ToDbfId).ToList();
			if(offered.Count == 0 || offered.Any(dbf => dbf == 0) || deckCards == null)
				return null;

			var deckClass = DraftWatcher.ToClass(hero);
			if(deckClass == HearthDb.Enums.CardClass.INVALID)
				deckClass = DraftWatcher.ToClass(heroPower);

			var context = deckCards
				.SelectMany(c => Enumerable.Repeat(DraftWatcher.ToDbfId(c.Id), Math.Max(1, c.Count)))
				.Where(dbf => dbf != 0)
				.ToList();

			return new ChoicePlan(new CardChoiceEventArgs(offered, deckClass, context),
				string.Join(",", offered));
		}


		/// <summary>
		/// One empty poll is not "the choice is over" — see <see cref="PollsBeforeGone"/>. Until the
		/// count is reached nothing happens at all: the overlay stays up (a flicker the player never
		/// sees) and, crucially, the dedup key survives, so the same choice cannot be re-announced.
		/// </summary>

		/// <summary>
		/// Leaving the scene ends the choice at once — no debounce there, because the screen the
		/// choice belonged to is gone, which is the one thing a flicker never means.
		/// </summary>
		private void Clear()
		{
			_gate.Reset();
			OnChoicesGone?.Invoke(this, EventArgs.Empty);
		}

	}
}
