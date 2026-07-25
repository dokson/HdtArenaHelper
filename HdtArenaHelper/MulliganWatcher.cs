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
		/// <summary>The run deck's class, so the keep stats come from that class's games.</summary>
		public HearthDb.Enums.CardClass DeckClass { get; }

		public MulliganEventArgs(IReadOnlyList<int> handDbfIds, HearthDb.Enums.CardClass deckClass)
		{
			HandDbfIds = handDbfIds;
			DeckClass = deckClass;
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
				arenaInfo?.Deck?.HeroPower);
			if(plan == null)
			{
				Clear();
				return;
			}

			if(plan.Signature == _lastSignature)
				return;
			_lastSignature = plan.Signature;
			_showing = true;

			Log($"mulligan: {plan.Args.HandDbfIds.Count} cards, class={plan.Args.DeckClass}");
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
			string? hero, string? heroPower)
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

			return new MulliganPlan(new MulliganEventArgs(hand, deckClass),
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
