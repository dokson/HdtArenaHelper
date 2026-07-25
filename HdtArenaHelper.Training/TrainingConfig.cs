using System;
using HearthDb.Enums;

namespace HdtArenaHelper.Training
{
	/// <summary>
	/// Every knob of the fit policy in one reviewable place: endpoints, inclusion floors, the
	/// feature-selection rules and the cross-validation grid. Deliberately together - these are
	/// the values a reviewer must be able to check without reading the whole pipeline.
	/// </summary>
	internal static class TrainingConfig
	{
		internal const string HsReplayUrl =
			"https://hsreplay.net/api/v1/arena/card_stats/free/?format=json";
		internal const string FirestoneUrlFmt =
			"https://static.zerotoheroes.com/api/arena/stats/cards/arena-underground/last-patch/{0}.gz.json";
		internal const string UserAgent =
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
			"(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

		internal const int MinGames = 100;      // per-source inclusion floor (matches training)
		internal const int MinClassRows = 50;   // drop a class bucket with too few cards
		internal const double LegacyAlpha = 10.0;     // legacy fixed penalty, kept only to log the old regime

		// Minimum rows a feature must appear on to be fitted at all. Derived from power, not
		// taste: with residual SD ~3 and 80% power at 0.05, detecting an effect worth a column
		// (~1.0-1.5 points, given this source carries blend weight 0.5 of 1.5) needs n >= 40-70.
		// Below that the estimate is noise wearing a plausible sign — and the observed drift
		// confirms it, scaling as 1/sqrt(support). 40 also sits in a natural gap in the support
		// distribution (35 -> 51 -> 100), so features don't flip in and out week to week.
		// A dropped feature is simply absent from the json, and ArenaWeights returns 0.0 for a
		// feature it doesn't know: no runtime change, no coordinated release.
		internal const int MinFeatureSupport = 40;

		// The one genuinely redundant column: tx_discover is set by the SAME predicate as
		// kw_discover, so the pair is an exact duplicate and ridge splits the effect evenly
		// between them (both landed on +0.39), which only misleads whoever reads the diff.
		//
		// statline and stat_per_mana were dropped here in an earlier attempt on the argument
		// that statline = attack + health - 2*cost - 1 is already spanned. That argument is
		// WRONG and the experiment proved it: statline is emitted only for MINIONS, so it is an
		// interaction (is_minion x stat quality), and its span needs per-type stat terms that
		// this model does not have. Removing it left the global attack/health slopes to absorb
		// minion stat quality, and they went NEGATIVE — more attack and health scoring worse.
		// Cross-validated rank correlation actually IMPROVED while that happened, because CV
		// only sees cards the win-rate feeds cover; the golden scores caught it (a 2-mana 2/3
		// fell to 14.9/100). Restoring per-type stat interactions is the way to revisit this.
		internal static readonly string[] RedundantFeatures = { "tx_discover" };

		// The support floor applies ONLY to kw_*/tx_* indicators — the things we are trying to
		// ESTIMATE an effect for. It must never drop a structural/type feature: removing such a
		// dummy does not remove a noisy estimate, it removes the baseline offset for that card
		// type (dropping is_hero moved Frost Lich Jaina from 76.6 to 53.6, with health=30 left
		// unoffset).
		//
		// is_hero is the degenerate case and is now handled at INFERENCE instead: it has one
		// supporting row, and the runtime source refuses to score HERO cards at all rather than
		// emit an offset that re-rolls every refit. The dummy stays in the fit — it still absorbs
		// that row instead of letting the health slope do it — but nothing downstream reads its
		// coefficient any more. Do not "clean it up" by dropping it: that reintroduces the very
		// contamination this floor exists to prevent.
		internal static bool IsEstimatedIndicator(string feature)
			=> feature.StartsWith("kw_", StringComparison.Ordinal)
				|| feature.StartsWith("tx_", StringComparison.Ordinal);

		internal static readonly CardType[] Playable =
		{
			CardType.MINION, CardType.SPELL, CardType.WEAPON, CardType.LOCATION, CardType.HERO
		};

		// alpha used to be a hardcoded 10.0, which — because it is added raw to a Gram matrix
		// built from unnormalized sqrt(games) weights — amounted to no regularization at all
		// (see Ridge.PenaltyDiagnostics). With the weights normalized to mean 1, alpha is
		// finally on a meaningful scale and can be chosen by CV rather than guessed.
		internal static readonly double[] AlphaGrid =
			{ 0.03, 0.1, 0.3, 1, 3, 10, 30, 100, 300, 1000, 3000 };

		internal const int CvFolds = 5;
		internal const int CvRepeats = 5;
		internal const int CvSeed = 20260725; // fixed: the fit must stay reproducible

		// Case-bootstrap replicates. Each is a closed-form refit on a few dozen features, so this
		// is seconds, not minutes - no reason to economise into a noisy standard error.
		internal const int BootstrapReplicates = 300;

		// Same-class triples the release gate measures pick flips over. Cheap (an argmax of three
		// doubles) and the variance of the flip rate falls as 1/sqrt(this), so be generous.
		internal const int GateTriples = 20000;

		/// <summary>
		/// Share of same-class picks that must change before a refit is worth a human's attention.
		/// Absolute and interpretable on purpose: the statistically ideal threshold (what resampling
		/// the same data alone produces) is ~31% at this model's stability, which no real refit
		/// would ever exceed — see REPORT.md.
		/// </summary>
		internal const double MaterialFlipRate = 0.02;
	}
}
