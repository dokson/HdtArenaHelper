using System;
using HearthMirror;
using HearthMirror.Enums;

namespace HdtArenaHelper
{
	/// <summary>The current arena opponent's BattleTag name, as read from the client.</summary>
	public class OpponentIdentityEventArgs : EventArgs
	{
		/// <summary>BattleTag name with no discriminator (matches Blizzard's leaderboard <c>accountid</c>
		/// field, which also carries no discriminator).</summary>
		public string BattleTagName { get; }
		public OpponentIdentityEventArgs(string battleTagName) => BattleTagName = battleTagName;
	}

	/// <summary>
	/// Detects the current arena match's opponent, by BattleTag, so the overlay can look their rank up
	/// on Blizzard's public leaderboard (<see cref="ArenaLeaderboardSource"/>).
	///
	/// Gated like <see cref="MulliganWatcher"/>: GAMEPLAY scene, arena match only — the leaderboard
	/// lookup is only meaningful for an arena opponent, not a Battlegrounds or ranked one sharing the
	/// same GAMEPLAY scene.
	///
	/// Reads <c>Reflection.Client.GetMatchInfo()</c> rather than HDT's own <c>Core.Game.Opponent.Name</c>:
	/// that field is populated from the in-game entity name (may carry no discriminator either way, and
	/// its exact provenance depends on log-parser timing), while HearthMirror's <c>MatchInfo</c> reads
	/// the BattleTag directly from the client's own presence manager — the same battletag Blizzard's
	/// leaderboard reports as <c>accountid</c>.
	/// </summary>
	public class OpponentIdentityWatcher : GameWatcher
	{
		public event EventHandler<OpponentIdentityEventArgs>? OnOpponentIdentified;
		public event EventHandler? OnOpponentGone;

		private string? _lastTag;

		protected override SceneMode Scene => SceneMode.GAMEPLAY;
		protected override bool ArenaMatchOnly => true;

		protected override void OnSceneLeft() => Clear();

		public override void Reset()
		{
			base.Reset();
			_lastTag = null;
		}

		protected override void PollCore()
		{
			var tag = Reflection.Client.GetMatchInfo()?.OpposingPlayer?.BattleTag?.Name;
			if(string.IsNullOrWhiteSpace(tag))
			{
				Clear();
				return;
			}
			if(tag == _lastTag)
				return;
			_lastTag = tag;
			Log($"opponent identified: '{tag}'");
			OnOpponentIdentified?.Invoke(this, new OpponentIdentityEventArgs(tag!));
		}

		private void Clear()
		{
			if(_lastTag == null)
				return;
			_lastTag = null;
			OnOpponentGone?.Invoke(this, EventArgs.Empty);
		}
	}
}
