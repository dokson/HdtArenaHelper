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
		[InlineData("LOOT_413", 35.70)] // Plated Beetle - vanilla-ish minion
		[InlineData("CS2_189", 39.73)]  // Elven Archer - battlecry damage
		[InlineData("EX1_093", 33.83)]  // Defender of Argus - buff + taunt text
		[InlineData("CS2_106", 49.55)]  // Fiery War Axe - weapon path
		[InlineData("CS2_029", 49.43)]  // Fireball - spell with damage magnitude
		[InlineData("EX1_050", 33.65)]  // Coldlight Oracle - draw text
		[InlineData("GIL_828", 51.05)]  // Dire Frenzy - buff spell
		[InlineData("CS2_235", 57.65)]  // Northshire Cleric - persistent draw
		[InlineData("EX1_046", 24.29)]  // Dark Iron Dwarf - conditional buff
		[InlineData("NEW1_030", 11.52)] // Deathwing - near the floor
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
		public void Draftable_hero_cards_are_scored()
		{
			// Hero CARDS (not HERO_* skins) are draftable, unlike skins. The is_hero
			// bonus is fit together with their constant 30 health, so the net value is
			// what the regression decided, not the raw bonus. With the drawn-win-rate
			// target hero cards rate high: the bomb gets credit when actually drawn.
			// Golden from the training tool.
			var score = Source.GetNormalizedScore(Dbf("ICC_833")); // Frost Lich Jaina
			Assert.NotNull(score);
			Assert.Equal(78.35, score!.Value.Score, 0.005);
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
			var weights = root["weights"]!.ToObject<System.Collections.Generic.Dictionary<string, double>>()!;

			foreach(var kv in Cards.All)
			{
				var card = kv.Value;
				if(!card.Collectible || card.DbfId == 0 || kv.Key.StartsWith("HERO_", System.StringComparison.Ordinal))
					continue;
				if(card.Type != CardType.MINION && card.Type != CardType.SPELL &&
				   card.Type != CardType.WEAPON && card.Type != CardType.LOCATION &&
				   card.Type != CardType.HERO)
					continue;

				var raw = intercept;
				foreach(var f in HeuristicArenaDataSource.BuildFeatures(card))
					raw += (weights.TryGetValue(f.Key, out var w) ? w : 0.0) * f.Value;
				var expected = System.Math.Max(0, System.Math.Min(100, 50 + 15 * (raw - anchor)));

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
