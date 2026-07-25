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
	/// Detects arena draft picks by polling HearthMirror. Call <see cref="Poll"/>
	/// from the plugin's OnUpdate (~100ms). Fires <see cref="OnChoicesChanged"/>
	/// only when a new set of choices appears (deduped by DraftChoices.Version),
	/// mirroring HDT's own ArenaWatcher approach without depending on its internals.
	/// In the Underground redraft's "Edit Your Deck" phase (no card is being picked — the 5 cards
	/// offered after a loss are already in the deck and 5 must go) it fires
	/// <see cref="OnDeckReview"/> with the whole deck instead.
	/// </summary>
	public class DraftWatcher
	{
		public event EventHandler<DraftChoicesEventArgs>? OnChoicesChanged;
		public event EventHandler<DeckReviewEventArgs>? OnDeckReview;
		public event EventHandler? OnDraftEnded;

		private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

		private int _lastVersion = int.MinValue;
		private bool _wasDrafting;
		private DateTime _nextPollUtc = DateTime.MinValue;
		private bool _pollErrorLogged;
		private bool _sceneErrorLogged;
		private string? _lastReviewSig; // dedup the deck-edit review; re-fire when the deck changes

		/// <summary>Reset transient state; call from the plugin's OnLoad on (re)enable.</summary>
		public void Reset()
		{
			_lastVersion = int.MinValue;
			_wasDrafting = false;
			_nextPollUtc = DateTime.MinValue;
			_pollErrorLogged = false;
			_sceneErrorLogged = false;
			_lastReviewSig = null;
		}

		/// <summary>
		/// True while Hearthstone is showing the arena draft scene (the card draft, the hero pick
		/// and the redraft's "Edit Your Deck" all live there).
		///
		/// Fails PERMISSIVE on purpose: if the scene cannot be read we fall back to the session-state
		/// gate alone rather than silently disabling the overlay for the whole session. A ghost panel
		/// on the wrong screen is a visible annoyance the user reports; an overlay that never appears
		/// looks like a broken plugin.
		/// </summary>
		private bool IsArenaSceneActive()
		{
			try
			{
				var scene = Reflection.Client.GetSceneMgrState();
				if(scene == null)
					return true;
				return (SceneMode)scene.Value.Mode == SceneMode.DRAFT;
			}
			catch(Exception ex)
			{
				if(!_sceneErrorLogged)
				{
					_sceneErrorLogged = true;
					Log($"scene state unavailable, scene gate disabled: {ex.Message}");
				}
				return true;
			}
		}

		public void Poll()
		{
			// HDT calls OnUpdate ~10x/s; HDT's own ArenaWatcher polls at 500ms. Match that so we
			// don't hammer the mono memory-reflection read continuously outside Arena.
			var now = DateTime.UtcNow;
			if(now < _nextPollUtc)
				return;
			_nextPollUtc = now + PollInterval;

			// FIRST gate, before reading anything else: is the arena screen the one the player is
			// actually looking at? VERIFIED LIVE that ArenaSessionState alone is not enough — with a
			// redraft left unfinished the client keeps reporting EDITING_DECK while the player is on
			// the main menu and inside Battlegrounds (both in the HDT log), so the deck panel sat on
			// top of both for minutes. The scene manager is the authority on the current screen.
			if(!IsArenaSceneActive())
			{
				EndDraft();
				return;
			}

			DraftChoices? choices;
			try
			{
				choices = Reflection.Client.GetArenaDraftChoicesV3();
				_pollErrorLogged = false;
			}
			catch(Exception ex)
			{
				// HS starting/closing throws every tick - log once per failure streak, not 2x/s.
				if(!_pollErrorLogged)
				{
					_pollErrorLogged = true;
					Log($"GetArenaDraftChoices unavailable (retrying quietly): {ex.Message}");
				}
				return;
			}

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

			// "Edit Your Deck" / discard phase: rank the whole deck so the weakest cards to cut
			// are obvious. This is a real screen even though no card is being picked.
			if(arenaInfo != null && arenaInfo.SessionState == ArenaSessionState.EDITING_DECK)
			{
				HandleDeckEdit(arenaInfo);
				return;
			}

			if(choices == null || choices.Choices == null || choices.Choices.Count == 0)
			{
				EndDraft();
				return;
			}

			// Mid-animation the memory can expose a partial choice list; only 3 is a real
			// pick (HDT's ArenaStateWatcher applies the same 0-or-3 rule).
			if(choices.Choices.Count != 3)
				return;

			if(arenaInfo == null)
				return; // transient (HS starting/closing); retry next tick

			// Choices can linger in memory outside the draft (landing screen, mid-run):
			// only DRAFTING and the redraft states are real picks — anything else must
			// hide the overlay, or we'd paint plaques over a non-draft screen.
			if(!IsActiveDraftState(arenaInfo.SessionState))
			{
				EndDraft();
				return;
			}

			if(choices.Version == _lastVersion)
				return;
			_lastVersion = choices.Version;
			_wasDrafting = true;

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
			// Union both lists by dbf id: the run deck is the full deck to trim, while the redraft
			// list holds only the NEW cards being added — ranking either alone misses cards. Keep
			// the larger copy count on overlap; the count only affects the "xN" label, not the rank.
			var byDbf = new Dictionary<int, int>();
			foreach(var list in new[] { runCards, redraftCards })
			{
				if(list == null)
					continue;
				foreach(var c in list)
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

		private static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] {msg}");
	}
}
