# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.1.0]: https://github.com/dokson/HdtArenaHelper/releases
