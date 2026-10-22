using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitarCam.Projects;

/// <summary>
/// An open project folder:
/// <code>
///   guitarcam.json        the project
///   guitarcam.json.bak    the previous save
///   guitarcam.lock        held while it's open (a second window opens it read-only)
///   historial/            snapshots, to go back further than the .bak
///   media/                recordings and imported files (never rewritten)
///   exports/
/// </code>
/// Every save writes a temporary file, forces it to disk and swaps it in with File.Replace, so the
/// project file is always either the old save or the new one, never half of each.
/// </summary>
public sealed class ProjectStore : IDisposable
{
    public const string FileName = "guitarcam.json";
    public const string MediaDir = "media";
    public const string HistoryDir = "historial";
    const string LockName = "guitarcam.lock";
    const string SnapshotStamp = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>A new snapshot at most this often while saving.</summary>
    public static TimeSpan SnapshotEvery { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>The newest ones always stay; older ones keep one per day.</summary>
    public const int KeepRecentSnapshots = 30;
    public const int KeepDailySnapshotsDays = 90;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly object writeLock = new();
    FileStream? lockFile;
    DateTime lastSnapshot = DateTime.MinValue;
    string? lastSnapshotText;

    public string Dir { get; }
    public Project Project { get; private set; }
    /// <summary>Set when the project can't be saved from here; the reason is shown to the user.</summary>
    public string? ReadOnlyReason { get; private set; }
    public bool ReadOnly => ReadOnlyReason != null;
    /// <summary>What happened while opening (recovered from a backup, missing files, recovered takes…).</summary>
    public List<string> Notices { get; } = [];

    public string FilePath => Path.Combine(Dir, FileName);
    string BakPath => FilePath + ".bak";
    string TmpPath => FilePath + ".tmp";

    ProjectStore(string dir, Project project)
    {
        Dir = dir;
        Project = project;
    }

    public static bool IsProject(string dir) => File.Exists(Path.Combine(dir, FileName));

    /// <summary>Creates the folder (it must not hold a project yet) and writes the first save.</summary>
    public static ProjectStore Create(string dir, Project project)
    {
        if (IsProject(dir)) throw new IOException("Esa carpeta ya tiene un proyecto");
        Directory.CreateDirectory(Path.Combine(dir, MediaDir));
        project.Normalize();
        var store = new ProjectStore(dir, project);
        store.TakeLock();
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        store.Save();
        return store;
    }

    public static ProjectStore Open(string dir)
    {
        var (project, from, notices) = LoadWithFallback(dir);
        var store = new ProjectStore(dir, project);
        store.Notices.AddRange(notices);
        store.TakeLock();
        if (project.Format != Project.FormatId)
            store.ReadOnlyReason = "El archivo no es un proyecto de GuitarCam";
        else if (project.Version > Project.CurrentVersion)
            store.ReadOnlyReason = "Lo guardó una versión más nueva de GuitarCam: abierto solo para ver";
        if (!store.ReadOnly)
        {
            Directory.CreateDirectory(Path.Combine(dir, MediaDir));
            Migrate(project);
            var recovered = store.RecoverRecordings();
            int adopted = store.AdoptStrayMedia();
            // a project rescued from the .bak or a snapshot goes back to being the main file right away
            if (from != FileName || recovered.Count > 0 || adopted > 0) store.Save();
            foreach (var j in recovered) File.Delete(j); // only once the project has the takes
        }
        store.CheckMedia();
        return store;
    }

    /// <summary>Older versions of the format get upgraded here, one step at a time.</summary>
    static void Migrate(Project p)
    {
        // version 1 is the first one: nothing to do yet
        p.Version = Project.CurrentVersion;
    }

    static (Project Project, string From, List<string> Notices) LoadWithFallback(string dir)
    {
        var notices = new List<string>();
        string main = Path.Combine(dir, FileName);
        Exception? first = null;
        Project? TryParse(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
            {
                first ??= e;
                return null;
            }
        }
        if (TryParse(main) is { } ok) return (ok, FileName, notices);

        // the newest readable copy wins: the last snapshot can be newer than the .bak
        var best = new[] { main + ".bak" }
            .Concat(Snapshots(dir).Take(10).Select(s => s.Path))
            .Select(path => (Path: path, Project: TryParse(path)))
            .Where(c => c.Project != null)
            .MaxBy(c => c.Project!.UpdatedAt);
        if (best.Project == null) throw first ?? new FileNotFoundException("La carpeta no tiene un proyecto de GuitarCam", main);
        string what = best.Path.EndsWith(".bak")
            ? "el guardado anterior (.bak)"
            : $"la copia del historial {Path.GetFileNameWithoutExtension(best.Path)}";
        notices.Add($"El proyecto estaba dañado: lo abrí desde {what}");
        if (File.Exists(main))
        {
            string keep = Path.Combine(dir, $"guitarcam.dañado-{DateTime.Now.ToString(SnapshotStamp, CultureInfo.InvariantCulture)}.json");
            File.Move(main, keep);
            notices.Add($"El archivo dañado quedó como {Path.GetFileName(keep)}");
        }
        return (best.Project, best.Path, notices);
    }

    public static Project Parse(string json)
    {
        var p = JsonSerializer.Deserialize<Project>(json, Json) ?? throw new InvalidDataException("El proyecto está vacío");
        p.Normalize();
        return p;
    }

    public string Serialize()
    {
        Project.UpdatedAt = DateTime.UtcNow;
        return JsonSerializer.Serialize(Project, Json);
    }

    /// <summary>Serializes and writes now. Call it on the thread that owns the project.</summary>
    public void Save() => Write(Serialize());

    /// <summary>
    /// Writes an already serialized project (safe from any thread; writes run one at a time).
    /// tmp → flush to disk → replace (the old file becomes the .bak) → maybe a snapshot.
    /// </summary>
    public void Write(string json)
    {
        if (ReadOnly) return;
        lock (writeLock)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using (var fs = new FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            Retry(() =>
            {
                if (File.Exists(FilePath)) File.Replace(TmpPath, FilePath, BakPath, true);
                else File.Move(TmpPath, FilePath);
            });
            if (DateTime.UtcNow - lastSnapshot < SnapshotEvery) return;
            try
            {
                WriteSnapshot(json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the save itself went through; the next one tries the snapshot again
                lastSnapshot = DateTime.MinValue;
            }
        }
    }

    /// <summary>Antivirus and indexers sometimes hold the file for a moment.</summary>
    static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                a();
                return;
            }
            catch (IOException) when (i < 5)
            {
                Thread.Sleep(40 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 5)
            {
                Thread.Sleep(40 * (i + 1));
            }
        }
    }

    void WriteSnapshot(string json)
    {
        lastSnapshot = DateTime.UtcNow;
        if (lastSnapshotText == null && Snapshots(Dir) is [var newest, ..]) lastSnapshotText = TryRead(newest.Path);
        if (json == lastSnapshotText) return;
        string dir = Path.Combine(Dir, HistoryDir);
        Directory.CreateDirectory(dir);
        string name = DateTime.Now.ToString(SnapshotStamp, CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, name + ".json");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name}_{i}.json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, path);
        lastSnapshotText = json;
        PruneSnapshots();
    }

    static string? TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return null;
        }
    }

    void PruneSnapshots()
    {
        var all = Snapshots(Dir);
        var keep = new HashSet<string>(all.Take(KeepRecentSnapshots).Select(s => s.Path));
        var cutoff = DateTime.Now.AddDays(-KeepDailySnapshotsDays);
        foreach (var day in all.Where(s => s.Time >= cutoff).GroupBy(s => s.Time.Date))
            keep.Add(day.First().Path); // newest of each day
        foreach (var s in all.Where(s => !keep.Contains(s.Path)))
        {
            try
            {
                File.Delete(s.Path);
            }
            catch (IOException)
            {
            }
        }
    }

    public readonly record struct Snapshot(string Path, DateTime Time);

    /// <summary>The project's snapshots, newest first.</summary>
    public static List<Snapshot> Snapshots(string dir)
    {
        string h = Path.Combine(dir, HistoryDir);
        if (!Directory.Exists(h)) return [];
        var list = new List<Snapshot>();
        foreach (var f in Directory.GetFiles(h, "*.json"))
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (n.Length >= SnapshotStamp.Length
                && DateTime.TryParseExact(n[..SnapshotStamp.Length], SnapshotStamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                list.Add(new Snapshot(f, t));
        }
        return list.OrderByDescending(s => s.Time).ThenByDescending(s => s.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Goes back to a snapshot. The current state is saved as a snapshot first, so this can be undone
    /// from the same list.
    /// </summary>
    public void Restore(Snapshot s)
    {
        if (ReadOnly) throw new InvalidOperationException(ReadOnlyReason);
        var old = Parse(File.ReadAllText(s.Path, Encoding.UTF8));
        lock (writeLock) WriteSnapshot(Serialize());
        Migrate(old);
        Project = old;
        Project.AddLog("snapshot-restored", Path.GetFileNameWithoutExtension(s.Path));
        lastSnapshot = DateTime.MinValue;
        Save();
        Notices.Clear();
        CheckMedia();
    }

    void TakeLock()
    {
        try
        {
            lockFile = new FileStream(Path.Combine(Dir, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var who = Encoding.UTF8.GetBytes($"{Environment.MachineName} {Environment.ProcessId} {DateTime.Now:s}");
            lockFile.SetLength(0);
            lockFile.Write(who);
            lockFile.Flush();
        }
        catch (IOException)
        {
            ReadOnlyReason = "Está abierto en otra ventana de GuitarCam: abierto solo para ver";
        }
    }

    /// <summary>Absolute path of a media file (relative paths are inside the project).</summary>
    public string? Resolve(string? rel) => string.IsNullOrEmpty(rel) ? null : Path.IsPathRooted(rel) ? rel : Path.Combine(Dir, rel);

    /// <summary>Clips whose media file is gone stay in the project, marked as offline by the app.</summary>
    void CheckMedia()
    {
        var used = Project.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaId).ToHashSet();
        var missing = Project.Media
            .Where(m => used.Contains(m.Id) && Resolve(m.Audio) is { } a && !File.Exists(a))
            .Select(m => m.Name)
            .ToList();
        if (missing.Count > 0) Notices.Add($"Faltan archivos de audio: {string.Join(", ", missing)} (quedan sin conexión)");
        var orphan = Project.Tracks.SelectMany(t => t.Clips).Count(c => Project.FindMedia(c.MediaId) == null);
        if (orphan > 0) Notices.Add($"{orphan} clip(s) apuntan a medios que no están en el proyecto");
    }

    // --- recordings in progress ------------------------------------------------------------------

    /// <summary>
    /// Written next to a WAV before recording starts and deleted once the project has the take.
    /// If one is still there when the project opens, the program died mid-take.
    /// </summary>
    public sealed class RecordingJournal
    {
        public string MediaId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Audio { get; set; } = "";
        public string? Video { get; set; }
        public string? TrackId { get; set; }
        public double AtSec { get; set; }
        public double SyncMs { get; set; }
        public double VideoOffsetMs { get; set; } = 80;
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    static string JournalPath(string dir, string mediaId) => Path.Combine(dir, MediaDir, mediaId + ".grabando.json");

    public void BeginRecording(RecordingJournal j)
    {
        string path = JournalPath(Dir, j.MediaId);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(j, Json), Encoding.UTF8);
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>Call after the project with the new take is saved.</summary>
    public void EndRecording(string mediaId) => File.Delete(JournalPath(Dir, mediaId));

    /// <summary>
    /// Turns every unfinished recording into a media + clip, so a crash never costs a take.
    /// Returns the journals to delete after the project is saved.
    /// </summary>
    List<string> RecoverRecordings()
    {
        var done = new List<string>();
        string media = Path.Combine(Dir, MediaDir);
        foreach (var jf in Directory.GetFiles(media, "*.grabando.json"))
        {
            RecordingJournal? j;
            try
            {
                j = JsonSerializer.Deserialize<RecordingJournal>(File.ReadAllText(jf, Encoding.UTF8), Json);
            }
            catch (JsonException)
            {
                j = null;
            }
            if (j == null || Project.FindMedia(j.MediaId) != null)
            {
                File.Delete(jf); // already in the project (it died after saving) or unreadable
                continue;
            }
            string? wav = Resolve(j.Audio);
            if (wav == null || !File.Exists(wav))
            {
                File.Delete(jf);
                continue;
            }
            Audio.WavFile.Info info;
            try
            {
                info = Audio.WavFile.Repair(wav);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Notices.Add($"No pude recuperar la grabación {j.Name}: {e.Message}");
                continue;
            }
            if (info.DurationSec < 0.05)
            {
                File.Delete(jf);
                continue;
            }
            string? video = Resolve(j.Video) is { } v && File.Exists(v) ? j.Video : null;
            var m = new Media
            {
                Id = j.MediaId,
                Name = j.Name,
                Kind = "recording",
                Audio = j.Audio,
                Video = video,
                DurationSec = info.DurationSec,
                SampleRate = info.SampleRate,
                Channels = info.Channels,
                CreatedAt = j.StartedAt,
                SyncMs = j.SyncMs,
                VideoOffsetMs = j.VideoOffsetMs,
            };
            Project.Media.Add(m);
            var track = Project.Tracks.Find(t => t.Id == j.TrackId);
            if (track == null)
            {
                track = new Track { Name = j.Name, Video = video != null };
                Project.Tracks.Add(track);
            }
            track.Clips.Add(new Clip { MediaId = m.Id, AtSec = j.AtSec });
            Project.AddLog("recording-recovered", j.Name);
            Notices.Add($"Recuperé la grabación interrumpida \"{j.Name}\" ({info.DurationSec:0.0} s)");
            done.Add(jf);
        }
        return done;
    }

    static readonly string[] AudioExts = [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aif", ".aiff"];
    static readonly string[] VideoExts = [".mp4", ".webm", ".mov", ".mkv"];

    /// <summary>
    /// Audio in media/ that no media points to (an older save was restored, or the project file was
    /// rescued from a backup) comes back as media without a clip, never lost in the folder.
    /// </summary>
    int AdoptStrayMedia()
    {
        string media = Path.Combine(Dir, MediaDir);
        var known = Project.Media.SelectMany(m => new[] { Resolve(m.Audio), Resolve(m.Video) })
            .Where(p => p != null)
            .Select(p => Path.GetFullPath(p!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var f in Directory.GetFiles(media))
        {
            if (!AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()) || known.Contains(Path.GetFullPath(f))) continue;
            if (File.Exists(f.Replace(".wav", ".grabando.json"))) continue;
            string stem = Path.GetFileNameWithoutExtension(f);
            string? video = VideoExts.Select(e => Path.Combine(media, stem + e)).FirstOrDefault(v => File.Exists(v) && !known.Contains(Path.GetFullPath(v)));
            var m = new Media
            {
                Name = stem,
                Audio = $"{MediaDir}/{Path.GetFileName(f)}",
                Video = video == null ? null : $"{MediaDir}/{Path.GetFileName(video)}",
                CreatedAt = File.GetCreationTimeUtc(f),
            };
            try
            {
                var info = Audio.WavFile.ReadInfo(f);
                (m.DurationSec, m.SampleRate, m.Channels) = (info.DurationSec, info.SampleRate, info.Channels);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                // not a WAV: the app reads its length when it loads it
            }
            Project.Media.Add(m);
            names.Add(stem);
        }
        if (names.Count > 0)
        {
            Project.AddLog("media-adopted", string.Join(", ", names));
            Notices.Add($"Había {names.Count} archivo(s) en media/ que el proyecto no tenía: los sumé a los medios ({string.Join(", ", names)})");
        }
        return names.Count;
    }

    public void Dispose()
    {
        lockFile?.Dispose();
        lockFile = null;
    }
}
