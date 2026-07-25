<img src="docs/hdt-logo.svg" alt="Hearthstone Deck Tracker" width="80" align="right" />

# HDT Arena Helper

[![build](https://github.com/dokson/HdtArenaHelper/actions/workflows/build.yml/badge.svg)](https://github.com/dokson/HdtArenaHelper/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/dokson/HdtArenaHelper?sort=semver)](https://github.com/dokson/HdtArenaHelper/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![Hearthstone Deck Tracker](https://img.shields.io/badge/plugin%20for-Hearthstone%20Deck%20Tracker-2c7ce6)](https://hsdecktracker.net/)

[![data: HSReplay](https://img.shields.io/badge/data-HSReplay-1d9bf0)](https://hsreplay.net/)
[![data: Firestone](https://img.shields.io/badge/data-Firestone-e8873a)](https://www.firestoneapp.com/)
[![data: HearthDb](https://img.shields.io/badge/data-HearthDb-6e7681)](https://github.com/HearthSim/HearthDb)

**A free, open-source Arena draft-helper plugin for [Hearthstone Deck Tracker](https://hsdecktracker.net/).**
During an Arena or Underground Arena draft it reads the three offered cards and shows a
single blended **0–100 score** for each, right in an overlay over the client — no
subscription, no paywalled data, no account required.

> **Status: work in progress.** Draft detection, data pipeline, scoring and the overlay are
> live and verified on a real HDT client. Two independent win-rate sources, class-context
> scoring and a bounded deck-synergy engine are in — see [Roadmap](#roadmap) for what's next.

![The class tier list at the hero pick](docs/screenshot-hero.png)

![The overlay scoring an Underground Arena legendary-group pick](docs/screenshot.png)

## Why this plugin

Most arena tier-list tools are either a browser tab you tab out to mid-draft, or a paid
overlay behind a subscription. HDT Arena Helper scores every pick **in the client, in
real time**, using only data that's free and public:

- **No subscription.** Real arena win-rate data comes from two independent free public
  sources (HSReplay and Firestone), blended — if one goes dark, the other carries the score.
- **No blank picks.** An offline heuristic (fit from real win-rate data, not hand-tuned
  keyword bonuses) backstops cards the win-rate source hasn't seen yet.
- **No separate window.** The score renders using HDT's own native `ArenaPlaque` visuals,
  scaled to the client — it looks like part of the tracker, not a bolt-on.
- **No re-scraping abuse.** Data is cached with a 1-day TTL; nothing is hammered or
  redistributed.

## Features

- **Live draft detection** — reads the three offered cards (and, in Underground Arena, the
  legendary + package groups) as you draft, via HDT's HearthMirror path.
- **One blended score per card**, 0–100, with a per-source breakdown available:
  - Real arena win-rates from HSReplay's free public arena endpoint AND Firestone's
    public per-class CDN, blended at equal weight.
  - Scored **in your class's context** once the hero is picked (per-class data, falling
    back to the global rate where the class sample is thin).
  - An offline metadata heuristic as a fallback, with weights fit by ridge regression
    against real win-rate data — not hand-tuned.
- **Deck-aware synergy (experimental)** — a small, deliberately bounded bonus from the
  cards you've already drafted: curve gaps, tribal payoffs/members, weapon crowding,
  spell damage. It breaks ties between comparable cards; it never overrides the
  win-rate signal. Experimental because these rules cannot be validated against public
  data — the overlay marks its reasons "(exp.)" accordingly.
- **Class / hero picker** — at the hero pick, classes are ranked from the same win-rate
  data, so the overlay doubles as a tier list for your next hero.
- **Underground Arena support** — scores the legendary-group pick as the average quality
  of the four cards it adds.
- **Native look** — hosts HDT's own `ArenaPlaque` control in a scalable overlay, so it
  resizes and DPI-corrects automatically with the game window.
- **Toggleable** — enable/disable and refresh cached data from HDT's Plugins menu; your
  existing native Arenasmith overlay preference is preserved and restored.

## How the score works

```
finalScore(card) = weightedMean( each source's normalized 0–100 score )  +  synergyBonus
```

Each data source normalizes its own metric (win-rate %, heuristic points, …) onto a common
0–100 scale so they can be blended fairly. Real arena win-rate is the primary signal; the
offline heuristic is a deliberately weak backstop — card *metadata* alone predicts arena
win-rate only loosely, so whenever real win-rate data exists for a card, it drives the score.

## Install

1. Grab the latest `HdtArenaHelper-<version>.zip` from
   [Releases](https://github.com/dokson/HdtArenaHelper/releases) — **not** the source zip.
2. Extract the `HdtArenaHelper` folder into `%AppData%\HearthstoneDeckTracker\Plugins\`.
3. In HDT: **Options → Tracker → Plugins**, enable **Arena Helper**.
4. Start an Arena (or Underground Arena) draft — the overlay appears over the offered
   cards automatically.

Use the **Plugins → Arena Helper** menu in HDT to toggle the overlay or force a data refresh.

## Requirements

- Windows, with Hearthstone Deck Tracker installed (a `net472` build).
- No HDT binaries are bundled — the plugin binds to your existing HDT install at runtime.

## FAQ

**Does this need a paid subscription (Arenasmith, HSReplay Premium, etc.)?**
No. All scoring comes from HSReplay's and Firestone's free public arena data plus an
offline heuristic; nothing paywalled is used or required.

**Does it replace HDT's native Arenasmith overlay?**
It suppresses HDT's native overlay while active so the two don't stack, and restores your
original preference when you disable the plugin — nothing is lost.

**Does it update itself?**
Yes. On startup (at most once a day) it checks this repo's GitHub releases and, if a
newer one exists, downloads it and stages it for the next time you restart HDT — no
manual re-download. You can turn this off, or trigger a check by hand, from
**Plugins → Arena Helper**. It only ever fetches from this project's official
releases over HTTPS.

**The overlay doesn't show up over Hearthstone.**
Run Hearthstone in **Windowed** or **Borderless Windowed** mode (Options → Graphics).
In exclusive Fullscreen, Windows composites the game above every other window, so no
overlay — HDT's own included — can draw over it.

**Why is a card unscored / showing a lower-confidence score?**
Very new or very low-sample cards fall back to the offline heuristic (a weaker signal by
design) until enough real arena games exist for a shrinkage-adjusted win-rate.

**Does it consider my drafted deck (synergies, curve, tribes)?**
Yes, conservatively: curve gaps, tribal payoffs/members, weapon crowding and spell-damage
pairing add a bonus clamped to a few points. It's deliberately small — these rules can't
be validated against public data the way the win-rate signal can, so they nudge close
calls rather than decide picks.

## Roadmap

- [x] Deck-synergy engine — a +/- bonus from the cards already drafted (tribal payoffs,
      curve fit, anti-synergy), on top of the win-rate/heuristic blend.
- [x] Firestone's public arena data as a second runtime win-rate source.
- [ ] Live verification pass of class-context scoring and synergy on a real draft.

Have an idea or found a bug? Open an [issue](https://github.com/dokson/HdtArenaHelper/issues).

## Contributing

Contributions are welcome — see [CONTRIBUTING.md](./CONTRIBUTING.md) for build/test setup,
code style, and how the heuristic weights are fit and re-trained. Please also read the
[Code of Conduct](./CODE_OF_CONDUCT.md).

## Data sources & credits

| Source | What it provides |
|---|---|
| [HSReplay](https://hsreplay.net/) arena API | Real arena win-rate / popularity per card and class (free, public) |
| HearthDb (bundled with HDT) | Card metadata, used offline for the heuristic and id resolution |
| [Firestone](https://www.firestoneapp.com/) public arena CDN | Real arena win-rate per class: second runtime win-rate source + offline weight fitting |

## Changelog

See [CHANGELOG.md](./CHANGELOG.md) for release notes.

## Security

See [SECURITY.md](./SECURITY.md) to report a vulnerability.

## License

[MIT](./LICENSE).

---

*Fan project. Hearthstone is a trademark of Blizzard Entertainment, Inc. Not affiliated
with or endorsed by Blizzard, HearthSim, HSReplay, Firestone, or any data provider.*
