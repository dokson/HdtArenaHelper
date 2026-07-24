# Contributing

Thanks for helping improve HDT Arena Helper. This is an open-source, MIT-licensed plugin
for [Hearthstone Deck Tracker](https://hsdecktracker.net/).

## Prerequisites

- **Windows** with Hearthstone Deck Tracker installed (a `net472` build).
- **.NET SDK 8+** (the plugin itself targets .NET Framework 4.7.2, x64).

The repository ships **no HDT binaries**; the build resolves them from your local HDT
install via `HSDT.props` (auto-discovers `%LocalAppData%\HearthstoneDeckTracker\app-*`).

## Build & test

```powershell
dotnet build HdtArenaHelper.sln -c Release
dotnet test  HdtArenaHelper.sln -c Release
```

A local build auto-installs the plugin into `%AppData%\HearthstoneDeckTracker\Plugins\`,
so you can restart HDT and try it immediately. Point at a non-default HDT install with
`/p:HSDTPath="C:\path\to\HDT\app-1.53.x"`.

## Code style

Style is enforced by [`.editorconfig`](.editorconfig): **tabs** in C# (matching HDT's own
code), spaces in project/config files, LF line endings, no trailing whitespace (except
Markdown). Keep comments minimal — explain *why*, not *what*.

### Pre-commit hook (recommended)

A shared git hook in [`.githooks/`](.githooks) checks these rules on staged changes only:
it auto-fixes trailing whitespace and blocks CRLF / wrong indentation. Existing code is
left untouched. Enable it once:

```sh
git config core.hooksPath .githooks
```

Bypass a single commit with `git commit --no-verify` if needed.

## Data & ethics

The plugin uses **only free, public data** (the HSReplay and Firestone public arena
endpoints) and card metadata already shipped with HDT. Do **not** add code that scrapes a
paywalled service or bundles/redistributes anyone's proprietary tier data. Network code
must cache and fail soft; note that for hsreplay.net a browser User-Agent alone is NOT
enough (Cloudflare fingerprints the TLS stack) — the runtime shells out to `curl`.

## The heuristic weights

The offline heuristic's weights are produced by the weight-fitting pipeline in
[`HdtArenaHelper.Training/`](HdtArenaHelper.Training/) and embedded as `arena_weights.json`
(single source of truth — no coefficients are hardcoded). They describe the current card
pool, so they are **re-fit weekly by the scheduled `retrain` workflow**
(`.github/workflows/train.yml`), which opens a PR with the weight diff when they moved —
check for an open bot PR before re-fitting by hand. Either way, review the weight diff
before merging (a data blip should not silently ship) and update the golden tests from
the trainer's printed values. See `HdtArenaHelper.Training/README.md`.

## Pull requests

- Keep changes focused; match the surrounding style.
- Make sure `dotnet build` is warning-free and `dotnet test` is green.
- Describe what changed and why. For scoring changes, mention how you validated them.
