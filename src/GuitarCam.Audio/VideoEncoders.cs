using System.Diagnostics;
using System.Globalization;

namespace GuitarCam.Audio;

/// <summary>Who encodes the export's H.264: the processor (libx264) or a video card's encoder chip.</summary>
public enum VideoEncoderKind
{
    Cpu,
    Nvenc,
    Amf,
    Qsv,
}

/// <summary>What the FFmpeg on the PATH can do for an export: the video card encoder that works here (null = none) and
/// whether it has loopback decoders (FFmpeg 7+), which let every take run its filters on its own thread.</summary>
public sealed record FfmpegCaps(VideoEncoderKind? Gpu, bool Loopback);

/// <summary>
/// Picks the export's video encoder. A card's encoder (NVIDIA NVENC, AMD AMF, Intel Quick Sync) frees the processor
/// for the filters and encodes several times faster than libx264, but FFmpeg lists all of them even without the card
/// or its driver, so <see cref="DetectAsync"/> encodes a short test clip with each one instead of reading the list,
/// once per run of the program. The card's encoders are not as efficient as libx264's CRF, so they get a bitrate
/// (the quality's <see cref="ExportQualitySpec.Mbps"/>, the same the dialog estimates the size with) instead of a CRF.
/// The preference is the app's <see cref="ConfigKey"/> setting: <see cref="Auto"/> or <see cref="CpuId"/>.
/// </summary>
public static class VideoEncoders
{
    /// <summary>The app setting (<c>config.json</c>) with the preference: it belongs to the machine, not to the project.</summary>
    public const string ConfigKey = "export.encoder";
    public const string Auto = "auto";
    public const string CpuId = "cpu";
    /// <summary>The order the cards are tried in when more than one works (a laptop with Intel and NVIDIA picks NVIDIA).</summary>
    static readonly VideoEncoderKind[] Order = [VideoEncoderKind.Nvenc, VideoEncoderKind.Amf, VideoEncoderKind.Qsv];
    /// <summary>A probe that takes longer than this counts as not working (a hung driver must not hang the export).</summary>
    const int ProbeTimeoutMs = 15000;
    /// <summary>Peak and buffer of the card's VBR, relative to the target bitrate: room for strumming, without blowing up the file.</summary>
    const double PeakFactor = 1.5, BufferFactor = 2.0;

    static readonly object capsGate = new();
    static Task<FfmpegCaps>? caps;

    /// <summary>What this machine's FFmpeg can do; probed the first time and remembered.</summary>
    public static Task<FfmpegCaps> DetectAsync()
    {
        lock (capsGate) return caps ??= Task.Run(Probe);
    }

    /// <summary>Drops what was probed, so the next <see cref="DetectAsync"/> looks again (FFmpeg was just installed).</summary>
    public static void Forget()
    {
        lock (capsGate) caps = null;
    }

    public static string Id(VideoEncoderKind k) => k switch
    {
        VideoEncoderKind.Nvenc => "nvenc",
        VideoEncoderKind.Amf => "amf",
        VideoEncoderKind.Qsv => "qsv",
        _ => CpuId,
    };

    /// <summary>The brand, for the settings and the console ("NVIDIA", "AMD", "Intel", "").</summary>
    public static string Brand(VideoEncoderKind k) => k switch
    {
        VideoEncoderKind.Nvenc => "NVIDIA",
        VideoEncoderKind.Amf => "AMD",
        VideoEncoderKind.Qsv => "Intel",
        _ => "",
    };

    static string Codec(VideoEncoderKind k) => k switch
    {
        VideoEncoderKind.Nvenc => "h264_nvenc",
        VideoEncoderKind.Amf => "h264_amf",
        VideoEncoderKind.Qsv => "h264_qsv",
        _ => "libx264",
    };

    /// <summary>"auto", "cpu", "nvenc", "amf" or "qsv" (anything else = auto).</summary>
    public static string ParsePreference(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "cpu" or "procesador" => CpuId,
        "nvenc" or "nvidia" => "nvenc",
        "amf" or "amd" => "amf",
        "qsv" or "intel" => "qsv",
        _ => Auto,
    };

    /// <summary>The encoder an export with <paramref name="preference"/> uses here. A named card is used even if the probe
    /// did not find it (gclab, to see FFmpeg's error); "auto" falls back to the processor.</summary>
    public static VideoEncoderKind Resolve(string? preference, FfmpegCaps found) => ParsePreference(preference) switch
    {
        CpuId => VideoEncoderKind.Cpu,
        "nvenc" => VideoEncoderKind.Nvenc,
        "amf" => VideoEncoderKind.Amf,
        "qsv" => VideoEncoderKind.Qsv,
        _ => found.Gpu ?? VideoEncoderKind.Cpu,
    };

    /// <summary>
    /// FFmpeg's video codec arguments for <paramref name="k"/> at quality <paramref name="q"/> and that frame size.
    /// <paramref name="customMbps"/> ("Avanzado") replaces the quality's bitrate, and turns x264 from CRF to that
    /// bitrate too; the quality still picks the encoder's speed preset.
    /// </summary>
    public static IReadOnlyList<string> Args(VideoEncoderKind k, ExportQualitySpec q, int width, int height, double? customMbps = null)
    {
        double? custom = Mp4Exporter.NormalizeBitrate(customMbps);
        if (k == VideoEncoderKind.Cpu && custom == null)
            return ["-c:v", "libx264", "-preset", q.Preset, "-crf", q.Crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"];

        int kbps = Math.Max(500, (int)Math.Round(Mp4Exporter.VideoMbps(q.Value, width, height, custom) * 1000));
        string b = $"{kbps}k", peak = $"{(int)(kbps * PeakFactor)}k", buf = $"{(int)(kbps * BufferFactor)}k";
        if (k == VideoEncoderKind.Cpu)
            return ["-c:v", "libx264", "-preset", q.Preset, "-b:v", b, "-maxrate", peak, "-bufsize", buf, "-pix_fmt", "yuv420p"];
        bool draft = q.Value == ExportQuality.Borrador;
        return k switch
        {
            VideoEncoderKind.Nvenc => ["-c:v", Codec(k), "-preset", draft ? "p3" : "p5", "-tune", "hq", "-rc", "vbr",
                "-b:v", b, "-maxrate", peak, "-bufsize", buf, "-pix_fmt", "yuv420p"],
            VideoEncoderKind.Amf => ["-c:v", Codec(k), "-usage", "transcoding", "-quality", draft ? "speed" : "balanced",
                "-rc", "vbr_peak", "-b:v", b, "-maxrate", peak, "-bufsize", buf, "-pix_fmt", "nv12"],
            _ => ["-c:v", Codec(k), "-preset", draft ? "veryfast" : "medium",
                "-b:v", b, "-maxrate", peak, "-bufsize", buf, "-pix_fmt", "nv12"],
        };
    }

    static FfmpegCaps Probe()
    {
        VideoEncoderKind? gpu = null;
        foreach (var k in Order)
        {
            // 1280x720 because NVENC refuses tiny frames; a third of a second is enough to open the driver
            if (Run($"-hide_banner -loglevel error -f lavfi -i color=c=black:s=1280x720:r=30:d=0.3 -c:v {Codec(k)} -f null -"))
            {
                gpu = k;
                break;
            }
        }
        bool loopback = Run("-hide_banner -loglevel error -f lavfi -i color=c=black:s=64x64:r=30:d=0.2 "
            + "-filter_complex [0:v]null[a] -map [a] -c:v rawvideo -f null - -dec 0:0 "
            + "-filter_complex [dec:0]null[b] -map [b] -c:v rawvideo -f null -");
        return new FfmpegCaps(gpu, loopback);
    }

    static bool Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(ProbeTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
