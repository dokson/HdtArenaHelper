using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class ScoreAggregatorTests
	{
		private sealed class FakeSource : IArenaDataSource
		{
			private readonly Dictionary<int, SourceScore?> _scores;
			// A score WITHOUT games is model-based; one WITH games is empirical. The blend-math
			// tests below pass an identical games count to every source so the sample-confidence
			// factor cancels in the weighted mean and their expectations stay exact, while still
			// exercising the empirical path (a model-only card is deliberately shrunk toward
			// neutral - see Model_only_scores_are_shrunk_toward_neutral).
			public FakeSource(string name, double weight, Dictionary<int, SourceScore?> scores,
				bool? hasSamples = null)
			{
				Name = name;
				Weight = weight;
				_scores = scores;
				// Defaults to "empirical iff it carries games anywhere", which suits the existing
				// tests; pass it explicitly for a feed that has no rows for the card under test.
				HasSamples = hasSamples ?? scores.Values.Any(v => v?.Games != null);
			}
			public string Name { get; }
			public double Weight { get; }
			public bool IsLoaded => true;
			// A property of the SOURCE, not of one card: a feed with no row for the card under test
			// must still count as empirical, exactly as HsReplay/Firestone do.
			public bool HasSamples { get; }
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
			public SynergyResult GetSynergy(int offeredDbfId, IReadOnlyCollection<int> draftedDbfIds,
				HearthDb.Enums.CardClass draftClass = HearthDb.Enums.CardClass.INVALID)
				=> new SynergyResult(_bonus, "fake reason");
		}

		private static readonly int[] NoDeck = new int[0];

		[Fact]
		public void A_winrate_source_without_a_per_card_sample_still_counts_as_real_data()
		{
			// The hero pick: the class TIER is a real win-rate, backed by a whole class bucket rather
			// than one card, so it carries no games. Deducing "no data" from "no games" made the
			// overlay print "win-rate data unavailable" over three displayed win-rates and star every
			// class as low-confidence — verified on a live client, hence this test.
			var tier = new FakeSource("HSReplay", 0.5,
				new Dictionary<int, SourceScore?> { { 1, new SourceScore(58) } }, hasSamples: true);
			var agg = new ScoreAggregator(new IArenaDataSource[] { tier });

			var s = agg.Score(1, NoDeck);

			Assert.True(s.HasData);
			Assert.True(s.HasWinRateData);
			Assert.False(s.IsLowConfidence);
			Assert.Null(s.MaxGames);
		}

		[Fact]
		public void A_model_only_score_is_still_flagged_low_confidence()
		{
			// The other side of the same rule: no empirical source at all must stay flagged, or the
			// fix above would silently bless heuristic-only scores as measured.
			var model = new FakeSource("Heuristic", 0.5,
				new Dictionary<int, SourceScore?> { { 1, new SourceScore(58) } }, hasSamples: false);
			var agg = new ScoreAggregator(new IArenaDataSource[] { model });

			var s = agg.Score(1, NoDeck);

			Assert.True(s.HasData);
			Assert.False(s.HasWinRateData);
			Assert.True(s.IsLowConfidence);
		}

		[Fact]
		public void Blends_sources_by_weight()
		{
			var a = new FakeSource("A", 1.0, Scores(1, 80, games: 5000));
			var b = new FakeSource("B", 3.0, Scores(1, 40, games: 5000));
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
			var a = new FakeSource("A", 1.0, Scores(1, 70, games: 5000));
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
			var a = new FakeSource("A", 1.0, Scores(1, 95, games: 5000));
			var agg = new ScoreAggregator(new[] { a });
			agg.SetSynergyEngine(new FakeSynergy(20));

			var s = agg.Score(1, NoDeck);

			Assert.Equal(100, s.Value, 3); // 95 + 20 clamped to 100
			Assert.Equal(20, s.SynergyBonus, 3);
		}

		[Fact]
		public void Negative_synergy_is_clamped_to_0()
		{
			var a = new FakeSource("A", 1.0, Scores(1, 10, games: 5000));
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
		public void A_model_source_tracks_the_empirical_confidence()
		{
			// Renamed from "keeps its configured weight", which is no longer what happens: a model
			// source is scaled by the empirical sources' collective confidence. With a huge sample
			// that factor is ~1, so this also pins the abundant-data case at its configured weight.
			var modelBased = new FakeSource("model", 0.5, Scores(1, 40));
			var huge = new FakeSource("winrate", 0.5, Scores(1, 80, games: 1_000_000));
			var agg = new ScoreAggregator(new IArenaDataSource[] { modelBased, huge });

			var s = agg.Score(1, NoDeck);

			Assert.Equal(0.5, s.Components.Single(c => c.SourceName == "model").Weight, 3);
			Assert.Equal(60, s.Value, 0);
		}

		[Fact]
		public void An_uncovered_feed_lowers_confidence_instead_of_promoting_the_model()
		{
			// Two feeds configured, only ONE has a row for this card — the normal case for obscure
			// cards, i.e. exactly where the model is measured to be worst. The model's share must
			// stay at its configured third rather than rising to a half.
			var covering = new FakeSource("wr-a", 0.5, Scores(1, 80, games: 5000));
			var absent = new FakeSource("wr-b", 0.5,
				new Dictionary<int, SourceScore?> { { 2, new SourceScore(80, 5000) } }, hasSamples: true);
			var model = new FakeSource("model", 0.5, Scores(1, 20));
			var agg = new ScoreAggregator(new IArenaDataSource[] { covering, absent, model });

			var s = agg.Score(1, NoDeck);
			var modelShare = s.Components.Single(c => c.SourceName == "model").Weight
				/ s.Components.Sum(c => c.Weight);

			Assert.Equal(1.0 / 3.0, modelShare, 2);
		}

		[Fact]
		public void The_model_share_is_the_collective_empirical_confidence()
		{
			// Two empirical sources with very different samples: the model must be scaled by the
			// weighted COLLECTIVE factor, which is what keeps its share exactly one third. With a
			// single empirical source this is indistinguishable from min/first — hence two here.
			var big = new FakeSource("wr-big", 0.5, Scores(1, 5000, games: 5000));
			var small = new FakeSource("wr-small", 0.5, Scores(1, 20, games: 20));
			var model = new FakeSource("model", 0.5, Scores(1, 40));
			var agg = new ScoreAggregator(new IArenaDataSource[] { big, small, model });

			var s = agg.Score(1, NoDeck);
			var modelShare = s.Components.Single(c => c.SourceName == "model").Weight
				/ s.Components.Sum(c => c.Weight);

			Assert.Equal(1.0 / 3.0, modelShare, 3);
		}

		[Fact]
		public void Thin_win_rate_data_does_not_promote_the_model_based_source()
		{
			// The failure this guards: scaling only the empirical sources by sample confidence let
			// the heuristic's share of the blend grow as the real evidence thinned (33% intended,
			// 67% at 20 games) — and it is measured to be at its worst on exactly those cards.
			// Its share must be the same at every sample size.
			static double ModelShare(int games)
			{
				var wr = new FakeSource("winrate", 1.0, Scores(1, 80, games: games));
				var model = new FakeSource("model", 0.5, Scores(1, 20));
				var s = new ScoreAggregator(new IArenaDataSource[] { wr, model }).Score(1, NoDeck);
				var total = s.Components.Sum(c => c.Weight);
				return s.Components.Single(c => c.SourceName == "model").Weight / total;
			}

			var abundant = ModelShare(5000);
			var thin = ModelShare(20);

			Assert.Equal(1.0 / 3.0, abundant, 3);
			Assert.Equal(abundant, thin, 3);
		}

		[Fact]
		public void An_uncovered_card_still_gets_the_shrunk_backstop_in_the_shipped_wiring()
		{
			// Mirrors BuildAggregator: TWO empirical feeds plus the model. The earlier version of
			// this test used the model source ALONE, a configuration the plugin never builds — so
			// it stayed green while every card neither feed covered rendered as "no data", with the
			// backstop and its shrink both unreachable in production. Any test of the model-only
			// path must therefore include the empirical sources that are actually configured.
			var noRows = new Dictionary<int, SourceScore?>();
			var wrA = new FakeSource("wr-a", 0.5, noRows, hasSamples: true);
			var wrB = new FakeSource("wr-b", 0.5, noRows, hasSamples: true);
			var model = new FakeSource("model", 0.5, Scores(1, 90));
			var agg = new ScoreAggregator(new IArenaDataSource[] { wrA, wrB, model });

			var s = agg.Score(1, NoDeck);

			Assert.True(s.HasData, "an uncovered card must still get the heuristic backstop");
			Assert.Equal(ScoreAggregator.NeutralScore
				+ (90 - ScoreAggregator.NeutralScore) * ScoreAggregator.ModelOnlyShrink, s.Value, 3);
			Assert.True(s.IsLowConfidence);
		}

		[Fact]
		public void Model_only_scores_are_shrunk_toward_neutral()
		{
			// No empirical sample for this card: the score rests entirely on the offline model,
			// which is measured to be no better than a constant on thinly-sampled cards. So it
			// must not state a confident number.
			var modelOnly = new FakeSource("model", 0.5, Scores(1, 90));
			var agg = new ScoreAggregator(new[] { modelOnly });

			var s = agg.Score(1, NoDeck);

			// Pin the FACTOR, not just the sign: `50 < v < 90` passes for any shrink in (0,1), so
			// ModelOnlyShrink could drift arbitrarily while the test stayed green.
			Assert.True(s.HasData);
			Assert.Equal(ScoreAggregator.NeutralScore
				+ (90 - ScoreAggregator.NeutralScore) * ScoreAggregator.ModelOnlyShrink, s.Value, 3);
			Assert.True(s.IsLowConfidence);
		}

		[Fact]
		public void Shrinking_preserves_the_order_among_unmeasured_cards()
		{
			// Shrinking toward neutral is monotone, so it removes an unmeasured card's ability to
			// outrank a well-sampled one WITHOUT reordering the unmeasured cards among themselves.
			var model = new FakeSource("model", 0.5, new Dictionary<int, SourceScore?>
			{
				{ 1, new SourceScore(90) },
				{ 2, new SourceScore(70) },
				{ 3, new SourceScore(20) },
			});
			var agg = new ScoreAggregator(new[] { model });

			var a = agg.Score(1, NoDeck).Value;
			var b = agg.Score(2, NoDeck).Value;
			var c = agg.Score(3, NoDeck).Value;

			Assert.True(a > b && b > c, $"order lost: {a}, {b}, {c}");
			// Order alone is vacuous — a positive affine map preserves it, so this would pass with
			// the shrink absent entirely. Also assert the deviations really were pulled in, and
			// that the one BELOW neutral was pulled UP.
			Assert.True(a < 90, $"the top score was not shrunk: {a}");
			Assert.True(c > 20, $"a below-neutral score must be pulled up, got {c}");
			Assert.Equal((90 - 50) / (70 - 50.0), (a - 50) / (b - 50), 3); // one common factor
		}

		[Fact]
		public void An_empirically_backed_score_is_not_shrunk()
		{
			var winrate = new FakeSource("winrate", 0.5, Scores(1, 90, games: 5000));
			var agg = new ScoreAggregator(new[] { winrate });

			Assert.Equal(90, agg.Score(1, NoDeck).Value, 3);
		}

		[Fact]
		public void Zero_weight_source_is_ignored()
		{
			var a = new FakeSource("A", 0.0, Scores(1, 10, games: 5000));
			var b = new FakeSource("B", 1.0, Scores(1, 60, games: 5000));
			var agg = new ScoreAggregator(new IArenaDataSource[] { a, b });

			var s = agg.Score(1, NoDeck);

			Assert.Equal(60, s.Value, 3);
			Assert.Single(s.Components);
		}
	}
}
