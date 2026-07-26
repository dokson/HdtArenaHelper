using System.Collections.Generic;
using System.Text.RegularExpressions;
using HearthDb;

namespace HdtArenaHelper
{
	/// <summary>How the opponent's hero power can answer a small body, and what it costs them.</summary>
	public enum HeroPowerAnswer
	{
		/// <summary>Nothing it does removes a minion (armour, healing, card draw).</summary>
		None,
		/// <summary>It buffs the hero's attack or equips a weapon: the hero can kill the body, but
		/// eats its attack in return, so a bigger attacker is a real cost to them.</summary>
		HeroAttack,
		/// <summary>It summons a body that can attack the turn it arrives (Charge/Rush), trading a
		/// token — which often dies at end of turn anyway — for your minion.</summary>
		ChargeToken,
		/// <summary>It deals damage to a target of their choosing: the cheapest possible answer,
		/// costing them nothing but the mana.</summary>
		DirectDamage,
	}

	/// <summary>
	/// Reads an opponent's HERO POWER card and says how cheaply it can remove a small minion.
	///
	/// Keyed on the hero power CARD, never on the class, and that is the whole design. In Arena a
	/// dual-class hero does not identify its hero power; hero cards and upgrades REPLACE it mid-game
	/// (which is why HDT tracks PastHeroPowers at all); and the question a mulligan actually asks is
	/// "can THIS button kill my 2/1", which is a property of the printed card. A hand-written class
	/// table would also have to be revisited every patch — deriving it from the card cannot go stale.
	///
	/// Derived from the pool rather than remembered, and the pool corrected two things a from-memory
	/// list got wrong: Paladin's Reinforce does NOT answer a body (a Silver Hand Recruit has no Charge,
	/// so it cannot attack the turn it lands), while Death Knight's Ghoul does (1/1 or 2/1 WITH Charge).
	/// </summary>
	public static class HeroPowerThreat
	{
		/// <summary>
		/// Damage aimed at a target. The "to the enemy hero" wording is EXCLUDED, because a hero power
		/// that can only hit the face answers no minion at all.
		///
		/// Steady Shot is why this is read per-clause AND reconciled in <see cref="Classify"/>: HearthDb
		/// ships its text twice ("Deal $2 damage to the enemy hero." followed by a bare "Deal $2
		/// damage."), and the card is genuinely FACE-ONLY. Letting the bare clause decide put Hunter
		/// among the classes that ping a minion, which is wrong — see the reconciliation rule below.
		/// </summary>
		private static readonly Regex DamageRe = new Regex(
			@"\bdeal\s+\$?(\d+)\s+damage\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex FaceOnlyRe = new Regex(
			@"\bto\s+the\s+enemy\s+hero\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>
		/// Damage they cannot AIM is not an answer to a particular minion — "Deal $1 damage to a random
		/// enemy" may well hit the face. Found by classifying the whole hero-power pool and reading the
		/// result, not by reasoning about the wording in advance.
		/// </summary>
		private static readonly Regex UnaimableRe = new Regex(
			@"\brandom\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>
		/// A clause that GRANTS an effect rather than performing it: "Give your minions 'Deathrattle:
		/// Summon a 2/1 Squashling with Rush'" summons nothing itself, and reading it as a Charge token
		/// credited the hero power with a body it never makes. The other pool-driven correction here.
		/// </summary>
		private static readonly Regex GrantsRe = new Regex(
			@"\bgive\s+your\b|\bdeathrattle\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>A summoned body that can attack immediately — Charge (or Rush) — and its attack.</summary>
		private static readonly Regex ChargeTokenRe = new Regex(
			@"\bsummon\b[^.]*?(\d+)\s*/\s*\d+[^.]*?\b(charge|rush)\b",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>The hero itself becomes the removal: an attack buff, or a weapon to swing with.</summary>
		private static readonly Regex HeroAttackRe = new Regex(
			@"\+\$?a?(\d+)\s+attack\b|\bequip\b[^.]*?(\d+)\s*/\s*\d+",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		/// <summary>
		/// Classifies the hero power, and reports how much damage it can put on ONE minion for free
		/// (0 when the answer costs them their hero's face or nothing is available).
		/// </summary>
		public static (HeroPowerAnswer Answer, int FreeDamage) Classify(Card? heroPower)
		{
			if(heroPower == null)
				return (HeroPowerAnswer.None, 0);

			var text = CardText.Flattened(heroPower);
			if(text.Length == 0)
				return (HeroPowerAnswer.None, 0);

			// Clause by clause, so a face-only sentence cannot vouch for a targeted one. But the two
			// have to be RECONCILED, because HearthDb ships some hero powers' old and new text
			// concatenated: Steady Shot carries both "Deal $2 damage to the enemy hero." and a bare
			// "Deal $2 damage.", and it really is face-only (confirmed by a player — the data alone
			// cannot say which string is current). So a bare clause whose damage EQUALS a face-only
			// clause's is the same effect described twice, not a second targeted ability. Same amount,
			// same effect; a genuinely different amount (face 2 plus "deal 1 damage to a minion") is
			// still read as targeted.
			var faceOnly = new HashSet<int>();
			var aimable = new HashSet<int>();
			// Split on "then" and commas as well as sentences: "Deal $1 damage, then deal $1 damage to
			// the enemy hero" (Spread Shot) puts an aimed hit and a face hit in ONE sentence, and reading
			// the sentence whole credited the face restriction to both halves.
			foreach(var clause in text.Replace(" then ", ". ").Split('.', ','))
			{
				if(UnaimableRe.IsMatch(clause))
					continue;
				var m = DamageRe.Match(clause);
				if(!m.Success || !int.TryParse(m.Groups[1].Value, out var dmg))
					continue;
				if(FaceOnlyRe.IsMatch(clause))
					faceOnly.Add(dmg);
				else
					aimable.Add(dmg);
			}

			// Reconcile ONLY when the text is visibly two renderings of the same card, which HearthDb
			// marks by repeating the "Hero Power" label. Anything else keeps its clauses independent,
			// because a single rendering can legitimately do both: Spread Shot ("Deal $1 damage, then
			// deal $1 damage to the enemy hero") really does aim one of them, and an earlier version of
			// this rule zeroed it out. That error is the dangerous direction — this classifier will be
			// used to RELAX the one-health rule, so under-reading a threat means advising a player to
			// keep a body that dies for free, while over-reading it merely keeps today's behaviour.
			var duplicatedRendering = CountOccurrences(text, "hero power") > 1;

			var best = 0;
			foreach(var dmg in aimable)
			{
				if(duplicatedRendering && faceOnly.Contains(dmg))
					continue;
				if(dmg > best)
					best = dmg;
			}
			if(best > 0)
				return (HeroPowerAnswer.DirectDamage, best);

			foreach(var clause in text.Split('.'))
			{
				if(GrantsRe.IsMatch(clause))
					continue;
				var token = ChargeTokenRe.Match(clause);
				if(token.Success && int.TryParse(token.Groups[1].Value, out var tokenAttack))
					return (HeroPowerAnswer.ChargeToken, tokenAttack);
			}

			var swing = HeroAttackRe.Match(text);
			if(swing.Success)
			{
				var raw = swing.Groups[1].Success ? swing.Groups[1].Value : swing.Groups[2].Value;
				// Not "free": the hero takes the minion's attack back, so the caller decides whether
				// that trade is one the opponent wants. Reported as 0 free damage for that reason.
				return int.TryParse(raw, out _)
					? (HeroPowerAnswer.HeroAttack, 0)
					: (HeroPowerAnswer.None, 0);
			}

			return (HeroPowerAnswer.None, 0);
		}

		private static int CountOccurrences(string text, string needle)
		{
			var count = 0;
			// Case-insensitive: CardText.Flattened strips markup and collapses whitespace but does NOT
			// lower-case (only Normalized does), so the label arrives as "Hero Power".
			var at = text.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase);
			while(at >= 0)
			{
				count++;
				at = text.IndexOf(needle, at + needle.Length, System.StringComparison.OrdinalIgnoreCase);
			}
			return count;
		}

		/// <summary>
		/// Can this hero power kill a body of the given health without the opponent losing anything
		/// they care about? Direct damage that covers the health, or a Charge token big enough to trade.
		/// Deliberately does NOT count the hero-attack case: swinging the hero into a minion costs them
		/// its attack in face damage, which is exactly the difference between a 2/1 and a 3/1.
		/// </summary>
		public static bool KillsForFree(Card? heroPower, int health)
		{
			if(health <= 0)
				return false;
			var (answer, damage) = Classify(heroPower);
			return (answer == HeroPowerAnswer.DirectDamage || answer == HeroPowerAnswer.ChargeToken)
				&& damage >= health;
		}
	}
}
