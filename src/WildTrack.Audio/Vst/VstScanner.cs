using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WildTrack.Audio.Vst;

/// <summary>
/// A plugin class found in a .vst3 file: what wtvsthost --scan says about it. <see cref="AudioIns"/> and
/// <see cref="EventIns"/> are its main audio inputs and its note inputs (null when the scan couldn't ask, or was made
/// before it did): what it really takes decides whether it is an instrument or an effect, because plenty of plugins
/// say the wrong sub-category (or none).
/// </summary>
public sealed record VstPluginInfo(string Path, string ClassId, string Name, string Vendor, string Categories, string Version,
    int? AudioIns = null, int? EventIns = null)
{
    /// <summary>
    /// It plays notes: it takes notes and no audio, whatever it says; it takes audio and no notes, an effect, whatever it
    /// says; both (a synth with a sidechain, a vocoder) or neither, its sub-categories decide ("Instrument", or no "Fx"
    /// and it takes notes).
    /// </summary>
    public bool IsInstrument => (AudioIns, EventIns) switch
    {
        (0, > 0) => true,
        ( > 0, 0) => false,
        _ => Has("Instrument") || !Has("Fx") && EventIns > 0,
    };

    /// <summary>The scan knew what it takes (a scan before that only had its sub-categories).</summary>
    public bool KnowsBuses => AudioIns != null && EventIns != null;

    bool Has(string category) => Categories.Split('|').Any(c => c.Trim().Equals(category, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Finds VST3 plugins: the .vst3 files and bundles in the standard folders, and what each one holds, asked to
/// wtvsthost.exe one file at a time (a plugin that crashes while loading takes only that process down).
/// </summary>
public static class VstScanner
{
    /// <summary>Where Windows installs VST3 plugins: the common folder and the per-user one.</summary>
    public static IEnumerable<string> DefaultFolders =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"),
    ];

    /// <summary>Every .vst3 under the folders: files, and bundle folders as one plugin (their insides are not searched).</summary>
    public static List<string> FindFiles(IEnumerable<string> folders)
    {
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            if (depth > 6) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var e in entries)
            {
                bool isDir = Directory.Exists(e);
                if (e.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) found.Add(e);
                else if (isDir) Walk(e, depth + 1);
            }
        }
        foreach (var f in folders.Where(Directory.Exists)) Walk(f, 0);
        return found;
    }

    /// <summary>The plugin classes in one .vst3 (empty if it has none or failed to load; <paramref name="error"/> says why).</summary>
    public static List<VstPluginInfo> ScanFile(string path, out string? error, int timeoutMs = 30000)
    {
        error = null;
        string? exe = VstHost.Find();
        if (exe == null) { error = Texts.T("err.vst.noHost", "Falta wtvsthost.exe."); return []; }
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--scan");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        var lines = new List<string>();
        var reader = Task.Run(() => { while (p.StandardOutput.ReadLine() is { } l) lines.Add(l); });
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            error = Texts.T("err.vst.scanTimeout", "tardó demasiado en abrir");
            return [];
        }
        reader.Wait(2000);
        var list = new List<VstPluginInfo>();
        foreach (var l in lines)
        {
            if (l.StartsWith("error ", StringComparison.Ordinal)) { error = l[6..]; continue; }
            if (!l.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(l);
                var r = doc.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                int? N(string k) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out int n) ? n : null;
                list.Add(new VstPluginInfo(path, S("cid"), S("name"), S("vendor"), S("sub"), S("version"), N("ins"), N("events")));
            }
            catch (JsonException) { }
        }
        if (list.Count == 0 && error == null && p.ExitCode != 0) error = Texts.T("err.vst.scanCrash", "se cerró al abrir");
        return list;
    }
}
