using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HdtArenaHelper.Tests
{
	public class SelfUpdaterTests
	{
		[Theory]
		[InlineData("v0.1.2", 0, 1, 2)]
		[InlineData("0.1.2", 0, 1, 2)]
		[InlineData("V2.0.0", 2, 0, 0)]
		[InlineData(" v1.4 ", 1, 4, -1)] // 2-part tag: Build unset (-1) until normalized
		public void ParseVersion_accepts_common_tag_shapes(string tag, int major, int minor, int build)
		{
			var v = SelfUpdater.ParseVersion(tag);
			Assert.NotNull(v);
			Assert.Equal(major, v!.Major);
			Assert.Equal(minor, v.Minor);
			Assert.Equal(build, v.Build);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		[InlineData("latest")]
		[InlineData("v")]
		public void ParseVersion_rejects_garbage(string? tag)
		{
			Assert.Null(SelfUpdater.ParseVersion(tag));
		}

		[Fact]
		public void IsNewer_true_only_when_release_exceeds_installed()
		{
			var installed = new Version(0, 1, 1);
			Assert.True(SelfUpdater.IsNewer(installed, new Version(0, 1, 2)));
			Assert.True(SelfUpdater.IsNewer(installed, new Version(0, 2, 0)));
			Assert.True(SelfUpdater.IsNewer(installed, new Version(1, 0, 0)));

			Assert.False(SelfUpdater.IsNewer(installed, new Version(0, 1, 1)));
			Assert.False(SelfUpdater.IsNewer(installed, new Version(0, 1, 0)));
			Assert.False(SelfUpdater.IsNewer(installed, new Version(0, 0, 9)));
		}

		[Fact]
		public void A_four_part_tag_would_never_install_which_is_why_ci_forbids_them()
		{
			// Documents the constraint rather than working around it: release tags are 3-part by
			// rule (build.yml rejects anything else), because a v0.1.2.1 hotfix would compare as
			// "not newer" than an installed 0.1.2 and would reach nobody. The updater is the
			// recovery path for a bad release, so the rule matters more than the comparison.
			Assert.False(SelfUpdater.IsNewer(new Version(0, 1, 2), new Version(0, 1, 2, 1)));
		}

		[Fact]
		public void IsNewer_ignores_the_revision_component()
		{
			// The plugin reports a 3-part version (Revision unset = -1); a 4-part tag equal on
			// major/minor/build must NOT read as newer just because its Revision is 0 > -1.
			var installed = new Version(0, 1, 1); // Revision -1
			Assert.False(SelfUpdater.IsNewer(installed, new Version(0, 1, 1, 0)));
			Assert.False(SelfUpdater.IsNewer(installed, new Version(0, 1, 1, 5)));
		}

		private const string RealAssetUrl =
			"https://github.com/dokson/HdtArenaHelper/releases/download/v0.1.2/HdtArenaHelper.dll";

		[Fact]
		public void SelectDllAssetUrl_finds_the_bare_dll_by_exact_name()
		{
			var release = JObject.Parse(@"{
				""tag_name"": ""v0.1.2"",
				""assets"": [
					{ ""name"": ""HdtArenaHelper-0.1.2.zip"", ""browser_download_url"": ""https://github.com/dokson/HdtArenaHelper/releases/download/v0.1.2/x.zip"" },
					{ ""name"": ""HdtArenaHelper.dll"",       ""browser_download_url"": """ + RealAssetUrl + @""" }
				]
			}");
			Assert.Equal(RealAssetUrl, SelfUpdater.SelectDllAssetUrl(release));
		}

		[Fact]
		public void SelectDllAssetUrl_null_when_only_the_zip_is_attached()
		{
			var release = JObject.Parse(@"{
				""assets"": [
					{ ""name"": ""HdtArenaHelper-0.1.2.zip"", ""browser_download_url"": ""https://github.com/dokson/HdtArenaHelper/releases/download/v0.1.2/x.zip"" }
				]
			}");
			Assert.Null(SelfUpdater.SelectDllAssetUrl(release));
		}

		[Theory]
		[InlineData("https://github.com/dokson/HdtArenaHelper/releases/download/v1/HdtArenaHelper.dll", true)]
		[InlineData("https://github.com/attacker/HdtArenaHelper/releases/download/v1/HdtArenaHelper.dll", false)] // another repo
		[InlineData("https://github.com/dokson/HdtArenaHelper/issues/1", false)]                                  // not a release path
		[InlineData("https://objects.githubusercontent.com/foo", true)]
		[InlineData("https://release-assets.githubusercontent.com/foo", true)]
		[InlineData("http://github.com/dokson/x.dll", false)]           // plaintext
		[InlineData("https://github.com.evil.test/dokson/x.dll", false)] // prefix look-alike
		[InlineData("https://evil.test/HdtArenaHelper.dll", false)]
		[InlineData("file:///C:/tmp/HdtArenaHelper.dll", false)]
		public void Only_github_https_asset_hosts_are_trusted(string url, bool trusted)
		{
			// These bytes get executed on the next launch, so the host is the one thing the
			// updater is strict about — an asset URL from anywhere else is refused outright.
			Assert.Equal(trusted, SelfUpdater.IsTrustedAssetUrl(url));
		}

		[Fact]
		public void SelectDllAssetUrl_refuses_an_off_host_asset_url()
		{
			var release = JObject.Parse(@"{
				""assets"": [
					{ ""name"": ""HdtArenaHelper.dll"", ""browser_download_url"": ""https://evil.test/x.dll"" }
				]
			}");
			Assert.Null(SelfUpdater.SelectDllAssetUrl(release));
		}

		[Fact]
		public void SelectDllAssetUrl_null_when_no_assets()
		{
			Assert.Null(SelfUpdater.SelectDllAssetUrl(JObject.Parse(@"{ ""tag_name"": ""v0.1.2"" }")));
		}
	}

	/// <summary>
	/// The two-phase update on unlocked temp files: <c>StageBytes</c> only PARKS a validated
	/// download, and <c>ApplyPendingUpdate</c> performs the rename swap at the next load. Lock
	/// semantics can't be simulated here (nothing loads these files), but the payload validation,
	/// the swap and the rollback lifecycle are pinned offline.
	///
	/// Payloads are the REAL plugin assembly: the updater now checks the managed assembly identity
	/// of what it downloaded, so a fabricated "MZ..." blob is (correctly) refused.
	/// </summary>
	public class SelfUpdaterStagingTests : IDisposable
	{
		private readonly string _dir = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(), "HdtArenaHelperTests", Guid.NewGuid().ToString("N"));
		private readonly string _dll;
		private readonly SelfUpdater _updater;

		private static readonly byte[] RealPlugin =
			System.IO.File.ReadAllBytes(typeof(ScoreAggregator).Assembly.Location);
		private static readonly byte[] WrongAssembly =
			System.IO.File.ReadAllBytes(typeof(HearthDb.Cards).Assembly.Location);

		public SelfUpdaterStagingTests()
		{
			System.IO.Directory.CreateDirectory(_dir);
			_dll = System.IO.Path.Combine(_dir, "HdtArenaHelper.dll");
			_updater = new SelfUpdater(_dll, _dir);
		}

		public void Dispose()
		{
			// Nothing locks these temp files (no assembly is ever loaded from them),
			// so a plain delete is safe — and keeps slopwatch's no-empty-catch gate honest.
			if(System.IO.Directory.Exists(_dir))
				System.IO.Directory.Delete(_dir, recursive: true);
		}

		/// <summary>A marker byte appended past the PE image so payloads stay distinguishable
		/// while remaining loadable assemblies.</summary>
		private static byte[] Marked(byte[] assembly, byte marker)
		{
			var data = new byte[assembly.Length + 1];
			Array.Copy(assembly, data, assembly.Length);
			data[assembly.Length] = marker;
			return data;
		}

		private static byte MarkerOf(string path)
		{
			var bytes = System.IO.File.ReadAllBytes(path);
			return bytes[bytes.Length - 1];
		}

		[Fact]
		public void Staging_a_download_does_not_touch_the_installed_dll()
		{
			// The whole point of the split: the live DLL must be untouched until the next load, so
			// process death right after a download can never leave the folder without a plugin.
			System.IO.File.WriteAllBytes(_dll, Marked(RealPlugin, 1));

			Assert.True(_updater.StageBytes(Marked(RealPlugin, 2)));

			Assert.Equal(1, MarkerOf(_dll));                                  // still the old one
			Assert.True(System.IO.File.Exists(_dll + ".new"));                // parked
			Assert.False(System.IO.File.Exists(_dll + ".old"));               // nothing renamed yet
		}

		[Fact]
		public void The_swap_happens_on_apply_and_keeps_the_previous_version()
		{
			System.IO.File.WriteAllBytes(_dll, Marked(RealPlugin, 1));
			Assert.True(_updater.StageBytes(Marked(RealPlugin, 2)));

			Assert.Equal(UpdateOutcome.Staged, _updater.ApplyPendingUpdate());

			Assert.Equal(2, MarkerOf(_dll));                                  // new version live
			Assert.Equal(1, MarkerOf(_dll + ".old"));                         // rollback kept
			Assert.False(System.IO.File.Exists(_dll + ".new"));               // no leftover
			Assert.False(System.IO.File.Exists(_dll + ".old.prev"));          // no leftover
		}

		[Fact]
		public void Apply_is_a_no_op_without_a_staged_download()
		{
			System.IO.File.WriteAllBytes(_dll, Marked(RealPlugin, 1));

			Assert.Equal(UpdateOutcome.UpToDate, _updater.ApplyPendingUpdate());

			Assert.Equal(1, MarkerOf(_dll));
		}

		[Fact]
		public void Rejects_payloads_that_are_not_a_pe_image()
		{
			System.IO.File.WriteAllBytes(_dll, Marked(RealPlugin, 1));

			Assert.False(_updater.StageBytes(System.Text.Encoding.UTF8.GetBytes(
				new string('x', 2048)))); // 2 KB of junk: passes the size floor, fails the header

			Assert.Equal(1, MarkerOf(_dll));
			Assert.False(System.IO.File.Exists(_dll + ".new"));
		}

		[Fact]
		public void Rejects_a_valid_assembly_that_is_not_this_plugin()
		{
			// The MZ check alone would accept any PE — a native DLL, a Debug build, another
			// project's output. Installing one of those means HDT cannot load the plugin at all,
			// with no in-process way back, so identity is checked before the swap.
			System.IO.File.WriteAllBytes(_dll, Marked(RealPlugin, 1));

			Assert.False(_updater.StageBytes(WrongAssembly));

			Assert.Equal(1, MarkerOf(_dll));
			Assert.False(System.IO.File.Exists(_dll + ".new"));
		}

		[Fact]
		public void A_second_update_replaces_the_rollback_backup()
		{
			System.IO.File.WriteAllBytes(_dll, Marked(RealPlugin, 1));
			_updater.StageBytes(Marked(RealPlugin, 2));
			_updater.ApplyPendingUpdate();
			_updater.StageBytes(Marked(RealPlugin, 3));
			_updater.ApplyPendingUpdate();

			Assert.Equal(3, MarkerOf(_dll));
			Assert.Equal(2, MarkerOf(_dll + ".old"));
		}

		[Fact]
		public void Apply_promotes_a_prior_backup_when_the_rollback_is_missing()
		{
			// A swap interrupted between the two moves leaves the genuine previous version only
			// under *.old.prev. Deleting it would destroy the documented manual rollback.
			System.IO.File.WriteAllBytes(_dll, Marked(RealPlugin, 5));
			System.IO.File.WriteAllBytes(_dll + ".old.prev", Marked(RealPlugin, 4));

			_updater.ApplyPendingUpdate();

			Assert.Equal(4, MarkerOf(_dll + ".old"));                         // promoted, not deleted
			Assert.False(System.IO.File.Exists(_dll + ".old.prev"));
		}

		[Fact]
		public void Apply_discards_a_staged_download_that_fails_validation()
		{
			System.IO.File.WriteAllBytes(_dll, Marked(RealPlugin, 1));
			System.IO.File.WriteAllBytes(_dll + ".new", WrongAssembly); // e.g. a tampered leftover

			Assert.Equal(UpdateOutcome.UpToDate, _updater.ApplyPendingUpdate());

			Assert.Equal(1, MarkerOf(_dll));
			Assert.False(System.IO.File.Exists(_dll + ".new"));
		}
	}
}
