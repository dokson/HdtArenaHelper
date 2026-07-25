# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.4] - 2026-07-25

### Changed

- **Rarity no longer influences a card's score.** `rarity_ord` and `is_legendary` are gone from the
  model: rarity is a print-run label, and what actually makes a legendary strong — an above-curve
  statline, unique text — the model already reads directly, so the label was collecting credit that
  belongs to the card. The clearest illustration is Deathwing, which fell from 16.01 to 5.12: a
  10-mana 12/12 that hands over the board belongs at the floor, and it had been getting ~10 display
  points for its rarity alone. Weights were re-fit without the two features and adopted, with the
  golden literals updated. The fit's own numbers all moved the right way too, but they are NOT the
  argument and REPORT.md 14 says why (the regularization strength is re-selected per feature set,
  and the thin-sample statistics carry no standard error).
- **The offline heuristic no longer scores hero cards; it abstains.** Its `is_hero` term has a single
  supporting row and also has to cancel the 30 health those cards report, so it was never an estimate
  — this week's refit flipped it from −0.08 to +0.77 with a standard error of 0.98 and sign
  consistency 0.46, i.e. a coin toss deciding a whole card type, worth ~25 display points. Measured
  cost of abstaining: of the 46 collectible hero cards, exactly 2 appear in either win-rate feed —
  and those two are the ones actually in the pool, since a feed reports what gets drafted. So no card
  a player can be offered loses its number. The intuitive fix — hero cards are strong, give them a
  bonus — stays refused: hand-tuned card values are what this project measured to be worse than
  nothing.

### Fixed

- **The in-game overlay no longer appears outside an arena match.** With an arena run open, a
  **Battlegrounds** hero/trinket pick arrives through the same choice zone, on the same gameplay
  scene, as a Discover — so it was scored with arena win-rates and drawn over the Battlegrounds
  board, which is disruptive enough to make disabling the plugin the reasonable response. The gate
  that was supposed to prevent this asked whether an arena RUN existed; a run stays open across
  modes, so it answered yes all through Battlegrounds. Both in-game watchers (Discover and mulligan)
  now also require the current MATCH to be arena, read from the client's game type. It stays
  permissive only where the client says nothing — an unreadable type, or the "unknown" reported for
  a moment as a game starts, which is exactly the mulligan's window; a stated non-arena type is a
  definite no. When it blocks it logs one line, so a missing overlay is diagnosable rather than
  mysterious.

## [0.1.3] - 2026-07-25

### Added

- **In-game card choices (Discover).** The same scoring engine the draft uses, applied to the cards
  a Discover offers, in your run deck's class context and with the deck as synergy context. Gated on
  the active scene being gameplay AND on being in an arena run: every number here is an arena
  win-rate, so outside arena there is none we are entitled to show.

- **Board awareness on in-game choices.** A Discover now reports what the board makes impossible:
  a card that cannot be cast this turn (with both numbers, since one mana short and five short are
  different decisions), a minion with no room, and — first, because it is the only irreversible
  one — a full hand, where the discovered card is destroyed. Deliberately words next to the score
  and not points inside it: the rules make the facts objective, nothing public says what they are
  worth, and the score already reads well without a guess bolted onto it.
- **Mulligan guidance.** On the mulligan screen, each card's keep win-rate and how often players
  keep it, for your drafted class, from counters already present in the data the plugin downloads
  anyway — so it costs no extra requests. Presented as an estimate and qualified with its sample
  size, because it is single-source, thinly sampled, and not causal: a card is kept in hands that
  already look good, so part of a high keep win-rate is the hand rather than the card. Cards below
  the sample floor show a dash instead of a number.

### Changed

- **The two win-rate feeds are now joined by card identity, not by printing.** They report different
  printings of the same card (HSReplay `CORE_YOP_001`, Firestone `YOP_001`), which left **216 cards
  scored by a single source** — exactly where a second opinion is worth most, and where the offline
  heuristic silently gained influence. Cards covered by both feeds went from 1007 to 1219. Printings
  are pooled as counts (wins and games), never as an average of two rates, which would weight a
  thin printing like a thick one.
- Hero plaques sit lower, below the client's own hero-name banner, where they no longer overlap it.
- Internal structure, for the bug classes it removes rather than for tidiness: the three client
  pollers now share one template (poll throttle, **scene gate**, log-once) — both ghost-overlay bugs
  this project has had came from a watcher acting on another screen's state, and the gate had been
  copy-pasted; the overlay's four mutually exclusive screens became one active-screen field, since
  the "only one at a time" rule was previously maintained in two separate places that had to agree;
  and the leave-one-out shrink prior moved into `ScoreMath`, which is where shared statistical policy
  belongs — the two copies had already drifted, only one of them range-checking its result.

### Build

- The plugin test assembly runs sequentially. The code under test logs through HDT's own logger,
  which enqueues into an unsynchronised queue, so two logging test classes in parallel corrupt it and
  throw from inside HDT with a stack trace that looks like ours. It failed only in CI while the same
  suite was green locally — core count decides whether the race is hit.
- The .NET SDK is pinned (`global.json`) and every workflow aligned to it. The repo treats code-style
  rules as build errors, and analyzer behaviour differs across SDK majors — with local on 10 and CI
  on 8, a green build in the two places was not the same claim.
- NuGet versions moved into `Directory.Packages.props`. Three test projects pinned xunit and the Test
  SDK independently, so a partial bump could leave them mismatched — a skew that presents itself as a
  failing test.

### Security

- The canary workflow now declares least-privilege permissions (`contents: read`). It publishes
  nothing — it only reads another public repo's release tag — but an unset block inherits the repo
  default, which is a write-scoped token in many configurations. Flagged by CodeQL.
- Downloaded payloads are now treated as untrusted input. The parse path never used
  `TypeNameHandling` or `DeserializeObject<T>`, so remote code execution was never reachable — that
  is now written down as an invariant. What was reachable, and is now bounded: a stack overflow from
  deeply nested JSON (the bundled Newtonsoft's depth limit is unlimited, and a stack overflow cannot
  be caught — it would take the whole tracker down, not just the plugin), a gzip bomb via the
  compressed per-class files, an oversized body, and out-of-range values, which are dropped rather
  than clamped so a poisoned row falls back to the other source instead of asserting a number the
  feed never reported.

### Fixed

- The overlay announced "win-rate data unavailable" — and starred every option as low-confidence —
  over scores it had just derived from thousands of games, at the hero pick and on legendary groups.
  Both read "is this real data" off a per-card sample size, which a class tier and a synthesized
  group score legitimately do not carry. Provenance is now explicit and required at every
  construction site, since the same bug appeared three times.
- The overlay could stay blank for a whole draft if HDT started while a draft was already open:
  the pick was consumed for dedup before its cards resolved, and HearthDb is empty at startup, so
  nothing resolved and the pick was never retried. A pick is now consumed only once every offered
  card resolves — which also prevents a partially resolved pick from putting each score on the
  wrong card.

### Docs

- The data-source policy no longer says "free, public data": publicly reachable is not licensed.
  Firestone's developer objected to this project using their CDN without asking, and was right to —
  their repos carry no licence and the site publishes no terms, so there is no permission to infer.
  Every source must stay individually droppable.
- Both feeds are documented as Underground-scoped and short-windowed, which they always were.

## [0.1.2] - 2026-07-25

### Added

- Redraft deck-review panel: during the "Edit Your Deck" / discard phase (no card is
  being picked) the overlay ranks the deck weakest-score first, so the cards to cut
  are obvious. Scored in the deck's own context (a dead payoff sinks), shown as our
  own corner panel with each card's mana cost, sized to how many still need
  discarding, and hidden once the deck is trimmed back to 30.
- Synergy engine, dead-card anti-synergy: a separate, larger, progress-scaled
  penalty that can reorder a close pick, for cards the win-rate sources over-rate
  because they average them over the decks that ran them — a tribal payoff/enabler
  drafted with none of its tribe ("draw your Dragon" with no dragons), or a quest
  that arena tempo rarely rewards. Guarded against false positives: one live tribe
  clears a multi-tribe card, a card with a playable body (or a hero card) only loses
  a fraction, sidequests take a lighter touch than full quests, and self-sufficient
  cards (summon/discover their own, or target the opponent's) are exempt.
- Synergy engine, new fuzzy rules (bounded, tie-breaking): spell-school payoff/member
  on `Card.SpellSchool` (tight cap), `Draenei` added to the tribe list, and
  location-slot crowding.
- Hero pick: each class's estimated arena **win-rate in real percentage points** under the
  plaque, because "71/100" is not a number a player can check and "~53%" is. Derived from the
  per-card tallies both feeds already publish, games-weighted and re-centred so the pool sits
  at 50 — the weighting oversamples winning decks, since a winning arena deck keeps playing.
  The two independent sources agree within ~2pp with identical ordering. Shown as a label
  only, NOT blended into the score: measured, it ranks the classes the same as the existing
  pool-quality tier (Spearman 0.96), so it buys readability, not accuracy.
- Synergy engine, per-class tribe availability: the dead-card penalty now asks how much of the
  drafted class's deck the missing tribe normally holds before condemning a payoff. Demons are
  16.6% of a Warlock's deck slots and 0.6% of a Paladin's — a 28x spread the class-blind
  penalty charged identically. Measured per patch from data already fetched, and deliberately
  one-way: it can only reduce the penalty, never deepen it. This also refuted the hypothesis
  that prompted it — Priest is the third BEST dragon class, not a poor one; the genuinely dead
  cases are Hunter and Demon Hunter.

### Changed

- Redraft deck panel, rebuilt after live testing: it now lists the WHOLE deck in the game's own
  order with an HDT-style score badge per row, the suggested cuts shaded red-to-yellow by how
  clear the cut is, as a full-height column on the left edge. An earlier version
  drew a badge column ON TOP of that list; that is not tunable and was removed — measured live,
  the redraft deck has 23-28 distinct rows against the ~21 the list shows, so it always scrolls,
  and the scroll offset is not readable from the client. It had shipped dormant behind a 22-row
  guard and could never once have fired.
- The model-only shrink constant is now derived the way a shrink factor has to be: a REGRESSION
  SLOPE of held-out truth on prediction (0.31 on the thin decile against 0.92 on a random
  holdout, so 0.34), emitted every refit in `metrics.json`. It was previously a ratio of rank
  CORRELATIONS, which answers a different question. The value barely moved — it was right by
  accident, and the reasoning was not.

- Scoring, model-only cards: when neither win-rate source has a sample for a card the
  blended score is now pulled toward the middle instead of stating a confident number.
  Backtesting showed the offline heuristic is no better than a constant on thinly-sampled
  cards — the very cards where it is the only signal — so it no longer gets to outrank a
  well-measured card on a guess. The ordering among unmeasured cards is unchanged.
- Scoring, thin win-rate data: the offline heuristic no longer *gains* influence as the real
  data thins. Sample-size weighting used to shrink only the win-rate sources, so the
  heuristic's share of a pick grew from the intended third to two thirds on 20-game cards.
  It now keeps the same share at every sample size.
- The heuristic's 0-100 display scale is measured per re-fit (median AND robust spread)
  instead of a fixed slope on the raw score, so the displayed spread no longer drifts with
  whatever scale a re-fit happened to land on.
- Synergy engine: cards that summon or discover their own tribe members, or that target the
  opponent's, are exempt from the dead-card penalty — merely mentioning a tribe is not
  depending on one (Animal Companion brings its own Beast). Also a large speed-up in
  synergy and heuristic scoring, which the deck-review panel needed.

- Synergy curve fit now measures a slot against its target count for a full 30-card
  deck instead of the partial-deck fraction, so it stops flagging a slot as
  "crowded" mid-draft just because cheap cards are drafted first.

### Fixed

- The deck-review panel could stay on screen over the main menu and inside Battlegrounds. With
  a redraft left unfinished the client keeps reporting `EDITING_DECK` on other screens, so the
  session-state gate alone was not enough; the watcher now gates on the active SCENE first, and
  fails permissive if the scene cannot be read. Two claims in the code about this phase were
  also wrong and are corrected: the deck does not always read 30/30 and does not always refuse
  to shrink — the client reports both forms across sessions.
- The overlay never logged `overlay shown` / `overlay hidden`: `Show()` already leaves the window
  visible, so the transition check could not fire — exactly the two lines needed to diagnose a
  "why is it still on screen" report.

### Added (infrastructure)

- `HdtArenaHelper.Numerics`: the ridge solver and the statistics extracted into a library with
  no HDT/HearthDb reference, plus `HdtArenaHelper.Numerics.Tests` — the one suite that runs on a
  machine without HDT installed. Also `HdtArenaHelper.Training.Tests`, covering trainer
  behaviour that was documented as load-bearing but untested: the weight-rounding floor that
  makes "removing a feature needs no runtime change" true, the `metrics.json` format (LF only,
  invariant decimals), and the shrink derivation's clamping and NaN refusal.
- Retrain tooling: the trainer snapshots what it fetched, so a fit can be reproduced or
  re-run entirely offline (`-- --offline`) without hitting a public endpoint again; the
  ridge penalty is now chosen by cross-validation grouped by card instead of a fixed
  constant; per-coefficient standard errors are printed beside each weight; and the model is
  measured on the population it actually serves (cards with little win-rate data) rather
  than only on the well-sampled ones it is fitted on. The weekly retrain PR is gated on a
  `metrics.json` the trainer writes, and CI now runs the golden tests inside the retrain job
  (a PR opened with the default token never triggers them otherwise). Findings and the open
  questions are recorded in `HdtArenaHelper.Training/REPORT.md`.

- In-plugin self-update: on load (throttled to once a day) the plugin checks the
  project's public GitHub releases and, when a newer one exists, downloads the
  bundled DLL and stages it via a rename swap that applies on the next HDT
  restart — no external updater process. Toggleable from the Plugins menu, with a
  manual "Check for updates now" and a one-click fallback to the releases page if
  the automatic swap can't be applied.

## [0.1.1] - 2026-07-24

### Added

- The overlay now explains itself: the dominant synergy reason appears under the
  option label ("fills the 3-drop gap", "Murloc synergy", "too many weapons" —
  marked "(exp.)": the synergy rules are experimental, not validated like the
  win-rate signal),
  and low-confidence scores — no win-rate sample of at least 200 games behind
  them — get a dimmed, starred label so they stop looking as authoritative as
  well-sampled ones.
- Firestone as a second runtime win-rate source (11 public per-class CDN files,
  drawn-win-rate metric, cached per class with independent fail-soft): if either
  endpoint becomes unavailable, the other carries the score alone.
- Class-context scoring: once the hero is picked, cards are rated from the drafted
  class's own bucket (shrunk toward a leave-that-class-out prior, on the same scale
  as the class-agnostic scores), falling back to the global rate where class data is
  thin or missing. Validated cross-source before shipping: the class estimator beats
  the global one at predicting the other source's class rates (Spearman 0.73 vs 0.53).
- Post-loss redraft support (Underground and Normal arena): redraft picks are
  scored like draft picks, with the run deck plus the redraft cards as the
  synergy context.
- Deck-context synergy engine (`MetadataSynergyEngine`): mana-curve gaps, tribal
  payoffs/members (incl. amalgams), weapon crowding and spell-damage pairing, from
  card metadata only. Deliberately bounded (total clamped to ±3 points) so it
  breaks ties but never overrides the win-rate signal.

### Added (infrastructure)

- Weekly retrain workflow (`train.yml`, Fridays): re-fits the heuristic weights
  against live data and opens a review PR only when they moved.
- Weekly HDT-drift canary (`canary.yml`, Wednesdays): builds and tests against the
  LATEST official HDT release, so a breaking change in the runtime-bound surfaces
  (HearthMirror, ArenaPlaque, HearthDb) is caught before users hit it.

### Fixed

- The overlay now only shows during actual draft states: arena choices linger in
  the client's memory on other screens (landing page, mid-run), and the overlay
  would have painted plaques over them. Partial choice lists exposed mid-animation
  are ignored too.
- Robustness fixes from an independent review pass: Firestone publishes data and
  completeness atomically (no window rendering against a stale partial bundle), an
  unusable cached class file is dropped and re-downloaded instead of wedging the
  class until the TTL expires, cache files are swapped atomically (no torn reads),
  a refresh purges every cache file even if one is locked, and the warm-up
  supersession guard is race-free.

### Changed

- Per-card precision weighting in the blend: a source's weight is now scaled by
  the sample behind its estimate for that card (the same n/(n+k) factor the
  shrinkage uses), so a 5000-game estimate no longer averages 50/50 against a
  30-game one. The per-source breakdown exposes the effective weight and sample
  size, surfaced in the overlay as the low-confidence flag.
- Heuristic weights re-fit with the training target switched to the drawn win-rate
  (the same, less deck-confounded metric the runtime win-rate sources use), and the
  display anchor now ships inside `arena_weights.json` (`anchor_median_raw`, measured
  by the trainer) instead of being hardcoded.
- The overlay now renders as soon as at least one data source has loaded, instead
  of waiting for all of them.
- Statistical scoring (shrinkage + median/MAD logistic) extracted into a policy
  shared by all win-rate sources, keeping their scales mutually calibrated.

## [0.1.0] - 2026-07-24

First release.

### Added

- Live Arena draft detection: reads the three offered cards during an Arena /
  Underground Arena draft via HDT's HearthMirror path.
- Multi-source blended **0–100 score** per card, with a per-source breakdown:
  - Real arena win-rates from HSReplay's free public arena endpoint (primary
    signal), cached daily.
  - An offline metadata heuristic whose weights are ridge-fit against real
    win-rates (embedded `arena_weights.json`), used as a fallback for cards the
    win-rate data hasn't covered.
- Native HDT `ArenaPlaque` overlay covering the card draft, the hero pick, and
  the Underground Arena legendary-group pick (cumulative group scoring).
- Class tier list at the hero pick, derived from per-class win-rates, so the
  overlay doubles as a class picker.

### Notes

- Uses only free, public data — no paid subscription, no scraping of paywalled
  services, no bundled third-party data.
- The synergy engine is designed but not yet implemented (a `NullSynergyEngine`
  placeholder is wired in).

[0.1.4]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/dokson/HdtArenaHelper/releases/tag/v0.1.0
