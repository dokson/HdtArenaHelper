using System.Collections.Generic;
using HearthDb.Enums;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The class win-rate estimator's re-centring rule. Pins the PROPERTIES that make the number
	/// honest — the pool lands on 50 and the between-class spread is untouched — not magic values,
	/// because the offset is a property of whatever payload was fetched.
	/// </summary>
	public class ScoreMathTests
	{
		private static Dictionary<CardClass, (double Wins, double Games)> Tallies(
			params (CardClass Cls, double Wins, double Games)[] rows)
		{
			var d = new Dictionary<CardClass, (double, double)>();
			foreach(var r in rows)
				d[r.Cls] = (r.Wins, r.Games);
			return d;
		}

		[Fact]
		public void Recentres_the_pool_onto_fifty()
		{
			// Pooled = 1100/2000 = 55%: the +5pp bias the games-weighting introduces.
			var rates = ScoreMath.RecentreClassWinRates(Tallies(
				(CardClass.MAGE, 600, 1000),
				(CardClass.DRUID, 500, 1000)));

			// Weighted mean of the re-centred rates must be exactly the neutral rate.
			var pooled = (rates[CardClass.MAGE] * 1000 + rates[CardClass.DRUID] * 1000) / 2000;
			Assert.Equal(ScoreMath.NeutralWinRate, pooled, 6);
			Assert.Equal(55.0, rates[CardClass.MAGE], 6); // 60 - 5
			Assert.Equal(45.0, rates[CardClass.DRUID], 6); // 50 - 5
		}

		[Fact]
		public void Preserves_the_spread_between_classes()
		{
			// Only an offset may be removed: rescaling would quietly invent or destroy the very
			// class-to-class differences the number exists to report.
			var rates = ScoreMath.RecentreClassWinRates(Tallies(
				(CardClass.MAGE, 620, 1000),
				(CardClass.DRUID, 480, 1000),
				(CardClass.ROGUE, 400, 1000)));

			Assert.Equal(62.0 - 48.0, rates[CardClass.MAGE] - rates[CardClass.DRUID], 6);
			Assert.Equal(48.0 - 40.0, rates[CardClass.DRUID] - rates[CardClass.ROGUE], 6);
		}

		[Fact]
		public void Weights_classes_by_their_own_sample_when_pooling()
		{
			// A class with 10x the games must dominate the pooled anchor: otherwise a tiny class
			// file (Warrior's sample is ~1/20th of Demon Hunter's on the live payload) would drag
			// every other class's displayed rate with it.
			var rates = ScoreMath.RecentreClassWinRates(Tallies(
				(CardClass.MAGE, 5500, 10000),
				(CardClass.WARRIOR, 100, 1000)));

			// Pooled = 5600/11000 = 50.909%, so the offset is small and Mage barely moves.
			Assert.Equal(55.0 - 0.909, rates[CardClass.MAGE], 2);
			Assert.Equal(10.0 - 0.909, rates[CardClass.WARRIOR], 2);
		}

		[Fact]
		public void Skips_classes_with_no_sample_and_survives_an_empty_pool()
		{
			var rates = ScoreMath.RecentreClassWinRates(Tallies(
				(CardClass.MAGE, 600, 1000),
				(CardClass.DRUID, 0, 0)));
			Assert.True(rates.ContainsKey(CardClass.MAGE));
			Assert.False(rates.ContainsKey(CardClass.DRUID));

			// No games at all: an empty map, never a divide-by-zero or a fabricated 50.
			Assert.Empty(ScoreMath.RecentreClassWinRates(Tallies((CardClass.MAGE, 0, 0))));
		}
	}
}
