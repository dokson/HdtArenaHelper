using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility.Logging;
using HdtCard = Hearthstone_Deck_Tracker.Hearthstone.Card;

namespace HdtArenaHelper
{
	/// <summary>
	/// Open-source arena draft helper for Hearthstone Deck Tracker.
	///
	/// Reads the current draft via HearthMirror, scores each offered option by merging
	/// normalized ratings from one or more data sources (the public HSReplay "free" arena
	/// endpoint plus an offline metadata heuristic) with a deck-context synergy bonus, and
	/// shows the result in an overlay. While active it takes over the arena overlay from
	/// HDT's built-in one (draft and hero pick), restoring it when disabled.
	///
	/// The overlay lifecycle is driven entirely from <see cref="OnUpdate"/> (which HDT calls
	/// on its UI thread): it renders when the pick or data-readiness changes and recomputes
	/// visibility every tick, so nothing is shown before data has loaded and nothing lingers
	/// once Hearthstone is minimised or the pick is over.
	/// </summary>
	public class ArenaHelperPlugin : IPlugin
	{
		public string Name => "Arena Helper";
		public string Description =>
			"Open-source arena draft helper. Blends free public card-winrate data " +
			"into a single 0-100 score per pick, with deck synergy. Uses no paid " +
			"or scraped data.";
		public string ButtonText => "Refresh data";
		public string Author => "Alessandro Colace";
		// Version.props is the single source of truth; read it back from the assembly
		// so a release bump cannot drift from what this property reports.
		public Version Version
		{
			get
			{
				var v = typeof(ArenaHelperPlugin).Assembly.GetName().Version;
				return v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, v.Build);
			}
		}

		private readonly DraftWatcher _watcher = new DraftWatcher();
		// In-game choices (Discover). A separate watcher, not a branch of DraftWatcher: it polls a
		// different client structure and is gated on a different scene, and the draft path's two
		// ghost-overlay bugs both came from one gate serving two screens.
		private readonly CardChoiceWatcher _choiceWatcher = new CardChoiceWatcher();
		private readonly MulliganWatcher _mulliganWatcher = new MulliganWatcher();
		// Deck-relative mulligan advice. No data source behind it by design — see DeckMulliganAdvisor.
		private readonly IMulliganAdvisor _mulliganAdvisor = new DeckMulliganAdvisor();
		// Card map builds lazily on first use, so constructing at OnLoad (HearthDb may
		// still be empty) is safe.
		private readonly MetadataSynergyEngine _synergy = new MetadataSynergyEngine();
		private volatile ScoreAggregator? _aggregator;
		private ArenaOverlayWindow? _overlay;
		private MenuItem? _menuItem;
		private bool _enabled = true;

		// Self-update over GitHub Releases (see SelfUpdater). Auto-check on load (throttled to
		// once/day) and stage the new DLL for the next restart; toggleable, off by never touching
		// the network. UI state lives on the menu; _updatePage is the page to open on manual fallback.
		private SelfUpdater? _updater;
		private bool _autoUpdate = true;
		private bool _autoUpdateLoaded;          // pref read lazily (menu can build before OnLoad)
		private volatile bool _updatePending;   // a newer DLL was staged this session
		private int _updateCheckRunning;         // Interlocked guard: one check in flight at a time
		private MenuItem? _updateStatusItem;     // the submenu line reporting update state
		private string _updateStatusText = "—"; // last status, replayed when the menu builds late
		private volatile string? _updatePage;    // releases page to open on click, when relevant
												 // Cancels an in-flight check on unload/disable: without it a download that finishes after
												 // the plugin is gone would still run the rename swap on a DLL nobody will reload.
		private CancellationTokenSource? _updateCts;
		private DateTime _lastManualCheckUtc = DateTime.MinValue;
		private static readonly TimeSpan ManualCheckFloor = TimeSpan.FromMinutes(1);
		private bool _nativeOverlayPrev = true; // user's EnableArenasmithOverlay, restored on unload/disable
		private bool _nativeOverlaySaved;

		private volatile bool _dataReady;            // sources have finished their load attempt
		private volatile int _warmGeneration;        // bumped per WarmData; a superseded loop stops
													 // Pairs the generation check with the _dataReady write: without it a superseded
													 // loop could pass the check, get preempted by a Refresh (bump + reset), then
													 // stamp _dataReady with the OLD aggregator's verdict.
		private readonly object _warmLock = new object();
		// ONE active screen, not four nullable fields: the screens are mutually exclusive, and the old
		// shape maintained that in two places (every handler AND every render branch nulled the
		// siblings), so missing one left a stale screen up.
		private object? _activeScreen;   // DraftChoices | DeckReview | CardChoice | Mulligan event args
		private object? _renderedScreen; // the instance the overlay was last built from
		private bool _renderedReady;                 // data-ready state the overlay was built with
		private int _renderedSources;                // LoadedSourceCount the overlay was built with

		private static string CacheDir =>
			Path.Combine(Config.AppDataPath, "ArenaHelper");

		public void OnLoad()
		{
			try
			{
				// Wire fully, THEN publish: _aggregator is volatile and read off this
				// thread, so nothing may observe it before the synergy engine is set.
				var aggregator = BuildAggregator();
				WireSynergy(aggregator);
				_aggregator = aggregator;
				_watcher.OnChoicesChanged += OnChoicesChanged;
				_watcher.OnDeckReview += OnDeckReviewChanged;
				_watcher.OnRunSummary += OnRunSummaryChanged;
				_watcher.OnDraftEnded += OnDraftEnded;
				_choiceWatcher.OnChoicesChanged += OnCardChoiceChanged;
				_choiceWatcher.OnChoicesGone += OnCardChoiceGone;
				_mulliganWatcher.OnMulligan += OnMulliganChanged;
				_mulliganWatcher.OnMulliganGone += OnMulliganGone;

				// Clear watcher + pick/render state so a stale pick can't bleed in.
				ResetDraftState();

				// Created here on HDT's UI thread; all overlay access stays on this thread.
				_overlay = new ArenaOverlayWindow();

				// This plugin owns the arena overlay: suppress HDT's built-in one.
				SuppressNativeArenaOverlay();

				// Warm the data off the UI thread.
				WarmData();

				// Self-update: finish any swap staged last session, then check GitHub off-thread.
				InitSelfUpdate();

				Log.Info("[ArenaHelper] loaded");
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] OnLoad failed: " + ex);
			}
		}

		public void OnUnload()
		{
			// Wrapped like OnLoad: Close() on a torn-down WPF window can throw, and the native-overlay
			// restore below is the user's setting — it must be attempted even then.
			try
			{
				OnUnloadCore();
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] OnUnload failed: " + ex);
			}
		}

		private void OnUnloadCore()
		{
			_watcher.OnChoicesChanged -= OnChoicesChanged;
			_watcher.OnDeckReview -= OnDeckReviewChanged;
			_watcher.OnRunSummary -= OnRunSummaryChanged;
			_watcher.OnDraftEnded -= OnDraftEnded;
			_choiceWatcher.OnChoicesChanged -= OnCardChoiceChanged;
			_choiceWatcher.OnChoicesGone -= OnCardChoiceGone;
			_mulliganWatcher.OnMulligan -= OnMulliganChanged;
			_mulliganWatcher.OnMulliganGone -= OnMulliganGone;
			// Abandon any in-flight update check before the DLL stops being ours to swap.
			try { _updateCts?.Cancel(); }
			catch(Exception ex) { Log.Error("[ArenaHelper] update cancel failed: " + ex.Message); }
			RestoreNativeArenaOverlay();
			_overlay?.Close();
			_overlay = null;
			Log.Info("[ArenaHelper] unloaded");
		}

		public void OnUpdate()
		{
			// HDT disables a plugin after 100 exceptions from OnUpdate; never let a transient
			// per-tick fault (render, HearthMirror, WPF) trip that counter.
			try
			{
				if(!_enabled)
				{
					_overlay?.UpdateVisibility(false);
					return;
				}

				_watcher.Poll(); // fires OnChoicesChanged / OnDeckReview / OnDraftEnded on this thread
				_choiceWatcher.Poll(); // fires OnCardChoiceChanged / OnCardChoiceGone on this thread
				_mulliganWatcher.Poll(); // fires OnMulliganChanged / OnMulliganGone on this thread

				var screen = _activeScreen;
				var want = _dataReady && screen != null;

				// (Re)build the overlay when the screen changes OR when another data source
				// has come online since the last render (the heuristic is loaded instantly,
				// so the first render may predate the win-rate downloads — a bool "ready"
				// latch alone would leave those scores stale forever).
				var loadedSources = _aggregator?.LoadedSourceCount ?? 0;
				var readinessChanged = _dataReady != _renderedReady || loadedSources != _renderedSources;
				if(want && (!ReferenceEquals(screen, _renderedScreen) || readinessChanged))
				{
					_renderedScreen = screen;
					_renderedReady = _dataReady;
					_renderedSources = loadedSources;
					RenderScreen(screen!);
				}

				_overlay?.UpdateVisibility(want);
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] OnUpdate failed: " + ex);
			}
		}

		public void OnButtonPress()
		{
			Log.Info("[ArenaHelper] refresh requested");
			// Mark caches stale rather than deleting them: the rebuilt sources re-download,
			// but if the network is down they fall back to the day-old data (see the sources'
			// stale-cache path) instead of blacking out the session. Per-file: a superseded
			// warm-up may hold one file open mid-write, which must not spare the others.
			try
			{
				var caches = new List<string> { Path.Combine(CacheDir, "hsreplay_arena.json") };
				var staleTime = DateTime.UtcNow - TimeSpan.FromDays(2);
				foreach(var file in caches.Where(File.Exists))
				{
					try { File.SetLastWriteTimeUtc(file, staleTime); }
					catch(Exception ex) { Log.Error($"[ArenaHelper] refresh: could not mark {file} stale: {ex.Message}"); }
				}
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] refresh failed: " + ex);
			}

			var aggregator = BuildAggregator();
			WireSynergy(aggregator);
			_aggregator = aggregator; // publish only once fully wired
			_renderedScreen = null; // force a re-render once fresh data is in
			WarmData();
		}

		// Load/refresh source data OFF the UI thread — HDT calls OnLoad/OnButtonPress on its
		// dispatcher and EnsureLoadedAsync runs a synchronous prefix (cache read + parse +
		// scoring) before its first await. Awaited in a try/catch so a fault is logged rather
		// than dropped as an unobserved task exception. Only sets a flag; OnUpdate renders.
		private void WarmData()
		{
			var aggregator = _aggregator;
			if(aggregator == null)
				return;
			int generation;
			lock(_warmLock)
			{
				generation = ++_warmGeneration; // supersede any warm-up still running (e.g. Refresh)
				_dataReady = false;
			}
			Log.Info("[ArenaHelper] loading source data...");
			Task.Run(async () =>
			{
				// HearthDb (and the network) may not be ready at startup, so retry until the
				// sources have data rather than giving up on the first miss — this is what
				// makes the data load automatically on tracker start without a manual refresh.
				const int maxAttempts = 30;
				for(var attempt = 1; ; attempt++)
				{
					if(generation != _warmGeneration)
						return; // a newer warm-up superseded us; stop touching shared state

					try
					{
						await aggregator.EnsureLoadedAsync().ConfigureAwait(false);
					}
					catch(Exception ex)
					{
						Log.Error("[ArenaHelper] data load failed: " + ex);
					}

					// Partial data beats a blank overlay: render as soon as ANY source has
					// data, while the loop keeps retrying the stragglers in the background
					// (OnUpdate re-renders as LoadedSourceCount grows). Generation check
					// and write happen under one lock: a bare check-then-act would let a
					// superseded loop stamp _dataReady right after a Refresh reset it.
					if(aggregator.LoadedSourceCount > 0)
					{
						lock(_warmLock)
						{
							if(generation == _warmGeneration)
								_dataReady = true;
						}
					}

					if(aggregator.IsLoaded)
					{
						Log.Info($"[ArenaHelper] source data ready (attempt {attempt})");
						break;
					}
					if(attempt >= maxAttempts)
					{
						Log.Info($"[ArenaHelper] source data still incomplete after {attempt} attempts");
						break;
					}
					await Task.Delay(2000).ConfigureAwait(false);
				}
				// Don't claim ready if nothing loaded (total failure) — keep the overlay gate shut.
				lock(_warmLock)
				{
					if(generation == _warmGeneration)
						_dataReady = aggregator.LoadedSourceCount > 0;
				}
			});
		}

		/// <summary>
		/// Attach the synergy engine to an aggregator, WITH the data its dead-card lever needs: the
		/// per-class tribe availability that decides whether a payoff with no members yet is really
		/// dead. One helper for both call sites, so neither can wire the engine and forget its data.
		/// </summary>
		private void WireSynergy(ScoreAggregator aggregator)
		{
			aggregator.SetSynergyEngine(_synergy);
			_synergy.SetTribeAvailability(
				aggregator.Sources.OfType<IClassTribeAvailabilitySource>().FirstOrDefault());
		}

		/// <summary>
		/// The active blend. The win-rate signal carries weight 1.0 against the offline heuristic's
		/// 0.5 — a 2:1 ratio that is measured policy, not taste: the heuristic is a backstop and
		/// REPORT.md argues its authority should if anything go down.
		///
		/// It used to be two sources of 0.5 each, because they measured the SAME quantity and had to
		/// count as ONE consensus rather than two votes. The second feed was withdrawn in 0.1.5 at
		/// its provider's request, so HSReplay carries that 1.0 alone. Note what
		/// moved and what deliberately did NOT: the win-rate/heuristic ratio is unchanged, because
		/// letting the heuristic rise to half the blend would be a scoring change smuggled in as a
		/// dependency removal. What is genuinely lost is the consensus — nothing averages away a
		/// sampling artefact now, and nothing cross-checks a poisoned payload.
		/// </summary>

		private static ScoreAggregator BuildAggregator()
		{
			var sources = new List<IArenaDataSource>
			{
				new HsReplayArenaDataSource(CacheDir, weight: 1.0),
				new HeuristicArenaDataSource(weight: 0.5),
			};
			return new ScoreAggregator(sources);
		}

		public MenuItem MenuItem
		{
			get
			{
				if(_menuItem != null)
					return _menuItem;

				_menuItem = new MenuItem { Header = "Arena Helper" };

				var toggle = new MenuItem { Header = "Enabled", IsCheckable = true, IsChecked = true };
				toggle.Click += (_, __) =>
				{
					_enabled = toggle.IsChecked;
					Log.Info($"[ArenaHelper] enabled = {_enabled}");
					if(_enabled)
					{
						ResetDraftState();            // don't resurrect a stale pick from a previous run
						SuppressNativeArenaOverlay(); // take over from HDT's built-in overlay
					}
					else
					{
						RestoreNativeArenaOverlay();  // hand the arena overlay back to HDT
						_overlay?.UpdateVisibility(false);
					}
				};
				_menuItem.Items.Add(toggle);

				var refresh = new MenuItem { Header = "Refresh data now" };
				refresh.Click += (_, __) => OnButtonPress();
				_menuItem.Items.Add(refresh);

				_menuItem.Items.Add(new Separator());

				// Informational line; enabled (clickable → opens the releases page) only when
				// there's a manual update to fetch or a failure worth investigating. Replays
				// the last status: a check may have completed before the menu was built.
				_updateStatusItem = new MenuItem
				{
					Header = "Updates: " + _updateStatusText,
					IsEnabled = _updatePage != null
				};
				_updateStatusItem.Click += (_, __) =>
				{
					var page = _updatePage;
					if(page == null)
						return;
					try { System.Diagnostics.Process.Start(page); }
					catch(Exception ex) { Log.Error("[ArenaHelper] could not open releases page: " + ex.Message); }
				};
				_menuItem.Items.Add(_updateStatusItem);

				var checkNow = new MenuItem { Header = "Check for updates now" };
				checkNow.Click += (_, __) => CheckForUpdates(manual: true);
				_menuItem.Items.Add(checkNow);

				// The menu can be built before OnLoad runs: read the pref now or the checkbox
				// would show the default instead of the user's saved choice.
				EnsureAutoUpdatePref();
				var auto = new MenuItem { Header = "Auto-update", IsCheckable = true, IsChecked = _autoUpdate };
				auto.Click += (_, __) =>
				{
					_autoUpdate = auto.IsChecked;
					SaveAutoUpdatePref(_autoUpdate);
					Log.Info($"[ArenaHelper] auto-update = {_autoUpdate}");
				};
				_menuItem.Items.Add(auto);

				return _menuItem;
			}
		}

		// ---- self-update ---------------------------------------------------------

		private void InitSelfUpdate()
		{
			try
			{
				EnsureAutoUpdatePref();
				_updater = new SelfUpdater(typeof(ArenaHelperPlugin).Assembly.Location, CacheDir);
				// Cancelled and replaced, NOT disposed: a check from the previous enable cycle may
				// still be inside CheckAndStageAsync, and disposing under it makes
				// token.Register(...) throw ObjectDisposedException — surfaced to the user as a
				// spurious "update check failed" — while its finally clears the guard belonging to
				// this new cycle, allowing two concurrent checks. The garbage collector can have it.
				try { _updateCts?.Cancel(); }
				catch(Exception ex) { Log.Error("[ArenaHelper] update cancel failed: " + ex.Message); }
				_updateCts = new CancellationTokenSource();
				// Apply a download from a previous session NOW, at load, where the process has a
				// whole session ahead of it. Doing the rename when the download finishes risks
				// process death mid-swap (the check starts at load, so downloads often complete
				// seconds before the user closes HDT), and that is the one state with no
				// in-process repair path. The *.dll.old rollback is KEPT (HDT ignores it).
				if(_updater.ApplyPendingUpdate() == UpdateOutcome.Staged)
				{
					_updatePending = true;
					SetUpdateStatus("update applied — restart HDT to run it", null);
					MarkHeaderUpdateReady();
				}
				if(_autoUpdate && _updater.DueForCheck())
					CheckForUpdates(manual: false);
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] self-update init failed: " + ex);
			}
		}

		// Runs the GitHub check off the UI thread; marshals the result back to update the menu.
		private void CheckForUpdates(bool manual)
		{
			var updater = _updater;
			if(updater == null)
				return;
			if(_updatePending)
			{
				// Already staged this session: no second swap (the DLL is renamed away already).
				SetUpdateStatus("update downloaded — applied on the next HDT start", null);
				return;
			}
			// One check at a time: the auto-check on load and a manual "Check now" can
			// otherwise overlap and race the rename dance on the same files.
			if(Interlocked.CompareExchange(ref _updateCheckRunning, 1, 0) != 0)
				return;
			// Repeated "Check now" clicks would walk into GitHub's 60/hr unauthenticated limit and
			// then show "failed" for an hour. One manual check a minute is plenty.
			var since = DateTime.UtcNow - _lastManualCheckUtc;
			if(manual && since < ManualCheckFloor)
			{
				Interlocked.Exchange(ref _updateCheckRunning, 0);
				SetUpdateStatus($"just checked — try again in {(int)Math.Ceiling((ManualCheckFloor - since).TotalSeconds)}s", null);
				return;
			}
			if(manual)
				_lastManualCheckUtc = DateTime.UtcNow;
			if(manual)
				SetUpdateStatus("checking…", null);
			var token = _updateCts?.Token ?? CancellationToken.None;
			Task.Run(async () =>
			{
				try
				{
					var result = await updater.CheckAndStageAsync(Version, token).ConfigureAwait(false);
					if(!token.IsCancellationRequested)
						ApplyUpdateResult(result);
				}
				finally
				{
					Interlocked.Exchange(ref _updateCheckRunning, 0);
				}
			});
		}

		private void ApplyUpdateResult(UpdateCheckResult r)
		{
			switch(r.Outcome)
			{
				case UpdateOutcome.Staged:
					_updatePending = true;
					SetUpdateStatus($"update downloaded (v{r.Latest}) — applied on the next HDT start", null);
					MarkHeaderUpdateReady();
					Log.Info($"[ArenaHelper] update v{r.Latest} downloaded; applies on the next start");
					break;
				case UpdateOutcome.ManualAvailable:
					SetUpdateStatus($"v{r.Latest} available — click to open the download page", r.ReleasesPage);
					Log.Info($"[ArenaHelper] update v{r.Latest} available (manual)");
					break;
				case UpdateOutcome.UpToDate:
					SetUpdateStatus($"up to date (v{Version})", null);
					break;
				default:
					SetUpdateStatus("update check failed — click to open the releases page", r.ReleasesPage);
					break;
			}
		}

		// Menu mutation must happen on the WPF thread; the check completes on a worker thread.
		private void EnsureAutoUpdatePref()
		{
			if(_autoUpdateLoaded)
				return;
			_autoUpdateLoaded = true;
			_autoUpdate = LoadAutoUpdatePref();
		}

		private void SetUpdateStatus(string text, string? page)
		{
			_updateStatusText = text;
			_updatePage = page;
			var item = _updateStatusItem;
			if(item == null)
				return;
			void Apply()
			{
				item.Header = "Updates: " + text;
				item.IsEnabled = page != null; // clickable only when there's a page worth opening
			}
			if(item.Dispatcher.CheckAccess())
				Apply();
			else
				item.Dispatcher.BeginInvoke((Action)Apply);
		}

		private void MarkHeaderUpdateReady()
		{
			var menu = _menuItem;
			if(menu == null)
				return;
			void Apply() => menu.Header = "Arena Helper — update ready";
			if(menu.Dispatcher.CheckAccess())
				Apply();
			else
				menu.Dispatcher.BeginInvoke((Action)Apply);
		}

		private string AutoUpdatePrefFile => Path.Combine(CacheDir, "auto_update.pref");

		private bool LoadAutoUpdatePref()
		{
			try
			{
				if(File.Exists(AutoUpdatePrefFile)
					&& bool.TryParse(File.ReadAllText(AutoUpdatePrefFile).Trim(), out var v))
					return v;
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] auto-update pref read failed: " + ex.Message);
			}
			return true; // default: keep users on the latest fixes unless they opt out
		}

		private void SaveAutoUpdatePref(bool value)
		{
			try
			{
				Directory.CreateDirectory(CacheDir);
				File.WriteAllText(AutoUpdatePrefFile, value ? "true" : "false");
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] auto-update pref write failed: " + ex.Message);
			}
		}

		// Clears the watcher's dedup state and the pick/render state. Run on load AND on
		// re-enable: without it, re-enabling in a later run whose DraftChoices.Version
		// collides with the frozen one would be deduped away and the old pick re-shown.
		private void ResetDraftState()
		{
			_watcher.Reset();
			_choiceWatcher.Reset();
			_mulliganWatcher.Reset();
			_activeScreen = null;
			_renderedScreen = null;
			_renderedReady = false;
			_renderedSources = 0;
		}

		private void OnChoicesChanged(object sender, DraftChoicesEventArgs e)
		{
			// One field, so a pick REPLACES a deck-edit rather than having to remember to clear it.
			_activeScreen = e;
			Log.Info($"[ArenaHelper] choices changed: {e.Offered.Count} offered, " +
				$"{e.DraftedDbfIds.Count} drafted, underground={e.IsUnderground}");
		}

		private void OnRunSummaryChanged(object sender, RunSummaryEventArgs e) => _activeScreen = e;

		/// <summary>
		/// Describes the finished run deck on the screen between matches. Descriptive only — counts the
		/// player can check against their own deck, and no judgement drawn from them, which is why this
		/// needs no validation to be honest.
		/// </summary>
		private void RenderRunSummary(RunSummaryEventArgs e)
		{
			var mechanics = DeckMechanics.Describe(e.DeckDbfIds);
			Log.Info($"[ArenaHelper] run summary: {mechanics.ToLine()}");
			_overlay?.SetRunSummary(mechanics, e.DraftClass.ToString(), e.Wins, e.Losses);
		}

		private void OnDeckReviewChanged(object sender, DeckReviewEventArgs e)
		{
			_activeScreen = e;
			Log.Info($"[ArenaHelper] deck review: {e.Deck.Count} cards, " +
				$"class={e.DraftClass}, underground={e.IsUnderground}");

			// What the deck DOES, in counts. Logged before it is shown anywhere: the numbers are
			// verifiable against the deck on screen, and the overlay row for them needs a live client
			// to place (see AGENTS.md — geometry is never verified offline).
			var expanded = new List<int>();
			foreach(var card in e.Deck)
			{
				for(var i = 0; i < System.Math.Max(1, card.Count); i++)
					expanded.Add(card.DbfId);
			}
			Log.Info($"[ArenaHelper] deck mechanics: {DeckMechanics.Describe(expanded).ToLine()}");
		}

		private void OnDraftEnded(object sender, EventArgs e)
		{
			ClearScreen<DraftChoicesEventArgs>();
			ClearScreen<DeckReviewEventArgs>();
			Log.Info("[ArenaHelper] draft ended");
		}

		/// <summary>
		/// The per-source breakdown of a score, for the log: which feed said what, at which effective
		/// weight, on how many games, plus the synergy nudge. Shared by every render path because the
		/// pick path used to log only the final number — and then "why is this card 54 and that one
		/// 69" could not be answered from the log at all, which is the one place it should be.
		/// </summary>
		private static string DescribeScore(BlendedScore s)
		{
			var parts = s.HasData
				? string.Join(" ", s.Components.Select(c =>
					$"{c.SourceName}={Math.Round(c.NormalizedScore)}(w{c.Weight:0.00}" +
					$"{(c.Games.HasValue ? ",g" + c.Games : "")})"))
				: "no data";
			var syn = Math.Abs(s.SynergyBonus) >= 0.05
				? $" syn={s.SynergyBonus:+0.0;-0.0}{(s.SynergyReason != null ? " '" + s.SynergyReason + "'" : "")}"
				: "";
			return $"[{parts}]{syn}";
		}

		private void OnCardChoiceChanged(object sender, CardChoiceEventArgs e) => _activeScreen = e;

		private void OnCardChoiceGone(object sender, EventArgs e)
			=> ClearScreen<CardChoiceEventArgs>();

		private void OnMulliganChanged(object sender, MulliganEventArgs e)
		{
			_activeScreen = e;
			LogOpponentHeroPower();
		}

		/// <summary>
		/// Reads the opponent's CURRENT hero power and logs how cheaply it can answer a small body.
		///
		/// LOG ONLY, deliberately: whether HDT has the entity populated by the time the mulligan is on
		/// screen cannot be established offline, and no verdict may rest on a read that might be empty.
		/// Once the log confirms it arrives in time, the mulligan's one-health rule can consume it.
		///
		/// Keyed on the hero power CARD, never on the class. A dual-class arena hero does not identify
		/// its hero power, and hero cards replace it mid-game — the question is what THIS button does.
		/// It comes from HDT's own game state because HearthMirror does not carry it: verified across
		/// all 76 <c>IReflection</c> methods, <c>MatchInfo.Player</c> and <c>MulliganState</c>.
		/// </summary>
		private static HearthDb.Card? ReadOpponentHeroPower()
		{
			try
			{
				var entity = Core.Game?.Opponent?.PlayerEntities?
					.FirstOrDefault(x => x != null && x.IsHeroPower && x.IsInPlay);
				if(entity == null || string.IsNullOrEmpty(entity.CardId))
					return null;

				// Resolved through HearthDb rather than HDT's own Card wrapper: the classifier reads the
				// printed text, and HearthDb is the card data this project already trusts everywhere.
				HearthDb.Cards.All.TryGetValue(entity.CardId, out var card);
				return card;
			}
			catch(Exception ex)
			{
				// Fail soft: an unreadable hero power must leave the advice exactly as it was, never
				// throw out of a render path.
				Log.Info($"[ArenaHelper] mulligan: opponent hero power read failed: {ex.Message}");
				return null;
			}
		}

		private static void LogOpponentHeroPower()
		{
			var card = ReadOpponentHeroPower();
			if(card == null)
			{
				Log.Info("[ArenaHelper] mulligan: opponent hero power not readable yet");
				return;
			}

			var (answer, free) = HeroPowerThreat.Classify(card);
			Log.Info($"[ArenaHelper] mulligan: opponent hero power '{card.Name}' -> {answer}, "
				+ $"free damage {free}, kills 1 health={HeroPowerThreat.KillsForFree(card, 1)}, "
				+ $"2 health={HeroPowerThreat.KillsForFree(card, 2)}");
		}

		private void OnMulliganGone(object sender, EventArgs e) => ClearScreen<MulliganEventArgs>();

		/// <summary>
		/// Drop the active screen only if it is still the KIND that just ended. Without the type
		/// check, the draft watcher leaving the DRAFT scene would also wipe a mulligan that the
		/// in-game watcher had just put up — the two watchers run on the same tick and each only
		/// speaks for its own screens.
		/// </summary>
		private void ClearScreen<T>() where T : class
		{
			if(!(_activeScreen is T))
				return;
			_activeScreen = null;
			_renderedScreen = null;
		}

		/// <summary>Render whichever screen is active. One place where the four kinds are named.</summary>
		private void RenderScreen(object screen)
		{
			switch(screen)
			{
				case DraftChoicesEventArgs choices:
					RenderContent(choices);
					break;
				case DeckReviewEventArgs review:
					RenderDeckReview(review);
					break;
				case RunSummaryEventArgs run:
					RenderRunSummary(run);
					break;
				case CardChoiceEventArgs cardChoice:
					RenderCardChoice(cardChoice);
					break;
				case MulliganEventArgs mulligan:
					RenderMulligan(mulligan);
					break;
			}
		}

		/// <summary>
		/// Snapshot the board from HDT's own game state. Wrapped in try/catch and defaulting to
		/// "unknown" — a board we cannot read must make every rule silent rather than print a
		/// confident "needs 7 mana, you have 0" over a game that is perfectly playable.
		/// </summary>
		private static GameStateSnapshot ReadGameState()
		{
			try
			{
				var game = Core.Game;
				var entity = game?.PlayerEntity;
				if(game == null || entity == null)
					return GameStateSnapshot.Unknown;

				// Crystals minus what this turn already spent, plus the temporary mana a coin or a
				// ritual added — the same three tags the client itself displays.
				var mana = entity.GetTag(HearthDb.Enums.GameTag.RESOURCES)
					- entity.GetTag(HearthDb.Enums.GameTag.RESOURCES_USED)
					+ entity.GetTag(HearthDb.Enums.GameTag.TEMP_RESOURCES);

				return new GameStateSnapshot(
					availableMana: Math.Max(0, mana),
					handCount: game.PlayerHandCount,
					maxHandSize: game.Player?.MaxHandSize ?? 0,
					friendlyMinions: game.PlayerMinionCount,
					maxBoardSize: 0);
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] could not read game state: " + ex.Message);
				return GameStateSnapshot.Unknown;
			}
		}

		// Runs on the UI thread. Shows each opening-hand card's keep record for the drafted class.
		// Unlike every other screen this is NOT the blended score: it is a single source's
		// percentage points, so it is rendered separately and labelled as an estimate.
		private void RenderMulligan(MulliganEventArgs e)
		{
			var aggregator = _aggregator;
			if(aggregator == null || _overlay == null)
				return;

			// The advisor asks for a card's arena score rather than computing one: how good a card
			// is has already been measured from win-rates, and duplicating that judgement here
			// would be a second opinion nobody validated.
			_mulliganAdvisor.SetScoreSource(dbfId =>
			{
				var blended = aggregator.Score(dbfId, e.DeckDbfIds, e.DeckClass);
				// A low-confidence score is worse than none here: the advisor uses it to decide
				// whether an expensive card is a bomb worth holding, and a thinly-sampled legendary
				// scores mid-table for lack of games rather than for lack of power. Null makes the
				// advisor abstain, which is the honest verdict on a card nobody has measured.
				return blended.HasData && !blended.IsLowConfidence ? blended.Value : (double?)null;
			});

			// Read fresh per render, never cached: the hero power belongs to THIS opponent, and a stale
			// one from the previous match would answer the wrong question with full confidence.
			var verdicts = _mulliganAdvisor.Evaluate(e.HandDbfIds, e.DeckDbfIds, e.DeckClass, e.OnCoin,
				ReadOpponentHeroPower());
			// The advisor answers all-or-nothing (verdicts are laid out by index), so a short list
			// means it declined to speak about this hand at all.
			if(verdicts.Count != e.HandDbfIds.Count)
				return;

			var entries = new List<MulliganOverlayEntry>();
			for(var i = 0; i < e.HandDbfIds.Count; i++)
			{
				var dbCard = HearthDb.Cards.GetFromDbfId(e.HandDbfIds[i]);
				var name = dbCard != null ? ResolveName(dbCard.Id) : e.HandDbfIds[i].ToString();
				entries.Add(new MulliganOverlayEntry(name, verdicts[i]));
			}

			_overlay.SetMulligan(entries);
			Log.Info($"[ArenaHelper] rendered mulligan: {entries.Count} cards, class={e.DeckClass}, " +
				$"coin={e.OnCoin}, calls={entries.Count(x => x.Verdict.Verdict != MulliganVerdict.Situational)}");
		}

		// Runs on the UI thread. Scores an in-game card choice (Discover) with the SAME engine the
		// draft uses, in the run deck's class context and with the deck as synergy context. The board
		// state is reported in WORDS beside the score and never folded into it: what the rules make
		// objective is whether a card is castable or would be lost, not how many points that is worth.
		private void RenderCardChoice(CardChoiceEventArgs e)
		{
			if(_aggregator == null || _overlay == null)
				return;

			var state = ReadGameState();
			// Decided once for the whole choice, not per card: the mana line is worth showing only
			// when it separates the options.
			var manaSeparates = GameStateFacts.ManaSeparates(
				e.OfferedDbfIds.Select(dbf => HearthDb.Cards.GetFromDbfId(dbf)), state);

			var entries = new List<OverlayEntry>();
			foreach(var dbfId in e.OfferedDbfIds)
			{
				var score = _aggregator.Score(dbfId, e.DeckDbfIds, e.DeckClass);
				var dbCard = HearthDb.Cards.GetFromDbfId(dbfId);
				var name = dbCard != null ? ResolveName(dbCard.Id) : dbfId.ToString();
				var fact = GameStateFacts.Describe(dbCard, state, manaSeparates);
				entries.Add(new OverlayEntry(name, score, cost: -1, note: fact));
				Log.Info($"[ArenaHelper] choice {name} dbf={dbfId} " +
					$"score={(score.HasData ? Math.Round(score.Value).ToString() : "-")} {DescribeScore(score)}" +
					$"{(fact != null ? $" board='{fact}'" : "")}");
			}

			_overlay.SetEntries(entries, OverlayLayout.InGameChoice);
			Log.Info($"[ArenaHelper] rendered {entries.Count} in-game choices class={e.DeckClass}");
		}

		// Runs on the UI thread (called from OnUpdate). Builds the plaques for the current
		// pick; at the hero pick the label is the class name so the score is unambiguous.
		private void RenderContent(DraftChoicesEventArgs e)
		{
			if(_aggregator == null || _overlay == null)
				return;

			var isHeroPick = e.Offered.Count > 0 && e.Offered.All(o => IsHeroCard(o.CardId));
			var entries = new List<OverlayEntry>();
			foreach(var option in e.Offered)
			{
				var isGroup = option.PackageDbfIds.Count > 0;
				var score = isGroup
					? ScoreGroup(option, e.DraftedDbfIds, e.DraftClass)
					: _aggregator.Score(option.DbfId, e.DraftedDbfIds, e.DraftClass);
				var label = isHeroPick
					? HeroClassName(option.CardId)
					: isGroup
						? $"{ResolveName(option.CardId)} +{option.PackageDbfIds.Count}"
						: ResolveName(option.CardId);
				// At the hero pick, also show the class's win-rate in real percentage points: "71"
				// on a normalized scale is not interpretable, "53%" is. Estimate, so labelled.
				var note = isHeroPick ? ClassWinRateNote(option.CardId) : null;
				entries.Add(new OverlayEntry(label, score, cost: -1, note: note));
				Log.Info($"[ArenaHelper] option {option.CardId} dbf={option.DbfId} pkg={option.PackageDbfIds.Count} " +
					$"label='{label}' score={(score.HasData ? Math.Round(score.Value).ToString() : "-")} " +
					DescribeScore(score));
			}

			_overlay.IsUnderground = e.IsUnderground;
			_overlay.SetEntries(entries, isHeroPick ? OverlayLayout.HeroPick : OverlayLayout.CardDraft);
			Log.Info($"[ArenaHelper] rendered {entries.Count} options heroPick={isHeroPick}");
		}

		/// <summary>
		/// Minimum cards to keep at each mana cost (index = cost, last entry covers that cost and
		/// up) before a card there may be suggested as a cut. Guards the early game and the
		/// mid-curve that the raw win-rate ranking would otherwise eat first.
		/// </summary>
		private static readonly int[] CurveFloors = { 0, 2, 5, 4, 3, 2, 1, 0 };

		// Runs on the UI thread. Scores every card in the deck being edited and shows them
		// ranked weakest-first, so the redraft discard decision gets the same per-card guidance
		// a pick does. Each card is scored in the context of the rest of the deck, so a dead
		// payoff (e.g. a dragon card with no dragons) sinks to a cut candidate.
		private void RenderDeckReview(DeckReviewEventArgs e)
		{
			if(_aggregator == null || _overlay == null)
				return;

			var deckContext = e.Deck.SelectMany(c => Enumerable.Repeat(c.DbfId, c.Count)).ToList();
			var entries = new List<OverlayEntry>();
			foreach(var card in e.Deck)
			{
				var score = _aggregator.Score(card.DbfId, deckContext, e.DraftClass);
				var dbCard = HearthDb.Cards.GetFromDbfId(card.DbfId);
				var name = dbCard != null ? ResolveName(dbCard.Id) : card.DbfId.ToString();
				var cost = dbCard?.Cost ?? -1;
				var label = card.Count > 1 ? $"{name} x{card.Count}" : name;
				entries.Add(new OverlayEntry(label, score, cost));
			}

			var fullRanked = entries
				.OrderBy(x => x.Score.HasData ? x.Score.Value : double.PositiveInfinity)
				.ToList();

			// Diagnostic: log EVERY card's score with its per-source breakdown and synergy, so
			// "why is card X scored Y / not in the cut list" is answerable from the log. The
			// score is win-rate-driven; synergy is bounded to a few points.
			foreach(var x in fullRanked)
			{
				var s = x.Score;
				Log.Info($"[ArenaHelper]   deck-card {x.Label}: " +
					$"{(s.HasData ? Math.Round(s.Value).ToString() : "-")} {DescribeScore(s)}");
			}

			// A raw ascending sort is NOT safe advice. Drawn win-rate rises with mana cost (measured
			// on the live feeds: ~51 at one mana, ~55 at eight), so the bottom of the ranking is
			// systematically the cheap cards — and conditional removal ranks near the floor because
			// you draw it in games you are already losing. Cutting five off the bottom would gut the
			// early game and the removal that holds an arena deck together.
			//
			// So the candidate list is CONSTRAINED: a card is only offered for the cut while its
			// cost bucket still has cards to spare. Cards with no data sink to the bottom of the
			// sort but are unknown value, not recommended cuts.
			var remaining = new Dictionary<int, int>();
			foreach(var x in fullRanked)
			{
				if(x.Cost < 0)
					continue;
				var bucket = Math.Min(CurveFloors.Length - 1, x.Cost);
				remaining[bucket] = remaining.TryGetValue(bucket, out var n) ? n + 1 : 1;
			}

			var ranked = new List<OverlayEntry>();
			foreach(var x in fullRanked)
			{
				if(ranked.Count >= Math.Max(1, e.SuggestCount))
					break;
				var bucket = x.Cost < 0 ? -1 : Math.Min(CurveFloors.Length - 1, x.Cost);
				if(bucket >= 0)
				{
					if(remaining[bucket] <= CurveFloors[bucket])
						continue; // cutting here would break the curve floor
					remaining[bucket]--;
				}
				ranked.Add(x);
			}
			// If the floors left nothing to suggest, fall back to the plain ranking rather than
			// showing an empty panel — the player still has to discard something.
			if(ranked.Count == 0)
				ranked = fullRanked.Take(Math.Max(1, e.SuggestCount)).ToList();

			_overlay.IsUnderground = e.IsUnderground;
			_overlay.SetDeckReview(ranked, fullRanked);
			Log.Info($"[ArenaHelper] deck-review rendered {ranked.Count} of {fullRanked.Count} cards");
		}

		// Underground / normal-arena "legendary group": scored by LegendaryGroupScore, which is
		// static and testable — the tilt below is a scoring rule and the provenance flag has been a
		// bug three times, so both need tests that a private plugin method cannot have.
		private BlendedScore ScoreGroup(DraftOption option, IReadOnlyCollection<int> draftedDbfIds,
			HearthDb.Enums.CardClass draftClass)
			=> _aggregator == null
				? BlendedScore.Empty
				: LegendaryGroupScore.Score(_aggregator, option.DbfId, option.PackageDbfIds,
					draftedDbfIds, draftClass);

		/// <summary>
		/// The offered class's estimated arena win-rate, as a line under the hero plaque. Consensus
		/// of whichever win-rate sources have loaded — the same "two independent feeds, one signal"
		/// rule the card scores use — and marked "est." because it is derived from per-card tallies
		/// and re-centred, not a published figure (see <see cref="ScoreMath.RecentreClassWinRates"/>).
		/// Null when no source can answer, so the overlay simply shows nothing.
		/// </summary>
		private string? ClassWinRateNote(string cardId)
		{
			var aggregator = _aggregator;
			if(aggregator == null || !HearthDb.Cards.All.TryGetValue(cardId, out var card))
				return null;

			double sum = 0;
			var n = 0;
			foreach(var source in aggregator.Sources)
			{
				if(!(source is IClassWinRateSource winRates))
					continue;
				var rates = winRates.ClassWinRates;
				if(rates != null && rates.TryGetValue(card.Class, out var rate))
				{
					sum += rate;
					n++;
				}
			}
			return n == 0 ? null : $"~{sum / n:0.#}% win rate (est.)";
		}

		private static bool IsHeroCard(string cardId)
			=> cardId.StartsWith("HERO", StringComparison.OrdinalIgnoreCase);

		// The offered class name (e.g. "Priest") for a hero-skin card id, for the label.
		private static string HeroClassName(string cardId)
		{
			try
			{
				if(HearthDb.Cards.All.TryGetValue(cardId, out var card))
				{
					var cls = card.Class.ToString();
					if(cls.Length > 0)
						return char.ToUpperInvariant(cls[0]) + cls.Substring(1).ToLowerInvariant();
				}
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] hero class lookup failed: " + ex);
			}
			return ResolveName(cardId);
		}

		private static string ResolveName(string cardId)
		{
			try
			{
				var name = new HdtCard(cardId).LocalizedName;
				return string.IsNullOrEmpty(name) ? cardId : name!;
			}
			catch
			{
				return cardId;
			}
		}

		// This plugin replaces HDT's built-in arena overlay across the draft AND the hero pick
		// — both are gated by Config.EnableArenasmithOverlay, so turning it off suppresses the
		// native overlay while we're active. HDT persists Config (Config.Save) BEFORE it
		// unloads plugins on shutdown, so our OnUnload restore runs too late and the false
		// would be persisted — and next launch we'd read that false as "the user's value" and
		// never restore it. So we capture the user's real preference to our own file the first
		// time we ever suppress, and always restore from THAT (never the possibly-stomped
		// live Config value).
		private string NativePrefFile => Path.Combine(CacheDir, "native_overlay.pref");

		private void SuppressNativeArenaOverlay()
		{
			try
			{
				if(!_nativeOverlaySaved)
				{
					_nativeOverlayPrev = LoadOrCaptureNativePref();
					_nativeOverlaySaved = true;
				}
				Config.Instance.EnableArenasmithOverlay = false;
				Log.Info("[ArenaHelper] native arena overlay suppressed");
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] could not suppress native overlay: " + ex);
			}
		}

		private void RestoreNativeArenaOverlay()
		{
			try
			{
				if(_nativeOverlaySaved)
				{
					Config.Instance.EnableArenasmithOverlay = _nativeOverlayPrev;
					Log.Info("[ArenaHelper] native arena overlay restored");
				}
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] could not restore native overlay: " + ex);
			}
		}

		// The user's pre-suppression EnableArenasmithOverlay value: from our file if captured
		// before, else capture the current (not-yet-stomped) live value once.
		private bool LoadOrCaptureNativePref()
		{
			try
			{
				if(File.Exists(NativePrefFile) && bool.TryParse(File.ReadAllText(NativePrefFile).Trim(), out var saved))
					return saved;

				var current = Config.Instance.EnableArenasmithOverlay;
				Directory.CreateDirectory(CacheDir);
				File.WriteAllText(NativePrefFile, current ? "true" : "false");
				return current;
			}
			catch(Exception ex)
			{
				Log.Error("[ArenaHelper] native overlay pref read/write failed: " + ex);
				return true; // safest default: assume the user had the native overlay on
			}
		}
	}
}
