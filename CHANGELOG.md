# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.8] - 2026-07-30

### Changed

- **The mulligan stops calling half your deck a bomb.** An expensive card escaped the "too slow" toss
  by clearing an absolute score bar — but scores are anchored on the whole card pool, so on a strong
  class most cards clear it. Measured on a live Demon Hunter run: the class's median card scored above
  the bar, ~60% of the cards offered counted as bombs, and a card *below* its own class's median was
  exempting itself. A bomb is now one of the best cards **you own**, counted per distinct card so a
  duplicate cannot push it down the ranking, with an absolute floor that can only veto — the best three
  cards of a bad deck are not bombs. When the win-rate feed has covered less than half your deck the
  answer is "cannot tell" and nothing is tossed.
- **The Coin is spent once.** It was credited to every card in hand at the same time, so one mana
  crystal made a 3-drop "play on turn 2" and a 5-drop "play on turn 4" in the same hand — and since a
  card counts as top end from 5 mana, going second no 5-drop was ever judged as top end at all. Its
  tempo now goes to a single card, the cheapest early play, and never to an expensive one: coining out a
  5-drop is what you do with a hand you should have thrown away. The Coin as a Combo enabler is
  unchanged, being a condition rather than a tempo swing.

### Fixed

- **The run screen no longer skips the moment you finish a draft.** Straight after the 30th pick the deck
  panel and your own rating did not appear at all — you had to play a match first, which cleared the stale
  choices the client keeps in memory and let the panel through. The run screen was gated on an EMPTY choice
  zone, and a finished draft has three choices still sitting in it. Poll routing is now one pure, tested
  function of the session state, so a choice count can no longer decide which screen you are on.

## [0.1.7] - 2026-07-28

### Added

- **Your own arena rating on the run screen, and the rank it would put you at.** A panel of its own
  above the deck stats: the rating exactly as the client reports it, plus — when you are not listed —
  the position that rating *would* enter the leaderboard at. Both cost nothing: the rating is read
  from the client with no network at all, and the projected rank is a count over the board already
  cached locally. Worded "would enter", never "your rank", because a listing also needs a seasonal
  minimum of games. Offered for **Underground only**: that board publishes the same rating the client
  reports (verified live), while the Normal Arena board publishes average wins per run, and placing a
  rating on a board sorted by average wins would be an invented number.
- **The current arena opponent's rank, beside your own, for the whole match.**
  `OpponentIdentityWatcher` reads the opponent's BattleTag; `ArenaLeaderboardSource` looks it up
  against Blizzard's own `leaderboardsData` API — first-party data, display-only, never blended into
  any score. **Opt-in and OFF by default** ("Opponent leaderboard rank" in the plugin menu): it is
  continuous background traffic against Blizzard's own site, so nobody's bandwidth pays for it without
  asking. Shown in the same panel as your own standing, in a different colour and behind a divider,
  because confusing whose rank is whose is the one mistake that panel must not make. A shared display
  name resolves to the best rank *and says how many players share it*, never to one rank asserted as
  certainly theirs; a name the crawl has not reached says "checking" rather than "not listed", because
  those are different facts.
- **A rule for OUTCAST cards at the mulligan, because the effect is positional.** Outcast fires only
  from the leftmost or rightmost card in hand, and the two edges are not equivalent: the left one is
  stable, while a card always arrives on the right before your first turn — your draw, or the Coin —
  so a rightmost Outcast is gone before it can be used, and the card that displaces it may be
  expensive enough to make the whole plan moot. Read off `GameTag.OUTCAST` and never the text: 13 cards
  say the word "Outcast" without having the mechanic, Illidari Studies among them, and a text match
  would make positional claims about cards that have no positional behaviour. The rule can only
  DEMOTE a Keep to Situational, never toss: mulliganing rearranges the very positions it reads, so a
  positional toss would invalidate its own premise, while Situational asserts nothing about what to do
  and leaves the reading intact.
- Most opponents will not resolve, and two separate reasons matter. The board covers only a few
  thousand players per region, and **a listing needs a seasonal minimum of games**: measured live, a
  rating well above the board's own threshold was still absent. So "not on the leaderboard" says
  nothing about how good a player is, and nothing in the plugin implies that it does.

### Changed

- **A cheap spell that puts bodies on board now counts as one of the deck's early plays.** It already
  counted when judging that card — the documented Mining Casualties rule — but the count that decides
  whether a deck is *thin* on early plays saw only minions, weapons and locations, so the same card was
  an early play in one place and did not exist in the other. One definition now serves both.
  **This changes advice**: 79 more cards count across the cost-1-2 pool (3.3%, spread very unevenly —
  Druid 18, Mage 2), so decks look less thin, the early window widens less often, and some 3-drops that
  were kept are now demoted. No wrong verdict was demonstrated; this is a consistency fix, adopted
  deliberately.
- The run screen is recognised in the `REDRAFTING` and `MIDRUN_REDRAFT_PENDING` states, not only
  `MIDRUN`. Around a redraft the client reports `REDRAFTING`, and in that state **nothing** appeared:
  the deck-review panel wants `EDITING_DECK`, the run panel wanted `MIDRUN`, and the standings panel
  hangs off the run panel — so the deck description came back only if you left arena and re-entered.
- **Silent code paths now say why they are silent.** Three separate "nothing is showing and nothing is
  logged" investigations this release each ended at a branch that returned without a word: an
  unrecognised session state, a scene that did not match (which returns *before* any diagnostic inside
  the watcher, so the watcher looked dead), and a choice list that is neither empty nor three. Each now
  logs once per distinct value. The deck description is still deliberately **not** shown during the
  redraft's `EDITING_DECK` phase — that screen already carries the scored cut list, and the numbers stay
  in the log.
- **The leaderboard crawl follows demand, not uptime.** It starts when you reach an arena screen,
  crawls one page at a time, and stops once no arena has been played for a while — rather than running
  for the whole session after a single match. One board per client at a time, only your current region
  (regions are separate shards, so the others hold nobody you can be matched against), gzip requested
  explicitly, and a full refresh pass targeted at 24 hours. Measured, the combination takes the
  worst-case client from ~171 MB/day to a few hundred requests proportional to how much arena you
  actually play.
- A display name published by more than one player now resolves to **all of them**, best rank first,
  rather than to whichever the crawl saw last — which, since it walks pages in rank order, was
  systematically the worst of them. Measured on live boards: about 1% of names are shared, and their
  holders sit hundreds of ranks apart.
- **The overlay's show/hide decision is now a pure class with a test per bug it caused.** Which screen
  is active and whether the overlay should be visible used to live inside the plugin's update loop,
  next to WPF and client calls — untestable, and the source of four separate overlay bugs, each found
  by someone watching a live client rather than by a test. `OverlayState` holds the rules; its tests
  are those four bugs written as regressions, and each was verified to go red when the corresponding
  mistake is put back.
- **The player's own standings panel refreshes instead of being built once.** The client's rating reads
  as null intermittently and the leaderboard cache loads on a background thread, so a panel built at
  render time could show no rating at all, or state that the crawl had not finished a pass over a cache
  that had. Neither corrected itself, because the screen does not change again.
- **A card, a hero power and a hero are three different kinds of thing, and now three different
  databases.** They shared one list and one set of named accessors, where the bare name went to
  whichever printing had the lowest dbf id — so `HSCard.IcyTouch` was the Mage HERO POWER rather than
  the Death Knight Frost spell of the same name, and a measurement written against it silently read
  the wrong card. Split into `CardDatabase`/`HSCard`, `HeroPowerDatabase`/`HSHeroPower` and
  `HeroDatabase`/`HSHero`: a name now only has to be unique among things of its own kind, and nothing
  that takes a card can be handed a hero power.
  The files follow: `Generated/HSDatabase.g.cs` (one compile unit, three databases) and three
  markdown files named like the rest of `docs/` — `hearthstone-cards.md`,
  `hearthstone-hero-powers.md`, `hearthstone-heroes.md`. The generator is `HSDatabaseGenerator` and
  its flag is `--dump-database`. It returns a LIST of files it writes, and the drift test iterates
  that list rather than naming the files itself, so a file nothing checks cannot be added by accident.
- **Repeated in-game Discover offerings are documented rather than papered over.** The debounce
  comment claimed to fix them; measured across two live sessions it does not, and the shortest gap
  between a repeated trio was 10 seconds. The comment now says so, records that HearthMirror exposes
  no per-choice id to key on, and the log line carries the diagnostic needed to establish staleness.

### Fixed

- **Four overlay bugs, and the rules that caused them are no longer untestable.** The arena run panel
  could sit on top of a live Battlegrounds game (nothing dropped it when the client left the arena
  screens); it could survive a switch between Underground and Normal Arena, showing the previous mode's
  run; it could vanish for the rest of the arena screen, because an attempt to fix the first hung the
  teardown on an event that also fires while you are still in arena; and a finished Discover's three
  plaques stayed drawn on the board, because the overlay had always relied on being HIDDEN to make
  stale content invisible — which stopped being true once the standings panel could keep the window
  visible through a match. The run panel now has its own teardown signal, distinct from the draft
  panel's and from leaving the scene; content is cleared when a screen goes rather than merely covered.
  The show/hide decision moved into `OverlayState`, a pure class with no WPF and no client calls, and
  its tests are these four bugs written as regressions — each verified to go red when the mistake is
  put back.
- A malformed number from a downloaded payload no longer kills the leaderboard crawl silently.
  Newtonsoft's `JToken` casts throw rather than returning null, and net472 swallows an unobserved task
  exception — so a rating stated as a string would have stopped the crawl for the session with nothing
  in the log. Values of the wrong type are dropped, the way every other poisoned field already was.
- The crawl no longer loses progress on a transient network failure, retries only failures that are
  actually transient (a 4xx is a refusal and is never retried), and no longer keeps running after the
  plugin is disabled or unloaded.

- **DIVINE SHIELD ends the one-health question before it is asked.** Hardlight Protector, a 2/1 Mech
  *with* Divine Shield, was told "1 health, dies to Ghoul Charge" — it does not: the shield eats the
  first instance of damage whatever its size, so a ping bounces off and the Charge Ghoul trades
  itself for the shield. Read off the card, not the text. 14 collectible minions at cost 3 or less
  have one health and Divine Shield, Argent Squire among them.
- **A card that UPGRADES while you hold it is no longer simply "too slow".** Infuse states it as a
  keyword ("Infuse (3): Gain +2/+2") and a handful of older cards say it longhand ("Whenever a
  friendly minion dies while this is in your hand, gain +1/+1"): for those, holding the card IS the
  plan, which is the same reason the trade-upside rule exists. 29 Infuse cards in the pool, 13 at
  cost 5 or more — the only place the top-end rule can fire. Situational, never Keep: whether the
  enablers happen is exactly the judgement no data here can settle.
  The veto is the whole distinction: **"in hand or deck" is not a reason to hold anything.** Lotus
  Troublemaker's counter ticks whether you keep it or not, so it stays an ordinary top-end card — it
  is written precisely so you do not have to hold it.
- **The run panel said "thin at 5-drop" beside a curve row showing `5:3`.** The thinnest slot was
  computed over MINIONS against a minions target while the row above it counts all 30 cards, so the
  two contradicted each other a line apart — the same deck had 6:1 and 1:2 sitting visibly thinner.
  Removed from the panel: a statistic a reader cannot reconcile with the numbers beside it is worse
  than none, and the curve row already shows where the deck is short. The log keeps it, labelled
  "thinnest body slot" next to the bodies curve, where it is coherent.
- **The run panel labels its mean.** "midrange (3.5)" now reads "midrange (avg cost 3.5)": a bare
  number beside a word does not say what it counts.
- **A cheap spell that SUMMONS is now an early play, like the body it puts down.** Mining Casualties
  ("Summon two 1/1 Silver Hand Recruits…") got no verdict at all: the early-keep rule asked what a
  card IS — minion, weapon, location — and a spell failed the test however much board it made. The
  rule against cheap spells stands for the cards it was written for, removal and reach, which answer
  a board that does not exist yet. Two guards, both found by running the rule over the pool rather
  than by reasoning about it: QUOTED text belongs to the token, not the card (Mining Casualties' own
  recruits carry a Deathrattle, and reading it as the spell's condition rejected the card the rule
  exists for), and a quest is excluded by its TAG, because a quest's text states what you must DO
  and reads exactly like a summon — Jungle Giants, Unite the Murlocs and Unseal the Vault all
  matched until then. 117 cheap spells qualify.
- **A cheap minion whose text needs a WEAPON is no longer a turn-1 keep.** Air Guitarist
  ("Battlecry: Give your weapon +1 Durability") is a 1/1 whose text does nothing until a weapon
  exists, and on turn 1 none can — nothing is equipped before your first turn, which makes this
  stricter than the friendly-board rule it sits beside. The dependency list named only minion things
  ("your minions", "your beasts", …), so "your weapon" fell straight through it. Vetoed for a card
  that equips one itself, the same shape as the synergy engine's generation veto. Measured through
  the rule, not by grepping the pool: 15 collectible cards at cost 2 or less, mostly Rogue's poisons
  — a grep said 20 because it counted hero powers the rule never sees.
- **A test comment described a rule that does not exist.** It claimed a 1-attack body was excluded
  from the cheap-play keeps by a `MinContestingAttack` constant; there is no such constant anywhere
  in the source, and a 1/1 is treated as a cheap play today. Corrected in place rather than deleted,
  because that comment is how a fixture goes on looking justified.

## [0.1.6] - 2026-07-27

### Added

- **The mulligan now reads the OPPONENT'S HERO POWER, and "1 health dies for free" stopped being a
  universal claim.** It never was one: derived from the cards rather than from a class list, only
  Mage's Fireblast and the Death Knight's Charge Ghoul kill a one-health body for nothing. Druid,
  Demon Hunter and Rogue can kill it only by swinging the hero and eating its attack — which is
  exactly why a 3/1 and a 2/1 are different cards to hold — and the other six answer it not at all.
  So the same 2/1 is now a keep against a Warrior and a toss against a Mage, and the reason names the
  card: *"1 health, dies to Fireblast"*.
  Keyed on the hero power CARD, never the class, because a dual-class arena hero does not identify its
  hero power. Read fresh per hand and never cached, so a stale value cannot answer the previous
  match's question, and an unreadable one keeps the old advice rather than relaxing a rule on missing
  data. Classification validated over all 2138 hero-power cards; two pool-driven corrections came out
  of it — a granted "Deathrattle: summon a Rush token" is not the hero power summoning anything, and
  damage it cannot aim ("to a random enemy") is not an answer to a particular minion.
- **Secrets and Auras are dependency axes now, not blind spots.** The engine modelled 12 tribes and 7
  spell schools but nothing else, so Chatty Bartender ("if you control a Secret, deal 2 damage to all
  enemies") took no penalty with zero Secrets drafted while Mirror Dimension's dragon clause did — in
  the same offered triple, seen live. Membership comes from a `GameTag`, so it is as objective as a
  race, and the availability damping is measured per class from the feed we already fetch: Secrets are
  ~4.2% of Mage, Hunter and Rogue slots and 0% of the other eight. That measurement is also the
  argument against hard-coding it — Paladin has Secrets in principle and none in this pool.
- **The deck mechanics summary is shown on the RUN SCREEN** — the one between matches, with Play,
  Retire and your wins on it: minion curve, hard removal, damage, AoE and card draw. Gated positively
  on the client reporting a run `MIDRUN` with a complete deck, never on "no other screen is showing":
  that screen reports as the DRAFT scene, so the scene alone cannot tell it from the main menu, and
  this is the exact screen that produced a ghost overlay twice.
- **A deck mechanics summary in the log**: minion curve, hard removal, damage, AoE and card draw.
  Descriptive only, and deliberately so — there is no public per-deck data to fit "this deck needs more
  removal" against, so it states counts the player can check against their own deck and draws no
  conclusion from them. Every count reuses a feature the scoring model already extracts, so it cannot
  drift from what the model sees and adds no new text patterns.
- **A summoned minion's Battlecry never fires, and the score now knows it.** A new bounded synergy
  component reads the cards that pull minions *out of your deck* and judges them by what your cheap
  minions are: two 1-drops fetched from a deck of Deathrattles are two real cards, the same two
  fetched from a deck of Battlecries are two blank bodies. It is a rule of the game, and no win-rate
  feed can see it — the feed averages the card over every deck that ever drafted it. Inside the
  usual ±3 clamp (capped at 1.2), so it breaks ties and can never override a solid win-rate.
- The trigger was **narrowed after checking it against the live card pool**, not against the tests:
  it fires only on cards whose text states a cheap limit ("1-Cost", "costs (2) or less"), which is
  6 cards. Of the 45 collectible cards that summon from the deck, most point elsewhere — Cowardly
  Grunt and Maxima Blastenheimer take any minion, Meat Wagon and Lead Dancer go by attack, Finja by
  tribe — and judging those by the 1-2 bucket can invert the sign. Cards that fetch a *known* card,
  their own body, are excluded: Patches the Pirate says "summon this", while Persistent Peddler and
  Moragg name themselves, so the card's own name is checked too.
- **The card pool is now committed to the repo**, so the "validate against the pool, not against the
  tests" rule this project keeps learning the hard way no longer needs a Hearthstone install to
  follow: `docs/CardDatabase.md` to grep, and `Generated/CardDatabase.g.cs` compiled by the three
  test projects. Neither is referenced by the plugin — the shipped DLL is byte-for-byte the size it
  was, because the plugin reads the same data from HearthDb at runtime.
  On licensing, stated plainly rather than assumed: HearthDb's *code* is MIT, but its card *data*
  comes from `HearthSim/hsdata`, which ships no licence and is extracted from Blizzard's client. So
  these files carry Blizzard content under the Fan Content Policy, non-commercially, and not under
  MIT. An earlier note calling a dump of data already vendored in HDT "no licence concern" was
  wrong, and has been corrected where it was written.
  Two tests keep it honest, because a file no code reads and no test checks is wrong the moment
  HearthDb moves: the pool's own invariants (unique ids, a total ordering, no line breaks in card
  text, and every tag axis carried by at least one card — the guard against a tag read that
  silently yields nothing), and a drift test that diffs both files byte-for-byte against the
  generator's own output and names the first line that moved.
- **Test fixtures name their cards instead of carrying ids.** `HSCard.Tuskpiercer` replaced
  `"BAR_330"` plus a trailing comment, across every test in the repo: nothing but the deliberately
  unresolvable ids and the synthetic HSReplay payload — where the id is the feed's own content and
  the parser under test is what resolves it — still holds a card id. Each card was checked to keep
  the same dbf id, so no test changed what it measures.
  It immediately paid for itself twice. A fixture labelled "Metamorphosis, names its own class" was
  in fact **Chaos Nova**, whose entire text is "Deal $4 damage to all minions" — so the test that
  exists to prove class names are stripped before tribe matching was asserting nothing, and would
  have gone on passing with that code deleted. And the hero-power suite looked up "Spread Shot" by
  NAME, of which the pool holds two, so which card it tested depended on enumeration order.
- **One command runs the whole gate** (`scripts/gate.ps1`): build, format, all three suites, the
  no-HDT suite on its own, slopwatch, and the offline refit — in CI's order, with the absolute
  HSDTPath that a hand-typed command keeps getting wrong.

### Fixed

- **A hand that cannot play anything until turn 4 now goes back whole, instead of getting three
  shrugs.** Seen on a live client: a Mage going first held two 4-drops and a 5-drop and the overlay
  said "no clear call" three times — the one hand a player does not need help with. Every rule in the
  advisor was relative to another card, so each of those cards looked defensible on its own: cost 4 is
  in nobody's window (the early window ends at 2, or 3 in a deck short of cheap permanents, and the
  top end starts at 5), and the "behind a cheaper play" rule needs a cheaper card the hand did not
  have. The 5-drop was silent for a second reason — the top-end rule abstains without a win-rate, and
  the feed covers 1224 cards. The new rule reads the HAND rather than a card, needs no data (that a
  hand has no early play is a fact, not an estimate), and sits above the score-dependent one so the
  unscored card is judged too. It counts effective turns, so the Coin makes the same hand playable,
  and it leaves the standing exemptions alone: a self-discounting card, a measured bomb, and a card
  with a trade upside, which is a turn-1 play by itself.
- **Card text carries the client's tooltip line breaks, and a regex with a literal space silently
  skipped every card that wrapped mid-phrase.** Half the collectible pool (3594 of 7300 cards with
  text) contains a newline, so `from your deck` missed Skydiving Instructor ("Summon a\n1-Cost
  minion from\nyour deck") and Reinforcement Aura — 2 of the 6 cards the new rule exists for — purely
  because of where Blizzard broke the line. Whether a rule fires must not depend on that.
- **The same bug was in the tribal dependency patterns, where it mattered more**, because those feed
  the dead-card penalty — the one lever allowed past the ±3 clamp. Measured by dumping the whole card
  pool through the engine before and after, **24 cards move**: 22 now take the penalty they were
  dodging (Corrosive Breath, Molten Breath and Lightning Breath, Stormhammer, Twilight Acolyte,
  Goblin Blastmage, Ini Stormcoil, Gentle Megasaur, Serpentbloom and more — dragon and mech payoffs
  that genuinely do nothing without the tribe), and 2 correctly stop taking it because their
  generation clause is finally visible (Lady Prestor, Boom Wrench, which make their own dragons and
  mechs). Both regression tests are pinned on the wrapped cards specifically, and both were checked
  to FAIL without the fix — the first attempt used a card that was already penalized and so proved
  nothing.
- **The redraft deck panel would not let go of a card you had just cut** — and only ever the newly
  drafted ones. Found live during this release's own verification: discarding removes the card from the
  run deck immediately, but the redraft list keeps reporting all five arriving cards for the whole
  phase, and the panel was ranking the UNION of the two. So a new card went straight back in, the
  dedup signature never changed, no re-render fired, and the panel sat there contradicting the deck on
  screen. It now ranks the run deck, with the redraft list as a fallback for a client form that
  exposes no run deck.
- **A spell whose base line still plays is no longer condemned as a dead card.** Seen live: Mirror
  Dimension ("Summon a 0/4 minion with Taunt. If you are holding a Dragon, summon another") took the
  FULL dead-card penalty although it is a perfectly good 1-mana Taunt with no dragons at all. The
  exemption only looked at minion bodies, so every spell whose tribal clause is a *rider* read as
  structurally dead. Now, if any sentence never mentions the missing tribe, that sentence is the base
  line and the card loses a bonus rather than its function. Measured through the engine, **50+ cards**
  soften from −6.07 to −1.52 — the whole "Deal N damage. If you're holding a Dragon…" family, Kill
  Command included — while cards whose entire text IS the condition (Elemental Evocation, Ancient
  Mysteries) keep the full penalty. Two of the three false positives reported below are fixed by this:
  Nofin Can Stop Us and Grave Digging. A clause that merely CONTINUES the previous one ("Draw a Secret.
  It costs (0)") does not count as a base line, or the exemption would have cleared cards that really
  do nothing.
- **The dependency patterns learned the other grammar.** They were written for tribe wordings ("your
  Dragons", "a friendly Beast") and so missed "the next Secret **you play** costs (0)" — Anonymous
  Informant, Kabal Lackey, Kirin Tor Mage and Game Master all slipped through. Measured across all 12
  tribes plus both categories, the added wordings flag 8 distinct cards once the generation veto and
  the own-membership guard have had their say: the entire Draenei cluster is excluded because those
  cards ARE Draenei, and Archimonde is vetoed because it depends on other cards *generating* Demons —
  a dependency this engine cannot see, so no penalty is the honest answer.
- **Indentation is checked now, not merely declared.** `.editorconfig` has said `indent_style = tab`
  for a long time and nothing enforced it, so a mis-indented line compiled silently and reached
  review — which is exactly what happened. `IDE0055` is on, and with warnings-as-errors that makes a
  whitespace slip a broken build like every other style rule here.
- **Five dead-code rules adopted at zero cost**: unused and unread private members, pointless
  assignments, unreachable code, and fields that should be readonly (`IDE0051`, `IDE0052`, `IDE0059`,
  `IDE0035`, `IDE0044`). Measured before adopting: the repo has **no** violations of any of them, so
  they cost nothing today and stop the rot tomorrow, which is the only good moment to add a rule.
  Each now carries a comment saying what it catches, and the whole file is sorted into groups —
  formatting, using directives, enforced analyzers, advisory preferences — because a rule book that
  cannot be scanned is one where a duplicate hides.
- **Using order is enforced, and NOT by the build.** It cannot be: using order is not a Roslyn
  diagnostic, so no `dotnet_diagnostic` line can make it an error — verified by scrambling a file's
  usings, which left the build at 0 warnings while `dotnet format` reported `IMPORTS`. The gate is a
  `dotnet format --verify-no-changes` step in `build.yml`. `System` sorts first, chosen by
  measurement: plain alphabetical (`System` after `HearthDb`) would have put 44 of the repo's files
  in violation, against 5 that were genuinely out of order and are now fixed.
- **A weak cheap card is judged against YOUR deck's slot, not against the pool.** The score below which
  a cheap card stopped being a keep was absolute — and that answered the DRAFT's question inside a class
  built to answer a different one: the draft score already said whether the card is good, and the
  mulligan asks whether it is your best play on that turn given the other 27 cards. Found in a live
  hand: Wild Pyromancer is genuinely below average for a Mage (48.8% drawn against a 51.3% class
  median) and was still the right keep in a deck with nothing better at two mana, because a mediocre
  turn-2 play beats an empty turn 2. The score is now a comparison within the slot rather than a gate,
  and the verdict carries the count ("weak for the slot (3 better at 2)") so the log says why. It takes
  TWO better cards at that cost to demote: one among thirty is a card you will probably not have drawn
  by then. Unchanged at the top end, where there is no slot to compare within and only the card's own
  quality can say whether something you cannot cast for five turns is worth holding.
- **A discount aimed at another card no longer exempts the card holding it.** Alter Time is "Discover
  two Arcane spells from the past. **They** cost (2) less" — the discount lands on what it finds, but
  the check read it as self-discounting and so exempted it from every top-end rule, leaving a 4-mana
  spell sitting behind a 3-drop with no verdict at all. Found in a live mulligan; measured on the pool,
  66 of the 454 cards the old check matched are this shape, and a pronoun subject ("It costs (1) less")
  always points at another card. Cards that really do reduce their own cost stay exempt.
- **The mulligan labels line up with a 3-card hand.** The client fans a smaller hand wider — measured
  against card positions, the real gap is ~0.28 of the width with 3 cards against ~0.195 with 4 — so a
  single spread left the two outer labels noticeably inside their cards when going first. The hand is
  always 3 or 4 cards, so the two measured values are the complete set rather than a special case.
- **Known, and not fixed:** the in-game Discover overlay sometimes reappears for a few seconds during
  a match, showing a choice that is already over. Reported live, and the log has it: the same three
  cards fired twice 72 seconds apart, which a random Discover does not do. It is the same shape as the
  three ghost-overlay bugs already fixed here — a list that outlives the screen that produced it —
  and `CardChoices` carries only `IsVisible` and `Cards`, so HearthMirror cannot say whose choice it
  is or whether it is still live. What it is NOT: the opponent's cards. The client never learns his
  Discover options, so what flashes is your own previous choice. The fix has a candidate — HDT's own
  entities expose `IsInSetAside` and `IsControlledBy`, so a live choice can be told from a stale one —
  but gating on an unverified model of the client would silently kill the whole in-game Discover if
  the model is wrong, so it waits for a live session rather than shipping blind.
- **Known, and not fixed:** Tiny Pal still takes a penalty it does not deserve — "Choose your Elemental
  Ammunition!" is its own mechanic, not a reference to drafted Elementals, and no base line saves it
  because its whole text is that one clause. That is a dependency-pattern problem rather than an
  exemption one, and it needs its own pass over the pool.

### Changed (internals)

- **Card-text normalization now lives in one class (`CardText`) instead of three copies.** The line
  break trap above had already been found and fixed once, in the mulligan advisor, and the lesson
  never reached the other two pipelines — which is exactly what a duplicated helper does. The
  convention (`\s+`, never a literal space) and its two normalized forms now have one home and their
  own tests. The scoring form deliberately still keeps newlines: the heuristic's weights are ridge-fit
  against the features those patterns extract, so collapsing there is a refit, not a cleanup.

### Changed

- **Mulligan: "early" is a property of your deck, not of a mana cost.** In a deck that cannot curve
  out — fewer than six cheap permanents — the early window reaches turn 3, because there is no
  cheaper body to draw into and throwing away the first one chases cards the deck does not hold.
  Widening the window rather than adding a rule keeps every existing guard (duplicate slot, quality
  floor, empty board, Combo, one health) applying to the three-drop exactly as to the two-drop.
- **A "second 2-drop" now means a second one you were told to keep**, not a second card that costs
  2. Sharing a cost is not sharing a slot: a hand holding a 2-mana location that wants a board you
  do not have yet plus a real 2-drop has exactly one early play, and counting the demoted card threw
  away the only one.
- **A card that gets BETTER when you trade it is no longer flatly "too slow".** Wind-Up Enforcer is a
  6-mana 3/5 whose trade upgrades it, so one mana buys a real turn-1 play on a turn that had none.
  Being Tradeable is NOT enough and that distinction is the rule: of 54 Tradeable cards only nine
  carry a trade upside, and a card that merely cycles still goes back, because cycling something you
  did not want is worse than the free replacement a mulligan already gives you. It also applies only
  while turn 1 is otherwise empty — once the hand holds a real 1-drop the two compete for that mana.
  Situational, never Keep: the upgrade is value, not board presence.
- **A one-health minion whose death pays you keeps its verdict.** Loot Hoarder draws when it dies, so
  the hero power that kills it costs the opponent a turn and gains them nothing; Sinful Sous Chef puts
  two cards in your hand. The old rule read the printed statline and told players to toss cards whose
  whole point is that dying is fine. 28 one-health minions are exempted, each verified against the pool.
- **A one-health minion that brings a second body keeps its verdict.** The rule that a 1-health body
  dies free to a hero power reads the printed statline, and for Maze Guide the statline is not the
  play: the hero power that eats the 1/1 leaves the other body standing.

## [0.1.5] - 2026-07-26

### Build

- Warnings are errors for every build, not only in CI. The workflow already passed `-warnaserror`
  while a local `dotnet build` did not, so the two produced the same green tick for different
  checks — and a doc comment that documented one parameter out of three passed here and broke
  there, after the push. The setting now lives in `Directory.Build.props`, where both read it.

### Removed

- **Firestone is no longer a data source**, at the request of its author
  ([#8](https://github.com/dokson/HdtArenaHelper/issues/8)). The runtime feed, the training input
  and the cached files are all gone. This is the project's stated policy meeting its first real
  test — "publicly reachable is not licensed, and if a provider says stop, stop" — and it cost
  something: scores now rest on **one** win-rate source, so nothing averages away a sampling
  artefact and nothing cross-checks a poisoned payload. That is a real limitation and it is written
  down rather than glossed over. The win-rate signal keeps weight 1.0 against the offline model's
  0.5: letting the model rise to half the blend would have been a scoring change smuggled in as a
  dependency removal.
- The heuristic weights were **re-fit from HSReplay alone** and adopted, so the release ships no
  model derived from the withdrawn data. Every card's score moves a little as a result; the fit
  runs on more rows (2438 vs 1868 — the old intersection of two feeds was an implicit filter) with
  a different regularization strength.

### Changed

- **The mulligan screen was rebuilt as our own advice: tempo × quality, judged against your deck.**
  The previous version showed keep win-rates from the withdrawn feed. Rather than drop the screen,
  it now answers a different question with things the plugin owns outright — the 30 cards you
  drafted, the card's own arena score, and the rules of the game. What a card does on turns 1-2
  decides the verdict; the score decides whether that turn is worth buying; the deck decides the
  exceptions (a second 2-drop is a spare when the deck holds five more). Each call names its
  reason: *"plays on turn 2"*, *"needs a board you do not have on turn 1"*, *"one health — a hero
  power removes it for free"*, *"the Coin turns its Combo on"*, *"nothing plays it before turn 6"*.
  There is deliberately **no percentage** — an invented keep-rate is exactly the kind of number
  this project measured to be worse than nothing — and three situations produce silence rather
  than a verdict: a card with no score, a card whose score is low-confidence (a thinly-sampled
  legendary looks average for want of games, not power), and anything whose printed cost is not
  its real cost. Most cards get no call at all, which is the honest answer and keeps the one that
  matters visible.

## [0.1.4] - 2026-07-25

### Changed

- **Rarity no longer influences a card's score.** `rarity_ord` and `is_legendary` are gone from the
  model: rarity is a print-run label, and what actually makes a legendary strong — an above-curve
  statline, unique text — the model already reads directly, so the label was collecting credit that
  belongs to the card. The clearest illustration is Deathwing, which fell from 16.01 to 5.12: a
  10-mana 12/12 that hands over the board belongs at the floor, and it had been getting ~10 display
  points for its rarity alone. Weights were re-fit without the two features and adopted, with the
  golden literals updated. The fit's own numbers all moved the right way too, but they are NOT the
  argument and REPORT.md 14 says why (the regularization strength is re-selected per feature set,
  and the thin-sample statistics carry no standard error).
- **The offline heuristic no longer scores hero cards; it abstains.** Its `is_hero` term has a single
  supporting row and also has to cancel the 30 health those cards report, so it was never an estimate
  — this week's refit flipped it from −0.08 to +0.77 with a standard error of 0.98 and sign
  consistency 0.46, i.e. a coin toss deciding a whole card type, worth ~25 display points. Measured
  cost of abstaining: of the 46 collectible hero cards, exactly 2 appear in either win-rate feed —
  and those two are the ones actually in the pool, since a feed reports what gets drafted. So no card
  a player can be offered loses its number. The intuitive fix — hero cards are strong, give them a
  bonus — stays refused: hand-tuned card values are what this project measured to be worse than
  nothing.

### Fixed

- **The in-game overlay no longer appears outside an arena match.** With an arena run open, a
  **Battlegrounds** hero/trinket pick arrives through the same choice zone, on the same gameplay
  scene, as a Discover — so it was scored with arena win-rates and drawn over the Battlegrounds
  board, which is disruptive enough to make disabling the plugin the reasonable response. The gate
  that was supposed to prevent this asked whether an arena RUN existed; a run stays open across
  modes, so it answered yes all through Battlegrounds. Both in-game watchers (Discover and mulligan)
  now also require the current MATCH to be arena, read from the client's game type. It stays
  permissive only where the client says nothing — an unreadable type, or the "unknown" reported for
  a moment as a game starts, which is exactly the mulligan's window; a stated non-arena type is a
  definite no. When it blocks it logs one line, so a missing overlay is diagnosable rather than
  mysterious.

## [0.1.3] - 2026-07-25

### Added

- **In-game card choices (Discover).** The same scoring engine the draft uses, applied to the cards
  a Discover offers, in your run deck's class context and with the deck as synergy context. Gated on
  the active scene being gameplay AND on being in an arena run: every number here is an arena
  win-rate, so outside arena there is none we are entitled to show.

- **Board awareness on in-game choices.** A Discover now reports what the board makes impossible:
  a card that cannot be cast this turn (with both numbers, since one mana short and five short are
  different decisions), a minion with no room, and — first, because it is the only irreversible
  one — a full hand, where the discovered card is destroyed. Deliberately words next to the score
  and not points inside it: the rules make the facts objective, nothing public says what they are
  worth, and the score already reads well without a guess bolted onto it.
- **Mulligan guidance.** On the mulligan screen, each card's keep win-rate and how often players
  keep it, for your drafted class, from counters already present in the data the plugin downloads
  anyway — so it costs no extra requests. Presented as an estimate and qualified with its sample
  size, because it is single-source, thinly sampled, and not causal: a card is kept in hands that
  already look good, so part of a high keep win-rate is the hand rather than the card. Cards below
  the sample floor show a dash instead of a number.

### Changed

- **The two win-rate feeds are now joined by card identity, not by printing.** They report different
  printings of the same card (HSReplay `CORE_YOP_001`, Firestone `YOP_001`), which left **216 cards
  scored by a single source** — exactly where a second opinion is worth most, and where the offline
  heuristic silently gained influence. Cards covered by both feeds went from 1007 to 1219. Printings
  are pooled as counts (wins and games), never as an average of two rates, which would weight a
  thin printing like a thick one.
- Hero plaques sit lower, below the client's own hero-name banner, where they no longer overlap it.
- Internal structure, for the bug classes it removes rather than for tidiness: the three client
  pollers now share one template (poll throttle, **scene gate**, log-once) — both ghost-overlay bugs
  this project has had came from a watcher acting on another screen's state, and the gate had been
  copy-pasted; the overlay's four mutually exclusive screens became one active-screen field, since
  the "only one at a time" rule was previously maintained in two separate places that had to agree;
  and the leave-one-out shrink prior moved into `ScoreMath`, which is where shared statistical policy
  belongs — the two copies had already drifted, only one of them range-checking its result.

### Build

- The plugin test assembly runs sequentially. The code under test logs through HDT's own logger,
  which enqueues into an unsynchronised queue, so two logging test classes in parallel corrupt it and
  throw from inside HDT with a stack trace that looks like ours. It failed only in CI while the same
  suite was green locally — core count decides whether the race is hit.
- The .NET SDK is pinned (`global.json`) and every workflow aligned to it. The repo treats code-style
  rules as build errors, and analyzer behaviour differs across SDK majors — with local on 10 and CI
  on 8, a green build in the two places was not the same claim.
- NuGet versions moved into `Directory.Packages.props`. Three test projects pinned xunit and the Test
  SDK independently, so a partial bump could leave them mismatched — a skew that presents itself as a
  failing test.

### Security

- The canary workflow now declares least-privilege permissions (`contents: read`). It publishes
  nothing — it only reads another public repo's release tag — but an unset block inherits the repo
  default, which is a write-scoped token in many configurations. Flagged by CodeQL.
- Downloaded payloads are now treated as untrusted input. The parse path never used
  `TypeNameHandling` or `DeserializeObject<T>`, so remote code execution was never reachable — that
  is now written down as an invariant. What was reachable, and is now bounded: a stack overflow from
  deeply nested JSON (the bundled Newtonsoft's depth limit is unlimited, and a stack overflow cannot
  be caught — it would take the whole tracker down, not just the plugin), a gzip bomb via the
  compressed per-class files, an oversized body, and out-of-range values, which are dropped rather
  than clamped so a poisoned row falls back to the other source instead of asserting a number the
  feed never reported.

### Fixed

- The overlay announced "win-rate data unavailable" — and starred every option as low-confidence —
  over scores it had just derived from thousands of games, at the hero pick and on legendary groups.
  Both read "is this real data" off a per-card sample size, which a class tier and a synthesized
  group score legitimately do not carry. Provenance is now explicit and required at every
  construction site, since the same bug appeared three times.
- The overlay could stay blank for a whole draft if HDT started while a draft was already open:
  the pick was consumed for dedup before its cards resolved, and HearthDb is empty at startup, so
  nothing resolved and the pick was never retried. A pick is now consumed only once every offered
  card resolves — which also prevents a partially resolved pick from putting each score on the
  wrong card.

### Docs

- The data-source policy no longer says "free, public data": publicly reachable is not licensed.
  Firestone's developer objected to this project using their CDN without asking, and was right to —
  their repos carry no licence and the site publishes no terms, so there is no permission to infer.
  Every source must stay individually droppable.
- Both feeds are documented as Underground-scoped and short-windowed, which they always were.

## [0.1.2] - 2026-07-25

### Added

- Redraft deck-review panel: during the "Edit Your Deck" / discard phase (no card is
  being picked) the overlay ranks the deck weakest-score first, so the cards to cut
  are obvious. Scored in the deck's own context (a dead payoff sinks), shown as our
  own corner panel with each card's mana cost, sized to how many still need
  discarding, and hidden once the deck is trimmed back to 30.
- Synergy engine, dead-card anti-synergy: a separate, larger, progress-scaled
  penalty that can reorder a close pick, for cards the win-rate sources over-rate
  because they average them over the decks that ran them — a tribal payoff/enabler
  drafted with none of its tribe ("draw your Dragon" with no dragons), or a quest
  that arena tempo rarely rewards. Guarded against false positives: one live tribe
  clears a multi-tribe card, a card with a playable body (or a hero card) only loses
  a fraction, sidequests take a lighter touch than full quests, and self-sufficient
  cards (summon/discover their own, or target the opponent's) are exempt.
- Synergy engine, new fuzzy rules (bounded, tie-breaking): spell-school payoff/member
  on `Card.SpellSchool` (tight cap), `Draenei` added to the tribe list, and
  location-slot crowding.
- Hero pick: each class's estimated arena **win-rate in real percentage points** under the
  plaque, because "71/100" is not a number a player can check and "~53%" is. Derived from the
  per-card tallies both feeds already publish, games-weighted and re-centred so the pool sits
  at 50 — the weighting oversamples winning decks, since a winning arena deck keeps playing.
  The two independent sources agree within ~2pp with identical ordering. Shown as a label
  only, NOT blended into the score: measured, it ranks the classes the same as the existing
  pool-quality tier (Spearman 0.96), so it buys readability, not accuracy.
- Synergy engine, per-class tribe availability: the dead-card penalty now asks how much of the
  drafted class's deck the missing tribe normally holds before condemning a payoff. Demons are
  16.6% of a Warlock's deck slots and 0.6% of a Paladin's — a 28x spread the class-blind
  penalty charged identically. Measured per patch from data already fetched, and deliberately
  one-way: it can only reduce the penalty, never deepen it. This also refuted the hypothesis
  that prompted it — Priest is the third BEST dragon class, not a poor one; the genuinely dead
  cases are Hunter and Demon Hunter.

### Changed

- Redraft deck panel, rebuilt after live testing: it now lists the WHOLE deck in the game's own
  order with an HDT-style score badge per row, the suggested cuts shaded red-to-yellow by how
  clear the cut is, as a full-height column on the left edge. An earlier version
  drew a badge column ON TOP of that list; that is not tunable and was removed — measured live,
  the redraft deck has 23-28 distinct rows against the ~21 the list shows, so it always scrolls,
  and the scroll offset is not readable from the client. It had shipped dormant behind a 22-row
  guard and could never once have fired.
- The model-only shrink constant is now derived the way a shrink factor has to be: a REGRESSION
  SLOPE of held-out truth on prediction (0.31 on the thin decile against 0.92 on a random
  holdout, so 0.34), emitted every refit in `metrics.json`. It was previously a ratio of rank
  CORRELATIONS, which answers a different question. The value barely moved — it was right by
  accident, and the reasoning was not.

- Scoring, model-only cards: when neither win-rate source has a sample for a card the
  blended score is now pulled toward the middle instead of stating a confident number.
  Backtesting showed the offline heuristic is no better than a constant on thinly-sampled
  cards — the very cards where it is the only signal — so it no longer gets to outrank a
  well-measured card on a guess. The ordering among unmeasured cards is unchanged.
- Scoring, thin win-rate data: the offline heuristic no longer *gains* influence as the real
  data thins. Sample-size weighting used to shrink only the win-rate sources, so the
  heuristic's share of a pick grew from the intended third to two thirds on 20-game cards.
  It now keeps the same share at every sample size.
- The heuristic's 0-100 display scale is measured per re-fit (median AND robust spread)
  instead of a fixed slope on the raw score, so the displayed spread no longer drifts with
  whatever scale a re-fit happened to land on.
- Synergy engine: cards that summon or discover their own tribe members, or that target the
  opponent's, are exempt from the dead-card penalty — merely mentioning a tribe is not
  depending on one (Animal Companion brings its own Beast). Also a large speed-up in
  synergy and heuristic scoring, which the deck-review panel needed.

- Synergy curve fit now measures a slot against its target count for a full 30-card
  deck instead of the partial-deck fraction, so it stops flagging a slot as
  "crowded" mid-draft just because cheap cards are drafted first.

### Fixed

- The deck-review panel could stay on screen over the main menu and inside Battlegrounds. With
  a redraft left unfinished the client keeps reporting `EDITING_DECK` on other screens, so the
  session-state gate alone was not enough; the watcher now gates on the active SCENE first, and
  fails permissive if the scene cannot be read. Two claims in the code about this phase were
  also wrong and are corrected: the deck does not always read 30/30 and does not always refuse
  to shrink — the client reports both forms across sessions.
- The overlay never logged `overlay shown` / `overlay hidden`: `Show()` already leaves the window
  visible, so the transition check could not fire — exactly the two lines needed to diagnose a
  "why is it still on screen" report.

### Added (infrastructure)

- `HdtArenaHelper.Numerics`: the ridge solver and the statistics extracted into a library with
  no HDT/HearthDb reference, plus `HdtArenaHelper.Numerics.Tests` — the one suite that runs on a
  machine without HDT installed. Also `HdtArenaHelper.Training.Tests`, covering trainer
  behaviour that was documented as load-bearing but untested: the weight-rounding floor that
  makes "removing a feature needs no runtime change" true, the `metrics.json` format (LF only,
  invariant decimals), and the shrink derivation's clamping and NaN refusal.
- Retrain tooling: the trainer snapshots what it fetched, so a fit can be reproduced or
  re-run entirely offline (`-- --offline`) without hitting a public endpoint again; the
  ridge penalty is now chosen by cross-validation grouped by card instead of a fixed
  constant; per-coefficient standard errors are printed beside each weight; and the model is
  measured on the population it actually serves (cards with little win-rate data) rather
  than only on the well-sampled ones it is fitted on. The weekly retrain PR is gated on a
  `metrics.json` the trainer writes, and CI now runs the golden tests inside the retrain job
  (a PR opened with the default token never triggers them otherwise). Findings and the open
  questions are recorded in `HdtArenaHelper.Training/REPORT.md`.

- In-plugin self-update: on load (throttled to once a day) the plugin checks the
  project's public GitHub releases and, when a newer one exists, downloads the
  bundled DLL and stages it via a rename swap that applies on the next HDT
  restart — no external updater process. Toggleable from the Plugins menu, with a
  manual "Check for updates now" and a one-click fallback to the releases page if
  the automatic swap can't be applied.

## [0.1.1] - 2026-07-24

### Added

- The overlay now explains itself: the dominant synergy reason appears under the
  option label ("fills the 3-drop gap", "Murloc synergy", "too many weapons" —
  marked "(exp.)": the synergy rules are experimental, not validated like the
  win-rate signal),
  and low-confidence scores — no win-rate sample of at least 200 games behind
  them — get a dimmed, starred label so they stop looking as authoritative as
  well-sampled ones.
- Firestone as a second runtime win-rate source (11 public per-class CDN files,
  drawn-win-rate metric, cached per class with independent fail-soft): if either
  endpoint becomes unavailable, the other carries the score alone.
- Class-context scoring: once the hero is picked, cards are rated from the drafted
  class's own bucket (shrunk toward a leave-that-class-out prior, on the same scale
  as the class-agnostic scores), falling back to the global rate where class data is
  thin or missing. Validated cross-source before shipping: the class estimator beats
  the global one at predicting the other source's class rates (Spearman 0.73 vs 0.53).
- Post-loss redraft support (Underground and Normal arena): redraft picks are
  scored like draft picks, with the run deck plus the redraft cards as the
  synergy context.
- Deck-context synergy engine (`MetadataSynergyEngine`): mana-curve gaps, tribal
  payoffs/members (incl. amalgams), weapon crowding and spell-damage pairing, from
  card metadata only. Deliberately bounded (total clamped to ±3 points) so it
  breaks ties but never overrides the win-rate signal.

### Added (infrastructure)

- Weekly retrain workflow (`train.yml`, Fridays): re-fits the heuristic weights
  against live data and opens a review PR only when they moved.
- Weekly HDT-drift canary (`canary.yml`, Wednesdays): builds and tests against the
  LATEST official HDT release, so a breaking change in the runtime-bound surfaces
  (HearthMirror, ArenaPlaque, HearthDb) is caught before users hit it.

### Fixed

- The overlay now only shows during actual draft states: arena choices linger in
  the client's memory on other screens (landing page, mid-run), and the overlay
  would have painted plaques over them. Partial choice lists exposed mid-animation
  are ignored too.
- Robustness fixes from an independent review pass: Firestone publishes data and
  completeness atomically (no window rendering against a stale partial bundle), an
  unusable cached class file is dropped and re-downloaded instead of wedging the
  class until the TTL expires, cache files are swapped atomically (no torn reads),
  a refresh purges every cache file even if one is locked, and the warm-up
  supersession guard is race-free.

### Changed

- Per-card precision weighting in the blend: a source's weight is now scaled by
  the sample behind its estimate for that card (the same n/(n+k) factor the
  shrinkage uses), so a 5000-game estimate no longer averages 50/50 against a
  30-game one. The per-source breakdown exposes the effective weight and sample
  size, surfaced in the overlay as the low-confidence flag.
- Heuristic weights re-fit with the training target switched to the drawn win-rate
  (the same, less deck-confounded metric the runtime win-rate sources use), and the
  display anchor now ships inside `arena_weights.json` (`anchor_median_raw`, measured
  by the trainer) instead of being hardcoded.
- The overlay now renders as soon as at least one data source has loaded, instead
  of waiting for all of them.
- Statistical scoring (shrinkage + median/MAD logistic) extracted into a policy
  shared by all win-rate sources, keeping their scales mutually calibrated.

## [0.1.0] - 2026-07-24

First release.

### Added

- Live Arena draft detection: reads the three offered cards during an Arena /
  Underground Arena draft via HDT's HearthMirror path.
- Multi-source blended **0–100 score** per card, with a per-source breakdown:
  - Real arena win-rates from HSReplay's free public arena endpoint (primary
    signal), cached daily.
  - An offline metadata heuristic whose weights are ridge-fit against real
    win-rates (embedded `arena_weights.json`), used as a fallback for cards the
    win-rate data hasn't covered.
- Native HDT `ArenaPlaque` overlay covering the card draft, the hero pick, and
  the Underground Arena legendary-group pick (cumulative group scoring).
- Class tier list at the hero pick, derived from per-class win-rates, so the
  overlay doubles as a class picker.

### Notes

- Uses only free, public data — no paid subscription, no scraping of paywalled
  services, no bundled third-party data.
- The synergy engine is designed but not yet implemented (a `NullSynergyEngine`
  placeholder is wired in).

[0.1.5]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/dokson/HdtArenaHelper/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/dokson/HdtArenaHelper/releases/tag/v0.1.0
