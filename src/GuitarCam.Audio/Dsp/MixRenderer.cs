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
    /// <summary>The track's insert slots in order; null = the fixed chain from <see cref="Sat"/>, <see cref="Comp"/> and the EQ.</summary>
    public IReadOnlyList<InsertSpec>? Inserts { get; init; }
    /// <summary>Its folder's bus (an index into <see cref="MixBusSpec.Folders"/>), -1 = none: its post-fader output goes
    /// through that folder's slots on its way to the master. Its sends don't.</summary>
    public int Folder { get; init; } = -1;
    public bool ReverbOn { get; init; } = true;
    public double ReverbSend { get; init; }
    public bool DelayOn { get; init; } = true;
    public double DelaySend { get; init; }
    /// <summary>The reverb and delay sends taken before the fader (after the slots and the pan); false = after it.</summary>
    public bool ReverbPre { get; init; }
    public bool DelayPre { get; init; }
    /// <summary>Its sends to the extra returns (<see cref="MixBusSpec.Returns"/>).</summary>
    public IReadOnlyList<MixSendSpec> Sends { get; init; } = [];
}

/// <summary>A track's send to an extra return: <see cref="Return"/> is an index into <see cref="MixBusSpec.Returns"/>.</summary>
public sealed class MixSendSpec
{
    public int Return { get; init; }
    public double Level { get; init; }
    public bool On { get; init; } = true;
    /// <summary>Taken before the fader (after the slots and the pan, still silenced by mute and solo).</summary>
    public bool Pre { get; init; }
}

/// <summary>An extra return (native only): what the tracks send to it goes through its slots and its fader into the master.</summary>
public sealed class MixReturnSpec
{
    public string Name { get; init; } = "";
    public double Volume { get; init; } = 1;
    public bool Muted { get; init; }
    public IReadOnlyList<InsertSpec> Inserts { get; init; } = [];
}

public sealed class MixBusSpec
{
    public ReverbParams Reverb { get; init; } = ReverbParams.Defaults;
    public DelayParams Delay { get; init; } = DelayParams.Defaults;
    public double Bpm { get; init; } = 120;
    public MasterParams Master { get; init; } = MasterParams.Off;
    /// <summary>The folders' buses (<see cref="MixTrackSpec.Folder"/> points here). One without slots is no bus at all.</summary>
    public IReadOnlyList<MixFolderSpec> Folders { get; init; } = [];
    /// <summary>The extra returns, besides the fixed reverb and delay (<see cref="MixTrackSpec.Sends"/> point here).</summary>
    public IReadOnlyList<MixReturnSpec> Returns { get; init; } = [];
}

/// <summary>A folder's bus: the slots its tracks' summed output goes through before the master (native only).</summary>
public sealed class MixFolderSpec
{
    public string Name { get; init; } = "";
    public IReadOnlyList<InsertSpec> Inserts { get; init; } = [];
}

/// <summary>Receives a rendered stretch of the mix that starts at <paramref name="frame"/> (<see cref="MixRenderer.RenderTo"/>).</summary>
public delegate void MixSink(int frame, ReadOnlySpan<float> left, ReadOnlySpan<float> right);

/// <summary>
/// Per-track DSP chain shared by live ASIO playback (<c>PlaybackEngine</c>) and offline export render
/// (<see cref="MixRenderer"/>): clips with fade envelopes → the track's insert slots (<see cref="ITrackInsert"/>, by
/// default Vida → compressor with its preset EQ → 6-band EQ) → Web Audio <c>StereoPannerNode</c> → fader + the
/// reverb/delay sends, each post-fader (by default) or pre-fader (after the slots and the pan, before the fader; mute
/// and solo still silence it). Preallocates its buffers (8192 frames) so <see cref="ProcessFilledBuffer"/> never allocates.
/// The slots are an array the UI replaces whole (<see cref="SetInserts"/>); when the list itself changes (a slot added,
/// removed or moved) the audio thread dips the track for <see cref="SwapFade"/> samples on each side of the swap, so
/// the new chain comes in without a click. The sends to the extra returns (<see cref="Sends"/>) are an immutable array
/// swapped whole too: while there are any, the block's pre- and post-fader signals are kept for <see cref="AddSend"/>.
/// </summary>
public sealed class TrackDspChain
{
    public const int MaxBlockSize = 8192;
    /// <summary>5 ms at 48 kHz: the fade out of the old slots and the fade in of the new ones when the list changes.</summary>
    public const int SwapFade = 240;

    readonly float[] bufL = new float[MaxBlockSize];
    readonly float[] bufR = new float[MaxBlockSize];
    // the last block's pre-fader and post-fader signals, kept only while there are sends to extra returns
    readonly float[] preL = new float[MaxBlockSize];
    readonly float[] preR = new float[MaxBlockSize];
    readonly float[] postL = new float[MaxBlockSize];
    readonly float[] postR = new float[MaxBlockSize];
    readonly InsertRack rack;
    volatile TrackSend[] sends = [];

    /// <summary>Its sends to the extra returns, swapped whole by the UI (never changed in place).</summary>
    public TrackSend[] Sends { get => sends; set => sends = value; }
    /// <summary>
    /// Audio thread: the sends the last <see cref="ProcessFilledBuffer"/> kept its signals for. The caller adds these
    /// (<see cref="AddSend"/>), not <see cref="Sends"/>, which the UI may have swapped in the meantime.
    /// </summary>
    public TrackSend[] BlockSends { get; private set; } = [];
    public bool ReverbPre { get; set; }
    public bool DelayPre { get; set; }

    public double Volume { get; set; } = 1;
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public bool ReverbOn { get; set; } = true;
    public double ReverbSend { get; set; }
    public bool DelayOn { get; set; } = true;
    public double DelaySend { get; set; }
    volatile SpectrumTap? spectrum;
    /// <summary>Post-inserts, pre-fader tap for the EQ editor's live spectrum (the web's per-track analyser); null when nobody reads it.</summary>
    public SpectrumTap? Spectrum { get => spectrum; set => spectrum = value; }

    public TrackDspChain(int sampleRate)
    {
        rack = new InsertRack(sampleRate);
        SetInserts(TrackInserts.Legacy(SatParams.From(false, SatMode.Tube, 0.3), CompState.Defaults, false, TrackEq.DefaultBands), fade: false);
    }

    /// <summary>The inserts the UI last set, in order (the audio thread may still be fading over to them).</summary>
    public IReadOnlyList<ITrackInsert> Inserts => rack.Inserts;

    /// <summary>How late the slots' output comes out, all added up. Zero while every slot is a built-in effect.</summary>
    public int LatencySamples => rack.LatencySamples;

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
        ReverbPre = spec.ReverbPre;
        DelayPre = spec.DelayPre;
        SetInserts(spec.Inserts ?? TrackInserts.Legacy(spec.Sat, spec.Comp, spec.EqOn, spec.EqBands), fade: false);
    }

    /// <summary>
    /// Takes the slots' settings (UI thread): inserts with the same id keep their state; a new list is handed to the audio
    /// thread whole. <paramref name="fade"/> = false swaps at once, only for a chain the audio thread isn't running yet.
    /// </summary>
    public void SetInserts(IReadOnlyList<InsertSpec> specs, bool fade = true) => rack.Set(specs, fade);

    public void Reset() => rack.Reset();

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

    /// <summary>The post-fader output's RMS, for the channel's mixer meter.</summary>
    public RmsTap Rms { get; } = new();

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

        grDb = rack.Process(bl, br);
        spectrum?.Write(bl, br);

        bool mute = Muted || (anySolo && !Solo);
        float vol = mute ? 0f : (float)Volume;
        double pan = Math.Clamp(Pan, -1, 1);
        double x = pan <= 0 ? pan + 1 : pan;
        float gl = (float)Math.Cos(x * Math.PI / 2.0);
        float gr = (float)Math.Sin(x * Math.PI / 2.0);
        float revSend = ReverbOn ? (float)Math.Clamp(ReverbSend, 0, 1) : 0f;
        float dlySend = DelayOn ? (float)Math.Clamp(DelaySend, 0, 1) : 0f;
        // pre-fader: after the slots and the pan, before the fader (and the folder's VCA), still silenced by mute / solo
        float preGain = mute ? 0f : 1f;
        bool revPre = ReverbPre, dlyPre = DelayPre;
        var blockSends = sends;
        BlockSends = blockSends;
        bool keep = blockSends.Length > 0;

        float pkL = 0, pkR = 0, sqL = 0, sqR = 0;
        for (int i = 0; i < n; i++)
        {
            float l = bl[i], r = br[i];
            float xl = pan <= 0 ? l + r * gl : l * gl;
            float xr = pan <= 0 ? r * gr : r + l * gr;
            float ol = xl * vol;
            float or = xr * vol;
            float pl = xl * preGain, pr = xr * preGain;
            masterL[i] += ol;
            masterR[i] += or;
            reverbInL[i] += (revPre ? pl : ol) * revSend;
            reverbInR[i] += (revPre ? pr : or) * revSend;
            delayInL[i] += (dlyPre ? pl : ol) * dlySend;
            delayInR[i] += (dlyPre ? pr : or) * dlySend;
            if (keep)
            {
                preL[i] = pl; preR[i] = pr;
                postL[i] = ol; postR[i] = or;
            }
            if (Math.Abs(ol) > pkL) pkL = Math.Abs(ol);
            if (Math.Abs(or) > pkR) pkR = Math.Abs(or);
            sqL += ol * ol;
            sqR += or * or;
        }
        Rms.Add(sqL, sqR, n);
        peakL = pkL;
        peakR = pkR;
    }

    /// <summary>
    /// Adds the last block (<see cref="ProcessFilledBuffer"/>) into an extra return's input, pre- or post-fader, at
    /// <paramref name="level"/>. Only for the sends in <see cref="BlockSends"/> (the block is kept only while there are any).
    /// </summary>
    public void AddSend(int n, float level, bool pre, Span<float> dstL, Span<float> dstR)
    {
        var srcL = pre ? preL : postL;
        var srcR = pre ? preR : postR;
        for (int i = 0; i < n; i++)
        {
            dstL[i] += srcL[i] * level;
            dstR[i] += srcR[i] * level;
        }
    }
}

/// <summary>
/// One send of a track to an extra return, as the audio thread reads it: where (<see cref="To"/> live, or
/// <see cref="Index"/> into the export's returns), how much and whether it is taken before the fader. Immutable: the
/// UI builds a new array of them on every edit (<see cref="TrackDspChain.Sends"/>).
/// </summary>
public sealed class TrackSend(ReturnDspChain? to, int index, float level, bool pre)
{
    public readonly ReturnDspChain? To = to;
    public readonly int Index = index;
    public readonly float Level = level;
    public readonly bool Pre = pre;
}

/// <summary>
/// An extra return (native only), live and in the export: the tracks' sends add up in its buffer (8192 frames,
/// preallocated), go through its insert slots (<see cref="InsertRack"/>, where a reverb or a delay is just one more
/// slot) and its fader, and into the master next to the fixed reverb and delay. Live, the engine hands the audio thread
/// the whole list of returns as one array; each block it clears every buffer (<see cref="Begin"/>) before the tracks
/// add into them, so a send never lands in a return that won't be processed.
/// </summary>
public sealed class ReturnDspChain
{
    readonly float[] bufL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] bufR = new float[TrackDspChain.MaxBlockSize];
    readonly InsertRack rack;

    public ReturnDspChain(int sampleRate) => rack = new InsertRack(sampleRate);

    /// <summary>Its fader (0..1.5) and mute, set by the UI, read once per block.</summary>
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }

    /// <summary>The slots the UI last set.</summary>
    public IReadOnlyList<ITrackInsert> Inserts => rack.Inserts;

    /// <summary>Peaks of its output and its slots' reduction (dB): written by the audio thread, read and reset by the UI.</summary>
    public float MeterL, MeterR;
    public double GrDb;
    /// <summary>Its output's RMS, for its strip's meter.</summary>
    public RmsTap Rms { get; } = new();
    /// <summary>UI side: the shown peak, with fall-off.</summary>
    public float Show;

    /// <inheritdoc cref="InsertRack.Set"/>
    public void SetInserts(IReadOnlyList<InsertSpec> specs, bool fade = true) => rack.Set(specs, fade);

    public void Configure(MixReturnSpec spec)
    {
        Volume = spec.Volume;
        Muted = spec.Muted;
        SetInserts(spec.Inserts, fade: false);
    }

    public void Reset() => rack.Reset();

    /// <summary>Audio thread, before the tracks: clears its input.</summary>
    public void Begin(int n)
    {
        bufL.AsSpan(0, n).Clear();
        bufR.AsSpan(0, n).Clear();
    }

    public Span<float> BufferL(int n) => bufL.AsSpan(0, n);
    public Span<float> BufferR(int n) => bufR.AsSpan(0, n);

    /// <summary>
    /// Runs the slots and the fader over <paramref name="bl"/> / <paramref name="br"/> in place (its input becomes its
    /// output) and meters it. The export calls it on its own buffers; live, <see cref="End"/> does it on this one.
    /// </summary>
    public void Process(Span<float> bl, Span<float> br)
    {
        double gr = rack.Process(bl, br);
        if (gr < GrDb) GrDb = gr;
        float g = Muted ? 0f : (float)Volume;
        float pkL = MeterL, pkR = MeterR, sqL = 0, sqR = 0;
        for (int i = 0; i < bl.Length; i++)
        {
            float l = bl[i] * g, r = br[i] * g;
            bl[i] = l;
            br[i] = r;
            if (Math.Abs(l) > pkL) pkL = Math.Abs(l);
            if (Math.Abs(r) > pkR) pkR = Math.Abs(r);
            sqL += l * l;
            sqR += r * r;
        }
        Rms.Add(sqL, sqR, bl.Length);
        MeterL = pkL;
        MeterR = pkR;
    }

    /// <summary>Audio thread, after the tracks: processes its buffer and adds it into the master.</summary>
    public void End(int n, Span<float> masterL, Span<float> masterR)
    {
        var bl = bufL.AsSpan(0, n);
        var br = bufR.AsSpan(0, n);
        Process(bl, br);
        for (int i = 0; i < n; i++)
        {
            masterL[i] += bl[i];
            masterR[i] += br[i];
        }
    }
}

/// <summary>
/// A list of insert slots as the audio thread runs them, for a track (<see cref="TrackDspChain"/>) or a folder's bus
/// (<see cref="BusDspChain"/>). The UI hands it a whole new array (<see cref="Set"/>, built by
/// <see cref="TrackInserts.Build"/>, which reuses the inserts by slot id so they keep their state); when the list itself
/// changed, the audio thread fades the signal out for <see cref="TrackDspChain.SwapFade"/> samples, swaps, and fades it
/// back in, so a slot added, removed or moved never clicks. <see cref="Process"/> never allocates.
/// </summary>
public sealed class InsertRack(int sampleRate)
{
    // what the audio thread runs, and what the UI last asked for (with its slot ids, for Build)
    ITrackInsert[] inserts = [];
    volatile ITrackInsert[] wanted = [];
    string[] wantedIds = [];
    int fadeOut, fadeIn;

    /// <summary>The inserts the UI last set, in order (the audio thread may still be fading over to them).</summary>
    public IReadOnlyList<ITrackInsert> Inserts => wanted;

    /// <summary>How late the slots' output comes out, all added up. Zero while every slot is a built-in effect.</summary>
    public int LatencySamples
    {
        get
        {
            int n = 0;
            foreach (var i in wanted) n += i.LatencySamples;
            return n;
        }
    }

    /// <summary>
    /// Audio thread: false only with no slots, none asked for and no swap fade going on, when <see cref="Process"/>
    /// would leave the sound exactly as it is.
    /// </summary>
    public bool Busy => inserts.Length > 0 || wanted.Length > 0 || fadeOut > 0 || fadeIn > 0;

    /// <summary>
    /// Takes the slots' settings (UI thread): inserts with the same id keep their state; a new list is handed to the audio
    /// thread whole. <paramref name="fade"/> = false swaps at once, only for a chain the audio thread isn't running yet.
    /// </summary>
    public void Set(IReadOnlyList<InsertSpec> specs, bool fade = true)
    {
        var next = TrackInserts.Build(specs, wanted, wantedIds, sampleRate, out var ids);
        wantedIds = ids;
        if (next == wanted) return;
        wanted = next;
        if (!fade) inserts = next;
    }

    public void Reset()
    {
        inserts = wanted;
        fadeOut = fadeIn = 0;
        foreach (var i in inserts) i.Reset();
    }

    /// <summary>Runs the slots over the block in place (and the swap fade, if the list changed). Returns their gain reduction (dB).</summary>
    public double Process(Span<float> bl, Span<float> br)
    {
        var cur = inserts;
        double reduction = 0;
        for (int k = 0; k < cur.Length; k++)
        {
            cur[k].Process(bl, br);
            reduction += cur[k].TakeGrDb();
        }
        if (fadeOut == 0 && fadeIn == 0 && wanted != cur) fadeOut = TrackDspChain.SwapFade;
        if (fadeOut > 0) FadeOutAndSwap(bl, br);
        else if (fadeIn > 0) FadeIn(bl, br);
        return reduction;
    }

    /// <summary>The old slots fade to silence; once there, the new ones take over and fade in from the next block.</summary>
    void FadeOutAndSwap(Span<float> bl, Span<float> br)
    {
        for (int i = 0; i < bl.Length; i++)
        {
            float g = fadeOut / (float)TrackDspChain.SwapFade;
            if (fadeOut > 0) fadeOut--;
            bl[i] *= g;
            br[i] *= g;
        }
        if (fadeOut > 0) return;
        inserts = wanted;
        fadeIn = TrackDspChain.SwapFade;
    }

    void FadeIn(Span<float> bl, Span<float> br)
    {
        for (int i = 0; i < bl.Length && fadeIn > 0; i++)
        {
            float g = 1 - fadeIn-- / (float)TrackDspChain.SwapFade;
            bl[i] *= g;
            br[i] *= g;
        }
    }
}

/// <summary>
/// A folder's bus in live playback (native only): the post-fader outputs of the folder's tracks add up in its buffer
/// (8192 frames, preallocated), go through the folder's insert slots (<see cref="InsertRack"/>) and into the master.
/// The tracks' sends don't pass through it, and the folder's fader, mute and solo stay a VCA on the tracks. While it
/// has no slots (and no swap fade going on) it isn't used at all: its tracks go straight to the master, exactly as
/// without folders. The audio thread decides that once per block (<see cref="Begin"/>), so a track never lands in a
/// bus that won't be processed. <see cref="MixRenderer"/> does the same per stretch for the export.
/// </summary>
public sealed class BusDspChain
{
    readonly float[] bufL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] bufR = new float[TrackDspChain.MaxBlockSize];
    readonly InsertRack rack;

    public BusDspChain(int sampleRate) => rack = new InsertRack(sampleRate);

    /// <summary>The slots the UI last set.</summary>
    public IReadOnlyList<ITrackInsert> Inserts => rack.Inserts;

    /// <summary>Audio thread: whether the tracks go through it this block (set by <see cref="Begin"/>, cleared by <see cref="End"/>).</summary>
    public bool Routed { get; private set; }

    /// <summary>Peaks of its output and its slots' reduction (dB): written by the audio thread, read and reset by the UI.</summary>
    public float MeterL, MeterR;
    public double GrDb;
    /// <summary>Its output's RMS, for the folder strip's meter.</summary>
    public RmsTap Rms { get; } = new();
    /// <summary>UI side: the shown peak (with fall-off) and reduction (instant attack, slow release), like a track's.</summary>
    public float Show;
    public double GrShow;

    /// <inheritdoc cref="InsertRack.Set"/>
    public void SetInserts(IReadOnlyList<InsertSpec> specs, bool fade = true) => rack.Set(specs, fade);

    public void Reset() => rack.Reset();

    /// <summary>Audio thread, before the tracks: decides whether it is used this block and, if so, clears its buffer.</summary>
    public void Begin(int n)
    {
        Routed = rack.Busy;
        if (!Routed) return;
        bufL.AsSpan(0, n).Clear();
        bufR.AsSpan(0, n).Clear();
    }

    public Span<float> BufferL(int n) => bufL.AsSpan(0, n);
    public Span<float> BufferR(int n) => bufR.AsSpan(0, n);

    /// <summary>Audio thread, after the tracks: runs the slots over the sum and adds it into the master.</summary>
    public void End(int n, Span<float> masterL, Span<float> masterR)
    {
        if (!Routed) return;
        Routed = false;
        var bl = bufL.AsSpan(0, n);
        var br = bufR.AsSpan(0, n);
        double gr = rack.Process(bl, br);
        if (gr < GrDb) GrDb = gr;
        float pkL = MeterL, pkR = MeterR, sqL = 0, sqR = 0;
        for (int i = 0; i < n; i++)
        {
            float l = bl[i], r = br[i];
            masterL[i] += l;
            masterR[i] += r;
            if (Math.Abs(l) > pkL) pkL = Math.Abs(l);
            if (Math.Abs(r) > pkR) pkR = Math.Abs(r);
            sqL += l * l;
            sqR += r * r;
        }
        Rms.Add(sqL, sqR, n);
        MeterL = pkL;
        MeterR = pkR;
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
    /// <summary>The returns' RMS, for their mixer meters.</summary>
    public RmsTap ReverbRms { get; } = new();
    public RmsTap DelayRms { get; } = new();

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
        float rSqL = 0, rSqR = 0, dSqL = 0, dSqR = 0;
        for (int i = 0; i < masterL.Length; i++)
        {
            float rvL = rl[i], rvR = rr[i];
            float dyL = dl[i], dyR = dr[i];
            if (Math.Abs(rvL) > rPkL) rPkL = Math.Abs(rvL);
            if (Math.Abs(rvR) > rPkR) rPkR = Math.Abs(rvR);
            if (Math.Abs(dyL) > dPkL) dPkL = Math.Abs(dyL);
            if (Math.Abs(dyR) > dPkR) dPkR = Math.Abs(dyR);
            rSqL += rvL * rvL; rSqR += rvR * rvR;
            dSqL += dyL * dyL; dSqR += dyR * dyR;
            masterL[i] += rvL + dyL;
            masterR[i] += rvR + dyR;
        }
        ReverbMeterL = rPkL;
        ReverbMeterR = rPkR;
        DelayMeterL = dPkL;
        DelayMeterR = dPkR;
        ReverbRms.Add(rSqL, rSqR, masterL.Length);
        DelayRms.Add(dSqL, dSqR, masterL.Length);
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
    /// or the sum of all of them. <see cref="XL"/> / <see cref="XR"/>: one pair per extra return.</summary>
    sealed class ChunkBuffers(int returns)
    {
        public readonly float[] L = new float[ChunkFrames], R = new float[ChunkFrames];
        public readonly float[] RevL = new float[ChunkFrames], RevR = new float[ChunkFrames];
        public readonly float[] DlyL = new float[ChunkFrames], DlyR = new float[ChunkFrames];
        public readonly float[][] XL = Enumerable.Range(0, returns).Select(_ => new float[ChunkFrames]).ToArray();
        public readonly float[][] XR = Enumerable.Range(0, returns).Select(_ => new float[ChunkFrames]).ToArray();

        public void Clear(int n)
        {
            Array.Clear(L, 0, n);
            Array.Clear(R, 0, n);
            Array.Clear(RevL, 0, n);
            Array.Clear(RevR, 0, n);
            Array.Clear(DlyL, 0, n);
            Array.Clear(DlyR, 0, n);
            for (int j = 0; j < XL.Length; j++)
            {
                Array.Clear(XL[j], 0, n);
                Array.Clear(XR[j], 0, n);
            }
        }

        void AddSends(ChunkBuffers b, int n)
        {
            for (int j = 0; j < XL.Length; j++)
            {
                float[] dl = XL[j], dr = XR[j], sl = b.XL[j], sr = b.XR[j];
                for (int i = 0; i < n; i++)
                {
                    dl[i] += sl[i];
                    dr[i] += sr[i];
                }
            }
        }

        /// <summary>A folder's track: its sends into these, its output into its folder's bus (<paramref name="busL"/>, <paramref name="busR"/>).</summary>
        public void AddToBus(ChunkBuffers b, float[] busL, float[] busR, int n)
        {
            for (int i = 0; i < n; i++)
            {
                busL[i] += b.L[i];
                busR[i] += b.R[i];
                RevL[i] += b.RevL[i];
                RevR[i] += b.RevR[i];
                DlyL[i] += b.DlyL[i];
                DlyR[i] += b.DlyR[i];
            }
            AddSends(b, n);
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
            AddSends(b, n);
        }
    }

    /// <summary>One stretch in flight: each track's buffers, each folder bus's sum, and the sum of all (the master and the sends).</summary>
    sealed class ChunkSet(int tracks, int buses, int returns)
    {
        public readonly ChunkBuffers[] Tracks = Enumerable.Range(0, tracks).Select(_ => new ChunkBuffers(returns)).ToArray();
        public readonly float[][] BusL = Enumerable.Range(0, buses).Select(_ => new float[ChunkFrames]).ToArray();
        public readonly float[][] BusR = Enumerable.Range(0, buses).Select(_ => new float[ChunkFrames]).ToArray();
        public readonly ChunkBuffers Sum = new(returns);
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
    /// its sends through the reverb and delay returns and every extra return, without the master limiter, so the stems add up to the mix
    /// before the limiter and none is squashed by it. A stem doesn't go through its folder's slots either (they work on
    /// the sum of the folder's tracks, which a single track isn't). <paramref name="limit"/> = false leaves the limiter
    /// out of the full mix too: <see cref="MixMaster"/> runs it afterwards with the loudness gain in front.
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
        // the extra returns, each with its slots and its fader (the fixed reverb and delay stay in MixGraphBus)
        var returns = new ReturnDspChain[busSpec.Returns.Count];
        for (int j = 0; j < returns.Length; j++)
        {
            returns[j] = new ReturnDspChain(sampleRate);
            returns[j].Configure(busSpec.Returns[j]);
        }
        limit &= only < 0;
        master &= only < 0;
        int latency = limit ? bus.Limiter.Latency : 0;
        int frames = Math.Max(1, (int)Math.Ceiling(durationSec * sampleRate));
        int totalFrames = frames + latency;

        bool anySolo = false;
        foreach (var t in tracks)
            if (t.Solo) { anySolo = true; break; }

        // a folder with slots is a bus: its tracks' post-fader outputs add up there and go through its slots (the
        // sends don't). One without slots is no bus at all, so its tracks add into the master exactly as before.
        var folders = busSpec.Folders;
        var buses = new List<InsertRack>();
        var busOfFolder = new Dictionary<int, int>();
        var active = new List<(MixClipSpec[] Clips, ClipAudioJoins?[] Joins, TrackDspChain Chain, int Bus)>();
        for (int k = 0; k < tracks.Count; k++)
        {
            var t = tracks[k];
            bool mute = t.Muted || (anySolo && !t.Solo);
            if (only >= 0 ? k != only : mute || t.Clips.Count == 0) continue;
            var chain = new TrackDspChain(sampleRate);
            chain.Configure(t);
            if (only >= 0) chain.Muted = chain.Solo = false;
            chain.Sends = [.. t.Sends.Where(s => s.On && s.Level > 0 && s.Return >= 0 && s.Return < returns.Length)
                .Select(s => new TrackSend(null, s.Return, (float)Math.Clamp(s.Level, 0, 1), s.Pre))];
            var clips = t.Clips.ToArray();
            int into = -1;
            if (only < 0 && t.Folder >= 0 && t.Folder < folders.Count && folders[t.Folder].Inserts.Count > 0
                && !busOfFolder.TryGetValue(t.Folder, out into))
            {
                var rack = new InsertRack(sampleRate);
                rack.Set(folders[t.Folder].Inserts, fade: false);
                into = busOfFolder[t.Folder] = buses.Count;
                buses.Add(rack);
            }
            active.Add((clips, TrackDspChain.AudioJoins(clips, sampleRate), chain, into));
        }
        if (only >= 0) anySolo = false;

        int chunks = (totalFrames + ChunkFrames - 1) / ChunkFrames;
        var sets = new ChunkSet[Math.Min(Sets, chunks)];
        for (int k = 0; k < sets.Length; k++) sets[k] = new ChunkSet(active.Count, buses.Count, returns.Length);
        int reportStep = Math.Max(sampleRate / 2, totalFrames / 40);
        int nextReport = reportStep;

        // three stages, each on its own thread and each over the stretches in order (the effects keep their state from
        // one to the next): the tracks (in parallel among them) -> the returns (fixed and extra, in parallel) -> the
        // master and the sink.
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
                var (clips, joins, chain, _) = active[k];
                var b = set.Tracks[k];
                b.Clear(len);
                for (int off = 0; off < len; off += Quantum)
                {
                    int n = Math.Min(Quantum, len - off);
                    TrackDspChain.SampleClips(clips, at + off, n, sampleRate, chain.BufferL(n), chain.BufferR(n), joins);
                    chain.ProcessFilledBuffer(n, anySolo, b.L.AsSpan(off, n), b.R.AsSpan(off, n), b.RevL.AsSpan(off, n),
                        b.RevR.AsSpan(off, n), b.DlyL.AsSpan(off, n), b.DlyR.AsSpan(off, n), out _, out _, out _);
                    foreach (var s in chain.BlockSends)
                        chain.AddSend(n, s.Level, s.Pre, b.XL[s.Index].AsSpan(off, n), b.XR[s.Index].AsSpan(off, n));
                }
            }
            if (active.Count > 1) Parallel.For(0, active.Count, new ParallelOptions { CancellationToken = token }, Track);
            else if (active.Count == 1) Track(0);
            // summed in the tracks' order, so every sample is the same float the one-block-at-a-time loop gave
            set.Sum.Clear(len);
            if (buses.Count == 0)
            {
                foreach (var b in set.Tracks) set.Sum.Add(b, len);
                return;
            }
            for (int j = 0; j < buses.Count; j++)
            {
                Array.Clear(set.BusL[j], 0, len);
                Array.Clear(set.BusR[j], 0, len);
            }
            for (int k = 0; k < active.Count; k++)
            {
                int into = active[k].Bus;
                if (into < 0) set.Sum.Add(set.Tracks[k], len);
                else set.Sum.AddToBus(set.Tracks[k], set.BusL[into], set.BusR[into], len);
            }
            // each folder's slots over its sum, in blocks like the tracks (they keep their state from one stretch to the next)
            void Bus(int j)
            {
                for (int off = 0; off < len; off += Quantum)
                {
                    int n = Math.Min(Quantum, len - off);
                    buses[j].Process(set.BusL[j].AsSpan(off, n), set.BusR[j].AsSpan(off, n));
                }
            }
            if (buses.Count > 1) Parallel.For(0, buses.Count, new ParallelOptions { CancellationToken = token }, Bus);
            else Bus(0);
            for (int j = 0; j < buses.Count; j++)
            {
                float[] l = set.BusL[j], r = set.BusR[j];
                for (int i = 0; i < len; i++)
                {
                    set.Sum.L[i] += l[i];
                    set.Sum.R[i] += r[i];
                }
            }
        }

        void RunReturns(int c)
        {
            var sum = sets[c % sets.Length].Sum;
            int len = Math.Min(ChunkFrames, totalFrames - c * ChunkFrames);
            // the fixed reverb (0), the fixed delay (1) and each extra return, in parallel: each keeps its own state
            void Return(int j)
            {
                for (int off = 0; off < len; off += Quantum)
                {
                    int n = Math.Min(Quantum, len - off);
                    if (j == 0) bus.Reverb.Process(sum.RevL.AsSpan(off, n), sum.RevR.AsSpan(off, n));
                    else if (j == 1) bus.Delay.Process(sum.DlyL.AsSpan(off, n), sum.DlyR.AsSpan(off, n));
                    else returns[j - 2].Process(sum.XL[j - 2].AsSpan(off, n), sum.XR[j - 2].AsSpan(off, n));
                }
            }
            Parallel.For(0, 2 + returns.Length, new ParallelOptions { CancellationToken = token }, Return);
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
                // the extra returns first, then the fixed ones, as the live engine adds them
                for (int j = 0; j < returns.Length; j++)
                {
                    var xl = sum.XL[j].AsSpan(off, n);
                    var xr = sum.XR[j].AsSpan(off, n);
                    for (int i = 0; i < n; i++)
                    {
                        ml[i] += xl[i];
                        mr[i] += xr[i];
                    }
                }
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
