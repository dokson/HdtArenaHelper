using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HearthDb;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// Deck-context synergy from objective card metadata (HearthDb): mana curve, tribes, spell
	/// schools, weapon and location slots, spell damage. No tier lists, no card-specific rules.
	///
	/// UNVALIDATED BY DESIGN: unlike the heuristic's ridge weights there is no free public
	/// per-deck dataset to fit these rules against, and this project's own validation
	/// showed hand-tuned card values score WORSE than nothing. The guardrail is the bound:
	/// the FUZZY components below are each capped and their total clamped to
	/// ±<see cref="MaxBonus"/> points — well under one blend standard deviation (~15) — so
	/// they break ties between comparable cards but never override a solid win-rate signal.
	///
	/// The one exception is the DEAD-CARD penalty: a tribal payoff drafted with zero members
	/// of its tribe (e.g. "Draw your Dragon" with no dragons) is a near-objective structural
	/// dead card, not a fuzzy tie-break, so it gets a separate, larger (progress-scaled)
	/// negative lever that CAN reorder a pick. The win-rate sources can't see this — they
	/// average a payoff over the tribal decks that drafted it — so it's ours to catch.
	/// Because that lever is the only one allowed past the clamp it fails CLOSED: merely
	/// MENTIONING a tribe is not depending on one, so a card that summons/discovers its own
	/// members, or that targets the opponent's, is excluded (see SelfSufficientRe).
	///
	/// Components:
	///   - Curve gap: reward filling a cost slot with room left (vs its full-deck target
	///     count), penalize piling onto a full one, scaled by draft progress (the curve
	///     barely matters at pick 3, a lot at pick 25).
	///   - Tribal: a payoff card (text references a tribe) is worth more the more members
	///     of that tribe are drafted, and a member is worth more with payoffs drafted (12
	///     tribes incl. Draenei).
	///   - Spell school: the same payoff/member idea on Card.SpellSchool, capped tighter
	///     (arena rarely reaches a school's critical mass).
	///   - Weapon crowding: a third-plus weapon mostly wastes a slot.
	///   - Location crowding: a third-plus location, the gentlest penalty.
	///   - Spell damage: an enabler with damage spells drafted, and vice versa.
	///   - Dead card (separate, larger): a quest/questline, or a tribal payoff with none of
	///     its tribe drafted.
	/// </summary>
	public sealed class MetadataSynergyEngine : ISynergyEngine
	{
		/// <summary>Hard clamp on the total bonus, in blend points.</summary>
		public const double MaxBonus = 3.0;

		// Target mana-curve fractions per cost bucket (0-1, 2, 3, 4, 5, 6, 7+), as a share of the
		// deck's MINIONS. Two corrections against drafting consensus for the current meta: 5 mana is
		// where arena's best-value minions live, so the old 0.12 (~3.6 of them) started penalizing a
		// fifth five-drop — the opposite of what a tempo deck wants; and the old 7+ target of 0.11
		// alongside 0.10 at six meant ~6.3 cards costing 6+, which loses the early game. Mass moved
		// from 7+ into 5 and 4.
		private static readonly double[] CurveTarget = { 0.08, 0.22, 0.20, 0.18, 0.15, 0.10, 0.07 };
		private const double CurveScale = 8.0;   // points per unit of (target - actual) fraction
		private const double CurveCap = 2.0;
		private const int DeckSize = 30;
		/// <summary>Minions in a typical arena deck; the curve targets are shares of THIS, not of 30,
		/// because the rule counts only minions on both sides.</summary>
		private const double MinionsPerDeck = 19.0;

		private const double TribePayoffPerMember = 0.4;  // payoff offered, members drafted
		private const int TribePayoffMemberCap = 5;
		private const double TribeMemberPerPayoff = 0.3;  // member offered, payoffs drafted
		private const int TribeMemberPayoffCap = 3;

		private const double WeaponCrowdPenalty = 0.75;   // per weapon beyond the first
		private const double WeaponCrowdCap = 1.5;

		private const double SpellDamageEnablerBonus = 0.5; // needs >= 2 damage spells drafted
		private const double DamageSpellWithSdBonus = 0.3;  // needs >= 1 enabler drafted

		// Spell-school payoff/member, mirroring the tribal rule on Card.SpellSchool. Capped
		// TIGHTER than tribes: arena decks are minion-dense, so one spell school rarely reaches
		// a critical mass, and school references are often one-shot on-cast, not deckbuild.
		private const double SpellSchoolPerMember = 0.2;
		private const int SpellSchoolMemberCap = 4;
		private const double SpellSchoolPerPayoff = 0.2;
		private const int SpellSchoolPayoffCap = 3;
		// Raised from 1.0: the "arena is minion-dense so a school never reaches critical mass" claim
		// is false where this pool concentrates — Shadow 57, Holy 38 and Nature 36 spells, with real
		// class-concentrated payoffs (Paladin Holy, Shaman Nature, DK Frost). Still the tightest cap
		// in the engine, because a school payoff is usually one-shot on cast rather than deckbuilding.
		private const double SpellSchoolCap = 1.5;

		// Location-slot crowding, a gentler mirror of the weapon rule: only one Location occupies
		// the zone at a time, so a third-plus has diminishing returns — but Locations are strong
		// individually, so this is the softest penalty in the engine.
		private const double LocationCrowdPenalty = 0.35;
		private const double LocationCrowdCap = 0.5;

		// Quest / questline: a card and a deck slot spent toward a payoff that rarely resolves in
		// arena's tempo game. Near-dead regardless of the rest of the deck, so it uses the same
		// separate (non-fuzzy) lever as the dead-card penalty, bounded like it.
		private const double QuestPenalty = 6.0;
		// Sidequests are cheap, fast builds that a normal arena curve often completes
		// anyway — worth a nudge away, not the full quest condemnation.
		private const double QuestSidequestFactor = 0.5;

		// Dead-card penalty: a tribal payoff/enabler drafted with NONE of its tribe. Separate
		// from and larger than the ±MaxBonus fuzzy bonus (a dead card isn't a tie-break),
		// but scaled by draft progress and bounded by this cap — deliberately well under a
		// blend SD (~15): "can reorder a close pick", not "vetoes the win-rate".
		private const double DeadPayoffMax = 7.0;
		// A conditional card with a real BODY is not dead — the text may go blank but the
		// stats still play (Blackwing Corruptor without dragons is a 5-mana 5/4, not a
		// mulligan). Only that fraction of the penalty applies to playable-body minions.
		private const double DeadBodyFactor = 0.25;
		// AVAILABILITY DAMPING. A tribal payoff with zero members is only dead if members are not
		// coming, and that depends on the CLASS: measured on the live pool, Demons are 16.6% of a
		// Warlock's deck slots and 0.6% of a Paladin's — a 28x spread the class-blind penalty was
		// charging identically. So the penalty is scaled by the members the remaining picks are
		// expected to bring, share x picksLeft, relative to the couple of members a payoff needs
		// to switch on. Direction is deliberately one-way: this can only REDUCE the penalty, never
		// raise it, because it is the single lever allowed past the fuzzy clamp and no measured
		// input justifies making it more aggressive. No data (class unknown, feed not loaded,
		// tribe unseen) leaves the penalty exactly as it was.
		private const double DeadEnoughMembers = 2.0;
		private const double DeadAvailabilityFloor = 0.25;
		// Below this vanilla-curve deficit a minion's body doesn't count as playable
		// (attack+health vs 2*cost+1), so it takes the full dead penalty like a spell.
		private const int DeadBodyStatlineFloor = -4;

		private static readonly Regex DamageSpellRe =
			new Regex(@"deals?\s+\$?\d+\s+damage", RegexOptions.Compiled);

		// Tribal dependency is a WHITELIST, not a blacklist of self-sufficiency. The blacklist form
		// had to be extended forever and every gap cost a full DeadPayoffMax on a card that brings
		// its own members: enumerating the live pool found it missed "FILL your board with random
		// Dragons" (Endtime Murozond, one of the strongest cards in the format), "GET a random
		// Demon", and every card where the tribe is the OUTPUT ("convert a Divine Shield into a 5/5
		// Elemental"). It also scored anti-tribe tech UP for the tribe it exists to punish. So only
		// treat a card as tribe-dependent when its text references members you ALREADY have.
		// A bounded gap, not adjacency: real card wording puts words between the possessive and the
		// tribe ("Draw YOUR lowest and highest Cost DRAGON", "Draw a Beast, a Dragon, and a Murloc
		// FROM YOUR DECK"), and requiring "your dragon" verbatim missed genuine dead cards. The gap
		// is capped and stops at a sentence boundary so it cannot reach across an unrelated clause.
		private static readonly string[] DependencyPatterns =
		{
			@"your (other )?[^.]{0,30}{0}",
			@"if you control [^.]{0,25}{0}",
			@"(a|an|another) friendly [^.]{0,15}{0}",
			@"friendly {0}s?\b",
			@"holding [^.]{0,20}{0}",
			@"for each {0}s? you",
			@"{0}s?[^.]{0,25}(in|from) your (hand|deck)",
			@"{0}s? you control",
			@"control (a|an|another) {0}",
			@"{0}s? that (died|have died)",
			// "Draw a Beast, Dragon, and Murloc" (The Curator) says nothing about your deck, but
			// drawing is dependent BY DEFINITION — you can only draw what you drafted. Contrast
			// "Discover"/"Summon a random", which create from the whole card pool and are not.
			@"draw [^.]{0,30}{0}",
		};

		// Even a dependency whitelist needs one narrow veto, because a possessive can appear inside
		// a GENERATION clause: "Fill YOUR board with random DRAGONS" (Endtime Murozond) reads as
		// "your ... dragons" but creates them from the whole card pool. Pure pattern matching cannot
		// tell the two apart, so an explicit generation verb near the tribe wins over the whitelist.
		private static readonly string[] GenerationPatterns =
		{
			@"\b(fill|summon|discover|recruit|get|generate) [^.]{0,40}{0}",
			@"\brandom [^.]{0,20}{0}",
		};

		/// <summary>
		/// One alternation of the given templates with <c>{0}</c> bound to a tribe word, compiled
		/// once at startup. Both the dependency and the generation sets are built this way.
		/// </summary>
		private static Regex BuildTribeRegex(string[] templates, string word)
			=> new Regex(string.Join("|",
					Array.ConvertAll(templates, t => "(?:" + t.Replace("{0}", word) + ")")),
				RegexOptions.Compiled);

		/// <summary>
		/// Card text with the class name "demon hunter" stripped. Without this, every Demon Hunter
		/// card that names its own class matched <c>\bdemons?\b</c> and read as a Demon tribal
		/// payoff — inflating the tribal bonus and, worse, exposing those cards to the dead-card
		/// penalty for not having drafted Demons.
		/// </summary>
		private static string StripClassNames(string text) => DemonHunterRe.Replace(text, " ");

		private static readonly Regex DemonHunterRe =
			new Regex(@"\bdemon\s+hunter\b", RegexOptions.Compiled);

		// One entry per spell school: the payoff pattern its cards use ("cast a Fire spell...")
		// and the SpellSchool tag that makes a spell a member. Patterns are precompiled: with 12
		// tribes + 7 schools the static Regex.IsMatch cache (15 entries) would thrash and re-parse
		// every pattern on nearly every call, and these loops run per drafted card.
		private static readonly (string Word, Regex Re, Regex GeneratesRe, SpellSchool School)[] SpellSchools =
		{
			SchoolEntry("fire", SpellSchool.FIRE),
			SchoolEntry("frost", SpellSchool.FROST),
			SchoolEntry("arcane", SpellSchool.ARCANE),
			SchoolEntry("nature", SpellSchool.NATURE),
			SchoolEntry("holy", SpellSchool.HOLY),
			SchoolEntry("shadow", SpellSchool.SHADOW),
			SchoolEntry("fel", SpellSchool.FEL),
		};

		// Match "<school> spell(s)" rather than the bare school word (which appears in flavor and as
		// damage types), so only genuine spell-school payoffs count — and veto the generators. Most
		// school-mentioning cards in the live pool make their own ("Discover a Holy spell", "Get a
		// random Frost spell", "add a random Fire spell"); those depend on nothing you drafted, and
		// were being scored UP for it, exactly as the tribal rule was before its whitelist.
		private static (string, Regex, Regex, SpellSchool) SchoolEntry(string word, SpellSchool school)
			=> (word, new Regex($@"\b{word}\s+spell", RegexOptions.Compiled),
				BuildTribeRegex(GenerationPatterns, word + @"\s+spell"), school);

		// dbf -> card, built lazily on the first call: the engine is constructed during
		// OnLoad when HearthDb may still be empty, but by the first draft pick it is ready.
		private volatile Dictionary<int, Card>? _byDbfId;
		private readonly object _initLock = new object();

		// A reason is only worth surfacing when its component moved the needle.
		private const double MinReasonPoints = 0.5;

		/// <summary>
		/// The availability feed used to damp the dead-card penalty, or null to keep the
		/// class-blind behaviour. Set once at wiring time, read on the poll thread.
		/// </summary>
		public void SetTribeAvailability(IClassTribeAvailabilitySource? availability)
			=> _availability = availability;

		private volatile IClassTribeAvailabilitySource? _availability;

		public SynergyResult GetSynergy(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds,
			CardClass draftClass = CardClass.INVALID)
		{
			var byDbf = ResolveMap();
			if(byDbf == null || !byDbf.TryGetValue(offeredDbfId, out var offered))
				return default;

			var drafted = new List<Card>(draftedDbfIds.Count);
			foreach(var dbf in draftedDbfIds)
			{
				if(byDbf.TryGetValue(dbf, out var card))
					drafted.Add(card);
			}

			// Clean each card's text ONCE. CleanText is a regex replace plus an allocation, and
			// the tribe/school rules below would otherwise re-run it per drafted card per tribe
			// (12 + 7 times each) — which the deck-review panel multiplies by the whole deck.
			var offeredText = StripClassNames(HeuristicArenaDataSource.CleanText(offered));
			var draftedText = new string[drafted.Count];
			for(var i = 0; i < drafted.Count; i++)
				draftedText[i] = StripClassNames(HeuristicArenaDataSource.CleanText(drafted[i]));

			// Fuzzy synergy: weak, unvalidated rules, so their total is clamped to ±MaxBonus
			// and they only break ties between comparable cards.
			var parts = new[]
			{
				CurveBonus(offered, drafted),
				TribalBonus(offered, offeredText, drafted, draftedText),
				SpellSchoolBonus(offered, offeredText, drafted, draftedText),
				WeaponBonus(offered, drafted),
				LocationBonus(offered, drafted),
				SpellDamageBonus(offered, offeredText, drafted, draftedText),
			};

			double fuzzy = 0;
			string? reason = null;
			var topAbs = MinReasonPoints;
			foreach(var (points, label) in parts)
			{
				fuzzy += points;
				if(label != null && Math.Abs(points) >= topAbs)
				{
					topAbs = Math.Abs(points);
					reason = label;
				}
			}
			fuzzy = Math.Max(-MaxBonus, Math.Min(MaxBonus, fuzzy));

			// Dead-card penalty: a near-objective structural fact, so it gets a bigger, separate
			// lever that can actually reorder a pick — and, being the biggest deal when it fires,
			// it owns the reason line.
			var (deadPoints, deadLabel) = ConditionalPenalty(offered, offeredText, drafted, draftClass);
			if(deadPoints <= -MinReasonPoints)
				reason = deadLabel;

			return new SynergyResult(fuzzy + deadPoints, reason);
		}

		// ---- curve ---------------------------------------------------------------

		private static (double Points, string? Label) CurveBonus(Card offered, IReadOnlyList<Card> drafted)
		{
			// "Curve" in arena means A BODY ON TURN N, so only minions count — on either side.
			// Counting every card by cost was the engine's worst systematic error: a third of the
			// pool is spells, so a deck holding a few cheap spells read as a FULL 2-slot and the
			// engine then penalized a genuine 2-drop minion. Exactly backwards from what a tempo
			// deck wants. Spell/removal composition is a separate question this rule does not model.
			if(offered.Type != CardType.MINION)
				return (0, null);

			var minions = 0;
			var bucket = CostBucket(offered.Cost);
			var inBucket = 0;
			foreach(var card in drafted)
			{
				if(card.Type != CardType.MINION)
					continue;
				minions++;
				if(CostBucket(card.Cost) == bucket)
					inBucket++;
			}
			if(minions == 0)
				return (0, null);

			// Compare against the slot's target COUNT for a full deck's MINION complement, not the
			// fraction of what has been drafted so far: mid-draft you naturally take cheap cards
			// first, so a fraction-vs-fraction check calls a slot crowded long before it is. Scaled
			// by draft progress, since the curve barely matters at pick 3 and a lot at pick 25.
			var room = CurveTarget[bucket] * MinionsPerDeck - inBucket;
			var progress = Math.Min(1.0, drafted.Count / (double)DeckSize);
			var bonus = Math.Max(-CurveCap,
				Math.Min(CurveCap, CurveScale * (room / MinionsPerDeck) * progress));
			var label = bonus > 0
				? $"fills the {BucketLabel(bucket)} gap"
				: $"crowds the {BucketLabel(bucket)} slot";
			return (bonus, label);
		}

		private static int CostBucket(int cost)
			=> cost <= 1 ? 0 : cost >= 7 ? 6 : cost - 1;

		private static string BucketLabel(int bucket)
			=> bucket == 6 ? "7+ drop" : $"{bucket + 1}-drop";

		// ---- tribes ----------------------------------------------------------------

		// One entry per tribe: the (precompiled) pattern its payoff cards use in text, and the
		// race tags that make a card a member (BEAST/PET both mean beast; ALL matches everything).
		private static readonly (string Word, Regex Re, Regex DependsRe, Regex GeneratesRe, Race[] Races)[] Tribes =
		{
			TribeEntry("murloc", Race.MURLOC),
			TribeEntry("beast", Race.BEAST, Race.PET),
			TribeEntry("dragon", Race.DRAGON),
			TribeEntry("pirate", Race.PIRATE),
			TribeEntry("mech", Race.MECHANICAL),
			TribeEntry("demon", Race.DEMON),
			TribeEntry("elemental", Race.ELEMENTAL),
			TribeEntry("totem", Race.TOTEM),
			TribeEntry("undead", Race.UNDEAD),
			TribeEntry("naga", Race.NAGA),
			TribeEntry("quilboar", Race.QUILBOAR),
			TribeEntry("draenei", Race.DRAENEI),
		};

		private static (string, Regex, Regex, Regex, Race[]) TribeEntry(string word, params Race[] races)
			=> (word, new Regex($@"\b{word}s?\b", RegexOptions.Compiled),
				BuildTribeRegex(DependencyPatterns, word),
				BuildTribeRegex(GenerationPatterns, word), races);

		private static (double Points, string? Label) TribalBonus(Card offered, string offeredText,
			IReadOnlyList<Card> drafted, string[] draftedText)
		{
			double bonus = 0;
			string? bestTribe = null;
			double bestTribePoints = 0;

			foreach(var (word, re, dependsRe, generatesRe, races) in Tribes)
			{
				double tribePoints = 0;

				// Payoff offered ("give your Murlocs...") -> count drafted members. Guarded the
				// same way as the penalty: a card that punishes the ENEMY's murlocs must not be
				// scored up for the murlocs YOU drafted.
				if(IsDeckDependent(offeredText, dependsRe, generatesRe))
				{
					var members = 0;
					foreach(var card in drafted)
					{
						if(IsOfTribe(card, races))
							members++;
					}
					if(members > 0)
						tribePoints += TribePayoffPerMember * Math.Min(members, TribePayoffMemberCap);
				}

				// Member offered -> count drafted payoffs referencing its tribe.
				if(IsOfTribe(offered, races))
				{
					var payoffs = 0;
					for(var i = 0; i < draftedText.Length; i++)
					{
						if(re.IsMatch(draftedText[i]))
							payoffs++;
					}
					if(payoffs > 0)
						tribePoints += TribeMemberPerPayoff * Math.Min(payoffs, TribeMemberPayoffCap);
				}

				bonus += tribePoints;
				if(tribePoints > bestTribePoints)
				{
					bestTribePoints = tribePoints;
					bestTribe = word;
				}
			}

			var label = bestTribe == null ? null : Capitalize(bestTribe) + " synergy";
			return (bonus, label);
		}

		// ---- dead-card conditionality ----------------------------------------------

		// A tribal payoff/enabler (references a tribe but is not a member of it) is a dead card
		// when the deck holds NONE of that tribe. Separate from and larger than the fuzzy tribal
		// bonus: it returns a strong negative that can push past the ±MaxBonus clamp, because a
		// card that literally cannot function is not a tie-break.
		private (double Points, string? Label) ConditionalPenalty(Card offered, string offeredText,
			IReadOnlyList<Card> drafted, CardClass draftClass)
		{
			// Quest / questline: a slow build-around that rarely pays off in arena's tempo
			// game. Gated on the actual GameTags — a card whose TEXT merely says "if you
			// control a Quest..." (Questing Explorer is a 2-mana 2/3) is a normal minion,
			// not a quest — and progress-scaled like the tribal lever: early you could
			// still draft around it, late it's a wasted slot. Sidequests are cheap, fast
			// builds a normal curve often completes anyway: half weight.
			var isQuest = offered.Entity.GetTag(GameTag.QUEST) != 0
				|| offered.Entity.GetTag(GameTag.QUESTLINE) != 0;
			var isSidequest = !isQuest && offered.Entity.GetTag(GameTag.SIDEQUEST) != 0;
			if(isQuest || isSidequest)
			{
				var questProgress = Math.Min(1.0, drafted.Count / (double)DeckSize);
				var weight = isSidequest ? QuestSidequestFactor : 1.0;
				return (-QuestPenalty * weight * questProgress, "quest — too slow for arena");
			}

			if(drafted.Count == 0)
				return (0, null);

			// A card that supplies its own members, or that targets the OPPONENT's tribe, is not
			// waiting on the draft at all: Animal Companion ("Summon a random Beast Companion")
			// is a premium card with zero beasts drafted, and "Destroy a Pirate" wants the ENEMY
			// to have pirates. Both merely MENTION a tribe, which is not the same as depending on
			// one. The per-tribe dependency check in the loop below now decides this, so there is
			// no blanket text guard here.

			// Dead only if EVERY tribe the card references (without being one itself — a
			// member that is merely the first of its tribe is not a dead card) has zero
			// drafted members: a menagerie card with beasts drafted is live even with no
			// dragons, so one live tribe clears the whole card.
			string? missing = null;
			Race[]? missingRaces = null;
			foreach(var (word, re, dependsRe, generatesRe, races) in Tribes)
			{
				if(!IsDeckDependent(offeredText, dependsRe, generatesRe) || IsOfTribe(offered, races))
					continue;

				// Count only GENUINE members here. Race.ALL amalgams count for every tribe, which
				// is intended for the bonus but silently disarmed this penalty for the whole draft:
				// one drafted amalgam made "members > 0" true for all 12 tribes, so no tribal
				// payoff could ever read as dead again.
				var members = 0;
				foreach(var card in drafted)
				{
					if(IsOfTribe(card, races, countAmalgams: false))
						members++;
				}
				if(members > 0)
					return (0, null); // at least one referenced tribe is genuinely live
				if(missing == null)
				{
					missing = word;
					missingRaces = races;
				}
			}
			if(missing == null)
				return (0, null); // references no tribe it isn't part of

			// Grows with draft progress: at pick 3 zero members is fine (you can still
			// pivot into the tribe); by pick 25 the payoff is a dead card. Cards with a
			// standalone function only lose the conditional part, not the whole card.
			var progress = Math.Min(1.0, drafted.Count / (double)DeckSize);
			var cap = HasStandaloneFunction(offered) ? DeadPayoffMax * DeadBodyFactor : DeadPayoffMax;
			var damping = AvailabilityDamping(draftClass, missingRaces, drafted.Count);
			return (-cap * progress * damping, $"no {Capitalize(missing)}s for this card");
		}

		/// <summary>
		/// How much of the dead-card penalty survives, given how much of THIS class's deck the
		/// missing tribe normally holds. The quantity is the members the rest of the draft is
		/// expected to bring — share x picks left — against the couple a payoff needs to switch on:
		/// a Warlock offered a Demon payoff at pick 5 will see Demons, a Paladin will not.
		///
		/// Returns 1.0 (no change) whenever the answer is unknown, and can never exceed 1.0. A
		/// dual-race tribe uses its most available race, since either one turns the payoff on.
		/// </summary>
		private double AvailabilityDamping(CardClass draftClass, Race[]? races, int draftedCount)
		{
			var availability = _availability;
			if(availability == null || races == null || draftClass == CardClass.INVALID)
				return 1.0;

			double? best = null;
			foreach(var race in races)
			{
				var share = availability.TribeShare(draftClass, race);
				if(share != null && (best == null || share > best))
					best = share;
			}
			if(best == null)
				return 1.0;

			var picksLeft = Math.Max(0, DeckSize - draftedCount);
			var expectedMembers = best.Value / 100.0 * picksLeft;
			return Math.Max(DeadAvailabilityFloor,
				Math.Min(1.0, 1.0 - expectedMembers / DeadEnoughMembers));
		}

		/// <summary>
		/// Does this card's tribal text actually depend on what YOU drafted? A card that supplies
		/// its own members (Animal Companion summons its Beast) or that targets the opponent's
		/// (Golakka Crawler destroys a Pirate — it wants the ENEMY to have them) merely MENTIONS a
		/// tribe. Both the dead-card penalty and the tribal BONUS must respect this: without it,
		/// anti-tribe tech cards were scored UP for drafting the very tribe they punish.
		/// </summary>
		private static bool IsDeckDependent(string text, Regex dependsRe, Regex generatesRe)
			=> dependsRe.IsMatch(text) && !generatesRe.IsMatch(text);

		// Spells, weapons and locations ARE their text; hero cards always replace the hero
		// power and add armor; a minion stands alone when its body isn't far below the
		// vanilla curve. These keep playing even when their conditional text goes blank.
		private static bool HasStandaloneFunction(Card card)
			=> card.Type == CardType.HERO
				|| (card.Type == CardType.MINION
					&& card.Attack + card.Health - (2 * card.Cost + 1) > DeadBodyStatlineFloor);

		private static string Capitalize(string word)
			=> word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1);

		private static bool IsOfTribe(Card card, Race[] races, bool countAmalgams = true)
		{
			if(countAmalgams && card.Race == Race.ALL)
				return true;
			foreach(var race in races)
			{
				if(card.Race == race || card.SecondaryRace == race)
					return true;
			}
			return false;
		}

		// ---- weapons ---------------------------------------------------------------

		private static (double Points, string? Label) WeaponBonus(Card offered, IReadOnlyList<Card> drafted)
		{
			if(offered.Type != CardType.WEAPON)
				return (0, null);

			var weapons = 0;
			foreach(var card in drafted)
			{
				if(card.Type == CardType.WEAPON)
					weapons++;
			}
			if(weapons <= 1)
				return (0, null); // a second weapon is fine; the third+ crowds the slot
			return (-Math.Min(WeaponCrowdCap, WeaponCrowdPenalty * (weapons - 1)), "too many weapons");
		}

		// ---- locations -------------------------------------------------------------

		private static (double Points, string? Label) LocationBonus(Card offered, IReadOnlyList<Card> drafted)
		{
			if(offered.Type != CardType.LOCATION)
				return (0, null);

			var locations = 0;
			foreach(var card in drafted)
			{
				if(card.Type == CardType.LOCATION)
					locations++;
			}
			if(locations <= 1)
				return (0, null); // a second location is fine; only one occupies the slot at a time
			return (-Math.Min(LocationCrowdCap, LocationCrowdPenalty * (locations - 1)), "too many locations");
		}

		// ---- spell school ----------------------------------------------------------

		// Mirror of the tribal rule on Card.SpellSchool: a school payoff ("cast a Fire spell...")
		// rises with drafted spells of that school, and a school spell rises with drafted payoffs.
		// Positive-only and clamped tight (arena rarely reaches school critical mass).
		private static (double Points, string? Label) SpellSchoolBonus(Card offered, string offeredText,
			IReadOnlyList<Card> drafted, string[] draftedText)
		{
			double bonus = 0;
			string? best = null;
			double bestPoints = 0;

			foreach(var (word, re, generatesRe, school) in SpellSchools)
			{
				double points = 0;

				// Payoff offered -> count drafted spells of that school, unless the card supplies its
				// own (a Discover/Get/random school spell depends on nothing you drafted).
				if(re.IsMatch(offeredText) && !generatesRe.IsMatch(offeredText))
				{
					var members = 0;
					foreach(var card in drafted)
					{
						if(card.Type == CardType.SPELL && card.SpellSchool == (int)school)
							members++;
					}
					if(members > 0)
						points += SpellSchoolPerMember * Math.Min(members, SpellSchoolMemberCap);
				}

				// School spell offered -> count drafted payoffs referencing that school.
				if(offered.Type == CardType.SPELL && offered.SpellSchool == (int)school)
				{
					var payoffs = 0;
					for(var i = 0; i < draftedText.Length; i++)
					{
						if(re.IsMatch(draftedText[i]))
							payoffs++;
					}
					if(payoffs > 0)
						points += SpellSchoolPerPayoff * Math.Min(payoffs, SpellSchoolPayoffCap);
				}

				bonus += points;
				if(points > bestPoints)
				{
					bestPoints = points;
					best = word;
				}
			}

			bonus = Math.Min(SpellSchoolCap, bonus);
			var label = best == null ? null : Capitalize(best) + " spell synergy";
			return (bonus, label);
		}

		// ---- spell damage ------------------------------------------------------------

		private static (double Points, string? Label) SpellDamageBonus(Card offered, string offeredText,
			IReadOnlyList<Card> drafted, string[] draftedText)
		{
			double bonus = 0;

			if(offeredText.Contains("spell damage"))
			{
				var damageSpells = 0;
				for(var i = 0; i < draftedText.Length; i++)
				{
					if(IsDamageSpell(drafted[i], draftedText[i]))
						damageSpells++;
				}
				if(damageSpells >= 2)
					bonus += SpellDamageEnablerBonus;
			}

			if(IsDamageSpell(offered, offeredText))
			{
				for(var i = 0; i < draftedText.Length; i++)
				{
					if(draftedText[i].Contains("spell damage"))
					{
						bonus += DamageSpellWithSdBonus;
						break;
					}
				}
			}
			return (bonus, bonus > 0 ? "spell-damage synergy" : null);
		}

		private static bool IsDamageSpell(Card card, string text)
			=> card.Type == CardType.SPELL && DamageSpellRe.IsMatch(text);

		// ---- init ----------------------------------------------------------------

		private Dictionary<int, Card>? ResolveMap()
		{
			var map = _byDbfId;
			if(map != null)
				return map;
			if(Cards.All.Count == 0)
				return null; // HearthDb not ready yet (right after HDT start)

			lock(_initLock)
			{
				if(_byDbfId != null)
					return _byDbfId;
				var built = new Dictionary<int, Card>(Cards.All.Count);
				foreach(var kv in Cards.All)
				{
					var dbf = kv.Value.DbfId;
					if(dbf != 0 && !built.ContainsKey(dbf))
						built[dbf] = kv.Value;
				}
				_byDbfId = built;
				return built;
			}
		}
	}
}
