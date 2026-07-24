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
- Firestone/zerotoheroes per class: `winrate = decksWithCardThenWin / decksWithCard`.
- Model artifact: `arena_weights.json`, produced by the `HdtArenaHelper.Training` C# tool.

## Central methodological finding
HSReplay `win_rate` = win-rate of the DECKS that include the card. In the ALL bucket it is dominated
by class strength, not by card quality:
- Ridge without class: Spearman OOF 0.253. Adding only the class dummies: **0.800**.
- For a draft helper (comparing 3 cards for the SAME class) that signal is useless/misleading.
- Honest target: win-rate **centered on the class mean** (rows (card, class) from the class
  buckets, `num_games >= 100`, GroupKFold by card to avoid leakage from neutrals).

## Noise ceiling
Agreement between the two sources (HSReplay vs Firestone, same per-class target): mean Spearman
**0.469** (per-row 0.533). No model on metadata alone can exceed this ceiling by much.

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

## Cross-check Firestone
- Final formula (trained on the mean target) evaluated OOF: rho 0.260 vs HSReplay, 0.201 vs
  Firestone (P10 0.155). Version trained only on HSReplay: 0.299 vs Firestone pooled.
- Holds on both sources: not overfit to HSReplay.

## Per-class vs ALL (deliverable 4)
- PER-CLASS ridge models (OOF, num_games>=100): mean Spearman **0.184** — worse than the
  pooled global model (0.263): too little data per class, it overfits.
- The uncentered ALL bucket, on the other hand, inflates (0.80) due to class confounding.
- Conclusion: a single global weight set + per-class centered target is the sweet spot;
  real residual per-class variation exists (warrior 0.417 vs shaman 0.063) but the current
  data is not enough to learn it reliably.

## FINAL FORMULA (H4, dual-source ridge, units = percentage points of win-rate vs class mean)
score = -0.17
  + 5.28*is_hero + 0.93*is_weapon + 0.55*is_loc - 0.55*is_spell + 0.29*is_minion
  - 1.27*is_neutral - 0.09*is_legendary - 0.05*rarity_ord
  + 0.10*statline + 0.64*stat_per_mana - 0.23*attack - 0.27*health + 0.08*cost
  - 0.20*weapon_value + 0.11*has_tribe
  (keyword) - 2.86*charge + 1.49*windfury + 0.95*reborn + 0.88*stealth + 0.86*lifesteal
  + 0.76*rush + 0.72*poisonous + 0.68*colossal + 0.66*freeze + 0.52*battlecry
  + 0.31*secret + 0.30*outcast + 0.29*echo + 0.23*taunt + 0.21*discover + 0.08*divine_shield
  - 4.25*forge - 1.10*spellpower - 0.64*magnetic - 0.56*deathrattle - 0.49*combo
  (text) + 2.16*silence + 1.04*armor + 0.80*summon + 0.65*destroy_minion + 0.25*aoe
  + 0.29*persistent + 0.15*mana_cheat + 0.14*restore_amt + 0.14*random + 0.13*draw
  + 0.10*damage_amt + 0.21*tx_discover - 0.80*gain_card - 0.47*transform - 0.30*dmg_per_mana

where: statline=(atk+hp)-(2*cost+1) minions only; stat_per_mana=(atk+hp)/(cost+1) minions only;
weapon_value=(atk*dur)-(2*cost+1) weapons only; the tx_* are regex flags on the text (see
BuildFeatures in the training tool); exact weights in arena_weights.json.

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
