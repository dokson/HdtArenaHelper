using System.Collections.Generic;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class BlendedScoreTests
	{
		[Fact]
		public void Empty_has_no_data_and_a_zero_value()
		{
			var empty = BlendedScore.Empty;

			Assert.False(empty.HasData);
			Assert.Equal(0, empty.Value);
			Assert.Empty(empty.Components);
			Assert.Equal(0, empty.SynergyBonus);
		}

		[Fact]
		public void HasData_is_true_once_a_component_is_present()
		{
			var score = new BlendedScore(
				value: 62,
				components: new List<ScoreComponent> { new ScoreComponent("HSReplay Arena", 62, 1.0) },
				synergyBonus: 0);

			Assert.True(score.HasData);
			Assert.Equal(62, score.Value);
		}

		[Fact]
		public void Preserves_components_and_synergy_bonus()
		{
			var components = new List<ScoreComponent>
			{
				new ScoreComponent("HSReplay Arena", 70, 1.0),
				new ScoreComponent("Heuristic", 55, 0.5),
			};
			var score = new BlendedScore(value: 68, components, synergyBonus: 3.5);

			Assert.Equal(2, score.Components.Count);
			Assert.Equal("Heuristic", score.Components[1].SourceName);
			Assert.Equal(55, score.Components[1].NormalizedScore);
			Assert.Equal(0.5, score.Components[1].Weight);
			Assert.Equal(3.5, score.SynergyBonus);
		}
	}
}
