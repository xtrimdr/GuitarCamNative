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
/// The samples a clip plays: its media's audio as recorded, or moved by its track's Transponer
/// (<see cref="GuitarCam.Audio.Dsp.PitchShifter"/>, same length). Never changed once built, so the audio thread can hold it.
/// </summary>
public sealed class ClipSound(float[] left, float[] right)
{
    public readonly float[] L = left, R = right;
}

/// <summary>
/// A media file's audio in memory (resampled to the engine's sample rate via <see cref="AudioLoader"/>),
/// with its waveform peaks, measured level and compressor envelope. Shared by its clips.
/// </summary>
public sealed class MediaAudio
{
    public const int PeakBin = 64;

    public float[] L { get; }
    public float[] R { get; }
    /// <summary>The audio as recorded, as a clip plays it.</summary>
    public ClipSound Sound { get; }
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
        Sound = new ClipSound(L, R);
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

/// <summary>
/// A clip loaded for playback: its media's audio (null = offline) and its own video decoder. A timeline edit
/// or an undo points <see cref="Clip"/> at the project's new object instead of building another one, so the
/// audio and the decoder carry on; the audio thread reads the reference once per block.
/// </summary>
public sealed class LiveClip
{
    Clip clip;
    /// <summary>Re-pointing it (a timeline edit, an undo) also takes the clip's volume curve.</summary>
    public Clip Clip
    {
        get => clip;
        set { clip = value; RefreshCurve(); }
    }
    /// <summary>The clip's volume curve as the audio thread reads it: swapped whole, never changed in place.</summary>
    public ClipGainCurve.Point[] Curve { get; private set; } = [];

    /// <summary>Takes the curve again after an edit that changed it on the same clip (the inspector edits in place).</summary>
    public void RefreshCurve() => Curve = clip.CurvePoints();
    public Media? Media { get; set; }
    public MediaAudio? Audio { get; }
    public bool Offline => Audio == null;

    volatile ClipSound? sound;
    /// <summary>
    /// What it plays (its <see cref="Audio"/> as recorded, or transposed): the UI swaps it whole and the audio thread
    /// crossfades to it over <see cref="LiveTrack.SoundFade"/> samples. The waveform and the level stay the recording's.
    /// </summary>
    public ClipSound? Sound { get => sound; set => sound = value; }
    /// <summary>The semitones <see cref="Sound"/> is moved by (UI thread).</summary>
    public double SoundSemis { get; set; }
    // audio thread only: the sound it last played and the one it is fading out of
    internal ClipSound? Heard, Fading;
    internal int FadeLeft;

    public VideoDecoder? Video { get; private set; }
    /// <summary>The take's video file (it may be missing), for the lane's thumbnails; null for audio-only media.</summary>
    public string? VideoPath { get; init; }
    public WriteableBitmap? Frame { get; private set; }
    public bool InClip { get; private set; }
    public ProjectSettings? Settings { get; set; }
    long frameVersion;
    byte[]? rawFrame;
    byte[]? gradedFrame;
    ExportColorSpec? lastColorSpec;
    int lastGrainLevels = -1;
    int lastFrameSeed = int.MinValue;

    public LiveClip(Clip clip, Media? media, MediaAudio? audio)
    {
        this.clip = clip;
        RefreshCurve();
        Media = media;
        Audio = audio;
        sound = audio?.Sound;
    }

    public string Name => Media?.Name ?? Loc.T("track.noMedia");
    public double LengthSec => Audio?.LengthSec ?? Media?.DurationSec ?? 0;
    public ClipEnvelope.ClipWindow Window => ClipEnvelope.Window(LengthSec, Clip.AtSec, Clip.TrimStartSec, Clip.TrimEndSec, Clip.EffectiveSyncMs(Media));
    public double OffsetSec => Window.Origin;
    public double ClipStartSec => Window.Start;
    public double ClipEndSec => Window.End;
    public ClipEnvelope.FadePair AudioFades => ClipEnvelope.Fades(Window, Clip.FadeInSec, Clip.FadeOutSec).Audio;
    public float Gain => (float)Math.Pow(10, Clip.GainDb / 20);

    /// <summary>True once <see cref="OpenVideo"/> ran, so a picture that can't be opened isn't retried on every edit.</summary>
    public bool VideoTried { get; private set; }

    public void OpenVideo(ProjectStore store)
    {
        VideoTried = true;
        if (store.Resolve(Media?.Video) is { } path) Video = VideoDecoder.Open(path);
    }

    /// <summary>
    /// Asks the decoder for the frame at a timeline time, applies the 3D LUT trilinear color grade and
    /// monochrome film grain (<see cref="VideoColorProcessor"/>), and copies the result into <see cref="Frame"/>.
    /// Returns true when the picture changed (new decoded frame, color/grain edit while paused, or animated grain).
    /// Outside the clip the panel is empty.
    /// </summary>
    public bool UpdateVideo(double timelineSec, ColorGrade? color = null, VideoFx? videoFx = null)
    {
        if (Video == null) return false;
        bool inside = timelineSec >= ClipStartSec && timelineSec < ClipEndSec;
        bool changed = inside != InClip;
        InClip = inside;
        if (!inside) return changed;

        double videoSec = timelineSec - OffsetSec + Clip.EffectiveVideoOffsetMs(Media) / 1000;
        Video.Want(videoSec);
        Frame ??= new WriteableBitmap(new PixelSize(Video.Width, Video.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        bool newRaw = Video.TryCopyLatest(ref frameVersion, buf =>
        {
            if (rawFrame == null || rawFrame.Length != buf.Length)
                rawFrame = new byte[buf.Length];
            Buffer.BlockCopy(buf, 0, rawFrame, 0, buf.Length);
        });

        if (rawFrame == null) return changed;

        var colSpec = PreviewVideoFx.ToSpec(color ?? Settings?.Color);
        var fxSpec = PreviewVideoFx.ToSpec(videoFx ?? Settings?.VideoFx);
        int grainLevels = VideoColorProcessor.GrainLevels(fxSpec);
        int frameSeed = (int)Math.Floor(Math.Max(0, timelineSec) * VideoDecoder.Fps);

        bool needWrite = newRaw
            || lastColorSpec != colSpec
            || lastGrainLevels != grainLevels
            || (grainLevels > 0 && frameSeed != lastFrameSeed);

        if (!needWrite) return changed;

        lastColorSpec = colSpec;
        lastGrainLevels = grainLevels;
        lastFrameSeed = frameSeed;

        byte[] srcToCopy;
        if (VideoColorProcessor.IsNeutralGrade(colSpec) && grainLevels <= 0)
        {
            srcToCopy = rawFrame;
        }
        else
        {
            if (gradedFrame == null || gradedFrame.Length != rawFrame.Length)
                gradedFrame = new byte[rawFrame.Length];
            var lutTable = PreviewVideoFx.GetLutTable(colSpec);
            VideoColorProcessor.GradeAndGrainBgraParallel(
                rawFrame,
                gradedFrame,
                Video.Width,
                Video.Height,
                lutTable,
                grainLevels,
                frameSeed);
            srcToCopy = gradedFrame;
        }

        using (var fb = Frame.Lock())
        {
            int row = Video.Width * 4;
            for (int y = 0; y < Video.Height; y++)
                Marshal.Copy(srcToCopy, y * row, fb.Address + y * fb.RowBytes, row);
        }
        return true;
    }

    /// <summary>Closes the decoder; <see cref="OpenVideo"/> may open it again (a project switched back from solo audio).</summary>
    public void CloseVideo()
    {
        Video?.Dispose();
        Video = null;
        VideoTried = false;
        InClip = false;
        rawFrame = null;
        gradedFrame = null;
    }
}

/// <summary>
/// A track loaded for playback: its clips, the full per-track DSP chain (<see cref="TrackDspChain"/>:
/// clip fades → saturator → compressor EQ → compressor → 6-band track EQ → panner → fader → post-fader sends)
/// and the live meters. Timeline edits re-point <see cref="Track"/> and swap <see cref="Clips"/> for a new list
/// (never changed in place: the audio thread may be walking the old one), keeping the chain's state.
/// </summary>
public sealed class LiveTrack
{
    /// <summary>10 ms at 48 kHz: a clip that starts playing its transposed audio crossfades to it, never clicks.</summary>
    public const int SoundFade = 480;
    readonly int sampleRate;

    public Track Track { get; set; }
    public Channel Channel => Track.Channel;
    public IReadOnlyList<LiveClip> Clips { get; set; }
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
    public float InputL, InputR; // UI side: the input's peak while armed, shown on the track's meter
    public double GrPeak, GrShow; // UI side: this tick's reduction, and the mixer's (instant attack, slow release)
    /// <summary>Silences the track while a take that replaces its clips is being recorded (set by the UI).</summary>
    public volatile bool RecordMuted;
    /// <summary>
    /// What the track's folder does to it (native only, <see cref="TrackFolders.Mix"/>): its fader multiplies the
    /// track's, its mute and solo add to the track's. Set by the UI, read by the audio thread.
    /// </summary>
    public volatile float FolderGain = 1;
    public volatile bool FolderMuted, FolderSolo;
    public bool Soloed => Channel.Solo || FolderSolo;
    public bool Silenced => Channel.Muted || FolderMuted;

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

    /// <summary>The clip whose picture shows in the panel: the last one under the playhead, the one on top in the timeline and the export.</summary>
    public LiveClip? ShownClip => Clips.LastOrDefault(c => c.InClip);

    /// <summary>Associates the project's settings with all clips on this track so <see cref="LiveClip.UpdateVideo"/> follows color and video FX changes.</summary>
    public void SetProjectSettings(ProjectSettings? settings)
    {
        for (int i = 0; i < Clips.Count; i++)
            Clips[i].Settings = settings;
    }

    /// <summary>
    /// Updates the entire channel strip (saturator, compressor + its 4-stage EQ, 6-band track EQ, fader,
    /// pan, mute/solo and reverb/delay sends) from the UI thread without audio dropouts or torn coefficients.
    /// If <paramref name="ch"/> is provided, replaces <c>Track.Channel</c> first.
    /// </summary>
    public void ApplyChannel(Channel? ch = null)
    {
        if (ch != null) Track.Channel = ch;
        var cur = Track.Channel;
        Chain.Volume = cur.Volume * FolderGain;
        Chain.Pan = cur.Pan;
        Chain.Muted = cur.Muted || FolderMuted;
        Chain.Solo = cur.Solo || FolderSolo;
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
    /// accumulates into the master and reverb/delay send buffers. Zero allocations. During a seek crossfade
    /// (<paramref name="fromFrame"/> &gt;= 0) the clips at the old position, weighted by <paramref name="wOut"/>, are
    /// summed with the new ones, weighted by <paramref name="wIn"/>, before the chain, so its state stays continuous.
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
        Span<float> delayInR,
        long fromFrame = -1,
        ReadOnlySpan<float> wIn = default,
        ReadOnlySpan<float> wOut = default)
    {
        var bl = Chain.BufferL(n);
        var br = Chain.BufferR(n);
        bl.Clear();
        br.Clear();
        if (fromFrame < 0) AddClips(posFrame, n, bl, br, default);
        else
        {
            AddClips(fromFrame, n, bl, br, wOut);
            AddClips(posFrame, n, bl, br, wIn);
        }
        RunChain(n, anySolo, masterL, masterR, reverbInL, reverbInR, delayInL, delayInR);
    }

    /// <summary>Adds every clip's samples for frames <c>[posFrame .. posFrame + n)</c>, times <paramref name="weight"/> if given.</summary>
    void AddClips(long posFrame, int n, Span<float> bl, Span<float> br, ReadOnlySpan<float> weight)
    {
        double invSr = 1.0 / sampleRate;
        var clips = Clips;
        for (int cIdx = 0; cIdx < clips.Count; cIdx++)
        {
            var c = clips[cIdx];
            var a = c.Sound;
            if (a == null || a.L.Length == 0) continue;
            if (a != c.Heard)
            {
                c.Fading = c.Heard;
                c.Heard = a;
                c.FadeLeft = c.Fading != null && c.Fading.L.Length == a.L.Length ? SoundFade : 0;
            }
            var old = c.FadeLeft > 0 ? c.Fading : null;
            int fadeLeft = c.FadeLeft;
            var w = c.Window;
            if (w.End <= w.Start) continue;
            long startFrame = Math.Max(0, (long)Math.Round(w.Start * sampleRate));
            long endFrame = (long)Math.Round(w.End * sampleRate);
            if (posFrame + n <= startFrame || posFrame >= endFrame) continue;

            long originFrame = (long)Math.Round(w.Origin * sampleRate);
            var fades = c.AudioFades;
            float cg = c.Gain;
            var curve = c.Curve;
            float k = 1, dk = 0;
            float[] left = a.L;
            float[] right = a.R;

            for (int i = 0; i < n; i++)
            {
                long f = posFrame + i;
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
                float env = (float)ClipEnvelope.GainAt(timeSec, w.Start, w.End, fades.FadeIn, fades.FadeOut) * cg * (k + dk * (i % ClipGainCurve.Step));
                if (!weight.IsEmpty) env *= weight[i];
                float sl = left[src], sr = right[src];
                if (old != null && fadeLeft > 0)
                {
                    float wOld = fadeLeft-- / (float)SoundFade;
                    sl += (old.L[src] - sl) * wOld;
                    sr += (old.R[src] - sr) * wOld;
                }
                bl[i] += sl * env;
                br[i] += sr * env;
            }
            c.FadeLeft = fadeLeft;
        }
    }

    void RunChain(int n, bool anySolo, Span<float> masterL, Span<float> masterR, Span<float> reverbInL, Span<float> reverbInR,
        Span<float> delayInL, Span<float> delayInR)
    {
        var ch = Channel;
        Chain.Volume = ch.Volume * FolderGain;
        Chain.Pan = ch.Pan;
        Chain.Muted = ch.Muted || FolderMuted || RecordMuted;
        Chain.Solo = ch.Solo || FolderSolo;
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
