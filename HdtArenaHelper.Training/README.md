# training/

The heuristic's model artifact and its analysis. The weights are fit by the
**`HdtArenaHelper.Training`** C# tool (in the solution) — there is no Python here.

## Files

- **`arena_weights.json`** — the committed model (intercept + per-feature ridge
  coefficients), embedded into the plugin. Single source of truth for the heuristic.
- `arena_weights.generated.json` — the trainer's latest output (git-ignored), compared
  against the committed file.
- `REPORT.md` — the original analysis: how the model was chosen and why the metadata
  heuristic is only a weak signal.

## Re-fitting the weights (per patch / rotation)

```powershell
dotnet run --project HdtArenaHelper.Training -c Release
```

The trainer fetches the public HSReplay + Firestone arena win-rates (via `curl`, to get
past Cloudflare — .NET's own HTTP stack is blocked), builds a class-centered dual-source
target, and fits the same ridge model the plugin scores with — **reusing
`HeuristicArenaDataSource.BuildFeatures`, so training and inference share one feature
definition and cannot drift.** It writes `arena_weights.generated.json` and prints a
weight-by-weight diff against the committed file.

To adopt a re-fit: review the diff, replace `arena_weights.json` with the generated file,
rebuild, and update the golden values in `HeuristicArenaDataSourceTests` (run the tests;
each failing assertion prints the new score). The heuristic is a deliberately weak
fallback (~0.27 out-of-fold Spearman vs real win-rates, against a ~0.5 two-source
agreement ceiling), so expect only small moves.
