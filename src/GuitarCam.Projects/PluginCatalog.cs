using System.Text;
using System.Text.Json;
using GuitarCam.Audio.Vst;

namespace GuitarCam.Projects;

/// <summary>
/// The VST3 plugins on this computer (native only): what <see cref="VstScanner"/> found, kept in
/// %APPDATA%\GuitarCam Studio\plugins.json with each file's date, so a new scan only opens new or changed files (some
/// plugins take seconds to load). Files that failed are remembered too, with why, so they are not retried every time.
/// What the user manages in Configuración → Plugins lives in config.json: the folders added to the standard ones
/// ("vst.folders") and the plugins hidden from the lists ("vst.hidden", by <see cref="Key"/>).
/// </summary>
public static class PluginCatalog
{
    public sealed class Entry
    {
        public string Path { get; set; } = "";
        public DateTime Modified { get; set; }
        public string? Error { get; set; }
        public List<VstPluginInfo> Plugins { get; set; } = [];
        /// <summary>The <see cref="CurrentFormat"/> it was opened with (0: before there was one).</summary>
        public int Format { get; set; }
    }

    /// <summary>
    /// What a scan learns of each file: 2 = also its buses (<see cref="VstPluginInfo.AudioIns"/>), to tell instruments from
    /// effects. A file opened with an older one is opened again on the next scan, once, so that is detected on its own.
    /// </summary>
    public const int CurrentFormat = 2;

    static string FilePath => System.IO.Path.Combine(AppConfig.Dir, "plugins.json");
    const string FoldersKey = "vst.folders", HiddenKey = "vst.hidden";
    /// <summary>One scan (or retry) at a time: they read and write the same plugins.json.</summary>
    static readonly object scanGate = new();

    /// <summary>What the last scan found (empty if there was none).</summary>
    public static List<Entry> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath, Encoding.UTF8), ProjectStore.Json) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Every plugin class found, instruments and effects, by name.</summary>
    public static List<VstPluginInfo> Plugins(IEnumerable<Entry> entries) =>
        [.. entries.SelectMany(e => e.Plugins).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The ones the user didn't hide: what the menus offer.</summary>
    public static List<VstPluginInfo> Visible(IEnumerable<Entry> entries)
    {
        var hidden = Hidden();
        return [.. Plugins(entries).Where(p => !hidden.Contains(Key(p)))];
    }

    /// <summary>
    /// Looks through the folders again (<see cref="Folders"/> by default): unchanged files keep what was found, new or
    /// changed ones are opened (<paramref name="progress"/> gets each name, its number and how many will be opened),
    /// and files that are gone are forgotten. Cancelled, it stops between files and keeps what it knew of the rest.
    /// Slow the first time: never on the UI thread.
    /// </summary>
    public static List<Entry> Scan(Action<string, int, int>? progress = null, IEnumerable<string>? folders = null, bool all = false,
        CancellationToken cancel = default)
    {
        lock (scanGate)
        {
            var old = Load().ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            var files = new List<(string Path, DateTime Modified)>();
            foreach (var path in VstScanner.FindFiles(folders ?? Folders))
            {
                try { files.Add((path, File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            // a file that failed isn't retried for a newer format: only when it changes, or on "Revisar todos"
            bool Known((string Path, DateTime Modified) f) => !all && old.TryGetValue(f.Path, out var k) && k.Modified == f.Modified
                && (k.Format >= CurrentFormat || k.Error != null);
            int total = files.Count(f => !Known(f)), n = 0;
            var result = new List<Entry>();
            foreach (var f in files)
            {
                if (Known(f) || cancel.IsCancellationRequested)
                {
                    // cancelled: a file not opened this time keeps what was known of it (a new one waits for the next scan)
                    if (old.TryGetValue(f.Path, out var known)) result.Add(known);
                    continue;
                }
                progress?.Invoke(System.IO.Path.GetFileNameWithoutExtension(f.Path), ++n, total);
                var found = VstScanner.ScanFile(f.Path, out string? error);
                result.Add(new Entry { Path = f.Path, Modified = f.Modified, Error = found.Count == 0 ? error : null, Plugins = found, Format = CurrentFormat });
            }
            Save(result);
            return result;
        }
    }

    /// <summary>Opens one file again (a retry after it failed, or after updating it) and keeps the rest as they were.</summary>
    public static List<Entry> Rescan(string path)
    {
        lock (scanGate)
        {
            var list = Load();
            int i = list.FindIndex(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            Entry? entry = null;
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    var modified = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path);
                    var found = VstScanner.ScanFile(path, out string? error);
                    entry = new Entry { Path = path, Modified = modified, Error = found.Count == 0 ? error : null, Plugins = found, Format = CurrentFormat };
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // gone (or unreadable): forgotten, like a scan would
            if (entry == null) { if (i >= 0) list.RemoveAt(i); }
            else if (i >= 0) list[i] = entry;
            else list.Add(entry);
            Save(list);
            return list;
        }
    }

    static void Save(List<Entry> list)
    {
        try { AppConfig.WriteAtomic(FilePath, JsonSerializer.Serialize(list, ProjectStore.Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* only a cache */ }
    }

    // --- what the user manages

    /// <summary>Where a scan looks: Windows' folders and the ones the user added.</summary>
    public static List<string> Folders => [.. VstScanner.DefaultFolders, .. ExtraFolders()];

    /// <summary>The folders the user added (Configuración → Plugins, or gclab vst carpetas --agregar).</summary>
    public static List<string> ExtraFolders() => ReadList(FoldersKey);

    /// <summary>Adds a folder to look in; false if it was already there (Windows' ones included).</summary>
    public static bool AddFolder(string dir)
    {
        dir = System.IO.Path.GetFullPath(dir).TrimEnd('\\', '/');
        if (Folders.Any(d => SamePath(d, dir))) return false;
        WriteList(FoldersKey, [.. ExtraFolders(), dir]);
        return true;
    }

    /// <summary>Stops looking in a folder the user added (its plugins are forgotten on the next scan).</summary>
    public static bool RemoveFolder(string dir)
    {
        var list = ExtraFolders();
        if (list.RemoveAll(d => SamePath(d, dir)) == 0) return false;
        WriteList(FoldersKey, list);
        return true;
    }

    /// <summary>How a plugin class is told apart: its file and its class id.</summary>
    public static string Key(VstPluginInfo p) => p.Path.ToLowerInvariant() + "|" + p.ClassId;

    /// <summary>The <see cref="Key"/>s of the plugins hidden from the lists.</summary>
    public static HashSet<string> Hidden() => [.. ReadList(HiddenKey)];

    /// <summary>Hides a plugin from the lists (tracks that already use it keep it), or shows it again.</summary>
    public static void SetHidden(VstPluginInfo p, bool hidden)
    {
        var set = Hidden();
        if (hidden ? !set.Add(Key(p)) : !set.Remove(Key(p))) return;
        WriteList(HiddenKey, [.. set.Order(StringComparer.Ordinal)]);
    }

    static List<string> ReadList(string key)
    {
        try { return AppConfig.Get(key) is { } s ? JsonSerializer.Deserialize<List<string>>(s)?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [] : []; }
        catch (JsonException) { return []; }
    }

    static void WriteList(string key, List<string> list) => AppConfig.Set(key, JsonSerializer.Serialize(list));

    static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>A plugin by name (exact, then the first that contains it), for gclab.</summary>
    public static VstPluginInfo? Find(IEnumerable<VstPluginInfo> plugins, string name) =>
        plugins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? plugins.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    public static PluginRef Ref(VstPluginInfo p) => new() { Path = p.Path, ClassId = p.ClassId, Name = p.Name, Vendor = p.Vendor };
}
