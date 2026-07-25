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
		private DraftChoicesEventArgs? _lastChoices; // current pick (null between picks)
		private DeckReviewEventArgs? _lastReview;    // current redraft deck-edit (mutually exclusive with a pick)
		private DraftChoicesEventArgs? _renderedChoices; // what the overlay currently shows
		private DeckReviewEventArgs? _renderedReview;    // deck-review the overlay currently shows
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
				_watcher.OnDraftEnded += OnDraftEnded;

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
			_watcher.OnChoicesChanged -= OnChoicesChanged;
			_watcher.OnDeckReview -= OnDeckReviewChanged;
			_watcher.OnDraftEnded -= OnDraftEnded;
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

				var choices = _lastChoices;
				var review = _lastReview; // mutually exclusive with a pick (the watcher clears the other)
				var want = _dataReady && (choices != null || review != null);

				// (Re)build the overlay when the pick/deck changes OR when another data source
				// has come online since the last render (the heuristic is loaded instantly,
				// so the first render may predate the win-rate downloads — a bool "ready"
				// latch alone would leave those scores stale forever).
				var loadedSources = _aggregator?.LoadedSourceCount ?? 0;
				var readinessChanged = _dataReady != _renderedReady || loadedSources != _renderedSources;
				if(want && choices != null && (!ReferenceEquals(choices, _renderedChoices) || readinessChanged))
				{
					_renderedChoices = choices;
					_renderedReview = null;
					_renderedReady = _dataReady;
					_renderedSources = loadedSources;
					RenderContent(choices);
				}
				else if(want && review != null && (!ReferenceEquals(review, _renderedReview) || readinessChanged))
				{
					_renderedReview = review;
					_renderedChoices = null;
					_renderedReady = _dataReady;
					_renderedSources = loadedSources;
					RenderDeckReview(review);
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
				caches.AddRange(Directory.GetFiles(CacheDir, "firestone_*.json"));
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
			_renderedChoices = null; // force a re-render once fresh data is in
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
		/// The active blend of data sources. HSReplay and Firestone measure the SAME
		/// quantity (drawn win-rate), so they are one consensus signal at a combined
		/// weight of 1.0 (0.5 each), not two independent votes — otherwise adding the
		/// second source would silently demote the heuristic from 1/3 to 1/5 of the
		/// blend. Where both cover a card the consensus averages their sampling noise;
		/// if either endpoint goes dark the survivor carries the win-rate signal (then
		/// weighted evenly against the heuristic — the price of fixed weights). The
		/// offline <see cref="HeuristicArenaDataSource"/> backstops cards neither covers.
		/// </summary>
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

		private static ScoreAggregator BuildAggregator()
		{
			var sources = new List<IArenaDataSource>
			{
				new HsReplayArenaDataSource(CacheDir, weight: 0.5),
				new FirestoneArenaDataSource(CacheDir, weight: 0.5),
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
			_lastChoices = null;
			_lastReview = null;
			_renderedChoices = null;
			_renderedReview = null;
			_renderedReady = false;
			_renderedSources = 0;
		}

		private void OnChoicesChanged(object sender, DraftChoicesEventArgs e)
		{
			_lastChoices = e;
			_lastReview = null; // a pick and a deck-edit are different screens; never show both
			Log.Info($"[ArenaHelper] choices changed: {e.Offered.Count} offered, " +
				$"{e.DraftedDbfIds.Count} drafted, underground={e.IsUnderground}");
		}

		private void OnDeckReviewChanged(object sender, DeckReviewEventArgs e)
		{
			_lastReview = e;
			_lastChoices = null;
			Log.Info($"[ArenaHelper] deck review: {e.Deck.Count} cards, " +
				$"class={e.DraftClass}, underground={e.IsUnderground}");
		}

		private void OnDraftEnded(object sender, EventArgs e)
		{
			_lastChoices = null;
			_lastReview = null;
			Log.Info("[ArenaHelper] draft ended");
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
					$"label='{label}' hasData={score.HasData} score={(score.HasData ? Math.Round(score.Value).ToString() : "-")}");
			}

			_overlay.IsUnderground = e.IsUnderground;
			_overlay.SetEntries(entries, isHeroPick);
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
				var parts = s.HasData
					? string.Join(" ", s.Components.Select(c =>
						$"{c.SourceName}={Math.Round(c.NormalizedScore)}(w{c.Weight:0.00}{(c.Games.HasValue ? ",g" + c.Games : "")})"))
					: "no data";
				var syn = Math.Abs(s.SynergyBonus) >= 0.05
					? $" syn={s.SynergyBonus:+0.0;-0.0}{(s.SynergyReason != null ? " '" + s.SynergyReason + "'" : "")}"
					: "";
				Log.Info($"[ArenaHelper]   deck-card {x.Label}: {(s.HasData ? Math.Round(s.Value).ToString() : "-")} " +
					$"[{parts}]{syn}");
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

		// Underground "legendary group": the pick's value is the legendary PLUS its 3-card
		// package, so score all four and average the ones we have data for (the average card
		// quality you actually add to the deck).
		/// <summary>
		/// How far a legendary group's score leans toward its best card rather than its mean. The
		/// bomb is the part you cannot get later; the filler is the part you can.
		/// </summary>
		private const double BestCardTilt = 0.35;

		private BlendedScore ScoreGroup(DraftOption option, IReadOnlyCollection<int> draftedDbfIds,
			HearthDb.Enums.CardClass draftClass)
		{
			if(_aggregator == null)
				return BlendedScore.Empty;

			var ids = new List<int> { option.DbfId };
			ids.AddRange(option.PackageDbfIds);

			// Score each card against the drafted deck PLUS the rest of its own group. The package
			// is arriving together, so a tribal bundle (three Dragons behind a Dragon legendary)
			// makes its own payoffs live — synergy the engine already computes but never saw here,
			// because each card used to be scored against the drafted deck alone.
			var values = new List<double>();
			int? maxGames = null;
			foreach(var id in ids)
			{
				var context = new List<int>(draftedDbfIds);
				foreach(var other in ids)
				{
					if(other != id)
						context.Add(other);
				}
				var s = _aggregator.Score(id, context, draftClass);
				if(!s.HasData)
					continue;
				values.Add(s.Value);
				// Carry the group's best sample so the confidence flag reflects the
				// underlying data, not the synthesized "group avg" component.
				if(s.MaxGames.HasValue && s.MaxGames.Value > (maxGames ?? -1))
					maxGames = s.MaxGames;
			}
			if(values.Count == 0)
				return BlendedScore.Empty;

			// A mean answers "average card quality added", which is the right quantity but the wrong
			// decision criterion for THIS pick: the first pick is the only guaranteed legendary of
			// the run, while ~29 later picks can supply average bodies. A plain mean therefore
			// prefers four solid cards over a bomb plus filler, which inverts how the choice
			// actually plays. Tilt toward the best card in the group without ignoring the rest.
			var mean = values.Average();
			var best = values.Max();
			var score = mean + BestCardTilt * (best - mean);
			var components = new List<ScoreComponent>
			{
				new ScoreComponent($"group {values.Count}/{ids.Count} (avg {mean:0.#}, best {best:0.#})",
					score, 1.0, maxGames)
			};
			return new BlendedScore(score, components, 0);
		}

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
