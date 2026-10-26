using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.Audio;

public enum ExportQuality
{
    Alta,
    Estandar,
    Borrador,
}

public enum ExportStage
{
    Mix,
    Upload,
    Video,
    Finalize,
}

public sealed record ExportQualitySpec(
    ExportQuality Value,
    string Id,
    string Label,
    string Title,
    int Crf,
    string Preset,
    double Mbps);

public sealed record ExportCheck(
    string Folder,
    string FileName,
    string FullPath,
    bool Exists,
    string Suggested,
    bool FolderExists,
    bool IsDefault);

public sealed record RenderProgress(
    ExportStage Stage,
    double Progress,
    double? Time = null,
    double? Duration = null,
    double? Fps = null,
    double? Speed = null,
    double? EtaSec = null);

public sealed record ExportProgress(
    ExportStage Stage,
    double Mix,
    double Upload,
    RenderProgress? Render,
    double ElapsedSec)
{
    // Share of the whole job each stage takes (ExportDialog.tsx:355).
    public const double WeightMix = 0.12;
    public const double WeightUpload = 0.03;
    public const double WeightVideo = 0.83;
    public const double WeightFinalize = 0.02;

    public double StageFraction => Stage switch
    {
        ExportStage.Mix => Mix,
        ExportStage.Upload => Upload,
        ExportStage.Video => Render?.Progress ?? 0,
        _ => 0,
    };

    /// <summary>Port of <c>overallProgress</c> in <c>ExportDialog.tsx:361-369</c>.</summary>
    public double Overall
    {
        get
        {
            double total = Stage switch
            {
                ExportStage.Mix => WeightMix * Mix,
                ExportStage.Upload => WeightMix + WeightUpload * Upload,
                ExportStage.Video => WeightMix + WeightUpload + WeightVideo * (Render?.Progress ?? 0),
                ExportStage.Finalize => WeightMix + WeightUpload + WeightVideo + WeightFinalize * (Render?.Progress ?? 0),
                _ => 0,
            };
            return Math.Clamp(total, 0, 1);
        }
    }

    public int Percent => (int)Math.Floor(Overall * 100.0);

    public double? EtaSec
    {
        get
        {
            if (Stage == ExportStage.Video && Render?.EtaSec is { } rEta)
                return rEta * (1.0 + WeightFinalize);
            double ov = Overall;
            if (ov > 0.05 && ov < 1.0)
                return (ElapsedSec * (1.0 - ov)) / ov;
            return null;
        }
    }
}

public sealed record ExportTakeSpec
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public required string VideoPath { get; init; }
    public double DurationSec { get; init; }
    /// <summary>Timeline time of sample 0 in seconds (corresponds to <c>audioOffsetMs / 1000</c> in the web app).</summary>
    public double AtSec { get; init; }
    /// <summary>Effective per-clip sync correction in ms (<c>clip.EffectiveSyncMs(media)</c>).</summary>
    public double SyncMs { get; init; }
    /// <summary>Effective per-clip camera latency in ms (<c>clip.EffectiveVideoOffsetMs(media)</c>).</summary>
    public double VideoOffsetMs { get; init; } = 80;
    public double TrimStartSec { get; init; }
    public double TrimEndSec { get; init; }
    public double FadeInSec { get; init; }
    public double FadeOutSec { get; init; }
    public bool LinkVideoFades { get; init; } = true;
    public double VideoFadeInSec { get; init; }
    public double VideoFadeOutSec { get; init; }
    public double Zoom { get; init; } = 1;
    public double PanX { get; init; }
    public double PanY { get; init; }
    public bool Mirror { get; init; }
    /// <summary>
    /// The panel this clip shows in; -1 = a panel of its own (one take per panel, like the web). Several clips
    /// of a track share its panel (native only), each one shown during its own window.
    /// </summary>
    public int Panel { get; init; } = -1;
    /// <summary>Native only: how the picture comes into this clip from the one before it in its panel (<see cref="ClipTransitions"/>).</summary>
    public string Transition { get; init; } = ClipTransitions.CutId;
    public double TransitionSec { get; init; } = ClipTransitions.DefaultSec;

    public ClipEnvelope.ClipWindow Window =>
        ClipEnvelope.Window(DurationSec, AtSec, TrimStartSec, TrimEndSec, SyncMs);
}

public sealed record ExportColorSpec(
    bool On = true,
    string Look = "none",
    double Amount = 100,
    double Exposure = 0,
    double Contrast = 0,
    double Highlights = 0,
    double Shadows = 0,
    double Saturation = 0,
    double Vibrance = 0,
    double Temperature = 0,
    double Tint = 0,
    double Fade = 0,
    double Whites = 0,
    double Blacks = 0,
    double Light = 0)
{
    public static readonly ExportColorSpec Defaults = new();
}

public sealed record ExportVideoFxSpec(
    bool On = true,
    double Vignette = 0,
    double VignetteSize = 50,
    double Grain = 0,
    string Letterbox = "off",
    double LetterboxHeight = VideoColorProcessor.LetterboxHeightDefault,
    double Sharpness = 0,
    double Clarity = 0,
    double Glow = 0)
{
    public static readonly ExportVideoFxSpec Defaults = new();
}

public sealed record ExportJobSpec
{
    public required string ProjectDir { get; init; }
    public string ProjectName { get; init; } = "GuitarCam";
    public string? OutDir { get; init; }
    public string? FileName { get; init; }
    public bool Overwrite { get; init; }
    public ExportQuality Quality { get; init; } = ExportQuality.Alta;
    public string Layout { get; init; } = "side-by-side";
    public string AspectRatio { get; init; } = "16:9";
    /// <summary>The gap around each panel, in pixels of a 1080p export: it grows with <see cref="Resolution"/> so the
    /// frame looks the same at any size (and like the preview).</summary>
    public int BorderSize { get; init; } = 4;
    /// <summary>Native only: the frame's short side ("Avanzado", one of <see cref="Mp4Exporter.Resolutions"/>).</summary>
    public int Resolution { get; init; } = Mp4Exporter.DefaultResolution;
    /// <summary>Native only: the video bitrate in Mbps ("Avanzado"); null = the quality's (x264's CRF, or the card's bitrate).</summary>
    public double? BitrateMbps { get; init; }
    public ExportColorSpec Color { get; init; } = ExportColorSpec.Defaults;
    public ExportVideoFxSpec VideoFx { get; init; } = ExportVideoFxSpec.Defaults;
    public required IReadOnlyList<ExportTakeSpec> Takes { get; init; }
    public required IReadOnlyList<MixTrackSpec> MixTracks { get; init; }
    public required MixBusSpec MixBus { get; init; }
    public int SampleRate { get; init; } = 48000;
    /// <summary>How long the reverb and delay may ring after the last clip (<see cref="MixMaster"/>); 0 = cut there.</summary>
    public double TailSec { get; init; } = MixMaster.MaxTailSec;
    /// <summary>Integrated loudness the mix is mastered to (a social preset, <see cref="ExportTargets"/>); null = as it is.</summary>
    public double? TargetLufs { get; init; }
    /// <summary>Native only: who encodes the video, a <see cref="VideoEncoders.ParsePreference"/> id ("auto" = the card if it has one).</summary>
    public string Encoder { get; init; } = VideoEncoders.Auto;
}

public sealed record ExportResult(
    string FileName,
    string File,
    string FullPath,
    string Folder,
    bool IsDefaultFolder,
    long Bytes,
    double DurationSec,
    double ElapsedSec,
    double Lufs = double.NegativeInfinity,
    double TailSec = 0,
    VideoEncoderKind? Encoder = null);

public sealed class ExportException : Exception
{
    public string? Details { get; }
    public bool Exists { get; }

    public ExportException(string message, string? details = null, bool exists = false) : base(message)
    {
        Details = details;
        Exists = exists;
    }
}

/// <summary>
/// Port of the MP4 export pipeline (<c>GuitarCam/server/index.js</c>, <c>src/session/exportApi.ts</c>,
/// <c>src/components/ExportDialog.tsx</c>, <c>src/shared/panelLayout.js</c>, <c>src/shared/colorGrade.js</c>,
/// and <c>src/shared/videoFx.js</c>):
/// renders the audio mix offline via <see cref="MixRenderer"/> (same DSP graph as live playback, without
/// master limiter look-ahead delay) and masters it with <see cref="MixMaster"/> (effect tails, loudness of a social
/// preset), builds the exact single-pass FFmpeg <c>filter_complex</c> (panel layout,
/// per-clip <c>SyncMs</c>/<c>VideoOffsetMs</c> alignment, 3D LUT <c>.cube</c>, grain, mirror, cover-scale + crop
/// framing, vignette multiply mask, letterbox bars, <c>tpad</c> hold/delay, video fades and overlay; one filtergraph
/// per take when FFmpeg can, so the takes filter in parallel), encodes with the video card's H.264 encoder or
/// <c>libx264</c> (<see cref="VideoEncoders"/>) + <c>aac 320k</c> into a hidden <c>.part</c> file while parsing <c>-progress pipe:1</c>,
/// and atomically renames to <c>.mp4</c> on completion (or kills FFmpeg and removes <c>.part</c> on cancel/error).
/// </summary>
public static partial class Mp4Exporter
{
    public const int LutSize = 33;

    public static readonly IReadOnlyList<ExportQualitySpec> QualityOptions =
    [
        new(ExportQuality.Alta, "alta", "Alta", "Máxima calidad (CRF 18). Para publicar.", 18, "fast", 12.0),
        new(ExportQuality.Estandar, "estandar", "Estándar", "Muy buena calidad y archivo más liviano (CRF 22).", 22, "fast", 6.0),
        new(ExportQuality.Borrador, "borrador", "Borrador", "Rápido y liviano, para revisar (CRF 28).", 28, "veryfast", 2.5),
    ];

    public static ExportQualitySpec GetQualitySpec(ExportQuality q) => q switch
    {
        ExportQuality.Estandar => QualityOptions[1],
        ExportQuality.Borrador => QualityOptions[2],
        _ => QualityOptions[0],
    };

    public static ExportQuality ParseQuality(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "estandar" or "estándar" or "standard" => ExportQuality.Estandar,
        "borrador" or "draft" => ExportQuality.Borrador,
        _ => ExportQuality.Alta,
    };

    public static string QualityToId(ExportQuality q) => GetQualitySpec(q).Id;

    /// <summary>The frame's short side the export can pick (720p, 1080p, 1440p, 4K); 1080 is the one without "Avanzado".</summary>
    public static readonly IReadOnlyList<int> Resolutions = [720, 1080, 1440, 2160];
    public const int DefaultResolution = 1080;
    /// <summary>Range of the custom bitrate: under 1 Mbps a 720p video is mush, over 100 no platform keeps it.</summary>
    public const double MinBitrateMbps = 1, MaxBitrateMbps = 100;

    /// <summary>One of <see cref="Resolutions"/> (anything else = 1080).</summary>
    public static int NormalizeResolution(int shortSide) =>
        Resolutions.Contains(shortSide) ? shortSide : DefaultResolution;

    /// <summary>A custom bitrate in range, in whole Mbps, or null (= the quality's).</summary>
    public static double? NormalizeBitrate(double? mbps) =>
        mbps is { } m && double.IsFinite(m) && m > 0 ? Math.Clamp(Math.Round(m), MinBitrateMbps, MaxBitrateMbps) : null;

    /// <summary>The frame for a relation at a short side (1080 = 1920×1080, 1080×1920, 1080×1080, 1080×1350); even sizes,
    /// as H.264's 4:2:0 needs.</summary>
    public static (int Width, int Height) ExportSize(string? aspectRatio, int shortSide = DefaultResolution)
    {
        var (w, h) = aspectRatio switch
        {
            "9:16" => (1080, 1920),
            "1:1" => (1080, 1080),
            "4:5" => (1080, 1350),
            _ => (1920, 1080),
        };
        int s = NormalizeResolution(shortSide);
        if (s == DefaultResolution) return (w, h);
        int Even(int v) => (int)Math.Round(v * s / (double)DefaultResolution / 2) * 2;
        return (Even(w), Even(h));
    }

    /// <summary>The video bitrate the card's encoders (and x264 with a custom one) aim at: <paramref name="customMbps"/>,
    /// or the quality's for 1080p scaled by the frame's pixels.</summary>
    public static double VideoMbps(ExportQuality quality, int width, int height, double? customMbps = null) =>
        NormalizeBitrate(customMbps) ?? GetQualitySpec(quality).Mbps * (width * height) / (1920.0 * 1080.0);

    /// <summary>Estimated MP4 size in bytes (<c>ExportDialog.tsx:214-215</c>).</summary>
    public static double EstimateBytes(ExportQuality quality, int width, int height, double durationSec, double? customMbps = null) =>
        ((VideoMbps(quality, width, height, customMbps) + 0.32) * 1e6 * Math.Max(0, durationSec)) / 8.0;

    /// <summary>Port of <c>formatBytes</c> in <c>sessionApi.ts:283-287</c>.</summary>
    public static string FormatBytes(double bytes)
    {
        if (bytes < 1024 * 1024)
            return $"{Math.Max(1, (long)Math.Round(bytes / 1024.0, MidpointRounding.AwayFromZero))} KB";
        if (bytes < 1024.0 * 1024.0 * 1024.0)
            return $"{(bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture)} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00", CultureInfo.CurrentCulture)} GB";
    }

    /// <summary>Port of <c>fmtTime</c> in <c>ExportDialog.tsx:65-69</c> (<c>m:ss</c>).</summary>
    public static string FormatTime(double sec)
    {
        int s = Math.Max(0, (int)Math.Round(sec, MidpointRounding.AwayFromZero));
        int m = s / 60;
        return $"{m}:{(s % 60):00}";
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex BadCharsRegex();

    [GeneratedRegex(@"^(con|prn|aux|nul|com\d|lpt\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedNameRegex();

    /// <summary>Port of <c>sessions.folderNameFor</c> (<c>server/sessions.js</c>).</summary>
    public static string SafeFileName(string? name, string fallback = "GuitarCam")
    {
        string raw = (name ?? "").Trim();
        if (raw.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^4].Trim();
        string clean = Regex.Replace(BadCharsRegex().Replace(raw, " "), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (ReservedNameRegex().IsMatch(clean)) clean += "_";
        if (clean.Length > 80) clean = clean[..80].TrimEnd('.', ' ');
        return clean.Length == 0 ? fallback : clean;
    }

    /// <summary>
    /// Port of <c>exportTarget</c> and <c>/api/export/check</c> (<c>server/index.js:109-137</c>):
    /// resolves the destination folder and <c>.mp4</c> file name, checks whether it already exists,
    /// and computes the first free <c>"name (n)"</c> suggestion. <paramref name="ext"/> is the native audio export's
    /// <c>.wav</c> / <c>.mp3</c>.
    /// </summary>
    public static ExportCheck CheckDestination(string projectDir, string? outDir, string? fileName, string ext = ".mp4")
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            throw new ArgumentException("Falta la carpeta del proyecto.");
        string dir = Path.GetFullPath(projectDir);
        string rawOut = (outDir ?? "").Trim();
        if (rawOut.Length > 0 && !Path.IsPathRooted(rawOut))
            throw new ExportException(Texts.T("err.export.badDir", "La carpeta de destino no es válida."));

        bool isDefault = rawOut.Length == 0;
        string folder = isDefault ? Path.Combine(dir, "exports") : Path.GetFullPath(rawOut);
        string baseName = SafeFileName(fileName, "GuitarCam");
        string full = Path.Combine(folder, $"{baseName}{ext}");
        string suggested = baseName;
        for (int i = 2; File.Exists(Path.Combine(folder, $"{suggested}{ext}")); i++)
            suggested = $"{baseName} ({i})";

        bool exists = File.Exists(full);
        bool folderExists = isDefault || Directory.Exists(folder);
        return new ExportCheck(folder, baseName, full, exists, suggested, folderExists, isDefault);
    }

    // --- Color grade & Video FX (shared via VideoColorProcessor) ---------------------------------

    public static ExportColorSpec NormalizeGrade(ExportColorSpec? g) => VideoColorProcessor.NormalizeGrade(g);
    public static bool IsNeutralGrade(ExportColorSpec? g) => VideoColorProcessor.IsNeutralGrade(g);
    public static float[] BuildLut(ExportColorSpec? g, int size = LutSize) => VideoColorProcessor.BuildLut(g, size);
    public static string LutToCube(ExportColorSpec? g, int size = LutSize) => VideoColorProcessor.LutToCube(g, size);
    public static ExportVideoFxSpec NormalizeVideoFx(ExportVideoFxSpec? fx) => VideoColorProcessor.NormalizeVideoFx(fx);
    public static bool HasVignette(ExportVideoFxSpec? fx) => VideoColorProcessor.HasVignette(fx);
    public static int GrainLevels(ExportVideoFxSpec? fx) => VideoColorProcessor.GrainLevels(fx);
    public static int LetterboxBar(ExportVideoFxSpec? fx, int w, int h) => VideoColorProcessor.LetterboxBar(fx, w, h);
    public static byte[] VignetteMask(ExportVideoFxSpec? fx, int w, int h) => VideoColorProcessor.VignetteMask(fx, w, h);

    // --- FFmpeg filter_complex & arguments (port of server/index.js:201-379) ---------------------

    /// <summary>How many panels the video has: one per take, or one per track when its clips share a panel.</summary>
    public static int PanelCount(IReadOnlyList<ExportTakeSpec> takes) =>
        takes.Select((t, i) => t.Panel >= 0 ? t.Panel : i).Distinct().Count();

    static string F3(double v) => Math.Max(0, v).ToString("0.000", CultureInfo.InvariantCulture);

    public sealed record BuiltExportCommand(
        IReadOnlyList<string> FfmpegArgs,
        string FilterComplex,
        double MaxDurationSec,
        int OutWidth,
        int OutHeight,
        IReadOnlyList<string> TempFilesToClean);

    /// <summary>A tail shorter than this is not worth freezing the picture for.</summary>
    const double MinTailSec = 0.05;

    /// <summary>
    /// Builds the exact FFmpeg <c>filter_complex</c> and argument list from <c>server/index.js:201-379</c>.
    /// <paramref name="audioSec"/> is the mastered mix's length: when the effect tails run past the last take, the
    /// picture freezes on its last frame and fades to the background over the tail (native only), so the video
    /// ends with the sound instead of cutting it or going black under it.
    /// When <paramref name="tempDir"/> is provided and color grade / vignette are active, writes the temporary
    /// <c>.cube</c> and <c>.gray</c> files into <paramref name="tempDir"/> using bare filenames (since FFmpeg
    /// runs with <c>WorkingDirectory = tempDir</c> to avoid escaping Windows drive colons in filter args).
    /// <paramref name="parallel"/> (FFmpeg 7+, <see cref="FfmpegCaps.Loopback"/>) gives every take its own filtergraph,
    /// which FFmpeg runs on its own thread, and hands the frames to the one that composes the panels through a loopback
    /// decoder: the frames are the same, but the takes stop waiting for each other (one graph runs its filters in
    /// order, a take after another).
    /// </summary>
    public static BuiltExportCommand BuildFfmpegCommand(
        ExportJobSpec job,
        string mixWavPath,
        string partOutPath,
        string? tempDir = null,
        string? runId = null,
        double? audioSec = null,
        VideoEncoderKind encoder = VideoEncoderKind.Cpu,
        bool parallel = false)
    {
        if (job.Takes.Count == 0)
            throw new ExportException("No hay canales activos para exportar.");

        var (outW, outH) = ExportSize(job.AspectRatio, job.Resolution);
        int numTakes = job.Takes.Count;
        int PanelOf(int i) => job.Takes[i].Panel >= 0 ? job.Takes[i].Panel : i;
        int numPanels = PanelCount(job.Takes);
        var ffmpegArgs = new List<string> { "-y" };

        for (int i = 0; i < numTakes; i++)
        {
            string vp = job.Takes[i].VideoPath;
            if (string.IsNullOrWhiteSpace(vp) || !File.Exists(vp))
                throw new ExportException($"Falta el video del canal #{i + 1}");
            ffmpegArgs.Add("-i");
            ffmpegArgs.Add(vp);
        }
        ffmpegArgs.Add("-i");
        ffmpegArgs.Add(mixWavPath);

        var panelRects = PanelLayout.Compute(numPanels, job.Layout, outW, outH);
        string id = runId ?? $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..20];
        var tempFiles = new List<string>();

        string? lutFile = null;
        if (!IsNeutralGrade(job.Color))
        {
            lutFile = $"grade_{id}.cube";
            if (tempDir != null)
            {
                Directory.CreateDirectory(tempDir);
                string fullLut = Path.Combine(tempDir, lutFile);
                File.WriteAllText(fullLut, LutToCube(job.Color), new UTF8Encoding(false));
                tempFiles.Add(fullLut);
            }
        }

        int grain = GrainLevels(job.VideoFx);
        bool vignette = HasVignette(job.VideoFx);
        int nextInput = numTakes + 1;

        var filterParts = new List<string>();
        // parallel: each take's own graph and the label it outputs
        var takeGraphs = new List<(string Graph, string Label)>();
        const string BG = "0x09090b";

        double maxDuration = 1.0;
        for (int i = 0; i < numTakes; i++)
        {
            double end = job.Takes[i].Window.End;
            if (end > maxDuration) maxDuration = end;
        }
        double mixDur = MixRenderer.MixDurationSec(job.MixTracks, job.SampleRate);
        if (mixDur > maxDuration) maxDuration = mixDur;
        double contentEnd = maxDuration;
        double tail = audioSec is { } a && a - contentEnd > MinTailSec ? a - contentEnd : 0;
        maxDuration += tail;

        filterParts.Add($"color=c={BG}:s={outW}x{outH}:r=30:d={F3(contentEnd)}[base0]");

        // video transitions between clips of a panel: how far each picture shows past its edges, and the fades they add
        var lead = new double[numTakes];
        var tailOver = new double[numTakes];
        var transFades = new List<string>?[numTakes];
        bool[] alpha = new bool[numTakes];
        for (int panel = 0; panel < numPanels; panel++)
        {
            int[] members = [.. Enumerable.Range(0, numTakes).Where(i => PanelOf(i) == panel)];
            var windows = members.Select(i => job.Takes[i].Window).ToArray();
            for (int m = 0; m < members.Length; m++)
            {
                var t = job.Takes[members[m]];
                if (ClipTransitions.Find(windows, m, ClipTransitions.Parse(t.Transition), t.TransitionSec) is not { } j) continue;
                int o = members[j.Out], n = members[j.In];
                lead[n] = Math.Max(lead[n], j.Overhang);
                tailOver[o] = Math.Max(tailOver[o], j.Overhang);
                if (j.Kind == TransitionKind.Crossfade)
                {
                    // the one stacked on top fades its alpha: rising if it comes in, sinking if it goes out
                    int top = Math.Max(o, n);
                    alpha[top] = true;
                    (transFades[top] ??= []).Add($"fade=t={(top == n ? "in" : "out")}:st={F3(j.Start)}:d={F3(j.Sec)}:alpha=1");
                }
                else
                {
                    (transFades[o] ??= []).Add($"fade=t=out:st={F3(j.Start)}:d={F3(j.Sec / 2)}:color={BG}");
                    (transFades[n] ??= []).Add($"fade=t=in:st={F3(j.At)}:d={F3(j.Sec / 2)}:color={BG}");
                }
            }
        }
        string baseLabel = "base0";

        for (int i = 0; i < numTakes; i++)
        {
            var t = job.Takes[i];
            string vIn = $"{i}:v";
            var rect = panelRects[PanelOf(i)];
            // a later clip of a shared panel starts at its own time instead of padding with the background,
            // which would cover the clips before it
            bool later = Enumerable.Range(0, i).Any(k => PanelOf(k) == PanelOf(i));
            int pw = rect.W - (rect.W % 2);
            int ph = rect.H - (rect.H % 2);

            var w = t.Window;
            var fades = ClipEnvelope.Fades(w, t.FadeInSec, t.FadeOutSec, t.LinkVideoFades, t.VideoFadeInSec, t.VideoFadeOutSec);
            // a transition shows the picture past the clip's edges (lead / tailOver); its own fades stay on its edges
            double visStart = Math.Max(0, w.Start - lead[i]);
            double visEnd = w.End + tailOver[i];

            // Video time = timeline time - net shift. Positive VideoOffsetMs (camera latency) pulls the video earlier.
            double netVideoShift = w.Origin - (t.VideoOffsetMs / 1000.0);
            double srcStart = Math.Max(0, visStart - netVideoShift);
            double srcEnd = visEnd - netVideoShift;

            double zoom = Math.Clamp(double.IsFinite(t.Zoom) && t.Zoom > 0 ? t.Zoom : 1.0, 1.0, 2.5);
            double panX = Math.Clamp(double.IsFinite(t.PanX) ? t.PanX : 0.0, -1.0, 1.0);
            double panY = Math.Clamp(double.IsFinite(t.PanY) ? t.PanY : 0.0, -1.0, 1.0);

            int scaledW = (int)Math.Round(pw * zoom, MidpointRounding.AwayFromZero);
            int scaledH = (int)Math.Round(ph * zoom, MidpointRounding.AwayFromZero);

            string cropXExpr = $"((iw-{pw})/2)*(1+({panX.ToString("0.000", CultureInfo.InvariantCulture)}))";
            string cropYExpr = $"((ih-{ph})/2)*(1+({panY.ToString("0.000", CultureInfo.InvariantCulture)}))";

            int border = (int)Math.Round(job.BorderSize * NormalizeResolution(job.Resolution) / (double)DefaultResolution);
            int padInner = border > 0 && numPanels > 1 ? border : 0;
            int innerW = pw - padInner * 2 - ((pw - padInner * 2) % 2);
            int innerH = ph - padInner * 2 - ((ph - padInner * 2) % 2);

            if (srcEnd - srcStart > 0.01 && visEnd > visStart)
            {
                double firstAt = srcStart + netVideoShift;
                double hold = firstAt - visStart;

                var pre = new List<string>
                {
                    $"trim=start={F3(srcStart)}:end={F3(srcEnd)}",
                    "setpts=PTS-STARTPTS",
                };
                if (lutFile != null) pre.Add($"lut3d=file={lutFile}:interp=trilinear");
                if (grain > 0)
                {
                    pre.Add("format=yuv420p");
                    pre.Add($"noise=c0s={grain}:c0f=t+u");
                }
                if (t.Mirror) pre.Add("hflip");
                pre.Add($"scale={scaledW}:{scaledH}:force_original_aspect_ratio=increase");
                pre.Add($"crop={innerW}:{innerH}:{cropXExpr}:{cropYExpr}");

                var chain = new List<string>();
                // sharpness, clarity and glow on the cropped panel, where their radius matches the preview's
                string picture = VideoDetail.ExportFilters(VideoDetail.For(job.Color, job.VideoFx), innerW, innerH, $"[{vIn}]{string.Join(",", pre)}", chain, i.ToString(CultureInfo.InvariantCulture));
                if (vignette)
                {
                    string maskName = $"vignette_{id}_{i}.gray";
                    if (tempDir != null)
                    {
                        Directory.CreateDirectory(tempDir);
                        string fullMask = Path.Combine(tempDir, maskName);
                        File.WriteAllBytes(fullMask, VignetteMask(job.VideoFx, innerW, innerH));
                        tempFiles.Add(fullMask);
                    }
                    ffmpegArgs.AddRange(["-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{innerW}x{innerH}", "-i", maskName]);
                    int m = nextInput++;
                    chain.Add($"{picture},format=gbrp[pic{i}]");
                    chain.Add($"[{m}:v]format=gbrp[mask{i}]");
                    chain.Add($"[pic{i}][mask{i}]blend=all_mode=multiply[vig{i}]");
                    picture = $"[vig{i}]null";
                }

                var vf = new List<string> { "format=yuv420p" };
                int bar = LetterboxBar(job.VideoFx, innerW, innerH);
                if (bar > 0)
                {
                    vf.Add($"drawbox=x=0:y=0:w=iw:h={bar}:color={BG}:t=fill");
                    vf.Add($"drawbox=x=0:y=ih-{bar}:w=iw:h={bar}:color={BG}:t=fill");
                }
                if (hold > 0.0005) vf.Add($"tpad=start_duration={F3(hold)}:start_mode=clone");
                if (later) vf.Add($"setpts=PTS-STARTPTS+{F3(visStart)}/TB");
                else
                {
                    if (visStart > 0.0005) vf.Add($"tpad=start_duration={F3(visStart)}:color={BG}");
                    vf.Add("setpts=PTS-STARTPTS");
                }
                // past the file's end (a transition's overhang with nothing hidden there) the last frame holds
                if (tailOver[i] > 0)
                {
                    vf.Add($"tpad=stop_mode=clone:stop_duration={F3(tailOver[i])}");
                    vf.Add($"trim=end={F3(visEnd)}");
                }
                double clipStart = Math.Max(0, w.Start);
                double vfIn = Math.Max(0, w.Start + fades.Video.FadeIn - clipStart);
                if (vfIn > 0.001) vf.Add($"fade=t=in:st={F3(clipStart)}:d={F3(vfIn)}:color={BG}");
                if (fades.Video.FadeOut > 0.001)
                    vf.Add($"fade=t=out:st={F3(w.End - fades.Video.FadeOut)}:d={F3(fades.Video.FadeOut)}:color={BG}");
                if (alpha[i]) vf.Add("format=yuva420p");
                if (transFades[i] is { } tf) vf.AddRange(tf);

                chain.Add($"{picture},{string.Join(",", vf)},fps=30,format={(alpha[i] ? "yuva420p" : "yuv420p")}[v{i}]");
                string takeOut = $"v{i}";
                if (parallel)
                {
                    takeGraphs.Add((string.Join(";", chain), takeOut));
                    takeOut = $"dec:{takeGraphs.Count - 1}";
                }
                else filterParts.AddRange(chain);

                string nextBase = $"base{i + 1}";
                int ox = rect.X + padInner;
                int oy = rect.Y + padInner;
                string enable = later ? $":enable='between(t,{F3(visStart)},{F3(visEnd)})'" : "";
                filterParts.Add($"[{baseLabel}][{takeOut}]overlay=x={ox}:y={oy}:eof_action=pass{enable}[{nextBase}]");
                baseLabel = nextBase;
            }
        }

        filterParts.Add(tail > 0
            ? $"[{baseLabel}]tpad=stop_mode=clone:stop_duration={F3(tail)},fade=t=out:st={F3(contentEnd)}:d={F3(tail)}:color={BG}[vout]"
            : $"[{baseLabel}]null[vout]");
        filterParts.Add($"[{numTakes}:a]aformat=sample_fmts=fltp:channel_layouts=stereo,apad[aout]");

        string filterComplex = string.Join(";", filterParts);
        var q = GetQualitySpec(job.Quality);

        // each take's graph goes to a null output whose frames come back as [dec:k] (the k-th output)
        for (int k = 0; k < takeGraphs.Count; k++)
            ffmpegArgs.AddRange(["-filter_complex", takeGraphs[k].Graph, "-map", $"[{takeGraphs[k].Label}]",
                "-c:v", "rawvideo", "-f", "null", "-", "-dec", $"{k}:0"]);
        ffmpegArgs.AddRange([
            "-filter_complex", filterComplex,
            "-map", "[vout]",
            "-map", "[aout]",
        ]);
        ffmpegArgs.AddRange(VideoEncoders.Args(encoder, q, outW, outH, job.BitrateMbps));
        ffmpegArgs.AddRange([
            "-c:a", "aac",
            "-b:a", "320k",
            "-ar", "48000",
            "-movflags", "+faststart",
            "-t", F3(maxDuration),
            "-progress", "pipe:1",
            "-nostats",
            "-f", "mp4",
            partOutPath,
        ]);

        string allGraphs = string.Join(" | ", takeGraphs.Select(g => g.Graph).Append(filterComplex));
        return new BuiltExportCommand(ffmpegArgs, allGraphs, maxDuration, outW, outH, tempFiles);
    }

    /// <summary>
    /// Runs the complete export asynchronously:
    /// 1) Mixes the audio offline via <see cref="MixRenderer.Render"/> into a temporary WAV,
    /// 2) Spawns <c>ffmpeg</c> with <c>-progress pipe:1</c> writing into a hidden <c>.part</c> file,
    /// 3) Streams <see cref="ExportProgress"/> updates,
    /// 4) Atomically moves <c>.part</c> to the target <c>.mp4</c> on completion, or cleans up on cancel/error.
    /// </summary>
    public static async Task<ExportResult> ExportAsync(
        ExportJobSpec job,
        Action<ExportProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        if (job.Takes.Count == 0)
            throw new ExportException(Texts.T("err.export.noTracks", "Necesitás al menos un canal en el timeline para exportar."));

        var target = CheckDestination(job.ProjectDir, job.OutDir, string.IsNullOrWhiteSpace(job.FileName) ? job.ProjectName : job.FileName);
        if (target.Exists && !job.Overwrite)
            throw new ExportException($"Ya existe {target.FileName}.mp4 en esa carpeta.", exists: true);
        if (!target.IsDefault && !Directory.Exists(target.Folder))
            throw new ExportException("La carpeta de destino ya no existe.");

        Directory.CreateDirectory(target.Folder);

        var sw = Stopwatch.StartNew();
        void Report(ExportStage stage, double mix, double upload, RenderProgress? render) =>
            onProgress?.Invoke(new ExportProgress(stage, mix, upload, render, sw.Elapsed.TotalSeconds));

        string tempWorkDir = Path.Combine(Path.GetTempPath(), "GuitarCamExport");
        Directory.CreateDirectory(tempWorkDir);
        string runId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..20];
        string mixWavPath = Path.Combine(tempWorkDir, $"mix_{runId}.wav");
        string partPath = Path.Combine(target.Folder, $".{target.FileName}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.part");
        IReadOnlyList<string> extraTempFiles = [];

        try
        {
            Report(ExportStage.Mix, 0, 0, null);

            double maxDuration = 1.0;
            for (int i = 0; i < job.Takes.Count; i++)
            {
                double end = job.Takes[i].Window.End;
                if (end > maxDuration) maxDuration = end;
            }
            double mixDur = MixRenderer.MixDurationSec(job.MixTracks, job.SampleRate);
            if (mixDur > maxDuration) maxDuration = mixDur;

            var mix = await Task.Run(() =>
                MixMaster.Render(
                    job.MixTracks,
                    job.MixBus,
                    maxDuration,
                    job.SampleRate,
                    job.TailSec,
                    job.TargetLufs,
                    f => Report(ExportStage.Mix, f, 0, null),
                    ct), ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            Report(ExportStage.Upload, 1, 0, null);

            await Task.Run(() => WriteFloatStereoWav(mixWavPath, mix.Left, mix.Right, job.SampleRate), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Report(ExportStage.Upload, 1, 1, null);

            var found = await VideoEncoders.DetectAsync().ConfigureAwait(false);
            var encoder = VideoEncoders.Resolve(job.Encoder, found);
            var built = BuildFfmpegCommand(job, mixWavPath, partPath, tempWorkDir, runId, mix.DurationSec, encoder, found.Loopback);
            extraTempFiles = built.TempFilesToClean;

            var (code, details) = await RunFfmpegAsync(built, tempWorkDir, r => Report(ExportStage.Video, 1, 1, r), ct).ConfigureAwait(false);
            // a card's encoder can still refuse this frame size or a busy driver, and an odd FFmpeg build the take graphs:
            // the processor in a single graph is what always worked, so "auto" tries that before giving up
            if (code != 0 && (encoder != VideoEncoderKind.Cpu || found.Loopback) && VideoEncoders.ParsePreference(job.Encoder) == VideoEncoders.Auto)
            {
                encoder = VideoEncoderKind.Cpu;
                built = BuildFfmpegCommand(job, mixWavPath, partPath, tempWorkDir, runId, mix.DurationSec, encoder, parallel: false);
                (code, details) = await RunFfmpegAsync(built, tempWorkDir, r => Report(ExportStage.Video, 1, 1, r), ct).ConfigureAwait(false);
            }
            if (code != 0)
                throw new ExportException("Error al renderizar con FFmpeg", string.IsNullOrWhiteSpace(details) ? null : details);

            Report(ExportStage.Finalize, 1, 1, new RenderProgress(ExportStage.Finalize, 1));
            string outFilename = $"{target.FileName}.mp4";
            try
            {
                File.Move(partPath, target.FullPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ExportException(
                    Texts.T("err.export.replace", "No se pudo reemplazar {0}: puede que esté abierto en otro programa.", outFilename));
            }

            long bytes = File.Exists(target.FullPath) ? new FileInfo(target.FullPath).Length : 0;
            string rel = Path.GetRelativePath( Path.GetFullPath(job.ProjectDir), target.FullPath);
            string loggedFile = !string.IsNullOrEmpty(rel) && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)
                ? rel.Replace('\\', '/')
                : target.FullPath;

            return new ExportResult(
                outFilename,
                loggedFile,
                target.FullPath,
                target.Folder,
                target.IsDefault,
                bytes,
                built.MaxDurationSec,
                sw.Elapsed.TotalSeconds,
                mix.Lufs,
                mix.TailSec,
                encoder);
        }
        finally
        {
            TryDelete(partPath);
            TryDelete(mixWavPath);
            foreach (var f in extraTempFiles) TryDelete(f);
        }
    }

    /// <summary>
    /// Runs FFmpeg with <paramref name="built"/>, reporting its <c>-progress</c> lines, and returns its exit code and the
    /// end of its error output (empty when it worked). Cancelling kills it.
    /// </summary>
    static async Task<(int Code, string Details)> RunFfmpegAsync(BuiltExportCommand built, string tempWorkDir, Action<RenderProgress> report, CancellationToken ct)
    {
        var videoStart = Stopwatch.StartNew();
        report(new RenderProgress(ExportStage.Video, 0, 0, built.MaxDurationSec));

        var psi = new ProcessStartInfo("ffmpeg", built.FfmpegArgs)
        {
            WorkingDirectory = tempWorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                throw new ExportException(Texts.T("err.ffmpeg.start0", "No se pudo iniciar FFmpeg."));
        }
        catch (Win32Exception ex)
        {
            throw new ExportException(
                ex.NativeErrorCode == 2
                    ? Texts.T("err.ffmpeg.missing", "No se encontró FFmpeg en el PATH.")
                    : Texts.T("err.ffmpeg.start", "No se pudo iniciar FFmpeg: {0}", ex.Message));
        }

        var stderrBuilder = new StringBuilder();
        var stderrGate = new object();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderrGate)
            {
                stderrBuilder.AppendLine(e.Data);
                if (stderrBuilder.Length > 20000)
                    stderrBuilder.Remove(0, stderrBuilder.Length - 20000);
            }
        };
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        });

        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            block[key] = val;
            if (!string.Equals(key, "progress", StringComparison.OrdinalIgnoreCase)) continue;

            double us = 0;
            if ((block.TryGetValue("out_time_us", out var usStr) || block.TryGetValue("out_time_ms", out usStr))
                && double.TryParse(usStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedUs) && parsedUs > 0)
            {
                us = parsedUs;
            }
            double timeSec = us > 0 ? us / 1e6 : 0;
            double prog = string.Equals(val, "end", StringComparison.OrdinalIgnoreCase)
                ? 1.0
                : Math.Clamp(timeSec / built.MaxDurationSec, 0, 1);

            double fps = block.TryGetValue("fps", out var fpsStr)
                && double.TryParse(fpsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps)
                ? parsedFps
                : 0;
            double speed = 0;
            if (block.TryGetValue("speed", out var spdStr))
            {
                string cleanSpd = spdStr.TrimEnd('x', 'X', ' ');
                if (double.TryParse(cleanSpd, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpd))
                    speed = parsedSpd;
            }

            double elapsedVideo = videoStart.Elapsed.TotalSeconds;
            double? eta = prog > 0.02 && prog < 1.0 ? (elapsedVideo * (1.0 - prog)) / prog : null;

            report(new RenderProgress(
                ExportStage.Video,
                prog,
                Math.Min(timeSec, built.MaxDurationSec),
                built.MaxDurationSec,
                fps,
                speed,
                eta));
            block.Clear();
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode == 0) return (0, "");
        lock (stderrGate)
        {
            string s = stderrBuilder.ToString().Trim();
            return (proc.ExitCode, s.Length > 1200 ? s[^1200..] : s);
        }
    }

    internal static void WriteFloatStereoWav(string path, ReadOnlySpan<float> left, ReadOnlySpan<float> right, int sampleRate)
    {
        int n = Math.Min(left.Length, right.Length);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        fs.Write(WavFile.FloatStereoHeader(sampleRate, n));
        byte[] buf = new byte[8192 * 8];
        for (int i = 0; i < n;)
        {
            int chunk = Math.Min(8192, n - i);
            var span = buf.AsSpan(0, chunk * 8);
            for (int j = 0; j < chunk; j++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(span[(j * 8)..], left[i + j]);
                BinaryPrimitives.WriteSingleLittleEndian(span[(j * 8 + 4)..], right[i + j]);
            }
            fs.Write(span);
            i += chunk;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
