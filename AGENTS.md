# AGENTS.md — HDT Arena Helper

Guidance for AI agents and human contributors. Read this before making changes.

## What this is

An open-source **Arena draft-helper plugin for [Hearthstone Deck Tracker](https://hsdecktracker.net/)**
(HDT). During an Arena / Underground Arena draft it reads the three offered cards,
computes a **single blended 0–100 score** for each, and shows them in an overlay.

**Status:** data pipeline, draft detection, multi-source blend and overlay are in place and
**live-verified on a real HDT client**. The overlay hosts HDT's native `ArenaPlaque`, scales
with the client (a `Viewbox` design-space, so resize/DPI are automatic), hides when HS is
minimised, and covers the card draft, the hero/class pick, and the Underground "legendary
group" pick (cumulative scoring). The synergy engine is designed but not yet implemented (a
`NullSynergyEngine` is wired in).

## Data sources & ethics

The plugin uses **only free, public data** and must never scrape a paywalled service or
bundle/redistribute anyone's proprietary tier data.

| Source | What | Status |
|---|---|---|
| HSReplay arena `api/v1/arena/card_stats/free/` | Real arena win-rate / popularity per card + class tier list | ✅ used (primary) |
| HearthDb (bundled with HDT) | Card metadata for the offline heuristic + id resolution | ✅ used, offline |
| Firestone public arena CDN | Real arena win-rate per class | ✅ used offline in `training/` (weight fitting); ⭐ planned as runtime source |

The HSReplay endpoint **403s .NET's HTTP stack**: Cloudflare fingerprints the TLS
ClientHello, and a browser User-Agent alone is NOT enough (verified — `WebClient` and
`HttpClient` both fail; `curl` is let through). So the runtime fetch **shells out to `curl`**
(bundled in `%SystemRoot%\System32` on Windows 10 1803+), with a fail-soft `WebClient`
fallback. `--compressed` keeps it ~100 KB on the wire. Cache downloads with a 1-day TTL; do
not hammer. See `HsReplayArenaDataSource.DownloadWithCurlAsync`.

## Scoring model

```
finalScore(card) = weightedMean( each source's normalized 0–100 score )  +  synergyBonus(card, draftedDeck)
```

- **`IArenaDataSource`** — a rating provider. Each normalizes its metric to 0–100 so
  heterogeneous signals (win-rate %, heuristic points) can be merged.
  - `HsReplayArenaDataSource` — real arena win-rate, the primary signal. Pipeline:
    `drawn_win_rate` (less deck-confounded than included win-rate) → empirical-Bayes
    shrinkage toward the global mean (low-sample cards regress; no hard games cutoff) →
    logistic anchored at the robust **median** (MAD scale), so the median card maps to 50
    and outliers can't rescale it. Per-class buckets get the same treatment into a
    **class tier list** (unweighted shrunk mean, *not* games-weighted) used to score the
    `HERO_*` skins at the hero pick, so the overlay doubles as a class picker.
  - `HeuristicArenaDataSource` — offline base value from card metadata. The weights are
    **fit by ridge regression against real win-rates** (HSReplay + Firestone, win-rate
    centered per class; see `training/`), not hand-tuned: hand-tuned keyword bonuses
    validated *worse* than the vanilla stat curve alone. Still a weak signal in absolute
    terms (out-of-fold Spearman ~0.27, ~55% of the two-source agreement ceiling), so it
    must not dilute a solid win-rate — it's a backstop for uncovered cards.
    The weights are the SINGLE SOURCE OF TRUTH: fit offline (see `training/`), serialized
    to `arena_weights.json`, embedded into the DLL and loaded at runtime — no coefficients
    are hardcoded, and feature extraction is shared with training so the two can't drift.
    (Train offline / infer online: the plugin *applies* the weights per card at runtime; it
    never re-fits them.) **Re-fit each rotation/patch** by re-running the pipeline; they
    describe the current card pool, not universal keyword values.
- **`ScoreAggregator`** — weighted mean over sources that have data + the synergy bonus;
  returns a per-source breakdown. Sources share a centre (median card → 50 in both), but
  their *slopes* differ: HSReplay's logistic is steeper (~+35 pts per robust-SD) than the
  heuristic (~+15), so on disagreement the real win-rate dominates even at equal weight —
  intended, since the heuristic is a weak backstop. If the heuristic improves, revisit its
  scale, not just its weight.
- **`ISynergyEngine`** — deck-context: see below.
- **Legendary groups (Underground first pick).** Each option is a legendary + a 3-card
  package. `DraftWatcher` reads `DraftChoices.Packages` (a `List<List<Card>>` index-aligned
  to `Choices`) into `DraftOption.PackageDbfIds`, and the shown score is the **mean of the
  four cards'** scores that have data — the baseline "average card quality you add". The
  intra-group and deck synergy belongs in `ISynergyEngine` (deferred with it). See
  `ArenaHelperPlugin.ScoreGroup`.

## Synergy engine (design)

During a draft, a card's value depends on what you've already drafted. The engine turns
the drafted deck into a **+/- bonus** folded into `finalScore`, computed from objective
card metadata (mechanics, tribes, spell schools, stat curve):

- **Synergy → positive bonus** (a tribal payoff when you already have that tribe, a
  spell-damage minion when you have burn, a curve gap filled).
- **Anti-synergy → negative bonus** (over-loading one part of the curve with no payoff).

The raw win-rate sources capture *average* value; synergy expresses *this deck's* context.
See `ISynergyEngine.GetSynergyBonus`.

## Architecture (file map)

| File | Responsibility |
|---|---|
| `ArenaHelperPlugin.cs` | `IPlugin` entry point; wires sources, warms data off-thread with retry, drives all overlay render/visibility from `OnUpdate` (wrapped in try/catch), suppresses/restores HDT's native overlay via a persisted pref file |
| `DraftWatcher.cs` | Reads offered cards + `Packages` via HearthMirror (`GetArenaDraftChoicesV3`), dedup by `Version`, throttled to 500ms, cardId→dbfId; `Reset()` on (re)enable |
| `IArenaDataSource.cs` / `ScoreAggregator.cs` | Multi-source blend → `BlendedScore`; `IsLoaded` gates the overlay |
| `HsReplayArenaDataSource.cs` | Real arena win-rate source; curl fetch, cache-then-download, gated on HearthDb being ready |
| `HeuristicArenaDataSource.cs` | Offline heuristic base value |
| `PlaqueTier.cs` | Pure 0-100 score → 1-5 plaque tier map (WPF-free, unit-tested) |
| `ISynergyEngine.cs` | Synergy contract (`NullSynergyEngine` placeholder) |
| `ArenaOverlayWindow.cs` | Borderless click-through overlay hosting HDT's native `ArenaPlaque` (hand-drawn fallback) in a 4:3 `Viewbox` design-space; class/name label under each plaque; poll-driven show/hide |
| `HdtArenaHelper.Tests/` | xUnit tests: aggregator math, heuristic golden scores (verified against the training tool), HSReplay parsing/tier list via synthetic cache, `PlaqueTier`/`DraftWatcher.ToDbfId` (offline) |
| `HdtArenaHelper.Training/` | C# console tool that fits the heuristic weights → `arena_weights.json` (embedded into the plugin) + analysis (`REPORT.md`). Reuses the plugin's `BuildFeatures` (shared train/inference features); ridge via Math.NET Numerics. Re-run per patch: `dotnet run --project HdtArenaHelper.Training`. |

## Native overlay reuse

HDT's arena overlay lives in `Controls/Overlay/Arena/` and is **public** (verified on
upstream master), so `ArenaOverlayWindow` hosts HDT's own plaque for a pixel-native look
across the card draft AND the hero/class pick, with the hand-drawn plaque as a fallback:
- **`ArenaPlaque` (UserControl) + `ArenaPlaqueViewModel(string score, int level, int seed,
  bool isUnderground[, Thickness margin])`** — the score plaque, built entirely from our
  own data (no HDT API types). `score` is the display string (we pass the 0-100 blend
  rounded, matching HDT's own integer display for scores ≥10); `level` is the 1-5 flame/bolt
  tier (0 = a "loading" state we don't use) — we map it from the score in
  `PlaqueTier.FromScore`; `seed` just randomises flame angles; `isUnderground`
  picks the red/gold vs blue theme (threaded from `DraftWatcher`'s `IsUnderground`).
- `ArenaPickSingle{Card,Hero,HeroPower,DualClassHero}Option` — richer per-option panels,
  but their view-models take HDT API-response types (`ArenaCardPickApiResponse.CardStatsEntry`,
  `ArenaHeroPickApiResponse.ResponseData`); reusing them means adapting our data into those.

**Resource resolution (verified, not assumed):** `ArenaPlaque.xaml` defines almost all its
`StaticResource` keys locally; its only external deps are the `BoolToVisibility` /
`InverseBoolToVisibility` converters. HDT's `App.xaml` merges those converters **and**
`ArenaResources.xaml` at the **Application** level, and our plugin runs inside HDT's WPF
`Application`, so `new ArenaPlaque { DataContext = vm }` resolves everything with **no pack
URI merge on our part**. `StaticResource` resolves at parse time (inside `InitializeComponent`,
before the control is parented), so the keys must live in `Application.Current.Resources` —
which they do. Construction is wrapped in try/catch: on any failure we log once and fall
back to hand-drawn plaques for the rest of the session. **Still verify rendering on a live
client** — geometry/sizing and WPF layout can't be checked offline.

**Geometry & lifecycle (resize/DPI-safe, live-tuned).** Plaques are laid out in a fixed 4:3
design space (1200×900) inside a `Viewbox` (Uniform) that WPF scales to the client — the same
design-space-scaled approach HDT uses, so resizing the HS window just rescales everything (no
per-size pixel maths). The window is glued to the client rect each tick (`Reposition`,
DPI-corrected: Win32 reports device pixels, WPF wants DIPs). The three options sit at the same
horizontal positions in both phases (`OptionSpreadFraction`); only the vertical anchor differs
(hero portraits vs the lower card row). A class/name label sits under each plaque so the score
is unambiguously tied to its option. Visibility is driven every tick from `OnUpdate`: shown
only while a pick is active AND the client exists and is not minimised; nothing shows before
data has loaded. Anchors were tuned against a live client — re-check after an HDT/HS layout change.

HDT's arena *pipeline* (`ArenaPickHelperViewModel`/Watchers) is **not** a public injection
point — its view-model wires itself to HDT's internal `ArenaStateWatcher` and pulls the paid
Arenasmith scores, so we can't feed it our free scores. We reuse the public `ArenaPlaque`
*visual* only and drive our own overlay from `DraftWatcher`. Never copy HDT source into this
repo (unclear license) — reference its public API at runtime only.

## Runtime gotchas (verified live)

- **HearthDb isn't ready at `OnLoad`.** Right after HDT starts, `HearthDb.Cards.All` can be
  empty, so parsing resolves zero cards and everything looks like "no data" until a manual
  refresh. The load is **gated** on HearthDb being populated and **retries** until the sources
  report `IsLoaded`; any HearthDb-derived lookup (hero-skin → class) is built at load time,
  never in a constructor running during `OnLoad`.
- **Restoring `EnableArenasmithOverlay` can't rely on `OnUnload`.** HDT calls `Config.Save()`
  BEFORE unloading plugins on shutdown, so a restore in `OnUnload` is too late and the
  suppressed `false` would persist — and next launch we'd read that as the user's value. So
  the real preference is captured once to `native_overlay.pref` and always restored from there.
- **`OnUpdate` is wrapped in try/catch** (HDT disables a plugin after 100 `OnUpdate`
  exceptions), data loads **off the UI thread**, and `DraftWatcher` polls at **500ms** (HDT's
  own cadence), logging a fetch failure once per streak rather than every tick.

## Build & install

Requires HDT installed. Both work:

```sh
dotnet build HdtArenaHelper.sln -c Release
msbuild HdtArenaHelper.sln /restore /p:Configuration=Release
dotnet test HdtArenaHelper.sln -c Release    # runs offline, but needs HDT installed
```

Tests reference the HDT-shipped assemblies with `Private=True` (copied into the local
test output only — still never redistributed). The heuristic golden-score tests pin the
formula to the training tool's values: if an HDT update changes a card's text/stats,
recompute the affected goldens with `HdtArenaHelper.Training/`.

`HSDT.props` auto-discovers HDT under `%LocalAppData%\HearthstoneDeckTracker\app-*`
(override with `/p:HSDTPath=...`). The referenced HDT/HearthMirror/HearthDb/Newtonsoft
assemblies are `Private=False` — bound at runtime, never redistributed. A local build
auto-installs the DLL into `%AppData%\HearthstoneDeckTracker\Plugins\HdtArenaHelper\`
(PostBuild, skipped when `CI=true`). Enable **Arena Helper** in HDT → Options → Tracker →
Plugins.

## Conventions

- **Verify, don't assume.** Check accessibility, APIs and behaviour against the actual
  HDT source and by building/running *before* asserting them — e.g. HDT's arena overlay
  controls turned out to be `public`, not `internal` as first claimed. Future agents must
  verify too, never guess. Verify against HDT's **upstream master** on GitHub
  (`HearthSim/Hearthstone-Deck-Tracker`, e.g. via `gh api .../contents/...?ref=master`) —
  that is what ships to users — not only a local checkout, which may be a diverging fork/branch.
- C# 9 / net472, `Nullable` enabled. Match the style of the existing files.
- Comments minimal; explain *why*, not *what*. Do not reference issue numbers in code.
- Network code: always cache, always fail soft (log + return null). The HSReplay fetch uses
  `curl` (a browser UA on .NET's own stack is not enough — Cloudflare TLS fingerprint).
- **Observability:** log the meaningful steps with the `[ArenaHelper]` prefix — data load
  source/counts, choices, per-option resolution, overlay layout coords, show/hide — so live
  issues are diagnosable from the HDT log.
- Style is enforced by `.editorconfig` (tabs in C#, spaces elsewhere, LF, no trailing
  whitespace). A `.githooks/` pre-commit hook checks staged whitespace/indentation; enable
  once with `git config core.hooksPath .githooks`. Contributor guide: `CONTRIBUTING.md`.

## License

[MIT](./LICENSE).
