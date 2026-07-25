# HdtArenaHelper.Training

Fits the plugin's offline heuristic weights against real arena win-rates and writes
`arena_weights.json`, which is embedded into the plugin DLL at build time. C# only — no Python.

**[`REPORT.md`](./REPORT.md) is the single source of truth for every data-science conclusion about
training and card scoring** — validation protocols, measured scores, what was tried and rejected,
and the open questions. Read it before changing the model, and record new findings there rather
than here or in `AGENTS.md`.

## Re-fitting the weights (per patch / rotation)

```powershell
dotnet run --project HdtArenaHelper.Training -c Release              # live fetch + snapshot
dotnet run --project HdtArenaHelper.Training -c Release -- --offline # refit from the snapshot
```

The weights describe the **current card pool**, not universal keyword values, so re-fit every
rotation and patch. `--offline` re-runs the fit on the last snapshot with no network, which is what
makes a fit reproducible and lets you iterate on cross-validation, bootstrap and holdouts without
hitting a free public endpoint again.

## What a run does

1. Fetches HSReplay's free arena endpoint (via `curl` — Cloudflare 403s .NET's own TLS stack) plus
   the 11 Firestone per-class CDN files, snapshotting both.
2. Builds `(card, class)` rows from the class-centered **drawn** win-rate averaged over the two
   sources, and cross-checks the hero-pick tier ranking of one source against the other.
3. Drops redundant columns and the `kw_*`/`tx_*` indicators below the support floor, then selects
   the ridge penalty by cross-validation **grouped by card** — a neutral card contributes one row
   per class, so a random row split would leak.
4. Evaluates on the population the model actually serves (the lowest-`games` decile, and
   leave-one-set-out), not only on random folds. This distinction matters more than the fit itself;
   `REPORT.md` explains why.
5. Bootstraps 300 card-resampled refits for per-coefficient standard errors, printed beside each
   weight so a meaningless coefficient reads as `-2.92 ± 1.73` instead of having to be intuited.

It reuses **`HeuristicArenaDataSource.BuildFeatures`**, so training and inference share one feature
definition and cannot drift.

## Outputs

| file | committed? | what |
|---|---|---|
| `arena_weights.json` | **yes** | the model the plugin embeds; also records `fit_alpha`, `fit_rows`, `fit_cards` |
| `arena_weights.generated.json` | no | this run's fit, to diff before adopting |
| `metrics.json` | no | machine-readable run summary; the CI retrain gate reads this |
| `.snapshot/` | no | the fetched payloads, so a fit can be reproduced offline |

## Adopting a re-fit

1. Review the printed weight diff and the per-coefficient standard errors.
2. Copy `arena_weights.generated.json` over `arena_weights.json`.
3. Paste the printed golden scores into `HeuristicArenaDataSourceTests` — this manual step is the
   deliberate tripwire that a human looked at the new weights.
4. Rebuild and run the tests.

`train.yml` does this weekly and opens a PR only when the refit changes enough recommendations to
be worth a reviewer's time. It runs the golden tests inside the job, because a PR opened with the
default token never triggers them. What that threshold is — and why it is deliberately not the
statistically ideal one — is in `REPORT.md`.

## Honest summary of the model

The heuristic is a **backstop** for cards the win-rate feeds have no data for, blended at weight
0.5 against their combined 1.0. Its cross-validated within-class rank correlation is **~0.20**, and
on the thinnest-sampled cards — the ones it exists for — it measures no better than predicting a
constant. That is why the blend shrinks a model-only score toward neutral rather than trusting it,
and why its weight must not be raised to compensate. The measurements, and three attempts to
improve it that all measured worse, are in `REPORT.md`.
