namespace GuitarCam.Audio.Dsp;

public sealed class MixClipSpec
{
    public required float[] Left { get; init; }
    public required float[] Right { get; init; }
    public double DurationSec { get; init; }
    public double AtSec { get; init; }
    public double TrimStartSec { get; init; }
    public double TrimEndSec { get; init; }
    public double FadeInSec { get; init; }
    public double FadeOutSec { get; init; }
    public double GainDb { get; init; }
    public double SyncMs { get; init; }

    public ClipEnvelope.ClipWindow GetWindow(int sampleRate)
    {
        double mediaDur = Left.Length / (double)sampleRate;
        double dur = DurationSec > 0 ? Math.Min(DurationSec, mediaDur) : mediaDur;
        return ClipEnvelope.Window(dur, AtSec, TrimStartSec, TrimEndSec, SyncMs);
    }
}

public sealed class MixTrackSpec
{
    public IReadOnlyList<MixClipSpec> Clips { get; init; } = [];
    public double Volume { get; init; } = 1;
    public double Pan { get; init; }
    public bool Muted { get; init; }
    public bool Solo { get; init; }
    public SatParams Sat { get; init; } = SatParams.From(false, SatMode.Tube, 0.3);
    public CompState Comp { get; init; } = CompState.Defaults;
    public bool EqOn { get; init; }
    public EqBandSpec[] EqBands { get; init; } = TrackEq.DefaultBands;
    public bool ReverbOn { get; init; } = true;
    public double ReverbSend { get; init; }
    public bool DelayOn { get; init; } = true;
    public double DelaySend { get; init; }
}

public sealed class MixBusSpec
{
    public ReverbParams Reverb { get; init; } = ReverbParams.Defaults;
    public DelayParams Delay { get; init; } = DelayParams.Defaults;
    public double Bpm { get; init; } = 120;
}

/// <summary>
/// Per-track DSP chain shared by live ASIO playback (<c>PlaybackEngine</c>) and offline export render
/// (<see cref="MixRenderer"/>): clips with fade envelopes → <see cref="Saturator"/> →
/// <see cref="CompEqFilter"/> (pre) → <see cref="Compressor"/> → <see cref="CompEqFilter"/> (post) →
/// <see cref="TrackEq"/> → Web Audio <c>StereoPannerNode</c> → fader + post-fader reverb/delay sends.
/// Preallocates its buffers (8192 frames) so <see cref="ProcessBlock"/> never allocates.
/// </summary>
public sealed class TrackDspChain
{
    public const int MaxBlockSize = 8192;

    readonly int fs;
    readonly float[] bufL = new float[MaxBlockSize];
    readonly float[] bufR = new float[MaxBlockSize];

    public Saturator Sat { get; }
    public CompEqFilter CompEq { get; }
    public Compressor Comp { get; }
    public TrackEq Eq { get; }

    public double Volume { get; set; } = 1;
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public bool ReverbOn { get; set; } = true;
    public double ReverbSend { get; set; }
    public bool DelayOn { get; set; } = true;
    public double DelaySend { get; set; }
    volatile SpectrumTap? spectrum;
    /// <summary>Post-EQ, pre-fader tap for the EQ editor's live spectrum (the web's per-track analyser); null when nobody reads it.</summary>
    public SpectrumTap? Spectrum { get => spectrum; set => spectrum = value; }

    public TrackDspChain(int sampleRate)
    {
        fs = sampleRate;
        Sat = new Saturator(sampleRate);
        CompEq = new CompEqFilter(sampleRate);
        Comp = new Compressor(sampleRate);
        Eq = new TrackEq(sampleRate);
    }

    static double F(double v) => (float)v;

    public void Configure(MixTrackSpec spec)
    {
        Volume = spec.Volume;
        Pan = spec.Pan;
        Muted = spec.Muted;
        Solo = spec.Solo;
        ReverbOn = spec.ReverbOn;
        ReverbSend = spec.ReverbSend;
        DelayOn = spec.DelayOn;
        DelaySend = spec.DelaySend;

        ApplySat(spec.Sat);
        ApplyComp(spec.Comp);
        Eq.Set(spec.EqOn, spec.EqBands);
    }

    public void ApplySat(SatParams p)
    {
        Sat.Set(p with
        {
            Drive = F(p.Drive),
            Bias = F(p.Bias),
            Tone = F(p.Tone),
            ToneMix = F(p.ToneMix),
            Bump = F(p.Bump),
        });
    }

    public void ApplyComp(CompState c)
    {
        CompEq.Set(c.On, c.Eq);
        Comp.Bypass = !c.On;
        Comp.Threshold = F(c.Threshold);
        Comp.Ratio = F(c.Ratio);
        Comp.Knee = F(c.Knee);
        Comp.Attack = F(c.Attack);
        Comp.Release = F(c.Release);
        Comp.Makeup = F(c.Makeup);
        Comp.Limit = c.Limit;
        Comp.Ceiling = F(CompPresets.TrackLimitCeilingDb);
    }

    public void Reset()
    {
        Sat.Reset();
        CompEq.Reset();
        Comp.Reset();
        Eq.Reset();
    }

    public static void SampleClips(
        ReadOnlySpan<MixClipSpec> clips,
        long posFrame,
        int n,
        int sampleRate,
        Span<float> dstL,
        Span<float> dstR)
    {
        dstL.Clear();
        dstR.Clear();
        double invSr = 1.0 / sampleRate;
        for (int cIdx = 0; cIdx < clips.Length; cIdx++)
        {
            var c = clips[cIdx];
            if (c.Left.Length == 0) continue;
            var w = c.GetWindow(sampleRate);
            if (w.End <= w.Start) continue;
            long startFrame = Math.Max(0, (long)Math.Round(w.Start * sampleRate));
            long endFrame = (long)Math.Round(w.End * sampleRate);
            if (posFrame + n <= startFrame || posFrame >= endFrame) continue;

            long originFrame = (long)Math.Round(w.Origin * sampleRate);
            var fades = ClipEnvelope.Fades(w, c.FadeInSec, c.FadeOutSec).Audio;
            float clipGain = (float)Math.Pow(10.0, c.GainDb / 20.0);
            float[] left = c.Left;
            float[] right = c.Right.Length == left.Length ? c.Right : left;

            for (int i = 0; i < n; i++)
            {
                long f = posFrame + i;
                if (f < startFrame || f >= endFrame) continue;
                long src = f - originFrame;
                if (src < 0 || src >= left.Length) continue;
                double timeSec = f * invSr;
                float env = (float)ClipEnvelope.GainAt(timeSec, w.Start, w.End, fades.FadeIn, fades.FadeOut) * clipGain;
                dstL[i] += left[src] * env;
                dstR[i] += right[src] * env;
            }
        }
    }

    public Span<float> BufferL(int n) => bufL.AsSpan(0, n);
    public Span<float> BufferR(int n) => bufR.AsSpan(0, n);

    /// <summary>
    /// Runs the track's full insert chain on <paramref name="bl"/> / <paramref name="br"/> (already filled
    /// with the track's source clips for the block) and accumulates the post-fader output into
    /// <paramref name="masterL"/>/<paramref name="masterR"/> and the reverb/delay send buffers.
    /// </summary>
    public void ProcessFilledBuffer(
        int n,
        bool anySolo,
        Span<float> masterL,
        Span<float> masterR,
        Span<float> reverbInL,
        Span<float> reverbInR,
        Span<float> delayInL,
        Span<float> delayInR,
        out float peakL,
        out float peakR,
        out double grDb)
    {
        var bl = bufL.AsSpan(0, n);
        var br = bufR.AsSpan(0, n);

        Sat.Process(bl, br);
        CompEq.ProcessWith(Comp, bl, br);
        grDb = Comp.TakeMeter();
        Eq.Process(bl, br);
        spectrum?.Write(bl, br);

        bool mute = Muted || (anySolo && !Solo);
        float vol = mute ? 0f : (float)Volume;
        double pan = Math.Clamp(Pan, -1, 1);
        double x = pan <= 0 ? pan + 1 : pan;
        float gl = (float)Math.Cos(x * Math.PI / 2.0);
        float gr = (float)Math.Sin(x * Math.PI / 2.0);
        float revSend = ReverbOn ? (float)Math.Clamp(ReverbSend, 0, 1) : 0f;
        float dlySend = DelayOn ? (float)Math.Clamp(DelaySend, 0, 1) : 0f;

        float pkL = 0, pkR = 0;
        for (int i = 0; i < n; i++)
        {
            float l = bl[i], r = br[i];
            float ol = (pan <= 0 ? l + r * gl : l * gl) * vol;
            float or = (pan <= 0 ? r * gr : r + l * gr) * vol;
            masterL[i] += ol;
            masterR[i] += or;
            reverbInL[i] += ol * revSend;
            reverbInR[i] += or * revSend;
            delayInL[i] += ol * dlySend;
            delayInR[i] += or * dlySend;
            if (Math.Abs(ol) > pkL) pkL = Math.Abs(ol);
            if (Math.Abs(or) > pkR) pkR = Math.Abs(or);
        }
        peakL = pkL;
        peakR = pkR;
    }
}

/// <summary>
/// Master and return buses shared by live playback and offline export render:
/// <see cref="ReverbBus"/> + <see cref="DelayBus"/> + <see cref="MasterLimiter"/>, with preallocated
/// send accumulation buffers (8192 frames) so the audio callback never allocates.
/// </summary>
public sealed class MixGraphBus
{
    readonly float[] revInL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] revInR = new float[TrackDspChain.MaxBlockSize];
    readonly float[] dlyInL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] dlyInR = new float[TrackDspChain.MaxBlockSize];

    public ReverbBus Reverb { get; }
    public DelayBus Delay { get; }
    public MasterLimiter Limiter { get; }

    public float ReverbMeterL { get; private set; }
    public float ReverbMeterR { get; private set; }
    public float DelayMeterL { get; private set; }
    public float DelayMeterR { get; private set; }

    public MixGraphBus(int sampleRate)
    {
        Reverb = new ReverbBus(sampleRate);
        Delay = new DelayBus(sampleRate);
        int lookahead = (int)Math.Round(MasterLimiter.LookaheadSec * sampleRate);
        Limiter = new MasterLimiter(sampleRate, lookahead, MasterLimiter.CeilingDb);
    }

    public Span<float> PrepareReverbL(int n) { var s = revInL.AsSpan(0, n); s.Clear(); return s; }
    public Span<float> PrepareReverbR(int n) { var s = revInR.AsSpan(0, n); s.Clear(); return s; }
    public Span<float> PrepareDelayL(int n) { var s = dlyInL.AsSpan(0, n); s.Clear(); return s; }
    public Span<float> PrepareDelayR(int n) { var s = dlyInR.AsSpan(0, n); s.Clear(); return s; }

    public void Configure(MixBusSpec spec, bool glideDelay = false)
    {
        Reverb.Set(spec.Reverb);
        Delay.Set(spec.Delay, spec.Bpm, glide: glideDelay);
    }

    public void ConfigureLive(MixBusSpec spec, bool glideDelay = true)
    {
        Reverb.SetLive(spec.Reverb);
        Delay.Set(spec.Delay, spec.Bpm, glide: glideDelay);
    }

    public void Reset()
    {
        Reverb.Reset();
        Delay.Reset();
        Limiter.Reset();
    }

    public (float RevL, float RevR, float DlyL, float DlyR) TakeReturnMeters()
    {
        var m = (ReverbMeterL, ReverbMeterR, DelayMeterL, DelayMeterR);
        ReverbMeterL = ReverbMeterR = DelayMeterL = DelayMeterR = 0;
        return m;
    }

    public void ProcessReturnsAndLimiter(
        Span<float> masterL,
        Span<float> masterR,
        Span<float> rl,
        Span<float> rr,
        Span<float> dl,
        Span<float> dr)
    {
        Reverb.Process(rl, rr);
        Delay.Process(dl, dr);
        float rPkL = ReverbMeterL, rPkR = ReverbMeterR;
        float dPkL = DelayMeterL, dPkR = DelayMeterR;
        for (int i = 0; i < masterL.Length; i++)
        {
            float rvL = rl[i], rvR = rr[i];
            float dyL = dl[i], dyR = dr[i];
            if (Math.Abs(rvL) > rPkL) rPkL = Math.Abs(rvL);
            if (Math.Abs(rvR) > rPkR) rPkR = Math.Abs(rvR);
            if (Math.Abs(dyL) > dPkL) dPkL = Math.Abs(dyL);
            if (Math.Abs(dyR) > dPkR) dPkR = Math.Abs(dyR);
            masterL[i] += rvL + dyL;
            masterR[i] += rvR + dyR;
        }
        ReverbMeterL = rPkL;
        ReverbMeterR = rPkR;
        DelayMeterL = dPkL;
        DelayMeterR = dPkR;
        Limiter.Process(masterL, masterR);
    }
}

/// <summary>
/// Port of <c>renderMix</c> in <c>GuitarCam/src/audio/mixGraph.ts</c>: renders the whole mix offline
/// through <see cref="TrackDspChain"/> and <see cref="MixGraphBus"/> (the exact same DSP graph as live
/// playback) and strips the master limiter's look-ahead latency so the rendered audio aligns with sample 0.
/// <para>
/// Known difference vs. the web app: <see cref="TrackDspChain.SampleClips"/> rounds clip <c>Origin</c>,
/// <c>Start</c> and <c>End</c> to the nearest sample index (up to 0.5 sample offset when <c>SyncMs</c> is
/// fractional, e.g. 12.34 ms), whereas Chromium's <c>AudioBufferSourceNode.start()</c> interpolates
/// sub-sample start offsets.
/// </para>
/// </summary>
public static class MixRenderer
{
    const int Quantum = 128;

    /// <summary>Port of <c>mixDurationSec</c> in <c>mixGraph.ts</c>: end of the last clip (at least 1 s).</summary>
    public static double MixDurationSec(IEnumerable<MixTrackSpec> tracks, int sampleRate)
    {
        double maxEnd = 1.0;
        foreach (var t in tracks)
        {
            foreach (var c in t.Clips)
            {
                var w = c.GetWindow(sampleRate);
                if (w.End > maxEnd) maxEnd = w.End;
            }
        }
        return maxEnd;
    }

    public static (float[] Left, float[] Right) Render(
        IReadOnlyList<MixTrackSpec> tracks,
        MixBusSpec busSpec,
        double durationSec,
        int sampleRate = 48000,
        Action<double>? onProgress = null,
        CancellationToken ct = default)
    {
        var bus = new MixGraphBus(sampleRate);
        bus.Configure(busSpec, glideDelay: false);
        int latency = bus.Limiter.Latency;
        int frames = Math.Max(1, (int)Math.Ceiling(durationSec * sampleRate));
        int totalFrames = frames + latency;

        bool anySolo = false;
        foreach (var t in tracks)
            if (t.Solo) { anySolo = true; break; }

        var active = new List<(MixClipSpec[] Clips, TrackDspChain Chain)>();
        foreach (var t in tracks)
        {
            bool mute = t.Muted || (anySolo && !t.Solo);
            if (mute || t.Clips.Count == 0) continue;
            var chain = new TrackDspChain(sampleRate);
            chain.Configure(t);
            active.Add((t.Clips.ToArray(), chain));
        }

        var outL = new float[frames];
        var outR = new float[frames];
        var masterL = new float[Quantum];
        var masterR = new float[Quantum];

        int reportStep = Math.Max(sampleRate / 2, totalFrames / 40);
        int nextReport = reportStep;

        for (int pos = 0; pos < totalFrames; pos += Quantum)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(Quantum, totalFrames - pos);
            var ml = masterL.AsSpan(0, n);
            var mr = masterR.AsSpan(0, n);
            ml.Clear();
            mr.Clear();
            var rl = bus.PrepareReverbL(n);
            var rr = bus.PrepareReverbR(n);
            var dl = bus.PrepareDelayL(n);
            var dr = bus.PrepareDelayR(n);

            foreach (var (clips, chain) in active)
            {
                var bl = chain.BufferL(n);
                var br = chain.BufferR(n);
                TrackDspChain.SampleClips(clips, pos, n, sampleRate, bl, br);
                chain.ProcessFilledBuffer(n, anySolo, ml, mr, rl, rr, dl, dr, out _, out _, out _);
            }

            bus.ProcessReturnsAndLimiter(ml, mr, rl, rr, dl, dr);

            for (int i = 0; i < n; i++)
            {
                int dst = pos + i - latency;
                if (dst >= 0 && dst < frames)
                {
                    outL[dst] = ml[i];
                    outR[dst] = mr[i];
                }
            }

            if (onProgress != null && pos + n >= nextReport)
            {
                onProgress(Math.Min(1.0, (pos + n) / (double)totalFrames));
                nextReport += reportStep;
            }
        }

        onProgress?.Invoke(1.0);
        return (outL, outR);
    }
}
