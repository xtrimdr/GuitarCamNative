using System.Text.Json;
using System.Text.Json.Serialization;

namespace WildTrack.Audio;

public enum CalibrationMethod
{
    Loopback,
    Tap,
}

public readonly record struct CalibrationResult(
    CalibrationMethod Method,
    double LatencyMs,
    double SpreadMs,
    int Matched,
    int Total);

public readonly record struct CalibrationProgress(
    int Step,
    int Total,
    bool CountIn);

/// <summary>
/// Port of <c>LatencyInfo</c> (<c>GuitarCam/src/audio/audioEngine.ts</c>).
/// </summary>
public readonly record struct LatencyInfo(
    double OutputMs,
    double InputMs,
    double TotalMs,
    int SampleRate,
    int InputSampleRate,
    double? CalibratedMs,
    double AppliedMs);

/// <summary>
/// Sample-accurate state machine that plays the calibration pulses/clicks (delayed by the master limiter
/// lookahead, like backing tracks) and records the stereo input from frame 0 on the same callback clock.
/// Port of <c>StudioAudioEngine.measureLatency</c> (<c>GuitarCam/src/audio/audioEngine.ts:522-606</c>).
/// </summary>
public sealed class CalibrationSession
{
    public const double LeadSec = 0.25;

    readonly CalibrationMethod method;
    readonly int sampleRate;
    readonly int lookaheadSamples;
    readonly float[] pulse;
    readonly float[] clickHi;
    readonly float[] clickLo;
    readonly double[] times;
    readonly int[] frames;
    readonly int countIn;
    readonly int totalFrames;
    readonly float[] recL;
    readonly float[] recR;

    long currentFrame;

    public CalibrationMethod Method => method;
    public int SampleRate => sampleRate;
    public int TotalFrames => totalFrames;
    public long CurrentFrame => Interlocked.Read(ref currentFrame);
    public bool IsFinished => Interlocked.Read(ref currentFrame) >= totalFrames;

    public CalibrationSession(
        CalibrationMethod method,
        int sampleRate,
        int limiterLookaheadSamples,
        ClickSound clickSound = ClickSound.Madera)
    {
        this.method = method;
        this.sampleRate = sampleRate;
        lookaheadSamples = limiterLookaheadSamples;
        pulse = LatencyCalibration.MakePulse(sampleRate);
        clickHi = ClickSynth.Render(clickSound, true, sampleRate);
        clickLo = ClickSynth.Render(clickSound, false, sampleRate);

        double endSec;
        if (method == CalibrationMethod.Loopback)
        {
            countIn = 0;
            times = new double[LatencyCalibration.LoopbackPulses];
            frames = new int[LatencyCalibration.LoopbackPulses];
            for (int k = 0; k < LatencyCalibration.LoopbackPulses; k++)
            {
                double t = LeadSec + k * LatencyCalibration.LoopbackIntervalSec;
                times[k] = t;
                frames[k] = (int)Math.Round(t * sampleRate);
            }
            endSec = LeadSec
                + (LatencyCalibration.LoopbackPulses - 1) * LatencyCalibration.LoopbackIntervalSec
                + LatencyCalibration.MaxLatencySec + 0.1;
        }
        else
        {
            double beatSec = 60.0 / LatencyCalibration.TapBpm;
            countIn = LatencyCalibration.TapCountIn;
            int n = LatencyCalibration.TapCountIn + LatencyCalibration.TapBeats;
            times = new double[n];
            frames = new int[n];
            for (int b = 0; b < n; b++)
            {
                double t = LeadSec + b * beatSec;
                times[b] = t;
                frames[b] = (int)Math.Round(t * sampleRate);
            }
            endSec = LeadSec + (n - 1) * beatSec + LatencyCalibration.MaxLatencySec + 0.15;
        }

        totalFrames = (int)Math.Ceiling(endSec * sampleRate);
        recL = new float[totalFrames];
        recR = new float[totalFrames];
    }

    /// <summary>
    /// Processes one audio callback block: captures <paramref name="inL"/>/<paramref name="inR"/> and writes
    /// the scheduled pulses or clicks to <paramref name="outL"/>/<paramref name="outR"/>. Zero allocations.
    /// </summary>
    public void ProcessBlock(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, Span<float> outL, Span<float> outR, int n)
    {
        long f0 = Interlocked.Read(ref currentFrame);
        bool hasR = inR.Length >= n;
        for (int i = 0; i < n; i++)
        {
            long f = f0 + i;
            if (f >= totalFrames) break;
            int fi = (int)f;
            recL[fi] = inL[i];
            recR[fi] = hasR ? inR[i] : inL[i];

            float s = SampleOutput(fi);
            outL[i] = s;
            if (outR.Length > i) outR[i] = s;
        }
        Interlocked.Exchange(ref currentFrame, f0 + n);
    }

    float SampleOutput(int frame)
    {
        if (method == CalibrationMethod.Loopback)
        {
            for (int k = 0; k < frames.Length; k++)
            {
                int rel = frame - (frames[k] + lookaheadSamples);
                if (rel >= 0 && rel < pulse.Length)
                    return pulse[rel];
            }
            return 0f;
        }
        else
        {
            for (int b = 0; b < frames.Length; b++)
            {
                int rel = frame - (frames[b] + lookaheadSamples);
                var c = (b % 4 == 0) ? clickHi : clickLo;
                if (rel >= 0 && rel < c.Length)
                    return c[rel];
            }
            return 0f;
        }
    }

    public CalibrationProgress? GetProgress()
    {
        double el = Interlocked.Read(ref currentFrame) / (double)sampleRate;
        if (times.Length == 0 || el < times[0]) return null;
        int i = 0;
        while (i + 1 < times.Length && times[i + 1] <= el) i++;
        int total = times.Length - countIn;
        return new CalibrationProgress(Math.Max(1, i + 1 - countIn), total, i < countIn);
    }

    public CalibrationResult? Analyze()
    {
        int captured = (int)Math.Min(totalFrames, Interlocked.Read(ref currentFrame));
        var l = recL.AsSpan(0, captured);
        var r = recR.AsSpan(0, captured);
        if (method == CalibrationMethod.Loopback)
            return LatencyCalibration.AnalyzeLoopback(l, r, pulse, frames, sampleRate);

        int intervalFrames = (int)Math.Round((60.0 / LatencyCalibration.TapBpm) * sampleRate);
        return LatencyCalibration.AnalyzeTap(l, r, frames.AsSpan(countIn), intervalFrames, sampleRate);
    }
}

/// <summary>
/// A calibration saved for one input + output pair (machine-specific).
/// Port of <c>StoredCalibration</c> (<c>GuitarCam/src/audio/latencyCalibration.ts</c>).
/// </summary>
public sealed class StoredCalibration
{
    [JsonPropertyName("latencyMs")] public double LatencyMs { get; set; }
    [JsonPropertyName("spreadMs")] public double SpreadMs { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "loopback";
    [JsonPropertyName("estimateMs")] public double EstimateMs { get; set; }
    [JsonPropertyName("sampleRate")] public int SampleRate { get; set; }
    [JsonPropertyName("at")] public long At { get; set; }
}

/// <summary>
/// Port of <c>GuitarCam/src/audio/latencyCalibration.ts</c>: round-trip latency measurement via
/// seeded Hann-windowed white-noise pulses (<c>loopback</c>) or 90 BPM click attacks (<c>tap</c>),
/// plus per-device calibration storage and staleness check.
/// </summary>
public static class LatencyCalibration
{
    /// <summary>Longest round trip searched for (Bluetooth output can be ~250 ms).</summary>
    public const double MaxLatencySec = 0.45;

    /// <summary>Loopback test signal parameters.</summary>
    public const int LoopbackPulses = 8;
    public const double LoopbackIntervalSec = 0.6;
    public const double PulseSec = 0.008;

    /// <summary>Tap test parameters: count-in beats to listen, then beats to play on.</summary>
    public const double TapBpm = 90;
    public const int TapCountIn = 4;
    public const int TapBeats = 12;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static string MethodToString(CalibrationMethod method) =>
        method == CalibrationMethod.Tap ? "tap" : "loopback";

    public static CalibrationMethod ParseMethod(string? s) =>
        string.Equals(s, "tap", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(s, "tocando", StringComparison.OrdinalIgnoreCase)
            ? CalibrationMethod.Tap
            : CalibrationMethod.Loopback;

    /// <summary>
    /// Whether the audio setup changed since <paramref name="c"/> was measured (different sample rate,
    /// or estimated latency shifted by more than 40 ms).
    /// </summary>
    public static bool IsCalibrationStale(StoredCalibration c, double estimateMs, int sampleRate) =>
        c.SampleRate != sampleRate || Math.Abs(c.EstimateMs - estimateMs) > 40.0;

    public static string CalibrationKey(string inputId, string outputId = "") =>
        string.IsNullOrEmpty(outputId) ? $"gc.latencyCal.{inputId}" : $"gc.latencyCal.{inputId}|{outputId}";

    static string StoreFilePath(string? dataDir)
    {
        string dir = !string.IsNullOrWhiteSpace(dataDir)
            ? dataDir
            : AppFolder.Dir;
        return Path.Combine(dir, "latencia.json");
    }

    public static StoredCalibration? ReadCalibration(string inputId, string outputId = "", string? dataDir = null)
    {
        try
        {
            string path = StoreFilePath(dataDir);
            if (!File.Exists(path)) return null;
            var dict = JsonSerializer.Deserialize<Dictionary<string, StoredCalibration>>(File.ReadAllText(path), JsonOpts);
            if (dict == null) return null;
            if (dict.TryGetValue(CalibrationKey(inputId, outputId), out var c) && c != null && double.IsFinite(c.LatencyMs))
                return c;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static void WriteCalibration(string inputId, string outputId, StoredCalibration? c, string? dataDir = null)
    {
        try
        {
            string path = StoreFilePath(dataDir);
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            Dictionary<string, StoredCalibration>? dict = null;
            if (File.Exists(path))
            {
                try
                {
                    dict = JsonSerializer.Deserialize<Dictionary<string, StoredCalibration>>(File.ReadAllText(path), JsonOpts);
                }
                catch
                {
                    dict = null;
                }
            }
            dict ??= new Dictionary<string, StoredCalibration>(StringComparer.Ordinal);
            string key = CalibrationKey(inputId, outputId);
            if (c != null) dict[key] = c;
            else dict.Remove(key);

            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(dict, JsonOpts);
                fs.Write(bytes);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Ignore storage errors so calibration still works in-memory.
        }
    }

    /// <summary>
    /// Seeded white-noise burst with a Hann window: broadband, so its cross-correlation has one sharp
    /// peak even after the interface's filters.
    /// </summary>
    public static float[] MakePulse(int sampleRate)
    {
        int n = (int)Math.Round(PulseSec * sampleRate);
        var res = new float[n];
        uint seed = 0x2545f491u;
        for (int i = 0; i < n; i++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            double white = (seed / (double)0xffffffffu) * 2.0 - 1.0;
            res[i] = (float)(white * 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1))));
        }
        return res;
    }

    /// <summary>Louder channel per sample, so it works whichever input the cable/mic is plugged into.</summary>
    public static float[] MonoOf(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        var res = new float[n];
        for (int i = 0; i < n; i++)
            res[i] = Math.Abs(left[i]) >= Math.Abs(right[i]) ? left[i] : right[i];
        return res;
    }

    /// <summary>
    /// Loopback: cross-correlates the recording with the pulse after each scheduled pulse start
    /// (<paramref name="pulseFrames"/>, frames from sample 0 of the recording) and returns where each one came back.
    /// </summary>
    public static CalibrationResult? AnalyzeLoopback(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        ReadOnlySpan<float> pulse,
        ReadOnlySpan<int> pulseFrames,
        int sampleRate)
    {
        float[] x = MonoOf(left, right);
        int maxLag = (int)Math.Round(MaxLatencySec * sampleRate);
        var lags = new List<int>(pulseFrames.Length);

        for (int p = 0; p < pulseFrames.Length; p++)
        {
            int p0 = pulseFrames[p];
            double best = 0;
            int bestLag = -1;
            double sum = 0;
            int count = 0;
            for (int lag = 0; lag < maxLag; lag++)
            {
                int at = p0 + lag;
                if (at < 0) continue;
                if (at + pulse.Length > x.Length) break;
                double c = 0;
                for (int k = 0; k < pulse.Length; k++)
                    c += (double)x[at + k] * pulse[k];
                c = Math.Abs(c); // cable/interface may invert polarity
                sum += c;
                count++;
                if (c > best)
                {
                    best = c;
                    bestLag = lag;
                }
            }
            if (bestLag >= 0 && count > 0 && best > 1e-4 && best > (sum / count) * 12.0)
                lags.Add(bestLag);
        }

        int minMatches = (pulseFrames.Length + 1) / 2;
        if (lags.Count < minMatches) return null;

        var ms = new double[lags.Count];
        for (int i = 0; i < lags.Count; i++)
            ms[i] = (lags[i] / (double)sampleRate) * 1000.0;

        return new CalibrationResult(
            CalibrationMethod.Loopback,
            Median(ms),
            Mad(ms),
            lags.Count,
            pulseFrames.Length);
    }

    /// <summary>
    /// Tap: finds the attacks the player made on each click (<paramref name="clickFrames"/>, after the count-in)
    /// and measures how late they arrived.
    /// </summary>
    public static CalibrationResult? AnalyzeTap(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        ReadOnlySpan<int> clickFrames,
        int intervalFrames,
        int sampleRate)
    {
        float[] x = MonoOf(left, right);
        int hop = Math.Max(1, (int)Math.Round(sampleRate / 1000.0));
        int envLen = (x.Length + hop - 1) / hop;
        if (envLen == 0) return null;

        var env = new float[envLen];
        for (int h = 0; h < envLen; h++)
        {
            float m = 0;
            int end = Math.Min(x.Length, (h + 1) * hop);
            for (int i = h * hop; i < end; i++)
            {
                float a = Math.Abs(x[i]);
                if (a > m) m = a;
            }
            env[h] = m;
        }

        var sorted = (float[])env.Clone();
        Array.Sort(sorted);
        double noise = sorted[(int)Math.Floor(sorted.Length * 0.2)];
        double peak = sorted[(int)Math.Floor(sorted.Length * 0.995)];
        if (peak < 0.01) return null;
        double threshold = Math.Max(Math.Max(noise * 6.0, peak * 0.2), 0.004);

        int early = (int)Math.Round(0.12 * sampleRate); // players can rush a little
        int lateLimit = Math.Min(intervalFrames - early, (int)Math.Round(MaxLatencySec * sampleRate));
        var lates = new List<int>(clickFrames.Length);

        for (int idx = 0; idx < clickFrames.Length; idx++)
        {
            int c = clickFrames[idx];
            int from = Math.Max(0, (int)Math.Floor((c - early) / (double)hop));
            int to = Math.Min(env.Length, (int)Math.Floor((c + lateLimit) / (double)hop));
            int h = from;
            while (h < to && env[h] >= threshold * 0.5) h++;
            for (; h < to; h++)
            {
                if (env[h] >= threshold)
                {
                    int i = h * hop;
                    int end = Math.Min(x.Length, i + hop);
                    while (i < end && Math.Abs(x[i]) < threshold) i++;
                    lates.Add(i - c);
                    break;
                }
            }
        }

        int minMatches = (clickFrames.Length + 1) / 2;
        if (lates.Count < minMatches) return null;

        var ms = new double[lates.Count];
        for (int i = 0; i < lates.Count; i++)
            ms[i] = (lates[i] / (double)sampleRate) * 1000.0;

        return new CalibrationResult(
            CalibrationMethod.Tap,
            Median(ms),
            Mad(ms),
            lates.Count,
            clickFrames.Length);
    }

    static double Median(ReadOnlySpan<double> v)
    {
        var s = v.ToArray();
        Array.Sort(s);
        int m = s.Length >> 1;
        return (s.Length & 1) != 0 ? s[m] : (s[m - 1] + s[m]) * 0.5;
    }

    static double Mad(ReadOnlySpan<double> v)
    {
        double m = Median(v);
        var dev = new double[v.Length];
        for (int i = 0; i < v.Length; i++)
            dev[i] = Math.Abs(v[i] - m);
        return Median(dev);
    }
}
