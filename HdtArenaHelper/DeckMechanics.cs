using System.Collections.Generic;
using HearthDb;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// What the drafted deck DOES, in counts: its minion curve, how much removal and AoE it holds,
	/// how much it draws. Descriptive only — no score, no ranking, no advice.
	///
	/// That restraint is the point. REPORT.md measures that an unvalidated number is worse than no
	/// number, and there is no public per-deck dataset to fit "this deck needs more removal" against.
	/// So this states facts the player can verify by looking at their own deck and draws no conclusion
	/// from them. It is the one deck-level feature that needs no validation, because it asserts nothing.
	///
	/// Every count maps 1:1 onto a feature <see cref="HeuristicArenaDataSource.BuildFeatures"/> already
	/// extracts, or onto <see cref="Card.Type"/>. Deliberately NO new text patterns: the features are
	/// shared with the trainer, so reusing them means this summary cannot drift from what the model
	/// sees, and the tooltip-line-break trap (see <see cref="CardText"/>) cannot be re-introduced here.
	/// </summary>
	public sealed class DeckMechanics
	{
		/// <summary>
		/// Minions per cost bucket (0-1, 2, 3, 4, 5, 6, 7+), the same buckets and the same MINIONS-ONLY
		/// rule the synergy engine's curve uses. This is the one the SCORE reasons about: "curve" there
		/// means having a BODY on turn N, and counting spells by cost once made a few cheap spells read
		/// as a full slot, which penalised the two-drops the deck actually needed.
		/// </summary>
		public IReadOnlyList<int> MinionCurve { get; }

		/// <summary>
		/// EVERY card per cost bucket — what the player means by "my curve", and what the client's own
		/// curve widget shows a few centimetres from where this is displayed. Kept separate from
		/// <see cref="MinionCurve"/> rather than replacing it: the two answer different questions, and
		/// showing the minions-only one on screen invited the reader to compare it against the client's
		/// all-cards widget and conclude ours was miscounting (it was not — a 6-cost spell was simply
		/// not a body).
		/// </summary>
		public IReadOnlyList<int> FullCurve { get; }
		public int Minions { get; }
		public int Spells { get; }
		public int Weapons { get; }
		public int Locations { get; }
		/// <summary>Cards that destroy a minion outright (<c>tx_destroy_minion</c>).</summary>
		public int HardRemoval { get; }
		/// <summary>
		/// Cards that deal damage (<c>tx_damage_amt</c>), of ANY type — because damaging a minion to
		/// kill it IS removal, and most of a minion-heavy deck's answers are Battlecries rather than
		/// spells. An earlier version counted only SPELLS and reported "removal 0" for a real Paladin
		/// deck whose answers were all on bodies, which is true to the letter and useless to the player.
		/// Counted apart from <see cref="HardRemoval"/> because "destroy" and "deal 3" are not the same
		/// answer to a big minion.
		/// </summary>
		public int DamageCards { get; }
		/// <summary>Cards hitting everything on a side (<c>tx_aoe</c>).</summary>
		public int Aoe { get; }
		/// <summary>Cards that draw (<c>tx_draw</c>).</summary>
		public int Draw { get; }

		/// <summary>
		/// Mean cost of EVERY card in the deck, 0 when empty. All cards and not minions only, because a
		/// control deck legitimately carries its weight in expensive SPELLS — judging the profile on
		/// bodies alone would read a removal-and-AoE deck as midrange. This was also the sharpest
		/// objection an adversarial review raised against a minions-only statistic.
		/// </summary>
		public double AverageCost { get; }

		/// <summary>
		/// "aggro", "midrange" or "control" from <see cref="AverageCost"/> — empty when the deck
		/// has no minions to describe. Deliberately called a CURVE PROFILE and not an archetype: an
		/// archetype implies a deliberate game plan, and an arena deck is assembled from what it was
		/// offered rather than to a plan, so the honest claim is about the shape of the curve.
		/// </summary>
		public string Profile { get; }

		/// <summary>
		/// The cost bucket furthest BELOW the curve model's target for it, or -1 when nothing is short.
		/// Kept beside the profile because the profile is a mean, and a mean hides structure: a deck can
		/// sit dead-centre in midrange while having no three- or four-drops at all, which is the fact
		/// that changes a pick. Measured against the same targets the SCORE uses, never a second set.
		/// </summary>
		public int ThinnestSlot { get; }

		public DeckMechanics(IReadOnlyList<int> minionCurve, int minions, int spells, int weapons,
			int locations, int hardRemoval, int damageCards, int aoe, int draw,
			double averageCost = 0, string profile = "", int thinnestSlot = -1,
			IReadOnlyList<int>? fullCurve = null)
		{
			MinionCurve = minionCurve;
			FullCurve = fullCurve ?? minionCurve;
			Minions = minions;
			Spells = spells;
			Weapons = weapons;
			Locations = locations;
			HardRemoval = hardRemoval;
			DamageCards = damageCards;
			Aoe = aoe;
			Draw = draw;
			AverageCost = averageCost;
			Profile = profile;
			ThinnestSlot = thinnestSlot;
		}

		/// <summary>
		/// Curve-profile boundaries on the mean cost of the WHOLE deck. Chosen, not measured — and the
		/// distinction
		/// matters enough to state: what IS invariant is Hearthstone's mana schedule (one mana on turn
		/// one, two on turn two), which makes the curve a meaningful thing to measure at all and makes
		/// these thresholds ABSOLUTE rather than relative to a class. A class's typical curve is a
		/// product of the current card pool — Warrior has fielded dominant low-curve aggro decks — so
		/// anchoring to a class baseline would encode this patch's meta as if it were the game's shape.
		///
		/// But the invariant fixes the RULER, not the marks on it: where aggro ends and midrange begins
		/// depends on power level per mana, which does move. So these are a cheap heuristic, deliberately
		/// not fitted to outcomes, and the band is narrow because a random draft trends to the middle —
		/// a wide midrange band would label almost everything midrange and say nothing.
		///
		/// Measured on the live pool for calibration, the whole-deck mean spans 2.72 (Rogue) to 3.87
		/// (Warlock) across classes, against 3.01 to 4.05 for minions alone — Warrior drops from 4.05 to
		/// 3.45 once its cheap spells and weapons count, which is exactly why the profile reads the whole
		/// deck. Those are class AVERAGES, so individual decks spread wider; see REPORT.md.
		/// </summary>
		private const double AggroCeiling = 3.2;
		private const double ControlFloor = 3.6;

		/// <summary>
		/// One line for the log (and, later, the overlay). Kept here rather than at the call site so
		/// the wording is testable and identical wherever it is shown.
		/// </summary>
		public string ToLine()
		{
			var curve = new System.Text.StringBuilder();
			var bodies = new System.Text.StringBuilder();
			for(var i = 0; i < FullCurve.Count; i++)
			{
				if(curve.Length > 0)
				{
					curve.Append(' ');
					bodies.Append(' ');
				}
				var label = MetadataSynergyEngine.BucketLabel(i);
				curve.Append(label).Append(':').Append(FullCurve[i]);
				bodies.Append(label).Append(':').Append(MinionCurve[i]);
			}
			var thin = ThinnestSlot < 0
				? ""
				: $" | thinnest body slot {MetadataSynergyEngine.BucketLabel(ThinnestSlot)}";
			// Both curves in the log: the all-cards one is what the player compares against the client,
			// the minions-only one is what the score reasons about, and a diagnosis needs to tell them
			// apart — that ambiguity is what sent a live report of a "miscounted" 6-drop.
			return $"curve [{curve}] | bodies [{bodies}] | {Minions} minions, {Spells} spells, "
				+ $"{Weapons} weapons, {Locations} locations | removal {HardRemoval} hard + "
				+ $"{DamageCards} damage | AoE {Aoe} | draw {Draw} | {Profile} "
				+ $"(avg cost {AverageCost:0.00}){thin}";
		}

		private const int Buckets = 7;

		/// <summary>
		/// Describes a deck given as dbf ids (the form every watcher already carries). Unresolvable
		/// ids are skipped rather than failing the summary: a missing card understates a count, while
		/// throwing would take down a panel over one id HearthDb does not know.
		/// </summary>
		public static DeckMechanics Describe(IReadOnlyCollection<int>? deckDbfIds)
		{
			var curve = new int[Buckets];
			var fullCurve = new int[Buckets];
			int minions = 0, spells = 0, weapons = 0, locations = 0;
			int hardRemoval = 0, damageCards = 0, aoe = 0, draw = 0;
			int cards = 0, costTotal = 0;

			if(deckDbfIds != null)
			{
				foreach(var dbfId in deckDbfIds)
				{
					var card = Cards.GetFromDbfId(dbfId);
					if(card == null)
						continue;

					cards++;
					costTotal += card.Cost;
					fullCurve[MetadataSynergyEngine.CostBucket(card.Cost)]++;

					switch(card.Type)
					{
						case CardType.MINION:
							minions++;
							curve[MetadataSynergyEngine.CostBucket(card.Cost)]++;
							break;
						case CardType.SPELL:
							spells++;
							break;
						case CardType.WEAPON:
							weapons++;
							break;
						case CardType.LOCATION:
							locations++;
							break;
					}

					var f = HeuristicArenaDataSource.BuildFeatures(card);
					if(Feature(f, "tx_destroy_minion") > 0)
						hardRemoval++;
					if(Feature(f, "tx_damage_amt") > 0)
						damageCards++;
					if(Feature(f, "tx_aoe") > 0)
						aoe++;
					if(Feature(f, "tx_draw") > 0)
						draw++;
				}
			}

			var avgCost = cards > 0 ? costTotal / (double)cards : 0;
			var profile = cards == 0 ? string.Empty
				: avgCost < AggroCeiling ? "aggro"
				: avgCost > ControlFloor ? "control"
				: "midrange";

			// The thinnest slot stays on the MINION curve: it is measured against the score's own
			// minions-only targets, and comparing an all-cards count to a minions target would report a
			// hole the model does not believe in.
			return new DeckMechanics(curve, minions, spells, weapons, locations,
				hardRemoval, damageCards, aoe, draw, avgCost, profile,
				ThinnestAgainstTarget(curve), fullCurve);
		}

		/// <summary>
		/// The bucket furthest below the curve model's own target, or -1 when none is short. Reuses
		/// <see cref="MetadataSynergyEngine.CurveTarget"/> rather than declaring a second set of
		/// targets: two references for one question would eventually disagree, and then the score and
		/// the description would contradict each other on screen.
		/// </summary>
		private static int ThinnestAgainstTarget(IReadOnlyList<int> curve)
		{
			var worst = -1;
			var worstGap = 0.0;
			for(var i = 0; i < curve.Count && i < MetadataSynergyEngine.CurveTarget.Length; i++)
			{
				var gap = MetadataSynergyEngine.CurveTarget[i] * MetadataSynergyEngine.MinionsPerDeck
					- curve[i];
				if(gap > worstGap)
				{
					worstGap = gap;
					worst = i;
				}
			}
			return worst;
		}

		// A feature absent from the dictionary reads as 0, exactly as the scoring model treats it.
		private static double Feature(IReadOnlyDictionary<string, double> features, string key)
			=> features.TryGetValue(key, out var v) ? v : 0;
	}
}
