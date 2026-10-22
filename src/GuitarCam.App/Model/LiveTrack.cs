using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GuitarCam.App.Audio;
using GuitarCam.App.Video;
using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App.Model;

/// <summary>
/// A media file's audio in memory (resampled to the engine's sample rate via <see cref="AudioLoader"/>),
/// with its waveform peaks, measured level and compressor envelope. Shared by its clips.
/// </summary>
public sealed class MediaAudio
{
    public const int PeakBin = 64;

    public float[] L { get; }
    public float[] R { get; }
    public int SampleRate { get; }
    public int SourceSampleRate { get; }
    public float[] PeakMin { get; }
    public float[] PeakMax { get; }
    public double RmsDb { get; }
    public CompPresets.TakeEnvelope Envelope { get; }
    public double LengthSec => L.Length / (double)SampleRate;

    public MediaAudio(string path, int targetSampleRate = PlaybackEngine.SampleRate)
    {
        var loaded = AudioLoader.Load(path, targetSampleRate);
        L = loaded.Left;
        R = loaded.Right;
        SampleRate = loaded.SampleRate;
        SourceSampleRate = loaded.SourceSampleRate;

        int bins = (L.Length + PeakBin - 1) / PeakBin;
        PeakMin = new float[bins];
        PeakMax = new float[bins];
        for (int b = 0; b < bins; b++)
        {
            float lo = 0, hi = 0;
            for (int i = b * PeakBin, end = Math.Min(L.Length, i + PeakBin); i < end; i++)
            {
                float v = 0.5f * (L[i] + R[i]);
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            PeakMin[b] = lo;
            PeakMax[b] = hi;
        }
        RmsDb = LevelAnalysis.Analyze([L, R], SampleRate).RmsDb;
        Envelope = CompPresets.Analyze([L, R], SampleRate);
    }
}

/// <summary>A clip loaded for playback: its media's audio (null = offline) and its own video decoder.</summary>
public sealed class LiveClip
{
    public Clip Clip { get; }
    public Media? Media { get; }
    public MediaAudio? Audio { get; }
    public bool Offline => Audio == null;

    public VideoDecoder? Video { get; private set; }
    public WriteableBitmap? Frame { get; private set; }
    public bool InClip { get; private set; }
    long frameVersion;

    public LiveClip(Clip clip, Media? media, MediaAudio? audio)
    {
        Clip = clip;
        Media = media;
        Audio = audio;
    }

    public string Name => Media?.Name ?? "Sin medio";
    public double LengthSec => Audio?.LengthSec ?? Media?.DurationSec ?? 0;
    public ClipEnvelope.ClipWindow Window => ClipEnvelope.Window(LengthSec, Clip.AtSec, Clip.TrimStartSec, Clip.TrimEndSec, Media?.SyncMs ?? 0);
    public double OffsetSec => Window.Origin;
    public double ClipStartSec => Window.Start;
    public double ClipEndSec => Window.End;
    public ClipEnvelope.FadePair AudioFades => ClipEnvelope.Fades(Window, Clip.FadeInSec, Clip.FadeOutSec).Audio;
    public float Gain => (float)Math.Pow(10, Clip.GainDb / 20);

    public void OpenVideo(ProjectStore store)
    {
        if (store.Resolve(Media?.Video) is { } path) Video = VideoDecoder.Open(path);
    }

    /// <summary>
    /// Asks the decoder for the frame at a timeline time and copies a new one into <see cref="Frame"/>.
    /// Returns true when the picture changed. Outside the clip the panel is empty.
    /// </summary>
    public bool UpdateVideo(double timelineSec)
    {
        if (Video == null) return false;
        bool inside = timelineSec >= ClipStartSec && timelineSec < ClipEndSec;
        bool changed = inside != InClip;
        InClip = inside;
        if (!inside) return changed;
        Video.Want(timelineSec - OffsetSec + (Media?.VideoOffsetMs ?? 0) / 1000);
        Frame ??= new WriteableBitmap(new PixelSize(Video.Width, Video.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var wb = Frame;
        return Video.TryCopyLatest(ref frameVersion, buf =>
        {
            using var fb = wb.Lock();
            int row = Video.Width * 4;
            for (int y = 0; y < Video.Height; y++) Marshal.Copy(buf, y * row, fb.Address + y * fb.RowBytes, row);
        }) || changed;
    }

    public void CloseVideo()
    {
        Video?.Dispose();
        Video = null;
    }
}

/// <summary>
/// A track loaded for playback: its clips, the full per-track DSP chain (<see cref="TrackDspChain"/>:
/// clip fades → saturator → compressor EQ → compressor → 6-band track EQ → panner → fader → post-fader sends)
/// and the live meters.
/// </summary>
public sealed class LiveTrack
{
    readonly int sampleRate;

    public Track Track { get; }
    public Channel Channel => Track.Channel;
    public IReadOnlyList<LiveClip> Clips { get; }
    public TrackDspChain Chain { get; }
    public Saturator Sat => Chain.Sat;
    public CompEqFilter CompEq => Chain.CompEq;
    public Compressor Comp => Chain.Comp;
    public TrackEq Eq => Chain.Eq;

    // engine scratch + meters (written on the audio thread, read by the UI)
    public readonly float[] BufL = new float[TrackDspChain.MaxBlockSize];
    public readonly float[] BufR = new float[TrackDspChain.MaxBlockSize];
    public float MeterL, MeterR;
    public double GrDb;
    public float ShowL, ShowR; // UI side, with fall-off

    public LiveTrack(Track track, IReadOnlyList<LiveClip> clips, int sampleRate)
    {
        this.sampleRate = sampleRate;
        Track = track;
        Clips = clips;
        Chain = new TrackDspChain(sampleRate);
        ApplyAll();
    }

    /// <summary>Level the "Vida" knob is measured against: the first clip with audio (one take per track for now).</summary>
    public double RmsDb => Clips.FirstOrDefault(c => c.Audio != null)?.Audio!.RmsDb ?? -24;

    /// <summary>Pre-analyzed take envelope for the first clip with audio, used by <see cref="RecalculateCompPreset"/>.</summary>
    public CompPresets.TakeEnvelope? Envelope => Clips.FirstOrDefault(c => c.Audio != null)?.Audio!.Envelope;

    public bool HasPanel => Track.Video && Clips.Count > 0;
    public double EndSec => Clips.Count == 0 ? 0 : Clips.Max(c => c.ClipEndSec);

    /// <summary>The clip whose picture shows in the panel (the first one under the playhead).</summary>
    public LiveClip? ShownClip => Clips.FirstOrDefault(c => c.InClip);

    /// <summary>
    /// Updates the entire channel strip (saturator, compressor + its 4-stage EQ, 6-band track EQ, fader,
    /// pan, mute/solo and reverb/delay sends) from the UI thread without audio dropouts or torn coefficients.
    /// If <paramref name="ch"/> is provided, replaces <c>Track.Channel</c> first.
    /// </summary>
    public void ApplyChannel(Channel? ch = null)
    {
        if (ch != null) Track.Channel = ch;
        var cur = Track.Channel;
        Chain.Volume = cur.Volume;
        Chain.Pan = cur.Pan;
        Chain.Muted = cur.Muted;
        Chain.Solo = cur.Solo;
        Chain.ReverbOn = cur.ReverbOn;
        Chain.ReverbSend = cur.ReverbSend;
        Chain.DelayOn = cur.DelayOn;
        Chain.DelaySend = cur.DelaySend;
        ApplySat();
        ApplyComp();
        ApplyEq();
    }

    public void ApplyAll() => ApplyChannel();

    public void ApplySat() =>
        Chain.ApplySat(SatParams.From(Channel.Sat.On, Channel.Sat.Mode == "tape" ? SatMode.Tape : SatMode.Tube, Channel.Sat.Amount, RmsDb));

    public void ApplyComp()
    {
        var c = Channel.Comp;
        var eq = new CompEqParams(c.Eq.HpfHz, c.Eq.MudDb, c.Eq.PresenceDb, c.Eq.PresenceHz, c.Eq.AirDb);
        Chain.ApplyComp(new CompState(
            c.On,
            c.Preset,
            c.Amount,
            c.Limit,
            c.Threshold,
            c.Ratio,
            c.Attack,
            c.Release,
            c.Knee,
            c.Makeup,
            eq));
    }

    /// <summary>
    /// Recomputes the compressor's threshold, ratio, attack/release, makeup and CompEq from the preset and
    /// amount against the take's envelope (port of <c>updateComp</c> in <c>compPresets.ts</c>), writes them
    /// back into <see cref="Channel.Comp"/> and updates the live DSP chain.
    /// </summary>
    public double RecalculateCompPreset()
    {
        var c = Channel.Comp;
        var env = Envelope ?? CompPresets.Analyze([], sampleRate);
        var eq = new CompEqParams(c.Eq.HpfHz, c.Eq.MudDb, c.Eq.PresenceDb, c.Eq.PresenceHz, c.Eq.AirDb);
        var state = CompPresets.UpdateComp(
            new CompState(c.On, c.Preset, c.Amount, c.Limit, c.Threshold, c.Ratio, c.Attack, c.Release, c.Knee, c.Makeup, eq),
            env);
        c.Threshold = state.Threshold;
        c.Ratio = state.Ratio;
        c.Attack = state.Attack;
        c.Release = state.Release;
        c.Knee = state.Knee;
        c.Makeup = state.Makeup;
        c.Eq.HpfHz = state.Eq.HpfHz;
        c.Eq.MudDb = state.Eq.MudDb;
        c.Eq.PresenceDb = state.Eq.PresenceDb;
        c.Eq.PresenceHz = state.Eq.PresenceHz;
        c.Eq.AirDb = state.Eq.AirDb;
        Chain.ApplyComp(state);
        return state.EstGrDb ?? 0;
    }

    public void ApplyEq()
    {
        var eq = Channel.Eq;
        Span<EqBandSpec> bands = stackalloc EqBandSpec[TrackEq.BandCount];
        for (int i = 0; i < TrackEq.BandCount; i++)
        {
            if (i < eq.Bands.Count)
            {
                var b = eq.Bands[i];
                bands[i] = EqBandSpec.FromStrings(b.On, b.Type, b.Freq, b.Gain, b.Q, b.Slope, TrackEq.DefaultBands[i].Type);
            }
            else bands[i] = TrackEq.DefaultBands[i];
        }
        Chain.Eq.Set(eq.On, bands);
    }

    /// <summary>
    /// Samples all clips on this track for frames <c>[posFrame .. posFrame + n)</c> with their trim window,
    /// <c>SyncMs</c> offset, linear fade-in/out envelope and clip gain, then runs the full insert chain and
    /// accumulates into the master and reverb/delay send buffers. Zero allocations.
    /// </summary>
    public void MixInto(
        long posFrame,
        int n,
        bool anySolo,
        Span<float> masterL,
        Span<float> masterR,
        Span<float> reverbInL,
        Span<float> reverbInR,
        Span<float> delayInL,
        Span<float> delayInR)
    {
        var bl = Chain.BufferL(n);
        var br = Chain.BufferR(n);
        bl.Clear();
        br.Clear();

        double invSr = 1.0 / sampleRate;
        for (int cIdx = 0; cIdx < Clips.Count; cIdx++)
        {
            var c = Clips[cIdx];
            var a = c.Audio;
            if (a == null || a.L.Length == 0) continue;
            var w = c.Window;
            if (w.End <= w.Start) continue;
            long startFrame = Math.Max(0, (long)Math.Round(w.Start * sampleRate));
            long endFrame = (long)Math.Round(w.End * sampleRate);
            if (posFrame + n <= startFrame || posFrame >= endFrame) continue;

            long originFrame = (long)Math.Round(w.Origin * sampleRate);
            var fades = c.AudioFades;
            float cg = c.Gain;
            float[] left = a.L;
            float[] right = a.R;

            for (int i = 0; i < n; i++)
            {
                long f = posFrame + i;
                if (f < startFrame || f >= endFrame) continue;
                long src = f - originFrame;
                if (src < 0 || src >= left.Length) continue;
                double timeSec = f * invSr;
                float env = (float)ClipEnvelope.GainAt(timeSec, w.Start, w.End, fades.FadeIn, fades.FadeOut) * cg;
                bl[i] += left[src] * env;
                br[i] += right[src] * env;
            }
        }

        var ch = Channel;
        Chain.Volume = ch.Volume;
        Chain.Pan = ch.Pan;
        Chain.Muted = ch.Muted;
        Chain.Solo = ch.Solo;
        Chain.ReverbOn = ch.ReverbOn;
        Chain.ReverbSend = ch.ReverbSend;
        Chain.DelayOn = ch.DelayOn;
        Chain.DelaySend = ch.DelaySend;

        Chain.ProcessFilledBuffer(
            n,
            anySolo,
            masterL,
            masterR,
            reverbInL,
            reverbInR,
            delayInL,
            delayInR,
            out float pkL,
            out float pkR,
            out double gr);

        GrDb = Math.Min(GrDb, gr);
        if (pkL > MeterL) MeterL = pkL;
        if (pkR > MeterR) MeterR = pkR;
    }

    public void CloseVideo()
    {
        foreach (var c in Clips) c.CloseVideo();
    }
}
