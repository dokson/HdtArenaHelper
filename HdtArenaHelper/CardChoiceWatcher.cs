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
	/// Two gates, both deliberate:
	///   - the active scene must be GAMEPLAY. The draft has its own watcher; sharing one would mean
	///     a choice list left in client memory could paint over the wrong screen, which is the bug
	///     the draft path already had to fix twice.
	///   - the player must be in an ARENA run. Every number this plugin shows is an arena win-rate;
	///     rendering it over a Standard game would present a statistic measured somewhere else as
	///     if it applied here. When there is no arena deck we show nothing rather than a wrong
	///     number.
	/// </summary>
	public class CardChoiceWatcher : GameWatcher
	{
		public event EventHandler<CardChoiceEventArgs>? OnChoicesChanged;
		public event EventHandler? OnChoicesGone;

		private string? _lastSignature;
		private bool _showing;

		/// <summary>Only ever live during a game; the base gate enforces it.</summary>
		protected override SceneMode Scene => SceneMode.GAMEPLAY;

		protected override void OnSceneLeft() => Clear();

		public override void Reset()
		{
			base.Reset();
			_lastSignature = null;
			_showing = false;
		}

		protected override void PollCore()
		{
			var choices = Reflection.Client.GetCardChoices();
			var arenaInfo = Reflection.Client.GetArenaDeck();

			if(choices == null || !choices.IsVisible || choices.Cards == null)
			{
				Clear();
				return;
			}

			var plan = BuildChoicePlan(choices.Cards, arenaInfo?.Deck?.Cards,
				arenaInfo?.Deck?.Hero, arenaInfo?.Deck?.HeroPower);
			if(plan == null)
			{
				Clear();
				return;
			}

			// Dedup on the offered ids: unlike the draft's DraftChoices there is no Version field,
			// and re-scoring the same Discover twice a second would re-run the synergy engine over
			// the whole deck on the UI thread for nothing.
			if(plan.Signature == _lastSignature)
				return;
			_lastSignature = plan.Signature;
			_showing = true;

			Log($"card choice: {plan.Args.OfferedDbfIds.Count} offered, " +
				$"class={plan.Args.DeckClass}, deck={plan.Args.DeckDbfIds.Count}");
			OnChoicesChanged?.Invoke(this, plan.Args);
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


		private void Clear()
		{
			_lastSignature = null;
			if(!_showing)
				return;
			_showing = false;
			OnChoicesGone?.Invoke(this, EventArgs.Empty);
		}

	}
}
