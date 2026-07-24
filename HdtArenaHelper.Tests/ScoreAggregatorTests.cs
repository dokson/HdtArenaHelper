using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class ScoreAggregatorTests
	{
		private sealed class FakeSource : IArenaDataSource
		{
			private readonly Dictionary<int, double?> _scores;
			public FakeSource(string name, double weight, Dictionary<int, double?> scores)
			{
				Name = name;
				Weight = weight;
				_scores = scores;
			}
			public string Name { get; }
			public double Weight { get; }
			public bool IsLoaded => true;
			public Task EnsureLoadedAsync() => Task.CompletedTask;
			public double? GetNormalizedScore(int dbfId)
				=> _scores.TryGetValue(dbfId, out var v) ? v : null;
		}

		private sealed class FakeSynergy : ISynergyEngine
		{
			private readonly double _bonus;
			public FakeSynergy(double bonus) => _bonus = bonus;
			public double GetSynergyBonus(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds) => _bonus;
		}

		private static readonly int[] NoDeck = new int[0];

		[Fact]
		public void Blends_sources_by_weight()
		{
			var a = new FakeSource("A", 1.0, new Dictionary<int, double?> { { 1, 80 } });
			var b = new FakeSource("B", 3.0, new Dictionary<int, double?> { { 1, 40 } });
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
			var a = new FakeSource("A", 1.0, new Dictionary<int, double?> { { 1, 70 } });
			var b = new FakeSource("B", 1.0, new Dictionary<int, double?>()); // no data for card 1
			var agg = new ScoreAggregator(new IArenaDataSource[] { a, b });

			var s = agg.Score(1, NoDeck);

			Assert.Equal(70, s.Value, 3);
			Assert.Single(s.Components);
		}

		[Fact]
		public void No_data_returns_empty()
		{
			var a = new FakeSource("A", 1.0, new Dictionary<int, double?>());
			var agg = new ScoreAggregator(new[] { a });

			var s = agg.Score(1, NoDeck);

			Assert.False(s.HasData);
			Assert.Equal(0, s.Value);
		}

		[Fact]
		public void Synergy_bonus_is_added_and_clamped_to_100()
		{
			var a = new FakeSource("A", 1.0, new Dictionary<int, double?> { { 1, 95 } });
			var agg = new ScoreAggregator(new[] { a });
			agg.SetSynergyEngine(new FakeSynergy(20));

			var s = agg.Score(1, NoDeck);

			Assert.Equal(100, s.Value, 3); // 95 + 20 clamped to 100
			Assert.Equal(20, s.SynergyBonus, 3);
		}

		[Fact]
		public void Negative_synergy_is_clamped_to_0()
		{
			var a = new FakeSource("A", 1.0, new Dictionary<int, double?> { { 1, 10 } });
			var agg = new ScoreAggregator(new[] { a });
			agg.SetSynergyEngine(new FakeSynergy(-30));

			var s = agg.Score(1, NoDeck);

			Assert.Equal(0, s.Value, 3); // 10 - 30 clamped to 0
		}

		[Fact]
		public void Zero_weight_source_is_ignored()
		{
			var a = new FakeSource("A", 0.0, new Dictionary<int, double?> { { 1, 10 } });
			var b = new FakeSource("B", 1.0, new Dictionary<int, double?> { { 1, 60 } });
			var agg = new ScoreAggregator(new IArenaDataSource[] { a, b });

			var s = agg.Score(1, NoDeck);

			Assert.Equal(60, s.Value, 3);
			Assert.Single(s.Components);
		}
	}
}
