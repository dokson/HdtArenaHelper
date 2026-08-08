using System.Collections.Generic;
using Xunit;

namespace HdtArenaHelper.Training.Tests
{
	/// <summary>
	/// The serialization rule AGENTS.md calls load-bearing but nothing verified: a weight below the
	/// rounding floor is DROPPED from the json, and the runtime reads an absent weight as 0. That is
	/// what makes "removing a feature from the fit needs no runtime change" true, so it deserves a
	/// test rather than a paragraph.
	/// </summary>
	public class WeightsFileTests
	{
		private static SortedDictionary<string, double> Round(params (string Name, double Weight)[] rows)
		{
			var names = new List<string>();
			var raw = new double[rows.Length];
			for(var i = 0; i < rows.Length; i++)
			{
				names.Add(rows[i].Name);
				raw[i] = rows[i].Weight;
			}
			return WeightsFile.RoundWeights(names, raw);
		}

		[Fact]
		public void Weights_are_rounded_to_two_decimals()
		{
			var w = Round(("a", 1.2349), ("b", -2.005));
			Assert.Equal(1.23, w["a"], 6);
			Assert.Equal(-2.0, w["b"], 6);
		}

		[Fact]
		public void Weights_below_the_floor_are_omitted_not_zeroed()
		{
			// Omission is the contract, not a zero entry: the runtime treats a missing key as 0, so
			// writing 0.0 explicitly would say the same thing in more bytes — but a reader diffing
			// two fits must see the feature GONE, which is the signal that the fit dropped it.
			// NOTE 0.049 would round UP to 0.05 and survive — see the ordering test below. Use a
			// value that is still under the floor AFTER rounding.
			var w = Round(("kept", 0.05), ("dropped", 0.04), ("negDropped", -0.04), ("negKept", -0.05));

			Assert.True(w.ContainsKey("kept"));
			Assert.True(w.ContainsKey("negKept"));
			Assert.False(w.ContainsKey("dropped"));
			Assert.False(w.ContainsKey("negDropped"));
		}

		[Fact]
		public void The_floor_is_applied_after_rounding_not_before()
		{
			// 0.0449 rounds to 0.04 and is dropped; 0.0451 rounds to 0.05 and is kept. Testing the
			// order matters: checking the floor on the RAW value would keep a weight that then
			// serializes as 0.04, which the runtime would apply as a real (if tiny) coefficient.
			var w = Round(("justUnder", 0.0449), ("justOver", 0.0451));

			Assert.False(w.ContainsKey("justUnder"));
			Assert.Equal(0.05, w["justOver"], 6);
		}

		[Fact]
		public void Weights_are_ordered_deterministically()
		{
			// Ordinal sort, so two fits of the same data produce byte-identical json and a diff
			// shows only real weight changes — the property REPORT.md §2 relies on to claim the
			// refit is deterministic.
			var w = Round(("tx_summon", 1.0), ("attack", 1.0), ("Attack", 1.0), ("kw_forge", 1.0));
			Assert.Equal(new[] { "Attack", "attack", "kw_forge", "tx_summon" }, w.Keys);
		}

		/// <summary>
		/// The retrain PR's whole payload for a reviewer is a block of golden lines to paste, and for
		/// two releases it was pasting <c>[InlineData("LOOT_413", ...)]</c> at a test that had moved to
		/// named cards — a paste that does not compile, on the one PR the goldens exist for. So this
		/// asserts the printed form is real: every golden card has an <c>HSCard</c> accessor with that
		/// exact name. Reflection because that is the question — does the identifier EXIST — and this
		/// project compiles the generated pool into the test assembly, so the answer is checkable.
		/// </summary>
		[Fact]
		public void Every_golden_card_has_the_HSCard_accessor_the_trainer_prints()
		{
			var accessors = HSDatabaseGenerator.CardAccessorsById();

			foreach(var id in WeightsFile.GoldenCards)
			{
				Assert.True(accessors.TryGetValue(id, out var name), $"{id} has no HSCard accessor.");
				Assert.NotNull(typeof(HdtArenaHelper.CardDatabase.HSCard).GetProperty(name));
			}
		}
	}
}
