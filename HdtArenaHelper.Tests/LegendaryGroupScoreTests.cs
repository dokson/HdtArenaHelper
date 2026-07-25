using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HearthDb.Enums;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The legendary-group pick: a bomb plus three package cards, scored as one number. Two things
	/// need pinning here — the tilt toward the best card (a deliberate scoring rule, not an average)
	/// and the PROVENANCE of the synthesized component, which forgot where its number came from and
	/// made the overlay claim it had no win-rate data over three scored groups.
	/// </summary>
	public class LegendaryGroupScoreTests
	{
		private sealed class Source : IArenaDataSource
		{
			private readonly Dictionary<int, SourceScore?> _scores;
			public Source(string name, bool hasSamples, Dictionary<int, SourceScore?> scores)
			{
				Name = name;
				HasSamples = hasSamples;
				_scores = scores;
			}
			public string Name { get; }
			public double Weight => 1.0;
			public bool IsLoaded => true;
			public bool HasSamples { get; }
			public Task EnsureLoadedAsync() => Task.CompletedTask;
			public SourceScore? GetNormalizedScore(int dbfId, CardClass draftClass = CardClass.INVALID)
				=> _scores.TryGetValue(dbfId, out var v) ? v : null;
		}

		private static readonly int[] NoDeck = new int[0];

		// Group ids are arbitrary here: the aggregator only looks them up in the fake source, and no
		// synergy engine is attached, so nothing touches HearthDb.
		private const int Legendary = 1;
		private static readonly int[] Package = { 2, 3, 4 };

		private static ScoreAggregator WinRateAggregator(params (int Dbf, double Score)[] cards)
			=> new ScoreAggregator(new IArenaDataSource[]
			{
				new Source("HSReplay", hasSamples: true,
					cards.ToDictionary(c => c.Dbf, c => (SourceScore?)new SourceScore(c.Score, 5000))),
			});

		private static BlendedScore Score(ScoreAggregator agg)
			=> LegendaryGroupScore.Score(agg, Legendary, Package, NoDeck, CardClass.MAGE);

		[Fact]
		public void A_bomb_with_filler_beats_four_average_cards_of_the_same_mean()
		{
			// The rule the tilt exists for: both groups add the same average quality, but only one
			// gives you a card the remaining ~29 picks cannot replace. A plain mean would call these
			// identical, which inverts how the pick actually plays.
			var bomb = Score(WinRateAggregator((1, 95), (2, 45), (3, 45), (4, 45)));
			var flat = Score(WinRateAggregator((1, 57.5), (2, 57.5), (3, 57.5), (4, 57.5)));

			Assert.Equal(57.5, flat.Value, 6); // no spread, so the tilt cannot move it
			Assert.True(bomb.Value > flat.Value);
		}

		[Fact]
		public void The_score_sits_between_the_mean_and_the_best_card()
		{
			// Bounded on both sides: it must not become "just the best card" (the filler is real
			// value) nor collapse to the mean (which is the behaviour being corrected).
			var s = Score(WinRateAggregator((1, 90), (2, 50), (3, 50), (4, 50)));
			var mean = (90 + 50 + 50 + 50) / 4.0;

			Assert.True(s.Value > mean);
			Assert.True(s.Value < 90);
			Assert.Equal(mean + LegendaryGroupScore.BestCardTilt * (90 - mean), s.Value, 6);
		}

		[Fact]
		public void A_group_scored_from_win_rate_data_is_not_flagged_as_unmeasured()
		{
			// The bug this file exists for: the synthesized component must carry the provenance of
			// the cards behind it, or the overlay prints "win-rate data unavailable" over a score it
			// just derived from thousands of games.
			var s = Score(WinRateAggregator((1, 80), (2, 60), (3, 60), (4, 60)));

			Assert.True(s.HasData);
			Assert.True(s.HasWinRateData);
			Assert.False(s.IsLowConfidence);
			Assert.Equal(5000, s.MaxGames);
		}

		[Fact]
		public void A_group_scored_only_by_the_model_stays_flagged()
		{
			// The other direction, so the fix cannot bless model-only groups as measured.
			var model = new ScoreAggregator(new IArenaDataSource[]
			{
				new Source("Heuristic", hasSamples: false, new Dictionary<int, SourceScore?>
				{
					{ 1, new SourceScore(80) }, { 2, new SourceScore(60) },
					{ 3, new SourceScore(60) }, { 4, new SourceScore(60) },
				}),
			});

			var s = Score(model);

			Assert.True(s.HasData);
			Assert.False(s.HasWinRateData);
			Assert.True(s.IsLowConfidence);
			Assert.Null(s.MaxGames);
		}

	}
}
