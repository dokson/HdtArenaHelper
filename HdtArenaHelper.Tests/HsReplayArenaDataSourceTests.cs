using System;
using System.IO;
using System.Threading.Tasks;
using HearthDb;
using HearthDb.Enums;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// Exercises the HSReplay source fully offline: a synthetic payload is written to
	/// the cache file (the 1-day TTL makes it "fresh"), so EnsureLoadedAsync parses it
	/// without touching the network. Assertions target the reviewed scoring pipeline:
	/// drawn win-rate -> empirical-Bayes shrinkage -> median/MAD-anchored logistic.
	/// </summary>
	public class HsReplayArenaDataSourceTests : IDisposable
	{
		private readonly string _cacheDir = Path.Combine(
			Path.GetTempPath(), "HdtArenaHelperTests", Guid.NewGuid().ToString("N"));

		public void Dispose()
		{
			try { Directory.Delete(_cacheDir, recursive: true); } catch { /* temp dir */ }
		}

		private static int Dbf(string cardId) => Cards.All[cardId].DbfId;

		private async Task<HsReplayArenaDataSource> LoadAsync(string json)
		{
			var source = new HsReplayArenaDataSource(_cacheDir);
			File.WriteAllText(Path.Combine(_cacheDir, "hsreplay_arena.json"), json);
			await source.EnsureLoadedAsync();
			return source;
		}

		// Real Basic-set neutral ids so they resolve in HearthDb.
		// Rates symmetric around 50 with high, equal sample sizes (shrinkage negligible),
		// so the middle card is the median -> anchored to 50.
		private const string SymmetricPayload = @"{
			""data"": { ""ALL"": [
				{ ""card_id"": ""CS2_120"", ""drawn_win_rate"": 40.0, ""num_games"": 2000 },
				{ ""card_id"": ""CS2_168"", ""drawn_win_rate"": 45.0, ""num_games"": 2000 },
				{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 50.0, ""num_games"": 2000 },
				{ ""card_id"": ""CS2_189"", ""drawn_win_rate"": 55.0, ""num_games"": 2000 },
				{ ""card_id"": ""CS2_200"", ""drawn_win_rate"": 60.0, ""num_games"": 2000 }
			] } }";

		[Fact]
		public async Task Median_card_scores_about_50_and_the_scale_is_bounded()
		{
			var source = await LoadAsync(SymmetricPayload);

			var low = source.GetNormalizedScore(Dbf("CS2_120"))!.Value.Score;   // 40
			var mid = source.GetNormalizedScore(Dbf("CS2_182"))!.Value.Score;   // 50 (median)
			var high = source.GetNormalizedScore(Dbf("CS2_200"))!.Value.Score;  // 60

			Assert.True(source.IsLoaded);
			Assert.Equal(50.0, mid, 0);                 // median card -> 50, not min-max's 67
			Assert.True(high > 50 && low < 50);          // monotone around the anchor
			Assert.True(high < 100 && low > 0);          // no outlier pinned to 0/100
		}

		[Fact]
		public async Task Low_sample_extreme_is_shrunk_toward_the_mean()
		{
			// Two cards with the SAME high drawn win-rate but very different samples;
			// the low-sample one must be pulled toward the centre and score lower.
			const string payload = @"{
				""data"": { ""ALL"": [
					{ ""card_id"": ""CS2_120"", ""drawn_win_rate"": 48.0, ""num_games"": 3000 },
					{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 50.0, ""num_games"": 3000 },
					{ ""card_id"": ""CS2_200"", ""drawn_win_rate"": 52.0, ""num_games"": 3000 },
					{ ""card_id"": ""CS2_189"", ""drawn_win_rate"": 70.0, ""num_games"": 4000 },
					{ ""card_id"": ""CS2_168"", ""drawn_win_rate"": 70.0, ""num_games"": 15 }
				] } }";
			var source = await LoadAsync(payload);

			var highSample = source.GetNormalizedScore(Dbf("CS2_189"))!.Value.Score; // 70 @ 4000
			var lowSample = source.GetNormalizedScore(Dbf("CS2_168"))!.Value.Score;  // 70 @ 15

			Assert.True(lowSample < highSample);
		}

		[Fact]
		public async Task Uses_drawn_win_rate_not_included_win_rate()
		{
			// If drawn_win_rate drives the score, X (drawn 60) beats Y (drawn 40) even
			// though their included win_rate is the opposite.
			const string payload = @"{
				""data"": { ""ALL"": [
					{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 60.0, ""win_rate"": 40.0, ""num_games"": 3000 },
					{ ""card_id"": ""CS2_200"", ""drawn_win_rate"": 40.0, ""win_rate"": 60.0, ""num_games"": 3000 }
				] } }";
			var source = await LoadAsync(payload);

			Assert.True(source.GetNormalizedScore(Dbf("CS2_182"))!.Value.Score > source.GetNormalizedScore(Dbf("CS2_200"))!.Value.Score);
		}

		[Fact]
		public async Task Falls_back_to_included_win_rate_when_drawn_is_absent()
		{
			const string payload = @"{
				""data"": { ""ALL"": [
					{ ""card_id"": ""CS2_182"", ""win_rate"": 55.0, ""num_games"": 3000 }
				] } }";
			var source = await LoadAsync(payload);

			Assert.Equal(55.0, source.GetRaw(Dbf("CS2_182"))!.DrawnWinrate);
		}

		[Fact]
		public async Task Cards_below_the_min_games_floor_are_dropped()
		{
			const string payload = @"{
				""data"": { ""ALL"": [
					{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 50.0, ""num_games"": 15 },
					{ ""card_id"": ""CS2_200"", ""drawn_win_rate"": 55.0, ""num_games"": 5 }
				] } }";
			var source = await LoadAsync(payload);

			Assert.NotNull(source.GetNormalizedScore(Dbf("CS2_182"))); // 15 >= floor
			Assert.Null(source.GetNormalizedScore(Dbf("CS2_200")));    // 5  < floor
		}

		[Fact]
		public async Task Repeated_entries_for_one_card_are_summed_as_counts()
		{
			const string payload = @"{
				""data"": { ""ALL"": [
					{ ""card_id"": ""CS2_106"", ""drawn_win_rate"": 55.0, ""num_games"": 5000 },
					{ ""card_id"": ""CS2_106"", ""drawn_win_rate"": 52.0, ""num_games"": 9000 }
				] } }";
			var source = await LoadAsync(payload);

			// Games-weighted, NOT "keep the bigger entry" (which threw away 5000 real games) and NOT
			// the mean of the two rates (which would weight 5000 games like 9000):
			// (55*5000 + 52*9000) / 14000 = 53.071...
			var raw = source.GetRaw(Dbf("CS2_106"))!;
			Assert.Equal((55.0 * 5000 + 52.0 * 9000) / 14000, raw.DrawnWinrate!.Value, 6);
			Assert.Equal(14000, raw.Games);
		}

		[Fact]
		public async Task Two_printings_of_the_same_card_pool_their_samples()
		{
			// The reason the rule above matters: the same card is reported under different PRINTINGS
			// (CORE_YOP_001 and YOP_001 are one card), so keying on the raw dbf id split 216 cards
			// into separate thin entries. Both printings must land on one identity, and either
			// printing's dbf id must find it.
			const string payload = @"{
				""data"": { ""ALL"": [
					{ ""card_id"": ""YOP_001"", ""drawn_win_rate"": 60.0, ""num_games"": 1000 },
					{ ""card_id"": ""CORE_YOP_001"", ""drawn_win_rate"": 50.0, ""num_games"": 3000 }
				] } }";
			var source = await LoadAsync(payload);

			Assert.NotNull(source.GetNormalizedScore(Dbf("YOP_001")));
			Assert.NotNull(source.GetNormalizedScore(Dbf("CORE_YOP_001")));
			Assert.Equal(
				source.GetNormalizedScore(Dbf("YOP_001"))!.Value.Score,
				source.GetNormalizedScore(Dbf("CORE_YOP_001"))!.Value.Score, 6);

			// One pooled sample of 4000 games, at the games-weighted rate.
			var canonical = source.GetRaw(Dbf("YOP_001")) ?? source.GetRaw(Dbf("CORE_YOP_001"))!;
			Assert.Equal(4000, canonical.Games);
			Assert.Equal((60.0 * 1000 + 50.0 * 3000) / 4000, canonical.DrawnWinrate!.Value, 6);
		}

		// Three classes with different average card win-rates -> a ranked tier list.
		private const string ClassPayload = @"{
			""data"": {
				""ALL"": [ { ""card_id"": ""CS2_182"", ""drawn_win_rate"": 50.0, ""num_games"": 2000 } ],
				""WARRIOR"": [
					{ ""card_id"": ""CS2_106"", ""drawn_win_rate"": 58.0, ""num_games"": 1500 },
					{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 56.0, ""num_games"": 1500 }
				],
				""MAGE"": [
					{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 50.0, ""num_games"": 1500 },
					{ ""card_id"": ""CS2_200"", ""drawn_win_rate"": 49.0, ""num_games"": 1500 }
				],
				""PRIEST"": [
					{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 43.0, ""num_games"": 1500 },
					{ ""card_id"": ""CS2_200"", ""drawn_win_rate"": 41.0, ""num_games"": 1500 }
				]
			} }";

		[Fact]
		public async Task Class_buckets_become_a_ranked_bounded_tier_list()
		{
			var source = await LoadAsync(ClassPayload);

			Assert.NotNull(source.ClassScores);
			var warrior = source.ClassScores![CardClass.WARRIOR];
			var mage = source.ClassScores![CardClass.MAGE];
			var priest = source.ClassScores![CardClass.PRIEST];

			Assert.True(warrior > mage && mage > priest); // ranked by strength
			Assert.All(new[] { warrior, mage, priest }, s => Assert.InRange(s, 0.0, 100.0));
		}

		[Fact]
		public async Task Hero_skins_are_scored_by_their_class_tier()
		{
			var source = await LoadAsync(ClassPayload);

			var garrosh = source.GetNormalizedScore(Dbf("HERO_01"))!.Value.Score; // Warrior
			var jaina = source.GetNormalizedScore(Dbf("HERO_08"))!.Value.Score;   // Mage

			Assert.True(garrosh > jaina);
		}

		[Fact]
		public async Task Hero_skin_of_a_class_without_data_returns_null()
		{
			var source = await LoadAsync(ClassPayload);

			Assert.Null(source.GetNormalizedScore(Dbf("HERO_04"))); // Paladin: no bucket
		}

		[Fact]
		public void Before_loading_everything_returns_null()
		{
			var source = new HsReplayArenaDataSource(_cacheDir);

			Assert.False(source.IsLoaded);
			Assert.Null(source.GetNormalizedScore(Dbf("CS2_106")));
			Assert.Null(source.GetNormalizedScore(Dbf("HERO_01")));
		}

		// A card that is mediocre overall but excels in mage (and vice versa), so the
		// class-agnostic and per-class orderings disagree.
		private const string PerClassPayload = @"{
			""data"": {
				""ALL"": [
					{ ""card_id"": ""CS2_120"", ""drawn_win_rate"": 55.0, ""num_games"": 3000 },
					{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 50.0, ""num_games"": 3000 },
					{ ""card_id"": ""CS2_200"", ""drawn_win_rate"": 45.0, ""num_games"": 3000 },
					{ ""card_id"": ""CS2_189"", ""drawn_win_rate"": 48.0, ""num_games"": 3000 }
				],
				""MAGE"": [
					{ ""card_id"": ""CS2_182"", ""drawn_win_rate"": 58.0, ""num_games"": 1500 },
					{ ""card_id"": ""CS2_120"", ""drawn_win_rate"": 40.0, ""num_games"": 1500 }
				]
			} }";

		[Fact]
		public async Task Known_draft_class_scores_from_that_class_bucket()
		{
			var source = await LoadAsync(PerClassPayload);

			// Class-agnostic ordering (ALL): CS2_120 (55) beats CS2_182 (50)...
			Assert.True(source.GetNormalizedScore(Dbf("CS2_120"))!.Value.Score > source.GetNormalizedScore(Dbf("CS2_182"))!.Value.Score);
			// ...but drafting mage flips it: in the MAGE bucket CS2_182 (58) beats CS2_120 (40).
			Assert.True(source.GetNormalizedScore(Dbf("CS2_182"), CardClass.MAGE)!.Value.Score
				> source.GetNormalizedScore(Dbf("CS2_120"), CardClass.MAGE)!.Value.Score);
		}

		[Fact]
		public async Task Card_missing_from_the_class_bucket_falls_back_to_all()
		{
			var source = await LoadAsync(PerClassPayload);

			// CS2_189 has no MAGE entry: the mage-context score IS the ALL score.
			Assert.Equal(
				source.GetNormalizedScore(Dbf("CS2_189")),
				source.GetNormalizedScore(Dbf("CS2_189"), CardClass.MAGE));
		}

		[Fact]
		public async Task Class_without_a_bucket_falls_back_to_all()
		{
			var source = await LoadAsync(PerClassPayload);

			Assert.Equal(
				source.GetNormalizedScore(Dbf("CS2_120")),
				source.GetNormalizedScore(Dbf("CS2_120"), CardClass.PRIEST));
		}
	}
}
