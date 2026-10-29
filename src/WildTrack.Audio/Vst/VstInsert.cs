using WildTrack.Audio.Dsp;

namespace WildTrack.Audio.Vst;

/// <summary>
/// A VST3 effect slot's settings as the engine takes them: which plugin (and its saved state, for the export), whether
/// it is on, and either the running process the window keeps for it (<see cref="Live"/>; null while it loads or if it
/// couldn't open) or <see cref="Offline"/>, for the export, where the slot opens its own.
/// </summary>
public sealed record VstSlotSpec(VstInstance Plugin, bool On)
{
    public VstBridge? Live { get; init; }
    public bool Offline { get; init; }
}

/// <summary>
/// A VST3 effect as one more insert slot (<see cref="ITrackInsert"/>), mixed with the built-in ones in any order.
/// <para>
/// Live, the audio thread never waits for the plugin's process (<see cref="VstBridge"/>): each block it takes what the
/// plugin made of the block before and sends this one. What comes back waits in a queue that starts with one driver
/// block (<see cref="LiveBlock"/>) of silence, so the slot's output is always its input exactly one block plus the
/// plugin's own latency later, whatever the size of each call (the first block of a recording after its count-in is
/// shorter): that is its <see cref="LatencySamples"/>, which the rest of the mix waits for (<see cref="MixLatency"/>).
/// A block the plugin didn't finish in time is silence (counted in <see cref="Misses"/>); nothing else stops.
/// </para>
/// <para>
/// Off, while it loads or after it crashed, the sound goes through untouched (delayed as much as the plugin would, so
/// switching never jumps in time) with a <see cref="WetFade"/> crossfade; off, the plugin keeps getting the sound, so
/// turning it on again is seamless. In the export (<see cref="VstSlotSpec.Offline"/>) it opens its own process with the
/// saved state and waits for every block: no extra block, only the plugin's latency.
/// </para>
/// </summary>
public sealed class VstInsert : ITrackInsert, IDisposable
{
    /// <summary>10 ms at 48 kHz: switching between the plugin and the untouched sound.</summary>
    const int WetFade = 480;
    const int MaxBlock = VstBridge.MaxBlock;
    /// <summary>The queue of what came back: a block waiting, plus the next one.</summary>
    const int QueueSize = 4 * MaxBlock;

    static volatile int liveBlock = 512;
    /// <summary>The driver's block (frames), set by the engine when it opens: live, an effect is heard this much late.</summary>
    public static int LiveBlock { get => liveBlock; set => liveBlock = Math.Clamp(value, 1, MaxBlock); }

    readonly int sampleRate;
    volatile VstBridge? bridge;
    VstBridge? own;
    volatile bool on = true;
    double tempo = 120;
    volatile int reset;

    public VstInsert(int sampleRate) => this.sampleRate = sampleRate;

    public string Kind => TrackInserts.Vst;

    /// <summary>The process it plays through: the window's, or its own in the export; null while there is none.</summary>
    public VstBridge? Bridge => own ?? bridge;

    /// <summary>Blocks the plugin didn't finish in time (live), for the diagnostics.</summary>
    public int Misses;

    public int LatencySamples => own is { } o ? o.Latency : bridge is { } b ? LiveBlock + b.Latency : 0;

    /// <summary>
    /// Takes the slot's settings (UI thread; the export's thread there). Offline, the first call opens the plugin with its
    /// saved state and waits until it is up: it throws if it can't.
    /// </summary>
    public void Set(VstSlotSpec spec, double bpm)
    {
        on = spec.On;
        tempo = bpm;
        if (spec.Offline)
        {
            if (own != null) return;
            var p = spec.Plugin;
            VstBridge b;
            try
            {
                b = VstBridge.Start(p.Path, p.ClassId, p.Name, sampleRate, offline: true);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException(Texts.T("err.vst.export", "No se pudo abrir {0} para exportar: {1}", p.Name, e.Message), e);
            }
            if (p.State is { Length: > 0 } state && !b.SetState(state))
            {
                b.Dispose();
                throw new InvalidOperationException(Texts.T("err.vst.state", "{0} no aceptó su configuración guardada.", p.Name));
            }
            own = b;
            dry.Set(b.Latency);
            return;
        }
        if (spec.Live is { } live) dry.Reserve(MaxBlock + live.Latency);
        bridge = spec.Live;
    }

    // live, audio thread only
    VstBridge? seen;
    readonly float[] queueL = new float[QueueSize], queueR = new float[QueueSize];
    int queueRead, queued, primed;
    long pending;
    int pendingFrames;
    /// <summary>Frames the plugin still has to get before what it puts out is its own (after it starts or comes back).</summary>
    int warm;
    float wet;
    long time;
    readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
    readonly float[] takeL = new float[MaxBlock], takeR = new float[MaxBlock];
    readonly LatencyDelay dry = new();

    public void Process(Span<float> left, Span<float> right)
    {
        if (own != null) Render(left, right);
        else Live(left, right);
    }

    void Live(Span<float> left, Span<float> right)
    {
        int m = Math.Min(left.Length, MaxBlock);
        var b = bridge;
        int block = liveBlock;
        if (b != seen)
        {
            // another process (it loaded, crashed or opened again): its blocks count from zero
            seen = b;
            StartOver(block);
            warm = b == null ? 0 : block + b.Latency;
        }
        else if (block != primed || Interlocked.Exchange(ref reset, 0) != 0) StartOver(block);
        if (b != null)
        {
            if (pending > 0)
            {
                int got = b.TryTake(pending, takeL, takeR);
                if (got > 0) Push(takeL, takeR, got);
                else
                {
                    Misses++;
                    Push(null, null, pendingFrames);
                }
                pending = 0;
            }
            long sent = b.TrySend(left[..m], right[..m], m, time, tempo, VstBridge.FlagPlaying | flags);
            if (sent > 0)
            {
                pending = sent;
                pendingFrames = m;
                flags = 0;
                if (warm > 0) warm -= m;
            }
            else Push(null, null, m);
        }
        time += m;

        // the untouched sound, as late as the plugin's
        dry.Target = b == null ? 0 : block + b.Latency;
        var dl = dryL.AsSpan(0, m);
        var dr = dryR.AsSpan(0, m);
        dl.Clear();
        dr.Clear();
        dry.Add(left[..m], right[..m], 1f, dl, dr);

        float target = on && b != null && b.Ready && warm <= 0 ? 1 : 0;
        if (b == null) Pop(m, left, right, silent: true);
        else Pop(m, left, right, silent: false);
        float w = wet, step = 1f / WetFade;
        if (w == target && w == 0)
        {
            dl.CopyTo(left);
            dr.CopyTo(right);
            return;
        }
        for (int i = 0; i < m; i++)
        {
            if (w != target) w = target > w ? Math.Min(target, w + step) : Math.Max(target, w - step);
            left[i] = dl[i] + (left[i] - dl[i]) * w;
            right[i] = dr[i] + (right[i] - dr[i]) * w;
        }
        wet = w;
    }

    int flags;

    /// <summary>The queue starts over with one block of silence: from here on, the output is its input that much later.</summary>
    void StartOver(int block)
    {
        queueRead = queued = 0;
        pending = 0;
        primed = block;
        Push(null, null, block);
        flags |= VstBridge.FlagReset;
    }

    /// <summary>Adds <paramref name="n"/> frames to the queue (silence when <paramref name="l"/> is null).</summary>
    void Push(float[]? l, float[]? r, int n)
    {
        n = Math.Min(n, QueueSize - queued);
        int at = (queueRead + queued) % QueueSize;
        for (int i = 0; i < n; i++)
        {
            queueL[at] = l == null ? 0 : l[i];
            queueR[at] = r == null ? 0 : r![i];
            if (++at == QueueSize) at = 0;
        }
        queued += n;
    }

    /// <summary>The next <paramref name="m"/> frames of the queue into the block (silence where it ran short).</summary>
    void Pop(int m, Span<float> l, Span<float> r, bool silent)
    {
        int n = Math.Min(m, queued);
        for (int i = 0; i < n; i++)
        {
            l[i] = silent ? 0 : queueL[queueRead];
            r[i] = silent ? 0 : queueR[queueRead];
            if (++queueRead == QueueSize) queueRead = 0;
        }
        queued -= n;
        l[n..m].Clear();
        r[n..m].Clear();
    }

    /// <summary>The export: each block through the plugin, waiting for it; off, the untouched sound as late as the plugin's.</summary>
    void Render(Span<float> left, Span<float> right)
    {
        var b = own!;
        if (!on)
        {
            int m = Math.Min(left.Length, MaxBlock);
            var dl = dryL.AsSpan(0, m);
            var dr = dryR.AsSpan(0, m);
            dl.Clear();
            dr.Clear();
            dry.Add(left[..m], right[..m], 1f, dl, dr);
            dl.CopyTo(left);
            dr.CopyTo(right);
            return;
        }
        for (int off = 0; off < left.Length; off += MaxBlock)
        {
            int len = Math.Min(MaxBlock, left.Length - off);
            var l = left.Slice(off, len);
            var r = right.Slice(off, len);
            b.Render([], len, time, tempo, VstBridge.FlagPlaying, l, r, l, r);
            time += len;
        }
    }

    /// <summary>The transport starts: the plugin forgets its tails and the queue its old blocks (audio thread).</summary>
    public void Reset()
    {
        reset = 1;
        dry.Reset();
    }

    public double TakeGrDb() => 0;

    /// <summary>Closes its own process (the export's); the window's belongs to the window.</summary>
    public void Dispose()
    {
        own?.Dispose();
        own = null;
    }
}
