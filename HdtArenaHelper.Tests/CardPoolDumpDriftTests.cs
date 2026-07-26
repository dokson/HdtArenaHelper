using System;
using System.IO;
using HdtArenaHelper.Training;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// Checks that <c>docs/CardDatabase.md</c> and <c>Generated/CardDatabase.g.cs</c> still match
	/// what <see cref="CardPoolDump.Build"/> produces from the HearthDb this project already
	/// references — i.e. that nobody forgot to re-run
	/// <c>dotnet run --project HdtArenaHelper.Training -- --dump-cards</c> after a HearthDb bump,
	/// and that <c>card-database.yml</c> is actually keeping the committed files current. Compares
	/// against <see cref="CardPoolDump.Build"/>'s own output rather than a second, hand-written
	/// projection: a reimplementation here is exactly the mistake that let a regression test pass
	/// either way in 0.1.6 (see AGENTS.md's synergy-engine section).
	/// </summary>
	public class CardPoolDumpDriftTests
	{
		[Fact]
		public void Committed_files_match_the_live_HearthDb_projection()
		{
			var repoRoot = FindRepoRoot();
			var (markdown, csSource, _, _, _) = CardPoolDump.Build();

			var committedMd = File.ReadAllText(Path.Combine(repoRoot, "docs", "CardDatabase.md"));
			var committedCs = File.ReadAllText(Path.Combine(repoRoot, "Generated", "CardDatabase.g.cs"));

			Assert.True(markdown == committedMd, Stale("docs/CardDatabase.md", markdown, committedMd));
			Assert.True(csSource == committedCs, Stale("Generated/CardDatabase.g.cs", csSource, committedCs));
		}

		/// <summary>
		/// The failure message. Reports the FIRST differing line, not the diff: these files are over
		/// a megabyte each, and "is stale" alone leaves the reader to diff them by hand to find out
		/// whether a patch moved one card or the generator changed every row.
		/// </summary>
		private static string Stale(string file, string current, string committed)
		{
			var expected = current.Split('\n');
			var actual = committed.Split('\n');
			var where = $"{actual.Length} committed lines against {expected.Length} current";

			for(var i = 0; i < Math.Min(expected.Length, actual.Length); i++)
			{
				if(!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
				{
					where = $"first difference on line {i + 1}:"
						+ $"{Environment.NewLine}  committed: {Truncate(actual[i])}"
						+ $"{Environment.NewLine}  current:   {Truncate(expected[i])}";
					break;
				}
			}

			return $"{file} is stale — re-run `dotnet run --project HdtArenaHelper.Training -- --dump-cards`."
				+ $"{Environment.NewLine}{where}";
		}

		private static string Truncate(string line) =>
			line.Length <= 200 ? line : line.Substring(0, 200) + "…";

		private static string FindRepoRoot()
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while(dir != null && !File.Exists(Path.Combine(dir.FullName, "HdtArenaHelper.sln")))
				dir = dir.Parent;
			if(dir == null)
				throw new DirectoryNotFoundException("could not locate repo root (HdtArenaHelper.sln).");
			return dir.FullName;
		}
	}
}
