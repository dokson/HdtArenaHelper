using System;
using System.Collections.Generic;
using System.Linq;
using HearthMirror;
using HearthMirror.Enums;
using HearthMirror.Objects;

namespace HdtArenaHelper
{
	/// <summary>The opening hand currently on the mulligan screen.</summary>
	public class MulliganEventArgs : EventArgs
	{
		/// <summary>The offered cards, in the client's own zone order (left to right).</summary>
		public IReadOnlyList<int> HandDbfIds { get; }
		/// <summary>The run deck's class.</summary>
		public HearthDb.Enums.CardClass DeckClass { get; }
		/// <summary>The run deck: the advisor judges the hand AGAINST it, so it is not optional.</summary>
		public IReadOnlyList<int> DeckDbfIds { get; }
		/// <summary>
		/// Second player. Read from the hand SIZE rather than from the client: Hearthstone deals 3
		/// cards going first and 4 going second, and the Coin itself is not in hand yet at the
		/// mulligan. A rule beats a field read that could fail.
		/// </summary>
		public bool OnCoin { get; }

		public MulliganEventArgs(IReadOnlyList<int> handDbfIds, HearthDb.Enums.CardClass deckClass,
			IReadOnlyList<int> deckDbfIds, bool onCoin)
		{
			HandDbfIds = handDbfIds;
			DeckClass = deckClass;
			DeckDbfIds = deckDbfIds;
			OnCoin = onCoin;
		}
	}

	/// <summary>
	/// Detects the mulligan screen and reports the opening hand, so the overlay can show each card's
	/// keep record for the drafted class.
	///
	/// Gated exactly like <see cref="CardChoiceWatcher"/>: the scene must be GAMEPLAY and the player
	/// must be in an ARENA run, because the keep statistics are arena statistics. Ordering is by the
	/// client's own <c>ZonePosition</c> rather than by the order the list happens to arrive in — the
	/// overlay places one column per card, so a wrong order puts each number over the wrong card.
	/// </summary>
	public class MulliganWatcher : GameWatcher
	{
		public event EventHandler<MulliganEventArgs>? OnMulligan;
		public event EventHandler? OnMulliganGone;

		private string? _lastSignature;
		private bool _showing;

		protected override SceneMode Scene => SceneMode.GAMEPLAY;

		/// <summary>Keep statistics are arena statistics; an arena run being open is not an arena game.</summary>
		protected override bool ArenaMatchOnly => true;

		protected override void OnSceneLeft() => Clear();

		public override void Reset()
		{
			base.Reset();
			_lastSignature = null;
			_showing = false;
		}

		protected override void PollCore()
		{
			var state = Reflection.Client.GetMulliganState();
			var arenaInfo = Reflection.Client.GetArenaDeck();

			var plan = BuildMulliganPlan(state?.MulliganCards, arenaInfo?.Deck?.Hero,
				arenaInfo?.Deck?.HeroPower, arenaInfo?.Deck?.Cards);
			if(plan == null)
			{
				Clear();
				return;
			}

			if(plan.Signature == _lastSignature)
				return;
			_lastSignature = plan.Signature;
			_showing = true;

			Log($"mulligan: {plan.Args.HandDbfIds.Count} cards, class={plan.Args.DeckClass}, " +
				$"deck={plan.Args.DeckDbfIds.Count}, coin={plan.Args.OnCoin}");
			OnMulligan?.Invoke(this, plan.Args);
		}

		/// <summary>What one poll resolved: the event to fire, plus its dedup key.</summary>
		internal sealed class MulliganPlan
		{
			public MulliganEventArgs Args { get; }
			public string Signature { get; }
			public MulliganPlan(MulliganEventArgs args, string signature)
			{
				Args = args;
				Signature = signature;
			}
		}

		/// <summary>
		/// The decision, split out of HearthMirror to be testable. Null means "show nothing": no hand,
		/// no arena deck, or an id that does not resolve — ALL of them must, because the overlay
		/// places a column per card and a partial hand would shift every number onto its neighbour.
		/// A transient failure to resolve (HearthDb still loading) therefore retries on the next poll
		/// instead of freezing a half-built hand.
		/// </summary>
		internal static MulliganPlan? BuildMulliganPlan(IEnumerable<MulliganState.MulliganCard>? cards,
			string? hero, string? heroPower, IEnumerable<Card>? deckCards)
		{
			if(cards == null)
				return null;

			// The client's zone order is the on-screen order, and it is what the layout indexes by.
			var hand = cards
				.OrderBy(c => c.ZonePosition)
				.Select(c => DraftWatcher.ToDbfId(c.CardId))
				.ToList();
			if(hand.Count == 0 || hand.Any(dbf => dbf == 0))
				return null;

			var deckClass = DraftWatcher.ToClass(hero);
			if(deckClass == HearthDb.Enums.CardClass.INVALID)
				deckClass = DraftWatcher.ToClass(heroPower);
			// Without a class there are no per-class keep statistics to show, and the pooled ones
			// would answer a different question than the one on screen.
			if(deckClass == HearthDb.Enums.CardClass.INVALID)
				return null;

			// The deck is what the advice is made of, so no deck means no advice — the same "show
			// nothing rather than something generic" rule the class check above applies.
			var deck = (deckCards ?? Enumerable.Empty<Card>())
				.SelectMany(c => Enumerable.Repeat(DraftWatcher.ToDbfId(c.Id), Math.Max(1, c.Count)))
				.Where(dbf => dbf != 0)
				.ToList();
			if(deck.Count == 0)
				return null;

			// 3 cards going first, 4 going second. The Coin is not dealt into the mulligan hand, so
			// the count is the only thing that says which side of the turn order this is.
			var onCoin = hand.Count >= 4;

			return new MulliganPlan(new MulliganEventArgs(hand, deckClass, deck, onCoin),
				string.Join(",", hand));
		}

		private void Clear()
		{
			_lastSignature = null;
			if(!_showing)
				return;
			_showing = false;
			OnMulliganGone?.Invoke(this, EventArgs.Empty);
		}
	}
}
