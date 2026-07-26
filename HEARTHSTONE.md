# Hearthstone domain knowledge

Game knowledge for people and agents working on this plugin: how Hearthstone and its Arena mode
actually behave, so a feature can be reasoned about before it is built.

**This is the fourth document, and it has its own job.** [`AGENTS.md`](./AGENTS.md) states the project's
rules and invariants; [`REPORT.md`](./HdtArenaHelper.Training/REPORT.md) owns every conclusion about the
model and the data; [`CHANGELOG.md`](./CHANGELOG.md) owns the history. This file owns facts about the
GAME — things that would be true if this plugin did not exist.

**Two tiers, and the difference is load-bearing.** Section 1 is what this project has **verified
itself**, against the live card pool or on a real client, and each entry says how. Everything after it
is domain background or other people's advice, which is a source of *questions worth asking*, never of
authority. This project measured that hand-tuned card values score worse than nothing — so an idea from
outside enters as a hypothesis to be validated, not as a rule to be implemented.

---

## 1. Verified in this project

Every claim here was checked against HearthDb's card pool, against the HSReplay payload, or on a live
HDT client. Where a count is given, it is a count of collectible cards in the pool at the time — those
move with rotation, so re-measure rather than trusting the figure. The method that produced them, and
the numbers themselves, are in REPORT.md §15.

### 1.1 Rules of the game that a win-rate feed cannot see

**A SUMMONED minion never triggers its Battlecry.** Deathrattle, Taunt, Divine Shield and the statline
all survive being summoned; the Battlecry does not. So a card that pulls minions out of your deck is
worth more in a Deathrattle deck than in a Battlecry one — a difference no per-card win rate can
express, because the feed averages a card over every deck that drafted it.

**Only some hero powers answer a small minion, and the cost differs.** Derived from all 2138
HERO_POWER cards rather than from memory, which got three of these wrong:

| how it answers a 1-health body | hero powers | cost to them |
|---|---|---|
| direct damage | Mage's Fireblast | nothing but the mana |
| a token that can attack at once | the Death Knight's Ghoul (Charge, and it dies at end of turn anyway) | nothing |
| the hero swings | Druid's Shapeshift, Demon Hunter's Demon Claws, Rogue's dagger | takes the minion's attack to the face — which is exactly why a 3/1 and a 2/1 are different cards to hold |
| not at all | Hunter, Warrior, Priest, Warlock, Shaman, **Paladin** | — |

Two corrections worth keeping: **Paladin's Reinforce does NOT answer a body**, because a Silver Hand
Recruit has no Charge and cannot attack the turn it lands; and **Hunter's Steady Shot hits the face
only** — HearthDb ships its text twice, once unrestricted, and reading the wrong copy puts Hunter among
the classes that ping.

**A body that pays you when it dies is not dying for free.** A 1-health minion is exposed to any of the
above, but "dies for free" is a claim about the OPPONENT's side of the trade. A deathrattle that draws
or adds cards means the hero power that kills it costs them a turn and gains them nothing.

**A card that upgrades when traded has a turn-1 play that its printed cost hides.** Being Tradeable is
not the same thing: most Tradeable cards only cycle, and cycling an expensive card you did not want is
worse than the free replacement a mulligan already gives you. Only a small minority gain value from the
trade itself, and the wording for that is not uniform — at least four different phrasings exist.

**A printed cost is not always the real cost**, and the distinction has a trap: a card that reduces its
OWN cost breaks the "cost = the turn it lands" assumption, while a card that discounts the cards it
FINDS does not. "Discover two spells. **They** cost (2) less" is the second kind, and a pronoun subject
is the tell.

### 1.2 Structure of the Arena pool and mode

**A hand is 3 cards going first, 4 going second**, and the Coin is not dealt into the mulligan hand —
so the hand SIZE is a more reliable way to know who goes first than reading a field that can fail.

**Legendary group picks appear in BOTH normal Arena and Underground**, so nothing should gate that
screen on the mode. The first pick is the run's only guaranteed legendary, while ~29 later picks can
supply average bodies — which is why a group's value is not the mean of its cards.

**Underground allows a post-loss redraft**: five new cards arrive and five must go. Two client
behaviours to know about, both observed: the deck size is reported either flat at 30 with the new cards
already counted in, or counting down from 35; and discarding a card removes it from the run deck
immediately while the redraft list keeps reporting all five arriving cards for the whole phase.

**Hearthstone has NO per-player limit on Location cards.** The cost of drafting several is tempo and
board space, not slot exclusivity — which is why crowding them deserves the gentlest penalty in the
model rather than a hard cap.

**Questlines are excluded from the Arena pool outright, and Quests are allowed per rotation.** A rule
about them may be unreachable in a given season, so pin the rule rather than the expectation that it
fires.

**Some card categories exist in very few classes, and which ones moves with the pool.** Measured on the
current payload, Secrets are a meaningful share of only three classes' slots and absent from the other
eight — including a class that has Secrets in principle but none in this pool. Any rule keyed on a
category must therefore measure availability rather than carry a hard-coded class list.

### 1.3 Traps in the card DATA (not in the game)

**Card text carries the client's tooltip LINE BREAKS as newlines.** Roughly half the collectible cards
with text contain one, and it can fall in the middle of a phrase — so a text pattern written with a
literal space silently skips whichever cards happened to wrap there. This is the single most expensive
data trap this project has hit, and it hit it twice.

**Reprints are separate card ids with separate win-rate rows**, so joining a feed on the raw id splits
a card's sample across printings.

**Mentioning a tribe is not depending on one.** A card that summons its own members, generates them
from the whole card pool, or punishes the OPPONENT's, merely names the tribe. Getting this backwards
scores anti-tribe tech UP for the very tribe it exists to punish.

**A conditional clause is not the whole card.** "Deal 3 damage to a minion. If you're holding a Dragon,
it also hits the enemy hero" is a removal spell with a rider, not a dead card without dragons. What
makes a card structurally dead is the condition being the *entire* text.

### 1.4 Verified on a live client

- The in-game Discover overlay and the redraft deck panel render at their documented anchors.
- The opponent's current hero power is readable from the tracker's game state at mulligan time.
- Board constraints that make a choice objectively wrong — hand full so a discovered card is destroyed,
  board full so a minion cannot be summoned, cost above available mana — are readable and worth stating
  in words. What no public data provides is their value in POINTS, so they are reported, never scored.

---

## 2. Domain background

See **[`docs/hearthstone-primer.md`](./docs/hearthstone-primer.md)** — a reference on card mechanics and
keywords organised around the question that matters here: **can a program detect this at all?** Every
mechanic is classified Tag (a structured `GameTag`, safe to branch on), Text (pattern-matched, brittle)
or Runtime (only knowable in a live match, so it must never be folded into a draft score).

Its detectability table has been re-measured against the actual card pool rather than left as
assumption, which corrected three entries: `TRANSFORM`, `IMBUE` and `STARSHIP` exist in the tag enum but
are set on **no collectible card**, so a rule keyed on them would silently never fire.

Background on arena decision dynamics — tempo vs value vs card advantage, why the curve is a MINIONS
concept, why board control dominates arena more than constructed, the attack/health asymmetry that makes
a 3/1 and a 1/3 different cards, why removal is scarcer in arena, and why a bomb decides more games here
— is being kept with the primer rather than duplicated into this file.

---

## 3. Advice from outside this project

Publicly published guidance from experienced Arena players, recorded as candidate rules with their
provenance. Read the framing above before using any of it: an outside idea is a hypothesis.

Two standing constraints on this section. **No statistics are carried over** — a keep rate or win rate
from a guide is someone's summary of data we cannot audit, and this project shows invented numbers are
worse than none. **No passages are reproduced** — ideas are paraphrased and the source is linked,
because "publicly reachable is not licensed" is a lesson this project has already paid for once.

### What the search actually returned

Two searches were run over public arena guidance — one on mulligan advice, one on drafting. The most
useful result is negative, and worth recording so nobody repeats the search expecting more:

- **No credible source gives numeric targets** for how much removal, AoE, card draw or how many taunts
  an arena deck wants. Guides describe mixes and curve shapes qualitatively. So the numeric targets in
  this project's curve model remain ours to justify, not something borrowed.
- **No public framework exists for in-game Discover decisions** ("best card" vs "best card right now").
  Encoding one is original design work, not consensus to be transcribed.
- **No arena-specific source** was found on the mulligan cases this project already handles: hero cards
  and self-discounting cards abstaining, the 1-mana quest wanting turn 1, Combo interacting with going
  first. Those are this project's own reasoning, for better or worse.
- **The one official source found** covers the mulligan mechanic for the game generally, not arena.
  Everything arena-specific is community or editorial.

### Candidate rules worth considering

Recorded as hypotheses. "Opinion" means exactly that: no data behind it that we can audit.

| rule | kind | computable here? |
|---|---|---|
| Weight raw card power over curve fit early in a draft; let curve-filling take over in the last third | opinion, and sources do NOT agree on where the switch falls | partly — progress scaling already exists; the threshold would be a free parameter |
| A hole in the curve justifies a lower-rated card that fills it | the tempo loss from a blank turn is rules-derived; how much to pay for it is opinion | yes — already modelled |
| Removal should be judged as a MIX (cheap damage / hard removal / AoE), not a count | bucket mechanics rules-derived, targets opinion | partly — needs a removal-type classifier; `DeckMechanics` already separates hard removal from damage |
| Discount synergy early in a draft and only trust it once the pieces are confirmed | opinion | yes, and consistent with the progress scaling already used |
| Give a combo bonus only when each half clears a standalone floor | opinion | yes — and this validates the existing design, where synergy is a bounded addition on top of a win-rate base |
| Reweight card features by the class's HERO POWER (a healing hero power raises healing's value, a board-inert one raises early minions') | opinion | not currently — the per-class win-rate bucket captures this only implicitly. This is the most interesting gap the search surfaced, and it connects to work already done: `HeroPowerThreat` reads the hero power CARD, so the machinery to reason about hero powers exists |
| Going second allows a looser curve than going first | opinion, and the specific curve shapes came from a forum rather than an edited source | partly — the Coin's effect on effective turns is already modelled |

### Sources of disagreement, left unresolved

**Curve versus quality at the mulligan.** An arena-specific source argues the mulligan should chase card
QUALITY and that a bomb is worth keeping at the cost of curve; a constructed-focused one argues the
opposite, that unused early mana is expensive and sequencing wins. Both are stated as general advice.
The disagreement is probably real rather than one side being wrong — arena's thinner decks make a strong
standalone card more independently valuable — and this project currently sits on the arena side, since
`BombScore` exempts a high-scoring expensive card from the top-end toss.

**How much weight synergy deserves once an archetype is visible mid-draft.** One line of sources says
lean into it; another warns against over-weighting synergy even late. Both agree it must never override a
large gap in standalone power — which is the bound this project already enforces.
