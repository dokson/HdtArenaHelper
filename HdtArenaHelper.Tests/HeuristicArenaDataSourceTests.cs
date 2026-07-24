using HearthDb;
using HearthDb.Enums;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class HeuristicArenaDataSourceTests
	{
		private static readonly HeuristicArenaDataSource Source = new HeuristicArenaDataSource();

		private static int Dbf(string cardId) => Cards.All[cardId].DbfId;

		/// <summary>
		/// Golden values verified against the C# training tool (HdtArenaHelper.Training),
		/// which fits the embedded weights: the scorer must reproduce them exactly. When
		/// the weights are re-fit (new patch/rotation) these move — recompute them by
		/// re-running the trainer and reading the scores.
		/// </summary>
		[Theory]
		[InlineData("LOOT_413", 33.35)] // Plated Beetle - vanilla-ish minion
		[InlineData("CS2_189", 41.30)]  // Elven Archer - battlecry damage
		[InlineData("EX1_093", 32.39)]  // Defender of Argus - buff + taunt text
		[InlineData("CS2_106", 44.15)]  // Fiery War Axe - weapon path
		[InlineData("CS2_029", 46.37)]  // Fireball - spell with damage magnitude
		[InlineData("EX1_050", 34.70)]  // Coldlight Oracle - draw text
		[InlineData("GIL_828", 43.85)]  // Dire Frenzy - buff spell
		[InlineData("CS2_235", 64.85)]  // Northshire Cleric - persistent draw
		[InlineData("EX1_046", 28.82)]  // Dark Iron Dwarf - conditional buff
		[InlineData("NEW1_030", 0.0)]   // Deathwing - clamped to the floor
		public void Matches_training_golden_scores(string cardId, double expected)
		{
			var score = Source.GetNormalizedScore(Dbf(cardId));

			Assert.NotNull(score);
			Assert.Equal(expected, score!.Value, 2);
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
			// Hero CARDS (not HERO_* skins) are draftable, unlike skins. The
			// is_hero bonus (+5.28) is largely offset by their constant 30
			// health (-0.27 each) — the regression fit both together, so the
			// net value is deliberately modest. Golden from the training tool.
			var score = Source.GetNormalizedScore(Dbf("ICC_833")); // Frost Lich Jaina
			Assert.NotNull(score);
			Assert.Equal(41.30, score!.Value, 2);
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
				Assert.True(score >= 0 && score <= 100,
					$"{kv.Key} scored {score}, outside [0, 100]");
			}
		}
	}
}
