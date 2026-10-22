using System.Text.RegularExpressions;

namespace GuitarCam.Audio;

public enum InputChannelMode
{
    Stereo,
    Ch1Mono,
    Ch2Mono,
}

/// <summary>
/// Port of <c>StereoCaptureProcessor</c> (<c>GuitarCam/src/audio/audioEngine.ts</c>):
/// routes input channels (<c>stereo</c> with automatic mono-ch0 detection when ch1 is silent,
/// <c>ch1-mono</c>, <c>ch2-mono</c>), applies input pre-amp gain (+12 dB default), soft-limits peaks
/// above 0.92 with <c>tanh</c>, and reports peak levels and clipping without allocating in <see cref="Process"/>.
/// </summary>
public sealed class InputProcessor
{
    public const double DefaultInputGainDb = 12.0;

    volatile InputChannelMode mode = InputChannelMode.Stereo;
    volatile float gainLinear = (float)Math.Pow(10.0, DefaultInputGainDb / 20.0);
    double gainDb = DefaultInputGainDb;

    public InputChannelMode ChannelMode
    {
        get => mode;
        set => mode = value;
    }

    public double InputGainDb
    {
        get => gainDb;
        set
        {
            gainDb = value;
            gainLinear = (float)Math.Pow(10.0, value / 20.0);
        }
    }

    public static InputChannelMode ParseMode(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "ch1-mono" or "1" or "mono1" or "ch1" => InputChannelMode.Ch1Mono,
        "ch2-mono" or "2" or "mono2" or "ch2" => InputChannelMode.Ch2Mono,
        _ => InputChannelMode.Stereo,
    };

    public static string ModeToString(InputChannelMode m) => m switch
    {
        InputChannelMode.Ch1Mono => "ch1-mono",
        InputChannelMode.Ch2Mono => "ch2-mono",
        _ => "stereo",
    };

    /// <summary>
    /// Studio soft-saturation near 0 dBFS so boosted guitar peaks never digital-crack.
    /// Exact port of <c>StereoCaptureProcessor.softLimit</c>.
    /// </summary>
    public static float SoftLimit(float x)
    {
        if (x > 0.92f)
            return (float)(0.92 + 0.08 * Math.Tanh((x - 0.92) / 0.08));
        if (x < -0.92f)
            return (float)(-0.92 + 0.08 * Math.Tanh((x + 0.92) / 0.08));
        return x;
    }

    public void Process(
        ReadOnlySpan<float> ch0,
        ReadOnlySpan<float> ch1,
        Span<float> procL,
        Span<float> procR,
        out float maxL,
        out float maxR,
        out bool clip)
    {
        int len = Math.Min(ch0.Length, Math.Min(procL.Length, procR.Length));
        bool hasCh1 = ch1.Length >= len;

        float rawMax0 = 0f;
        float rawMax1 = 0f;
        for (int i = 0; i < len; i++)
        {
            float a0 = Math.Abs(ch0[i]);
            float a1 = hasCh1 ? Math.Abs(ch1[i]) : a0;
            if (a0 > rawMax0) rawMax0 = a0;
            if (a1 > rawMax1) rawMax1 = a1;
        }

        var curMode = mode;
        float g = gainLinear;
        bool autoMonoCh0 = curMode == InputChannelMode.Stereo && rawMax0 > 0.002f && rawMax1 < 0.00005f;

        float ml = 0f;
        float mr = 0f;
        for (int i = 0; i < len; i++)
        {
            float s0 = ch0[i];
            float s1 = hasCh1 ? ch1[i] : s0;
            float sL, sR;
            if (curMode == InputChannelMode.Ch1Mono || autoMonoCh0)
            {
                sL = s0;
                sR = s0;
            }
            else if (curMode == InputChannelMode.Ch2Mono)
            {
                sL = s1;
                sR = s1;
            }
            else
            {
                sL = s0;
                sR = s1;
            }

            float boostedL = SoftLimit(sL * g);
            float boostedR = SoftLimit(sR * g);
            procL[i] = boostedL;
            procR[i] = boostedR;

            float al = Math.Abs(boostedL);
            float ar = Math.Abs(boostedR);
            if (al > ml) ml = al;
            if (ar > mr) mr = ar;
        }

        maxL = ml;
        maxR = mr;
        clip = rawMax0 >= 0.99f || rawMax1 >= 0.99f || ml >= 0.985f || mr >= 0.985f;
    }
}

public sealed record RecordedTakeResult(
    string WavPath,
    long Frames,
    int SampleRate,
    double DurationSec,
    double MeasuredLatencyMs,
    double SyncMs);

/// <summary>
/// Lock-free recording session on top of <see cref="RecordingWriter"/>.
/// The ASIO callback pushes processed stereo blocks into a preallocated SPSC ring buffer via
/// <see cref="WriteFromCallback"/> without allocating or blocking; a background worker thread streams
/// them to disk and checkpoints the WAV header.
/// Implements the web app's take rules (<c>App.tsx</c>): takes shorter than <see cref="MinTakeDurationSec"/>
/// (0.3 s) or explicitly discarded are removed, and <c>syncMs</c> is computed from the applied round-trip latency.
/// </summary>
public sealed class TakeRecorder : IDisposable
{
    /// <summary>Takes shorter than 0.3 s are discarded (<c>App.tsx:985</c>).</summary>
    public const double MinTakeDurationSec = 0.3;

    static readonly Regex DefaultTrackNameRegex = new(@"^Pista \d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly RecordingWriter writer;
    readonly float[] ringL;
    readonly float[] ringR;
    readonly float[] tempL = new float[8192];
    readonly float[] tempR = new float[8192];
    readonly int ringCapacity;
    readonly Thread worker;

    long writeFrames;
    long readFrames;
    volatile bool stopping;
    bool finished;

    public string Path => writer.Path;
    public int SampleRate => writer.SampleRate;
    public long RecordedFrames => Interlocked.Read(ref writeFrames);
    public double RecordedSec => RecordedFrames / (double)SampleRate;
    public Exception? DiskError { get; private set; }

    public TakeRecorder(string path, int sampleRate, int ringSeconds = 6)
    {
        writer = new RecordingWriter(path, sampleRate);
        ringCapacity = Math.Max(65536, sampleRate * ringSeconds);
        ringL = new float[ringCapacity];
        ringR = new float[ringCapacity];
        worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "GuitarCam.TakeRecorder",
        };
        worker.Start();
    }

    /// <summary>
    /// Called on the ASIO driver thread: copies samples into the preallocated ring buffer with zero
    /// allocations and no locks.
    /// </summary>
    public void WriteFromCallback(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (stopping) return;
        int n = Math.Min(left.Length, right.Length);
        if (n <= 0) return;

        long w = Volatile.Read(ref writeFrames);
        long r = Volatile.Read(ref readFrames);
        int free = ringCapacity - (int)(w - r);
        if (n > free) n = free;
        if (n <= 0) return;

        int pos = (int)(w % ringCapacity);
        int first = Math.Min(n, ringCapacity - pos);
        left[..first].CopyTo(ringL.AsSpan(pos, first));
        right[..first].CopyTo(ringR.AsSpan(pos, first));
        int rem = n - first;
        if (rem > 0)
        {
            left.Slice(first, rem).CopyTo(ringL.AsSpan(0, rem));
            right.Slice(first, rem).CopyTo(ringR.AsSpan(0, rem));
        }

        Volatile.Write(ref writeFrames, w + n);
    }

    void WorkerLoop()
    {
        try
        {
            while (!stopping || Volatile.Read(ref readFrames) < Volatile.Read(ref writeFrames))
            {
                int drained = DrainOnce();
                if (drained == 0 && !stopping)
                    Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            DiskError = ex;
        }
    }

    int DrainOnce()
    {
        long w = Volatile.Read(ref writeFrames);
        long r = Volatile.Read(ref readFrames);
        int avail = (int)(w - r);
        if (avail <= 0) return 0;

        int total = 0;
        while (avail > 0)
        {
            int pos = (int)(r % ringCapacity);
            int chunk = Math.Min(avail, Math.Min(tempL.Length, ringCapacity - pos));
            ringL.AsSpan(pos, chunk).CopyTo(tempL.AsSpan(0, chunk));
            ringR.AsSpan(pos, chunk).CopyTo(tempR.AsSpan(0, chunk));
            writer.Append(tempL.AsSpan(0, chunk), tempR.AsSpan(0, chunk));
            r += chunk;
            total += chunk;
            Volatile.Write(ref readFrames, r);
            avail = (int)(Volatile.Read(ref writeFrames) - r);
        }
        return total;
    }

    /// <summary>
    /// Flushes all pending samples to disk, closes the WAV file, and returns the take result.
    /// Returns <c>null</c> (and deletes the WAV file) if <paramref name="discard"/> is true or the take is
    /// shorter than <see cref="MinTakeDurationSec"/> (0.3 s), matching <c>App.tsx:985</c>.
    /// </summary>
    public RecordedTakeResult? Finish(
        bool discard = false,
        double measuredLatencyMs = 0,
        double defaultOverdubOffsetMs = 0,
        bool isFirstTake = false,
        bool usedCountIn = true,
        bool usedClick = false)
    {
        if (finished) return null;
        finished = true;
        stopping = true;
        if (worker.IsAlive) worker.Join();
        long totalFrames = writer.Frames;
        writer.Dispose();

        double durationSec = totalFrames / (double)SampleRate;
        if (discard || durationSec <= MinTakeDurationSec)
        {
            TryDeleteFile(Path);
            return null;
        }

        double syncMs = ComputeSyncMs(isFirstTake, usedCountIn, usedClick, defaultOverdubOffsetMs, measuredLatencyMs);
        return new RecordedTakeResult(
            Path,
            totalFrames,
            SampleRate,
            durationSec,
            measuredLatencyMs,
            syncMs);
    }

    /// <summary>
    /// Port of <c>App.tsx:997-998</c>: a take recorded against a backing track, count-in, or click is pulled
    /// back by <c>defaultOverdubOffsetMs - measuredLatencyMs</c>; the very first take recorded free-time
    /// without count-in or click stays at 0 ms.
    /// </summary>
    public static double ComputeSyncMs(
        bool isFirstTake,
        bool usedCountIn,
        bool usedClick,
        double defaultOverdubOffsetMs,
        double measuredLatencyMs) =>
        (!isFirstTake || usedCountIn || usedClick) ? defaultOverdubOffsetMs - measuredLatencyMs : 0.0;

    /// <summary>
    /// Port of <c>App.tsx:1001-1005</c>: preserves a custom track name or builds
    /// <c>"Toma N — Base Rítmica / Solo / Punteo / Arreglo"</c> according to the lane slot.
    /// </summary>
    public static string DefaultTakeName(int takeNumber, int slot, string? existingTrackName = null)
    {
        if (!string.IsNullOrWhiteSpace(existingTrackName) && !DefaultTrackNameRegex.IsMatch(existingTrackName.Trim()))
            return existingTrackName.Trim();
        string roleLabel = slot == 0 ? "Base Rítmica" : slot == 1 ? "Solo / Punteo" : "Arreglo";
        return $"Toma {takeNumber} — {roleLabel}";
    }

    /// <summary>
    /// Port of <c>App.tsx:1027</c>: the second lane defaults to <c>"lead"</c> (Punteo), all other lanes to <c>"rhythm"</c> (Rasgueo).
    /// </summary>
    public static string DefaultCompPreset(int slot) => slot == 1 ? "lead" : "rhythm";

    static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Ignore cleanup failure.
        }
    }

    public void Dispose()
    {
        if (!finished)
            Finish(discard: true);
    }
}
