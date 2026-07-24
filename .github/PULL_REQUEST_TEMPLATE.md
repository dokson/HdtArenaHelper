## What changed

Briefly describe what this PR does and why. For scoring changes, mention how you
validated them.

## Checklist

- [ ] `dotnet build HdtArenaHelper.sln -c Release` is warning-free.
- [ ] `dotnet test HdtArenaHelper.sln -c Release` is green.
- [ ] Follows the `.editorconfig` style (tabs in C#, LF, no trailing whitespace).
- [ ] If `arena_weights.json` changed, it was regenerated via `HdtArenaHelper.Training/` and the weight diff was reviewed.
- [ ] No HDT source code copied or redistributed; HDT assemblies remain runtime-bound.
- [ ] No paid, scraped, or proprietary third-party data added (free public data only).
