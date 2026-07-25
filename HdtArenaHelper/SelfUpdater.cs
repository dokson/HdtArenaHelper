using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Newtonsoft.Json.Linq;

namespace HdtArenaHelper
{
	/// <summary>What a self-update check ended up doing, for the overlay/menu to report.</summary>
	public enum UpdateOutcome
	{
		UpToDate,
		/// <summary>A newer DLL was downloaded and swapped in; it applies on the next HDT restart.</summary>
		Staged,
		/// <summary>A newer version exists but couldn't be staged automatically (no DLL asset,
		/// unknown install path, or the swap was refused) — point the user at the releases page.</summary>
		ManualAvailable,
		Failed,
	}

	public readonly struct UpdateCheckResult
	{
		public UpdateOutcome Outcome { get; }
		public Version? Latest { get; }
		public string ReleasesPage { get; }

		public UpdateCheckResult(UpdateOutcome outcome, string releasesPage, Version? latest = null)
		{
			Outcome = outcome;
			ReleasesPage = releasesPage;
			Latest = latest;
		}
	}

	/// <summary>
	/// Self-update over the project's public GitHub Releases. When a newer release exists it
	/// downloads the bundled <c>HdtArenaHelper.dll</c> asset and stages it via a rename swap:
	/// the running DLL is locked against overwrite/delete while HDT is up, but NTFS still lets
	/// us RENAME it (verified), so we move it aside to <c>*.dll.old</c> and drop the new one in
	/// its place — HDT loads only files whose extension is exactly ".dll", so the backup is
	/// ignored and the update takes effect on the next restart. No external updater process.
	///
	/// Unlike the HSReplay endpoint, <c>api.github.com</c> is a plain TLS host (not
	/// Cloudflare-fingerprint gated), so a <see cref="WebClient"/> with a User-Agent works —
	/// no curl workaround needed here. Everything fails soft: a network/parse/IO error degrades
	/// to <see cref="UpdateOutcome.ManualAvailable"/> or <see cref="UpdateOutcome.Failed"/>, so
	/// the plugin can always fall back to pointing the user at the releases page by hand.
	/// </summary>
	public sealed class SelfUpdater
	{
		private const string Owner = "dokson";
		private const string Repo = "HdtArenaHelper";
		private const string LatestReleaseApi =
			"https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
		public const string ReleasesPage =
			"https://github.com/" + Owner + "/" + Repo + "/releases/latest";

		/// <summary>The release asset that is the bare plugin DLL (attached by build.yml).</summary>
		internal const string DllAssetName = "HdtArenaHelper.dll";

		private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

		/// <summary>The assembly the release asset must actually contain.</summary>
		private const string ExpectedAssemblyName = "HdtArenaHelper";
		/// <summary>Sanity cap on the download; the plugin is ~100 KB.</summary>
		private const int MaxAssemblyBytes = 16 * 1024 * 1024;
		private const int RestoreAttempts = 5;
		private const int RestoreRetryDelayMs = 200;
		private const string UserAgent = "HdtArenaHelper-updater";

		private readonly string _dllPath;   // the running, locked plugin DLL
		private readonly string _stampFile;  // last-check timestamp, to throttle the GitHub API

		public SelfUpdater(string dllPath, string cacheDir)
		{
			_dllPath = dllPath ?? "";
			_stampFile = Path.Combine(cacheDir, "update_check.stamp");
		}

		private string BackupPath => _dllPath + ".old";
		private string StagedPath => _dllPath + ".new";

		private string PriorBackupPath => BackupPath + ".prev";
		private string LockPath => _dllPath + ".lock";

		/// <summary>
		/// Exclusive guard over the staged file and the swap, so two HDT instances sharing this
		/// plugin folder cannot interleave on the same paths. Returns null when someone else holds
		/// it — a skipped update is a non-event, a mangled one is not. The lock file is left in
		/// place deliberately: deleting it would reintroduce a create/delete race between
		/// instances, and HDT ignores it (it loads only files whose extension is exactly ".dll").
		/// Catches broadly: on a permissions or path failure the answer is "skip", never "throw"
		/// — a throw here would abort self-update initialisation entirely for that user.
		/// </summary>
		private FileStream? TryLock()
		{
			try
			{
				return new FileStream(LockPath, FileMode.OpenOrCreate,
					FileAccess.ReadWrite, FileShare.None);
			}
			catch(Exception ex)
			{
				LogUpdate("could not take the update lock: " + ex.Message);
				return null;
			}
		}

		/// <summary>
		/// Applies a download from a previous session, then tidies up. Call once from OnLoad.
		///
		/// The swap deliberately happens HERE and not when the download finishes. A download
		/// completes at an arbitrary moment — very often seconds before the user closes HDT,
		/// since the check starts at load — and the rename dance has a window between "old DLL
		/// moved aside" and "new DLL in place" where process death leaves the folder with NO
		/// .dll at all. HDT loads only exact ".dll" files, so that state is unrecoverable: no
		/// plugin code ever runs again to repair it. At OnLoad the process has a whole session
		/// ahead of it instead of milliseconds, which is the narrowest window available to us.
		///
		/// The previous version's <c>*.dll.old</c> is KEPT as the manual rollback (HDT ignores
		/// it) and is replaced on the next successful update.
		/// </summary>
		public UpdateOutcome ApplyPendingUpdate()
		{
			// The in-process guards (_updatePending, Interlocked) say nothing about a SECOND HDT
			// instance sharing this plugin folder. Two of them interleaving on the same three paths
			// scrambles the rollback files, so hold an exclusive lock file across the whole dance
			// and skip entirely if someone else has it — a missed update is a non-event, a mangled
			// rollback is not.
			if(string.IsNullOrEmpty(_dllPath))
				return UpdateOutcome.UpToDate;

			using(var guard = TryLock())
			{
				if(guard == null)
				{
					LogUpdate("another instance is applying an update; skipping");
					return UpdateOutcome.UpToDate;
				}
				return ApplyPendingUpdateLocked();
			}
		}

		private UpdateOutcome ApplyPendingUpdateLocked()
		{
			// A swap interrupted between the two moves can leave the real previous version only
			// under *.prev. Promote it rather than deleting it, or the documented rollback is lost.
			try
			{
				if(!File.Exists(BackupPath) && File.Exists(PriorBackupPath))
					TryMove(PriorBackupPath, BackupPath);
			}
			catch(Exception ex)
			{
				LogUpdate("could not promote the prior rollback: " + ex.Message);
			}

			var applied = UpdateOutcome.UpToDate;
			if(File.Exists(StagedPath))
			{
				byte[]? staged = null;
				try { staged = File.ReadAllBytes(StagedPath); }
				catch(Exception ex) { LogUpdate("could not read the staged download: " + ex.Message); }

				if(staged != null && IsPluginAssembly(StagedPath))
					applied = SwapIn(staged) ? UpdateOutcome.Staged : UpdateOutcome.ManualAvailable;
				else
					LogUpdate("discarding a staged download that failed validation");
			}

			TryDelete(StagedPath);
			TryDelete(PriorBackupPath);
			return applied;
		}

		/// <summary>True when the last check was more than the interval ago (or never).</summary>
		public bool DueForCheck()
		{
			try
			{
				if(!File.Exists(_stampFile))
					return true;
				return DateTime.UtcNow - File.GetLastWriteTimeUtc(_stampFile) > CheckInterval;
			}
			catch(Exception ex)
			{
				LogUpdate("check-stamp read failed: " + ex.Message);
				return true;
			}
		}

		/// <summary>
		/// Checks the latest release; if newer than <paramref name="current"/>, downloads the
		/// DLL asset and stages it for the next restart. Never throws.
		/// </summary>
		public async Task<UpdateCheckResult> CheckAndStageAsync(Version current,
			CancellationToken cancellationToken = default)
		{
			try
			{
				// GitHub requires TLS 1.2; net472's default may still be SSL3/TLS1 on old OSes.
				ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

				// Stamp before the request, not after: on failure (offline, rate-limited, corporate
				// NAT) the old code never stamped and so re-checked on every launch forever.
				StampChecked();

				string json;
				using(var wc = new WebClient())
				using(cancellationToken.Register(wc.CancelAsync))
				{
					wc.Headers.Add(HttpRequestHeader.UserAgent, UserAgent);
					wc.Headers.Add(HttpRequestHeader.Accept, "application/vnd.github+json");
					json = await wc.DownloadStringTaskAsync(LatestReleaseApi).ConfigureAwait(false);
				}
				cancellationToken.ThrowIfCancellationRequested();

				var release = JObject.Parse(json);
				var tag = (string?)release["tag_name"];
				var latest = ParseVersion(tag);
				if(latest == null)
				{
					// A tag we cannot read is not an error the user can act on — releases are
					// 3-part by CI rule (build.yml enforces it), so anything else is either a
					// pre-release or a mistake upstream. Report up to date rather than showing
					// everyone "update check failed" until the next tag lands.
					LogUpdate($"ignoring unparsable release tag '{tag}'");
					return new UpdateCheckResult(UpdateOutcome.UpToDate, ReleasesPage);
				}

				if(!IsNewer(current, latest))
				{
					LogUpdate($"up to date (installed {current}, latest {latest})");
					return new UpdateCheckResult(UpdateOutcome.UpToDate, ReleasesPage, latest);
				}

				LogUpdate($"newer release available: {current} -> {latest}");
				var dllUrl = SelectDllAssetUrl(release);
				if(dllUrl == null || string.IsNullOrEmpty(_dllPath) || !File.Exists(_dllPath))
				{
					LogUpdate("no DLL asset or unknown install path; offering manual update");
					return new UpdateCheckResult(UpdateOutcome.ManualAvailable, ReleasesPage, latest);
				}

				var staged = await TryStageAsync(dllUrl, current, cancellationToken).ConfigureAwait(false);
				return new UpdateCheckResult(
					staged ? UpdateOutcome.Staged : UpdateOutcome.ManualAvailable, ReleasesPage, latest);
			}
			catch(OperationCanceledException)
			{
				// The plugin was disabled/unloaded mid-check: abandon it without touching the DLL.
				LogUpdate("check cancelled");
				return new UpdateCheckResult(UpdateOutcome.Failed, ReleasesPage);
			}
			catch(Exception ex)
			{
				LogUpdate("check failed: " + ex.Message);
				return new UpdateCheckResult(UpdateOutcome.Failed, ReleasesPage);
			}
		}

		private async Task<bool> TryStageAsync(string dllUrl, Version current, CancellationToken cancellationToken)
		{
			try
			{
				byte[] data;
				using(var wc = new WebClient())
				using(cancellationToken.Register(wc.CancelAsync))
				{
					wc.Headers.Add(HttpRequestHeader.UserAgent, UserAgent);
					data = await wc.DownloadDataTaskAsync(dllUrl).ConfigureAwait(false);
				}
				// Never start the rename dance after an unload: a swap half-done against a DLL
				// nobody is going to reload is the one state with no in-process repair path.
				cancellationToken.ThrowIfCancellationRequested();
				return StageBytes(data, current);
			}
			catch(OperationCanceledException)
			{
				LogUpdate("download cancelled before staging");
				TryDelete(StagedPath);
				return false;
			}
			catch(Exception ex)
			{
				LogUpdate("staging failed (offering manual update instead): " + ex.Message);
				TryDelete(StagedPath);
				return false;
			}
		}

		/// <summary>
		/// Validates the payload and parks it as <c>*.dll.new</c>. Deliberately does NOT touch the
		/// live DLL: the swap happens in <see cref="ApplyPendingUpdate"/> at the next OnLoad, where
		/// process death cannot catch it mid-rename. Network-free so tests can drive it directly.
		/// </summary>
		internal bool StageBytes(byte[] data, Version? mustExceed = null)
		{
			try
			{
				if(data == null || data.Length < 1024 || data[0] != (byte)'M' || data[1] != (byte)'Z')
				{
					LogUpdate($"download failed the PE-header sanity check ({data?.Length ?? 0} bytes)");
					return false;
				}
				if(data.Length > MaxAssemblyBytes)
				{
					LogUpdate($"download is implausibly large ({data.Length} bytes); refusing it");
					return false;
				}
				// Write to a temp name, validate THERE, then rename onto the staged path. The
				// rename is atomic on the same volume, so the swap at the next load can never see
				// a half-written file — and a second HDT instance staging concurrently cannot
				// truncate the file this one already validated. Validating after the final move is
				// impossible, so atomicity of the staged file is the only defence.
				var pending = StagedPath + ".tmp";
				TryDelete(pending);
				File.WriteAllBytes(pending, data);
				if(!IsPluginAssembly(pending, mustExceed))
				{
					TryDelete(pending);
					return false;
				}
				using(var guard = TryLock())
				{
					if(guard == null)
					{
						LogUpdate("another instance is updating; leaving this download unstaged");
						TryDelete(pending);
						return false;
					}
					TryDelete(StagedPath);
					File.Move(pending, StagedPath);
				}
				LogUpdate("update downloaded; it will be applied when HDT next starts");
				return true;
			}
			catch(Exception ex)
			{
				LogUpdate("download could not be staged (offering manual update instead): " + ex.Message);
				TryDelete(StagedPath);
				return false;
			}
		}

		/// <summary>
		/// Is this actually our plugin? The MZ/length check only proves "some PE image", which a
		/// native DLL, a Debug build or another project's output would also pass — and a DLL that
		/// HDT cannot load is a plugin that is gone, with no in-process way back. So read the
		/// managed assembly identity off the staged file before it goes anywhere near the live one.
		/// </summary>
		private static bool IsPluginAssembly(string stagedPath, Version? mustExceed = null)
		{
			try
			{
				var name = System.Reflection.AssemblyName.GetAssemblyName(stagedPath);
				if(!string.Equals(name.Name, ExpectedAssemblyName, StringComparison.Ordinal))
				{
					LogUpdate($"download is '{name.Name}', not {ExpectedAssemblyName}; refusing it");
					return false;
				}
				// Name alone accepts an OLDER build — a release whose asset is a stale artifact.
				// Installing that walks the installed version backwards and then re-downloads and
				// re-swaps the same DLL every day, permanently masking the real version.
				if(mustExceed != null && name.Version != null && !IsNewer(mustExceed, name.Version))
				{
					LogUpdate($"download is v{name.Version}, not newer than v{mustExceed}; refusing it");
					return false;
				}
				return true;
			}
			catch(Exception ex)
			{
				LogUpdate("download is not a loadable managed assembly: " + ex.Message);
				return false;
			}
		}

		/// <summary>
		/// The swap: move the loaded (locked) DLL aside and put the downloaded one at its name. A
		/// loaded assembly cannot be overwritten or deleted but CAN be renamed on NTFS (verified),
		/// and HDT loads only exact ".dll" files, so the backup is ignored.
		///
		/// Every failure path must end with a readable DLL at the plugin path. Antivirus holding a
		/// freshly written file for a few hundred milliseconds is the common real-world failure, so
		/// recovery retries instead of giving up on the first throw, and falls back to Copy — which
		/// succeeds where Move's source-delete is blocked.
		/// </summary>
		private bool SwapIn(byte[] data)
		{
			try
			{
				// Park any existing rollback rather than deleting it: deleting up front would burn
				// the user's fallback to pay for a swap that may still fail.
				TryDelete(PriorBackupPath);
				TryMove(BackupPath, PriorBackupPath);
				if(File.Exists(BackupPath))
				{
					LogUpdate("previous rollback is locked; leaving the installed DLL alone");
					return false;
				}

				File.Move(_dllPath, BackupPath);
				try
				{
					File.Move(StagedPath, _dllPath);
				}
				catch(Exception ex)
				{
					LogUpdate("swap refused, restoring: " + ex.Message);
					RestoreDll(data);
					TryMove(PriorBackupPath, BackupPath);
					return false;
				}

				LogUpdate("update applied; live from this session on");
				return true;
			}
			catch(Exception ex)
			{
				LogUpdate("swap failed (offering manual update instead): " + ex.Message);
				RestoreDll(data);
				return false;
			}
			finally
			{
				// Whatever happened, the rollback must end up back at *.dll.old. Without this, a
				// throw from the FIRST move left it only under *.prev, which the caller then
				// deletes — quietly destroying the user's documented fallback for a swap that
				// never even happened.
				if(!File.Exists(BackupPath))
					TryMove(PriorBackupPath, BackupPath);
			}
		}

		/// <summary>
		/// Get something loadable back to the plugin path, trying hardest last. Must not return
		/// while the folder has no DLL if any of these can succeed.
		/// </summary>
		private void RestoreDll(byte[] data)
		{
			for(var attempt = 0; attempt < RestoreAttempts; attempt++)
			{
				if(File.Exists(_dllPath))
					return;
				TryMove(BackupPath, _dllPath);   // the original, ideally
				if(File.Exists(_dllPath))
					return;
				TryCopy(BackupPath, _dllPath);   // Move blocked but Copy may not be
				if(File.Exists(_dllPath))
					return;
				try { File.WriteAllBytes(_dllPath, data); } // last resort: the new bytes
				catch(Exception ex) { LogUpdate($"restore attempt {attempt + 1} failed: {ex.Message}"); }
				if(File.Exists(_dllPath))
					return;
				System.Threading.Thread.Sleep(RestoreRetryDelayMs);
			}
			if(!File.Exists(_dllPath))
				LogUpdate("CRITICAL: no plugin DLL in place. Fix by hand: in the plugin folder rename "
					+ "HdtArenaHelper.dll.new (or .dll.old) to HdtArenaHelper.dll, or reinstall from "
					+ ReleasesPage);
		}
		/// <summary>
		/// The download URL of the bare-DLL release asset, or null if absent. The URL is only
		/// accepted from GitHub's own HTTPS asset hosts: these bytes get executed on the next
		/// launch, so the one thing worth being strict about is where they came from.
		/// </summary>
		internal static string? SelectDllAssetUrl(JObject release)
		{
			if(!(release["assets"] is JArray assets))
				return null;
			foreach(var a in assets)
			{
				if(!string.Equals((string?)a["name"], DllAssetName, StringComparison.OrdinalIgnoreCase))
					continue;
				var url = (string?)a["browser_download_url"];
				return url != null && IsTrustedAssetUrl(url) ? url : null;
			}
			return null;
		}

		internal static bool IsTrustedAssetUrl(string url)
			// Scoped to THIS repo's release path, not any github.com URL: the asset name is the only
			// thing the lookup matches on, so "some repo on github.com" is a weaker guarantee than
			// the code deserves. The githubusercontent hosts are where GitHub redirects asset
			// downloads, and carry opaque per-object paths.
			=> url.StartsWith("https://github.com/" + Owner + "/" + Repo + "/releases/",
					StringComparison.OrdinalIgnoreCase)
				|| url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
				|| url.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

		/// <summary>Parses a release tag ("v0.1.2", "0.1.2", "0.2") into a Version, or null.</summary>
		internal static Version? ParseVersion(string? tag)
		{
			if(string.IsNullOrWhiteSpace(tag))
				return null;
			tag = tag!.Trim();
			if(tag[0] == 'v' || tag[0] == 'V')
				tag = tag.Substring(1);
			return Version.TryParse(tag, out var v) ? v : null;
		}

		/// <summary>
		/// True if <paramref name="latest"/> is a newer release than <paramref name="current"/>.
		/// Both are normalized to (major, minor, build) — the plugin reports a 3-part version and
		/// tags are 3-part, so comparing raw Versions (where an unset Revision is -1, not 0) would
		/// mis-rank an occasional 4-part tag against a 3-part install.
		/// </summary>
		internal static bool IsNewer(Version current, Version latest)
			=> Normalize(latest) > Normalize(current);

		private static Version Normalize(Version v)
			=> new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

		private void StampChecked()
		{
			try
			{
				var dir = Path.GetDirectoryName(_stampFile);
				if(!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);
				File.WriteAllText(_stampFile, DateTime.UtcNow.ToString("o"));
			}
			catch(Exception ex)
			{
				LogUpdate("could not write check stamp: " + ex.Message);
			}
		}

		private static void TryDelete(string path)
		{
			try
			{
				if(File.Exists(path))
					File.Delete(path);
			}
			catch(Exception ex)
			{
				LogUpdate($"could not delete {Path.GetFileName(path)}: {ex.Message}");
			}
		}

		private static void TryCopy(string from, string to)
		{
			try
			{
				if(File.Exists(from))
					File.Copy(from, to, overwrite: false);
			}
			catch(Exception ex)
			{
				LogUpdate($"could not copy {Path.GetFileName(from)}: {ex.Message}");
			}
		}

		private static void TryMove(string from, string to)
		{
			try
			{
				if(File.Exists(from))
					File.Move(from, to);
			}
			catch(Exception ex)
			{
				LogUpdate($"could not restore {Path.GetFileName(to)}: {ex.Message}");
			}
		}

		private static void LogUpdate(string message) => Log.Info("[ArenaHelper] update: " + message);
	}
}
