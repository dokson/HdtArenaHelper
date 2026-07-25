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

It exists to be **a free alternative to the tracker's own freemium arena assistant**, with a
scoring algorithm of its own: open, documented, and fed by more than one data provider.

> **Status: usable, and still moving.** Draft detection, the data pipeline, scoring and the
> overlay are live and verified on a real HDT client, including the post-loss redraft. Next up is
> following you out of the draft and into the game — see [Roadmap](#roadmap).

![The class tier list at the hero pick](docs/screenshot-hero.png)

![The overlay scoring an Underground Arena legendary-group pick](docs/screenshot.png)

## Why this plugin

Hearthstone Deck Tracker is excellent, and its arena assistant is one of its best features —
but it is **freemium**: the draft scores stop when the trial does. Everything else in this
space is either a browser tab you alt-tab to mid-draft, or another subscription. This plugin
does that job **in the client, in real time, for free and forever**, from data anyone can
fetch.

**Free alternative, not a clone.** The scoring is ours, and deliberately not single-sourced.
HSReplay is the natural arena data provider — it is also made by the same team as the tracker,
so a helper built on it alone inherits one provider's view of the format. This plugin blends
**two independent free win-rate sources** (HSReplay and Firestone) as one consensus signal, so
no single provider decides the number, and if either goes dark the other keeps the score alive.
Adding further public sources is an explicit goal, not an afterthought.

**Built for the player who does not follow the meta.** You should not need to have read a tier
list, memorised the current pool, or know which tribe a class is quietly full of, to draft
reasonably. The plugin turns all of that into one number per card, and tells you *why* when
something moved it — the mana slot you are short of, the tribe you have no members for, the
sample size behind the estimate.

**"Objective" in a specific, checkable sense**, which is the part we care about most:

- every score traces back to **published arena win-rate statistics** — real games won and
  lost — never to anyone's opinion of a card, ours included;
- the offline fallback's weights are **fit by regression against those win-rates**, never
  hand-tuned. Hand-tuned keyword bonuses were tried and measured *worse* than nothing;
- where a rule **cannot** be validated against public data (deck synergy) it is bounded so it
  can only break ties, and the overlay marks it `(exp.)`;
- the method, the measurements and the **failures** are written down in
  [`REPORT.md`](./HdtArenaHelper.Training/REPORT.md), including several ideas that were tried
  and scored worse. You can audit the number instead of trusting it.

To be equally clear about what this is **not**: the paid assistant has data we do not have, and
we have never benchmarked against it. Nothing here claims to be more accurate than it — only
free, transparent, and honest about its limits.

Also, by construction:

- **No blank picks.** An offline heuristic (fit from real win-rate data, not hand-tuned
  keyword bonuses) backstops cards the win-rate sources haven't seen yet.
- **No separate window.** The score renders using HDT's own native `ArenaPlaque` visuals,
  scaled to the client — it looks like part of the tracker, not a bolt-on.
- **No re-scraping abuse.** Data is cached with a 1-day TTL; nothing is hammered, scraped from
  behind a paywall, or redistributed. And "publicly reachable" is not treated as "licensed": each
  source is individually droppable, because whether we may use one is the provider's call, not
  ours.

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
- **Deck-aware synergy (experimental)** — a bounded +/- from what you already drafted: curve
  gaps, tribal payoffs, weapon and location crowding, spell damage. It breaks ties, never
  overrides a win-rate. Marked `(exp.)` in the overlay because no public data can validate it.
  One exception is allowed to reorder a close pick: a card that is structurally **dead** (a
  tribal payoff with none of its tribe), weighted by how much of your class's deck that tribe
  normally holds.
- **Class / hero picker** — at the hero pick, classes are ranked from the same win-rate data,
  with each class's **estimated arena win-rate in real percentage points** under the plaque.
- **Underground Arena support** — scores the legendary-group pick as the average quality of the
  four cards it adds, and during a post-loss **redraft** shows your whole deck as a scored,
  full-height list so the cards to cut are obvious.
- **Self-updating** — checks this repo's public releases once a day and stages the new build for
  the next restart, keeping the previous one as a one-file rollback.
- **Native look** — hosts HDT's own `ArenaPlaque` control in a scalable overlay, so it
  resizes and DPI-corrects automatically with the game window.
- **Toggleable** — enable/disable and refresh cached data from HDT's Plugins menu; your
  existing native Arenasmith overlay preference is preserved and restored.

## How the score works

```
finalScore(card) = weightedMean( each source's normalized 0–100 score )  +  synergyBonus
```

Each source normalizes its own metric onto a common 0–100 scale, so they blend fairly. Real
arena win-rate drives the score; the offline heuristic is a deliberately weak backstop, because
card metadata alone predicts win-rate only loosely. Two consequences, both measured rather than
assumed: a source's weight shrinks with its sample size for that card, and a card **no** feed has
seen is pulled toward the middle instead of asserting a confident number.

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
No — that is the point of the project. Scoring comes from HSReplay's and Firestone's free public
arena data plus an offline heuristic. Nothing paywalled is used, required, or scraped.

**Does it replace HDT's native Arenasmith overlay?**
It suppresses HDT's native overlay while active so the two don't stack, and restores your
original preference when you disable the plugin — nothing is lost.

**Does it update itself?**
Yes: at most once a day it checks this repo's releases and stages a newer build for your next
HDT restart, keeping the previous one as a rollback. Toggle it, or check by hand, from
**Plugins → Arena Helper**. It fetches only from this project's official releases, over HTTPS.

**The overlay doesn't show up over Hearthstone.**
Run Hearthstone in **Windowed** or **Borderless Windowed** mode (Options → Graphics).
In exclusive Fullscreen, Windows composites the game above every other window, so no
overlay — HDT's own included — can draw over it.

**Why is a card unscored / showing a lower-confidence score?**
Very new or very low-sample cards fall back to the offline heuristic (a weaker signal by
design) until enough real arena games exist for a shrinkage-adjusted win-rate.

**Does it consider my drafted deck (synergies, curve, tribes)?**
Yes, conservatively: the bonus is clamped to a few points, so it nudges close calls rather than
deciding picks — these rules cannot be validated against public data the way a win-rate can. The
one exception is a card that is structurally dead in your deck, which may reorder a close pick.

## Roadmap

What shipped is in the [changelog](./CHANGELOG.md). Next, in **0.1.3** — the helper follows you
out of the draft and into the game:

- [ ] **Discover / card choices in game.** The same scoring engine, applied to the cards a
      Discover offers. Both the choices and their on-screen positions are readable, so this is
      mostly detection plus a layout.
- [ ] **Mulligan guidance** — keep/replace win-rate and keep rate per card, computed from the
      mulligan counters Firestone already publishes. Single-source and thin-sampled, so it needs
      the same shrinkage the card scores use; it will be presented as the estimate it is.
- [x] **Board awareness, stated rather than scored.** A Discover now says when a card is
      unplayable this turn, when the board has no room for a minion, and when a full hand would
      destroy the card outright. The score itself is untouched: the rules make those facts
      objective, but nothing public says what they are worth in points, and inventing a value is
      the one thing this project has already measured to be worse than nothing.
- [ ] **Judgement calls that go beyond the rules** ("I am behind, I want removal"). Still open, and
      still bounded and experimental if it ever lands — there is no public per-game dataset to fit
      it against.

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

Both feeds are scoped to **Underground Arena** and to recent games (HSReplay reports a 4-day
window, Firestone the current patch). So no pre-patch games dilute a score — and if you play normal
Arena, the numbers still come from Underground games, which is a real caveat rather than a detail.

## Changelog

See [CHANGELOG.md](./CHANGELOG.md) for release notes.

## Security

See [SECURITY.md](./SECURITY.md) to report a vulnerability.

## License

[MIT](./LICENSE).

---

*Fan project. Hearthstone is a trademark of Blizzard Entertainment, Inc. Not affiliated
with or endorsed by Blizzard, HearthSim, HSReplay, Firestone, or any data provider.*
