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

Before opening a PR, run the whole gate instead — same checks as CI, in the same order:

```powershell
./scripts/gate.ps1              # build, format, all three test suites, slopwatch, offline refit
./scripts/gate.ps1 -SkipRefit   # without the slow last step
```

A local build auto-installs the plugin into `%AppData%\HearthstoneDeckTracker\Plugins\`,
so you can restart HDT and try it immediately. Point at a non-default HDT install with
`/p:HSDTPath="C:\path\to\HDT\app-1.53.x"`.

## Code style

Style is enforced by [`.editorconfig`](.editorconfig): **tabs** in C# (matching HDT's own
code), spaces in project/config files, LF line endings, no trailing whitespace (except
Markdown). Keep comments minimal — explain *why*, not *what*.

Most of it is enforced at BUILD time (warnings are errors), with one exception worth knowing:
**using order is not a Roslyn diagnostic**, so a scrambled using block compiles perfectly. It is
checked by `dotnet format --verify-no-changes` instead, which CI runs and `scripts/gate.ps1`
includes. `System` first, then everything else alphabetically, in one block.

### Pre-commit hook (recommended)

A shared git hook in [`.githooks/`](.githooks) checks these rules on staged changes only:
it auto-fixes trailing whitespace and blocks CRLF / wrong indentation. Existing code is
left untouched. Enable it once:

```sh
git config core.hooksPath .githooks
```

Bypass a single commit with `git commit --no-verify` if needed.

## Data & ethics

The plugin uses only data it is **entitled to use** — currently the HSReplay public arena
endpoint, plus the card metadata already shipped with HDT. "Publicly reachable" is **not** the
same as licensed, and this project has learned that the expensive way: a provider asked us to
stop using its feed and the source was removed the same day, costing a second opinion on every
card. So: absent a stated licence the default is no permission, **ask before adding a source**
and record the answer, and keep every source individually droppable with the offline model as
the backstop. Do **not** add code that scrapes a paywalled service or bundles/redistributes
anyone's data. Network code must cache and fail soft; note that for
hsreplay.net a browser User-Agent alone is NOT enough (Cloudflare fingerprints the TLS stack)
— the runtime shells out to `curl`. Full policy: [AGENTS.md](./AGENTS.md#data-sources--ethics).

## The card pool, in the repo

`docs/hearthstone-cards.md`, `docs/hearthstone-hero-powers.md` and `docs/hearthstone-heroes.md` (to
grep) and `Generated/HSDatabase.g.cs` (compiled by the three test projects) are a generated copy of
every collectible card, hero and hero power. Regenerate them whenever HDT is bumped:

```powershell
dotnet run --project HdtArenaHelper.Training -c Release -- --dump-database
```

A test fails if you forget: the committed files are diffed byte-for-byte against the generator.

Cards, hero powers and heroes are kept apart on purpose — `HSCard`, `HSHeroPower`, `HSHero` — because
"Icy Touch" is both a spell and a hero power, and one shared list gave the name to the wrong one.

Two rules around it. **Test fixtures name their cards** — `HSCard.Tuskpiercer`, never `"BAR_330"`
with a comment saying which card that is, because the comment is not checked and one of them was
wrong for a long time. And on licensing: this data is **Blizzard's**, reproduced as non-commercial
fan content (see the README's License section) — HearthDb's MIT licence covers its code, not the
cards. Adding fields is a decision, not a freebie.

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
- Make sure `./scripts/gate.ps1` reports **GATE PASS**.
- Describe what changed and why. For scoring changes, mention how you validated them.
