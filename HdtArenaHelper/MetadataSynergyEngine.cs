using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HearthDb;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// Deck-context synergy from objective card metadata (HearthDb): mana curve, tribes,
	/// weapon slots, spell damage. No tier lists, no card-specific rules.
	///
	/// UNVALIDATED BY DESIGN: unlike the heuristic's ridge weights there is no free public
	/// per-deck dataset to fit these rules against, and this project's own validation
	/// showed hand-tuned card values score WORSE than nothing. The guardrail is the bound:
	/// every component is capped and the total is clamped to ±<see cref="MaxBonus"/>
	/// points — well under one blend standard deviation (~15) — so synergy breaks ties
	/// between comparable cards but can never override a solid win-rate signal.
	///
	/// Components:
	///   - Curve gap: reward filling an empty cost slot, penalize piling onto a full one,
	///     scaled by draft progress (the curve barely matters at pick 3, a lot at pick 25).
	///   - Tribal: a payoff card (text references a tribe) is worth more the more members
	///     of that tribe are drafted, and a member is worth more with payoffs drafted.
	///   - Weapon crowding: a third-plus weapon mostly wastes a slot.
	///   - Spell damage: an enabler with damage spells drafted, and vice versa.
	/// </summary>
	public sealed class MetadataSynergyEngine : ISynergyEngine
	{
		/// <summary>Hard clamp on the total bonus, in blend points.</summary>
		public const double MaxBonus = 3.0;

		// Target arena mana-curve fractions per cost bucket (0-1, 2, 3, 4, 5, 6, 7+):
		// the classic tempo-arena shape — front-loaded, thinning out past five.
		private static readonly double[] CurveTarget = { 0.08, 0.22, 0.20, 0.17, 0.12, 0.10, 0.11 };
		private const double CurveScale = 8.0;   // points per unit of (target - actual) fraction
		private const double CurveCap = 2.0;
		private const int DeckSize = 30;

		private const double TribePayoffPerMember = 0.4;  // payoff offered, members drafted
		private const int TribePayoffMemberCap = 5;
		private const double TribeMemberPerPayoff = 0.3;  // member offered, payoffs drafted
		private const int TribeMemberPayoffCap = 3;

		private const double WeaponCrowdPenalty = 0.75;   // per weapon beyond the first
		private const double WeaponCrowdCap = 1.5;

		private const double SpellDamageEnablerBonus = 0.5; // needs >= 2 damage spells drafted
		private const double DamageSpellWithSdBonus = 0.3;  // needs >= 1 enabler drafted

		private static readonly Regex DamageSpellRe =
			new Regex(@"deals?\s+\$?\d+\s+damage", RegexOptions.Compiled);

		// dbf -> card, built lazily on the first call: the engine is constructed during
		// OnLoad when HearthDb may still be empty, but by the first draft pick it is ready.
		private volatile Dictionary<int, Card>? _byDbfId;
		private readonly object _initLock = new object();

		// A reason is only worth surfacing when its component moved the needle.
		private const double MinReasonPoints = 0.5;

		public SynergyResult GetSynergy(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds)
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

			var parts = new[]
			{
				CurveBonus(offered, drafted),
				TribalBonus(offered, drafted),
				WeaponBonus(offered, drafted),
				SpellDamageBonus(offered, drafted),
			};

			double bonus = 0;
			string? topReason = null;
			var topAbs = MinReasonPoints;
			foreach(var (points, label) in parts)
			{
				bonus += points;
				if(label != null && Math.Abs(points) >= topAbs)
				{
					topAbs = Math.Abs(points);
					topReason = label;
				}
			}
			return new SynergyResult(Math.Max(-MaxBonus, Math.Min(MaxBonus, bonus)), topReason);
		}

		// ---- curve ---------------------------------------------------------------

		private static (double Points, string? Label) CurveBonus(Card offered, IReadOnlyList<Card> drafted)
		{
			if(drafted.Count == 0)
				return (0, null);

			var bucket = CostBucket(offered.Cost);
			var inBucket = 0;
			foreach(var card in drafted)
			{
				if(CostBucket(card.Cost) == bucket)
					inBucket++;
			}

			var gap = CurveTarget[bucket] - inBucket / (double)drafted.Count;
			var progress = Math.Min(1.0, drafted.Count / (double)DeckSize);
			var bonus = Math.Max(-CurveCap, Math.Min(CurveCap, CurveScale * gap * progress));
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

		// One entry per tribe: the word its payoff cards use in text, and the race tags
		// that make a card a member (BEAST/PET both mean beast; ALL matches everything).
		private static readonly (string Word, Race[] Races)[] Tribes =
		{
			("murloc", new[] { Race.MURLOC }),
			("beast", new[] { Race.BEAST, Race.PET }),
			("dragon", new[] { Race.DRAGON }),
			("pirate", new[] { Race.PIRATE }),
			("mech", new[] { Race.MECHANICAL }),
			("demon", new[] { Race.DEMON }),
			("elemental", new[] { Race.ELEMENTAL }),
			("totem", new[] { Race.TOTEM }),
			("undead", new[] { Race.UNDEAD }),
			("naga", new[] { Race.NAGA }),
			("quilboar", new[] { Race.QUILBOAR }),
		};

		private static (double Points, string? Label) TribalBonus(Card offered, IReadOnlyList<Card> drafted)
		{
			double bonus = 0;
			string? bestTribe = null;
			double bestTribePoints = 0;
			var offeredText = HeuristicArenaDataSource.CleanText(offered);

			foreach(var (word, races) in Tribes)
			{
				double tribePoints = 0;

				// Payoff offered ("give your Murlocs...") -> count drafted members.
				if(MentionsTribe(offeredText, word))
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
					foreach(var card in drafted)
					{
						if(MentionsTribe(HeuristicArenaDataSource.CleanText(card), word))
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

			var label = bestTribe == null
				? null
				: char.ToUpperInvariant(bestTribe[0]) + bestTribe.Substring(1) + " synergy";
			return (bonus, label);
		}

		private static bool IsOfTribe(Card card, Race[] races)
		{
			if(card.Race == Race.ALL)
				return true;
			foreach(var race in races)
			{
				if(card.Race == race || card.SecondaryRace == race)
					return true;
			}
			return false;
		}

		private static bool MentionsTribe(string text, string word)
			=> Regex.IsMatch(text, $@"\b{word}s?\b");

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

		// ---- spell damage ------------------------------------------------------------

		private static (double Points, string? Label) SpellDamageBonus(Card offered, IReadOnlyList<Card> drafted)
		{
			var offeredText = HeuristicArenaDataSource.CleanText(offered);
			double bonus = 0;

			if(offeredText.Contains("spell damage"))
			{
				var damageSpells = 0;
				foreach(var card in drafted)
				{
					if(IsDamageSpell(card))
						damageSpells++;
				}
				if(damageSpells >= 2)
					bonus += SpellDamageEnablerBonus;
			}

			if(IsDamageSpell(offered))
			{
				foreach(var card in drafted)
				{
					if(HeuristicArenaDataSource.CleanText(card).Contains("spell damage"))
					{
						bonus += DamageSpellWithSdBonus;
						break;
					}
				}
			}
			return (bonus, bonus > 0 ? "spell-damage synergy" : null);
		}

		private static bool IsDamageSpell(Card card)
			=> card.Type == CardType.SPELL
				&& DamageSpellRe.IsMatch(HeuristicArenaDataSource.CleanText(card));

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
