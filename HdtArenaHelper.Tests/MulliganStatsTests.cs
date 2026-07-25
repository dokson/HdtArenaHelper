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
	/// The mulligan statistics, offline: synthetic class files are written into the cache so parsing
	/// never touches the network. What is pinned is the POLICY — thin cards are dropped rather than
	/// asserted, what survives is shrunk toward its class, and reprints pool — not specific rates.
	/// </summary>
	public class MulliganStatsTests : IDisposable
	{
		private readonly string _cacheDir = Path.Combine(
			Path.GetTempPath(), "HdtArenaHelperTests", Guid.NewGuid().ToString("N"));

		public void Dispose()
		{
			// Reported rather than swallowed: a temp dir that will not delete is usually a file still
			// open, which is worth seeing in the test output even though it must not fail the run.
			try
			{
				Directory.Delete(_cacheDir, recursive: true);
			}
			catch(IOException ex)
			{
				Console.WriteLine($"could not remove test cache {_cacheDir}: {ex.Message}");
			}
			catch(UnauthorizedAccessException ex)
			{
				Console.WriteLine($"could not remove test cache {_cacheDir}: {ex.Message}");
			}
		}

		private static int Dbf(string cardId) => Cards.All[cardId].DbfId;

		/// <summary>One card's mulligan counters, as the CDN reports them.</summary>
		private static string Card(string cardId, int keptGames, int keptWins, int kept, int offered)
			=> $@"{{ ""cardId"": ""{cardId}"", ""stats"": {{
					""drawn"": {Math.Max(1, keptGames)}, ""drawnThenWin"": {keptWins},
					""inHandAfterMulligan"": {keptGames}, ""inHandAfterMulliganThenWin"": {keptWins},
					""keptInMulligan"": {kept}, ""drawnBeforeMulligan"": {offered} }} }}";

		private async Task<FirestoneArenaDataSource> LoadAsync(string cls, params string[] cards)
		{
			Directory.CreateDirectory(_cacheDir);
			var json = $@"{{ ""lastUpdated"": ""2026-07-25T00:00:00Z"", ""context"": ""{cls}"",
				""stats"": [ {string.Join(",", cards)} ] }}";
			File.WriteAllText(Path.Combine(_cacheDir, $"firestone_{cls}.json"), json);
			var source = new FirestoneArenaDataSource(_cacheDir, classes: new List<string> { cls });
			await source.EnsureLoadedAsync();
			return source;
		}

		[Fact]
		public async Task Keep_win_rate_and_keep_rate_are_reported_in_real_units()
		{
			var source = await LoadAsync("mage",
				Card("CS2_182", keptGames: 4000, keptWins: 2200, kept: 700, offered: 1000),
				Card("CS2_172", keptGames: 4000, keptWins: 1800, kept: 200, offered: 1000));

			var yeti = source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_182"))!.Value;
			var bloodfen = source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_172"))!.Value;

			// Percentage points, not a 0-100 blend: 55% vs 45%, and kept 70% vs 20% of the time.
			Assert.InRange(yeti.KeepWinRate, 54, 56);
			Assert.InRange(bloodfen.KeepWinRate, 44, 46);
			Assert.InRange(yeti.KeepRate, 68, 72);
			Assert.InRange(bloodfen.KeepRate, 18, 22);
			Assert.Equal(4000, yeti.Games);
		}

		[Fact]
		public async Task A_thin_card_is_not_reported_at_all()
		{
			// At the mulligan a missing number is honest; a number off a handful of games is a
			// recommendation nobody can check. Measured on a live payload, a class has as few as 69
			// cards with 30+ keep observations, so this branch is the common case, not an edge one.
			var source = await LoadAsync("mage",
				Card("CS2_182", keptGames: 4000, keptWins: 2200, kept: 700, offered: 1000),
				Card("CS2_172", keptGames: 3, keptWins: 3, kept: 3, offered: 3));

			Assert.NotNull(source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_182")));
			Assert.Null(source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_172")));
		}

		[Fact]
		public async Task A_thinly_sampled_card_is_pulled_toward_its_class_average()
		{
			// Same extreme raw rate (100% kept-and-won), very different samples: the thin one must
			// end up near the class average, the thick one may assert itself. Pinned as an ordering
			// plus a bound, never as a value.
			var source = await LoadAsync("mage",
				Card("CS2_182", keptGames: 20, keptWins: 20, kept: 20, offered: 20),
				Card("CS2_172", keptGames: 20000, keptWins: 20000, kept: 20000, offered: 20000),
				Card("CS2_200", keptGames: 20000, keptWins: 4000, kept: 4000, offered: 20000));

			var thin = source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_182"))!.Value;
			var thick = source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_172"))!.Value;

			Assert.True(thin.KeepWinRate < thick.KeepWinRate);
			Assert.True(thin.KeepWinRate < 100);
			Assert.True(thick.KeepWinRate > 90);
		}

		[Fact]
		public async Task Two_printings_of_one_card_pool_their_mulligan_samples()
		{
			// The same identity rule the win-rates use: the feed reports whichever printing it has,
			// and either dbf id must find the pooled record.
			var source = await LoadAsync("mage",
				Card("YOP_001", keptGames: 1000, keptWins: 600, kept: 500, offered: 1000),
				Card("CORE_YOP_001", keptGames: 3000, keptWins: 1500, kept: 1500, offered: 3000));

			var viaOld = source.GetMulliganStats(CardClass.MAGE, Dbf("YOP_001"));
			var viaCore = source.GetMulliganStats(CardClass.MAGE, Dbf("CORE_YOP_001"));

			Assert.NotNull(viaOld);
			Assert.Equal(viaOld!.Value.KeepWinRate, viaCore!.Value.KeepWinRate, 6);
			Assert.Equal(4000, viaOld.Value.Games); // 1000 + 3000, pooled as counts
		}

		[Fact]
		public async Task Each_card_carries_its_class_average_as_the_reference()
		{
			// The card's own rate is unreadable without it: arena keep win-rates all sit near 50, so
			// the overlay colours and sizes its bar by the DISTANCE from an average keep in that
			// class. The reference must therefore travel with the card, and must be the same for
			// every card of the class.
			var source = await LoadAsync("mage",
				Card("CS2_182", keptGames: 10000, keptWins: 6000, kept: 5000, offered: 10000),
				Card("CS2_172", keptGames: 10000, keptWins: 4000, kept: 5000, offered: 10000));

			var strong = source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_182"))!.Value;
			var weak = source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_172"))!.Value;

			Assert.Equal(strong.ClassAverage, weak.ClassAverage, 6);
			// 6000 + 4000 wins over 20000 keeps = 50%, and the two cards sit either side of it.
			Assert.Equal(50.0, strong.ClassAverage, 1);
			Assert.True(strong.KeepWinRate > strong.ClassAverage);
			Assert.True(weak.KeepWinRate < weak.ClassAverage);
		}

		[Fact]
		public async Task An_unknown_class_or_card_reports_nothing()
		{
			var source = await LoadAsync("mage",
				Card("CS2_182", keptGames: 4000, keptWins: 2200, kept: 700, offered: 1000));

			Assert.Null(source.GetMulliganStats(CardClass.PRIEST, Dbf("CS2_182")));
			Assert.Null(source.GetMulliganStats(CardClass.MAGE, Dbf("CS2_172")));
			Assert.Null(source.GetMulliganStats(CardClass.MAGE, dbfId: -1));
		}
	}
}
