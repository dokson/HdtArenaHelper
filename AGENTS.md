# AGENTS.md — HDT Arena Helper

Guidance for AI agents and human contributors. Read this before making changes.

## What this is

An open-source **Arena draft-helper plugin for [Hearthstone Deck Tracker](https://hsdecktracker.net/)**
(HDT). During an Arena / Underground Arena draft it reads the three offered cards,
computes a **single blended 0–100 score** for each, and shows them in an overlay.

**Positioning** (keep the README consistent with this): a free alternative to HDT's own freemium
arena assistant, with its OWN scoring algorithm fed by more than one provider — HSReplay is made
by the tracker's authors, so relying on it alone would inherit a single view of the format. The
audience includes players who do not follow the meta, which is why the score is one number with a
stated reason and a stated confidence. **Never claim to be more accurate than the paid helper**:
it has data we do not, and no benchmark against it exists. The claim is free, auditable, and
explicit about its limits.

**Status:** data pipeline, draft detection, multi-source blend and overlay are in place and
**live-verified on a real HDT client**. The overlay hosts HDT's native `ArenaPlaque`, scales with
the client (a `Viewbox` design-space, so resize/DPI are automatic) and hides when HS is minimised.
Covered: the card draft, the hero/class pick, the "legendary group" pick, the redraft deck review,
and — in game — Discover choices and the mulligan. One win-rate source (HSReplay) drives the score,
with reprints pooled by card IDENTITY; cards are scored in the drafted class's context when known;
a bounded synergy engine nudges deck-fit; the board's own constraints are reported in words rather
than folded into the score; and the mulligan is judged against the drafted DECK rather than from
per-card averages.

## Data sources & ethics

The plugin uses only data it is **entitled to use**, never merely data it can reach, and must
never scrape a paywalled service or bundle/redistribute anyone's data.

> **Publicly reachable is not licensed.** This section used to say "free, public data", which
> quietly treated an open URL as a grant. It is not: absent a stated licence the default is no
> permission — and this is not hypothetical here. A provider whose CDN this project used without
> asking objected, and the source was **removed in 0.1.5**, feed and cached files and all. It cost
> a second opinion on every card and an entire feature. That is the price of getting the order
> wrong, and it is cheaper than the alternative. Consequences for anyone working here:
>
> - **Ask before adding a source**, and record the answer next to it in the table below.
> - Never argue "it is public, so it is fair game" — that is the exact reasoning that failed.
> - If a provider says stop, stop, and do not keep a copy either. Every source must stay
>   individually droppable, with the offline model as the backstop. Keep it that way: the project
>   has now had to exercise that path for real.
> - Automated traffic (CI retrains) is the most visible thing we do on someone else's
>   infrastructure. Keep it rare, single-shot, and be ready to remove the schedule.

| Source | What | Status |
|---|---|---|
| HSReplay arena `api/v1/arena/card_stats/free/` | Real arena win-rate / popularity per card + class tier list | ✅ used — the ONLY win-rate source, weight 1.0, and also the training target |
| HearthDb (bundled with HDT) | Card metadata for the offline heuristic, the mulligan advisor and id resolution | ✅ used, offline |

**The feed is UNDERGROUND-scoped, and short-windowed.** The payload states its own filters —
`ArenaGameTypeFilter.BGT_UNDERGROUND_ARENA`, `ArenaTimestampRangeFilter.LAST_4_DAYS`,
`meta_period_id`. Two consequences worth holding on to: **no pre-patch games pollute the score**
(a concern that turns out to be already handled), and a player in NORMAL arena is being scored
from Underground games. The
4-day window also explains the thin samples seen everywhere, which is an argument for the shrinkage
already applied, not against it. `meta_period_id` is the ready-made hook if patch detection is ever
wanted.

The HSReplay endpoint **403s .NET's HTTP stack**: Cloudflare fingerprints the TLS
ClientHello, and a browser User-Agent alone is NOT enough (verified — `WebClient` and
`HttpClient` both fail; `curl` is let through). So the runtime fetch **shells out to `curl`**
(bundled in `%SystemRoot%\System32` on Windows 10 1803+), with a fail-soft `WebClient`
fallback. `--compressed` keeps it ~100 KB on the wire. Cache downloads with a 1-day TTL; do
not hammer. See `HsReplayArenaDataSource.DownloadWithCurlAsync`.

The scheduled retrain (`train.yml`) fetches the same endpoint from GitHub runners
**once a week** — a single ~100 KB request, to one host.
Keep it weekly and single-shot: automated traffic must stay well below anything that
could read as abuse of a free endpoint. If HSReplay ever blocks runner IPs, drop the
schedule to manual `workflow_dispatch` rather than working around the block.

## Scoring model

```
finalScore(card) = weightedMean( each source's normalized 0–100 score )  +  synergyBonus(card, draftedDeck)
```

> **Statistics live in [`HdtArenaHelper.Training/REPORT.md`](./HdtArenaHelper.Training/REPORT.md)** —
> the single source of truth for every data-science conclusion about training and card scoring:
> validation protocols, measured scores, what was tried and rejected, open questions. This section
> states *what* the pipeline does and which constraints a change must respect. **Put numbers there,
> not here**, and read it before touching the model: several claims in this file turned out to be
> true only in intent (the heuristic's slope, the "0.27" out-of-fold score) and REPORT.md is where
> that got caught. Keep it updated with each real-data session's findings.

- **`IArenaDataSource`** — a rating provider, normalizing its metric to 0–100 so heterogeneous
  signals can be merged. All statistical policy is shared in `ScoreMath` so the sources stay
  mutually calibrated: shrink toward a prior, then a logistic on the robust median/MAD.
  - `HsReplayArenaDataSource` — the real win-rate source, on
    **drawn** win-rate (less deck-confounded than inclusion win-rate). Once the drafted class is
    known, a card is scored from that class's bucket, shrunk toward a **leave-that-class-out**
    prior (a prior containing the observation would double-count it) on the same ALL-anchored
    scale as the fallback, so mixed picks stay comparable. HSReplay also builds a per-class **tier
    list** that scores the `HERO_*` skins at the hero pick. It additionally exposes
    `IClassWinRateSource` — a class's arena win-rate in **real percentage points**, games-weighted
    from per-card tallies and re-centred so the pool sits at 50 (the weighting oversamples winning
    decks, since a winning arena deck keeps playing). Shown as a label under the hero plaque,
    **never blended into the score**: measured, it ranks the classes the same as the pool tier
    (Spearman 0.96), so it buys readability, not accuracy. Numbers in REPORT.md §11 — including a
    cross-source validation that can no longer be repeated, since there is only one source now.
  - **Weights: the win-rate signal is 1.0 against the heuristic's 0.5**, a 2:1 ratio to preserve
    whatever the source list looks like. Sources measuring the SAME quantity share that 1.0 (two
    would be 0.5 each) rather than voting 1.0 apiece, which would silently demote the heuristic to
    1/5 of the blend. The ratio survived the drop to a single source deliberately: raising the
    heuristic to half the blend would be a scoring change disguised as a dependency removal, and
    REPORT.md argues its authority should if anything go DOWN.
  - `HeuristicArenaDataSource` — offline metadata backstop for cards the win-rate feeds don't
    cover. Weights are **ridge-fit against real drawn win-rates, never hand-tuned** (hand-tuned
    keyword bonuses validated *worse* than the bare stat curve). Every model number lives in
    `arena_weights.json`, embedded into the DLL; feature extraction is **shared** with the trainer
    so the two cannot drift. Train offline, infer online — the plugin never re-fits.
    **Re-fit each rotation/patch**; the weights describe the current pool, not universal keyword
    values. A weight absent from the json reads as 0, so *removing* a feature from the fit needs
    no runtime change — only *adding* one does.
- **`ScoreAggregator`** — weighted mean over sources with data, plus the synergy bonus; returns a
  per-source breakdown (effective weight + games) for the overlay's confidence display. Two rules
  that are load-bearing and measured:
  - each source's configured weight is scaled by that card's sample confidence, n/(n+k) with the
    shared prior k, so a 5000-game estimate is not averaged 50/50 against a 30-game one;
  - **with no empirical sample at all, the blend is shrunk toward 50** (`ModelOnlyShrink`): on the
    thinnest-sampled cards the heuristic measures no better than a constant, so it must not state
    a confident number where it is the only signal. Shrinking is monotone, so the order among
    unmeasured cards survives. **Do not raise the heuristic's weight to compensate** — the
    measurement says the opposite. The constant is a **regression slope ratio** (thin-decile
    calibration slope over the random holdout's), re-emitted every refit as
    `model_only_shrink_measured` in `metrics.json`; it is NOT a ratio of correlations, which is
    what an earlier version wrongly used. REPORT.md §6 has the numbers and why the two differ.
  - **Provenance is explicit and REQUIRED** on every `ScoreComponent` (`fromSample`): "is this real
    data" cannot be read off a per-card sample size, because a class tier and a synthesized legendary
    group score are real win-rate data with no per-card `Games`. Reading `MaxGames` instead made the
    overlay print "win-rate data unavailable" over three displayed win-rates and star every option as
    low-confidence — **three times, in three places**, which is why the parameter has no default.
- **Display scale.** Sources share a centre (median card → 50) but not a slope: the win-rate
  logistic is steeper (~+35 pts per robust-SD) than the heuristic (~+15), so a real win-rate
  dominates on disagreement even at equal weight. Intended — the heuristic is a backstop. Both
  centre and scale are per-fit measured values (`anchor_median_raw`, `anchor_sigma_raw`); a fixed
  slope on the raw score would let the displayed spread drift with each re-fit's raw scale.
- **`ISynergyEngine`** — deck-context: see below.
- **Legendary groups — first pick, in BOTH The Arena and The Underground.** The June-2025 rework
  brought them to normal arena, so this is not an Underground-only screen. Each option is a
  legendary plus a 3-card package. `DraftWatcher` reads `DraftChoices.Packages` (a
  `List<List<Card>>` index-aligned to `Choices`) into `DraftOption.PackageDbfIds`. The score is NOT
  the plain mean: it is `mean + 0.35·(best − mean)` (`LegendaryGroupScore.BestCardTilt`), because the
  first pick is the run's only guaranteed legendary while ~29 later picks can supply average bodies —
  a mean prefers four solid cards over a bomb plus filler, inverting how the choice plays. Each card
  is scored against the drafted deck PLUS the rest of its own group, so a tribal bundle makes its own
  payoffs live. See `LegendaryGroupScore` (static and tested; the plugin only delegates).

## Synergy engine

`MetadataSynergyEngine` turns the drafted deck into a **+/- bonus** folded into `finalScore`, from
objective card metadata only — no tier lists, no card-specific rules.

**Fuzzy components** (each capped, total clamped to ±`MaxBonus` = 3): **curve fit over MINIONS
ONLY** — "curve" means a body on turn N, and counting spells by cost meant a few cheap spells read
as a full 2-slot so the engine penalized the 2-drop minions the deck needed; targets are shares of
a deck's ~19 minions, scaled by draft progress. Then tribal payoff/member (12 tribes, `Race.ALL`
amalgams count for the bonus but NOT for clearing the dead-card penalty), spell school on
`Card.SpellSchool`, weapon crowding, location crowding (softest — and note Hearthstone has **no**
per-player Location limit; the cost is tempo and board space, not slot exclusivity), and spell
damage pairing.

**Tribal and school rules require a real DEPENDENCY, via a whitelist** (`DependencyPatterns`) plus a
generation veto (`GenerationPatterns`). Merely mentioning a tribe is not depending on one, and a
blacklist of verbs had to be extended forever: measured against the live pool it wrongly condemned
Endtime Murozond ("Fill your board with random Dragons"), Dig for Treasure, Castle Kennels and five
others, and it scored anti-tribe tech UP for the tribe it exists to punish. Class names are stripped
first — `demons?` matched "Demon Hunter", so every DH card naming its own class read as a Demon
payoff. **When changing these patterns, re-validate against the card pool**, not just the tests.

**Unvalidated by design**: there is no free public per-deck dataset to fit these against, and this
project's own validation showed hand-tuned card values score *worse* than nothing. The guardrail is
the bound, so they break ties between comparable cards and can never override a solid win-rate.
Tests pin directions, caps and the clamp — never magic numbers. Surfaced as EXPERIMENTAL (README,
and "(exp.)" on the overlay's reason lines).

**Availability damping.** The dead-card penalty is scaled by how much of the drafted CLASS's deck
the missing tribe normally holds (`IClassTribeAvailabilitySource`, popularity-weighted from the
HSReplay per-class payload we already fetch, so it re-measures itself each patch — no bundled table).
The quantity is `share x picksLeft` against the couple of members a payoff needs: a Warlock offered a
Demon payoff at pick 5 will see Demons (16.6% of its slots), a Paladin will not (0.60%). **One-way by
design: it can only REDUCE the penalty**, never deepen it, and missing data reproduces the old
class-blind behaviour exactly. Measured table and the refuted hypothesis that prompted it (Priest is
the 3rd BEST dragon class, not a bad one) in REPORT.md §12.

**The dead-card lever is the one exception**: a separate, larger, progress-scaled penalty
(`DeadPayoffMax`) that *can* reorder a close pick, for structural dead cards the win-rate sources
cannot see — they average a payoff over the tribal decks that drafted it. Two cases fire it:

- a **quest/questline**, gated on the actual `GameTag.QUEST`/`QUESTLINE`/`SIDEQUEST` and never on
  text (a card merely *referencing* quests is a normal minion); sidequests at half weight. NOTE:
  questlines are excluded from the arena pool outright and quests are allowed per-rotation, so this
  may be unreachable in a given season — its tests pin the rule, not that it ever fires;
- a **tribal payoff/enabler drafted with zero members** of its tribe.

Being the only lever allowed past the clamp, it **fails closed** — anything ambiguous gets no
penalty. Three guards, each of which exists because the naive version got a real card wrong:

1. dead only if **every** tribe it references is absent (one live tribe clears a menagerie card);
2. a card that **supplies its own members or targets the opponent's** is exempt (`SelfSufficientRe`)
   — merely *mentioning* a tribe is not depending on one: Animal Companion summons its own Beast,
   and "Destroy a Pirate" wants the *enemy* to have pirates;
3. a card with a **standalone function** — a minion whose body isn't far below the vanilla curve,
   or a hero card — loses only `DeadBodyFactor` of the penalty: its text goes blank but the stats
   still play (Blackwing Corruptor without dragons is a 5-mana 5/4, not a mulligan).

All tribe/school patterns are **precompiled** and each card's text cleaned **once** per call: the
static `Regex.IsMatch` cache holds 15 entries and this engine has 19 patterns, so the naive form
re-parsed nearly every pattern per drafted card — which the deck-review panel multiplies by the
whole deck, on the UI thread.

## Architecture (file map)

| File | Responsibility |
|---|---|
| `ArenaHelperPlugin.cs` | `IPlugin` entry point; wires sources, warms data off-thread with retry, drives all overlay render/visibility from `OnUpdate` (wrapped in try/catch), suppresses/restores HDT's native overlay via a persisted pref file |
| `GameWatcher.cs` | Template method for everything that polls the client: the base owns the 500 ms throttle, the **scene gate**, the **arena-match gate** and log-once-per-failure-streak; subclasses implement only their client read (`PollCore`), their dedup key and their "screen gone" event. The gate is why the base exists rather than three copies — **both ghost-overlay bugs this project had came from a watcher acting on state belonging to another screen**, and the gate had already been copied byte-identical into two pollers. Deliberately NOT in the base: dedup and the gone-event, which genuinely differ (`DraftChoices.Version` vs an offered-id signature vs `EndDraft`'s showing-state semantics). The **arena-match gate** (`ArenaMatchOnly`, opt-in, on both GAMEPLAY watchers) is the third ghost-overlay bug, and the lesson is narrower than the scene gate's: an arena RUN being open is NOT the same as the current MATCH being arena. The run persists across modes, so with a 30-card Paladin run in progress a **Battlegrounds** hero/trinket choice arrived through the same choice zone on the same GAMEPLAY scene and was scored with arena win-rates — live, and bad enough that the user disabled the plugin mid-game. The gate reads `Reflection.Client.GetGameType()` against the four arena types (`GT_ARENA`, `GT_UNDERGROUND_ARENA`, + their `_PLAYER_VS_AI` variants). It fails permissive ONLY where the client says nothing — an unreadable type, or the `GT_UNKNOWN` reported for a moment as a game starts, which is precisely the mulligan's window; a type the client states and that is not arena is a definite no, and an id HearthDb does not know is not arena either. Do not re-derive "are we in arena" from `GetArenaDeck()` in a new watcher: that is the check that was already there and did not hold |
| `MulliganWatcher.cs` | The mulligan screen: `GetMulliganState()` ordered by the client's `ZonePosition` (the overlay places one column per card, so a misordered or partial hand does not degrade — it lies), gated on GAMEPLAY + an arena MATCH (`ArenaMatchOnly`, see `GameWatcher.cs`) + an arena run + a known class. It also carries the **run deck** (the advice is judged against it, so no deck means no event) and the **coin**, which is read from the hand SIZE — 3 cards going first, 4 going second — because the Coin is not dealt into the mulligan hand and a rule beats a field read that can fail |
| `IMulliganAdvisor.cs` / `DeckMulliganAdvisor.cs` | The mulligan advice, and the reason it can exist without anyone's data: a mulligan question is not "is this card good" (the draft score answered that) but "is it good in THIS hand given the other 27 cards". Ordinal verdict (Keep / Toss / **Situational**, the default and the most common answer) plus the fact behind it in words — never a percentage, because a keep-rate would have to be invented, which is the mistake REPORT.md measured. **The model is TEMPO x QUALITY**: what a card does on turns 1-2 decides the verdict, and the plugin's own 0-100 score decides whether that turn is worth buying. Rules, first match wins: a cheap PERMANENT (minion/weapon/location — a cheap spell is a card to draw into, not to hold) is a keep unless it is below-average, needs a friendly board that does not exist yet, has its Combo switched off going first, or has one health (any hero power removes it for free); a 1-mana quest wants to be down on turn 1; the Coin is an **effective turn** shift plus a `GameTag.COMBO` enabler; the top end goes back unless the card scores as a bomb; a HERO card or a card whose printed cost is not its real cost (self-discounting, modular) gets no verdict at all. Two abstentions are load-bearing: **no score and low-confidence score both mean silence**, because a thinly-sampled legendary scores mid-table for lack of games rather than lack of power, and tossing it is the one error the player cannot recover from. All-or-nothing like every other screen: one unresolved card voids the hand |
| `GameStateFacts.cs` | What the BOARD says about an in-game choice, in words beside the score and **never folded into it**: hand full (a discovered card is destroyed — reported first because it is the only irreversible one), board full for a minion, and cost above available mana. These follow from the rules, so they need no fitting; what no public data provides is their VALUE in points, and inventing one is the mistake REPORT.md already measured. An unreadable board makes every rule silent — the failure mode to avoid is printing "needs 7 mana, you have 0" over a playable turn |
| `CardChoiceWatcher.cs` | In-game card choices (Discover): polls `GetCardChoices()`, gated FIRST on the active scene being `GAMEPLAY`, then on the current MATCH being arena (`ArenaMatchOnly` — Battlegrounds is GAMEPLAY too and delivers its hero/trinket picks through this very zone), and only then on being in an ARENA run, which supplies the class context. Every number here is an arena win-rate, so outside arena there is none we are entitled to show. Dedup is on the offered-id list (no `Version` field exists here). Pure decision extracted to `BuildChoicePlan` for testing, like `BuildDeckEditPlan`; **all** offered ids must resolve or the choice is voided, because plaques are laid out by index and a partial list puts every score on the wrong card |
| `DraftWatcher.cs` | Reads offered cards + `Packages` via HearthMirror (`GetArenaDraftChoicesV3`), dedup by `Version`, throttled to 500ms, cardId→dbfId; `Reset()` on (re)enable. Gated FIRST on the active **scene** (`Reflection.Client.GetSceneMgrState()` == `SceneMode.DRAFT`) and only then on `ArenaSessionState` (draft/redraft only). **Both gates are load-bearing and each was added after a live ghost-overlay bug**: choices linger in client memory on other screens (landing page), and — verified in the HDT log — an unfinished redraft keeps reporting `EDITING_DECK` while the player is on the main menu or inside Battlegrounds, so the session state alone left the deck panel sitting on top of both. The scene read fails PERMISSIVE (falls back to the session gate): a ghost panel gets reported, while an overlay that never appears looks like a dead plugin. Also gated on `Choices.Count == 3` (animations expose partial lists). **The `Version` is consumed only once EVERY offered card resolves to a dbf id** — HearthDb is empty while HDT starts, so a draft already open at startup resolved nothing, fired an empty pick, showed a blank overlay and then deduped that pick forever; and a partially resolved pick would lay out N−1 plaques centred as if there were N, putting each score on the wrong card. Post-loss redrafts are picks too: `IsRedraft`, with run deck + `RedraftDeck` as the synergy context. (Redraft is an **Underground** mechanic; the code handles it mode-agnostically rather than gating on `IsUnderground`, so it costs nothing if normal arena ever gets it — but do not document it as a normal-arena feature.) The redraft **`EDITING_DECK`** phase ("Edit Your Deck" / discard-to-30) has no pick — it fires `OnDeckReview` with the whole deck (deduped by content) so the overlay can rank the weakest cards to cut. **The panel's visibility must NOT hinge on the deck size**: the client reports this phase two different ways across sessions (both in the HDT log, same build) — `deckSize=30` flat with the 5 new cards already counted in, or `deckSize=35` counting down as cards are picked for discard. So `deckSize - 30` is 0 in the first form and an earlier version that expected the deck to shrink left the panel up forever. The count to cut comes from the **redraft list**, which starts at 5 in both forms; the panel hides when the phase ends |
| `IArenaDataSource.cs` / `ScoreAggregator.cs` | Multi-source blend → `BlendedScore` (drafted class threaded through); `LoadedSourceCount` drives partial rendering AND re-renders as late sources come online, `IsLoaded` ends the warm-up loop |
| `HsReplayArenaDataSource.cs` | Real arena win-rate source; curl fetch, cache-then-download, gated on HearthDb being ready; per-class card scores + class tier |
| `CardIdentity.cs` | Collapses a card's REPRINTS onto one identity so the two feeds can be joined: they report different printings of the same card (`CORE_YOP_001` vs `YOP_001`), and joining on the raw dbf id left **216 cards with only one source** — precisely where the consensus is worth most. Grouped by `(name, class, type)` among **collectible** cards only (tokens reuse names; none is ever drafted), canonical = lowest dbf id for determinism. Anything unmapped keeps its own id: failing to merge costs a consensus, a wrong merge would pool two different cards' win-rates. Measured: name matching recovered 216/216 where id normalisation recovered 210, with zero ambiguous groups. Built lazily — HearthDb is empty at OnLoad. **Reprints are summed as COUNTS** (wins and games) in both parsers, never as an average of the two rates, which would weight a 1,000-game printing like a 3,000-game one; the scoring lookup canonicalizes too, but AFTER the hero-skin check, or a skin would collapse onto its base hero |
| `PayloadGuard.cs` | Every downloaded payload is treated as **untrusted input**, because it is: the feeds are third-party endpoints that can serve whatever they like to whichever caller they like. **The invariant to preserve: no RCE path.** Parsing uses `JObject.Parse`/`Load` only — never `JsonConvert.DeserializeObject<T>` and never `TypeNameHandling`, which is the setting that turns a JSON document into a gadget chain; do not introduce either into a remote-data parse path. What the guard bounds, each one a real payload away: a **stack overflow** from deep nesting (HDT ships Newtonsoft **12.0.3**, whose `MaxDepth` default is *unlimited* — the 64 default arrives in 13.0.1 — and a StackOverflowException cannot be caught, so it takes the whole tracker down), a **gzip bomb** (a compressed payload's decompression is bounded, not trusted), an oversized body (`curl --max-filesize` + a byte cap), and **poisoned numbers** — out-of-range rates are DROPPED, not clamped, because a dropped row falls back to the offline model while a clamped one asserts a value the feed never reported. The residual risk it cannot address is SUBTLE poisoning (shifting a class by 2pp is indistinguishable from a meta shift), and the mitigation for that was a second source cross-checking the first. **There is no second source now**, so that risk is currently unmitigated — worth remembering before treating single-sourcing as merely a quality issue |
| `ScoreMath.cs` | Shared statistical policy (shrinkage, median/MAD logistic) + hero-skin map, so the win-rate sources stay mutually calibrated |
| `HeuristicArenaDataSource.cs` | Offline heuristic base value |
| `ArenaCardScore.cs` | The per-option score record the overlay renders |
| `PlaqueTier.cs` | Pure 0-100 score → 1-5 plaque tier map (WPF-free, unit-tested) |
| `ISynergyEngine.cs` / `MetadataSynergyEngine.cs` | Synergy contract + the metadata engine. Rules, bounds and the traps behind each guard: see **Synergy engine** above — do not restate them here |
| `ArenaOverlayWindow.cs` | Borderless click-through overlay hosting HDT's native `ArenaPlaque` (hand-drawn fallback) in a 4:3 `Viewbox` design-space; class/name label under each plaque; `SetDeckReview` renders the redraft edit phase's deck panel — the WHOLE deck in the game's order (cost, then name) with an HDT-style score badge per row and the suggested cuts shaded red-to-yellow by cut rank, as a full-height column on the LEFT edge (rows share the client height; clamped so a row can neither clip the badge nor stretch into a menu); poll-driven show/hide. **Do not try to align badges onto the game's own "Your Deck" list**: measured live, the redraft deck has 23–28 distinct rows against the ~21 that list shows, so it always scrolls, and the scroll offset is not readable from the client. That version was written, shipped dormant behind a 22-row guard, and never once fired |
| `SelfUpdater.cs` | In-plugin auto-update over the public GitHub releases. **Two phases, deliberately:** a check (throttled 1×/day) downloads the bare `HdtArenaHelper.dll` release asset and only PARKS it as `*.dll.new`; `ApplyPendingUpdate()` performs the swap at the **next OnLoad**. Never swap when the download finishes — the check starts at load, so downloads often complete seconds before the user quits, and process death between the two moves leaves the folder with no `.dll`, which HDT can never repair (it loads only exact `.dll` files, so no plugin code runs again). At OnLoad the process has a whole session ahead of it. The swap renames the running (locked) DLL to `*.dll.old`: a loaded assembly cannot be overwritten or deleted but CAN be renamed on NTFS (verified empirically). Recovery retries with a `File.Copy` fallback and must never return without a DLL in place. Validation before anything is touched: MZ header, size cap, and the **managed assembly identity** (`AssemblyName.GetAssemblyName` must say `HdtArenaHelper`) — an MZ check alone accepts any PE, and installing the wrong one is an unloadable plugin. Asset URLs are host-checked (`IsTrustedAssetUrl`, GitHub HTTPS hosts only). Takes a `CancellationToken` cancelled from `OnUnload`. The previous version is KEPT as `*.dll.old` (the manual rollback), moved aside to `*.dll.old.prev` during a swap and only dropped once it succeeds — and promoted back if a swap died mid-dance. **Trust boundary**: bytes come only from this repo's official releases over HTTPS with no signature verification — the same trust as the user's original manual install. Fail-soft to a manual "open releases page" |
| `HdtArenaHelper.Numerics/` | Pure maths — the scikit-learn-equivalent ridge solver and the descriptive statistics — with **no HDT/HearthDb/HearthMirror reference at all**. That isolation is the point: keep it that way, and never add an `HSDT.props` import here |
| `HdtArenaHelper.Tests/` | xUnit tests for the PLUGIN, all offline but requiring HDT installed (HearthDb): aggregator blend + shrink rules, heuristic golden scores (pinned to the trainer, the deliberate weights-changed tripwire), synergy directions/caps/clamp + availability damping, win-rate parsing via synthetic caches, the self-updater swap on temp files, `DraftWatcher.BuildDeckEditPlan`/`ToDbfId`, `PlaqueTier`, class win-rate re-centring, and the mulligan advisor's rule directions (each case built from a DECK, since the same card is a keep in one and a toss in another — a fixture that did not vary the deck would be testing a tier list) |
| `HdtArenaHelper.Numerics.Tests/` | The only suite that runs **without HDT installed** (`dotnet test HdtArenaHelper.Numerics.Tests`): ridge solver against known-truth properties, and the statistics — including `RegressionSlope`, whose asymmetry-vs-correlation test is the guard against the mistake `ModelOnlyShrink` was first derived with |
| `HdtArenaHelper.Training.Tests/` | The trainer's deterministic pieces: `WeightsFile.RoundWeights` (the drop-below-floor rule the runtime depends on), `metrics.json` format (LF only, invariant decimals), `HoldoutReport.ShrinkFromSlopes` (ratio, clamp, NaN refusal). Needs HDT, because the trainer references the plugin |
| `HdtArenaHelper.Training/` | Fits the heuristic weights → `arena_weights.json` (embedded into the plugin); ridge and statistics come from `HdtArenaHelper.Numerics`, features from the plugin's `BuildFeatures`. Re-run per patch with `dotnet run --project HdtArenaHelper.Training`, plus `-- --offline` to refit from the last payload snapshot without touching the network. One file per responsibility: `TrainingConfig` (every fit-policy knob), `PayloadFetcher`, `TrainingRows`, `ModelSelection` (CV + holdouts), `Bootstrap` (coefficient SEs + gate noise floor), `WeightsFile`, `RunMetrics` (`metrics.json`, what CI gates on). Findings: `REPORT.md` |

## Native overlay reuse

HDT's arena overlay controls in `Controls/Overlay/Arena/` are **public** (verified on upstream
master), so `ArenaOverlayWindow` hosts HDT's own plaque for a pixel-native look across the card
draft AND the hero/class pick, with a hand-drawn plaque as fallback.

- **`ArenaPlaque` + `ArenaPlaqueViewModel(string score, int level, int seed, bool isUnderground[,
  Thickness margin])`** — built entirely from our own data, no HDT API types. `score` is the display
  string (the 0-100 blend, rounded, matching HDT's integer display); `level` is the 1-5 flame/bolt
  tier from `PlaqueTier.FromScore` (0 = a "loading" state we don't use); `seed` randomises flame
  angles; `isUnderground` picks the red/gold vs blue theme.
- `ArenaPickSingle{Card,Hero,HeroPower,DualClassHero}Option` — richer panels, but their view-models
  take HDT API-response types, so reusing them means adapting our data into those.
- **Resources resolve with no work on our part** (verified): `ArenaPlaque.xaml`'s only external
  dependencies are the `BoolToVisibility` converters, which HDT's `App.xaml` merges at
  **Application** level — and we run inside HDT's `Application`. `StaticResource` resolves at parse
  time inside `InitializeComponent`, so the keys must be in `Application.Current.Resources`; they
  are. Construction is wrapped in try/catch: on failure, log once and use hand-drawn plaques for
  the rest of the session.
- **HDT's arena *pipeline* is NOT a public injection point** — `ArenaPickHelperViewModel` wires
  itself to HDT's internal `ArenaStateWatcher` and pulls the paid Arenasmith scores. We reuse the
  `ArenaPlaque` *visual* only and drive our own overlay from `DraftWatcher`. Never copy HDT source
  into this repo (unclear license) — reference its public API at runtime only.

**Geometry (resize/DPI-safe).** Plaques live in a fixed 4:3 design space (1200×900) inside a
`Viewbox` (Uniform), the same design-space-scaled approach HDT uses, so resizing the client just
rescales everything — no per-size pixel maths. The window is glued to the client rect each tick
(`Reposition`, DPI-corrected: Win32 reports device pixels, WPF wants DIPs). Visibility is driven
every tick from `OnUpdate`: shown only while a pick is active AND the client exists and is not
minimised, and never before data has loaded.

**Anchors are live-tuned and must be re-checked after any HDT/HS layout change.** Current values
(fractions of the design space unless noted), each corrected on a real client at least once:

| Screen | centre Y | spread | note |
|---|---|---|---|
| card draft / legendary group | 0.55 | 0.26 | one layout for both |
| hero pick | 0.60 | 0.29 | below the client's own hero-name banner; 0.43 ran through it |
| in-game Discover | 0.74 | 0.27 | BELOW the cards (drawn much larger than draft cards), centred on the FULL width — the draft's centre is offset left for the deck list, which in game does not exist |
| mulligan | 0.22 | 0.20 | above the hand; lower put the gauge on the card art |
| redraft deck panel | — | — | client-relative, LEFT edge, fills the height: top 0.015, bottom **0.075** (the client's own bottom bar lives there), row height clamped 24–48 DIP |

Geometry and WPF layout cannot be verified offline — **always confirm rendering on a live client**,
and the log prints every layout's coords (`layout <screen> … centreY=`) to correct them from.

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

Prerequisites, the HDT auto-discovery (`HSDT.props`, `/p:HSDTPath=...`) and the local
auto-install into the Plugins folder are in [`CONTRIBUTING.md`](./CONTRIBUTING.md) — that is the
contributor-facing copy and the one to keep current. What follows is only what an agent gets wrong
without being told.

```sh
dotnet build HdtArenaHelper.sln -c Release
msbuild HdtArenaHelper.sln /restore /p:Configuration=Release
dotnet test HdtArenaHelper.sln -c Release    # runs offline, but needs HDT installed
dotnet test HdtArenaHelper.Numerics.Tests    # the exception: no HDT required
```

Three test projects, split by what they need rather than by taste: `HdtArenaHelper.Numerics.Tests`
needs nothing but the repo, while `HdtArenaHelper.Tests` and `HdtArenaHelper.Training.Tests` need
HDT installed for HearthDb. Put a new test where its dependencies say, not where its subject lives —
a pure-maths test that lands in the wrong project silently loses the no-HDT guarantee.

`HdtArenaHelper.Tests` runs **sequentially** (`AssemblyInfo.cs`): the code under test logs through
HDT's `Log.WriteLine`, whose queue is unsynchronised, so parallel logging classes corrupt it and
throw from inside HDT — a CI-only failure while the same suite was green locally. Do not re-enable
parallelisation there; put new plugin tests in that project regardless.

Tests reference the HDT-shipped assemblies with `Private=True` (copied into the local
test output only — still never redistributed). The heuristic golden-score tests pin the
formula to the training tool's values: if an HDT update changes a card's text/stats,
recompute the affected goldens with `HdtArenaHelper.Training/`.

**Release tags MUST be 3-part** (`vMAJOR.MINOR.PATCH`), matching `Version.props`. The self-updater
compares `(major, minor, build)` only, so a 4-part hotfix tag would read as "not newer" on every
installed client and reach nobody — and the updater is the recovery path for a bad release.
`build.yml` rejects any other tag shape before publishing.

**The SDK is pinned** in `global.json`, and the `dotnet-version` in every workflow must stay in step
with it. This is not ceremony: `Directory.Build.props` turns code-style rules into build ERRORS, and
analyzer behaviour moves between SDK majors — unpinned, a green local build and a green CI build are
two different claims. It was already happening: local ran SDK 10 while CI pinned 8.

**Package versions live only in `Directory.Packages.props`** (central package management). Adding a
`Version=` attribute to a `PackageReference` is an error, not a style choice: the three test projects
used to pin xunit and the Test SDK separately, so one Dependabot bump could leave them mismatched —
which surfaces as a failing test, not as a version skew.

The referenced HDT/HearthMirror/HearthDb/Newtonsoft assemblies are `Private=False` — bound at
runtime, **never redistributed**, which is the constraint behind the whole reference setup.

## Conventions

- **Verify, don't assume.** Check accessibility, APIs and behaviour against the actual
  HDT source and by building/running *before* asserting them — e.g. HDT's arena overlay
  controls turned out to be `public`, not `internal` as first claimed. Future agents must
  verify too, never guess. Verify against HDT's **upstream master** on GitHub
  (`HearthSim/Hearthstone-Deck-Tracker`, e.g. via `gh api .../contents/...?ref=master`) —
  that is what ships to users — not only a local checkout, which may be a diverging fork/branch.
- C# 9 / net472, `Nullable` enabled. Match the style of the existing files.
- Comments minimal; explain *why*, not *what*. Do not reference issue numbers in code.
- Network code: always cache, always fail soft (log + return null). Why HSReplay needs `curl` is
  in **Data sources & ethics** above; do not re-explain it at the call site or here.
- **Observability:** log the meaningful steps with the `[ArenaHelper]` prefix — data load
  source/counts, choices, per-option resolution, overlay layout coords, show/hide — so live
  issues are diagnosable from the HDT log.
- Style rules and the pre-commit hook: [`CONTRIBUTING.md`](./CONTRIBUTING.md). The one part that
  is an agent's problem rather than a contributor's: `Directory.Build.props` makes those rules
  build **errors**, so a style slip is a broken build, not a review comment.
- **Slopwatch** (pinned local dotnet tool; CI gate) blocks NEW disabled tests, warning
  suppressions, empty catch blocks etc. Run `dotnet tool restore` once, then
  `dotnet tool run slopwatch analyze -d . --fail-on warning` after changing C#. The few
  deliberate legacy patterns are baselined in `.slopwatch/baseline.json` — extend the
  baseline only for a justified, commented pattern, never to silence a real fix.

## License

[MIT](./LICENSE).
