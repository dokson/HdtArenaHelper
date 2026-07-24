# HDT Arena Helper

[![build](https://github.com/dokson/HdtArenaHelper/actions/workflows/build.yml/badge.svg)](https://github.com/dokson/HdtArenaHelper/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/dokson/HdtArenaHelper?sort=semver)](https://github.com/dokson/HdtArenaHelper/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)


An open-source **Arena draft-helper plugin for [Hearthstone Deck Tracker](https://hsdecktracker.net/)** (HDT).
During an Arena / Underground Arena draft it reads the three offered cards, scores each one,
and shows a single **0–100 score** per card in an overlay — using only free, public data,
with no subscription required.

> **Status: work in progress.** Data pipeline, draft detection, multi-source blend and
> overlay are in place. The synergy engine is designed but not yet implemented.

## Features

- Detects the three offered cards live during the draft via HDT's HearthMirror path.
- Scores each card with a single blended 0–100 number, plus a per-source breakdown.
- Uses real, free arena win-rate data (HSReplay's public arena endpoint), cached daily.
- Falls back to an offline metadata heuristic for cards the win-rate data hasn't covered.
- At the hero pick, ranks the offered classes from the per-class win-rates.
- No paid subscription, no scraping of paywalled services, no bundled third-party data.

## How the score works

```
finalScore = weightedMean( each source's normalized 0–100 score )  +  synergyBonus
```

Real arena win-rate is the primary signal; the offline heuristic is a weak fallback.
An honest caveat: card *metadata* predicts arena win-rate only weakly, so the heuristic
is a backstop — when real win-rate data exists for a card, that drives the score.

## Requirements

- Windows, with Hearthstone Deck Tracker (a `net472` build) installed.
- .NET SDK 8+ for development (HDT itself runs on .NET Framework 4.7.2, x64).
- **The repository contains no HDT binaries.** Build references resolve from your local
  HDT install (auto-discovered) or, on CI, a pinned official HDT release package.

## Install in HDT

1. Download the latest `HdtArenaHelper-<version>.zip` from the
   [Releases](https://github.com/dokson/HdtArenaHelper/releases) page — **not** the source zip.
2. Extract the `HdtArenaHelper` folder into `%AppData%\HearthstoneDeckTracker\Plugins\`.
3. In HDT: **Options → Tracker → Plugins**, enable **Arena Helper**.
4. Start an Arena draft — the overlay appears over the three offered cards.

Use the **Plugins → Arena Helper** menu to toggle the overlay or refresh cached data.

## Build and test

HDT must be installed; `HSDT.props` auto-discovers it under
`%LocalAppData%\HearthstoneDeckTracker\app-*`.

```powershell
dotnet build HdtArenaHelper.sln -c Release
dotnet test  HdtArenaHelper.Tests\HdtArenaHelper.Tests.csproj
```

Point at a specific install with `/p:HSDTPath="C:\path\to\HDT\app-1.53.x"`. A local
(non-CI) build also auto-installs the DLL into
`%AppData%\HearthstoneDeckTracker\Plugins\HdtArenaHelper\`.

The referenced HDT assemblies (`HearthstoneDeckTracker.exe`, `HearthMirror.dll`,
`HearthDb.dll`, `Newtonsoft.Json.dll`) are `Private=False` — bound at runtime inside HDT,
never redistributed.

## Releases (CI)

`.github/workflows/build.yml` builds and tests on `windows-latest`, resolving the HDT
reference assemblies from a pinned official release
(`scripts/resolve-hdt.ps1`). Pushing a `v<version>` tag that matches `Version.props`
packages `HdtArenaHelper-<version>.zip` and publishes a GitHub release. The zip contains
only the plugin DLL:

```text
HdtArenaHelper/
  HdtArenaHelper.dll
```

## License

[MIT](./LICENSE).

---

*Fan project. Hearthstone is a trademark of Blizzard Entertainment, Inc. Not affiliated
with or endorsed by Blizzard or any data provider.*
