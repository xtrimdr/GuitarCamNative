using GuitarCam.Audio;
using Dsp = GuitarCam.Audio.Dsp;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitarCam.Projects;

/// <summary>
/// A project, as saved in guitarcam.json. Three layers:
/// <see cref="Media"/> (recordings and imported files, never modified), <see cref="Track"/> (the channel
/// strip and whether it gets a video panel) and <see cref="Clip"/> (a piece of a media placed on a track).
/// Timeline times are in seconds; latency-type corrections stay in ms, like in the web app.
/// Fields the app doesn't know go to <c>Extra</c> and are written back untouched.
/// </summary>
public sealed class Project
{
    public const string FormatId = "guitarcam-project";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = Ids.New("prj");
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ProjectSettings Settings { get; set; } = new();
    public List<Media> Media { get; set; } = [];
    public List<Track> Tracks { get; set; } = [];
    /// <summary>Native only: folders that group tracks (<see cref="Folder"/>, <see cref="TrackFolders"/>).</summary>
    public List<Folder> Folders { get; set; } = [];
    public List<ExportRecord> Exports { get; set; } = [];
    public List<LogEntry> Log { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public Media? FindMedia(string id) => Media.Find(m => m.Id == id);

    public void AddLog(string type, string? name = null) => Log.Add(new LogEntry { Type = type, Name = name });

    /// <summary>Fills what a hand-edited or older file left out, so the rest of the app never sees nulls.</summary>
    internal void Normalize()
    {
        Settings ??= new();
        Settings.Normalize();
        Media ??= [];
        Tracks ??= [];
        Exports ??= [];
        Log ??= [];
        Exports.RemoveAll(e => e == null);
        foreach (var e in Exports) e.TakeIds ??= [];
        Media.RemoveAll(m => m == null);
        Tracks.RemoveAll(t => t == null);
        foreach (var t in Tracks) t.Normalize();
        TrackFolders.Normalize(this);
    }
}

public sealed class ProjectSettings
{
    /// <summary>110 like a new project in the web app.</summary>
    public double Bpm { get; set; } = 110;
    /// <summary>
    /// video | audio (native only). Audio hides the stage and the camera and exports the mix instead of an MP4;
    /// nothing is deleted, so switching back to video shows everything again.
    /// </summary>
    public string Mode { get; set; } = "video";
    public bool AudioOnly => Mode == "audio";
    /// <summary>side-by-side | top-to-bottom</summary>
    public string Layout { get; set; } = "side-by-side";
    /// <summary>"16:9", "9:16", "1:1", "4:5"…</summary>
    public string Aspect { get; set; } = "16:9";
    public ReverbSettings Reverb { get; set; } = new();
    public DelaySettings Delay { get; set; } = new();
    public ColorGrade Color { get; set; } = new();
    public VideoFx VideoFx { get; set; } = new();
    public ExportSettings Export { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public (double W, double H) AspectWH()
    {
        var p = (Aspect ?? "").Split(':');
        return p.Length == 2 && double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out var w)
            && double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0
            ? (w, h)
            : (16, 9);
    }

    /// <summary>App.tsx changeAspectRatio: vertical stacks the panels, horizontal puts them side by side (square keeps the layout).</summary>
    public void SetAspect(string aspect)
    {
        if (aspect is not ("16:9" or "9:16" or "1:1")) throw new ArgumentException(Texts.T("err.aspect", "La relación tiene que ser 16:9, 9:16 o 1:1."));
        Aspect = aspect;
        if (aspect == "9:16") Layout = "top-to-bottom";
        else if (aspect == "16:9") Layout = "side-by-side";
    }

    /// <summary>App.tsx changeLayout.</summary>
    public void SetLayout(string layout)
    {
        if (layout is not ("side-by-side" or "top-to-bottom")) throw new ArgumentException(Texts.T("err.layout", "La disposición tiene que ser side-by-side o top-to-bottom."));
        Layout = layout;
    }

    public void SetMode(string mode)
    {
        if (mode is not ("video" or "audio")) throw new ArgumentException("El modo tiene que ser video o audio.");
        Mode = mode;
    }

    internal void Normalize()
    {
        if (!(Bpm is >= 20 and <= 400)) Bpm = 110;
        Mode = Mode == "audio" ? Mode : "video";
        Layout = Layout == "top-to-bottom" ? Layout : "side-by-side";
        Aspect ??= "16:9";
        Reverb ??= new();
        Delay ??= new();
        (Color ??= new()).Normalize();
        (VideoFx ??= new()).Normalize();
        Export ??= new();
    }
}

public sealed class ReverbSettings
{
    public double Decay { get; set; } = 1.8;
    public double PreDelayMs { get; set; } = 20;
    public double Tone { get; set; } = 6000;
    public double LowCut { get; set; } = 150;
    public double Width { get; set; } = 1;
    public double ReturnGain { get; set; } = 0.8;
    public bool Muted { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DelaySettings
{
    /// <summary>stereo | pingpong</summary>
    public string Mode { get; set; } = "stereo";
    public bool Sync { get; set; } = true;
    public string Division { get; set; } = "1/8.";
    public double TimeMs { get; set; } = 375;
    public double Feedback { get; set; } = 0.35;
    public double Tone { get; set; } = 5000;
    public double LowCut { get; set; } = 250;
    public double Width { get; set; } = 1;
    public double Duck { get; set; } = 0.35;
    public double ReturnGain { get; set; } = 0.7;
    public bool Muted { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ColorGrade
{
    public bool On { get; set; } = true;
    public string Look { get; set; } = "none";
    public double Amount { get; set; } = 100;
    public double Exposure { get; set; }
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Saturation { get; set; }
    public double Vibrance { get; set; }
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Fade { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Same ranges as normalizeGrade (colorGrade.js): unknown looks become "none".</summary>
    public void Normalize()
    {
        static double N(double v, double lo, double d) => double.IsFinite(v) ? Math.Clamp(v, lo, 100) : d;
        Look = GuitarCam.Audio.VideoColorProcessor.IsKnownLook(Look) ? Look : "none";
        Amount = N(Amount, 0, 100);
        Exposure = N(Exposure, -100, 0); Contrast = N(Contrast, -100, 0);
        Highlights = N(Highlights, -100, 0); Shadows = N(Shadows, -100, 0);
        Saturation = N(Saturation, -100, 0); Vibrance = N(Vibrance, -100, 0);
        Temperature = N(Temperature, -100, 0); Tint = N(Tint, -100, 0);
        Fade = N(Fade, 0, 0);
    }
}

public sealed class VideoFx
{
    public bool On { get; set; } = true;
    public double Vignette { get; set; }
    public double VignetteSize { get; set; } = 50;
    public double Grain { get; set; }
    public string Letterbox { get; set; } = "off";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Same ranges as normalizeVideoFx (videoFx.js).</summary>
    public void Normalize()
    {
        static double N(double v, double d) => double.IsFinite(v) ? Math.Clamp(v, 0, 100) : d;
        Vignette = N(Vignette, 0); VignetteSize = N(VignetteSize, 50); Grain = N(Grain, 0);
        if (!GuitarCam.Audio.VideoColorProcessor.LetterboxOptions.Any(l => l.Id == Letterbox)) Letterbox = "off";
    }
}

public sealed class ExportSettings
{
    /// <summary>Last export folder; null = the project's exports folder.</summary>
    public string? Dir { get; set; }
    public string Quality { get; set; } = "alta";
    /// <summary>wav | mp3: the last "Exportar audio" format (native only).</summary>
    public string AudioFormat { get; set; } = "wav";
    /// <summary>Native only: each track that sounds also goes to its own WAV next to the export ("stems").</summary>
    public bool Stems { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A recording or an imported file. Its files are never rewritten; edits live in the clips.</summary>
public sealed class Media
{
    public string Id { get; set; } = Ids.New("med");
    public string Name { get; set; } = "";
    /// <summary>recording | import</summary>
    public string Kind { get; set; } = "recording";
    /// <summary>Relative to the project folder (media/…), or absolute for files left where they were.</summary>
    public string? Audio { get; set; }
    public string? Video { get; set; }
    public double DurationSec { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Latency correction applied when it was recorded: the recording's start is at clip.AtSec - SyncMs.</summary>
    public double SyncMs { get; set; }
    /// <summary>Camera latency: positive trims the start of the video.</summary>
    public double VideoOffsetMs { get; set; } = 80;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Track
{
    public string Id { get; set; } = Ids.New("trk");
    public string Name { get; set; } = "";
    /// <summary>False = audio only: the track gets no panel in the video.</summary>
    public bool Video { get; set; } = true;
    /// <summary>
    /// The driver inputs it records from when armed, "3" or "3-4" (<see cref="GuitarCam.Audio.InputRoute"/>);
    /// null = the input of Configuración. Native only.
    /// </summary>
    public string? Input { get; set; }
    /// <summary>The track's color as "#rrggbb", tinting its badge and clips; null = no color (the accent). Native only.</summary>
    public string? Color { get; set; }
    /// <summary>The track's instrument icon id ("electric-guitar", "drum-kit"…); null = its number. Native only.</summary>
    public string? Icon { get; set; }
    /// <summary>The <see cref="Folder"/> the track is in; null = at the root. Native only.</summary>
    public string? FolderId { get; set; }
    public Channel Channel { get; set; } = new();
    public List<Clip> Clips { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        Input = GuitarCam.Audio.InputRoute.Parse(Input)?.Key;
        Icon = string.IsNullOrWhiteSpace(Icon) || Icon.Length > 64 ? null : Icon.Trim();
        Color = NormalizeColor(Color);
        Channel ??= new();
        Channel.Normalize();
        Clips ??= [];
        Clips.RemoveAll(c => c == null);
        foreach (var c in Clips) c.Normalize();
    }

    /// <summary>"#rrggbb" in lower case, or null for anything else.</summary>
    internal static string? NormalizeColor(string? color) =>
        color is { Length: 7 } hex && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
}

/// <summary>
/// A folder of tracks (native only, asked by the user): its children are the tracks with its <see cref="Track.FolderId"/>,
/// always together in <see cref="Project.Tracks"/>, and the folder sits right before them. It folds in the timeline and
/// in the mixer, and its fader, mute and solo act on all of them like a VCA (<see cref="TrackFolders.Mix"/>): the
/// children keep their own channel strips.
/// </summary>
public sealed class Folder
{
    public string Id { get; set; } = Ids.New("fld");
    public string Name { get; set; } = "";
    /// <summary>"#rrggbb" or null (the accent), like <see cref="Track.Color"/>.</summary>
    public string? Color { get; set; }
    /// <summary>Folded in the timeline: only its own row shows.</summary>
    public bool Collapsed { get; set; }
    /// <summary>Folded in the mixer: only its own strip shows.</summary>
    public bool MixerCollapsed { get; set; }
    /// <summary>Multiplies the children's faders (0..1.5, like a track's).</summary>
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    /// <summary>
    /// How many tracks come before it in <see cref="Project.Tracks"/>. With children it is the first child's index
    /// (<see cref="TrackFolders.Normalize"/> keeps it so); an empty folder needs it to keep its place.
    /// </summary>
    public int Index { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The channel strip of a track: stays with the track when its clips change.</summary>
public sealed class Channel
{
    public double Volume { get; set; } = 1;
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public SatSettings Sat { get; set; } = new();
    public CompSettings Comp { get; set; } = new();
    public EqSettings Eq { get; set; } = new();
    public bool ReverbOn { get; set; } = true;
    public double ReverbSend { get; set; }
    public bool DelayOn { get; set; } = true;
    public double DelaySend { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        Sat ??= new();
        Comp ??= new();
        Comp.Eq ??= new();
        Eq ??= new();
        Eq.Normalize();
    }
}

public sealed class SatSettings
{
    public bool On { get; set; }
    /// <summary>tube | tape</summary>
    public string Mode { get; set; } = "tube";
    public double Amount { get; set; } = 0.3;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CompSettings
{
    public bool On { get; set; }
    /// <summary>A preset id of the web app (rhythm, lead…) or "custom".</summary>
    public string Preset { get; set; } = "rhythm";
    public double Amount { get; set; } = 0.5;
    public bool Limit { get; set; } = true;
    public double Threshold { get; set; } = -18;
    public double Ratio { get; set; } = 4;
    public double Attack { get; set; } = 0.01;
    public double Release { get; set; } = 0.15;
    public double Knee { get; set; } = 6;
    public double Makeup { get; set; }
    public CompEq Eq { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The small EQ around the compressor that comes with its presets.</summary>
public sealed class CompEq
{
    public double HpfHz { get; set; } = 10;
    public double MudDb { get; set; }
    public double PresenceDb { get; set; }
    public double PresenceHz { get; set; } = 2500;
    public double AirDb { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Track EQ: always 6 bands (low cut, 4 bells, high cut), any band can change type.</summary>
public sealed class EqSettings
{
    public bool On { get; set; }
    public List<EqBand> Bands { get; set; } = Defaults();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static List<EqBand> Defaults() =>
    [
        new() { Type = "highpass", Freq = 80, Q = 0.7 },
        new() { Type = "peaking", Freq = 250 },
        new() { Type = "peaking", Freq = 800 },
        new() { Type = "peaking", Freq = 2000 },
        new() { Type = "peaking", Freq = 4500 },
        new() { Type = "lowpass", Freq = 12000, Q = 0.7 },
    ];

    internal void Normalize()
    {
        Bands ??= [];
        Bands.RemoveAll(b => b == null);
        var d = Defaults();
        while (Bands.Count < d.Count) Bands.Add(d[Bands.Count]);
        if (Bands.Count > d.Count) Bands.RemoveRange(d.Count, Bands.Count - d.Count);
    }
}

public sealed class EqBand
{
    public bool On { get; set; }
    /// <summary>highpass | lowshelf | peaking | highshelf | lowpass</summary>
    public string Type { get; set; } = "peaking";
    public double Freq { get; set; } = 1000;
    public double Gain { get; set; }
    public double Q { get; set; } = 1;
    /// <summary>12 or 24 dB/oct, cuts only.</summary>
    public int Slope { get; set; } = 12;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A piece of a media on a track.</summary>
public sealed class Clip
{
    public string Id { get; set; } = Ids.New("clp");
    public string MediaId { get; set; } = "";
    /// <summary>Timeline time of the media's sample 0 (includes the sync correction).</summary>
    public double AtSec { get; set; }
    /// <summary>Optional per-clip correction; null inherits the immutable recording correction.</summary>
    public double? SyncMs { get; set; }
    /// <summary>Optional camera latency; null inherits the recording's camera latency.</summary>
    public double? VideoOffsetMs { get; set; }

    public double EffectiveSyncMs(Media? media) => SyncMs ?? media?.SyncMs ?? 0;
    public double EffectiveVideoOffsetMs(Media? media) => VideoOffsetMs ?? media?.VideoOffsetMs ?? 80;

    internal void Normalize()
    {
        Framing ??= new();
        if (SyncMs is { } sync && !double.IsFinite(sync)) SyncMs = null;
        if (VideoOffsetMs is { } video && !double.IsFinite(video)) VideoOffsetMs = null;
        if (!double.IsFinite(GainDb)) GainDb = 0;
        GainDb = Math.Clamp(GainDb, MinGainDb, MaxGainDb);
        GainCurve = (GainCurve ?? []).Where(g => g != null && double.IsFinite(g.Sec) && double.IsFinite(g.Db))
            .OrderBy(g => g.Sec).ToList();
        foreach (var g in GainCurve) g.Db = Math.Clamp(g.Db, Dsp.ClipGainCurve.MinDb, Dsp.ClipGainCurve.MaxDb);
    }

    /// <summary>Range of <see cref="GainDb"/> (the inspector's slider).</summary>
    public const double MinGainDb = -24, MaxGainDb = 12;

    /// <summary>The curve as the audio thread reads it: a new array, never changed afterwards.</summary>
    public Dsp.ClipGainCurve.Point[] CurvePoints() => GainCurve.Select(g => new Dsp.ClipGainCurve.Point(g.Sec, g.Db)).ToArray();
    /// <summary>Hidden from the start / end of the media, without moving the rest.</summary>
    public double TrimStartSec { get; set; }
    public double TrimEndSec { get; set; }
    public double FadeInSec { get; set; }
    public double FadeOutSec { get; set; }
    /// <summary>The video fades with the audio fades; otherwise with its own times.</summary>
    public bool LinkVideoFades { get; set; } = true;
    public double VideoFadeInSec { get; set; }
    public double VideoFadeOutSec { get; set; }
    /// <summary>Clip gain, before the track's channel strip.</summary>
    public double GainDb { get; set; }
    /// <summary>
    /// Native only: the clip's volume curve on top of <see cref="GainDb"/> (<see cref="Dsp.ClipGainCurve"/>), in
    /// seconds from the media's sample 0. Empty = flat. Edits assign a new list (the audio thread reads a copy).
    /// </summary>
    public List<GainPoint> GainCurve { get; set; } = [];
    public Framing Framing { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A point of a clip's volume curve: seconds from the media's sample 0 and dB (<see cref="Dsp.ClipGainCurve"/>).</summary>
public sealed class GainPoint
{
    public double Sec { get; set; }
    public double Db { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>How the video sits in its panel: zoom around a pan point (-1..1), optional mirror.</summary>
public sealed class Framing
{
    public double Zoom { get; set; } = 1;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public bool Mirror { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ExportRecord
{
    public string File { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Layout { get; set; }
    public string? Aspect { get; set; }
    public string? Quality { get; set; }
    public double DurationSec { get; set; }
    public long Bytes { get; set; }
    /// <summary>The takes in the video (media ids), shown as "n canales" in Historial.</summary>
    public List<string> TakeIds { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One line of the project's activity log.</summary>
public sealed class LogEntry
{
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public static class Ids
{
    public static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
}
