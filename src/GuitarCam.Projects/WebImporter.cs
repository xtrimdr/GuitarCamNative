using System.Text.Json;

namespace GuitarCam.Projects;

/// <summary>
/// Turns a project of the web app (session.json + takes/) into a native one in a new folder.
/// The web project is only read. Media files are hard-linked when both folders are on the same drive
/// (instant, no extra space; media is never rewritten, so sharing the file is safe) and copied otherwise.
/// Every take comes along, unused ones as media without a clip.
/// </summary>
public static class WebImporter
{
    public sealed record Result(ProjectStore Store, int Linked, int Copied, List<string> Missing);

    public static bool IsWebProject(string dir) => File.Exists(Path.Combine(dir, "session.json"));

    /// <summary>"Name (nativa)" next to the web project, or the first free "(nativa 2)"…</summary>
    public static string SuggestTarget(string webDir)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(webDir).TrimEnd('\\', '/')) ?? ".";
        string name = Path.GetFileName(Path.GetFullPath(webDir).TrimEnd('\\', '/'));
        string path = Path.Combine(parent, $"{name} (nativa)");
        for (int i = 2; Directory.Exists(path); i++) path = Path.Combine(parent, $"{name} (nativa {i})");
        return path;
    }

    public static Result Import(string webDir, string targetDir, IProgress<string>? progress = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(webDir, "session.json")));
        var root = doc.RootElement;
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            throw new IOException("La carpeta de destino no está vacía");

        var p = new Project
        {
            Name = Str(root, "name") ?? Path.GetFileName(webDir),
            CreatedAt = Date(root, "createdAt") ?? DateTime.UtcNow,
        };
        var st = Obj(root, "settings");
        if (st is { } s)
        {
            p.Settings.Bpm = Num(s, "bpm", 110);
            p.Settings.Layout = Str(s, "layout") ?? "side-by-side";
            p.Settings.Aspect = Str(s, "aspectRatio") ?? "16:9";
            p.Settings.Reverb = As<ReverbSettings>(s, "reverb") ?? new();
            p.Settings.Delay = As<DelaySettings>(s, "delay") ?? new();
            p.Settings.Color = As<ColorGrade>(s, "color") ?? new();
            p.Settings.VideoFx = As<VideoFx>(s, "videoFx") ?? new();
            p.Settings.Export.Dir = Str(s, "exportDir");
            p.Settings.Export.Quality = Str(s, "exportQuality") ?? "alta";
        }

        Directory.CreateDirectory(Path.Combine(targetDir, ProjectStore.MediaDir));
        int linked = 0, copied = 0;
        var missing = new List<string>();
        string? Bring(string? rel, string takeName)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            string src = Path.Combine(webDir, rel);
            string file = Path.GetFileName(rel);
            string dst = Path.Combine(targetDir, ProjectStore.MediaDir, file);
            if (!File.Exists(src))
            {
                missing.Add($"{takeName}: {rel}");
                return $"{ProjectStore.MediaDir}/{file}"; // stays offline, the clip keeps its place
            }
            progress?.Report(file);
            if (FileLinks.TryHardLink(dst, src)) linked++;
            else
            {
                File.Copy(src, dst);
                copied++;
            }
            return $"{ProjectStore.MediaDir}/{file}";
        }

        // lanes: timeline takes and empty tracks, by their order
        var lanes = new List<(double Order, Track Track)>();
        foreach (var t in Arr(root, "takes"))
        {
            string id = Str(t, "id") ?? Ids.New("med");
            string name = Str(t, "name") ?? $"Toma {Num(t, "number", 0)}";
            string? audio = Bring(Str(t, "audioFile"), name);
            string? video = Bring(Str(t, "videoFile"), name);
            var media = new Media
            {
                Id = id,
                Name = name,
                Kind = "recording",
                Audio = audio,
                Video = video,
                DurationSec = Num(t, "durationSec", 0),
                CreatedAt = Date(t, "createdAt") ?? p.CreatedAt,
                SyncMs = SyncOf(t),
                VideoOffsetMs = Num(t, "videoOffsetMs", 80),
            };
            if (audio != null && File.Exists(Path.Combine(targetDir, audio)))
            {
                try
                {
                    var info = Audio.WavFile.ReadInfo(Path.Combine(targetDir, audio));
                    media.SampleRate = info.SampleRate;
                    media.Channels = info.Channels;
                    if (info.DurationSec > 0) media.DurationSec = info.DurationSec;
                }
                catch (InvalidDataException)
                {
                }
            }
            p.Media.Add(media);
            var track = new Track { Name = name, Channel = ChannelOf(t) };
            var clip = new Clip
            {
                MediaId = id,
                AtSec = Num(t, "audioOffsetMs", 0) / 1000,
                TrimStartSec = Num(t, "trimStartSec", 0),
                TrimEndSec = Num(t, "trimEndSec", 0),
                FadeInSec = Num(t, "fadeInSec", 0),
                FadeOutSec = Num(t, "fadeOutSec", 0),
                LinkVideoFades = Bool(t, "linkVideoFades", true),
                VideoFadeInSec = Num(t, "videoFadeInSec", 0),
                VideoFadeOutSec = Num(t, "videoFadeOutSec", 0),
                Framing = new Framing
                {
                    Zoom = Num(t, "zoom", 1),
                    PanX = Num(t, "panX", 0),
                    PanY = Num(t, "panY", 0),
                    Mirror = Bool(t, "mirror", false),
                },
            };
            if (Str(t, "role") is { } role)
                clip.Extra = new() { ["role"] = JsonSerializer.SerializeToElement(role) };
            media.Extra ??= [];
            media.Extra["lastTimelineClip"] = JsonSerializer.SerializeToElement(clip, ProjectStore.Json);
            media.Extra["lastTimelineChannel"] = JsonSerializer.SerializeToElement(track.Channel, ProjectStore.Json);
            if (!Bool(t, "inTimeline", true)) continue;
            track.Clips.Add(clip);
            lanes.Add((Num(t, "order", 0), track));
        }
        if (st is { } s2)
            foreach (var e in Arr(s2, "emptyTracks"))
            {
                var ch = Obj(e, "channel");
                lanes.Add((Num(e, "order", 0), new Track
                {
                    Id = Str(e, "id") ?? Ids.New("trk"),
                    Name = Str(e, "name") ?? "Pista",
                    Channel = ch is { } c ? ChannelOf(c) : new(),
                }));
            }
        p.Tracks.AddRange(lanes.OrderBy(l => l.Order).Select(l => l.Track));

        foreach (var e in Arr(root, "exports"))
        {
            string? file = Str(e, "file");
            if (file == null) continue;
            p.Exports.Add(new ExportRecord
            {
                // the videos stay in the web project
                File = Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(webDir, file)),
                CreatedAt = Date(e, "createdAt") ?? p.CreatedAt,
                Layout = Str(e, "layout"),
                Aspect = Str(e, "aspectRatio"),
                Quality = Str(e, "quality"),
                DurationSec = Num(e, "durationSec", 0),
                Bytes = (long)Num(e, "bytes", 0),
                TakeIds = Arr(e, "takeIds").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList(),
            });
        }
        foreach (var e in Arr(root, "history"))
        {
            var entry = e.Deserialize<LogEntry>(ProjectStore.Json);
            if (entry != null) p.Log.Add(entry);
        }
        p.Log.Add(new LogEntry
        {
            Type = "imported-from-web",
            Name = p.Name,
            Extra = new() { ["from"] = JsonSerializer.SerializeToElement(Path.GetFullPath(webDir)) },
        });

        var store = ProjectStore.Create(targetDir, p);
        Directory.CreateDirectory(Path.Combine(targetDir, "exports"));
        RecentProjects.Touch(targetDir, p.Name);
        return new Result(store, linked, copied, missing);
    }

    /// <summary>The web app saved syncMs only on newer takes; older ones get it from a small offset.</summary>
    static double SyncOf(JsonElement t)
    {
        if (t.TryGetProperty("syncMs", out var v) && v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        double off = Num(t, "audioOffsetMs", 0);
        return Math.Abs(off) <= 250 ? off : 0;
    }

    static Channel ChannelOf(JsonElement t)
    {
        var c = new Channel
        {
            Volume = Num(t, "volume", 1),
            Pan = Num(t, "pan", 0),
            Muted = Bool(t, "muted", false),
            Solo = Bool(t, "solo", false),
            Sat = As<SatSettings>(t, "sat") ?? new(),
            Comp = As<CompSettings>(t, "comp") ?? new(),
            Eq = As<EqSettings>(t, "eq") ?? new(),
            ReverbOn = Bool(t, "reverbOn", true),
            ReverbSend = Num(t, "reverbSend", 0),
            DelayOn = Bool(t, "delayOn", true),
            DelaySend = Num(t, "delaySend", 0),
        };
        // takes saved before compressor presets: "Manual" if it was on (loadComp in the web app)
        if (Obj(t, "comp") is { } comp && !comp.TryGetProperty("preset", out _)) c.Comp.Preset = "custom";
        c.Normalize();
        return c;
    }

    static T? As<T>(JsonElement e, string key) where T : class =>
        Obj(e, key) is { } o ? o.Deserialize<T>(ProjectStore.Json) : null;

    static JsonElement? Obj(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    static IEnumerable<JsonElement> Arr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double Num(JsonElement e, string key, double fallback) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    static bool Bool(JsonElement e, string key, bool fallback) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    static DateTime? Date(JsonElement e, string key) =>
        Str(e, key) is { } s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
