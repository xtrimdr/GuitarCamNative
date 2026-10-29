using WildTrack.Audio;
using System.Globalization;
using System.Text.Json;

namespace WildTrack.Projects;

/// <summary>
/// What the Historial tab needs from the project, port of HistoryPanel.tsx and the server side of the web
/// (sessions.js: saveState's take-removed/take-restored, purge-unused, delete-export): which takes are in use,
/// sending unused takes or an export to the Recycle Bin, and turning the log into readable lines. Same as the
/// web except: only files inside the project folder go to the Recycle Bin (an imported file left where it was
/// is just taken off the list, the original is the user's), and the native-only log entries get their own
/// lines while pure editing entries (clip moved, trimmed…) stay out, as the web never logs them.
/// </summary>
public static class ProjectHistory
{
    /// <summary>Lines shown per page in Actividad (ACTIVITY_PAGE).</summary>
    public const int ActivityPage = 25;
    /// <summary>Consecutive events of the same kind closer than this are shown as one line (MERGE_WINDOW_MS).</summary>
    static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(2);

    static readonly HashSet<string> TakeEvents = ["take-recorded", "take-removed", "take-restored"];

    /// <summary>Editing entries the window and wtlab log for their own sake (clip-moved, track-added…); the web has no line for them.</summary>
    static bool Silent(string type) =>
        type.StartsWith("clip-") || type.StartsWith("track") || type.StartsWith("timeline-") || type.StartsWith("format-")
        || type.StartsWith("wtlab-") || type == "test";

    public static HashSet<string> UsedMediaIds(Project p) => p.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaId).ToHashSet();

    /// <summary>Which media are in the timeline, the baseline <see cref="LogTimelineChanges"/> compares against.</summary>
    public static Dictionary<string, bool> TimelineState(Project p)
    {
        var used = UsedMediaIds(p);
        return p.Media.ToDictionary(m => m.Id, m => used.Contains(m.Id));
    }

    /// <summary>
    /// Logs take-removed / take-restored for every take that left or came back to the timeline since the last
    /// baseline, as saveState does in the web (so undo and redo log too). Takes new since then don't log:
    /// recording one already logs take-recorded.
    /// </summary>
    public static void LogTimelineChanges(Project p, Dictionary<string, bool> baseline)
    {
        var now = TimelineState(p);
        foreach (var m in p.Media)
        {
            bool inNow = now[m.Id];
            if (baseline.TryGetValue(m.Id, out bool was) && was != inNow)
                p.Log.Add(new LogEntry { Type = inNow ? "take-restored" : "take-removed", Name = m.Name, Extra = new() { ["takeId"] = Json(m.Id) } });
        }
        baseline.Clear();
        foreach (var (id, used) in now) baseline[id] = used;
    }

    /// <summary>"Quitar del timeline": the take's lane goes away (a track holding other clips keeps them).</summary>
    public static void RemoveFromTimeline(Project p, string mediaId)
    {
        foreach (var t in p.Tracks.Where(t => t.Clips.Any(c => c.MediaId == mediaId)).ToList())
        {
            if (t.Clips.All(c => c.MediaId == mediaId)) ClipEditing.RemoveTrack(p, t.Id);
            else foreach (var c in t.Clips.Where(c => c.MediaId == mediaId).ToList()) ClipEditing.RemoveClip(p, c.Id);
        }
    }

    /// <summary>Bytes on disk of a take (audio + video); a missing file counts 0.</summary>
    public static long MediaBytes(string projectDir, Media m) => FileBytes(Resolve(projectDir, m.Audio)) + FileBytes(Resolve(projectDir, m.Video));

    static string? Resolve(string dir, string? rel) => string.IsNullOrEmpty(rel) ? null : Path.IsPathRooted(rel) ? rel : Path.Combine(dir, rel);

    /// <summary>Absolute path of an export (exports saved outside the project are logged with their absolute path).</summary>
    public static string ExportPath(string projectDir, ExportRecord e) =>
        Path.IsPathRooted(e.File) ? e.File : Path.Combine(projectDir, e.File.Replace('/', Path.DirectorySeparatorChar));

    static long FileBytes(string? path)
    {
        try { return path != null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public sealed record PurgeResult(int Count, long Bytes);

    public static List<Media> UnusedMedia(Project p)
    {
        var used = UsedMediaIds(p);
        return p.Media.Where(m => !used.Contains(m.Id)).ToList();
    }

    /// <summary>
    /// "Borrar no usadas": every take that is not in the timeline goes to the Recycle Bin and leaves the project.
    /// The caller saves and clears undo (older states could point at the purged takes).
    /// </summary>
    public static PurgeResult PurgeUnused(ProjectStore store)
    {
        store.LogTimelineChanges(); // like the web, which saves the timeline before deciding what is unused
        var (purged, bytes, failure) = TrashMedia(store.Dir, UnusedMedia(store.Project));
        ForgetPurged(store.Project, purged, bytes);
        if (failure != null) throw new IOException(failure.Message, failure);
        return new(purged.Count, bytes);
    }

    /// <summary>
    /// Sends the takes' files to the Recycle Bin, one take at a time, and stops at the first that fails: the takes
    /// whose files went are returned so only those leave the project. Touches no project state (safe off the UI thread).
    /// </summary>
    public static (List<Media> Purged, long Bytes, Exception? Failure) TrashMedia(string projectDir, IReadOnlyList<Media> media)
    {
        var purged = new List<Media>();
        long bytes = 0;
        foreach (var m in media)
        {
            long size = MediaBytes(projectDir, m);
            try
            {
                foreach (var f in new[] { m.Video, m.Audio }) Trash(projectDir, f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                return (purged, bytes, e);
            }
            purged.Add(m);
            bytes += size;
        }
        return (purged, bytes, null);
    }

    /// <summary>Drops purged takes from the project and logs unused-purged (names and bytes, as the web).</summary>
    public static void ForgetPurged(Project p, IReadOnlyList<Media> purged, long bytes)
    {
        if (purged.Count == 0) return;
        var ids = purged.Select(m => m.Id).ToHashSet();
        p.Media.RemoveAll(m => ids.Contains(m.Id));
        p.Log.Add(new LogEntry
        {
            Type = "unused-purged",
            Extra = new() { ["takeIds"] = Json(ids.ToList()), ["names"] = Json(purged.Select(m => m.Name).ToList()), ["bytes"] = Json(bytes) },
        });
    }

    /// <summary>Sends one export to the Recycle Bin (wherever it was saved) and drops it from the list.</summary>
    public static void DeleteExport(ProjectStore store, ExportRecord e)
    {
        TrashExport(store.Dir, e);
        ForgetExport(store.Project, e);
    }

    /// <summary>The file part of <see cref="DeleteExport"/>; touches no project state.</summary>
    public static void TrashExport(string projectDir, ExportRecord e)
    {
        string full = ExportPath(projectDir, e);
        if (!File.Exists(full)) return;
        ToRecycleBin(full);
        if (File.Exists(full)) throw new IOException("No se pudo mandar el archivo a la Papelera");
    }

    public static void ForgetExport(Project p, ExportRecord e)
    {
        p.Exports.Remove(e);
        p.Log.Add(new LogEntry { Type = "export-deleted", Extra = new() { ["file"] = Json(e.File), ["bytes"] = Json(e.Bytes) } });
    }

    /// <summary>Only files inside the project folder: an imported file left where it was belongs to the user.</summary>
    static void Trash(string projectDir, string? rel)
    {
        if (Resolve(projectDir, rel) is not { } full || !File.Exists(full)) return;
        string dir = Path.GetFullPath(projectDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(full).StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return;
        ToRecycleBin(full);
    }

    static void ToRecycleBin(string path) => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    static JsonElement Json<T>(T v) => JsonSerializer.SerializeToElement(v, ProjectStore.Json);

    // ---------- activity log ----------

    /// <summary>A piece of an activity line; <see cref="Strong"/> is a quoted name, shown in Fg.</summary>
    public readonly record struct Part(string Text, bool Strong = false);

    /// <summary>One readable line. Icon: plus, folder, mic, minus, trash, film, history.</summary>
    public sealed record ActivityLine(DateTime At, string Icon, IReadOnlyList<Part> Parts, bool Rec = false)
    {
        public string Text => string.Concat(Parts.Select(p => p.Text));
    }

    static string? Str(LogEntry e, string key) =>
        e.Extra?.TryGetValue(key, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static Part Quote(string s) => new($"«{s}»", true);

    static string FileName(string? path) => (path ?? "").Split('\\', '/').Last() is { Length: > 0 } n ? n : "";

    static List<Part> ListNames(IReadOnlyList<string> names)
    {
        if (names.Count == 1) return [Quote(names[0])];
        if (names.Count <= 3)
        {
            var parts = new List<Part>();
            for (int i = 0; i < names.Count - 1; i++)
            {
                if (i > 0) parts.Add(new(", "));
                parts.Add(Quote(names[i]));
            }
            parts.Add(new(T("act.and", " y ")));
            parts.Add(Quote(names[^1]));
            return parts;
        }
        return [new(T("act.nChannels", "{0} canales", names.Count), true)];
    }

    /// <summary>
    /// Port of buildActivity: the raw log as readable lines, newest first. Bursts of the same action (several
    /// takes taken off the timeline in a row, an undo that brings a few back…) collapse into one line, and so do
    /// runs of events about purged takes whose name was never logged (older projects).
    /// </summary>
    public static List<ActivityLine> BuildActivity(Project p)
    {
        var byId = p.Media.ToDictionary(m => m.Id, m => m.Name);
        string? TakeName(LogEntry e) => (Str(e, "takeId") is { } id && byId.TryGetValue(id, out var n) ? n : null) ?? e.Name ?? Str(e, "name");
        bool IsGhost(LogEntry e) => TakeEvents.Contains(e.Type) && TakeName(e) == null;

        var groups = new List<List<LogEntry>>();
        foreach (var e in p.Log.Where(e => !Silent(e.Type)))
        {
            var last = groups.Count > 0 ? groups[^1] : null;
            var prev = last?[^1];
            bool ghostRun = prev != null && IsGhost(prev) && IsGhost(e);
            bool burst = prev != null && !IsGhost(e) && (e.Type is "take-removed" or "take-restored") && prev.Type == e.Type && e.At - prev.At < MergeWindow;
            if (ghostRun || burst) last!.Add(e);
            else groups.Add([e]);
        }

        var lines = new List<ActivityLine>();
        foreach (var g in groups)
        {
            var e = g[^1];
            var at = e.At;
            if (IsGhost(e))
            {
                int recorded = g.Where(x => x.Type == "take-recorded").Select(x => Str(x, "takeId")).Distinct().Count();
                int touched = g.Select(x => Str(x, "takeId")).Distinct().Count();
                lines.Add(new(at, recorded > 0 ? "mic" : "minus", [new(recorded > 0
                    ? T("act.ghost.recorded", "Grabaste {0} que después borraste", Channels(recorded))
                    : T("act.ghost.moved", "Moviste {0} que después borraste", Channels(touched)))]));
                continue;
            }
            var names = g.Select(x => TakeName(x) ?? T("act.aChannel", "un canal")).Distinct().ToList();
            switch (e.Type)
            {
                case "session-created":
                    lines.Add(new(at, "plus", [new(T("act.created", "Creaste el proyecto"))]));
                    break;
                case "session-duplicated":
                    lines.Add(new(at, "plus", [new(T("act.duplicatedFrom", "Proyecto duplicado de ")), Quote(FileName(Str(e, "from")) is { Length: > 0 } f ? f : T("act.otherProject", "otro proyecto"))]));
                    break;
                case "session-renamed":
                    lines.Add(new(at, "folder", [new(T("act.renamed", "Renombraste el proyecto: ")), Quote(Str(e, "from") ?? ""), new(" → "), Quote(Str(e, "to") ?? "")]));
                    break;
                case "take-recorded":
                    lines.Add(new(at, "mic", [new(T("act.recorded", "Grabaste ")), Quote(names[0])]));
                    break;
                case "take-removed":
                    lines.Add(new(at, "minus", [new(T("act.removed", "Sacaste ")), .. ListNames(names), new(T("act.removed.end", " del timeline"))]));
                    break;
                case "take-restored":
                    lines.Add(new(at, "plus", [new(T("act.restored", "Volviste a usar ")), .. ListNames(names)]));
                    break;
                case "unused-purged":
                {
                    var purged = Strings(e, "names");
                    int n = Strings(e, "takeIds").Count;
                    var who = purged.Count > 0 ? ListNames(purged) : [new(Channels(n))];
                    lines.Add(new(at, "trash", [new(T("act.sent", "Mandaste ")), .. who, new(T("act.toTrash", " a la Papelera{0}", BytesSuffix(e)))], Rec: true));
                    break;
                }
                case "exported":
                    lines.Add(new(at, "film", [new(T("act.exported", "Exportaste ")), Quote(FileName(Str(e, "file") ?? e.Name) is { Length: > 0 } x ? x : T("act.aVideo", "un video"))]));
                    break;
                case "export-deleted":
                    lines.Add(new(at, "trash", [new(T("act.sent", "Mandaste ")), Quote(FileName(Str(e, "file")) is { Length: > 0 } d ? d : T("act.aVideo", "un video")), new(T("act.toTrash", " a la Papelera{0}", BytesSuffix(e)))], Rec: true));
                    break;
                // native-only entries
                case "imported-from-web":
                    lines.Add(new(at, "plus", [new(T("act.importedWeb", "Importaste el proyecto de la app web"))]));
                    break;
                case "snapshot-restored":
                    lines.Add(new(at, "history", [new(T("act.versionBack", "Volviste a la versión ")), Quote(e.Name ?? "")]));
                    break;
                case "recording-recovered":
                    lines.Add(new(at, "mic", [new(T("act.recovered", "Recuperé ")), Quote(e.Name ?? T("act.aChannel", "un canal")), new(T("act.recovered.end", " después de un corte"))]));
                    break;
                case "media-adopted":
                    lines.Add(new(at, "plus", [new(T("act.adopted", "Sumé archivos que estaban en media: ")), Quote(e.Name ?? "")]));
                    break;
                default:
                    lines.Add(new(at, "history", [new(e.Type)]));
                    break;
            }
        }
        lines.Reverse();
        return lines;
    }

    static string T(string key, string es, params object?[] args) => Texts.T(key, es, args);
    static string Channels(int n) => n == 1 ? T("act.oneChannel", "un canal") : T("act.nChannels", "{0} canales", n);

    static List<string> Strings(LogEntry e, string key) =>
        e.Extra?.TryGetValue(key, out var v) == true && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    static string BytesSuffix(LogEntry e) =>
        e.Extra?.TryGetValue("bytes", out var v) == true && v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0
            ? $" ({FormatBytes((long)v.GetDouble())})" : "";

    /// <summary>Port of formatBytes (sessionApi.ts).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{Math.Max(1, Math.Round(bytes / 1024.0))} KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>Port of fmtDuration: "12.3 s" under a minute, "m:ss" after.</summary>
    public static string FormatDuration(double sec)
    {
        if (sec < 60) return sec.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        int m = (int)Math.Floor(sec / 60);
        return $"{m}:{(int)Math.Round(sec % 60):00}";
    }

    /// <summary>Port of dayLabel: "Hoy", "Ayer" or "Martes, 3 de marzo" (with the year if it isn't this one).</summary>
    public static string DayLabel(DateTime local, DateTime? today = null)
    {
        var t = (today ?? DateTime.Now).Date;
        if (local.Date == t) return T("hist.today", "Hoy");
        if (local.Date == t.AddDays(-1)) return T("hist.yesterday", "Ayer");
        var es = Texts.Culture;
        string label = local.ToString(local.Year != t.Year ? T("hist.day.fmtYear", "dddd, d 'de' MMMM 'de' yyyy") : T("hist.day.fmt", "dddd, d 'de' MMMM"), es);
        return char.ToUpper(label[0], es) + label[1..];
    }

    /// <summary>Port of fmtAgo: "recién", "hace 5 min", "hace 2 h", or the day and time for anything older.</summary>
    public static string FormatAgo(DateTime utc, DateTime? nowUtc = null)
    {
        double sec = ((nowUtc ?? DateTime.UtcNow) - utc).TotalSeconds;
        if (sec < 60) return T("hist.ago.now", "recién");
        if (sec < 3600) return T("hist.ago.min", "hace {0} min", (int)(sec / 60));
        if (sec < 86400) return T("hist.ago.h", "hace {0} h", (int)(sec / 3600));
        var local = utc.ToLocalTime();
        return $"{DayLabel(local)} {local:HH:mm}";
    }
}
