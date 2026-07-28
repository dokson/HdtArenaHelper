using System;
using HearthMirror;
using HearthMirror.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// Template method for polling the Hearthstone client: the base owns the throttle, the SCENE gate
	/// and log-once-per-failure-streak; subclasses implement what they read and what they do with it.
	///
	/// The gate is why this base exists: both ghost-overlay bugs this project had came from a watcher
	/// acting on another screen's state, and the check was already byte-identical in two pollers.
	///
	/// Deliberately NOT here: dedup and the "gone" event. Those genuinely differ (Version vs an
	/// offered-id list vs EndDraft's showing-state), and unifying them would flatten behaviour that
	/// took two live bugs to get right.
	/// </summary>
	public abstract class GameWatcher
	{
		// HDT's own cadence. The mono memory reads are not free and every screen this drives changes
		// on a human timescale, so there is nothing to gain from polling faster.
		private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

		private DateTime _nextPollUtc = DateTime.MinValue;
		private bool _readErrorLogged;
		private bool _sceneErrorLogged;
		private SceneMode? _blockedSceneLogged;
		private bool _gameTypeErrorLogged;
		private int _blockedGameTypeLogged = -1;

		/// <summary>The only scene in which this watcher's screen can exist.</summary>
		protected abstract SceneMode Scene { get; }

		/// <summary>
		/// Does this watcher's screen only exist inside an ARENA match? An arena RUN being open is not
		/// the same thing, and the difference was a live bug: with a 30-card Paladin run in progress, a
		/// Battlegrounds hero/trinket choice sits in the SAME choice zone on the SAME GAMEPLAY scene, so
		/// the choice watcher scored it and painted arena win-rates over a Battlegrounds board.
		/// </summary>
		protected virtual bool ArenaMatchOnly => false;

		/// <summary>Read the client and fire events. Exceptions are handled by the template.</summary>
		protected abstract void PollCore();

		/// <summary>Called when the scene is no longer ours: drop any state and hide.</summary>
		protected abstract void OnSceneLeft();

		/// <summary>Reset transient state; call from the plugin's OnLoad on (re)enable.</summary>
		public virtual void Reset()
		{
			_nextPollUtc = DateTime.MinValue;
			_readErrorLogged = false;
			_sceneErrorLogged = false;
			_blockedSceneLogged = null;
			_gameTypeErrorLogged = false;
			_blockedGameTypeLogged = -1;
		}

		public void Poll()
		{
			var now = DateTime.UtcNow;
			if(now < _nextPollUtc)
				return;
			_nextPollUtc = now + PollInterval;

			if(!IsSceneActive())
			{
				OnSceneLeft();
				return;
			}

			if(ArenaMatchOnly && !IsArenaMatch())
			{
				OnSceneLeft();
				return;
			}

			try
			{
				PollCore();
				_readErrorLogged = false;
			}
			catch(Exception ex)
			{
				// HS starting or closing throws every tick: one line per failure streak, not 2/second.
				if(!_readErrorLogged)
				{
					_readErrorLogged = true;
					Log($"client read unavailable (retrying quietly): {ex.Message}");
				}
			}
		}

		/// <summary>
		/// Is the client showing our scene? Fails PERMISSIVE: if the scene cannot be read we fall
		/// through to the watcher's own state checks rather than going silent for the session. A ghost
		/// panel gets reported by a user; a helper that never appears looks like a dead plugin.
		/// </summary>
		private bool IsSceneActive()
		{
			try
			{
				var scene = Reflection.Client.GetSceneMgrState();
				if(scene == null)
					return true; // unreadable: fail permissive, as documented above
				var mode = (SceneMode)scene.Value.Mode;
				if(mode == Scene)
				{
					_blockedSceneLogged = null;
					return true;
				}
				// The scene VALUE is logged, once per distinct one: this gate returns before PollCore, so
				// every diagnostic inside the watcher is unreachable when it rejects — and "the plugin shows
				// nothing and says nothing" was indistinguishable from a dead plugin. Live example: after an
				// Underground redraft the arena deck screen reported a scene that is not DRAFT, and four
				// minutes of total log silence were the only symptom.
				if(_blockedSceneLogged != mode)
				{
					_blockedSceneLogged = mode;
					Log($"scene {mode} is not {Scene}; {GetType().Name} staying quiet");
				}
				return false;
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

		/// <summary>
		/// Is the current MATCH an arena one? Fails permissive in the same spirit as the scene gate, but
		/// only for states that carry no information: an unreadable type, or the GT_UNKNOWN the client
		/// reports for a moment while a game starts — which is exactly when the mulligan screen appears,
		/// so treating it as "not arena" would cost that feature its whole window. A type the client
		/// states and that is not arena is a definite no.
		/// </summary>
		private bool IsArenaMatch()
		{
			int gameType;
			try
			{
				gameType = Reflection.Client.GetGameType();
				_gameTypeErrorLogged = false;
			}
			catch(Exception ex)
			{
				if(!_gameTypeErrorLogged)
				{
					_gameTypeErrorLogged = true;
					Log($"game type unavailable, arena-match gate disabled: {ex.Message}");
				}
				return true;
			}

			if(IsArenaGameType(gameType))
			{
				_blockedGameTypeLogged = -1;
				return true;
			}

			// One line per streak: the point is to explain a MISSING overlay in the log, not to narrate
			// every tick of a Battlegrounds game.
			if(_blockedGameTypeLogged != gameType)
			{
				_blockedGameTypeLogged = gameType;
				Log($"not an arena match ({(HearthDb.Enums.GameType)gameType}); staying hidden");
			}
			return false;
		}

		/// <summary>
		/// The arena game types, including their vs-AI variants. Anything else — Battlegrounds, ranked,
		/// a brawl — is a match whose cards this plugin has no win-rate for. An id HearthDb does not
		/// know is treated as non-arena: a future mode is not arena until someone here says it is.
		/// </summary>
		internal static bool IsArenaGameType(int gameType)
		{
			switch((HearthDb.Enums.GameType)gameType)
			{
				case HearthDb.Enums.GameType.GT_ARENA:
				case HearthDb.Enums.GameType.GT_ARENA_PLAYER_VS_AI:
				case HearthDb.Enums.GameType.GT_UNDERGROUND_ARENA:
				case HearthDb.Enums.GameType.GT_UNDERGROUND_ARENA_PLAYER_VS_AI:
				case HearthDb.Enums.GameType.GT_UNKNOWN:
					return true;
				default:
					return false;
			}
		}

		protected static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] {msg}");
	}
}
