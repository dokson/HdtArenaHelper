using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using HearthDb;
using HearthDb.Enums;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// Exercises the Firestone source fully offline: synthetic per-class payloads are
	/// written to the cache files (the 1-day TTL makes them "fresh"), so
	/// EnsureLoadedAsync parses them without touching the network.
	/// </summary>
	public class FirestoneArenaDataSourceTests : IDisposable
	{
		private readonly string _cacheDir = Path.Combine(
			Path.GetTempPath(), "HdtArenaHelperTests", Guid.NewGuid().ToString("N"));

		public void Dispose()
		{
			try { Directory.Delete(_cacheDir, recursive: true); } catch { /* temp dir */ }
		}

		private static int Dbf(string cardId) => Cards.All[cardId].DbfId;

		private async Task<FirestoneArenaDataSource> LoadAsync(params (string Cls, string Json)[] classFiles)
		{
			// Restrict the source to exactly the classes we cache, so a cache miss can
			// never fall through to a real network download inside a test.
			var classes = new List<string>();
			Directory.CreateDirectory(_cacheDir);
			foreach(var (cls, json) in classFiles)
			{
				classes.Add(cls);
				File.WriteAllText(Path.Combine(_cacheDir, $"firestone_{cls}.json"), json);
			}
			var source = new FirestoneArenaDataSource(_cacheDir, classes: classes);
			await source.EnsureLoadedAsync();
			return source;
		}

		private static string ClassFile(string cls, params (string CardId, int Drawn, int DrawnThenWin)[] cards)
		{
			var entries = new System.Text.StringBuilder();
			foreach(var (cardId, drawn, wins) in cards)
			{
				if(entries.Length > 0)
					entries.Append(",");
				entries.Append($@"{{ ""cardId"": ""{cardId}"", ""context"": ""{cls}"", " +
					$@"""stats"": {{ ""drawn"": {drawn}, ""drawnThenWin"": {wins} }} }}");
			}
			return $@"{{ ""lastUpdated"": ""2026-07-24"", ""context"": ""{cls}"", ""stats"": [ {entries} ] }}";
		}

		[Fact]
		public async Task Scores_cards_by_pooled_drawn_win_rate()
		{
			var source = await LoadAsync(("mage", ClassFile("mage",
				("CS2_120", 2000, 800),    // 40%
				("CS2_182", 2000, 1000),   // 50% (median)
				("CS2_200", 2000, 1200)))); // 60%

			Assert.True(source.IsLoaded);
			var low = source.GetNormalizedScore(Dbf("CS2_120"))!.Value.Score;
			var mid = source.GetNormalizedScore(Dbf("CS2_182"))!.Value.Score;
			var high = source.GetNormalizedScore(Dbf("CS2_200"))!.Value.Score;

			Assert.Equal(50.0, mid, 0);          // median card -> 50
			Assert.True(high > 50 && low < 50);   // monotone around the anchor
			Assert.True(high < 100 && low > 0);   // bounded, no outlier pinning
		}

		[Fact]
		public async Task Pools_a_card_across_class_files_weighted_by_draws()
		{
			// CS2_182 (neutral) appears in two classes: 60% over 3000 draws in mage,
			// 40% over 1000 in warrior -> pooled 55%, above the 50% card.
			var source = await LoadAsync(
				("mage", ClassFile("mage", ("CS2_182", 3000, 1800), ("CS2_120", 2000, 1000))),
				("warrior", ClassFile("warrior", ("CS2_182", 1000, 400), ("CS2_200", 2000, 900))));

			Assert.True(source.GetNormalizedScore(Dbf("CS2_182"))!.Value.Score > source.GetNormalizedScore(Dbf("CS2_120"))!.Value.Score);
		}

		[Fact]
		public async Task Known_draft_class_scores_from_that_class_file()
		{
			// Pooled (weighted by draws): CS2_120 55% beats CS2_182 46.7%; in the WARRIOR
			// file alone (40% vs 56.7%) the ordering is reversed.
			var source = await LoadAsync(
				("mage", ClassFile("mage", ("CS2_120", 3000, 1800), ("CS2_182", 3000, 1300))),
				("warrior", ClassFile("warrior", ("CS2_120", 1000, 400), ("CS2_182", 1000, 567))));

			Assert.True(source.GetNormalizedScore(Dbf("CS2_120"))!.Value.Score > source.GetNormalizedScore(Dbf("CS2_182"))!.Value.Score);
			Assert.True(source.GetNormalizedScore(Dbf("CS2_182"), CardClass.WARRIOR)!.Value.Score
				> source.GetNormalizedScore(Dbf("CS2_120"), CardClass.WARRIOR)!.Value.Score);
		}

		[Fact]
		public async Task Class_without_a_file_falls_back_to_pooled()
		{
			var source = await LoadAsync(("mage", ClassFile("mage",
				("CS2_120", 2000, 800), ("CS2_182", 2000, 1000), ("CS2_200", 2000, 1200))));

			Assert.Equal(
				source.GetNormalizedScore(Dbf("CS2_120")),
				source.GetNormalizedScore(Dbf("CS2_120"), CardClass.PRIEST));
		}

		[Fact]
		public async Task Class_files_become_a_tier_list_scoring_hero_skins()
		{
			// Warrior cards average well above mage cards.
			var source = await LoadAsync(
				("warrior", ClassFile("warrior", ("CS2_106", 2000, 1160), ("CS2_182", 2000, 1120))),
				("mage", ClassFile("mage", ("CS2_182", 2000, 900), ("CS2_200", 2000, 860))));

			var garrosh = source.GetNormalizedScore(Dbf("HERO_01"))!.Value.Score; // Warrior
			var jaina = source.GetNormalizedScore(Dbf("HERO_08"))!.Value.Score;   // Mage

			Assert.True(garrosh > jaina);
			Assert.Null(source.GetNormalizedScore(Dbf("HERO_04"))); // Paladin: no file
		}

		[Fact]
		public async Task Cards_below_the_min_draws_floor_are_dropped()
		{
			var source = await LoadAsync(("mage", ClassFile("mage",
				("CS2_182", 2000, 1000),
				("CS2_200", 5, 5)))); // 5 draws < floor

			Assert.NotNull(source.GetNormalizedScore(Dbf("CS2_182")));
			Assert.Null(source.GetNormalizedScore(Dbf("CS2_200")));
		}

		[Fact]
		public async Task A_broken_class_file_costs_that_class_not_the_source()
		{
			var source = await LoadAsync(
				("mage", ClassFile("mage", ("CS2_182", 2000, 1000), ("CS2_200", 2000, 1200))),
				("warrior", "{ not valid json"));

			// Partial data is published and scoreable...
			Assert.NotNull(source.GetNormalizedScore(Dbf("CS2_182")));
			Assert.Null(source.GetNormalizedScore(Dbf("HERO_01"))); // warrior tier missing
			// ...but the source does not report complete, so the warm-up loop keeps
			// retrying the missing class instead of freezing it out for the session,
			// and the unusable cache file was dropped so that retry reaches the network
			// instead of rereading the same garbage until the TTL expires.
			Assert.False(source.IsLoaded);
			Assert.False(File.Exists(Path.Combine(_cacheDir, "firestone_warrior.json")));
		}

		[Fact]
		public void Before_loading_everything_returns_null()
		{
			var source = new FirestoneArenaDataSource(_cacheDir);

			Assert.False(source.IsLoaded);
			Assert.Null(source.GetNormalizedScore(Dbf("CS2_182")));
			Assert.Null(source.GetNormalizedScore(Dbf("HERO_01")));
		}
	}
}
