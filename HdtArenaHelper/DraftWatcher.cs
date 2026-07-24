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
	/// Detects arena draft picks by polling HearthMirror. Call <see cref="Poll"/>
	/// from the plugin's OnUpdate (~100ms). Fires <see cref="OnChoicesChanged"/>
	/// only when a new set of choices appears (deduped by DraftChoices.Version),
	/// mirroring HDT's own ArenaWatcher approach without depending on its internals.
	/// </summary>
	public class DraftWatcher
	{
		public event EventHandler<DraftChoicesEventArgs>? OnChoicesChanged;
		public event EventHandler? OnDraftEnded;

		private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

		private int _lastVersion = int.MinValue;
		private bool _wasDrafting;
		private DateTime _nextPollUtc = DateTime.MinValue;
		private bool _pollErrorLogged;

		/// <summary>Reset transient state; call from the plugin's OnLoad on (re)enable.</summary>
		public void Reset()
		{
			_lastVersion = int.MinValue;
			_wasDrafting = false;
			_nextPollUtc = DateTime.MinValue;
			_pollErrorLogged = false;
		}

		public void Poll()
		{
			// HDT calls OnUpdate ~10x/s; HDT's own ArenaWatcher polls at 500ms. Match that so we
			// don't hammer the mono memory-reflection read continuously outside Arena.
			var now = DateTime.UtcNow;
			if(now < _nextPollUtc)
				return;
			_nextPollUtc = now + PollInterval;

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

			if(choices == null || choices.Choices == null || choices.Choices.Count == 0)
			{
				EndDraft();
				return;
			}

			// Mid-animation the memory can expose a partial choice list; only 3 is a real
			// pick (HDT's ArenaStateWatcher applies the same 0-or-3 rule).
			if(choices.Choices.Count != 3)
				return;

			ArenaInfo? arenaInfo = null;
			try
			{
				arenaInfo = Reflection.Client.GetArenaDeck();
			}
			catch(Exception ex)
			{
				Log($"GetArenaDeck failed: {ex.Message}");
			}
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

		private void EndDraft()
		{
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
