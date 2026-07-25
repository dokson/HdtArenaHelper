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

		/// <summary>The only scene in which this watcher's screen can exist.</summary>
		protected abstract SceneMode Scene { get; }

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
				return scene == null || (SceneMode)scene.Value.Mode == Scene;
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

		protected static void Log(string msg)
			=> Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[ArenaHelper] {msg}");
	}
}
