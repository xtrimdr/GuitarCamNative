using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GuitarCam.Audio;

/// <summary>How an FFmpeg install went: <see cref="Ready"/> when FFmpeg answers afterwards, and winget's last lines.</summary>
public sealed record FfmpegInstallResult(bool Ready, int ExitCode, string Log);

/// <summary>
/// Gets FFmpeg onto a PC that doesn't have it (asked by the user): the installer doesn't carry ffmpeg.exe, so when it
/// is missing the app offers to install it with winget (Gyan's build, the one ffmpeg.org links for Windows: it has
/// libx264, LAME and the card encoders the export uses) or to open the download page. winget writes the new folder to
/// the user's PATH in the registry, which a running process never sees, so <see cref="RefreshPath"/> reads it back
/// and FFmpeg works without restarting the app.
/// </summary>
public static class FfmpegSetup
{
    public const string WingetId = "Gyan.FFmpeg";
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/";
    /// <summary>What to type by hand, shown in the dialog.</summary>
    public const string WingetCommand = "winget install --id " + WingetId + " -e";
    /// <summary>A download that takes longer than this counts as stuck (the build is about 100 MB).</summary>
    const int InstallTimeoutMs = 15 * 60 * 1000;
    /// <summary>winget's lines kept for the error message.</summary>
    const int LogLines = 6;

    /// <summary>True when winget (App Installer) is on this PC; it comes with Windows 10 1809+ and 11, but can be missing.</summary>
    public static bool HasWinget()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget", "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return false;
            p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch (InvalidOperationException) { } return false; }
            return p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }

    /// <summary>
    /// Installs FFmpeg with winget, without windows or questions (the user asked for it with the button, which is
    /// accepting the package's terms), then refreshes the PATH and checks that FFmpeg answers. Cancelling kills winget.
    /// </summary>
    public static async Task<FfmpegInstallResult> InstallAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("winget", ["install", "--id", WingetId, "-e", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var log = new Queue<string>();
        void Keep(string? line)
        {
            line = Clean(line);
            if (line.Length == 0) return;
            lock (log)
            {
                log.Enqueue(line);
                while (log.Count > LogLines) log.Dequeue();
            }
        }
        int code;
        try
        {
            using var p = Process.Start(psi) ?? throw new Win32Exception();
            p.OutputDataReceived += (_, e) => Keep(e.Data);
            p.ErrorDataReceived += (_, e) => Keep(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeoutMs);
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                throw;
            }
            code = p.ExitCode;
        }
        catch (Win32Exception e) { return new FfmpegInstallResult(false, -1, e.Message); }
        RefreshPath();
        VideoEncoders.Forget();
        bool ready = await Task.Run(FfmpegInfo.Probe).ConfigureAwait(false) != null;
        lock (log) return new FfmpegInstallResult(ready, code, string.Join("\n", log));
    }

    /// <summary>
    /// Rebuilds this process' PATH from the machine's and the user's (as Windows keeps them now), keeping what only
    /// this process had, so a program installed while the app runs is found by name.
    /// </summary>
    public static void RefreshPath()
    {
        var parts = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Process })
        {
            string? value;
            try { value = Environment.GetEnvironmentVariable("Path", target); }
            catch (System.Security.SecurityException) { continue; }
            foreach (var raw in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string dir = Environment.ExpandEnvironmentVariables(raw);
                if (!parts.Contains(dir, StringComparer.OrdinalIgnoreCase)) parts.Add(dir);
            }
        }
        Environment.SetEnvironmentVariable("Path", string.Join(';', parts));
    }

    /// <summary>A winget line without its progress bar and spinner (they come as lines of their own through a pipe).</summary>
    static string Clean(string? line)
    {
        if (line == null) return "";
        int cr = line.LastIndexOf('\r');
        if (cr >= 0) line = line[(cr + 1)..];
        line = line.Trim();
        if (line.Length <= 1 || line.Contains('█') || line.Contains('▒')) return "";
        return line;
    }
}
