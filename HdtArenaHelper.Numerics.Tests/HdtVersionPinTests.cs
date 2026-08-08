using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace HdtArenaHelper.Numerics.Tests
{
	/// <summary>
	/// The HDT reference-assembly pin lives in ONE file, <c>hdt-version.txt</c>. It used to be a
	/// literal in each of the four workflows plus a default in <c>resolve-hdt.ps1</c> — five copies
	/// of a number that must agree, and they stopped agreeing: the canary compared against a pin
	/// that three other files had already moved past. These tests are what makes "one place" a
	/// property rather than an intention.
	///
	/// Here rather than in HdtArenaHelper.Tests because they read repo FILES and need no HearthDb,
	/// which is the rule AGENTS.md states for where a test belongs.
	/// </summary>
	public class HdtVersionPinTests
	{
		private static readonly string RepoRoot = FindRepoRoot();

		[Fact]
		public void The_pin_file_holds_a_plain_version()
		{
			var pin = File.ReadAllText(Path.Combine(RepoRoot, "hdt-version.txt")).Trim();

			// Shape only, never the value: the pin moves with every HDT release, and asserting the
			// number would fail on data rather than on a defect — the rule the committed pool's
			// tests follow for the same reason.
			Assert.Matches(@"^\d+\.\d+\.\d+$", pin);
		}

		[Fact]
		public void The_pin_file_is_a_single_line()
		{
			// Every consumer trims and compares as a string — the workflows against the latest
			// release tag, resolve-hdt.ps1 against its cache stamp. A stray second line would make
			// those comparisons fail in a way that reads as "a new HDT is out".
			var raw = File.ReadAllText(Path.Combine(RepoRoot, "hdt-version.txt"));

			Assert.Single(raw.Trim().Split('\n'));
		}

		[Fact]
		public void No_workflow_hard_codes_an_HDT_version()
		{
			var workflows = Directory.GetFiles(Path.Combine(RepoRoot, ".github", "workflows"), "*.yml");
			Assert.NotEmpty(workflows);

			foreach(var path in workflows)
			{
				var text = File.ReadAllText(path);
				// A quoted three-part version next to the word HDT is the shape the old
				// `HDT_VERSION: '1.53.14'` had. Matching the WORD as well keeps the plugin's own
				// version and an action's `@v7` out of it.
				var match = Regex.Match(text, @"HDT[A-Z_]*:\s*'\d+\.\d+\.\d+'");
				Assert.False(match.Success,
					$"{Path.GetFileName(path)} pins HDT inline ({match.Value}) — the pin lives in hdt-version.txt.");
			}
		}

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
