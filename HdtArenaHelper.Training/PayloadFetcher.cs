using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HdtArenaHelper.Training
{
	/// <summary>
	/// Fetches the public win-rate payloads, snapshotting each one so a fit can be reproduced
	/// (or re-run entirely offline) without hitting a free endpoint again.
	/// </summary>
	internal static class PayloadFetcher
	{
		// Snapshot of every fetched payload. A normal run SAVES what it fetched; --offline
		// refits from that saved copy without touching the network. Without this the fit is
		// unreproducible by construction: you can never re-run it on the same data, so
		// "the model changed" and "the data changed" are inseparable — and the whole
		// CV/bootstrap workload below would otherwise re-hammer a free endpoint per fit.
		internal static string SnapshotDir = "";
		internal static bool Offline;

		internal static string SnapshotPath(string url)
		{
			var name = new string(url.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
			if(name.Length > 120)
				name = name.Substring(0, 60) + "_" + url.GetHashCode().ToString("x8");
			return Path.Combine(SnapshotDir, name + ".json");
		}

		internal static string Download(string url)
		{
			var snapshot = SnapshotPath(url);
			if(Offline)
			{
				if(!File.Exists(snapshot))
					throw new FileNotFoundException(
						$"--offline requested but no snapshot for {url}. Run once online first.", snapshot);
				return File.ReadAllText(snapshot);
			}

			var payload = DownloadLive(url);
			try
			{
				Directory.CreateDirectory(SnapshotDir);
				File.WriteAllText(snapshot, payload);
			}
			catch(Exception ex)
			{
				// A snapshot is a convenience, never a reason to fail a live fit.
				Console.Error.WriteLine($"warning: could not write snapshot for {url}: {ex.Message}");
			}
			return payload;
		}

		internal static string DownloadLive(string url)
		{
			// .NET's TLS/HTTP fingerprint gets 403'd by Cloudflare on hsreplay.net, while
			// curl (bundled with Windows 10+/Git) is allowed. Shell out to it for this
			// dev-only fetch; --compressed keeps a gzipped payload small on the wire.
			var psi = new ProcessStartInfo("curl", $"-fsSL --compressed -A \"{TrainingConfig.UserAgent}\" \"{url}\"")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using(var proc = Process.Start(psi))
			{
				if(proc == null)
					throw new InvalidOperationException("could not start curl");
				var stdout = proc.StandardOutput.ReadToEnd();
				proc.WaitForExit();
				if(proc.ExitCode != 0)
					throw new InvalidOperationException($"curl failed for {url} (exit {proc.ExitCode})");
				return stdout;
			}
		}
	}
}
