# Hearthstone Card Mechanics and Keywords — A Reference for Draft-Helper Developers

This reference is written for a program that must decide, for each card, whether a mechanic is
present and — where relevant — how it changes the value of that card in a given deck/board
context. The critical axis for every entry is **detectability**: can the mechanic be read off a
structured game tag, does it require parsing card text, or does it only become knowable from
runtime game state (board, hand, mana)? Getting this axis wrong is how a scoring engine ends up
confidently wrong: a tag-backed fact can be trusted; a text-parsed fact is a heuristic; a
runtime-only fact cannot be baked into a static card score at all.

## Detectability tiers, defined once

| Tier | Meaning | Confidence for scoring |
|---|---|---|
| **Tag** | A structured `GameTag` (or enum field) on the card definition itself | High — safe to branch logic on |
| **Text** | Only recoverable by pattern-matching the card's rules text | Medium — brittle to wording changes, localization, and reprints with re-worded text; needs a whitelist/blacklist discipline, not a bare substring match |
| **Runtime** | Only knowable from the live board/hand/mana state during a match | Not a static card property at all — can inform in-game advice (mulligan, Discover) but must never be folded into a context-free draft score |

Many mechanics are tag-backed for "does this card have keyword X" but text-only for "what does
X actually do quantitatively" (e.g., a card has `Tag=DISCOVER` but the discovered pool/count is
implicit in text or in the referenced entity, not a scalar tag).

### Which tags are actually POPULATED — measured, not assumed

A tag existing in `HearthDb.Enums.GameTag` is **not** the same as that tag being set on cards. All 34
names below exist in the enum; the count is how many COLLECTIBLE cards carry a non-zero value. Anything
at zero is text-only in practice, whatever the enum suggests. Counts move with rotation — the method
matters more than the figure, so re-run it rather than trusting this table after a patch.

| tag | cards | tag | cards | tag | cards |
|---|---|---|---|---|---|
| `BATTLECRY` | 2303 | `SPELLPOWER` | 58 | `OBJECTIVE` | 27 |
| `DEATHRATTLE` | 700 | `TRADEABLE` | 55 | `MODULAR` | 26 |
| `TAUNT` | 496 | `CHARGE` | 49 | `COLOSSAL` | 24 |
| `DISCOVER` | 375 | `WINDFURY` | 35 | `DREDGE` | 21 |
| `RUSH` | 247 | `OUTCAST` | 35 | `FORGE` | 20 |
| `LIFESTEAL` | 141 | `REBORN` | 31 | `MINIATURIZE` | 19 |
| `SECRET` | 118 | `QUEST` | 30 | `TITAN` | 12 |
| `DIVINE_SHIELD` | 96 | `FREEZE` | 30 | `PALADIN_AURA` | 12 |
| `COMBO` | 87 | `POISONOUS` | 29 | `QUESTLINE` | 10 |
| `STEALTH` | 84 | | | `SIDEQUEST` | 9 |
| `OVERLOAD` | 82 | | | `SILENCE` | 8 |
| **`TRANSFORM`** | **0** | **`IMBUE`** | **0** | **`STARSHIP`** | **0** |

Three findings worth acting on:

- **`TRANSFORM`, `IMBUE` and `STARSHIP` are set on no collectible card**, so a rule keyed on those tags
  would silently never fire. They are Text tier despite looking Tag tier — and a rule that never fires
  is worse than an absent one, because tests pass and nothing happens.
- **`SILENCE` is tag-backed but only on 8 cards**, and `SIDEQUEST`/`QUESTLINE` on 9 and 10. Rules keyed
  on these are legitimate but their tests pin the RULE, not the expectation that they ever fire in a
  given season.
- `BATTLECRY` covers 2303 cards, which is why "a summoned minion loses its Battlecry" reaches so much
  of the pool and is worth modelling at all.

## Core keywords

### Battlecry
- **What it does:** Triggers an effect when the card is played from hand as a minion (or hero
  card/etc. that supports it).
- **Why it matters:** Front-loads the card's value onto the turn it is played; a big Battlecry
  minion whose body is otherwise weak is still "good the turn it lands" and bad if left in hand.
- **Detectability:** Tag (`GameTag.BATTLECRY`) confirms presence. The *magnitude* of the effect is
  text/referenced-entity only.
- **Interaction to get right:** see "Summon vs Battlecry" below — a minion that enters play via a
  summon effect (not played from hand) does **not** fire its Battlecry. Any feature that says "cost
  X, gets you effect Y as a body" must gate that bonus on the minion having actually been *played*,
  which a scoring model, working card-by-card with no board context, cannot always assume.

### Deathrattle
- **What it does:** Triggers an effect when the minion (or weapon, in some sets) dies.
- **Why it matters:** Adds resilience to removal — the minion "pays twice." Strong against
  single-target removal, weaker against AoE that would have killed several bodies anyway (still
  fires once per body though).
- **Detectability:** Tag (`GameTag.DEATHRATTLE`). Magnitude is text-only.
- **Interaction to get right:** Deathrattle fires regardless of *how* the minion entered play —
  played from hand, summoned by another effect, or transformed into. This is the counterpoint to
  Battlecry: Deathrattle and stats survive a summon, Battlecry does not.

### Taunt
- **What it does:** Enemies must attack this minion before any other minion (does not force them
  to attack at all, and does not affect hero-power/spell targeting).
- **Why it matters:** Board control/tempo tool; protects the rest of the board and the hero.
  Devalued by Rush/Charge minions and direct removal that can ignore it entirely, and by an empty
  board (nothing to protect).
- **Detectability:** Tag (`GameTag.TAUNT`), including auras that grant it, though "has Taunt right
  now" for an aura-granted case is technically runtime (the aura's presence), while "the printed
  card grants Taunt" is a tag/text fact and safe to use for draft scoring.

### Rush
- **What it does:** Can attack minions (not the hero, not the player) the turn it is played.
- **Why it matters:** Immediate tempo and removal-trading without the downside of Charge (can't
  snipe the hero same turn); strong defensively and for trading up.
- **Detectability:** Tag (`GameTag.RUSH`).

### Charge
- **What it does:** Can attack anything (minions or hero) the turn it is played.
- **Why it matters:** Maximum same-turn tempo/damage; historically the more powerful and more
  heavily costed of the two "can attack immediately" keywords.
- **Detectability:** Tag (`GameTag.CHARGE`).

### Divine Shield
- **What it does:** Absorbs the next instance of damage the minion would take (from any source);
  the shield is then removed. Additional simultaneous damage instances beyond the first still
  connect only if they land after the shield is consumed within the same resolution window, so
  in practice: "first hit is free."
- **Why it matters:** Effectively doubles the minion's survivability against single-hit trades,
  strong against AoE that deals one hit per minion, weak against multi-hit sources.
- **Detectability:** Tag (`GameTag.DIVINE_SHIELD`).

### Windfury
- **What it does:** Can attack twice per turn (Mega-Windfury variants attack four times; treat as
  the same family for detection purposes but note the different tag/value where it exists).
- **Why it matters:** Doubles effective attack-turn damage output; strongest on high-Attack
  minions or weapons, weak on 1-2 Attack bodies.
- **Detectability:** Tag (`GameTag.WINDFURY`, with a separate flag/value for the mega variant).

### Lifesteal
- **What it does:** Damage dealt by this card (minion attack, spell damage, or hero power in the
  Priest case) heals the controller's hero for the same amount.
- **Why it matters:** Converts damage output into life gain, valuable in grindy/control matchups
  and against aggressive decks; less valuable when the card wouldn't otherwise be dealing damage
  reliably (e.g., a Lifesteal minion that immediately gets removed before attacking).
- **Detectability:** Tag (`GameTag.LIFESTEAL`).

### Poisonous
- **What it does:** Destroys any minion it damages, regardless of the damage amount.
- **Why it matters:** Makes even a 1-Attack Poisonous minion a threat that trades up against
  anything; heavily favors small stat-line minions with Poisonous over raw stats.
- **Detectability:** Tag (`GameTag.POISONOUS`).

### Stealth
- **What it does:** Cannot be targeted by enemy spells, Hero Powers, or attacks until it attacks
  (or the game turn passes, depending on ruleset era) — check current-ruleset wording rather than
  assuming; UNVERIFIED: exact conditions under which Stealth breaks have varied across design eras
  and this document will not assert a single current rule.
- **Why it matters:** Guarantees at least one unanswered attack/board presence; strong on minions
  whose Battlecry/passive matters more than survivability, since it buys a "free" turn.
- **Detectability:** Tag (`GameTag.STEALTH`) for presence; whether the shield is currently "up" for
  a specific minion in a specific game state is runtime-only.

### Spell Damage
- **What it does:** Adds a flat bonus to damage dealt by the controller's spells (and some
  Hero Powers with the interaction, class-dependent).
- **Why it matters:** Multiplies the value of every damage spell in the deck rather than being a
  standalone effect — its value is deck-composition-dependent, not a fixed per-card value.
- **Detectability:** Tag (`GameTag.SPELLPOWER`/`SPELL_DAMAGE` field with an integer value). This is
  one of the cleanest tag-backed numeric mechanics — safe to treat as a scalar feature.
- **Note for synergy engines:** this is exactly the kind of mechanic that should feed a "spell
  damage pairing" bonus in a synergy model, since its value is realized only alongside damage
  spells already in the deck — see the project's own `MetadataSynergyEngine` for a live example of
  scoring this as deck-fit rather than a flat card bonus.

### Combo
- **What it does:** Grants a bonus effect if this card is played as the **second or later** card
  played from hand in the same turn (i.e., not the first spell/minion of that turn).
- **Why it matters:** Rewards tempo turns / low-curve chaining; nearly dead in an opening hand
  played as the turn's only action, strong when the deck can reliably chain cheap cards.
- **Detectability:** Tag (`GameTag.COMBO`) for presence. Whether Combo is actually "on" in a given
  turn is pure runtime state (has anything else been played this turn, and is this going first vs.
  second — going first, the very first turn of the game cannot combo at all since nothing else can
  have been played). This is exactly the kind of fact a mulligan/in-game advisor can reason about
  from board+turn state but a context-free draft score cannot.

### Discover
- **What it does:** Offers a choice of (usually three) cards, drawn from some defined pool, and
  puts the chosen one into hand (not into play — see "get vs. put into play" below).
- **Why it matters:** Card selection/quality smoothing; value depends heavily on the size and
  power level of the pool being discovered from (e.g., "Discover a spell" vs. "Discover a Dragon"
  vs. "Discover a card from your deck").
- **Detectability:** Tag (`GameTag.DISCOVER`) confirms the mechanic is present. The pool it
  discovers from is defined by a referenced entity/text and is NOT a simple scalar — a program
  wanting to value a specific Discover effect needs either a hand-built pool model or must fall
  back to treating "has Discover" as a coarse, text-derived quality signal.

### Tradeable
- **What it does:** Lets the player pay a small fixed mana cost to exchange the card in hand for a
  freshly drawn card, at any time they hold priority.
- **Why it matters:** A pressure-relief valve on dead/unplayable cards — most valuable on cards
  that can whiff (situational tech, high-roll payoffs) since it caps the downside.
- **Detectability:** Tag (`GameTag.TRADEABLE`).

### Quest / Sidequest / Questline
- **What it does:** A quest sits in play (usually from an opening-hand-only placement rule for
  base Quests) and completes a stated objective for a (typically) powerful reward; Sidequests are
  cheaper/faster variants with lighter rewards; Questlines are multi-stage quests that upgrade
  through several rewards as successive stages complete.
- **Why it matters:** A quest card is functionally *not* a normal card until completed — it
  occupies a board/hand slot for its own sake and its value is entirely contingent on completing
  it, which depends on the rest of the deck being built to enable it. An uncompletable quest in a
  deck that can't support it is closer to a dead card than to its face-value reward.
- **Detectability:** Tag (`GameTag.QUEST` / `QUESTLINE` / `SIDEQUEST`). This is exactly the kind of
  fact that should be checked on the actual tag, never inferred from the word "quest" appearing in
  text, because normal minions can reference quests in their text without being one (see the
  project's own dead-payoff logic, which gates strictly on these tags for this reason).
- **Format note (do not assume universality):** whether Questlines/Quests are legal in a given
  arena pool is a rotation/format decision, not a card-text fact — a rule can be written correctly
  and simply never fire in a season where the mechanic is excluded from the pool. Do not treat "the
  rule never triggered in testing" as evidence the rule is wrong.

### Outcast
- **What it does:** Grants a bonus effect if this card is in one of the two outermost slots of the
  hand (leftmost/rightmost) when played.
- **Why it matters:** Rewards specific hand-management/ordering; largely invisible to a
  card-in-isolation score and only relevant to in-game sequencing advice.
- **Detectability:** Tag (`GameTag.OUTCAST` family) for presence. Whether it is currently
  satisfied is **runtime-only** and specifically hand-*position*-dependent, which most game-state
  APIs do not even expose reliably — treat "is Outcast active" as generally undetectable in
  practice, not just theoretically runtime.

### Magnetic
- **What it does:** Lets a Mech minion be played "fused" onto another friendly Mech, combining
  stats and text onto one body instead of occupying a separate board slot.
- **Why it matters:** Board-slot efficiency and stacking value (multiple Magnetic effects can pile
  onto one Mech); requires an existing Mech target to be worth using as intended — playing it
  stand-alone still works but forgoes the upside.
- **Detectability:** Tag (`GameTag.MAGNETIC`) for presence. Whether a valid fuse target exists at
  play time is runtime-only.

### Reborn
- **What it does:** When the minion dies, it returns to play once with 1 Health (and no
  Deathrattle re-trigger loop — Reborn is consumed on that return).
- **Why it matters:** A built-in "second life," similar in spirit to Divine Shield but tied to
  death rather than damage — strong against removal, weak against anything that deals with a
  1-Health body trivially (most things).
- **Detectability:** Tag (`GameTag.REBORN`).

### Colossal
- **What it does:** When played, summons a set of additional minions (defined by the card) into
  adjacent-ish board slots alongside the main body, per a fixed list on the card.
- **Why it matters:** Effectively a multi-body Battlecry-summon baked into the card; the summoned
  extra bodies did **not** enter via Battlecry (see interaction below) so their own Battlecries, if
  any, do not fire.
- **Detectability:** Tag/structured field listing the summoned set is typically available; treat
  the *summoned companions* as text/structured-reference data, not a simple boolean.

## Card types with their own rules

### Location cards
- **What they do:** A permanent-adjacent card type that occupies a slot but is not a minion and
  cannot attack or be attacked; instead it is *activated* by the player paying its activation cost,
  which triggers an effect and starts a cooldown before it can be used again; it has Durability
  instead of Health, reduced by external damage.
- **Why it matters:** Value accrues over multiple turns of activation, so a Location that dies
  before it can be used a few times underperforms its printed ceiling; unlike minions, opponents
  cannot Taunt-bypass or attack-trade with it, only damage it directly.
- **Important correction:** Hearthstone has **no per-player Location slot limit** analogous to the
  7-minion board cap — the practical cost of stacking Locations is tempo (mana spent playing them)
  and general board space pressure, not a hard slot exclusivity rule. Any synergy model treating
  Location "crowding" as a bonus/penalty should model it as the softest of its crowding signals for
  exactly this reason.
- **Detectability:** Card type is a tag (`CardType.LOCATION`); activation cost/cooldown/effect are
  structured fields but the qualitative value of the effect is text-derived.

### Weapons
- **What they do:** Equip to the hero, granting Attack/Durability; the hero can then attack using
  the weapon's stats, consuming one Durability per attack (see Hero Power vs. Hero Attack below for
  why this matters relative to removal).
- **Why it matters:** Extends the hero into a repeatable removal/damage tool without spending a
  card each turn once equipped; vulnerable to weapon-destruction effects and to the hero taking
  return damage when attacking into a minion.
- **Detectability:** Card type tag (`CardType.WEAPON`) with Attack/Durability as structured fields.

### Hero cards
- **What they do:** Replace the player's current hero (and typically its Hero Power) with a new
  hero, new Armor value, and a new Hero Power for the rest of the game.
- **Why it matters:** A full class-agnostic (usually) value/tempo/survivability package in one
  card; often not comparable to a minion/spell on the same curve at all.
- **Detectability:** Card type tag (`CardType.HERO`). Per the mulligan advisor's own rule set (see
  `IMulliganAdvisor.cs`/`DeckMulliganAdvisor.cs` in this project): a Hero card gets **no mulligan
  verdict** at all, precisely because it doesn't fit the tempo-vs-quality model the other verdicts
  are built on.

### Spell Schools
- **What they are:** A tag-level classification of spells (e.g., Fire, Frost, Nature, Arcane,
  Shadow, Holy, Fel, Fun) independent of class, used by other cards' text to grant conditional
  bonuses ("if you've cast a Fire spell this game...").
- **Why they matter:** Payoff/synergy axis parallel to tribes but for spells; a spell school payoff
  is dead without spells of that school in the deck, exactly analogous to a tribal payoff being
  dead without members.
- **Detectability:** Tag (`Card.SpellSchool`) — clean, structured, safe to use directly for
  synergy pairing, unlike tribal dependency which needs the whitelist treatment described below.

### Tribes / minion types
- **What they are:** A classification (Beast, Dragon, Demon, Elemental, Mech, Murloc, Naga,
  Pirate, Quilboar, Totem, Undead, plus the "All" amalgam tag, roughly a dozen-plus tribes
  depending on era) used by other cards as a payoff condition or a summon-pool filter.
- **Why they matter:** Drive "tribal" archetypes where members enable each other's payoffs; a
  payoff card with zero members in the deck it's drafted into is close to a dead card for its
  text (though its stats may still be playable — see the standalone-function guard below).
- **Detectability:** Tag (`Card.Race`, including `Race.ALL` for amalgams). Clean and structured.
  The hard part is not detecting the tribe — it's detecting **dependency** on the tribe, which is
  a text-parsing problem; see the dedicated interaction section below.

## Secrets, Auras/Objectives, and other systems

### Secrets
- **What they do:** Played face-down for a mana cost, then trigger automatically (usually on the
  opponent's turn) when a stated condition is met, resolving an effect and being revealed/consumed.
- **Why they matter:** Ambush value and information/bluff pressure — the mere possibility of a
  Secret changes opponent behavior even unrevealed; classic play patterns (bait-then-punish) exist
  around specific Secret pools.
- **Detectability:** A structured "is Secret" classification exists (secrets are their own spell
  subtype/tag), but the **trigger condition** and effect are text-only, and whether a specific
  Secret is currently active/unrevealed on a board is runtime-only. A card-level score can know
  "this is a Secret" and roughly how impactful its class of effect tends to be from text, but
  cannot know whether it will actually trigger in a given game.

### Auras / Objectives
- **What they do:** "Aura" here refers to continuous, non-instantaneous effects that apply while
  their source is in play (buffs to other minions, stat modifications, blanket rule changes) rather
  than a one-time trigger. "Objective" is a newer permanent-in-play card type structurally similar
  to a Location/Quest hybrid, tracking progress toward a stated goal and delivering a reward on
  completion, distinct from both.
- **Why they matter:** Aura value is contingent on the source surviving and on there being targets
  to buff — removing the aura source can retroactively erase board-wide value gained. Objective
  value, like Quests, is entirely contingent on completion being achievable in that deck.
- **Detectability:** Whether a card grants a continuous aura vs. a one-shot effect is generally
  only recoverable from text (there isn't a universal "IS_AURA" tag covering the general case,
  UNVERIFIED beyond individual known keyword-backed exceptions like Taunt-granting auras). Whether
  an aura is currently live and what it is currently buffing is runtime-only. Objective as a card
  type may be tag-backed similarly to Quest/Location; treat its specific progress condition as
  text-only, same as Quests.

### Overload
- **What it does:** Locks a stated amount of the player's mana crystals unavailable at the start of
  their *next* turn only (not decked-forever) after this card is played.
- **Why it matters:** A discount that is repaid next turn, not free — its cost is highest exactly
  when the player wants full mana flexibility (e.g., holding up a counterspell/removal, or wanting
  to curve out on the following turn), and lowest when the following turn's plan doesn't need the
  locked mana anyway.
- **Detectability:** Tag with an integer value (`GameTag.OVERLOAD`). Clean.

### Freeze
- **What it does:** Prevents the frozen character from attacking or (for a full-freeze) taking any
  action; a frozen character who has not yet acted this turn typically thaws at the start of their
  next turn (frozen after already acting on the current turn skips the *following* turn instead —
  UNVERIFIED on exact edge-case wording across all sources; treat as a general "loses about a turn
  of action" effect for scoring purposes rather than asserting precise timing).
- **Why it matters:** Tempo denial without needing to kill anything; can lock down a hero swing or
  a key attacker for a turn cycle.
- **Detectability:** Tag (`GameTag.FREEZE` presence on damage/effect sources) for cards that
  *apply* Freeze as part of their effect. Whether a specific character is *currently* frozen is
  runtime-only board state.

### Silence
- **What it does:** Permanently strips all card text, keywords, and enchantments from a minion,
  leaving only its base printed stats.
- **Why it matters:** Answers to a minion whose *text* is the threat (a big Deathrattle, an aura, a
  Taunt-granted body) rather than its stats — useless or actively bad against a minion whose stats
  alone are the threat (e.g., silencing a vanilla-statline beater accomplishes nothing, and
  silencing a minion that already used a one-time Battlecry has already lost most of its value).
- **Detectability:** Tag (`GameTag.SILENCE`) marks cards that *apply* Silence. Whether Silencing a
  *specific* target minion right now is a good trade is entirely runtime/board-context — not a
  property of the Silence card itself, and definitely not something a synergy engine should try to
  score card-in-isolation.

### Transform
- **What it does:** Replaces a character (usually a minion) with a different, specific or random
  minion, discarding the original's stats/text/enchantments/tags in favor of the new form's (this
  differs from Silence, which strips text but keeps the *same* minion and its printed base stats).
- **Why it matters:** Universal answer to buffed/enchanted threats since it ignores whatever was
  stacked onto the original; double-edged when used on your own minion (loses your own
  investment) vs. as removal (erases the opponent's).
- **Detectability:** The *effect type* ("this card transforms something") is generally only
  recoverable from text; there is no single universal tag comparable to `SILENCE`/`FREEZE` for this
  across all cards, UNVERIFIED whether some specific implementations use an internal tag not
  exposed at the card-definition level consumable by a plugin.

### Summon effects
- See the dedicated interaction section below — this is one of the highest-value things to get
  right, and the least safe to guess about.

### Cost-reduction effects
- See the dedicated interaction section below (self-discount vs. discounting others).

## Interactions that are easy to get wrong

### 1. A summoned minion does not trigger its Battlecry — but Deathrattle, Taunt, and stats do
A minion's Battlecry is defined to trigger **"when played from hand."** Any mechanism that instead
puts the minion onto the board — being *summoned* by another card's effect, being the companion
body from a Colossal card, being resurrected, being transformed into — does not go through the
"played from hand" event, so Battlecry does **not** fire.

Everything else about the minion is unaffected by how it arrived: its printed stats, Taunt,
Divine Shield, Deathrattle, and any other passive/keyword all function normally regardless of
entry method, because those are properties of the minion *being in play*, not of the play action.

**Consequence for a scoring/synergy model:** never assume a minion's full text value is realized
just because it ends up on the board via some other card's summon text. A feature that says "this
card summons a copy of X" should value X's *stats and Deathrattle* fully but should heavily
discount or zero out any Battlecry-dependent value X would normally provide.

### 2. Summon vs. put into play vs. cast vs. get/add to hand — these are different verbs with different consequences
| Phrase | What actually happens | Battlecry fires? | Consumes mana? | Occupies a hand slot first? |
|---|---|---|---|---|
| "Summon a minion" | Minion appears directly on the board | No | No | No |
| "Put [a card] into play" | Used for non-minion permanents (weapons, etc.) entering play directly, bypassing hand | Effect-dependent, generally treated like a summon, not a "play" | No | No |
| "Cast [a spell]" | The named spell's effect resolves as if played, without needing to be played from hand normally | Battlecry N/A (spells don't have Battlecry); triggers that key off "cast a spell" DO fire | No (unless the effect explicitly charges mana) | No |
| "Add [a card] to your hand" / "Draw" | Card enters the hand as a normal card, to be played later at its own cost | N/A until actually played | Not yet — will be paid when played | Yes — and can be discarded/hand-size-capped like any card |

**Why this distinction is load-bearing for a draft/mulligan advisor:** "get a copy in hand" is a
*deferred* value (another card slot spent later, subject to hand-size caps and Discover/board
timing) while "summon" is *immediate, free* board presence with no further mana cost and no
Battlecry. Treating these as interchangeable when estimating a card's tempo will misprice both
directions — overvaluing a "summon a token" effect as if it cost a card to cash in, or undervaluing
a "put a spell effect... but actually adds a card to hand" effect as if it were free tempo.

### 3. Hero Power damage vs. hero attack — who removes small minions for free, and who has to swing and eat a counter-hit
Several classes have a Hero Power that deals direct damage (a clean example being a 2-damage
Hero Power), which can outright kill small minions **at zero risk to the hero** — no minion attacks
back at a Hero Power.

By contrast, removing a minion by attacking it with the **hero's own body** (via an equipped
weapon, or a Hero Power that grants an attack, or a Rush/Charge-style hero effect) means the hero
is the attacker, and — unless the target has 0 Attack or is prevented from retaliating (e.g.,
already dealt with, or the attack kills it outright before it can strike back per combat
resolution rules for simultaneous damage) — the defending minion's Attack value hits the hero back
during that combat. This is a materially different risk profile: a damage-dealing Hero Power is a
repeatable, riskless removal tool against small things; hero-attacking is removal with a
health-cost gamble that scales with the size of what's being traded into.

**Consequence for scoring:** do not treat "this class can Hero-Power-kill X-health minions" and
"this class can equip a weapon to trade with minions" as equivalent removal capability when
reasoning about class matchups or mulligan holds against small boards — the first is free, the
second costs hero health proportional to the target's Attack.

### 4. Mentioning a tribe is not depending on a tribe — generation vs. dependency, and anti-tribe tech
A card's text can *reference* a tribe in at least three structurally different ways, and only one
of them creates a real synergy dependency:

- **Dependency:** the card's value scales with how many members of the tribe the *player* has —
  e.g., "your Beasts have +1/+1," or "deal damage equal to the number of Dragons in your deck."
  This is a genuine payoff: zero members of the tribe means the effect does little or nothing.
- **Generation:** the card *creates or fetches* members of the tribe itself — e.g., "summon a
  random Beast," "Discover a Dragon." This is **self-sufficient**: the card supplies its own
  targets and needs no other copies of the tribe in the deck to function. Scoring this as "depends
  on Beasts" and then penalizing it for a Beast-light deck is a category error — the card doesn't
  care what else is in the deck; it makes its own.
- **Anti-tribe tech / opponent-referencing:** the card cares about the *opponent's* board or the
  tribe in a punishing sense — e.g., "destroy an enemy Pirate," "deal damage to all enemy Murlocs."
  Here the relevant population is the *opponent's* board, not the player's deck, so the card should
  score *up*, or at least not down, precisely when the player's *own* deck has none of that tribe —
  the opposite of a dependency read.
- **Naming collision with class names:** some tribe-name-shaped substrings match unrelated text —
  the clearest known case is "Demon" appearing inside "Demon Hunter" as a class name, which is not
  a Demon tribal reference at all. Any text-pattern approach must strip class names before matching
  tribe words, or every card that merely states its own class in text will misread as having a
  tribal dependency.

**The practical rule this project's own synergy engine encodes** (and any similar system should
adopt): require a **real dependency pattern** — a defined, curated set of phrasings for "your
tribe-members," "cost reduced by tribe count," etc. — rather than a blanket keyword search, and
**exempt** cards that supply their own targets (generation) or that target the opponent's board.
A blind blacklist-of-verbs approach does not converge: it has to keep growing forever as new
cards are printed with new phrasing, and it still gets edge cases wrong in both directions (wrongly
flagging self-sufficient generation cards as dependent, and wrongly under-valuing anti-tribe tech).
- **Detectability of the tribe itself:** Tag, always. **Detectability of dependency-vs-generation-
  vs-anti-tech:** text only, and only reliably with a maintained, narrow, re-validated pattern set —
  never a bare "does the text contain the tribe word" check.

### 5. Printed cost is not always real cost — self-discounting cards vs. cards that discount others
Two structurally different mechanics both change "what this card actually costs to play," and they
have opposite implications for how a scoring/mulligan model should treat the printed cost field:

- **Self-discounting / modular cost:** the card's *own* cost is reduced by some condition tied to
  the card itself or to actions already taken (e.g., a card that costs less for every copy of
  something already played, or a card whose cost is reduced by a resource spent earlier in the
  same or a previous turn — the general "Prepare"-style pattern of paying cost now to make a later
  card cheaper, or a card that discounts *itself* based on board/hand conditions). For these, the
  **printed mana cost is not a reliable signal of the turn on which the card is actually playable**
  — a nominally expensive card can come down far earlier than its curve position suggests, or a
  nominally cheap card may in practice only be cheap after a specific setup.
- **Discounting other cards:** the card's *effect* reduces the cost of *other* cards (in hand or of
  a stated type), leaving its own printed cost untouched. Its own tempo is exactly what's printed;
  its *value* is deck-composition-dependent (a discount is worthless without cards in the deck that
  benefit from being cheaper, and most valuable on decks stacked with cards that want the discount).

**Consequence for a tempo/mulligan model:** the project's own mulligan advisor explicitly withholds
a verdict for "a card whose printed cost is not its real cost (self-discounting, modular)" — this
is the correct instinct, because the TEMPO half of a tempo-times-quality model is built on "what
turn does this play," and a self-discounting card breaks that assumption in a way no per-card rule
can safely resolve without runtime hand/board state. A card that discounts *others*, by contrast,
keeps its own tempo legible and can still be scored on its own printed cost — it's the *synergy*
value of pairing it with cheap-loving cards that would need deck-context modeling, not its own
playability turn.
- **Detectability:** whether a card is self-discounting vs. other-discounting is knowable from a
  structured cost-reduction effect definition on some engines, but in the general case is a
  text-parsing problem, and whether the discount is *currently active* for a self-discounting card
  is runtime-only (depends on board/hand history that turn or across the game).

## Summary table: detectability at a glance

| Mechanic | Presence detectable | Magnitude/condition detectable | Currently-active detectable |
|---|---|---|---|
| Battlecry | Tag | Text | N/A (fires once, on play-from-hand only) |
| Deathrattle | Tag | Text | N/A |
| Taunt | Tag | — | Runtime if aura-granted |
| Rush | Tag | — | N/A |
| Charge | Tag | — | N/A |
| Divine Shield | Tag | — | Runtime (consumed or not) |
| Windfury | Tag | — | N/A |
| Lifesteal | Tag | — | N/A |
| Poisonous | Tag | — | N/A |
| Stealth | Tag | — | Runtime (broken or not) |
| Spell Damage | Tag (numeric) | Tag | N/A |
| Combo | Tag | — | Runtime (turn sequencing) |
| Discover | Tag | Text/referenced pool | Runtime (what's offered) |
| Tradeable | Tag | — | N/A |
| Quest/Sidequest/Questline | Tag | Text | Runtime (progress) |
| Outcast | Tag | — | Runtime (hand position) — generally undetectable in practice |
| Magnetic | Tag | — | Runtime (valid fuse target) |
| Reborn | Tag | — | N/A |
| Colossal | Tag/structured list | Structured list | N/A |
| Location | Type tag | Text (activation effect) | Runtime (cooldown state) |
| Weapon | Type tag | Structured (Attack/Durability) | Runtime (current Durability) |
| Hero card | Type tag | Text | N/A |
| Spell School | Tag | — | N/A |
| Tribe | Tag | — | N/A (presence is static) |
| Tribal dependency (vs. generation/anti-tech) | — | Text, curated patterns only | N/A |
| Secret | Type/subtype tag (mostly) | Text | Runtime (armed/revealed) |
| Aura | Generally text-only | Text | Runtime (source alive, targets) |
| Objective | Type tag (where present) | Text | Runtime (progress) |
| Overload | Tag (numeric) | Tag | Runtime (locked-next-turn state) |
| Freeze (applying) | Tag | Text | N/A for the source card |
| Freeze (being frozen) | — | — | Runtime only |
| Silence (applying) | Tag | — | N/A |
| Transform | Generally text-only | Text | N/A |
| Summon effects | Tag/structured where defined, else text | Text | N/A |
| Self-discount cost | Text (mostly) | Text | Runtime (condition met or not) |
| Discount-others cost | Text (mostly) | Text | N/A for own tempo; runtime for realized value |

## Practical guidance for a scoring/synergy engine

- **Trust tags for branching logic; treat text matches as heuristics that need a validated
  whitelist**, not a bare keyword search — the tribal-dependency case is the canonical example of
  why a blacklist-only approach fails and keeps failing as new cards print.
- **Never fold runtime-only facts into a context-free draft/pack score.** Combo, Outcast,
  Silence-target-quality, Freeze-target-state, and "is this Discover pool good right now" are all
  facts about a moment in a specific game, not about the card. They belong in in-game advisors
  (mulligan, Discover-choice, board-state facts reported in words) — never baked into the
  win-rate-blended pick score.
- **Model Battlecry-suppression on summon explicitly** wherever a card's own text summons another
  card, rather than reusing that summoned card's full base value.
- **Keep the self-discount / discount-others distinction visible** anywhere "cost" is used as a
  tempo proxy (curve-fit synergy, mulligan tempo judgment) — a printed cost is a default, not a
  guarantee, and the failure mode of ignoring this is a false read on what turn a card is playable.
