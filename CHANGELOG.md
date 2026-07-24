# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.1.1]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/dokson/HdtArenaHelper/releases/tag/v0.1.0
