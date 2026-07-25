using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HearthDb;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;

// The training tool builds its design matrix from the same BuildFeatures below, so
// training and inference can never disagree on what a card's features are.
[assembly: InternalsVisibleTo("HdtArenaHelper.Training")]

namespace HdtArenaHelper
{
	/// <summary>
	/// A fully-offline base "arena value" score computed from card metadata that
	/// HDT already ships (HearthDb). No network, no paywall, no scraping.
	///
	/// The model weights are the SINGLE SOURCE OF TRUTH produced by the offline training
	/// tool (<c>arena_weights.json</c>), embedded into this assembly at build time and
	/// loaded here — no coefficients are duplicated in code, and <see cref="BuildFeatures"/>
	/// is shared with training so the feature vectors can't drift. To re-fit for a new
	/// rotation/patch, re-run the trainer to regenerate that file and rebuild.
	///
	/// The weights were fit by ridge regression against real arena DRAWN win-rates
	/// (HSReplay + Firestone public data; the metric the runtime sources score with,
	/// centered on each class's average so the score measures card value, not class
	/// strength). Out-of-fold Spearman vs real win-rates is ~0.27 — a weak signal in
	/// absolute terms, so this only ranks cards the real win-rate data has not covered;
	/// it must not override a solid win-rate. The raw formula predicts win-rate points
	/// relative to an average card of the same class; weights describe the CURRENT card
	/// pool, not universal keyword values.
	/// </summary>
	public class HeuristicArenaDataSource : IArenaDataSource
	{
		// Embedded training output (see the csproj EmbeddedResource).
		private const string WeightsResource = "HdtArenaHelper.arena_weights.json";
		private static readonly ArenaWeights Model = ArenaWeights.LoadEmbedded(WeightsResource);

		// Published once HearthDb is ready (empty right at HDT startup); volatile so the
		// poll-thread reader sees a fully-built map, never a half-filled one.
		private volatile IReadOnlyDictionary<int, Card>? _byDbfId;

		public HeuristicArenaDataSource(double weight = 1.0)
		{
			Weight = weight;
		}

		public string Name => "Heuristic";
		public double Weight { get; }
		// Not "loaded" until the card map is built: HearthDb may be empty at OnLoad, and a
		// map built then would stay empty all session — so the warm-up loop keeps retrying
		// (otherwise the backstop is silently dead and IsLoaded would lie about it).
		public bool IsLoaded => _byDbfId != null;

		/// <summary>Model-based: it scores every card from metadata, with no sample behind it.</summary>
		public bool HasSamples => false;

		public Task EnsureLoadedAsync()
		{
			EnsureMap();
			return Task.CompletedTask;
		}

		// Build the dbfId->Card map once HearthDb is populated (it's empty right at HDT
		// startup, so a map built eagerly in the ctor would stay empty all session).
		// Idempotent and published atomically; safe to call from any thread / on demand.
		private void EnsureMap()
		{
			if(_byDbfId != null || Cards.All.Count == 0)
				return;

			var map = new Dictionary<int, Card>();
			foreach(var kv in Cards.All)
			{
				var dbf = kv.Value.DbfId;
				if(dbf != 0 && !map.ContainsKey(dbf))
					map[dbf] = kv.Value;
			}
			_byDbfId = map;
		}

		// draftClass is ignored: the model's weights are global by design (per-class fits
		// validated worse — too little data per class; see the training REPORT).
		// Games is null: a model-based score has no per-card sample behind it.
		public SourceScore? GetNormalizedScore(int dbfId, CardClass draftClass = CardClass.INVALID)
		{
			EnsureMap(); // build on first use (tests score without a warm-up; HDT defers at startup)
			var byDbfId = _byDbfId;
			if(byDbfId == null || !byDbfId.TryGetValue(dbfId, out var card))
				return null;

			// Class-pick hero skins are rated by the win-rate source's class tier.
			if(card.Id.StartsWith("HERO_", StringComparison.Ordinal))
				return null;

			double raw;
			try { raw = Model.Score(BuildFeatures(card)); }
			catch { return null; }

			// Display mapping onto the 0-100 blend scale: the median draftable card maps to 50,
			// and one ROBUST SD of the pool's raw spread is worth PointsPerRobustSd points. Both
			// the centre and the scale are measured by the trainer and shipped in the json.
			//
			// The scale is the load-bearing part. A fixed slope on the RAW score (what this used
			// to be) means the displayed spread drifts with every re-fit: heavier regularization
			// shrinks the raw predictions, and the same +/-15 per raw unit then covers a different
			// number of standard deviations. Measured by bootstrap, ~1 point of raw coefficient
			// wobble became ~15 display points, which is also why the golden scores used to swing
			// on refits that had barely changed the model. Dividing by the pool's robust sigma
			// makes the display invariant to the raw scale, so "+15 per robust SD" is finally what
			// happens rather than what the comment claimed.
			//
			// It stays deliberately SHALLOWER than the win-rate sources (~+35 per robust SD): on
			// disagreement the real win-rate must dominate even at equal blend weight, because
			// this source is a backstop. Do not raise it to match them.
			return new SourceScore(Clamp(
				50 + PointsPerRobustSd * (raw - Model.AnchorMedianRaw) / Model.AnchorSigmaRaw));
		}

		/// <summary>
		/// The ridge-model feature vector for a card (feature name → value). Shared by the
		/// plugin (inference) and the training tool (fitting), so the two never diverge.
		/// The learned coefficient for each feature lives only in <c>arena_weights.json</c>.
		/// Units follow the training set: win-rate percentage points vs the average card of
		/// the same class.
		/// </summary>
		internal static IReadOnlyDictionary<string, double> BuildFeatures(Card card)
		{
			var f = new Dictionary<string, double>();

			var cost = card.Cost;
			var attack = card.Attack;
			// HearthstoneJSON (the training source) stores weapon durability in its own
			// field; HearthDb mirrors it in Durability.
			//
			// Hero cards keep their literal 30 health: the training saw the same, so the is_hero
			// weight is fit net of it. Zeroing it and letting is_hero carry the level was TRIED and
			// measured worse - it concentrates the whole effect into a coefficient with one
			// supporting row (is_hero went -0.08 -> -3.66 +/- 1.76). See REPORT.md.
			var health = card.Type == CardType.WEAPON && card.Durability != 0
				? card.Durability
				: card.Health;
			var text = CleanText(card);

			f["cost"] = cost;
			f["attack"] = attack;
			f["health"] = health;

			switch(card.Type)
			{
				case CardType.MINION:
					f["is_minion"] = 1;
					f["statline"] = attack + health - (2 * cost + 1);
					f["stat_per_mana"] = (attack + health) / (double)(cost + 1);
					break;
				case CardType.SPELL:
					f["is_spell"] = 1;
					break;
				case CardType.WEAPON:
					f["is_weapon"] = 1;
					f["weapon_value"] = attack * health - (2 * cost + 1);
					break;
				case CardType.LOCATION:
					f["is_loc"] = 1;
					break;
				case CardType.HERO:
					f["is_hero"] = 1;
					break;
			}

			if(card.Class == CardClass.NEUTRAL)
				f["is_neutral"] = 1;

			// rarity_ord is the ordinal (rare=1, epic=2, legendary=3); legendaries carry an extra
			// is_legendary term. Splitting this into one dummy per rarity was TRIED and measured
			// no better (see REPORT.md), so the simpler committed encoding stands.
			switch(card.Rarity)
			{
				case Rarity.RARE: f["rarity_ord"] = 1; break;
				case Rarity.EPIC: f["rarity_ord"] = 2; break;
				case Rarity.LEGENDARY: f["rarity_ord"] = 3; f["is_legendary"] = 1; break;
			}

			if(card.Race != Race.INVALID)
				f["has_tribe"] = 1;

			AddKeywordFeatures(card, text, f);
			AddTextFeatures(text, cost, f);
			return f;
		}

		// Training counted a keyword when it appeared in the card's mechanics OR was
		// referenced by its text, so text matching is the faithful port (HearthDb's
		// Mechanics array alone is too sparse).
		private static void AddKeywordFeatures(Card card, string text, IDictionary<string, double> f)
		{
			if(card.Taunt || Has(text, @"\btaunt\b")) f["kw_taunt"] = 1;
			if(card.DivineShield || Has(text, "divine shield")) f["kw_divine_shield"] = 1;
			if(Has(text, @"\brush\b")) f["kw_rush"] = 1;
			if(Has(text, @"\bcharge\b")) f["kw_charge"] = 1;
			if(Has(text, @"\blifesteal\b")) f["kw_lifesteal"] = 1;
			if(card.Windfury || Has(text, @"\bwindfury\b")) f["kw_windfury"] = 1;
			if(card.Poisonous || Has(text, @"\bpoisonous\b")) f["kw_poisonous"] = 1;
			if(Has(text, @"\bbattlecry\b")) f["kw_battlecry"] = 1;
			if(card.Deathrattle || Has(text, @"\bdeathrattle\b")) f["kw_deathrattle"] = 1;
			if(Has(text, @"\bdiscover\b")) { f["kw_discover"] = 1; f["tx_discover"] = 1; }
			if(card.Reborn || Has(text, @"\breborn\b")) f["kw_reborn"] = 1;
			if(Has(text, @"\bstealth\b")) f["kw_stealth"] = 1;
			if(Has(text, "spell damage")) f["kw_spellpower"] = 1;
			if(Has(text, @"\bcombo\b")) f["kw_combo"] = 1;
			if(Has(text, @"\bsecret\b")) f["kw_secret"] = 1;
			if(Has(text, @"\bfreeze\b|\bfrozen\b")) f["kw_freeze"] = 1;
			if(Has(text, @"\btradeable\b")) f["kw_tradeable"] = 1;
			if(Has(text, @"\bforge\b")) f["kw_forge"] = 1;
			if(Has(text, @"\bmagnetic\b")) f["kw_magnetic"] = 1;
			if(Has(text, @"\boutcast\b")) f["kw_outcast"] = 1;
			if(Has(text, @"\bcolossal\b")) f["kw_colossal"] = 1;
			if(Has(text, @"\becho\b")) f["kw_echo"] = 1;
		}

		private static void AddTextFeatures(string text, int cost, IDictionary<string, double> f)
		{
			if(Has(text, @"\bdraws?\b")) f["tx_draw"] = 1;
			if(Has(text, @"destroy (a|an|the|all)?\s*(enemy )?minion")) f["tx_destroy_minion"] = 1;
			if(Has(text, @"\ball (enemy )?minions\b|\ball enemies\b|\bto all\b")) f["tx_aoe"] = 1;
			if(Has(text, "summon")) f["tx_summon"] = 1;
			if(Has(text, @"add (a|an|two|three|\d).{0,40}to your hand|copy of")) f["tx_gain_card"] = 1;
			if(Has(text, @"costs? \(?\d+\)? less|reduce.{0,20}cost")) f["tx_mana_cheat"] = 1;
			if(Has(text, "at the (end|start) of|whenever|after you")) f["tx_persistent"] = 1;
			if(Has(text, "random")) f["tx_random"] = 1;
			if(Has(text, "transform")) f["tx_transform"] = 1;
			if(Has(text, "silence")) f["tx_silence"] = 1;
			if(Has(text, @"\d+ armor")) f["tx_armor"] = 1;

			// magnitudes: biggest damage / heal number in the text
			var dmg = MaxNumber(text, @"deals?\s+\$?(\d+)\s+damage");
			if(dmg != 0)
			{
				f["tx_damage_amt"] = dmg;
				f["tx_dmg_per_mana"] = dmg / (double)(cost + 1);
			}
			var heal = MaxNumber(text, @"restore\s+#?(\d+)\s+health");
			if(heal != 0)
				f["tx_restore_amt"] = heal;
		}

		private static readonly Regex Markup = new Regex(@"<[^>]+>|\[x\]", RegexOptions.Compiled);

		// Shared with MetadataSynergyEngine so both read the same normalized card text.
		internal static string CleanText(Card card)
		{
			var text = card.GetLocText(Locale.enUS) ?? "";
			return Markup.Replace(text, " ").ToLowerInvariant();
		}

		// BuildFeatures runs ~33 distinct patterns per card and is called per card per score, but
		// the static Regex.IsMatch cache holds only 15 entries — so every feature extraction used
		// to evict and re-PARSE nearly every pattern. Compile each one once instead; the pattern
		// strings are untouched, so no feature (and no golden score) can move.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Compiled =
			new System.Collections.Concurrent.ConcurrentDictionary<string, Regex>();

		private static Regex Pattern(string pattern)
			=> Compiled.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled));

		private static bool Has(string text, string pattern) => Pattern(pattern).IsMatch(text);

		private static int MaxNumber(string text, string pattern)
		{
			var max = 0;
			foreach(Match m in Pattern(pattern).Matches(text))
			{
				if(int.TryParse(m.Groups[1].Value, out var n) && n > max)
					max = n;
			}
			return max;
		}

		/// <summary>Display points per robust SD of the pool's raw spread. Deliberately well below
		/// the win-rate sources' ~35 so a solid win-rate outvotes this backstop on disagreement.</summary>
		private const double PointsPerRobustSd = 15.0;

		private static double Clamp(double v) => Math.Max(0, Math.Min(100, v));
	}

	/// <summary>
	/// The ridge model (intercept + per-feature coefficients + display anchor) loaded
	/// from the embedded <c>arena_weights.json</c> — every model number lives in the
	/// json the trainer writes; nothing is hardcoded here. Unknown features read as 0,
	/// so a weight dropped by the training pipeline simply contributes nothing.
	/// </summary>
	internal sealed class ArenaWeights
	{
		private readonly IReadOnlyDictionary<string, double> _weights;

		public double Intercept { get; }

		/// <summary>
		/// Median raw score of the draftable pool, measured by the trainer at fit time:
		/// the display mapping subtracts it so the median card lands at 50.
		/// </summary>
		public double AnchorMedianRaw { get; }

		/// <summary>
		/// Robust SD (1.4826 x MAD) of the draftable pool's raw scores, measured by the trainer at
		/// fit time. The display mapping divides by it so the 0-100 spread is a property of the
		/// pool rather than of whatever scale this particular re-fit happened to land on.
		/// </summary>
		public double AnchorSigmaRaw { get; }

		public double this[string feature]
			=> _weights.TryGetValue(feature, out var v) ? v : 0.0;

		private ArenaWeights(double intercept, double anchorMedianRaw, double anchorSigmaRaw,
			IReadOnlyDictionary<string, double> weights)
		{
			Intercept = intercept;
			AnchorMedianRaw = anchorMedianRaw;
			AnchorSigmaRaw = anchorSigmaRaw > 1e-9 ? anchorSigmaRaw : 1.0;
			_weights = weights;
		}

		/// <summary>intercept + Σ weight[feature] · value[feature].</summary>
		public double Score(IReadOnlyDictionary<string, double> features)
		{
			var score = Intercept;
			foreach(var kv in features)
				score += this[kv.Key] * kv.Value;
			return score;
		}

		public static ArenaWeights LoadEmbedded(string resourceName)
		{
			try
			{
				var assembly = typeof(ArenaWeights).Assembly;
				using(var stream = assembly.GetManifestResourceStream(resourceName))
				{
					if(stream == null)
						throw new InvalidOperationException($"Embedded weights '{resourceName}' not found.");
					using(var reader = new StreamReader(stream))
					{
						var root = JObject.Parse(reader.ReadToEnd());
						var intercept = (double?)root["intercept"] ?? 0.0;
						var anchor = (double?)root["anchor_median_raw"] ?? 0.0;
						// Absent in weight files written before the display scale was measured:
						// 1.0 reproduces the old fixed-slope behaviour exactly rather than
						// silently rescaling every score.
						var sigma = (double?)root["anchor_sigma_raw"] ?? 1.0;
						var weights = root["weights"]?.ToObject<Dictionary<string, double>>()
							?? new Dictionary<string, double>();
						return new ArenaWeights(intercept, anchor, sigma, weights);
					}
				}
			}
			catch(Exception ex)
			{
				Hearthstone_Deck_Tracker.Utility.Logging.Log.Error("[ArenaHelper] weights load failed: " + ex);
				return new ArenaWeights(0.0, 0.0, 1.0, new Dictionary<string, double>());
			}
		}
	}
}
