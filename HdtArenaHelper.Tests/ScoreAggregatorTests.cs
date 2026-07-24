using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class ScoreAggregatorTests
	{
		private sealed class FakeSource : IArenaDataSource
		{
			private readonly Dictionary<int, SourceScore?> _scores;
			// Scores without games -> sample confidence 1.0, so the plain weighted-mean
			// expectations in the tests below hold unchanged.
			public FakeSource(string name, double weight, Dictionary<int, SourceScore?> scores)
			{
				Name = name;
				Weight = weight;
				_scores = scores;
			}
			public string Name { get; }
			public double Weight { get; }
			public bool IsLoaded => true;
			public Task EnsureLoadedAsync() => Task.CompletedTask;
			public SourceScore? GetNormalizedScore(int dbfId, HearthDb.Enums.CardClass draftClass = HearthDb.Enums.CardClass.INVALID)
				=> _scores.TryGetValue(dbfId, out var v) ? v : null;
		}

		private static Dictionary<int, SourceScore?> Scores(int dbfId, double score, int? games = null)
			=> new Dictionary<int, SourceScore?> { { dbfId, new SourceScore(score, games) } };

		private sealed class FakeSynergy : ISynergyEngine
		{
			private readonly double _bonus;
			public FakeSynergy(double bonus) => _bonus = bonus;
			public SynergyResult GetSynergy(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds)
				=> new SynergyResult(_bonus, "fake reason");
		}

		private static readonly int[] NoDeck = new int[0];

		[Fact]
		public void Blends_sources_by_weight()
		{
			var a = new FakeSource("A", 1.0, Scores(1, 80));
			var b = new FakeSource("B", 3.0, Scores(1, 40));
			var agg = new ScoreAggregator(new IArenaDataSource[] { a, b });

			var s = agg.Score(1, NoDeck);

			// weighted mean = (80*1 + 40*3) / 4 = 50
			Assert.True(s.HasData);
			Assert.Equal(50, s.Value, 3);
			Assert.Equal(2, s.Components.Count);
		}

		[Fact]
		public void Missing_source_lowers_confidence_not_score()
		{
			var a = new FakeSource("A", 1.0, Scores(1, 70));
			var b = new FakeSource("B", 1.0, new Dictionary<int, SourceScore?>()); // no data for card 1
			var agg = new ScoreAggregator(new IArenaDataSource[] { a, b });

			var s = agg.Score(1, NoDeck);

			Assert.Equal(70, s.Value, 3);
			Assert.Single(s.Components);
		}

		[Fact]
		public void No_data_returns_empty()
		{
			var a = new FakeSource("A", 1.0, new Dictionary<int, SourceScore?>());
			var agg = new ScoreAggregator(new[] { a });

			var s = agg.Score(1, NoDeck);

			Assert.False(s.HasData);
			Assert.Equal(0, s.Value);
		}

		[Fact]
		public void Synergy_bonus_is_added_and_clamped_to_100()
		{
			var a = new FakeSource("A", 1.0, Scores(1, 95));
			var agg = new ScoreAggregator(new[] { a });
			agg.SetSynergyEngine(new FakeSynergy(20));

			var s = agg.Score(1, NoDeck);

			Assert.Equal(100, s.Value, 3); // 95 + 20 clamped to 100
			Assert.Equal(20, s.SynergyBonus, 3);
		}

		[Fact]
		public void Negative_synergy_is_clamped_to_0()
		{
			var a = new FakeSource("A", 1.0, Scores(1, 10));
			var agg = new ScoreAggregator(new[] { a });
			agg.SetSynergyEngine(new FakeSynergy(-30));

			var s = agg.Score(1, NoDeck);

			Assert.Equal(0, s.Value, 3); // 10 - 30 clamped to 0
		}

		[Fact]
		public void Sample_size_weights_the_blend_per_card()
		{
			// Same configured weight, wildly different samples: the 5000-game estimate
			// must dominate the 30-game one instead of averaging 50/50.
			var big = new FakeSource("big", 1.0, Scores(1, 80, games: 5000));
			var small = new FakeSource("small", 1.0, Scores(1, 40, games: 30));
			var agg = new ScoreAggregator(new IArenaDataSource[] { big, small });

			var s = agg.Score(1, NoDeck);

			Assert.True(s.Value > 60, $"expected the large sample to dominate, got {s.Value}");
			Assert.True(s.Value < 80);
			// The per-card effective weights (not the configured ones) are exposed.
			Assert.True(s.Components[0].Weight > s.Components[1].Weight);
			Assert.Equal(5000, s.Components[0].Games);
		}

		[Fact]
		public void Model_based_sources_keep_their_configured_weight()
		{
			// No games (heuristic-style) -> confidence 1.0: plain weighted mean.
			var modelBased = new FakeSource("model", 0.5, Scores(1, 40));
			var huge = new FakeSource("winrate", 0.5, Scores(1, 80, games: 1_000_000));
			var agg = new ScoreAggregator(new IArenaDataSource[] { modelBased, huge });

			var s = agg.Score(1, NoDeck);

			// winrate confidence ~1.0 -> ~equal weights -> ~60.
			Assert.Equal(60, s.Value, 0);
		}

		[Fact]
		public void Zero_weight_source_is_ignored()
		{
			var a = new FakeSource("A", 0.0, Scores(1, 10));
			var b = new FakeSource("B", 1.0, Scores(1, 60));
			var agg = new ScoreAggregator(new IArenaDataSource[] { a, b });

			var s = agg.Score(1, NoDeck);

			Assert.Equal(60, s.Value, 3);
			Assert.Single(s.Components);
		}
	}
}
