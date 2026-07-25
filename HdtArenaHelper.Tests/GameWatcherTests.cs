using HearthDb.Enums;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// The arena-match gate. It exists because of a live bug (2026-07-25): with an arena run open, a
	/// Battlegrounds hero/trinket choice arrives on the GAMEPLAY scene through the same choice zone a
	/// Discover does, and got scored with arena win-rates over a Battlegrounds board.
	/// </summary>
	public class GameWatcherTests
	{
		[Theory]
		[InlineData(GameType.GT_ARENA)]
		[InlineData(GameType.GT_ARENA_PLAYER_VS_AI)]
		[InlineData(GameType.GT_UNDERGROUND_ARENA)]
		[InlineData(GameType.GT_UNDERGROUND_ARENA_PLAYER_VS_AI)]
		public void Arena_matches_pass(GameType gameType)
			=> Assert.True(GameWatcher.IsArenaGameType((int)gameType));

		[Theory]
		[InlineData(GameType.GT_BATTLEGROUNDS)]
		[InlineData(GameType.GT_BATTLEGROUNDS_DUO)]
		[InlineData(GameType.GT_BATTLEGROUNDS_FRIENDLY)]
		[InlineData(GameType.GT_BATTLEGROUNDS_PLAYER_VS_AI)]
		[InlineData(GameType.GT_RANKED)]
		[InlineData(GameType.GT_CASUAL)]
		[InlineData(GameType.GT_TAVERNBRAWL)]
		[InlineData(GameType.GT_VS_FRIEND)]
		[InlineData(GameType.GT_MERCENARIES_PVP)]
		public void Other_modes_are_blocked(GameType gameType)
			=> Assert.False(GameWatcher.IsArenaGameType((int)gameType));

		[Fact]
		public void Unknown_passes_because_the_client_reports_it_while_a_game_starts()
		{
			// Permissive on purpose, and only for the states that carry no information: the mulligan
			// screen appears in exactly that window, so blocking GT_UNKNOWN would cost that feature its
			// whole window. Battlegrounds does NOT hide behind it — it states its own type.
			Assert.True(GameWatcher.IsArenaGameType((int)GameType.GT_UNKNOWN));
		}

		[Fact]
		public void An_id_HearthDb_does_not_know_is_not_arena()
		{
			// A mode nobody here has seen is not arena until someone decides it is: the cost of being
			// wrong the other way is win-rates painted over a game they were not measured in.
			Assert.False(GameWatcher.IsArenaGameType(9999));
		}
	}
}
