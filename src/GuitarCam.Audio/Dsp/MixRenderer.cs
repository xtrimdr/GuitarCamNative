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
    /// <summary>The clip's volume curve (<see cref="ClipGainCurve"/>); empty = flat.</summary>
    public ClipGainCurve.Point[] GainCurve { get; init; } = [];
    public double SyncMs { get; init; }
    /// <summary>Native only: the transition this clip comes in through (<see cref="ClipTransitions"/>); its sound crossfades.</summary>
    public string Transition { get; init; } = ClipTransitions.CutId;
    public double TransitionSec { get; init; } = ClipTransitions.DefaultSec;
    /// <summary>Its take (the media id): two pieces of one take that were one crossfade in straight lines.</summary>
    public string? TakeId { get; init; }

    public ClipEnvelope.ClipWindow GetWindow(int sampleRate)
    {
        double mediaDur = Left.Length / (double)sampleRate;
        double dur = DurationSec > 0 ? Math.Min(DurationSec, mediaDur) : mediaDur;
        return ClipEnvelope.Window(dur, AtSec, TrimStartSec, TrimEndSec, SyncMs);
    }
}

public sealed class MixTrackSpec
{
    /// <summary>The track's name, for its stem file.</summary>
    public string Name { get; init; } = "";
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
    public MasterParams Master { get; init; } = MasterParams.Off;
}

/// <summary>Receives a rendered stretch of the mix that starts at <paramref name="frame"/> (<see cref="MixRenderer.RenderTo"/>).</summary>
public delegate void MixSink(int frame, ReadOnlySpan<float> left, ReadOnlySpan<float> right);

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

    /// <summary>The sound of the transitions between a track's clips (<see cref="ClipTransitions"/>), for <see cref="SampleClips"/>.</summary>
    public static ClipAudioJoins?[] AudioJoins(MixClipSpec[] clips, int sampleRate)
    {
        var w = new ClipEnvelope.ClipWindow[clips.Length];
        var kinds = new TransitionKind[clips.Length];
        var secs = new double[clips.Length];
        var takes = new int[clips.Length];
        for (int i = 0; i < clips.Length; i++)
        {
            w[i] = clips[i].GetWindow(sampleRate);
            kinds[i] = ClipTransitions.Parse(clips[i].Transition);
            secs[i] = clips[i].TransitionSec;
            string? take = clips[i].TakeId;
            takes[i] = take == null ? -1 - i : Array.FindIndex(clips, c => c.TakeId == take);
        }
        return ClipAudioJoins.For(w, kinds, secs, takes);
    }

    /// <summary>
    /// Adds the clips' sound for frames <c>[posFrame .. posFrame + n)</c>: trim window, fades, gain and curve, and
    /// with <paramref name="joins"/> (<see cref="AudioJoins"/>) the transitions' crossfades, which play a clip past its
    /// edges into the audio its trims hide. Same math as the live engine (<c>LiveTrack.AddClips</c>).
    /// </summary>
    public static void SampleClips(
        ReadOnlySpan<MixClipSpec> clips,
        long posFrame,
        int n,
        int sampleRate,
        Span<float> dstL,
        Span<float> dstR,
        ReadOnlySpan<ClipAudioJoins?> joins = default)
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
            var join = cIdx < joins.Length ? joins[cIdx] : null;
            long startFrame = Math.Max(0, (long)Math.Round((join?.From(w.Start) ?? w.Start) * sampleRate));
            long endFrame = (long)Math.Round((join?.Until(w.End) ?? w.End) * sampleRate);
            if (posFrame + n <= startFrame || posFrame >= endFrame) continue;

            long originFrame = (long)Math.Round(w.Origin * sampleRate);
            var fades = ClipEnvelope.Fades(w, c.FadeInSec, c.FadeOutSec).Audio;
            float clipGain = (float)Math.Pow(10.0, c.GainDb / 20.0);
            var curve = c.GainCurve;
            float k = 1, dk = 0;
            float[] left = c.Left;
            float[] right = c.Right.Length == left.Length ? c.Right : left;

            for (int i = 0; i < n; i++)
            {
                long f = posFrame + i;
                // same steps as the live engine (LiveTrack), so the export sounds like playback
                if (curve.Length > 0 && i % ClipGainCurve.Step == 0)
                {
                    double t = (f - originFrame) * invSr;
                    k = (float)ClipGainCurve.GainAt(curve, t);
                    dk = ((float)ClipGainCurve.GainAt(curve, t + ClipGainCurve.Step * invSr) - k) / ClipGainCurve.Step;
                }
                if (f < startFrame || f >= endFrame) continue;
                long src = f - originFrame;
                if (src < 0 || src >= left.Length) continue;
                double timeSec = f * invSr;
                // past its edges (a transition's overlap) it keeps its edge's fade level, under the crossfade
                double g = join == null ? ClipEnvelope.GainAt(timeSec, w.Start, w.End, fades.FadeIn, fades.FadeOut)
                    : ClipEnvelope.GainAt(Math.Clamp(timeSec, w.Start, w.End), w.Start, w.End, fades.FadeIn, fades.FadeOut) * join.Gain(timeSec);
                float env = (float)g * clipGain * (k + dk * (i % ClipGainCurve.Step));
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
/// <see cref="ReverbBus"/> + <see cref="DelayBus"/> + <see cref="MasterChain"/> + Empuje (or the Destino's gain,
/// <see cref="MasterLoudness"/>) + <see cref="SoftClipper"/> + <see cref="MasterLimiter"/>, with preallocated
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
    /// <summary>The master's EQ and multiband, between the returns and the limiter.</summary>
    public MasterChain Master { get; }
    /// <summary>The master's soft clip, between Empuje and the limiter.</summary>
    public SoftClipper Clip { get; }
    /// <summary>The gain into the limiter (Empuje or the Destino's), linear: the target and where the glide toward it is.</summary>
    volatile float driveTarget = 1;
    float drive = 1;
    /// <summary>How long the gain takes to move (s): a new Destino measurement or the Empuje's knob glide in, never jump.</summary>
    const double DriveGlideSec = 0.05;
    readonly float driveGlide;

    public float ReverbMeterL { get; private set; }
    public float ReverbMeterR { get; private set; }
    public float DelayMeterL { get; private set; }
    public float DelayMeterR { get; private set; }

    public MixGraphBus(int sampleRate)
    {
        Reverb = new ReverbBus(sampleRate);
        Delay = new DelayBus(sampleRate);
        Master = new MasterChain(sampleRate);
        Clip = new SoftClipper(sampleRate);
        driveGlide = (float)(1 - Math.Exp(-1 / (DriveGlideSec * sampleRate)));
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
        SetMaster(spec.Master);
        Master.Reset();
        drive = driveTarget;
    }

    /// <summary>The master chain and the limiter's settings, live (both glide or swap without clicks).</summary>
    public void SetMaster(MasterParams p)
    {
        Master.Set(p);
        Limiter.Set(p.LimCeilingDb, p.LimReleaseMs);
        Limiter.Bypass = !p.LimOn;
        // the Empuje or the Destino's gain pushes into the clip and the limiter (with the limiter off, the Empuje would only
        // clip; the Destino's gain was measured to keep the peaks under the Techo then)
        Clip.Set(p);
        driveTarget = (float)Math.Pow(10, MasterLoudness.GainDb(p) / 20);
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
        Master.Reset();
        Clip.Reset();
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
        Span<float> dr,
        bool limit = true,
        bool master = true)
    {
        Reverb.Process(rl, rr);
        Delay.Process(dl, dr);
        MixReturnsAndLimit(masterL, masterR, rl, rr, dl, dr, limit, master);
    }

    /// <summary>
    /// The second half of <see cref="ProcessReturnsAndLimiter"/>: adds the returns <see cref="Reverb"/> and
    /// <see cref="Delay"/> already processed into the master, then the master chain, Empuje, clip and limiter. Apart
    /// so the export can run the returns of a stretch while the master works on the one before.
    /// </summary>
    public void MixReturnsAndLimit(
        Span<float> masterL,
        Span<float> masterR,
        ReadOnlySpan<float> rl,
        ReadOnlySpan<float> rr,
        ReadOnlySpan<float> dl,
        ReadOnlySpan<float> dr,
        bool limit = true,
        bool master = true)
    {
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
        if (master) Master.Process(masterL, masterR);
        if (limit)
        {
            float d = drive, t = driveTarget;
            if (d != 1 || t != 1)
            {
                float a = driveGlide;
                for (int i = 0; i < masterL.Length; i++)
                {
                    d += (t - d) * a;
                    masterL[i] *= d;
                    masterR[i] *= d;
                }
                if (Math.Abs(t - d) < 1e-5f) d = t;
            }
            drive = d;
            Clip.Process(masterL, masterR);
            Limiter.Process(masterL, masterR);
        }
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
    /// <summary>How much each stage of <see cref="RenderTo"/> renders at a time: long enough that handing it over costs
    /// nothing, short enough that the stages start working together right away (64 blocks, 0.17 s at 48 kHz).</summary>
    const int ChunkFrames = Quantum * 64;
    /// <summary>Stretches in flight: one per stage, so none waits for buffers.</summary>
    const int Sets = 3;

    /// <summary>A stretch of a track's output after its fader and its sends (what its blocks added into the master),
    /// or the sum of all of them.</summary>
    sealed class ChunkBuffers
    {
        public readonly float[] L = new float[ChunkFrames], R = new float[ChunkFrames];
        public readonly float[] RevL = new float[ChunkFrames], RevR = new float[ChunkFrames];
        public readonly float[] DlyL = new float[ChunkFrames], DlyR = new float[ChunkFrames];

        public void Clear(int n)
        {
            Array.Clear(L, 0, n);
            Array.Clear(R, 0, n);
            Array.Clear(RevL, 0, n);
            Array.Clear(RevR, 0, n);
            Array.Clear(DlyL, 0, n);
            Array.Clear(DlyR, 0, n);
        }

        public void Add(ChunkBuffers b, int n)
        {
            for (int i = 0; i < n; i++)
            {
                L[i] += b.L[i];
                R[i] += b.R[i];
                RevL[i] += b.RevL[i];
                RevR[i] += b.RevR[i];
                DlyL[i] += b.DlyL[i];
                DlyR[i] += b.DlyR[i];
            }
        }
    }

    /// <summary>One stretch in flight: each track's buffers and their sum (the master and the sends).</summary>
    sealed class ChunkSet(int tracks)
    {
        public readonly ChunkBuffers[] Tracks = Enumerable.Range(0, tracks).Select(_ => new ChunkBuffers()).ToArray();
        public readonly ChunkBuffers Sum = new();
    }

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

    /// <summary>The tracks that sound in the mix (mute and solo as the mixer has them): the ones that get a stem.</summary>
    public static IEnumerable<int> Audible(IReadOnlyList<MixTrackSpec> tracks)
    {
        bool anySolo = tracks.Any(t => t.Solo);
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Clips.Count > 0 && !tracks[i].Muted && (!anySolo || tracks[i].Solo)) yield return i;
    }

    /// <summary>
    /// Renders the mix. With <paramref name="only"/> it renders that track alone (a stem, native only): its chain and
    /// its sends through the reverb and delay returns, without the master limiter, so the stems add up to the mix
    /// before the limiter and none is squashed by it. <paramref name="limit"/> = false leaves the limiter out of the
    /// full mix too: <see cref="MixMaster"/> runs it afterwards with the loudness gain in front.
    /// </summary>
    public static (float[] Left, float[] Right) Render(
        IReadOnlyList<MixTrackSpec> tracks,
        MixBusSpec busSpec,
        double durationSec,
        int sampleRate = 48000,
        Action<double>? onProgress = null,
        CancellationToken ct = default,
        int only = -1,
        bool limit = true)
    {
        int frames = Math.Max(1, (int)Math.Ceiling(durationSec * sampleRate));
        var outL = new float[frames];
        var outR = new float[frames];
        RenderTo(tracks, busSpec, durationSec, (at, l, r) => { l.CopyTo(outL.AsSpan(at)); r.CopyTo(outR.AsSpan(at)); },
            sampleRate, onProgress, ct, only, limit);
        return (outL, outR);
    }

    /// <summary>
    /// <see cref="Render"/> without keeping the result: every rendered stretch goes to <paramref name="sink"/> in order,
    /// aligned to sample 0. <paramref name="master"/> = false leaves <see cref="MasterChain"/> out; stems never go
    /// through it, like they never go through the limiter.
    /// </summary>
    public static void RenderTo(
        IReadOnlyList<MixTrackSpec> tracks,
        MixBusSpec busSpec,
        double durationSec,
        MixSink sink,
        int sampleRate = 48000,
        Action<double>? onProgress = null,
        CancellationToken ct = default,
        int only = -1,
        bool limit = true,
        bool master = true)
    {
        var bus = new MixGraphBus(sampleRate);
        bus.Configure(busSpec, glideDelay: false);
        limit &= only < 0;
        master &= only < 0;
        int latency = limit ? bus.Limiter.Latency : 0;
        int frames = Math.Max(1, (int)Math.Ceiling(durationSec * sampleRate));
        int totalFrames = frames + latency;

        bool anySolo = false;
        foreach (var t in tracks)
            if (t.Solo) { anySolo = true; break; }

        var active = new List<(MixClipSpec[] Clips, ClipAudioJoins?[] Joins, TrackDspChain Chain)>();
        for (int k = 0; k < tracks.Count; k++)
        {
            var t = tracks[k];
            bool mute = t.Muted || (anySolo && !t.Solo);
            if (only >= 0 ? k != only : mute || t.Clips.Count == 0) continue;
            var chain = new TrackDspChain(sampleRate);
            chain.Configure(t);
            if (only >= 0) chain.Muted = chain.Solo = false;
            var clips = t.Clips.ToArray();
            active.Add((clips, TrackDspChain.AudioJoins(clips, sampleRate), chain));
        }
        if (only >= 0) anySolo = false;

        int chunks = (totalFrames + ChunkFrames - 1) / ChunkFrames;
        var sets = new ChunkSet[Math.Min(Sets, chunks)];
        for (int k = 0; k < sets.Length; k++) sets[k] = new ChunkSet(active.Count);
        int reportStep = Math.Max(sampleRate / 2, totalFrames / 40);
        int nextReport = reportStep;

        // three stages, each on its own thread and each over the stretches in order (the effects keep their state from
        // one to the next): the tracks (in parallel among them) -> the reverb and delay returns -> the master and the sink.
        // A stretch's buffers are free again once the master is done with it.
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = failed.Token;
        using var free = new SemaphoreSlim(sets.Length);
        using var tracksDone = new SemaphoreSlim(0);
        using var returnsDone = new SemaphoreSlim(0);
        Exception? error = null;
        void Stage(Action<int> run, SemaphoreSlim wait, SemaphoreSlim done)
        {
            try
            {
                for (int c = 0; c < chunks; c++)
                {
                    wait.Wait(token);
                    run(c);
                    done.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref error, e, null);
                failed.Cancel();
            }
        }

        void RunTracks(int c)
        {
            var set = sets[c % sets.Length];
            int at = c * ChunkFrames, len = Math.Min(ChunkFrames, totalFrames - at);
            void Track(int k)
            {
                var (clips, joins, chain) = active[k];
                var b = set.Tracks[k];
                b.Clear(len);
                for (int off = 0; off < len; off += Quantum)
                {
                    int n = Math.Min(Quantum, len - off);
                    TrackDspChain.SampleClips(clips, at + off, n, sampleRate, chain.BufferL(n), chain.BufferR(n), joins);
                    chain.ProcessFilledBuffer(n, anySolo, b.L.AsSpan(off, n), b.R.AsSpan(off, n), b.RevL.AsSpan(off, n),
                        b.RevR.AsSpan(off, n), b.DlyL.AsSpan(off, n), b.DlyR.AsSpan(off, n), out _, out _, out _);
                }
            }
            if (active.Count > 1) Parallel.For(0, active.Count, new ParallelOptions { CancellationToken = token }, Track);
            else if (active.Count == 1) Track(0);
            // summed in the tracks' order, so every sample is the same float the one-block-at-a-time loop gave
            set.Sum.Clear(len);
            foreach (var b in set.Tracks) set.Sum.Add(b, len);
        }

        void RunReturns(int c)
        {
            var sum = sets[c % sets.Length].Sum;
            int len = Math.Min(ChunkFrames, totalFrames - c * ChunkFrames);
            void Rev()
            {
                for (int off = 0; off < len; off += Quantum)
                {
                    int n = Math.Min(Quantum, len - off);
                    bus.Reverb.Process(sum.RevL.AsSpan(off, n), sum.RevR.AsSpan(off, n));
                }
            }
            void Dly()
            {
                for (int off = 0; off < len; off += Quantum)
                {
                    int n = Math.Min(Quantum, len - off);
                    bus.Delay.Process(sum.DlyL.AsSpan(off, n), sum.DlyR.AsSpan(off, n));
                }
            }
            Parallel.Invoke(new ParallelOptions { CancellationToken = token }, Rev, Dly);
        }

        void RunMaster(int c)
        {
            var sum = sets[c % sets.Length].Sum;
            int at = c * ChunkFrames, len = Math.Min(ChunkFrames, totalFrames - at);
            for (int off = 0; off < len; off += Quantum)
            {
                int n = Math.Min(Quantum, len - off), pos = at + off;
                var ml = sum.L.AsSpan(off, n);
                var mr = sum.R.AsSpan(off, n);
                bus.MixReturnsAndLimit(ml, mr, sum.RevL.AsSpan(off, n), sum.RevR.AsSpan(off, n), sum.DlyL.AsSpan(off, n), sum.DlyR.AsSpan(off, n), limit, master);

                int skip = Math.Max(0, latency - pos);
                int dst = pos + skip - latency;
                int count = Math.Min(n - skip, frames - dst);
                if (count > 0) sink(dst, ml.Slice(skip, count), mr.Slice(skip, count));

                if (onProgress != null && pos + n >= nextReport)
                {
                    onProgress(Math.Min(1.0, (pos + n) / (double)totalFrames));
                    nextReport += reportStep;
                }
            }
        }

        var tracksTask = Task.Factory.StartNew(() => Stage(RunTracks, free, tracksDone), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var returnsTask = Task.Factory.StartNew(() => Stage(RunReturns, tracksDone, returnsDone), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // the master runs here, so the sink and the progress are called on the caller's thread like before
        Stage(RunMaster, returnsDone, free);
        Task.WaitAll(tracksTask, returnsTask);
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        ct.ThrowIfCancellationRequested();

        onProgress?.Invoke(1.0);
    }
}
