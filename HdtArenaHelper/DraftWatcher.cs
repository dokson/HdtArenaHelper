using System;
using System.Collections.Generic;
using System.Linq;
using HearthMirror;
using HearthMirror.Enums;
using HearthMirror.Objects;

namespace HdtArenaHelper
{
	/// <summary>One offered draft choice, resolved to a HearthDb dbf id.</summary>
	public class DraftOption
	{
		public string CardId { get; }
		public int DbfId { get; }
		/// <summary>
		/// Extra cards bundled with this choice (Underground Arena "legendary group":
		/// the legendary + a 3-card package). Empty for a normal single-card pick.
		/// </summary>
		public IReadOnlyList<int> PackageDbfIds { get; }

		public DraftOption(string cardId, int dbfId, IReadOnlyList<int>? packageDbfIds = null)
		{
			CardId = cardId;
			DbfId = dbfId;
			PackageDbfIds = packageDbfIds ?? new List<int>();
		}
	}

	public class DraftChoicesEventArgs : EventArgs
	{
		/// <summary>The (usually 3) cards currently offered.</summary>
		public IReadOnlyList<DraftOption> Offered { get; }
		/// <summary>dbf ids of everything already in the drafted deck.</summary>
		public IReadOnlyList<int> DraftedDbfIds { get; }
		public bool IsUnderground { get; }
		/// <summary>The class being drafted; INVALID until the hero has been picked.</summary>
		public HearthDb.Enums.CardClass DraftClass { get; }
		/// <summary>True for a post-loss redraft pick; DraftedDbfIds then includes the
		/// run deck AND the redraft cards picked so far.</summary>
		public bool IsRedraft { get; }

		public DraftChoicesEventArgs(IReadOnlyList<DraftOption> offered, IReadOnlyList<int> draftedDbfIds,
			bool isUnderground, HearthDb.Enums.CardClass draftClass = HearthDb.Enums.CardClass.INVALID,
			bool isRedraft = false)
		{
			Offered = offered;
			DraftedDbfIds = draftedDbfIds;
			IsUnderground = isUnderground;
			DraftClass = draftClass;
			IsRedraft = isRedraft;
		}
	}

	/// <summary>
	/// One card (and its copy count) in the deck being reviewed during the redraft edit phase.
	/// A plain immutable class, not a positional record: net472 has no
	/// <c>System.Runtime.CompilerServices.IsExternalInit</c>, so records need a polyfill shim.
	/// </summary>
	public class DeckReviewCard
	{
		public int DbfId { get; }
		public int Count { get; }
		public DeckReviewCard(int dbfId, int count)
		{
			DbfId = dbfId;
			Count = count;
		}
	}

	/// <summary>
	/// The finished run deck, reported on the run screen between matches so the overlay can describe
	/// what the deck DOES. Carries the whole deck rather than a summary: the description is computed
	/// where it is shown, so this stays a fact about the client and not a scoring decision.
	/// </summary>
	public class RunSummaryEventArgs : EventArgs
	{
		public IReadOnlyList<int> DeckDbfIds { get; }
		public HearthDb.Enums.CardClass DraftClass { get; }
		public bool IsUnderground { get; }
		public int Wins { get; }
		public int Losses { get; }

		public RunSummaryEventArgs(IReadOnlyList<int> deckDbfIds,
			HearthDb.Enums.CardClass draftClass, bool isUnderground, int wins, int losses)
		{
			DeckDbfIds = deckDbfIds;
			DraftClass = draftClass;
			IsUnderground = isUnderground;
			Wins = wins;
			Losses = losses;
		}
	}

	/// <summary>
	/// The current deck to rank while the player is editing it (the "Edit Your Deck" /
	/// discard phase of a redraft), so the overlay can flag the weakest cards to cut.
	/// </summary>
	public class DeckReviewEventArgs : EventArgs
	{
		public IReadOnlyList<DeckReviewCard> Deck { get; }
		public HearthDb.Enums.CardClass DraftClass { get; }
		public bool IsUnderground { get; }
		/// <summary>
		/// How many weakest cards to surface as cut candidates: the number the screen asks the
		/// player to discard plus a small margin, so there is room to choose. NOT from deck size —
		/// see <see cref="DraftWatcher.BuildDeckEditPlan"/> for why deck size cannot drive this.
		/// </summary>
		public int SuggestCount { get; }

		public DeckReviewEventArgs(IReadOnlyList<DeckReviewCard> deck,
			HearthDb.Enums.CardClass draftClass, bool isUnderground, int suggestCount)
		{
			Deck = deck;
			DraftClass = draftClass;
			IsUnderground = isUnderground;
			SuggestCount = suggestCount;
		}
	}

	/// <summary>
	/// Detects arena draft picks by polling HearthMirror. Call <see cref="GameWatcher.Poll"/>
	/// from the plugin's OnUpdate (~100ms). Fires <see cref="OnChoicesChanged"/>
	/// only when a new set of choices appears (deduped by DraftChoices.Version),
	/// mirroring HDT's own ArenaWatcher approach without depending on its internals.
	/// In the Underground redraft's "Edit Your Deck" phase (no card is being picked — the 5 cards
	/// offered after a loss are already in the deck and 5 must go) it fires
	/// <see cref="OnDeckReview"/> with the whole deck instead.
	/// </summary>
	public class DraftWatcher : GameWatcher
	{
		public event EventHandler<DraftChoicesEventArgs>? OnChoicesChanged;
		public event EventHandler<DeckReviewEventArgs>? OnDeckReview;
		public event EventHandler<RunSummaryEventArgs>? OnRunSummary;
		public event EventHandler? OnDraftEnded;

		private int _lastVersion = int.MinValue;
		private bool _wasDrafting;
		private bool _unresolvedLogged;
		private string? _lastReviewSig; // dedup the deck-edit review; re-fire when the deck changes
		private string? _lastRunSig;    // same, for the run screen: re-fire only when the deck changes
		private ArenaSessionState? _lastQuietState; // the state last reported as "nothing of ours", logged once
		private bool _quietStateLogged; // ...including the unreadable case, which no state value can stand for
		private int _lastPartialChoiceCount = -1; // a non-pick choice count, logged once per distinct value

		/// <summary>The arena screens are gone for real (the client left the DRAFT scene), so anything still
		/// showing from them must be dropped. Distinct from <c>OnDraftEnded</c>, which also fires while the
		/// player is still in arena — see <see cref="OnSceneLeft"/>.</summary>
		public event EventHandler? OnArenaScreenLeft;

		/// <summary>
		/// The RUN screen specifically is no longer being reported, so its panel must go — while the player
		/// may well still be in arena, which is why this cannot ride on <c>OnDraftEnded</c> or on leaving the
		/// scene. The case that needs it: switching to a mode with no run in progress leaves the previous
		/// mode's run panel on screen, because nothing else in the poll clears it.
		/// </summary>
		public event EventHandler? OnRunSummaryGone;

		/// <summary>
		/// The arena screens all live in the DRAFT scene. The gate is the base's, and it is
		/// load-bearing: `ArenaSessionState` alone is not enough — with a redraft left unfinished the
		/// client keeps reporting `EDITING_DECK` while the player is on the main menu or inside
		/// Battlegrounds (both seen in the HDT log), which left the deck panel sitting on top of both.
		/// </summary>
		protected override SceneMode Scene => SceneMode.DRAFT;

		/// <summary>
		/// Left the arena screens entirely. This is the ONLY place the run summary may be torn down, and it
		/// is deliberately not <see cref="EndDraft"/>: that also runs on transitions the player stays in
		/// arena for (a redraft trimmed back to 30, a session state that is no longer a real pick), so
		/// hiding the run panel there made it flicker — or vanish, since its dedup would not re-raise it.
		///
		/// The run signature is cleared here too, and that is what makes re-entering arena work: the run
		/// summary re-fires only when the DECK changes, so leaving and coming back with the same deck would
		/// otherwise never raise it again and the panel would be gone for good. `EndDraft` already does the
		/// same for the review signature; the run one was missing, which is why hiding the panel and
		/// showing it again could not both work.
		/// </summary>
		protected override void OnSceneLeft()
		{
			// Called on EVERY poll while the scene is not ours, so it has to fire ONCE per departure:
			// unguarded it raised the event twice a second and filled the log with identical lines. After
			// the first pass both of these are cleared, so later polls are no-ops.
			var wasShowing = _wasDrafting || _lastRunSig != null || _lastReviewSig != null;
			EndDraft();
			_lastRunSig = null;
			if(wasShowing)
				OnArenaScreenLeft?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>Reset transient state; call from the plugin's OnLoad on (re)enable.</summary>
		public override void Reset()
		{
			base.Reset();
			_lastVersion = int.MinValue;
			_wasDrafting = false;
			_unresolvedLogged = false;
			_lastReviewSig = null;
			_lastRunSig = null;
			_quietStateLogged = false;
			_lastQuietState = null;
		}


		protected override void PollCore()
		{
			var choices = Reflection.Client.GetArenaDraftChoicesV3();

			// Read the deck before concluding anything: in the redraft edit phase there are no
			// choices, but GetArenaDeck reports EDITING_DECK and carries the deck to review.
			ArenaInfo? arenaInfo = null;
			try
			{
				arenaInfo = Reflection.Client.GetArenaDeck();
			}
			catch(Exception ex)
			{
				Log($"GetArenaDeck failed: {ex.Message}");
			}

			var choiceCount = choices?.Choices?.Count ?? 0;
			switch(RouteFor(arenaInfo?.SessionState, choiceCount))
			{
				case PollRoute.DeckEdit:
					// "Edit Your Deck" / discard phase: rank the whole deck so the weakest cards to cut
					// are obvious. This is a real screen even though no card is being picked.
					EndRunSummary(); // the redraft screen is not the run screen
					HandleDeckEdit(arenaInfo!);
					return;

				case PollRoute.PartialChoices:
					// Mid-animation the memory can expose a partial choice list; only 3 is a real pick
					// (HDT's ArenaStateWatcher applies the same 0-or-3 rule).
					//
					// LOGGED, once per distinct count, because this return is otherwise invisible and it
					// swallows the whole poll: a list that is neither empty nor 3 skips the run screen and
					// the session-state diagnostic alike. Live symptom: two minutes of complete silence on
					// the arena deck screen with the scene reading DRAFT — a dead watcher, from outside.
					if(_lastPartialChoiceCount != choiceCount)
					{
						_lastPartialChoiceCount = choiceCount;
						Log($"choice list of {choiceCount} is not a pick (only 0 or 3 are); "
							+ "no screen handled this poll");
					}
					return;

				case PollRoute.RunOrNothing:
					HandleRunOrNothing(arenaInfo);
					return;

				case PollRoute.Retry:
					return; // transient (HS starting/closing): the deck is unreadable, so retry next tick

				default: // PollRoute.Pick — three choices, and a state in which they are a real one
					HandlePick(choices!, arenaInfo!);
					return;
			}
		}

		/// <summary>A real pick is on screen: resolve the offered cards and raise them.</summary>
		private void HandlePick(DraftChoices choices, ArenaInfo arenaInfo)
		{
			_lastPartialChoiceCount = -1;

			// The run panel is not what the player is looking at. It stays ahead of the Version dedup, so
			// a repeated poll of the same pick still clears the panel.
			EndRunSummary();

			if(choices.Version == _lastVersion)
				return;

			// Choices and Packages are index-aligned: Packages[i] is the 3-card bundle that
			// comes with Choices[i] at the Underground legendary-group pick (empty otherwise).
			var packages = choices.Packages;
			var offered = new List<DraftOption>();
			for(var i = 0; i < choices.Choices.Count; i++)
			{
				var dbf = ToDbfId(choices.Choices[i].Id);
				if(dbf == 0)
					continue;

				var package = new List<int>();
				if(packages != null && i < packages.Count && packages[i] != null)
				{
					foreach(var pkgCard in packages[i])
					{
						var pkgDbf = ToDbfId(pkgCard.Id);
						if(pkgDbf != 0)
							package.Add(pkgDbf);
					}
				}
				offered.Add(new DraftOption(choices.Choices[i].Id, dbf, package));
			}

			// EVERY offered card must resolve, and the Version is only consumed once it has. Two
			// reasons, and the second was a live bug: partially resolved choices would lay out N-1
			// plaques centred as if there were N, putting each score over the wrong card; and if
			// HearthDb is not populated yet — which happens when HDT starts while a draft is already
			// open — NOTHING resolves, so consuming the version first fired an empty pick, showed a
			// blank overlay, and then deduped every later poll of that same pick forever.
			if(offered.Count != choices.Choices.Count)
			{
				if(!_unresolvedLogged)
				{
					_unresolvedLogged = true;
					Log($"choices not resolvable yet ({offered.Count}/{choices.Choices.Count}); " +
						"retrying (HearthDb may still be loading)");
				}
				return;
			}
			_unresolvedLogged = false;
			_lastVersion = choices.Version;
			_wasDrafting = true;

			// During a redraft (after losses, Underground AND Normal arena) the picks build
			// the separate RedraftDeck on top of the existing run deck: both are the
			// synergy context for what's being offered.
			var isRedraft = arenaInfo.SessionState is ArenaSessionState.REDRAFTING
				or ArenaSessionState.MIDRUN_REDRAFT_PENDING;
			var contextCards = (arenaInfo.Deck?.Cards ?? new List<Card>()).AsEnumerable();
			if(isRedraft)
				contextCards = contextCards.Concat(arenaInfo.RedraftDeck?.Cards ?? new List<Card>());

			var drafted = contextCards
				.SelectMany(c => Enumerable.Repeat(ToDbfId(c.Id), Math.Max(1, c.Count)))
				.Where(id => id != 0)
				.ToList();

			var isUnderground = arenaInfo.IsUnderground;
			// Single-class arena carries the class on Deck.Hero; dual-class leaves Hero empty
			// and carries it on Deck.HeroPower (as HDT's own ArenaWatcher detects), so fall
			// back to the hero power's class.
			var draftClass = ToClass(arenaInfo.Deck?.Hero);
			if(draftClass == HearthDb.Enums.CardClass.INVALID)
				draftClass = ToClass(arenaInfo.Deck?.HeroPower);

			OnChoicesChanged?.Invoke(this, new DraftChoicesEventArgs(offered, drafted, isUnderground, draftClass, isRedraft));
		}

		// The redraft "Edit Your Deck" phase: rank the current deck (deduped by content, so a
		// discard re-fires it). The deck being edited is the redraft deck when one exists, else
		// the run deck; both counts are logged so the source can be confirmed on a live client.
		/// <summary>
		/// The deck-review decision, split out from HearthMirror so it can be tested: which cards
		/// are in play, how many still have to go, and how many to surface. Pure — no client
		/// access, no events — because an off-by-one here either hides the panel for the whole
		/// redraft or shows it forever, and neither is visible from the outside.
		/// </summary>
		internal readonly struct DeckEditPlan
		{
			/// <summary>dbf id -> copies, the union of the run deck and the redraft additions.</summary>
			public readonly Dictionary<int, int> ByDbf;
			public readonly int DeckSize;
			/// <summary>
			/// How many cards the screen is asking the player to discard. NOT a live countdown: the
			/// client reports the deck at 30/30 throughout the phase, so progress is not observable
			/// and this stays constant until the phase ends.
			/// </summary>
			public readonly int Over;
			/// <summary>How many weakest cards to surface as cut candidates.</summary>
			public readonly int Suggest;

			public DeckEditPlan(Dictionary<int, int> byDbf, int deckSize, int over, int suggest)
			{
				ByDbf = byDbf;
				DeckSize = deckSize;
				Over = over;
				Suggest = suggest;
			}
		}

		internal const int ArenaDeckSize = 30;
		/// <summary>Cut candidates shown beyond what strictly must go, so the player has a choice.</summary>
		internal const int SuggestMargin = 2;
		internal const int MinSuggested = 5;

		/// <summary>
		/// Builds the plan from the two card lists the client exposes. Takes (id, count) pairs so
		/// the caller adapts HearthMirror's types and this stays testable.
		/// </summary>
		internal static DeckEditPlan BuildDeckEditPlan(
			IReadOnlyList<(string Id, int Count)>? runCards,
			IReadOnlyList<(string Id, int Count)>? redraftCards)
		{
			// The RUN DECK alone decides what is in the deck; the redraft list is a FALLBACK for a
			// client form that exposes no run deck, NOT an addition to it.
			//
			// This used to union the two, and that made discards invisible. Verified live: discarding
			// removes the card from `Deck` immediately (the log shows deckSize 31 -> 30), but
			// `RedraftDeck` does NOT shrink — it keeps reporting all five arriving cards for the whole
			// phase. So for any card that was in BOTH lists, the union put it straight back, the
			// signature never changed, and the panel froze with the discarded card still ranked. Found
			// on a real client by toggling Divine Toll out of the deck and back: `distinct` stayed at
			// 25 through every toggle and no re-render fired at all.
			//
			// The cost is a client form where `Deck` omits the arriving cards: those would go
			// unranked. That form has never been observed, while the frozen panel has — and a panel
			// that silently contradicts the deck on screen is worse than one missing five rows.
			var byDbf = new Dictionary<int, int>();
			var authoritative = runCards != null && runCards.Count > 0 ? runCards : redraftCards;
			if(authoritative != null)
			{
				foreach(var c in authoritative)
				{
					var dbf = ToDbfId(c.Id);
					if(dbf == 0)
						continue;
					var count = Math.Max(1, c.Count);
					byDbf[dbf] = byDbf.TryGetValue(dbf, out var existing) ? Math.Max(existing, count) : count;
				}
			}

			var runTotal = runCards?.Sum(c => Math.Max(1, c.Count)) ?? 0;
			var redraftTotal = redraftCards?.Sum(c => Math.Max(1, c.Count)) ?? 0;
			var deckSize = runTotal > 0 ? runTotal : redraftTotal;

			// How many cards must go. VERIFIED ON A LIVE CLIENT, and `deckSize` alone CANNOT drive
			// it because the client reports the phase two different ways across sessions (both
			// observed in the HDT log, same build):
			//   - deckSize=30 for the whole phase, the 5 new cards already counted inside it;
			//   - deckSize=35 counting down 35,34,33,32,31 as cards are picked for discard.
			// So `deckSize - 30` is 0 in the first form, and an earlier version that assumed the
			// deck must shrink left the panel on screen forever.
			//
			// The number to cut is the number that ARRIVED: the redraft list, which starts at 5 in
			// both forms — and in the second form it counts down with the discards, which is the
			// behaviour we want anyway. The panel hides when the phase ends, not on a progress
			// check the first form cannot provide.
			var toDiscard = redraftTotal > 0 ? redraftTotal : Math.Max(0, deckSize - ArenaDeckSize);

			return new DeckEditPlan(byDbf, deckSize, toDiscard,
				Math.Max(MinSuggested, toDiscard + SuggestMargin));
		}

		/// <summary>Which of our screens a poll is about. See <see cref="RouteFor"/>.</summary>
		internal enum PollRoute
		{
			/// <summary>The redraft "Edit Your Deck" phase.</summary>
			DeckEdit,

			/// <summary>The run screen if there is a run to report, otherwise nothing of ours.</summary>
			RunOrNothing,

			/// <summary>A choice list that is neither empty nor 3: an animation artifact, not a pick.</summary>
			PartialChoices,

			/// <summary>The client could not be read this tick; try again.</summary>
			Retry,

			/// <summary>Three offered cards, in a state where they are a real pick.</summary>
			Pick
		}

		/// <summary>
		/// Which screen a poll is about, from the only two things that decide it. Pure and extracted
		/// because getting it wrong is invisible from the outside — the bug it exists to pin produced no
		/// panel and no log line at all, for as long as the player sat on the screen.
		///
		/// The load-bearing case is <c>(MIDRUN, 3)</c>: a FINISHED draft. The client keeps the last pick's
		/// three choices in memory while the session state moves on, so "the run screen" and "no choices on
		/// screen" are NOT the same condition — and while they were treated as one, the run screen (with the
		/// deck panel and the player's own rating hanging off it) was unreachable in the one case it exists
		/// for. A choice count is evidence about ANIMATION, never about which screen the player is on: only
		/// the session state says that, which is why every non-pick state routes the same way whatever is
		/// left in the choice zone.
		///
		/// A null state means the deck was unreadable. With no choices that is still <c>RunOrNothing</c> —
		/// the caller has a panel to tear down and a diagnostic to log, and neither needs the deck — but
		/// with a pick's worth of choices it is <c>Retry</c>, because nothing can be scored without the
		/// class and the drafted cards the deck carries.
		/// </summary>
		internal static PollRoute RouteFor(ArenaSessionState? state, int choiceCount)
		{
			if(state == ArenaSessionState.EDITING_DECK)
				return PollRoute.DeckEdit;
			if(choiceCount == 0)
				return PollRoute.RunOrNothing;
			if(choiceCount != 3)
				return PollRoute.PartialChoices;
			if(state == null)
				return PollRoute.Retry;
			return IsActiveDraftState(state.Value) ? PollRoute.Pick : PollRoute.RunOrNothing;
		}

		/// <summary>
		/// The run screen if there is a run to report, otherwise nothing of ours. One handler for both
		/// routes that reach it — a draft that has just ended and a screen with no choices at all — because
		/// they differ only in what the client happens to have left in the choice zone.
		///
		/// <see cref="EndDraft"/> runs only when there is no run to report, and that ordering matters: it
		/// fires whenever <c>_wasDrafting</c> is set, which <see cref="HandleRunSummary"/> itself sets, so
		/// calling it unconditionally re-raised <c>OnDraftEnded</c> on every poll of the run screen — twice
		/// a second, log line included. Showing the run panel already replaces the pick panel (one overlay,
		/// one screen), so nothing needs tearing down first.
		/// </summary>
		private void HandleRunOrNothing(ArenaInfo? arenaInfo)
		{
			// Gated POSITIVELY on a run state and a complete deck rather than on "nothing else is showing",
			// because this is the screen that produced a ghost overlay twice — an unfinished redraft reports
			// EDITING_DECK from the main menu, and "nothing else is showing" is true of Battlegrounds too.
			if(arenaInfo != null && HandleRunSummary(arenaInfo))
				return;

			// Nothing of ours is on screen — including no run to report. This is the path a mode switch
			// takes when the other mode has no run in progress, and without EndRunSummary the previous
			// mode's panel stayed up.
			LogQuietState(arenaInfo?.SessionState);
			EndRunSummary();
			EndDraft();
		}

		/// <summary>
		/// The run screen between matches: a finished deck, and no pick to make. Returns true when it
		/// took responsibility for the screen, so the caller does NOT hide the overlay.
		///
		/// The session state is the whole gate, and it has to be: this screen reports as the DRAFT scene
		/// (the client's arena hub does), so the scene gate alone cannot tell it from the main menu, and
		/// "no choices on screen" is true of every screen in the game.
		///
		/// `MIDRUN` alone was too narrow. Measured live, the client also reports **`REDRAFTING`** while the
		/// player is on the deck screen around a redraft, and in that state nothing showed at all: the
		/// deck-review panel wants `EDITING_DECK`, this wanted `MIDRUN`, and the rating panel hangs off
		/// this one. `MIDRUN_REDRAFT_PENDING` is included for the same reason — the run exists and the deck
		/// is complete in both.
		///
		/// KNOWN RISK, accepted deliberately: `EDITING_DECK` is documented to leak outside arena (the
		/// client keeps reporting it from the main menu and from inside Battlegrounds, which is how the
		/// deck panel once sat on top of both). Whether `REDRAFTING` leaks the same way is NOT established.
		/// If a run panel ever appears outside arena, this widening is the first thing to suspect, and the
		/// log line below names the state that let it through.
		/// </summary>
		private bool HandleRunSummary(ArenaInfo arenaInfo)
		{
			if(!IsRunScreenState(arenaInfo.SessionState))
				return false;

			var deck = new List<int>();
			var cards = arenaInfo.Deck?.Cards;
			if(cards != null)
			{
				foreach(var card in cards)
				{
					var dbf = ToDbfId(card.Id);
					if(dbf == 0)
						continue;
					for(var i = 0; i < Math.Max(1, card.Count); i++)
						deck.Add(dbf);
				}
			}

			// A partial read is not a deck. Same reasoning as the pick gate: HearthDb can be empty at
			// startup, and describing 11 of 30 cards would state a curve the player does not have.
			if(deck.Count < ArenaDeckSize)
				return false;

			var draftClass = ToClass(arenaInfo.Deck?.Hero);
			if(draftClass == HearthDb.Enums.CardClass.INVALID)
				draftClass = ToClass(arenaInfo.Deck?.HeroPower);

			// The signature carries the MODE and the RECORD, not just the deck. Deck-only was enough to
			// re-fire on a pick, and wrong for everything else the panel shows: with an unchanged deck a
			// win would not update the displayed W-L, and two runs sharing a deck would not redraw when
			// switching between them. Prophylactic rather than observed — but the panel states these
			// numbers, so they belong in the key that decides whether to restate them.
			var sig = $"{arenaInfo.IsUnderground}:{draftClass}:{arenaInfo.Wins}-{arenaInfo.Losses}:"
				+ deck.Count + ":" + string.Join(",", deck.OrderBy(d => d));
			if(sig != _lastRunSig)
			{
				_lastRunSig = sig;
				Log($"run screen: {deck.Count} cards, class={draftClass}, "
					+ $"underground={arenaInfo.IsUnderground}, {arenaInfo.Wins}-{arenaInfo.Losses}");
				OnRunSummary?.Invoke(this,
					new RunSummaryEventArgs(deck, draftClass, arenaInfo.IsUnderground,
						arenaInfo.Wins, arenaInfo.Losses));
			}

			_wasDrafting = true; // leaving this screen fires OnDraftEnded, which hides the panel
			return true;
		}

		private void HandleDeckEdit(ArenaInfo arenaInfo)
		{
			var plan = BuildDeckEditPlan(ToPairs(arenaInfo.Deck?.Cards), ToPairs(arenaInfo.RedraftDeck?.Cards));
			if(plan.ByDbf.Count == 0)
				return;

			Log($"deck-edit phase: {plan.ByDbf.Count} distinct, deckSize={plan.DeckSize}, over={plan.Over}");

			// Nothing to cut at all (e.g. the phase read with no redraft cards yet): hide. Note this
			// is NOT a completion check — the client gives no observable discard progress, so the
			// panel stays up for the whole screen and disappears when EDITING_DECK ends.
			if(plan.Over <= 0)
			{
				EndDraft(); // trimmed to 30 (or nothing to cut): hide the panel (fires once)
				return;
			}
			_wasDrafting = true; // now showing the panel; leaving the phase fires OnDraftEnded

			var deck = plan.ByDbf.Select(kv => new DeckReviewCard(kv.Key, kv.Value)).ToList();
			var sig = string.Join(",", deck.Select(d => d.DbfId + "x" + d.Count).OrderBy(s => s))
				+ "|" + plan.Suggest;
			if(sig == _lastReviewSig)
				return;
			_lastReviewSig = sig;

			var draftClass = ToClass(arenaInfo.Deck?.Hero);
			if(draftClass == HearthDb.Enums.CardClass.INVALID)
				draftClass = ToClass(arenaInfo.Deck?.HeroPower);

			OnDeckReview?.Invoke(this,
				new DeckReviewEventArgs(deck, draftClass, arenaInfo.IsUnderground, plan.Suggest));
		}

		private static IReadOnlyList<(string Id, int Count)>? ToPairs(
			IEnumerable<HearthMirror.Objects.Card>? cards)
			=> cards?.Select(c => (c.Id, c.Count)).ToList();

		/// <summary>
		/// Drops the run panel, once, when the run screen stops being reported. Called from every poll path
		/// that is NOT the run screen — a pick, the deck-edit phase, and "nothing of ours on screen" — because
		/// the player can leave the run screen without leaving arena, and nothing else clears it then.
		/// </summary>
		/// <summary>The states in which a completed run deck is what the player is looking at. See
		/// <see cref="HandleRunSummary"/> for why this is the whole gate and what it risks.</summary>
		private static bool IsRunScreenState(ArenaSessionState state)
			=> state is ArenaSessionState.MIDRUN
				or ArenaSessionState.REDRAFTING
				or ArenaSessionState.MIDRUN_REDRAFT_PENDING;

		/// <summary>
		/// Names the session state that led to no screen of ours, once per distinct value. Every "why is
		/// nothing showing?" ends up on one of the two paths that call this, and both used to return in
		/// silence — so the log could not tell an unrecognised state from a screen we had decided to ignore,
		/// and could not tell either of them from a dead watcher. Both symptoms were seen live.
		/// </summary>
		private void LogQuietState(ArenaSessionState? state)
		{
			// `_quietStateLogged` and not just the value: null means UNREADABLE here, and it is also the
			// field's initial value, so a state that could not be read at all would never be logged.
			if(_quietStateLogged && state == _lastQuietState)
				return;
			_quietStateLogged = true;
			_lastQuietState = state;
			Log($"no arena screen of ours: session state {(state == null ? "unreadable" : state.ToString())}");
		}

		private void EndRunSummary()
		{
			if(_lastRunSig == null)
				return;
			_lastRunSig = null;
			OnRunSummaryGone?.Invoke(this, EventArgs.Empty);
		}

		private void EndDraft()
		{
			_lastReviewSig = null;
			if(!_wasDrafting)
				return;
			_wasDrafting = false;
			_lastVersion = int.MinValue;
			OnDraftEnded?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>
		/// The session states in which the offered choices are a real pick. Choices linger
		/// in client memory on other screens (landing page, mid-run) — HDT gates on the
		/// same states in its ArenaStateWatcher.
		/// </summary>
		internal static bool IsActiveDraftState(ArenaSessionState state)
			=> state is ArenaSessionState.DRAFTING
				or ArenaSessionState.REDRAFTING
				or ArenaSessionState.MIDRUN_REDRAFT_PENDING;

		internal static int ToDbfId(string? cardId)
		{
			if(string.IsNullOrEmpty(cardId))
				return 0;
			return HearthDb.Cards.All.TryGetValue(cardId, out var card) ? card.DbfId : 0;
		}

		/// <summary>The class of the drafted hero card id; INVALID before the hero pick.</summary>
		internal static HearthDb.Enums.CardClass ToClass(string? heroCardId)
		{
			if(string.IsNullOrEmpty(heroCardId))
				return HearthDb.Enums.CardClass.INVALID;
			return HearthDb.Cards.All.TryGetValue(heroCardId, out var card)
				? card.Class
				: HearthDb.Enums.CardClass.INVALID;
		}

	}
}
