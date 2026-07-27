using System;
using System.IO;
using HdtArenaHelper.Training;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// Checks that <c>docs/HSDatabase.md</c> and <c>Generated/HSDatabase.g.cs</c> still match
	/// what <see cref="HSDatabaseGenerator.Build"/> produces from the HearthDb this project already
	/// references — i.e. that nobody forgot to re-run
	/// <c>dotnet run --project HdtArenaHelper.Training -- --dump-database</c> after a HearthDb bump,
	/// and that <c>card-database.yml</c> is actually keeping the committed files current. Compares
	/// against <see cref="HSDatabaseGenerator.Build"/>'s own output rather than a second,
	/// hand-written projection: a reimplementation here is exactly the mistake that let a regression
	/// test pass either way in 0.1.6 (see AGENTS.md's synergy-engine section).
	/// </summary>
	public class HSDatabaseDriftTests
	{
		[Fact]
		public void Committed_files_match_the_live_HearthDb_projection()
		{
			var repoRoot = FindRepoRoot();
			var (files, _, _, _) = HSDatabaseGenerator.Build();

			// Iterating whatever the generator says it writes, rather than naming the files here: a
			// list this test does not know about is a file nothing checks, which is the exact failure
			// the whole drift test exists to prevent.
			Assert.NotEmpty(files);
			foreach(var file in files)
			{
				var path = Path.Combine(repoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
				Assert.True(File.Exists(path), $"{file.Path} is missing — re-run `--dump-database`.");

				var committed = File.ReadAllText(path);
				Assert.True(file.Content == committed, Stale(file.Path, file.Content, committed));
			}
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

			return $"{file} is stale — re-run `dotnet run --project HdtArenaHelper.Training -- --dump-database`."
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
