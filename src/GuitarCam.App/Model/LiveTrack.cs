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
    /// <summary>
    /// True once <see cref="Frame"/> holds a picture decoded since the playhead entered the clip: before that it may
    /// still be one from the last time it was shown (another part of the take), so the preview leaves the panel empty.
    /// </summary>
    public bool Fresh { get; private set; }
    /// <summary>How far ahead of a clip's start its decoder is started (FFmpeg takes ~100–300 ms to give the first frame).</summary>
    const double PreRollSec = 1.0;
    /// <summary>
    /// How far its picture shows before its start and after its end: a crossfade's half on each side of the join
    /// (<see cref="ClipTransitions"/>), set every tick by <see cref="LiveTrack.UpdateTransitions"/>. Zero otherwise.
    /// </summary>
    public double Lead { get; internal set; }
    public double Tail { get; internal set; }
    volatile ClipAudioJoins? audioJoins;
    /// <summary>
    /// What the transitions do to its sound (<see cref="ClipTransitions"/>), null = nothing: swapped whole by
    /// <see cref="LiveTrack.UpdateTransitions"/> when they change, read by the audio thread once per block.
    /// </summary>
    public ClipAudioJoins? AudioJoins { get => audioJoins; internal set => audioJoins = value; }
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
        var video = Video;
        if (video == null) return false;
        bool inside = timelineSec >= ClipStartSec - Lead && timelineSec < ClipEndSec + Tail;
        bool changed = inside != InClip;
        InClip = inside;
        double videoOffset = -OffsetSec + Clip.EffectiveVideoOffsetMs(Media) / 1000;
        if (!inside)
        {
            // starting FFmpeg takes a few frames: done ahead of time, a cut doesn't freeze the picture
            double until = ClipStartSec - Lead - timelineSec;
            if (until > 0 && until <= PreRollSec) video.Want(VideoTime(ClipStartSec - Lead, videoOffset));
            else video.Idle();
            if (changed) Fresh = false;
            return changed;
        }
        if (changed)
        {
            Fresh = false;
            frameVersion = -1; // the decoder's current frame counts again, if it's from around here
        }

        video.Want(VideoTime(timelineSec, videoOffset));
        Frame ??= new WriteableBitmap(new PixelSize(video.Width, video.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        bool newRaw = video.TryCopyLatest(ref frameVersion, buf =>
        {
            if (rawFrame == null || rawFrame.Length != buf.Length)
                rawFrame = new byte[buf.Length];
            Buffer.BlockCopy(buf, 0, rawFrame, 0, buf.Length);
        });

        if (newRaw && !Fresh)
        {
            Fresh = true;
            changed = true;
        }
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
                video.Width,
                video.Height,
                lutTable,
                grainLevels,
                frameSeed);
            srcToCopy = gradedFrame;
        }

        using (var fb = Frame.Lock())
        {
            int row = video.Width * 4;
            for (int y = 0; y < video.Height; y++)
                Marshal.Copy(srcToCopy, y * row, fb.Address + y * fb.RowBytes, row);
        }
        return true;
    }

    /// <summary>
    /// The video time for a timeline time. Past the clip's edges (a transition's overhang) it stays within the
    /// recording, so a side with no hidden video holds its first or last frame, as the export does.
    /// </summary>
    double VideoTime(double timelineSec, double videoOffset)
    {
        double sec = timelineSec + videoOffset;
        if (timelineSec < ClipStartSec) sec = Math.Max(0, sec);
        if (timelineSec >= ClipEndSec) sec = Math.Min(sec, Math.Max(0, LengthSec - 2 / VideoDecoder.Fps));
        return sec;
    }

    /// <summary>Closes the decoder; <see cref="OpenVideo"/> may open it again (a project switched back from solo audio).</summary>
    public void CloseVideo() => DetachVideo()?.Dispose();

    /// <summary>
    /// Lets go of the decoder without closing it, on the UI thread, so a clip still on screen is never ticked while
    /// another thread clears it (that threw inside the tick and stopped its timer for good). The caller disposes it.
    /// </summary>
    public VideoDecoder? DetachVideo()
    {
        var video = Video;
        Video = null;
        VideoTried = false;
        InClip = false;
        Fresh = false;
        rawFrame = null;
        gradedFrame = null;
        return video;
    }
}

/// <summary>
/// A track loaded for playback: its clips, the full per-track DSP chain (<see cref="TrackDspChain"/>:
/// clip fades → the channel's insert slots → panner → fader → post-fader sends)
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
    /// <summary>
    /// Its folder's bus (<see cref="PlaybackEngine.SetFolders"/>), or null: while the bus has slots, the engine sends
    /// this track's post-fader output there instead of the master. Set by the UI, read by the audio thread once per block.
    /// </summary>
    public volatile BusDspChain? Bus;
    /// <summary>
    /// The engine's extra returns by id (<see cref="PlaybackEngine.SetReturns"/>), for <see cref="ApplySends"/>; null
    /// until the engine hands them over. UI thread only.
    /// </summary>
    public IReadOnlyDictionary<string, ReturnDspChain>? Returns { get; set; }
    /// <summary>The Escuchar tool is playing this track alone (native only): a solo that is not saved and does not light S.</summary>
    public volatile bool ListenSolo;
    public bool Soloed => Channel.Solo || FolderSolo || ListenSolo;
    public bool Silenced => Channel.Muted || FolderMuted;

    public LiveTrack(Track track, IReadOnlyList<LiveClip> clips, int sampleRate)
    {
        this.sampleRate = sampleRate;
        Track = track;
        Clips = clips;
        Chain = new TrackDspChain(sampleRate);
        ApplyAll();
        started = true;
    }

    /// <summary>Level the "Vida" knob is measured against: the first clip with audio (one take per track for now).</summary>
    public double RmsDb => Clips.FirstOrDefault(c => c.Audio != null)?.Audio!.RmsDb ?? -24;

    /// <summary>Pre-analyzed take envelope for the first clip with audio, used by <see cref="RecalculateCompPreset"/>.</summary>
    public CompPresets.TakeEnvelope? Envelope => Clips.FirstOrDefault(c => c.Audio != null)?.Audio!.Envelope;

    public bool HasPanel => Track.Video && Clips.Count > 0;
    public double EndSec => Clips.Count == 0 ? 0 : Clips.Max(c => c.ClipEndSec);

    /// <summary>The clip whose picture shows in the panel: the last one under the playhead, the one on top in the timeline and the export.</summary>
    public LiveClip? ShownClip => Clips.LastOrDefault(c => c.InClip);

    // the clips' windows and transitions, refilled every tick (grown only when the track gets more clips)
    ClipEnvelope.ClipWindow[] windows = [];
    TransitionKind[] kinds = [];
    double[] secs = [];
    int[] takes = [];
    AudioJoin[] ins = [], outs = [];
    /// <summary>The video transition under the playhead (<see cref="UpdateTransitions"/>), or null.</summary>
    public ClipTransitions.Join? Transition { get; private set; }
    /// <summary>The playhead's time when <see cref="Transition"/> was found, for <see cref="ClipTransitions.Weights"/>.</summary>
    public double TransitionAt { get; private set; }

    /// <summary>
    /// Finds this track's transitions (<see cref="ClipTransitions"/>, the export's same math). The sound: each clip's
    /// <see cref="LiveClip.AudioJoins"/>, for the audio thread. The picture (video tracks): each clip's
    /// <see cref="LiveClip.Lead"/> / <see cref="LiveClip.Tail"/> so its decoder runs past its edges during a crossfade,
    /// and <see cref="Transition"/> to the one under <paramref name="t"/>. Called every UI tick before the clips'
    /// <see cref="LiveClip.UpdateVideo"/>; allocates only when a transition changed. Returns true when the preview must be redrawn
    /// (inside a transition while the playhead moves, or one appeared, went or changed).
    /// </summary>
    public bool UpdateTransitions(double t)
    {
        var clips = Clips;
        int n = clips.Count;
        if (windows.Length < n)
        {
            int size = Math.Max(n, 2 * windows.Length);
            windows = new ClipEnvelope.ClipWindow[size];
            kinds = new TransitionKind[size];
            secs = new double[size];
            takes = new int[size];
            ins = new AudioJoin[size];
            outs = new AudioJoin[size];
        }
        for (int i = 0; i < n; i++)
        {
            var c = clips[i];
            windows[i] = c.Window;
            kinds[i] = ClipTransitions.Parse(c.Clip.Transition);
            secs[i] = c.Clip.TransitionSec;
            takes[i] = i;
            for (int k = 0; k < i; k++)
                if (c.Media != null && clips[k].Media?.Id == c.Media.Id) { takes[i] = k; break; }
            c.Lead = c.Tail = 0;
        }

        // the sound: every track, video or not; a clip gets a new (immutable) set only when its joins changed
        ClipTransitions.AudioJoins(windows.AsSpan(0, n), kinds.AsSpan(0, n), secs.AsSpan(0, n), takes.AsSpan(0, n), ins, outs);
        for (int i = 0; i < n; i++)
        {
            var c = clips[i];
            var now = c.AudioJoins;
            bool has = ins[i].Exists || outs[i].Exists;
            if (now == null ? has : !has || now.In != ins[i] || now.Out != outs[i])
                c.AudioJoins = has ? new ClipAudioJoins(ins[i], outs[i]) : null;
        }

        ClipTransitions.Join? active = null;
        if (Track.Video)
        {
            var w = windows.AsSpan(0, n);
            for (int i = 0; i < n; i++)
            {
                if (ClipTransitions.Find(w, i, kinds[i], secs[i]) is not { } j) continue;
                if (j.Overhang > 0)
                {
                    clips[j.In].Lead = Math.Max(clips[j.In].Lead, j.Overhang);
                    clips[j.Out].Tail = Math.Max(clips[j.Out].Tail, j.Overhang);
                }
                if (t >= j.Start && t < j.End) active = j;
            }
        }
        bool redraw = active != Transition || active != null && t != TransitionAt;
        Transition = active;
        TransitionAt = t;
        return redraw;
    }

    /// <summary>Associates the project's settings with all clips on this track so <see cref="LiveClip.UpdateVideo"/> follows color and video FX changes.</summary>
    public void SetProjectSettings(ProjectSettings? settings)
    {
        for (int i = 0; i < Clips.Count; i++)
            Clips[i].Settings = settings;
    }

    /// <summary>
    /// Updates the entire channel strip (its insert slots, fader, pan, mute/solo and reverb/delay sends) from the UI
    /// thread without audio dropouts or torn coefficients: the slots keep their state by id, and a new list of slots
    /// fades over (<see cref="TrackDspChain.SetInserts"/>). If <paramref name="ch"/> is provided, replaces
    /// <c>Track.Channel</c> first.
    /// </summary>
    public void ApplyChannel(Channel? ch = null)
    {
        if (ch != null) Track.Channel = ch;
        var cur = Track.Channel;
        Chain.Volume = cur.Volume * FolderGain;
        Chain.Pan = cur.Pan;
        Chain.Muted = cur.Muted || FolderMuted;
        Chain.Solo = cur.Solo || FolderSolo || ListenSolo;
        Chain.ReverbOn = cur.ReverbOn;
        Chain.ReverbSend = cur.ReverbSend;
        Chain.DelayOn = cur.DelayOn;
        Chain.DelaySend = cur.DelaySend;
        Chain.ReverbPre = cur.ReverbPre;
        Chain.DelayPre = cur.DelayPre;
        ApplyInserts();
        ApplySends();
    }

    /// <summary>
    /// Hands the chain its sends to the extra returns as a new immutable array (<see cref="TrackDspChain.Sends"/>): the
    /// ones that are on, above 0 and whose return the engine has. UI thread, after any edit of the sends or the returns.
    /// </summary>
    public void ApplySends()
    {
        var map = Returns;
        var list = Channel.Sends;
        int n = 0;
        foreach (var s in list)
            if (s.On && s.Level > 0 && map != null && map.ContainsKey(s.ReturnId)) n++;
        if (n == 0)
        {
            if (Chain.Sends.Length > 0) Chain.Sends = [];
            return;
        }
        var next = new TrackSend[n];
        int k = 0;
        foreach (var s in list)
            if (s.On && s.Level > 0 && map!.TryGetValue(s.ReturnId, out var to))
                next[k++] = new TrackSend(to, -1, (float)Math.Clamp(s.Level, 0, 1), s.Pre);
        Chain.Sends = next;
    }

    public void ApplyAll() => ApplyChannel();

    /// <summary>Hands the slots to the chain (a spare the edit turned on becomes a slot first).</summary>
    public void ApplyInserts()
    {
        Channel.AdoptSpares();
        Chain.SetInserts(ChannelDsp.Specs(Channel, RmsDb), fade: started);
    }

    /// <summary>False only while the constructor sets the chain up, before the audio thread can run it.</summary>
    bool started;

    /// <summary>
    /// Recomputes a compressor's threshold, ratio, attack/release, makeup and CompEq from its preset and amount against
    /// the take's envelope (port of <c>updateComp</c> in <c>compPresets.ts</c>), writes them back into
    /// <paramref name="comp"/> (the channel's first compressor when null) and updates the live DSP chain.
    /// </summary>
    public double RecalculateCompPreset(CompSettings? comp = null)
    {
        double gr = ChannelDsp.RecalculateComp(comp ?? Channel.Comp, Envelope ?? CompPresets.Analyze([], sampleRate));
        ApplyInserts();
        return gr;
    }

    /// <summary>
    /// Samples all clips on this track for frames <c>[posFrame .. posFrame + n)</c> with their trim window,
    /// <c>SyncMs</c> offset, linear fade-in/out envelope and clip gain, then runs the full insert chain and
    /// accumulates into the master and reverb/delay send buffers, and into its extra returns' buffers from
    /// <paramref name="sendOffset"/> on (the block's part where the tracks play). Zero allocations. During a seek crossfade
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
        int sendOffset,
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
        var sends = Chain.BlockSends;
        for (int k = 0; k < sends.Length; k++)
        {
            var to = sends[k].To;
            if (to == null) continue;
            Chain.AddSend(n, sends[k].Level, sends[k].Pre, to.BufferL(sendOffset + n).Slice(sendOffset),
                to.BufferR(sendOffset + n).Slice(sendOffset));
        }
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
            // a transition plays the clip past its edges, into the audio its trims hide (MixRenderer does the same)
            var join = c.AudioJoins;
            long startFrame = Math.Max(0, (long)Math.Round((join != null ? join.From(w.Start) : w.Start) * sampleRate));
            long endFrame = (long)Math.Round((join != null ? join.Until(w.End) : w.End) * sampleRate);
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
                double g = join == null ? ClipEnvelope.GainAt(timeSec, w.Start, w.End, fades.FadeIn, fades.FadeOut)
                    : ClipEnvelope.GainAt(Math.Clamp(timeSec, w.Start, w.End), w.Start, w.End, fades.FadeIn, fades.FadeOut) * join.Gain(timeSec);
                float env = (float)g * cg * (k + dk * (i % ClipGainCurve.Step));
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
        Chain.Solo = ch.Solo || FolderSolo || ListenSolo;
        Chain.ReverbOn = ch.ReverbOn;
        Chain.ReverbSend = ch.ReverbSend;
        Chain.DelayOn = ch.DelayOn;
        Chain.DelaySend = ch.DelaySend;
        Chain.ReverbPre = ch.ReverbPre;
        Chain.DelayPre = ch.DelayPre;

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
