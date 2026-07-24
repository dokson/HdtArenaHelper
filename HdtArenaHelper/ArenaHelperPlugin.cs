using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
		private bool _nativeOverlayPrev = true; // user's EnableArenasmithOverlay, restored on unload/disable
		private bool _nativeOverlaySaved;

		private volatile bool _dataReady;            // sources have finished their load attempt
		private volatile int _warmGeneration;        // bumped per WarmData; a superseded loop stops
		// Pairs the generation check with the _dataReady write: without it a superseded
		// loop could pass the check, get preempted by a Refresh (bump + reset), then
		// stamp _dataReady with the OLD aggregator's verdict.
		private readonly object _warmLock = new object();
		private DraftChoicesEventArgs? _lastChoices; // current pick (null between picks)
		private DraftChoicesEventArgs? _renderedChoices; // what the overlay currently shows
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
				aggregator.SetSynergyEngine(_synergy);
				_aggregator = aggregator;

				_watcher.OnChoicesChanged += OnChoicesChanged;
				_watcher.OnDraftEnded += OnDraftEnded;

				// Clear watcher + pick/render state so a stale pick can't bleed in.
				ResetDraftState();

				// Created here on HDT's UI thread; all overlay access stays on this thread.
				_overlay = new ArenaOverlayWindow();

				// This plugin owns the arena overlay: suppress HDT's built-in one.
				SuppressNativeArenaOverlay();

				// Warm the data off the UI thread.
				WarmData();

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
			_watcher.OnDraftEnded -= OnDraftEnded;
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

				_watcher.Poll(); // fires OnChoicesChanged / OnDraftEnded synchronously on this thread

				var choices = _lastChoices;
				var want = choices != null && _dataReady;

				// (Re)build the plaques when the pick changes OR when another data source
				// has come online since the last render (the heuristic is loaded instantly,
				// so the first render of a pick may predate the win-rate downloads — a
				// bool "ready" latch alone would leave those scores stale forever).
				var loadedSources = _aggregator?.LoadedSourceCount ?? 0;
				if(want && (!ReferenceEquals(choices, _renderedChoices)
					|| _dataReady != _renderedReady
					|| loadedSources != _renderedSources))
				{
					_renderedChoices = choices;
					_renderedReady = _dataReady;
					_renderedSources = loadedSources;
					RenderContent(choices!);
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
			aggregator.SetSynergyEngine(_synergy);
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

				return _menuItem;
			}
		}

		// Clears the watcher's dedup state and the pick/render state. Run on load AND on
		// re-enable: without it, re-enabling in a later run whose DraftChoices.Version
		// collides with the frozen one would be deduped away and the old pick re-shown.
		private void ResetDraftState()
		{
			_watcher.Reset();
			_lastChoices = null;
			_renderedChoices = null;
			_renderedReady = false;
			_renderedSources = 0;
		}

		private void OnChoicesChanged(object sender, DraftChoicesEventArgs e)
		{
			_lastChoices = e;
			Log.Info($"[ArenaHelper] choices changed: {e.Offered.Count} offered, " +
				$"{e.DraftedDbfIds.Count} drafted, underground={e.IsUnderground}");
		}

		private void OnDraftEnded(object sender, EventArgs e)
		{
			_lastChoices = null;
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
				entries.Add(new OverlayEntry(label, score));
				Log.Info($"[ArenaHelper] option {option.CardId} dbf={option.DbfId} pkg={option.PackageDbfIds.Count} " +
					$"label='{label}' hasData={score.HasData} score={(score.HasData ? Math.Round(score.Value).ToString() : "-")}");
			}

			_overlay.IsUnderground = e.IsUnderground;
			_overlay.SetEntries(entries, isHeroPick);
			Log.Info($"[ArenaHelper] rendered {entries.Count} options heroPick={isHeroPick}");
		}

		// Underground "legendary group": the pick's value is the legendary PLUS its 3-card
		// package, so score all four and average the ones we have data for (the average card
		// quality you actually add to the deck).
		private BlendedScore ScoreGroup(DraftOption option, IReadOnlyCollection<int> draftedDbfIds,
			HearthDb.Enums.CardClass draftClass)
		{
			if(_aggregator == null)
				return BlendedScore.Empty;

			var ids = new List<int> { option.DbfId };
			ids.AddRange(option.PackageDbfIds);

			var values = new List<double>();
			int? maxGames = null;
			foreach(var id in ids)
			{
				var s = _aggregator.Score(id, draftedDbfIds, draftClass);
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

			var mean = values.Average();
			var components = new List<ScoreComponent>
			{
				new ScoreComponent($"group avg {values.Count}/{ids.Count}", mean, 1.0, maxGames)
			};
			return new BlendedScore(mean, components, 0);
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
