using System;
using System.Collections.Generic;
using System.Linq;
using HearthMirror;
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

		public DraftChoicesEventArgs(IReadOnlyList<DraftOption> offered, IReadOnlyList<int> draftedDbfIds, bool isUnderground)
		{
			Offered = offered;
			DraftedDbfIds = draftedDbfIds;
			IsUnderground = isUnderground;
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
				if(_wasDrafting)
				{
					_wasDrafting = false;
					_lastVersion = int.MinValue;
					OnDraftEnded?.Invoke(this, EventArgs.Empty);
				}
				return;
			}

			if(choices.Version == _lastVersion)
				return;
			_lastVersion = choices.Version;
			_wasDrafting = true;

			ArenaInfo? arenaInfo = null;
			try
			{
				arenaInfo = Reflection.Client.GetArenaDeck();
			}
			catch(Exception ex)
			{
				Log($"GetArenaDeck failed: {ex.Message}");
			}

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

			var drafted = (arenaInfo?.Deck?.Cards ?? new List<Card>())
				.SelectMany(c => Enumerable.Repeat(ToDbfId(c.Id), Math.Max(1, c.Count)))
				.Where(id => id != 0)
				.ToList();

			var isUnderground = arenaInfo?.IsUnderground ?? false;

			OnChoicesChanged?.Invoke(this, new DraftChoicesEventArgs(offered, drafted, isUnderground));
		}

		internal static int ToDbfId(string? cardId)
		{
			if(string.IsNullOrEmpty(cardId))
				return 0;
			return HearthDb.Cards.All.TryGetValue(cardId, out var card) ? card.DbfId : 0;
		}

		private static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] {msg}");
	}
}
