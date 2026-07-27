# Arena card-value heuristic — final results (2026-07, arena-underground patch)

> **2026-07-24 re-fit note.** The committed `arena_weights.json` was re-fit with the
> target metric switched from deck-inclusion win-rate to **drawn win-rate** (the same,
> less deck-confounded metric the runtime win-rate sources score with — see the
> statistical review of `HsReplayArenaDataSource`). The trainer now also measures the
> draftable pool's median raw score and ships it as `anchor_median_raw` (display anchor),
> and prints the golden-test scores to paste on adoption. The analysis below documents
> the ORIGINAL model-selection study, which used the deck-inclusion target: the
> methodology conclusions (class-centered target, ridge ≥ GBM, noise ceiling) carry
> over; the exact rho values refer to that study.

## Data
- HSReplay `arena/card_stats/free` (ALL + 11 class buckets), dedup by `card_id` keeping max `num_games`.
- HearthstoneJSON `latest/enUS/cards.json` (join on `id`).
- Model artifact: `arena_weights.json`, produced by the `HdtArenaHelper.Training` C# tool.

## Central methodological finding
HSReplay `win_rate` = win-rate of the DECKS that include the card. In the ALL bucket it is dominated
by class strength, not by card quality:
- Ridge without class: Spearman OOF 0.253. Adding only the class dummies: **0.800**.
- For a draft helper (comparing 3 cards for the SAME class) that signal is useless/misleading.
- Honest target: win-rate **centered on the class mean** (rows (card, class) from the class
  buckets, `num_games >= 100`, GroupKFold by card to avoid leakage from neutrals).

## Noise ceiling
The target itself is noisy — a card's measured arena win-rate over a few hundred games carries
~3pp of standard error — so a model correlating with it is chasing a moving quantity. Measured
against an independent estimate of the same per-class target, the ceiling sat at mean Spearman
**0.469** (per-row 0.533): no model on metadata alone can exceed that by much, and a rho of ~0.26
is therefore roughly 55% of what is achievable rather than a failure.

Treat 0.469 as a fixed historical figure: it is a property of the data, but the pipeline no longer
recomputes it, so it does not move with a refit.

## Comparison table — class-centered target, pooled n=2481, GroupKFold(5)
| Heuristic | Spearman (test) | Top-decile P | Notes |
|---|---|---|---|
| H1 vanilla statline | 0.111 | 0.117 | (atk+hp)-(2c+1) |
| H2 +keyword/rarity "lore" weights | 0.101 | 0.093 | hand-tuned weights worsen H1 |
| H3 +text flags hand-tuned weights | 0.097 | 0.073 | ditto |
| H4 interpretable ridge | **0.263** | 0.133 | alpha=10, sqrt(games) weights |
| H4 final (dual-source target, rounded) | **0.266–0.275** | 0.144–0.155 | MAE 2.17pp |
| H5 GBM (reference ceiling) | 0.249 | 0.137 | does NOT beat the ridge |

The interpretable linear model leaves NOTHING on the table versus the GBM: the limit is the
features/target noise, not the model class.

## Table for the original spec (ALL bucket, win_rate, num_games>=500, n=907)
| Heuristic | Spearman | Top-decile P |
|---|---|---|
| H1 | 0.020 | 0.156 |
| H2 | 0.076 | 0.111 |
| H3 | 0.016 | 0.078 |
| H4 ridge OOF | 0.253 | 0.289 |
| H4 + class dummies OOF | 0.800 | 0.456 |
| H5 GBM + class OOF | 0.793 | 0.467 |

Threshold sensitivity (H4 ridge OOF on ALL): >=100 → 0.271 (n=1158); >=500 → 0.253 (n=907);
>=1000 → 0.220 (n=744). On the pooled target: >=50 → 0.261; >=100 → 0.263; >=200 → 0.219.

## Per-class vs ALL (deliverable 4)
- PER-CLASS ridge models (OOF, num_games>=100): mean Spearman **0.184** — worse than the
  pooled global model (0.263): too little data per class, it overfits.
- The uncentered ALL bucket, on the other hand, inflates (0.80) due to class confounding.
- Conclusion: a single global weight set + per-class centered target is the sweet spot;
  real residual per-class variation exists (warrior 0.417 vs shaman 0.063) but the current
  data is not enough to learn it reliably.

## The formula's SHAPE (never its coefficients)

```
raw   = intercept + Σ weight[feature] · value[feature]
shown = clamp(50 + 15 · (raw − anchor_median_raw) / anchor_sigma_raw, 0, 100)
```

Feature definitions: `statline = (atk+hp) − (2·cost+1)` and `stat_per_mana = (atk+hp)/(cost+1)`,
both minions only; `weapon_value = (atk·dur) − (2·cost+1)`, weapons only; the `tx_*` are regex flags
over the card text. All of them live in **`HeuristicArenaDataSource.BuildFeatures`**, which the
trainer reuses so training and inference cannot drift.

**The coefficients are deliberately NOT reproduced here.** This section used to list them, and it
rotted immediately: it kept claiming `is_hero +5.28` when the committed artifact says **−0.08** (a
sign flip on the very coefficient §7 and §10 below spend their length discussing), alongside weights
for features that have since been dropped entirely. A stale formula inside the file designated the
single source of truth is worse than no formula.

The authoritative values are **`arena_weights.json`**, which also records `fit_alpha`, `fit_rows` and
`fit_cards` — how it was actually fit. Note it serializes fewer entries than the fit has features:
`WeightsFile.RoundWeights` drops any `|w| < 0.05`, so a handful of fitted-but-negligible features
are absent by design rather than missing.

## Stability and the deployment population

A re-fit raised a question that turned out to matter more than the weights themselves: the
coefficients move on every refit — 43 of 49 by >= 0.01 between two runs 90 minutes apart — so how
much of that is signal? Findings below, in order of consequence, all reproducible offline with
`dotnet run --project HdtArenaHelper.Training -- --offline`, which refits from the payload
snapshot (added because until then a fit could not be re-run on identical bytes at all).

### 1. `alpha = 10` was not regularizing anything

`A = XcᵀW·Xc + alpha·I` was built with RAW `sqrt(games)` weights, never normalized. Measured:
`mean(diag(A)) = 74166`, so the shrinkage factor was **1.4e-4** and the effective degrees of
freedom **50.0 of 52**. It was unpenalized weighted OLS wearing a ridge's name — the same trap
scikit-learn has, since it does not rescale `sample_weight` either. Fix: normalize the weights to
mean 1, then choose alpha by cross-validation. Side effect worth recording: because the weight sum
grows every week as games accumulate, the effective regularization had been silently drifting
between refits.

### 2. The refit is deterministic; the churn is entirely data

Two refits on byte-identical snapshots produce identical weight files, so the movement is data
change and not nondeterminism (dictionary ordering, Cholesky on a near-singular matrix). This had
to be established before any statistic below meant anything.

### 3. The honest cross-validated score is ~0.20, not 0.27

Under CV grouped **by card** — a neutral card contributes one row per class, so a random row split
leaks — with Spearman computed **within each class bucket** and averaged, because the product
decision is "rank three cards of one class", the score is **0.1924** at 52 features. The
previously reported ~0.27 was measured differently (pooled, not card-grouped) and was optimistic.
The alpha curve is flat from 0.03 to 100 and then declines; the 1-SE rule picks 300.

### 4. Half the features were noise, and removing them helped

Coefficient drift scales as 1/sqrt(support): features on fewer than 40 rows drifted 0.133 on
average versus 0.019 above 40, and the model's LARGEST coefficients sat on its smallest supports
(`kw_forge` −2.92 from **3** cards, `kw_magnetic` −2.45 from 4, `tx_silence` +3.30 from 6).

Power calculation for the threshold: detecting an effect worth a column (~1.0–1.5 points, given
this source's blend weight) at 80% power and 0.05 with residual SD ~3 needs n >= 40–70. 40 also
sits in a natural gap in the support distribution (35 → 51 → 100), so features do not flip in and
out week to week. Dropping them took 52 → 31 fitted features and moved the score
**0.1924 → 0.2024**, with the standard error halving (0.0065 → 0.0041).

The reading that matters: **card-text semantics are not estimable at useful precision from this
much data.** At an effect size of 0.5 points you would need 282 supporting rows, which almost
nothing has. That is the same conclusion the earlier hand-tuned-keyword experiments reached,
arriving this time from the variance side.

### 5. Dropping `statline` was wrong, and cross-validation could not see it

`statline` is emitted only for MINIONS, so it is `is_minion x (attack + health − 2·cost − 1)` — an
interaction, not a plain linear combination, and its span needs per-type stat terms the model does
not have. Removing it left the global attack/health slopes to absorb minion stat quality and they
went **negative**. Cross-validated rho *improved* (0.2021) while a 2-mana 2/3 fell to 14.9/100:
CV sees only cards the win-rate feeds cover, and the golden scores caught what it could not.
Restoring per-type stat interactions (`attack_minion`, `health_minion`) is the way to revisit it.

Corollary for the support floor: it applies only to `kw_*`/`tx_*` indicators. Dropping a
structural/type dummy does not remove a noisy estimate, it removes the baseline offset for a card
type that is still scored — `is_hero` has ONE supporting row, and dropping it moved Frost Lich
Jaina 76.6 → 53.6 with `health = 30` left unoffset.

### 6. The backstop does not work on the population it exists for

This model only ever decides a pick for cards the win-rate feeds have no data for, yet it is
fitted and validated on the cards that DO have data — the popular, well-sampled ones. Textbook
covariate shift. Holding out the lowest-`games` decile of cards (the closest available proxy, and
an optimistic one, since cards with *zero* data are further out) against a random holdout of the
same size:

| holdout (n=90 cards) | pooled Spearman |
|---|---|
| random cards | **0.2831** |
| lowest-games decile | **0.0871** |
| leave-one-set-out (4 largest sets) | 0.2446 – 0.3482 |

Generalizing to an unseen *release* is fine. Thin data is not. Three explanations tested:

- **Label noise** — partial. The two feeds agree 0.4289 on the thin decile versus 0.5738
  elsewhere, so the target is genuinely noisier there. Disattenuating by sqrt(reliability):
  **0.133 vs 0.374**. The gap survives.
- **Range restriction** — ruled out, and in the opposite direction: the thin decile's target
  spread is **larger** (sd 5.16 vs 3.51), not smaller.
- **Real model failure** — confirmed. MAE **4.52 vs 2.48**. Against a spread of 5.16, predicting a
  constant scores MAE ~4.1 (normal approximation), so on the thin decile the model is **no better
  than a constant**.

Consequence, implemented in `ScoreAggregator`: when no empirical source has a sample for a card,
the blended score is shrunk toward 50 by `ModelOnlyShrink`. Shrinking is monotone, so the order
among unmeasured cards is preserved; what it removes is their ability to outrank a well-sampled
card on the strength of a guess. **Do not raise this source's blend weight to compensate** — the
measurement says the opposite.

**How that constant is derived — corrected.** It was first set to 0.35 from the ratio of the two
disattenuated correlations above (0.133/0.374). That is not a shrink factor: a correlation is
symmetric and unitless, while the question "the model claims +5, how much is real" is a REGRESSION
SLOPE. Measured properly — held-out truth regressed on prediction, emitted every refit as
`holdout_thin_calibration_slope` / `holdout_random_calibration_slope` / `model_only_shrink_measured`
in `metrics.json`:

| holdout | calibration slope |
|---|---|
| random cards | **0.9154** (se 0.157) |
| lowest-games decile | **0.3102** |
| ratio -> `ModelOnlyShrink` | **0.3388** |

Two readings. First, the random-holdout slope sits at ~0.92, i.e. **in the measured regime the model
is essentially well calibrated** — the ridge fit is not systematically over- or under-claiming, which
is the sanity check that makes the thin-decile number interpretable. Second, the constant is the
RATIO, not the raw thin slope: the display mapping was itself calibrated on the measured regime, so
using 0.31 directly would apply that regime's calibration twice.

The corrected value (0.34) is 0.011 away from the one the wrong derivation produced. **The number was
right by accident; the reasoning was not.** Do not restore the correlation-ratio argument because its
output looked fine. Given se 0.157 on the denominator, anything in 0.30-0.40 is indistinguishable
here — re-derive from `metrics.json` at a refit, do not tune the third decimal.

### 7. Per-coefficient sampling error (case bootstrap over cards)

300 replicates, resampling **cards** and not rows: rows are correlated in groups of up to 11, so
row-resampling would understate exactly these standard errors. Selected results at alpha=300:

| feature | weight | se | ratio | sign consistency |
|---|---|---|---|---|
| `is_neutral` | −1.57 | 0.19 | 8.0 | 1.00 |
| `tx_gain_card` | −1.00 | 0.23 | 4.3 | 1.00 |
| `tx_summon` | +0.91 | 0.23 | 4.0 | 1.00 |
| `attack` | −0.17 | 0.05 | 3.0 | 1.00 |
| `is_hero` | −0.08 | 0.97 | 0.1 | **0.40** |
| `tx_mana_cheat` | −0.03 | 0.34 | 0.1 | **0.51** |

`is_hero` at sign consistency 0.40 means we cannot tell which direction the effect points — the
quantitative form of limitation 5 below. Coefficients that do not clear twice their own standard
error are listed in `metrics.json` as `unreliable_coefficients`.

### 8. The display mapping normalizes scale, not just centre

It was `50 + 15·(raw − median)`: 15 points per *raw unit*, with only the centre re-anchored per
fit. The displayed spread therefore drifted with whatever raw scale a re-fit landed on, which is
why golden scores swung on refits that had barely changed the model. The trainer now also ships
`anchor_sigma_raw` (1.4826 × MAD of the pool's raw scores) and the mapping divides by it, so
"+15 points per robust SD" is finally what happens rather than what the comment claimed. The slope
stays deliberately below the win-rate sources' ~+35 so a real win-rate outvotes this backstop on
disagreement.

**Measured caveat: this did not reduce today's volatility.** The pool's robust sigma is ~0.95, so
dividing by it is nearly a no-op right now, and the bootstrap noise floor moved only 17.71 → 16.71
display points. It is a correctness fix against future scale drift, not a variance fix. The
volatility is coefficient sampling error amplified by the slope — **p95 ~17 display points per
card** under resampling — which is a reason to distrust this source, not to re-tune the mapping.

### 9. Release gate, and why it cannot be calibrated statistically

`metrics.json` (written per run) replaces byte-comparing the weights file and scraping the console
log; the latter was culture-dependent ("0,44" vs "0.44") and would have mis-parsed silently one
day. The gate decides on the **same-class pick-flip rate**: the share of random same-class triples
whose recommended card changes. That is the right statistic — it is what a user would notice, and
raw coefficient deltas mix units (0.15 on the attack slope is a catastrophe, the same move on a
3-support keyword is nothing).

The threshold should be the bootstrap null: what resampling the same cards produces by itself.
**It cannot be, and that is a finding rather than a tooling problem.** Three independent statistics
were tried and all agree the model's output is too unstable to gate on:

| statistic | noise floor under resampling |
|---|---|
| max abs weight delta | 0.23–0.53 (mixed units, below the shipped rounding — meaningless) |
| p95 per-card display shift | ~17 of 100 points |
| same-class pick-flip rate | **31.4% of picks** |

The pick-flip version was expected to be much tighter, because two cards scored by one fit share
their coefficient error and it should largely cancel in the comparison between them. It did not.
And the flips are **not** confined to near-ties, which would have made them harmless:

| model's winner leads by | share of those picks that flip |
|---|---|
| > 0 pts | 31.4% |
| > 3 pts | 25.5% |
| > 5 pts | 22.0% |
| **> 10 pts** | **15.1%** |

Even where the heuristic claims a winner by more than 10 display points — two thirds of a robust SD
— resampling the training cards reverses that recommendation 15% of the time. The model reorders
cards it claims to distinguish clearly. (Caveat on the magnitude: a case bootstrap leaves ~37% of
cards out of each replicate, so this is "had we learned from a different sample of arena cards",
which is the right notion of estimation uncertainty but a substantial perturbation.)

So the gate ships with an **absolute** threshold (2% of picks changed) and the bootstrap floor is
reported, not enforced — calibrating on a 31% floor would mark every real refit "not material" and
silently keep stale weights, which is worse than PR noise. **The fix is a stabler model, not a
fourth statistic.**

### Open, in priority order

1. Per-type stat interactions (`attack_minion`, `health_minion`). ~~A decision on hero cards~~ —
   **decided, see 13**: the runtime declines to score them. ~~Rarity as dummies~~ — **closed**: the
   dummies were tried and every metric got worse (10), and the ordinal it would replace is fitted at
   **+0.00** (|w|/se 0.0, sign consistency 0.58), below the rounding floor, so `rarity_ord` is not
   even present in the shipped json — rarity already contributes exactly nothing at runtime. This
   matches what players say and the data agrees with: a common can outclass an epic, and the price
   tag is not the effect. Do not reopen it without a mechanism, not a hunch. The one rarity-shaped
   term left is `is_legendary` (+0.59, se 0.42) — also inside the noise band, and a candidate for the
   same treatment.
2. Reduce per-card variance, or reduce this source's authority further. Given 6 and 7, the second
   is the honest option.
3. Only then calibrate the output-space gate on the bootstrap null.

### 10. Tried and rejected: per-type stat slopes, rarity dummies, zeroed hero health

The obvious reading of 5 was that one attack/health slope shared across every card type is what
let those coefficients go negative, so the fix would be per-type interactions. Implemented all
three candidate changes together (`attack_minion` = is_minion x attack, `health_minion`, rarity as
`is_rare`/`is_epic`/`is_legendary` dummies instead of an ordinal, and hero `health` zeroed so
`is_hero` carries the level) and measured. Every number got worse:

| metric | committed | with the changes |
|---|---|---|
| CV within-class rho (1-SE alpha) | 0.2009 | 0.1982 |
| lowest-games holdout rho | 0.0871 | 0.0638 |
| ... disattenuated | 0.133 | 0.0975 |
| covariate-shift gap | −0.196 | −0.227 |
| thin MAE | 4.52 | 4.51 |

And the interactions did not do what they were supposed to: for a minion the total attack slope is
`attack` −0.11 + `attack_minion` −0.09 = −0.20, i.e. the same negative value as before, merely
split across two terms. Zeroing hero health moved the whole 30-health effect into `is_hero`, taking
it from −0.08 to **−3.66 +/- 1.76** (sign consistency 0.65) — one supporting row now carrying a
large, unreliable coefficient instead of a small one.

Reverted. The lesson is about interpretation, not about the features: with `statline`
(= attack + health − 2·cost − 1), `stat_per_mana`, `cost`, `attack` and `health` all in the model,
the individual stat coefficients are not interpretable — you cannot hold `statline` fixed while
varying `attack`. Their signs are an artifact of the parameterization, not a defect to chase, and
only the total predicted effect (what CV measures) means anything. A future attempt here should
change the parameterization wholesale (pick raw OR derived, plus cost bucket dummies) and be
judged on the thin-decile holdout, not on whether the printed signs look sensible.

### 11. A class's real arena win-rate IS recoverable from per-card data (validated cross-source)

The hero pick used to show only the pool-quality tier: the unweighted mean of a class's shrunk
per-card drawn rates, normalized 0-100. Useful for ranking, but not interpretable — "71/100" says
nothing a player can check. A win-rate in percentage points does.

Estimator, from the payload we already fetch:

- `Σ num_games·win_rate / Σ num_games` over the class bucket, using INCLUSION `win_rate`
  (not the drawn rate the card scores use). Summing "games where the card was in the deck" x "their
  win-rate" counts every game once per card it contained, which is a deck-level rate.

**It needs a correction, and the bias is structural, not noise.** The pooled rate comes
out at **53.37%** where a true average win-rate must be 50 — in
arena a winning deck keeps playing (up to 12 wins) while a losing one stops at 3, so weighting by
games oversamples winning decks. Subtracting the pooled offset removes it; only the
offset, never the spread.

Re-centred, the estimates land within ~3pp of the figures HDT's paid helper displays for the three
classes we could observe — the only external check available:

| class | estimate | Arenasmith (observed) |
|---|---|---|
| Demon Hunter | 54.6 | — |
| Paladin | 52.7 | **52** |
| Hunter | 50.8 | — |
| Mage | 49.5 | — |
| Priest | 49.1 | — |
| Death Knight | 45.0 | — |
| Warlock | 44.1 | — |
| Shaman | 41.9 | **39** |
| Rogue | 39.1 | **36** |
| Druid | 35.7 | — |
| Warrior | 28.5 | — |

**It is displayed, NOT blended into the score**, and that is a measured decision: Spearman between
the pool-quality tier and this win-rate is **0.9636** — over 11 classes only Hunter (5th→3rd) and
Priest (3rd→5th) move at all. The proxy was already ranking the classes correctly, so folding the
win-rate into the score would buy ~0.04 of correlation at the price of a hand-derived re-centring
constant inside the scoring path. It earns its place as a label and nothing more.

Not recoverable: the **pick rate** ("Picked 57%"). The feed publishes inclusion, not selection
frequency, so what fraction of players took a card when offered is not in the data.
Do not attempt to infer it from `popularity`, which is inclusion-in-deck, a different quantity.

### 12. Tribe availability is per-CLASS, and the dead-card lever was ignoring it

The dead-card penalty fires when a tribal payoff is drafted with none of its tribe. It was
class-blind, which charges the same penalty to a Warlock offered a Demon payoff and to a Paladin —
two very different situations. Measured from the same HSReplay per-class payload we already fetch,
popularity-weighted (popularity IS "% of that class's decks that ran the card", so it reads directly
as a share of deck slots), amalgams excluded so it matches what the engine accepts as a member:

| class | Beast | Undead | Dragon | Elemental | Demon | Mech | Murloc | Naga |
|---|---|---|---|---|---|---|---|---|
| Death Knight | 5.23 | **27.22** | 2.54 | 3.18 | 2.69 | 1.83 | 1.73 | 0.40 |
| Demon Hunter | 6.52 | 2.88 | 2.07 | 2.52 | 12.97 | 1.52 | 1.35 | 4.11 |
| Druid | 13.61 | 2.45 | 6.23 | 2.88 | 0.72 | 2.14 | 1.29 | 0.41 |
| Hunter | **23.26** | 2.48 | *1.72* | 2.49 | 0.57 | 2.34 | 1.27 | 1.85 |
| Mage | 3.75 | 1.86 | 4.00 | 10.40 | 1.96 | 4.27 | 1.01 | 1.95 |
| Paladin | 9.09 | 4.18 | 3.96 | 3.85 | *0.60* | 7.52 | 2.96 | 0.35 |
| Priest | 3.19 | 6.89 | 7.61 | 3.91 | 0.58 | *1.25* | 0.75 | 0.66 |
| Rogue | 4.93 | 4.54 | 3.46 | 3.00 | 1.97 | 4.47 | 1.28 | 0.44 |
| Shaman | 6.85 | *1.77* | 5.21 | **11.51** | 0.73 | 3.48 | 1.48 | 1.59 |
| Warlock | 5.09 | 4.55 | 7.76 | 2.84 | **16.55** | 1.81 | 1.31 | 0.41 |
| Warrior | 6.31 | 1.90 | **8.95** | 3.42 | 2.48 | 8.23 | 1.31 | 0.44 |

Spreads of 5-28x (Demon: Warlock 16.55 vs Paladin 0.60; Undead: DK 27.22 vs Shaman 1.77), so this
is a real class-level signal, not noise. **It also refutes the hypothesis that prompted the work**:
the guess was that Priest is a poor home for a Dragon payoff — measured, Priest is the *3rd best*
Dragon class at 7.61%. The genuinely dead cases are Hunter (1.72%) and Demon Hunter (2.07%).
Guessing which class lacks which tribe does not work; the table does.

**How it is used.** The penalty is scaled by the members the remaining picks are expected to bring,
`share x picksLeft`, against the couple a payoff needs to switch on. The direction is deliberately
one-way — availability can only REDUCE the penalty, never deepen it — because this is the single
lever allowed past the fuzzy clamp and nothing measured here justifies making it more aggressive.
A floor stops the factor going negative, which would otherwise flip the penalty into a bonus that
GROWS with availability. Missing data (class unknown, feed not loaded, tribe unseen) reproduces the
class-blind behaviour exactly, and a test pins that.

**Still not validated end-to-end**, and it cannot be with public data: there is no per-deck dataset
to check whether damping the penalty improves picks. What changed is the INPUT — measured per-patch
availability instead of an implicit "every class runs every tribe equally", which the table shows is
false by up to 28x. The bound and the one-way direction remain the guardrail.

### 13. Hero cards: the runtime now declines to score them (open item 1, answered)

`is_hero` has **one supporting row**. It is not an estimate; it is a baseline offset that also has
to cancel the literal 30 health a hero card reports, and both ways of touching it were already
measured to be worse: dropping the dummy moved Frost Lich Jaina 76.6 → 53.6, zeroing the health
moved the coefficient −0.08 → −3.66 ± 1.76. The 2026-07-25 refit added the third data point — the
coefficient went −0.08 → **+0.77**, with se 0.98 and sign consistency **0.46**. A term whose sign
is a coin flip, carrying a whole card type, produces a score that re-rolls by ~25 display points
between refits on data that barely moved.

So the fit keeps the dummy (removing it contaminates the health slope) and **`HeuristicArenaDataSource`
returns null for `CardType.HERO`** instead. Fitting and inference are different acts; only the second
changed.

**What it costs, measured** — of the **46** collectible hero CARDS in HearthDb, exactly **2** appear
in the win-rate cache: Galakrond, the Unbreakable and Lord Jaraxxus. A feed only
reports what actually gets drafted, so those two are also the ones in the pool: abstaining removes a
number from **no card a player can currently be offered**. Frost Lich Jaina, the old golden, is
covered by neither feed and is not in the pool.

Where nothing at all covers a hero card, the plaque shows no score: with every source abstaining,
`ScoreAggregator` returns `Empty` at its `weightTotal <= 0` guard, which is **before** the
shrink-toward-50 path (that one needs a component to shrink). This surprised the review that caught
it, and it is worth stating plainly rather than assuming the shrink is a universal safety net.

The rejected alternative is worth naming, because it is the intuitive one: *hero cards are obviously
strong, so give them a bonus.* Probably true, and still not admissible — hand-tuned card values are
the thing this project measured to be **worse than nothing** (§ "Central methodological finding").
If hero cards deserve credit, it has to arrive as win-rate data.

### 14. Rarity is not an input any more (`rarity_ord` and `is_legendary` removed)

**Decided on the mechanism, not on the metrics.** Rarity is a print-run label. What makes a legendary
strong is an above-curve statline and unique text — both of which the model already reads directly —
so letting the label carry a coefficient lets it collect credit that belongs to the card. Commons
routinely outclass epics, and the model should have no way to disagree.

The statistical case was already there but is weaker than it looks: `rarity_ord` fitted to **+0.00**
(|w|/se 0.0, sign consistency 0.58) and was below the rounding floor, so it never even reached the
shipped json; `is_legendary` was **+0.59 ± 0.42**, inside the noise band. The rarity *dummies* had
been tried in 10 and made every metric worse.

**One checkable consequence**, which is worth more here than any correlation: **Deathwing went 16.01
→ 5.12**. A 10-mana 12/12 that hands the opponent the board belongs at the floor; the label had been
worth ~10 display points on its own. Its golden literal now pins that.

**What the refit reported, and why it is NOT the argument:**

| | committed (24/07) | same data, WITH rarity | same data, WITHOUT |
|---|---|---|---|
| `cv_within_class_rho` | 0.2009 | 0.2228 | 0.2265 |
| `holdout_random_rho` | 0.2831 | 0.3132 | 0.3156 |
| `holdout_thin_rho` | 0.0871 | 0.0400 | 0.0884 |
| bootstrap noise floor | 31.16% | 31.16% | 28.98% |
| `per_card_shift_p95` | — | 17.0 pts | 15.4 pts |
| selected `alpha` | 300 | 300 | **100** |

Every column moved the right way, and **that is not evidence**. An adversarial review of exactly this
table found the hole: `SelectAlphaByCv` re-picks alpha per feature set, and it did (300 → 100), so
these are three separately-tuned models rather than one model minus two columns. Worse, the thin
statistics carry no standard error at all — `randomScores` is averaged over `CvRepeats` while
`thinRho`/`thinSlope` are computed once — and on ~90 cards against a near-null signal a 0.05 move in
rho is unremarkable. The 0.087 → 0.040 → 0.088 path is a statistic wandering, not a loss and a
recovery. (One correction to that review: the thin decile is the *lowest-games* cards, a deterministic
set, so between the two same-data refits it is not draw luck — it is alpha and the feature set. That
makes the comparison cleaner, not significant.)

**`model_only_shrink_measured` moved 0.339 → 0.263 → 0.471 across three fits and was NOT adopted.**
`ScoreAggregator.ModelOnlyShrink` stays the hardcoded 0.34: a runtime constant derived from a ratio
whose numerator is a single-draw slope over ~90 cards has no business being re-committed automatically.
That the trainer only *reports* it is the design working.

**Open, from this**: give `thinRho` and `thinSlope` a bootstrap interval (or repeat them the way
`randomScores` is repeated) before anyone reads them run-over-run again. Until then they are direction,
not measurement.

### 15. Card-pool measurements behind the 0.1.6 synergy changes

Every number here was measured by dumping the WHOLE collectible pool through the real engine and
diffing two builds — not by re-implementing the patterns in a script. That distinction earned its
place: a PowerShell replication of `DependencyPatterns` disagreed with production (it substituted
`beasts?` where the engine substitutes `beast`) and sent the first version of a regression test at
Frizz Kindleroost, a card the old patterns already caught, so the test passed with and without the
fix and proved nothing. **Dump the pool through the engine; do not re-implement the engine.**

These are pool-dependent and will move with rotation. Re-measure rather than trusting them.

**Tooltip line breaks.** Card text carries the client's own line wraps as newlines and `CardText.Normalized`
does not collapse them (it cannot: the heuristic's weights are fit against those exact bytes). **3594 of
the 7300** collectible cards with text contain a newline. A literal space instead of `\s+` cost:

| pattern set | effect of the literal space |
|---|---|
| summon-from-deck | fired on 4 of the 6 cards it exists for (missed Skydiving Instructor, Reinforcement Aura) |
| `DependencyPatterns` / `GenerationPatterns` | **24 cards** scored differently once fixed |

Of those 24: 22 gained the dead-card penalty they were dodging (Corrosive Breath 0.00 → −6.07, plus
Molten/Lightning Breath, Stormhammer, Twilight Acolyte, Goblin Blastmage, Ini Stormcoil, Gentle
Megasaur, Serpentbloom), and 2 correctly LOST it because their generation clause became visible (Lady
Prestor, Boom Wrench). The whole suite was green before and after.

**Summon-from-deck population.** 45 collectible cards summon from the deck; only **6** state a cheap
limit and so may be judged by the 1-2 mana bucket (Apothecary's Caravan, Boogie Down, Reinforcement
Aura, Scarlet Recruiter, Skydiving Instructor, Trusty Fishing Rod). 3 fetch a KNOWN card — themselves —
and are excluded (Patches by "summon this", Persistent Peddler and Moragg by name).

**Secret / Aura availability, per class.** Popularity-weighted from the per-class payload, same method
as tribe availability (§12):

| classes | Secret share of slots | expected in 20 picks |
|---|---|---|
| HUNTER / ROGUE / MAGE | 4.29% / 4.23% / 4.20% | ~0.85 |
| the other **eight** | 0.00% | 0 |

PALADIN measures **0%** while having Secrets in principle — the pool decides, which is the argument
against a hard-coded class list. Only **21 of 118** collectible Secrets are in the current pool at all,
and Eater of Secrets, Kezan Mystic and Sunreaver Spy are absent entirely. Worked example of the damping
on a Mage at pick 10: 0.84 expected → factor 0.58; with the body factor (a 4/4 body) and progress 0.33,
Chatty Bartender's penalty is ≈ **−0.34**, i.e. 64 → 63.7. The axis is right; it is not what makes that
card score 64 — its measured drawn win-rate is 52.5% over 6076 games against a MAGE median of 51.3%.

**Neutral Secret payoffs** are the population that matters, since a class card is only offered to its
own class: of 27 neutral cards naming a Secret, the engine flags **10** as dependent, correctly excludes
4 (anti-secret tech, and two that generate their own), and missed 2 on grammar (below).

**Dependency grammar.** The patterns were written for tribe wordings and missed "the next Secret **you
play**". Adding `{0}s? you play`, `{0}s? you played` and `next {0}\b`, measured across all 12 tribes plus
both categories, flags **8 distinct cards** once the generation veto and own-membership guard apply —
the Draenei cluster is excluded because those cards ARE Draenei, and Archimonde is vetoed (it depends on
other cards GENERATING Demons, a dependency the engine cannot see). Still missed: Tiny Pal, whose "your
Elemental Ammunition" is an adjective rather than a member reference.

**Base-line exemption.** `HasUnconditionalClause` moved **50+ cards** from −6.07 to −1.52 — the entire
"Deal N damage. If you're holding a Dragon…" family, plus Kill Command, Mirror Dimension and Nofin Can
Stop Us. Cards whose ENTIRE text is the condition keep the full penalty (Elemental Evocation, Ancient
Mysteries). Two threshold findings, both from reading the diff: a clause floor of 14 characters kept the
full penalty on Grave Digging, whose base line is the 12-character "Draw 2 cards" (lowered to 10); and
at 10, a continuation clause ("Draw a Secret. **It costs (0)**") wrongly exempted 3 cards, so clauses
opening with a back-referring pronoun are rejected.

**Hero powers.** All **2138** HERO_POWER cards classified from text: 262 `DirectDamage`, 219
`HeroAttack`, 59 `ChargeToken`, 1598 `None`. Two pool-driven corrections: a GRANTED effect ("Give your
minions 'Deathrattle: Summon a 2/1 Squashling with Rush'") summons nothing itself, and damage that
cannot be AIMED ("to a random enemy", 35 cards) answers no particular minion. Of the eleven basic hero
powers only **Fireblast and the Charge Ghoul** kill a one-health body for free; Shapeshift, Demon Claws
and Dagger Mastery must swing the hero and eat its attack; Steady Shot and Ballista Shot are FACE-ONLY
(HearthDb ships their text twice, once unrestricted — confirmed face-only by a player, reconciled via
the repeated "Hero Power" label); and Reinforce does NOT answer a body, because a Silver Hand Recruit
has no Charge. A from-memory class list had three of these wrong.

**Display-scale observation, not yet acted on.** The 0-100 display is a logistic on ALL-anchored robust
z, so a card at the median of a strong class reads well above 50: with the PALADIN median drawn
win-rate at 52.4% against the pool's 49.1%, a *median* Paladin card computes to ≈69. Dance Floor at
53.3% drawn (z +0.21 vs class, +0.60 vs pool) displays 78. The ordering is unaffected — it is ordinal —
but the absolute numbers read higher than "78/100" suggests, and the deck-review panel is where this
misleads most, since every card in a real deck lands in the 70s-80s. Anchoring on ALL is deliberate (it
makes a class card and a neutral comparable at the same pick); the open question is whether the panel
should show a class-relative number instead.

### 16. Card-pool measurements behind the post-0.1.6 mulligan rules

2026-07-27, HearthDb 36.0.4.0, measured by running each rule over the committed pool — never by
re-implementing its patterns in a script. The one time that distinction was tested here it mattered:
a grep counted **20** cards for the weapon rule where the rule itself sees **15**, because a grep
counts hero powers (Sharpen, Cash In) that the rule is never handed.

| Rule | Support in the pool | Note |
|---|---|---|
| Needs an equipped weapon (`DependsOnAnEquippedWeapon`) | **15** cards at cost ≤ 2 | mostly Rogue's poisons; 53 at any cost |
| Pays with health (`PaysWithHealth`) | **10** distinct cards, cost 0-6 | one printed wording covers all: "costs Health instead of Mana" |
| Develops board (`DevelopsBoard`) | **117** spells of the 1514 at cost 1-3 | after the quoted-text and quest guards below |
| Upgrades while held (`UpgradesWhileHeld`) | **29** Infuse cards, **13** at cost ≥ 5 | plus ~11 longhand "while this is in your hand" |
| Grows in hand **or deck** (vetoed) | **6** rows | Lotus Troublemaker: holding it buys nothing |

Three guards were added because the naive pattern got a real card wrong, each found by the dump and
not by reasoning:

- **Quoted text belongs to the TOKEN.** Mining Casualties reads `Summon two 1/1 Silver Hand Recruits
  with "Deathrattle: Summon a 1/1 Frail Ghoul"`. Reading that Deathrattle as the spell's own
  condition rejected the very card the rule was written for.
- **A quest's text states what you must DO**, and reads exactly like a summon: Jungle Giants, Unite
  the Murlocs and Unseal the Vault all matched until the QUEST tag was checked instead.
- **"In hand or deck" is not a reason to hold.** Without that veto the Infuse exemption would also
  cover cards designed so you do NOT have to keep them.

**Spell-school synergy fires but is invisible.** Measured through the engine: Icy Touch (the Death
Knight FROST spell, dbf 78334) with two Rambunctious Stuffy drafted scores **+0.40**, against 0.00
with no payoffs. `SpellSchoolPerPayoff` is 0.2 with a cap of 3, so the payoff direction maxes at
**0.6** — against `MinReasonPoints` 0.5, which is what a component must reach to earn the reason
line. So only a fully maxed school synergy is ever named, and in a whole live session (1,668 scored
options) not one school label appeared, while tribes produced 99 Beast, 56 Elemental, 20 Undead. The
tribal constants are 0.4 per member with a cap of 5. Whether 0.2 is the right weight is a
calibration question; that the score moves without saying so is a defect either way.

**Feed coverage is better than the raw card count suggests.** Over the same session, of 1,668 scored
options only **54 (3%)** had no win-rate at all — so the offline model is the sole voice rarely. But
**131 of 1,614 (8%)** sat below the 200-game low-confidence threshold, and the heuristic keeps a
third of the blend at *every* sample size by design, so on a 2,071-game card like Activated Golem it
still moves the displayed score by ~7 points. See the open item on fading the model as n grows.

**Class context can disagree with the pool by more than noise.** Activated Golem (JAIL_883): ALL
bucket 52.2% drawn on 23,559 games (13.8% popularity), the drafted class's bucket 48.4% on 2,071.
At that n the standard error is ~1.1pp, so a 3.8pp gap is ~3 SE — not obviously noise, but a
single-class 4-day window is also where a meta artefact would look exactly like a class effect, and
the two buckets are not independent (ALL contains the class). Not acted on.

## Known limitations

1. Target = win-rate of the deck that includes the card: correlational, not causal (draft
   bias, player skill, synergies).
2. Noise ceiling 0.47-0.53 between sources: rho 0.26-0.27 is ~55% of the achievable maximum.
3. Some anti-folklore signs (negative charge, divine shield ~0) reflect the current pool
   (charge/forge minions are understatted), not the keyword's value in the abstract:
   the weights must be RE-ESTIMATED every rotation/patch, not treated as universal constants.
4. The ALL bucket flattens the class dependency; per-class models currently do worse
   (0.184 vs 0.263) due to data scarcity.
5. Hero cards (+5.28) and locations: few examples, unstable weight.
