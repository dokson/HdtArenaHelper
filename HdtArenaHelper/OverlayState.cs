namespace HdtArenaHelper
{
	/// <summary>
	/// Which of our screens is showing, and whether the overlay should therefore be visible at all.
	///
	/// Extracted for one reason: this decision produced FOUR overlay bugs, and every one of them was found
	/// by a person looking at a live client rather than by a test. Three were ghost overlays — the arena run
	/// panel on top of a Battlegrounds game, the previous mode's run after switching between Underground and
	/// Normal Arena, and a panel that outlived the screen it described — and the fourth was the opposite, a
	/// panel that vanished for the rest of the arena screen because the fix for the first was hung on the
	/// wrong event. None of that was testable while the rules lived inside <c>OnUpdate</c> next to WPF and
	/// HearthMirror calls; all of it is testable here, which is the same move this project already made for
	/// <c>BuildChoicePlan</c>, <c>BuildDeckEditPlan</c>, <c>BuildMulliganPlan</c> and <c>ChoiceGate</c>.
	///
	/// Pure by construction: no WPF, no HearthMirror, no logging. It is handed event ARGS as plain objects
	/// and never looks inside them — the type is the only thing that matters, because that is what says which
	/// screen a teardown is allowed to drop.
	///
	/// The distinctions that took four bugs to find, and which the tests pin:
	/// <list type="bullet">
	/// <item>the draft PANEL ending is not the arena SCREEN ending — the run summary must survive the
	/// first and not the second, because the first also fires while the player stays in arena;</item>
	/// <item>the RUN screen can stop being reported while the player is still in arena (switching to a mode
	/// with no run in progress), so it needs a teardown of its own;</item>
	/// <item>during a match there is no screen of ours at all outside a mulligan or a Discover, so the
	/// opponent's standings are a second, independent reason to be visible — and one that must not be able
	/// to outlive the match, which is exactly how the ghosts happened.</item>
	/// </list>
	/// </summary>
	internal sealed class OverlayState
	{
		/// <summary>The event args of the screen currently showing, or null. Kept as <c>object</c> so this
		/// class needs to know nothing about any screen beyond its type.</summary>
		internal object? ActiveScreen { get; private set; }

		/// <summary>True while an arena MATCH's standings are showing. Set only when an opponent has been
		/// identified — which the watcher gates to arena matches — and cleared when they are gone.</summary>
		internal bool MatchStandings { get; private set; }

		/// <summary>
		/// Should the overlay window be visible? A screen of ours, OR match standings: the second is what
		/// keeps the opponent's rank on screen through a game, where no screen of ours exists.
		/// </summary>
		internal bool WantVisible(bool dataReady) => dataReady && (ActiveScreen != null || MatchStandings);

		/// <summary>A screen is showing. Replaces whatever was there — one overlay, one screen.</summary>
		internal void Show(object screen) => ActiveScreen = screen;

		/// <summary>Drops the active screen only if it is of this kind, so a teardown cannot remove a screen
		/// that belongs to a different watcher.</summary>
		internal void Clear<T>() where T : class
		{
			if(ActiveScreen is T)
				ActiveScreen = null;
		}

		/// <summary>
		/// The draft PICK/REVIEW panel is gone. The run summary deliberately survives: this also fires on
		/// transitions the player stays in arena for, and dropping the run panel here made it disappear for
		/// the rest of the arena screen — the watcher will not raise it again while the deck is unchanged.
		/// </summary>
		internal void DraftEnded()
		{
			Clear<DraftChoicesEventArgs>();
			Clear<DeckReviewEventArgs>();
		}

		/// <summary>The RUN screen specifically stopped being reported, while the player may well still be in
		/// arena — switching to a mode with no run in progress is the case that needs this.</summary>
		internal void RunSummaryGone() => Clear<RunSummaryEventArgs>();

		/// <summary>The client left the arena screens for real, so everything they raise goes.</summary>
		internal void ArenaScreenLeft()
		{
			Clear<DraftChoicesEventArgs>();
			Clear<DeckReviewEventArgs>();
			Clear<RunSummaryEventArgs>();
		}

		internal void OpponentIdentified() => MatchStandings = true;

		internal void OpponentGone() => MatchStandings = false;

		/// <summary>Clears everything; for the plugin's (re)enable path, so a stale screen cannot bleed into a
		/// new session.</summary>
		internal void Reset()
		{
			ActiveScreen = null;
			MatchStandings = false;
		}
	}
}
