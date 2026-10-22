using System.Text.Json;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App.Model;

/// <summary>One take of the timeline, as the web app saves it in session.json.</summary>
public sealed class Take
{
    public string Id = "";
    public string Name = "";
    public int Order;
    public string AudioFile = "";
    public string VideoFile = "";
    public double DurationSec;
    public double AudioOffsetMs;
    public double SyncMs;
    public double VideoOffsetMs = 80;
    public double TrimStartSec, TrimEndSec;
    public double Zoom = 1, PanX, PanY;
    public bool Mirror;
    public float Volume = 1;
    public float Pan;
    public bool Muted, Solo;
    public double ReverbSend;
    public bool SatOn;
    public SatMode SatMode;
    public double SatAmount = 0.3;
    public bool CompOn;
    public string CompPreset = "custom";
    public double CompAmount = 0.5;
    public double Threshold = -18, Ratio = 4, Attack = 0.01, Release = 0.15, Knee = 6, Makeup;
    public bool Limit;
}

public sealed class Session
{
    public string Dir = "";
    public string Name = "";
    public double Bpm = 120;
    public string Layout = "side-by-side";
    public double AspectW = 16, AspectH = 9;
    public List<Take> Takes = [];

    /// <summary>The most recently opened project in the web app (~/.guitarcam/recent-sessions.json).</summary>
    public static string? MostRecentDir()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".guitarcam", "recent-sessions.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("dir").GetString())
                .FirstOrDefault(d => d != null && File.Exists(Path.Combine(d, "session.json")));
        }
        catch
        {
            return null;
        }
    }

    public static Session Load(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "session.json")));
        var root = doc.RootElement;
        var s = new Session { Dir = dir, Name = Str(root, "name") ?? Path.GetFileName(dir) };
        if (root.TryGetProperty("settings", out var st))
        {
            s.Bpm = Num(st, "bpm", 120);
            s.Layout = Str(st, "layout") ?? "side-by-side";
            var ar = (Str(st, "aspectRatio") ?? "16:9").Split(':');
            if (ar.Length == 2 && double.TryParse(ar[0], out var w) && double.TryParse(ar[1], out var h))
                (s.AspectW, s.AspectH) = (w, h);
        }
        foreach (var t in root.GetProperty("takes").EnumerateArray())
        {
            if (!Bool(t, "inTimeline", true)) continue;
            var take = new Take
            {
                Id = Str(t, "id") ?? "",
                Name = Str(t, "name") ?? $"Toma {Num(t, "number", 0)}",
                Order = (int)Num(t, "order", 0),
                AudioFile = Str(t, "audioFile") ?? "",
                VideoFile = Str(t, "videoFile") ?? "",
                DurationSec = Num(t, "durationSec", 0),
                AudioOffsetMs = Num(t, "audioOffsetMs", 0),
                SyncMs = Num(t, "syncMs", 0),
                VideoOffsetMs = Num(t, "videoOffsetMs", 80),
                TrimStartSec = Num(t, "trimStartSec", 0),
                TrimEndSec = Num(t, "trimEndSec", 0),
                Zoom = Num(t, "zoom", 1),
                PanX = Num(t, "panX", 0),
                PanY = Num(t, "panY", 0),
                Mirror = Bool(t, "mirror", false),
                Volume = (float)Num(t, "volume", 1),
                Pan = (float)Num(t, "pan", 0),
                Muted = Bool(t, "muted", false),
                Solo = Bool(t, "solo", false),
                ReverbSend = Num(t, "reverbSend", 0),
            };
            if (t.TryGetProperty("sat", out var sat) && sat.ValueKind == JsonValueKind.Object)
            {
                take.SatOn = Bool(sat, "on", false);
                take.SatMode = Str(sat, "mode") == "tape" ? SatMode.Tape : SatMode.Tube;
                take.SatAmount = Num(sat, "amount", 0.3);
            }
            if (t.TryGetProperty("comp", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                take.CompOn = Bool(c, "on", false);
                take.CompPreset = Str(c, "preset") ?? "custom";
                take.CompAmount = Num(c, "amount", 0.5);
                take.Threshold = Num(c, "threshold", -18);
                take.Ratio = Num(c, "ratio", 4);
                take.Attack = Num(c, "attack", 0.01);
                take.Release = Num(c, "release", 0.15);
                take.Knee = Num(c, "knee", 6);
                take.Makeup = Num(c, "makeup", 0);
                take.Limit = Bool(c, "limit", false);
            }
            s.Takes.Add(take);
        }
        s.Takes.Sort((a, b) => a.Order.CompareTo(b.Order));
        return s;
    }

    static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double Num(JsonElement e, string key, double fallback) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    static bool Bool(JsonElement e, string key, bool fallback) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
}
