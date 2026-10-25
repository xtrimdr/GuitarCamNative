using GuitarCam.Audio;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GuitarCam.Projects;

/// <summary>
/// The project manager's operations: list, create, rename, duplicate, delete, forget, projects folder.
/// Port of the project routes of the web app's server (GuitarCam/server/sessions.js), on guitarcam.json
/// instead of session.json. Same names, same "(2)" rules, same Recycle Bin.
/// Like the web's projectSummary except duration uses the native clip's trimmed end:
/// AtSec + media duration - TrimEndSec, rather than the untrimmed take duration.
/// </summary>
public static partial class ProjectLibrary
{
    /// <summary>Metadata for a manager row, without loading audio or video.</summary>
    public sealed record Summary(
        string Dir,
        string Name,
        bool Exists,
        DateTime? CreatedAt,
        DateTime? UpdatedAt,
        DateTime? OpenedAt,
        int TakeCount,
        int TrackCount,
        int ExportCount,
        double DurationSec,
        long Bytes,
        double? Bpm,
        bool InRoot)
    {
        /// <summary>The later of the last save and the last open.</summary>
        public DateTime? LastTouched => UpdatedAt is { } u && OpenedAt is { } o ? (u > o ? u : o) : UpdatedAt ?? OpenedAt;
    }

    // --- projects folder --------------------------------------------------------------------------

    /// <summary>
    /// Where new projects go and which folder the list scans. Saved in the program's config; by default
    /// the web app's projects folder (so both live together), else Documentos\GuitarCam Proyectos.
    /// </summary>
    public static string Root
    {
        get
        {
            if (RootOverride != null) return RootOverride;
            if (AppConfig.Get("projectsRoot") is { } r && Path.IsPathRooted(r)) return Path.GetFullPath(r);
            return WebRoot() ?? DefaultRoot();
        }
    }

    /// <summary>For gclab: a projects folder for this run only, without touching the config.</summary>
    public static string? RootOverride { get; set; }

    public static void SetRoot(string dir)
    {
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        AppConfig.Set("projectsRoot", dir);
    }

    static string DefaultRoot()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(Directory.Exists(docs) ? docs : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GuitarCam Proyectos");
    }

    static string? WebRoot()
    {
        try
        {
            string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".guitarcam", "config.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty("projectsRoot", out var v) && v.GetString() is { } s && Path.IsPathRooted(s) ? Path.GetFullPath(s) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // --- list -------------------------------------------------------------------------------------

    /// <summary>Every project in the projects folder plus recent ones elsewhere, most recent first.</summary>
    public static (string Root, List<Summary> Projects) List()
    {
        string root = Root;
        var recent = RecentProjects.Load();
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Directory.GetDirectories(root))
                if (!Path.GetFileName(d).StartsWith(".duplicando-", StringComparison.OrdinalIgnoreCase) && ProjectStore.IsProject(d))
                    dirs[Path.GetFullPath(d)] = Path.GetFullPath(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the projects folder doesn't exist yet
        }
        foreach (var r in recent)
        {
            string d = Path.GetFullPath(r.Dir);
            dirs.TryAdd(d, d);
        }
        var list = dirs.Values.Select(dir =>
        {
            var rec = recent.Find(r => SamePath(r.Dir, dir));
            var s = Summarize(dir, rec?.Name);
            return s with { OpenedAt = rec?.OpenedAt, InRoot = IsInside(root, dir) };
        }).ToList();
        list.Sort((a, b) => Recency(b).CompareTo(Recency(a)));
        return (root, list);
    }

    static DateTime Recency(Summary p) => p.LastTouched ?? DateTime.MinValue;

    static Summary Summarize(string dir, string? recentName)
    {
        try
        {
            var p = ProjectStore.Parse(File.ReadAllText(Path.Combine(dir, ProjectStore.FileName), Encoding.UTF8));
            var used = p.Tracks.Where(t => t.Clips.Count > 0).ToList();
            double duration = used.SelectMany(t => t.Clips)
                .Select(c => c.AtSec + (p.FindMedia(c.MediaId)?.DurationSec ?? 0) - c.TrimEndSec)
                .DefaultIfEmpty(0)
                .Max();
            long bytes = 0;
            foreach (var m in p.Media)
                foreach (var f in new[] { m.Audio, m.Video })
                    if (f != null && Resolve(dir, f) is { } path && File.Exists(path)) bytes += new FileInfo(path).Length;
            return new Summary(dir, string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileName(dir) : p.Name, true, p.CreatedAt, p.UpdatedAt, null,
                p.Media.Count, used.Count, p.Exports.Count, Math.Max(0, duration), bytes, p.Settings.Bpm, false);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return new Summary(dir, recentName ?? Path.GetFileName(dir), false, null, null, null, 0, 0, 0, 0, 0, null, false);
        }
    }

    static string Resolve(string dir, string rel) => Path.IsPathRooted(rel) ? rel : Path.Combine(dir, rel);

    // --- names ------------------------------------------------------------------------------------

    public static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static bool IsInside(string parent, string child) => SamePath(Path.GetDirectoryName(Path.GetFullPath(child).TrimEnd('\\')), parent);

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex BadChars();

    [GeneratedRegex(@"^(con|prn|aux|nul|com\d|lpt\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex Reserved();

    /// <summary>A project name as a valid Windows folder name.</summary>
    public static string FolderNameFor(string name)
    {
        string clean = Regex.Replace(BadChars().Replace(name, " "), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (Reserved().IsMatch(clean)) clean += "_";
        if (clean.Length > 80) clean = clean[..80];
        return clean.Length == 0 ? "Proyecto" : clean;
    }

    /// <summary>First free root\name, root\name (2)… (<paramref name="keep"/> counts as free: renaming onto itself).</summary>
    static string UniqueDir(string root, string name, string? keep = null)
    {
        string b = FolderNameFor(name);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(root, i == 1 ? b : $"{b} ({i})");
            if (keep != null && SamePath(candidate, keep)) return candidate;
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>A free folder for a new project, and its name with the same " (2)" so two projects never share a name.</summary>
    static (string Dir, string Name) UniqueProject(string root, string name)
    {
        var projects = List().Projects;
        string folder = FolderNameFor(name);
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? name : $"{name} ({i})";
            string dir = Path.Combine(root, i == 1 ? folder : $"{folder} ({i})");
            if (!NameTaken(projects, candidate) && !Path.Exists(dir)) return (dir, candidate);
        }
    }

    static bool NameTaken(IEnumerable<Summary> projects, string name, string? keep = null) =>
        projects.Any(p => p.Exists && !SamePath(p.Dir, keep) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The same free name and Windows folder preview that Create will use.</summary>
    public static (string Dir, string Name) PreviewNew(string name) => UniqueProject(Root, CleanName(name));

    public static string CleanName(string? value)
    {
        string name = (value ?? "").Trim();
        if (name.Length > 120) name = name[..120];
        if (name.Length == 0) throw new ArgumentException("El proyecto necesita un nombre");
        return name;
    }

    // --- operations -------------------------------------------------------------------------------

    /// <summary>A new project in the projects folder (or in <paramref name="dir"/>, an empty folder the user picked).</summary>
    /// <param name="mode">video | audio (native only): the project's mode, from the manager's "Video / Solo audio".</param>
    public static string Create(string name, ProjectTemplate? template, string? dir = null, string mode = "video")
    {
        name = CleanName(name);
        if (dir == null)
        {
            string root = Root;
            Directory.CreateDirectory(root);
            (dir, name) = UniqueProject(root, name);
        }
        else
        {
            dir = Path.GetFullPath(dir);
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                throw new IOException(Texts.T("err.lib.notEmpty", "Esa carpeta no está vacía. Elegí una carpeta vacía para crear el proyecto."));
        }
        var p = new Project { Name = name };
        template?.ApplyTo(p);
        p.Settings.SetMode(mode);
        p.AddLog("session-created");
        using var store = ProjectStore.Create(dir, p);
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        RecentProjects.Touch(dir, name);
        return dir;
    }

    /// <summary>The effective location after renaming, including a folder held open by another program.</summary>
    public sealed record RenameResult(string Dir, bool FolderKept);

    /// <summary>
    /// Renames a project. With <paramref name="renameFolder"/>, one inside the projects folder also gets
    /// its folder renamed (the caller closes it first if it's the open one).
    /// </summary>
    public static RenameResult Rename(string dir, string name, bool renameFolder)
    {
        name = CleanName(name);
        if (NameTaken(List().Projects, name, dir)) throw new IOException("Ya hay otro proyecto con ese nombre.");
        using (var store = ProjectStore.Open(dir))
        {
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            var p = store.Project;
            if (p.Name != name)
            {
                p.Log.Add(new LogEntry
                {
                    Type = "session-renamed",
                    Extra = new()
                    {
                        ["from"] = JsonSerializer.SerializeToElement(p.Name),
                        ["to"] = JsonSerializer.SerializeToElement(name),
                    },
                });
                p.Name = name;
                store.Save();
            }
        }
        bool kept = false;
        string root = Root;
        if (renameFolder && IsInside(root, dir))
        {
            string target = UniqueDir(root, name, dir);
            if (!SamePath(target, dir))
            {
                try
                {
                    Directory.Move(dir, target);
                    RecentProjects.Forget(dir);
                    dir = target;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // something has a file inside open (Explorer, a player…): keep the old folder name
                    kept = true;
                }
            }
        }
        RecentProjects.Touch(dir, name);
        return new RenameResult(dir, kept);
    }

    /// <summary>
    /// Copies a project (media and settings, not its exports or saved versions) into a new folder in the
    /// projects folder. Media is hard-linked when it can be: it's never rewritten, so sharing is safe.
    /// </summary>
    public static string Duplicate(string src)
    {
        var source = ProjectStore.Parse(File.ReadAllText(Path.Combine(src, ProjectStore.FileName), Encoding.UTF8));
        string root = Root;
        Directory.CreateDirectory(root);
        var (dir, name) = UniqueProject(root, $"{source.Name} (copia)");
        // Publish the project only after all its media has been copied. A crash leaves the source intact.
        string staging = Path.Combine(root, $".duplicando-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(staging, ProjectStore.MediaDir));
        var copy = Clone(source);
        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? CopyMedia(string? rel)
        {
            if (rel == null) return null;
            string from = Path.GetFullPath(Resolve(src, rel));
            if (copied.TryGetValue(from, out var existing)) return existing;
            string target = $"{ProjectStore.MediaDir}/{Guid.NewGuid():N}{Path.GetExtension(from)}";
            copied[from] = target;
            string to = Path.Combine(staging, target);
            if (File.Exists(from))
            {
                if (!FileLinks.TryHardLink(to, from)) File.Copy(from, to);
            }
            return target;
        }
        foreach (var m in copy.Media)
        {
            m.Audio = CopyMedia(m.Audio);
            m.Video = CopyMedia(m.Video);
        }
        copy.Id = Ids.New("prj");
        copy.Name = name;
        copy.CreatedAt = copy.UpdatedAt = DateTime.UtcNow;
        copy.Exports = [];
        copy.Log =
        [
            new LogEntry { Type = "session-duplicated", Extra = new() { ["from"] = JsonSerializer.SerializeToElement(src) } },
        ];
        using (var store = ProjectStore.Create(staging, copy)) { }
        Directory.CreateDirectory(Path.Combine(staging, "exports"));
        Directory.Move(staging, dir);
        RecentProjects.Touch(dir, name);
        return dir;
    }

    /// <summary>Sends a project folder to the Recycle Bin. Only folders that hold a project.</summary>
    public static void Delete(string dir)
    {
        dir = Path.GetFullPath(dir);
        if (Path.GetPathRoot(dir) == dir) throw new ArgumentException(Texts.T("err.lib.badPath", "Ruta inválida"));
        if (!ProjectStore.IsProject(dir)) throw new IOException(Texts.T("err.lib.notProject", "Esa carpeta no es un proyecto de GuitarCam"));
        // a project open in another window holds guitarcam.lock: don't pull the folder from under it
        using (var store = ProjectStore.Open(dir))
            if (store.ReadOnly) throw new IOException(Texts.T("err.lib.open", "Está abierto en otra ventana de GuitarCam"));
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        RecentProjects.Forget(dir);
    }

    /// <summary>Takes a project off the recent list (its files stay where they are).</summary>
    public static void Forget(string dir) => RecentProjects.Forget(dir);

    /// <summary>"Canal 3 — Base Rítmica" → "Base Rítmica"; a track's own name stays as is.</summary>
    static readonly Regex TakePrefix = new(@"^(?:Canal|Toma) \d+\s*[—-]\s*");

    /// <summary>The open project as a template: every track's channel (mute/solo cleared) plus its settings.</summary>
    public static ProjectTemplate TemplateOf(Project p, string name) => new()
    {
        Name = name,
        Tracks = p.Tracks.Select((t, i) =>
        {
            var ch = JsonSerializer.Deserialize<Channel>(JsonSerializer.Serialize(t.Channel, ProjectStore.Json), ProjectStore.Json)!;
            ch.Muted = ch.Solo = false;
            string n = TakePrefix.Replace(t.Name, "").Trim();
            return new TemplateTrack { Name = n.Length > 0 ? n : $"Pista {i + 1}", Color = t.Color, Icon = t.Icon, Channel = ch };
        }).ToList(),
        Settings = new TemplateSettings
        {
            Layout = p.Settings.Layout,
            Aspect = p.Settings.Aspect,
            Bpm = p.Settings.Bpm,
            Reverb = Clone(p.Settings.Reverb),
            Delay = Clone(p.Settings.Delay),
            Color = Clone(p.Settings.Color),
            VideoFx = Clone(p.Settings.VideoFx),
        },
    };

    /// <summary>Deep copy through the project's JSON, so Extra and unknown fields come along.</summary>
    public static T Clone<T>(T v) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, ProjectStore.Json), ProjectStore.Json)!;
}

/// <summary>The program's own settings (not a project's), in %APPDATA%\GuitarCam Studio\config.json.</summary>
public static class AppConfig
{
    /// <summary>For isolated gclab runs, without changing the user's recent projects or templates.</summary>
    public static string? DirectoryOverride { get; set; }
    public static string Dir => DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GuitarCam Studio");
    static string FilePath => Path.Combine(Dir, "config.json");
    static readonly object gate = new();

    static JsonObject Read()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) as JsonObject ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string? Get(string key)
    {
        lock (gate) return Read()[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    public static void Set(string key, string? value)
    {
        lock (gate)
        {
            var o = Read();
            o[key] = value;
            WriteAtomic(FilePath, o.ToJsonString(ProjectStore.Json));
        }
    }

    /// <summary>Forces settings to disk before swapping them in; an interrupted write keeps the old file valid.</summary>
    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + $".{Guid.NewGuid():N}.tmp";
        using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(Encoding.UTF8.GetBytes(content));
            file.Flush(true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, null, true);
        else File.Move(tmp, path);
    }
}

/// <summary>Shares immutable media without copying its bytes when Windows allows a hard link.</summary>
static class FileLinks
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public static bool TryHardLink(string link, string existing) => CreateHardLink(link, existing, IntPtr.Zero);
}
