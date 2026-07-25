using HearthDb;
using HearthDb.Enums;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class HeuristicArenaDataSourceTests
	{
		// The card map builds in EnsureLoadedAsync (HearthDb may be empty at plugin
		// OnLoad); here HearthDb is ready, so one synchronous call loads it.
		private static readonly HeuristicArenaDataSource Source = CreateLoaded();

		private static HeuristicArenaDataSource CreateLoaded()
		{
			var source = new HeuristicArenaDataSource();
			source.EnsureLoadedAsync().GetAwaiter().GetResult();
			return source;
		}

		private static int Dbf(string cardId) => Cards.All[cardId].DbfId;

		/// <summary>
		/// Golden values verified against the C# training tool (HdtArenaHelper.Training),
		/// which fits the embedded weights: the scorer must reproduce them exactly. When
		/// the weights are re-fit (new patch/rotation) these move — recompute them by
		/// re-running the trainer and reading the scores.
		/// </summary>
		[Theory]
		[InlineData("LOOT_413", 19.78)] // Plated Beetle - vanilla-ish minion
		[InlineData("CS2_189", 40.34)]  // Elven Archer - battlecry damage
		[InlineData("EX1_093", 38.20)]  // Defender of Argus - buff + taunt text
		[InlineData("CS2_106", 51.95)]  // Fiery War Axe - weapon path
		[InlineData("CS2_029", 52.93)]  // Fireball - spell with damage magnitude
		[InlineData("EX1_050", 32.49)]  // Coldlight Oracle - draw text
		[InlineData("GIL_828", 54.02)]  // Dire Frenzy - buff spell
		[InlineData("CS2_235", 53.76)]  // Northshire Cleric - persistent draw
		[InlineData("EX1_046", 30.28)]  // Dark Iron Dwarf - conditional buff
		// The LEGENDARY of the set, and a card whose statline is genuinely bad: it fell from
		// 16.01 to 5.12 when `is_legendary` was removed, i.e. the label had been worth ~10
		// display points on its own. That is the number this literal exists to keep honest.
		[InlineData("NEW1_030", 5.12)]  // Deathwing - at the floor, where it belongs
		public void Matches_training_golden_scores(string cardId, double expected)
		{
			var score = Source.GetNormalizedScore(Dbf(cardId));

			Assert.NotNull(score);
			// Tolerance = half the trainer's printed precision (0.00): digit-rounding
			// comparison would disagree on exact midpoints (the trainer prints
			// half-away-from-zero, xUnit rounds half-to-even — e.g. a true 39.725).
			Assert.Equal(expected, score!.Value.Score, 0.005);
		}

		[Fact]
		public void Unknown_dbf_id_returns_null()
		{
			Assert.Null(Source.GetNormalizedScore(-1));
		}

		[Fact]
		public void Class_pick_hero_skins_are_not_scored()
		{
			// The win-rate source rates these by class tier instead.
			Assert.Null(Source.GetNormalizedScore(Dbf("HERO_01")));
			Assert.Null(Source.GetNormalizedScore(Dbf("HERO_08")));
		}

		[Fact]
		public void Draftable_hero_cards_get_no_heuristic_opinion()
		{
			// Hero CARDS (not HERO_* skins) are draftable, and this source used to score them
			// — the golden literal was 48.74. It abstains now because the number was not an
			// estimate: `is_hero` has ONE supporting row and carries the whole card type, so
			// the score re-rolled by ~25 display points between refits on data that had barely
			// moved (dropping the dummy: 76.6 -> 53.6; zeroing the health: -0.08 -> -3.66).
			// The cost of abstaining is near zero — hero cards are collectible, so the win-rate
			// feeds cover them — and where nothing covers them the aggregator's shrink says 50,
			// which is what "we do not know" should look like.
			Assert.Null(Source.GetNormalizedScore(Dbf("ICC_833"))); // Frost Lich Jaina
		}

		/// <summary>
		/// Independent recomputation: parse the embedded json here and recompute
		/// intercept + Σ w·f and the anchor mapping for EVERY draftable card. This pins
		/// the whole plumbing (resource embedding, loader, dot product, display anchor)
		/// with no manual upkeep on re-fits. It cannot replace the golden literals
		/// above: expected values derived from the json would silently bless a wrong
		/// json — the literals are the tripwire that a human reviewed the weights.
		/// </summary>
		[Fact]
		public void Scorer_matches_an_independent_recomputation_from_the_embedded_json()
		{
			var assembly = typeof(HeuristicArenaDataSource).Assembly;
			Newtonsoft.Json.Linq.JObject root;
			using(var stream = assembly.GetManifestResourceStream("HdtArenaHelper.arena_weights.json")!)
			using(var reader = new System.IO.StreamReader(stream))
				root = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());

			var intercept = (double)root["intercept"]!;
			var anchor = (double)root["anchor_median_raw"]!;
			// The display SCALE, not just the centre: the mapping divides by the pool's robust
			// sigma so the 0-100 spread belongs to the card pool rather than to whatever raw
			// scale a re-fit landed on.
			var sigma = (double)root["anchor_sigma_raw"]!;
			var weights = root["weights"]!.ToObject<System.Collections.Generic.Dictionary<string, double>>()!;

			foreach(var kv in Cards.All)
			{
				var card = kv.Value;
				if(!card.Collectible || card.DbfId == 0 || kv.Key.StartsWith("HERO_", System.StringComparison.Ordinal))
					continue;
				if(card.Type != CardType.MINION && card.Type != CardType.SPELL &&
				   card.Type != CardType.WEAPON && card.Type != CardType.LOCATION)
					continue;
				// HERO cards are deliberately unscored by this source — see
				// Draftable_hero_cards_get_no_heuristic_opinion. There is no arithmetic to
				// recompute for a card the scorer refuses to answer on.

				var raw = intercept;
				foreach(var f in HeuristicArenaDataSource.BuildFeatures(card))
					raw += (weights.TryGetValue(f.Key, out var w) ? w : 0.0) * f.Value;
				var expected = System.Math.Max(0, System.Math.Min(100, 50 + 15 * (raw - anchor) / sigma));

				var actual = Source.GetNormalizedScore(card.DbfId);
				Assert.NotNull(actual);
				Assert.Equal(expected, actual!.Value.Score, 6);
			}
		}

		[Fact]
		public void All_collectible_cards_score_within_bounds()
		{
			foreach(var kv in Cards.All)
			{
				var card = kv.Value;
				if(!card.Collectible || card.DbfId == 0)
					continue;
				if(card.Type != CardType.MINION && card.Type != CardType.SPELL &&
				   card.Type != CardType.WEAPON && card.Type != CardType.LOCATION &&
				   card.Type != CardType.HERO)
					continue;

				var score = Source.GetNormalizedScore(card.DbfId);
				if(score == null)
					continue; // HERO_* skins
				Assert.True(score.Value.Score >= 0 && score.Value.Score <= 100,
					$"{kv.Key} scored {score.Value.Score}, outside [0, 100]");
			}
		}
	}
}
