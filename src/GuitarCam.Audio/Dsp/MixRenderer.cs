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
    /// <summary>A MIDI track's notes (<see cref="NoteSynth"/>), added to its clips' sound before the chain; null = none.</summary>
    public NoteSequence? Notes { get; init; }
    /// <summary>The instrument plugin that plays <see cref="Notes"/> instead of the test tone; null = the test tone.</summary>
    public Vst.VstInstance? Instrument { get; init; }
    /// <summary>The project's tempo, for the instrument's transport.</summary>
    public double Tempo { get; init; } = 120;
    /// <summary>True when there is something to play: clips or notes.</summary>
    public bool HasSound => Clips.Count > 0 || Notes is { Notes.Length: > 0 };
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
    /// <summary>Where its output goes: an index into <see cref="MixBusSpec.Nodes"/> (its folder or a bus), -1 = the master.</summary>
    public int Output { get; init; } = -1;
    /// <summary>Its sends to buses (<see cref="MixBusSpec.Nodes"/>).</summary>
    public IReadOnlyList<MixSendSpec> Sends { get; init; } = [];
}

/// <summary>A send to a bus: <see cref="To"/> is an index into <see cref="MixBusSpec.Nodes"/>.</summary>
public sealed class MixSendSpec
{
    public int To { get; init; }
    public double Level { get; init; }
    public bool On { get; init; } = true;
    /// <summary>Taken before the fader (after the slots and the pan, still silenced by mute and solo).</summary>
    public bool Pre { get; init; }
}

/// <summary>
/// A node of the mix (native only): a folder (a plain sum: no slots, pan or fader) or a bus. What is routed and sent to
/// it adds up, goes through its slots, pan, fader and mute (<see cref="BusDspChain"/>), and on to its
/// <see cref="Output"/> and its sends. Its <see cref="Output"/> and sends always point at nodes after it in
/// <see cref="MixBusSpec.Nodes"/> (the run order), or at the master.
/// </summary>
public sealed class MixNodeSpec
{
    public string Name { get; init; } = "";
    public double Volume { get; init; } = 1;
    public double Pan { get; init; }
    public bool Muted { get; init; }
    public IReadOnlyList<InsertSpec> Inserts { get; init; } = [];
    /// <summary>An index into <see cref="MixBusSpec.Nodes"/> after this one, -1 = the master.</summary>
    public int Output { get; init; } = -1;
    public IReadOnlyList<MixSendSpec> Sends { get; init; } = [];
}

public sealed class MixBusSpec
{
    public double Bpm { get; init; } = 120;
    public MasterParams Master { get; init; } = MasterParams.Off;
    /// <summary>The folders and the buses, in the order they run: each after every node that feeds it.</summary>
    public IReadOnlyList<MixNodeSpec> Nodes { get; init; } = [];
}

/// <summary>Receives a rendered stretch of the mix that starts at <paramref name="frame"/> (<see cref="MixRenderer.RenderTo"/>).</summary>
public delegate void MixSink(int frame, ReadOnlySpan<float> left, ReadOnlySpan<float> right);

/// <summary>
/// Per-track DSP chain shared by live ASIO playback (<c>PlaybackEngine</c>) and offline export render
/// (<see cref="MixRenderer"/>): clips with fade envelopes → the track's insert slots (<see cref="ITrackInsert"/>, by
/// default Vida → compressor with its preset EQ → 6-band EQ) → Web Audio <c>StereoPannerNode</c> → fader, into its
/// output (a folder, a bus or the master: the caller's buffer), and its sends to buses, each post-fader (by default) or
/// pre-fader (after the slots and the pan, before the fader; mute and solo still silence it). Preallocates its buffers
/// (8192 frames) so <see cref="ProcessFilledBuffer"/> never allocates. The slots are an array the UI replaces whole
/// (<see cref="SetInserts"/>); when the list itself changes (a slot added, removed or moved) the audio thread dips the
/// track for <see cref="SwapFade"/> samples on each side of the swap, so the new chain comes in without a click. The sends
/// (<see cref="Sends"/>) are an immutable array swapped whole too: while there are any, the block's pre- and post-fader
/// signals are kept for <see cref="AddSend"/>.
/// </summary>
public sealed class TrackDspChain
{
    public const int MaxBlockSize = 8192;
    /// <summary>5 ms at 48 kHz: the fade out of the old slots and the fade in of the new ones when the list changes.</summary>
    public const int SwapFade = 240;

    readonly float[] bufL = new float[MaxBlockSize];
    readonly float[] bufR = new float[MaxBlockSize];
    // the last block's pre-fader and post-fader signals, kept only while there are sends
    readonly float[] preL = new float[MaxBlockSize];
    readonly float[] preR = new float[MaxBlockSize];
    readonly float[] postL = new float[MaxBlockSize];
    readonly float[] postR = new float[MaxBlockSize];
    readonly InsertRack rack;
    volatile TrackSend[] sends = [];

    /// <summary>Its sends to buses, swapped whole by the UI (never changed in place).</summary>
    public TrackSend[] Sends { get => sends; set => sends = value; }
    /// <summary>
    /// Audio thread: the sends the last <see cref="ProcessFilledBuffer"/> kept its signals for. The caller adds these
    /// (<see cref="AddSend"/>), not <see cref="Sends"/>, which the UI may have swapped in the meantime.
    /// </summary>
    public TrackSend[] BlockSends { get; private set; } = [];

    public double Volume { get; set; } = 1;
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }
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
    /// Runs the track's full insert chain on its buffer (already filled with the track's source clips for the block)
    /// and accumulates the post-fader output into <paramref name="outL"/> / <paramref name="outR"/> (its folder's, its
    /// bus's or the master's). Its sends are the caller's: <see cref="BlockSends"/> with <see cref="AddSend"/>.
    /// </summary>
    public void ProcessFilledBuffer(
        int n,
        bool anySolo,
        Span<float> outL,
        Span<float> outR,
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
        // pre-fader: after the slots and the pan, before the fader, still silenced by mute / solo
        float preGain = mute ? 0f : 1f;
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
            outL[i] += ol;
            outR[i] += or;
            if (keep)
            {
                preL[i] = xl * preGain; preR[i] = xr * preGain;
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
    /// Adds the last block (<see cref="ProcessFilledBuffer"/>) into a bus's input, pre- or post-fader, at
    /// <paramref name="level"/>. Only for the sends in <see cref="BlockSends"/> (the block is kept only while there are any).
    /// </summary>
    public void AddSend(int n, float level, bool pre, Span<float> dstL, Span<float> dstR) =>
        BusDspChain.Add(pre ? preL.AsSpan(0, n) : postL.AsSpan(0, n), pre ? preR.AsSpan(0, n) : postR.AsSpan(0, n), level, dstL, dstR);

    /// <summary>The last block's pre-fader (or post-fader) signal, for the export, which adds the sends itself.</summary>
    public ReadOnlySpan<float> KeptL(int n, bool pre) => (pre ? preL : postL).AsSpan(0, n);
    public ReadOnlySpan<float> KeptR(int n, bool pre) => (pre ? preR : postR).AsSpan(0, n);
}

/// <summary>
/// One send to a bus, as the audio thread reads it: where (<see cref="To"/> live, or <see cref="Index"/> into the
/// export's nodes), how much and whether it is taken before the fader. Immutable: the UI builds a new array of them on
/// every edit (<see cref="TrackDspChain.Sends"/>, <see cref="BusDspChain.Sends"/>).
/// </summary>
public sealed class TrackSend(BusDspChain? to, int index, float level, bool pre)
{
    public readonly BusDspChain? To = to;
    public readonly int Index = index;
    public readonly float Level = level;
    public readonly bool Pre = pre;
}

/// <summary>
/// A node of the mix (native only), live and in the export: a folder (a plain sum, no slots, pan or fader) or a bus. What
/// is routed or sent to it adds up in its buffer (8192 frames, preallocated), goes through its insert slots
/// (<see cref="InsertRack"/>, where a reverb or a delay is just one more slot), its pan (the tracks' law), its fader and
/// its mute, and on to its output (<see cref="Out"/>: another node, or the master when null) and its sends
/// (<see cref="Sends"/>; pre-fader ones are taken after the slots and the pan). The nodes run in an order where each comes
/// after everything that feeds it (<c>ProjectBuses.Order</c>): live, the engine hands the audio thread that order as one
/// array and clears every buffer each block (<see cref="Begin"/>) before the tracks add into them; the export does the
/// same per stretch with <see cref="Process"/> on its own buffers. Nothing here allocates.
/// </summary>
public sealed class BusDspChain
{
    readonly float[] bufL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] bufR = new float[TrackDspChain.MaxBlockSize];
    // the last block after the slots and the pan, before the fader: kept only while there are pre-fader sends
    readonly float[] preL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] preR = new float[TrackDspChain.MaxBlockSize];
    readonly InsertRack rack;
    volatile BusDspChain? output;
    volatile TrackSend[] sends = [];

    public BusDspChain(int sampleRate) => rack = new InsertRack(sampleRate);

    /// <summary>Its fader (0..1.5), pan (-1..1) and mute, set by the UI, read once per block.</summary>
    public double Volume { get; set; } = 1;
    public double Pan { get; set; }
    public bool Muted { get; set; }
    /// <summary>Live: the node its output goes to (null = the master), set by the UI.</summary>
    public BusDspChain? Out { get => output; set => output = value; }
    /// <summary>Live: its sends, swapped whole by the UI (never changed in place).</summary>
    public TrackSend[] Sends { get => sends; set => sends = value; }

    /// <summary>The slots the UI last set.</summary>
    public IReadOnlyList<ITrackInsert> Inserts => rack.Inserts;

    /// <summary>Peaks of its output and its slots' reduction (dB): written by the audio thread, read and reset by the UI.</summary>
    public float MeterL, MeterR;
    public double GrDb;
    /// <summary>Its output's RMS, for its strip's meter.</summary>
    public RmsTap Rms { get; } = new();
    /// <summary>UI side: the shown peaks (with fall-off) and reduction (instant attack, slow release), like a track's.</summary>
    public float ShowL, ShowR;
    public double GrShow;

    /// <inheritdoc cref="InsertRack.Set"/>
    public void SetInserts(IReadOnlyList<InsertSpec> specs, bool fade = true) => rack.Set(specs, fade);

    /// <summary>The export's setup: fader, pan, mute and slots (its output and sends are indexes the export follows).</summary>
    public void Configure(MixNodeSpec spec)
    {
        Volume = spec.Volume;
        Pan = spec.Pan;
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
    /// Runs the slots, the pan, the fader and the mute over <paramref name="bl"/> / <paramref name="br"/> in place (its
    /// input becomes its output) and meters it. With <paramref name="keepPre"/> the signal before the fader is kept in
    /// <paramref name="preOutL"/> / <paramref name="preOutR"/> (silenced by the mute, like a track's) for pre-fader sends.
    /// </summary>
    public void Process(Span<float> bl, Span<float> br, bool keepPre, Span<float> preOutL, Span<float> preOutR)
    {
        double gr = rack.Process(bl, br);
        if (gr < GrDb) GrDb = gr;
        bool mute = Muted;
        float g = mute ? 0f : (float)Volume;
        float pg = mute ? 0f : 1f;
        double pan = Math.Clamp(Pan, -1, 1);
        // at the center the pan is no pan at all: a folder (and a centered bus) passes its sum untouched
        bool panned = pan != 0;
        double x = pan <= 0 ? pan + 1 : pan;
        float gl = (float)Math.Cos(x * Math.PI / 2.0);
        float gr2 = (float)Math.Sin(x * Math.PI / 2.0);
        float pkL = MeterL, pkR = MeterR, sqL = 0, sqR = 0;
        for (int i = 0; i < bl.Length; i++)
        {
            float l = bl[i], r = br[i];
            if (panned)
            {
                float xl = pan <= 0 ? l + r * gl : l * gl;
                float xr = pan <= 0 ? r * gr2 : r + l * gr2;
                l = xl;
                r = xr;
            }
            if (keepPre)
            {
                preOutL[i] = l * pg;
                preOutR[i] = r * pg;
            }
            l *= g;
            r *= g;
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

    /// <summary>Whether any of <paramref name="list"/> is taken before the fader.</summary>
    public static bool AnyPre(TrackSend[] list)
    {
        for (int k = 0; k < list.Length; k++) if (list[k].Pre) return true;
        return false;
    }

    /// <summary>Adds <paramref name="srcL"/> / <paramref name="srcR"/> times <paramref name="level"/> into a destination.</summary>
    public static void Add(ReadOnlySpan<float> srcL, ReadOnlySpan<float> srcR, float level, Span<float> dstL, Span<float> dstR)
    {
        for (int i = 0; i < srcL.Length; i++)
        {
            dstL[i] += srcL[i] * level;
            dstR[i] += srcR[i] * level;
        }
    }

    /// <summary>
    /// Audio thread, after the tracks and every node before it: processes its buffer and adds it into its output (the
    /// next node's buffer, or <paramref name="masterL"/> / <paramref name="masterR"/>) and into its sends' buses.
    /// </summary>
    public void End(int n, Span<float> masterL, Span<float> masterR)
    {
        var bl = bufL.AsSpan(0, n);
        var br = bufR.AsSpan(0, n);
        var list = sends;
        bool pre = AnyPre(list);
        Process(bl, br, pre, preL.AsSpan(0, n), preR.AsSpan(0, n));
        var to = output;
        if (to != null) Add(bl, br, 1f, to.BufferL(n), to.BufferR(n));
        else Add(bl, br, 1f, masterL, masterR);
        for (int k = 0; k < list.Length; k++)
        {
            var s = list[k];
            if (s.To == null) continue;
            if (s.Pre) Add(preL.AsSpan(0, n), preR.AsSpan(0, n), s.Level, s.To.BufferL(n), s.To.BufferR(n));
            else Add(bl, br, s.Level, s.To.BufferL(n), s.To.BufferR(n));
        }
    }
}

/// <summary>
/// A list of insert slots as the audio thread runs them, for a track (<see cref="TrackDspChain"/>) or a bus
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
/// The master, shared by live playback and offline export render: <see cref="MasterChain"/> + Empuje (or the Destino's
/// gain, <see cref="MasterLoudness"/>) + <see cref="SoftClipper"/> + <see cref="MasterLimiter"/>. The buses are
/// <see cref="BusDspChain"/> nodes that add into the master before it gets here. Nothing here allocates.
/// </summary>
public sealed class MixGraphBus
{
    public MasterLimiter Limiter { get; }
    /// <summary>The master's EQ and multiband, before the limiter.</summary>
    public MasterChain Master { get; }
    /// <summary>The master's soft clip, between Empuje and the limiter.</summary>
    public SoftClipper Clip { get; }
    /// <summary>The gain into the limiter (Empuje or the Destino's), linear: the target and where the glide toward it is.</summary>
    volatile float driveTarget = 1;
    float drive = 1;
    /// <summary>How long the gain takes to move (s): a new Destino measurement or the Empuje's knob glide in, never jump.</summary>
    const double DriveGlideSec = 0.05;
    readonly float driveGlide;

    public MixGraphBus(int sampleRate)
    {
        Master = new MasterChain(sampleRate);
        Clip = new SoftClipper(sampleRate);
        driveGlide = (float)(1 - Math.Exp(-1 / (DriveGlideSec * sampleRate)));
        int lookahead = (int)Math.Round(MasterLimiter.LookaheadSec * sampleRate);
        Limiter = new MasterLimiter(sampleRate, lookahead, MasterLimiter.CeilingDb);
    }

    public void Configure(MixBusSpec spec)
    {
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

    public void Reset()
    {
        Master.Reset();
        Clip.Reset();
        Limiter.Reset();
    }

    /// <summary>
    /// The master's chain over the mix in place: <see cref="MasterChain"/> (unless <paramref name="master"/> is false),
    /// then, with <paramref name="limit"/>, Empuje, clip and limiter.
    /// </summary>
    public void Process(Span<float> masterL, Span<float> masterR, bool limit = true, bool master = true)
    {
        if (master) Master.Process(masterL, masterR);
        if (!limit) return;
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

/// <summary>
/// Port of <c>renderMix</c> in <c>GuitarCam/src/audio/mixGraph.ts</c>: renders the whole mix offline
/// through <see cref="TrackDspChain"/>, the nodes (<see cref="BusDspChain"/>) and <see cref="MixGraphBus"/> (the exact
/// same DSP graph as live playback) and strips the master limiter's look-ahead latency so the rendered audio aligns
/// with sample 0.
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
    /// <summary>What an instrument plugin may still sound after its last note (its release, its own reverb).</summary>
    public const double InstrumentTailSec = 2;
    /// <summary>How much each stage of <see cref="RenderTo"/> renders at a time: long enough that handing it over costs
    /// nothing, short enough that the stages start working together right away (64 blocks, 0.17 s at 48 kHz).</summary>
    const int ChunkFrames = Quantum * 64;
    /// <summary>Stretches in flight: one per stage, so none waits for buffers.</summary>
    const int Sets = 3;

    /// <summary>A track's stretch: its post-fader output and, while it has pre-fader sends, its signal before the fader.</summary>
    sealed class TrackBuffers(bool pre)
    {
        public readonly float[] L = new float[ChunkFrames], R = new float[ChunkFrames];
        public readonly float[] PreL = pre ? new float[ChunkFrames] : [], PreR = pre ? new float[ChunkFrames] : [];
    }

    /// <summary>One stretch in flight: each track's buffers, each node's input (its output once processed) and pre-fader signal, and the master.</summary>
    sealed class ChunkSet(IReadOnlyList<bool> trackPre, int nodes)
    {
        public readonly TrackBuffers[] Tracks = trackPre.Select(p => new TrackBuffers(p)).ToArray();
        public readonly float[][] NodeL = Enumerable.Range(0, nodes).Select(_ => new float[ChunkFrames]).ToArray();
        public readonly float[][] NodeR = Enumerable.Range(0, nodes).Select(_ => new float[ChunkFrames]).ToArray();
        public readonly float[][] PreL = Enumerable.Range(0, nodes).Select(_ => new float[ChunkFrames]).ToArray();
        public readonly float[][] PreR = Enumerable.Range(0, nodes).Select(_ => new float[ChunkFrames]).ToArray();
        public readonly float[] L = new float[ChunkFrames], R = new float[ChunkFrames];
    }

    /// <summary>Port of <c>mixDurationSec</c> in <c>mixGraph.ts</c>: end of the last clip or note's release (at least 1 s).</summary>
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
            // an instrument's own release and effects ring past the last note: two seconds of room
            if (t.Notes != null) maxEnd = Math.Max(maxEnd, t.Notes.End / (double)sampleRate + (t.Instrument != null ? InstrumentTailSec : 0));
        }
        return maxEnd;
    }

    /// <summary>The tracks that sound in the mix (mute and solo as the mixer has them): the ones that get a stem.</summary>
    public static IEnumerable<int> Audible(IReadOnlyList<MixTrackSpec> tracks)
    {
        bool anySolo = tracks.Any(t => t.Solo);
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].HasSound && !tracks[i].Muted && (!anySolo || tracks[i].Solo)) yield return i;
    }

    /// <summary>
    /// Renders the mix. With <paramref name="only"/> it renders that track alone (a stem, native only): its chain, its
    /// output and sends through the folders and buses they reach (the other tracks are silent), without the master
    /// chain and the limiter, so the stems add up to the mix before the limiter and none is squashed by it. A bus that
    /// compresses works on that track alone there, not on the sum. <paramref name="limit"/> = false leaves the limiter out
    /// of the full mix too: <see cref="MixMaster"/> runs it afterwards with the loudness gain in front.
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

    /// <summary>A destination of a track or a node: a later node (<paramref name="to"/> &gt; <paramref name="from"/>) or the master (-1).</summary>
    static int Valid(int to, int from, int nodes) => to > from && to < nodes ? to : -1;

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
        bus.Configure(busSpec);
        // the folders and buses, in their run order; each one's output and sends only go to later ones (or the master)
        var nodeSpecs = busSpec.Nodes;
        int nodeCount = nodeSpecs.Count;
        var nodes = new BusDspChain[nodeCount];
        var nodeOut = new int[nodeCount];
        var nodeSends = new MixSendSpec[nodeCount][];
        var nodePre = new bool[nodeCount];
        // a node's level: one more than the deepest node that feeds it; the nodes of one level run in parallel
        var level = new int[nodeCount];
        for (int j = 0; j < nodeCount; j++)
        {
            nodes[j] = new BusDspChain(sampleRate);
            nodes[j].Configure(nodeSpecs[j]);
            nodeOut[j] = Valid(nodeSpecs[j].Output, j, nodeCount);
            nodeSends[j] = [.. nodeSpecs[j].Sends.Where(s => s.On && s.Level > 0 && Valid(s.To, j, nodeCount) >= 0)];
            nodePre[j] = nodeSends[j].Any(s => s.Pre);
        }
        for (int j = 0; j < nodeCount; j++)
        {
            if (nodeOut[j] >= 0) level[nodeOut[j]] = Math.Max(level[nodeOut[j]], level[j] + 1);
            foreach (var s in nodeSends[j]) level[s.To] = Math.Max(level[s.To], level[j] + 1);
        }
        var levels = Enumerable.Range(0, nodeCount).GroupBy(j => level[j]).OrderBy(g => g.Key).Select(g => g.ToArray()).ToArray();

        limit &= only < 0;
        master &= only < 0;
        int latency = limit ? bus.Limiter.Latency : 0;
        int frames = Math.Max(1, (int)Math.Ceiling(durationSec * sampleRate));
        int totalFrames = frames + latency;

        bool anySolo = false;
        foreach (var t in tracks)
            if (t.Solo) { anySolo = true; break; }

        var active = new List<(MixClipSpec[] Clips, ClipAudioJoins?[] Joins, NoteSequence? Notes, TrackDspChain Chain, int Out, MixSendSpec[] Sends, bool Pre)>();
        // each instrument track's plugin, in its own process, and the stretch it rendered (null = the test tone)
        var instruments = new List<(Vst.InstrumentRender Render, float[] L, float[] R)?>();
        for (int k = 0; k < tracks.Count; k++)
        {
            var t = tracks[k];
            bool mute = t.Muted || (anySolo && !t.Solo);
            if (only >= 0 ? k != only : mute || !t.HasSound) continue;
            var chain = new TrackDspChain(sampleRate);
            chain.Configure(t);
            if (only >= 0) chain.Muted = chain.Solo = false;
            MixSendSpec[] sends = [.. t.Sends.Where(s => s.On && s.Level > 0 && s.To >= 0 && s.To < nodeCount)];
            chain.Sends = [.. sends.Select(s => new TrackSend(null, s.To, (float)Math.Clamp(s.Level, 0, 1), s.Pre))];
            var clips = t.Clips.ToArray();
            int to = t.Output >= 0 && t.Output < nodeCount ? t.Output : -1;
            active.Add((clips, TrackDspChain.AudioJoins(clips, sampleRate), t.Notes, chain, to, sends, sends.Any(s => s.Pre)));
            instruments.Add(null);
            if (t.Instrument != null && t.Notes != null)
            {
                try { instruments[^1] = (new Vst.InstrumentRender(t.Instrument, t.Notes, t.Tempo, sampleRate), new float[ChunkFrames], new float[ChunkFrames]); }
                catch (Exception e) when (e is InvalidOperationException or IOException)
                {
                    foreach (var made in instruments) made?.Render.Dispose();
                    throw new InvalidOperationException(Texts.T("err.vst.export", "No pude abrir {0} para exportar: {1}", t.Instrument.Name, e.Message), e);
                }
            }
        }
        if (only >= 0) anySolo = false;

        int chunks = (totalFrames + ChunkFrames - 1) / ChunkFrames;
        var sets = new ChunkSet[Math.Min(Sets, chunks)];
        var trackPre = active.Select(a => a.Pre).ToArray();
        for (int k = 0; k < sets.Length; k++) sets[k] = new ChunkSet(trackPre, nodeCount);
        int reportStep = Math.Max(sampleRate / 2, totalFrames / 40);
        int nextReport = reportStep;

        // three stages, each on its own thread and each over the stretches in order (the effects keep their state from
        // one to the next): the tracks (in parallel among them) -> the nodes (level by level, in parallel within one) ->
        // the master and the sink. A stretch's buffers are free again once the master is done with it.
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = failed.Token;
        using var free = new SemaphoreSlim(sets.Length);
        using var tracksDone = new SemaphoreSlim(0);
        using var nodesDone = new SemaphoreSlim(0);
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
                var (clips, joins, notes, chain, _, _, pre) = active[k];
                var b = set.Tracks[k];
                Array.Clear(b.L, 0, len);
                Array.Clear(b.R, 0, len);
                var inst = instruments[k];
                if (inst is { } plugin) plugin.Render.Render(at, len, plugin.L.AsSpan(0, len), plugin.R.AsSpan(0, len));
                for (int off = 0; off < len; off += Quantum)
                {
                    int n = Math.Min(Quantum, len - off);
                    TrackDspChain.SampleClips(clips, at + off, n, sampleRate, chain.BufferL(n), chain.BufferR(n), joins);
                    if (inst is { } p)
                    {
                        BusDspChain.Add(p.L.AsSpan(off, n), p.R.AsSpan(off, n), 1f, chain.BufferL(n), chain.BufferR(n));
                    }
                    else if (notes != null) NoteSynth.Render(notes, at + off, n, sampleRate, chain.BufferL(n), chain.BufferR(n));
                    chain.ProcessFilledBuffer(n, anySolo, b.L.AsSpan(off, n), b.R.AsSpan(off, n), out _, out _, out _);
                    if (pre && chain.BlockSends.Length > 0)
                    {
                        chain.KeptL(n, true).CopyTo(b.PreL.AsSpan(off, n));
                        chain.KeptR(n, true).CopyTo(b.PreR.AsSpan(off, n));
                    }
                }
            }
            if (active.Count > 1) Parallel.For(0, active.Count, new ParallelOptions { CancellationToken = token }, Track);
            else if (active.Count == 1) Track(0);
            // summed in the tracks' order into the master and the nodes, so every sample is the same float every time
            Array.Clear(set.L, 0, len);
            Array.Clear(set.R, 0, len);
            for (int j = 0; j < nodeCount; j++)
            {
                Array.Clear(set.NodeL[j], 0, len);
                Array.Clear(set.NodeR[j], 0, len);
            }
            for (int k = 0; k < active.Count; k++)
            {
                var b = set.Tracks[k];
                var (_, _, _, _, to, sends, _) = active[k];
                ReadOnlySpan<float> l = b.L.AsSpan(0, len), r = b.R.AsSpan(0, len);
                BusDspChain.Add(l, r, 1f, to >= 0 ? set.NodeL[to].AsSpan(0, len) : set.L.AsSpan(0, len), to >= 0 ? set.NodeR[to].AsSpan(0, len) : set.R.AsSpan(0, len));
                foreach (var s in sends)
                    BusDspChain.Add(s.Pre ? b.PreL.AsSpan(0, len) : l, s.Pre ? b.PreR.AsSpan(0, len) : r, (float)Math.Clamp(s.Level, 0, 1),
                        set.NodeL[s.To].AsSpan(0, len), set.NodeR[s.To].AsSpan(0, len));
            }
        }

        void RunNodes(int c)
        {
            var set = sets[c % sets.Length];
            int len = Math.Min(ChunkFrames, totalFrames - c * ChunkFrames);
            // each node's slots over its input, in blocks like the tracks (they keep their state from one stretch to the next)
            void Node(int j)
            {
                for (int off = 0; off < len; off += Quantum)
                {
                    int n = Math.Min(Quantum, len - off);
                    nodes[j].Process(set.NodeL[j].AsSpan(off, n), set.NodeR[j].AsSpan(off, n), nodePre[j], set.PreL[j].AsSpan(off, n), set.PreR[j].AsSpan(off, n));
                }
            }
            foreach (var group in levels)
            {
                if (group.Length > 1) Parallel.For(0, group.Length, new ParallelOptions { CancellationToken = token }, i => Node(group[i]));
                else Node(group[0]);
                // then, in the nodes' order, their outputs and sends into the later nodes or the master
                foreach (int j in group)
                {
                    ReadOnlySpan<float> l = set.NodeL[j].AsSpan(0, len), r = set.NodeR[j].AsSpan(0, len);
                    int to = nodeOut[j];
                    BusDspChain.Add(l, r, 1f, to >= 0 ? set.NodeL[to].AsSpan(0, len) : set.L.AsSpan(0, len), to >= 0 ? set.NodeR[to].AsSpan(0, len) : set.R.AsSpan(0, len));
                    foreach (var s in nodeSends[j])
                        BusDspChain.Add(s.Pre ? set.PreL[j].AsSpan(0, len) : l, s.Pre ? set.PreR[j].AsSpan(0, len) : r, (float)Math.Clamp(s.Level, 0, 1),
                            set.NodeL[s.To].AsSpan(0, len), set.NodeR[s.To].AsSpan(0, len));
                }
            }
        }

        void RunMaster(int c)
        {
            var set = sets[c % sets.Length];
            int at = c * ChunkFrames, len = Math.Min(ChunkFrames, totalFrames - at);
            for (int off = 0; off < len; off += Quantum)
            {
                int n = Math.Min(Quantum, len - off), pos = at + off;
                var ml = set.L.AsSpan(off, n);
                var mr = set.R.AsSpan(off, n);
                bus.Process(ml, mr, limit, master);

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
        var nodesTask = Task.Factory.StartNew(() => Stage(RunNodes, tracksDone, nodesDone), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // the master runs here, so the sink and the progress are called on the caller's thread like before
        try
        {
            Stage(RunMaster, nodesDone, free);
            Task.WaitAll(tracksTask, nodesTask);
        }
        finally
        {
            foreach (var made in instruments) made?.Render.Dispose();
        }
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        ct.ThrowIfCancellationRequested();

        onProgress?.Invoke(1.0);
    }
}
