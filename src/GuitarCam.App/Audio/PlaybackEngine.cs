using System.Diagnostics;
using GuitarCam.App.Model;
using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App.Audio;

/// <summary>
/// Plays the timeline and records takes through ASIO on a single sample clock (port of
/// <c>StudioAudioEngine</c> in <c>audioEngine.ts</c> + <c>mixGraph.ts</c>):
/// per track: clip fades → saturator → compressor EQ → compressor → 6-band track EQ → pan → fader;
/// post-fader sends into the shared <see cref="ReverbBus"/> and <see cref="DelayBus"/>;
/// tracks + returns summed into <see cref="MasterLimiter"/>; click mixed outside the limiter (delayed by
/// <see cref="MasterLimiter.Latency"/> so it lands on the same sample as the limited tracks).
/// Also processes input 1-2 (<see cref="InputProcessor"/>: channel mode, pre-amp gain (0 dB default), soft-limiter,
/// optional software monitoring), runs count-in + recording on the same sample clock via <see cref="TakeRecorder"/>,
/// and runs round-trip latency calibration (<see cref="CalibrationSession"/>).
/// Native only: every driver input is open, and each armed track has an <see cref="InputTap"/> with its own
/// input; all of them record at once, one WAV each, starting on the same sample.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    public const int SampleRate = 48000;

    AsioDuplex? io;
    volatile LiveTrack[] tracks = [];
    readonly MixGraphBus buses = new(SampleRate);
    readonly InputProcessor inputProcessor = new();
    readonly float[] inProcL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] inProcR = new float[TrackDspChain.MaxBlockSize];
    readonly float[] mixL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] mixR = new float[TrackDspChain.MaxBlockSize];

    ClickSound clickSound = ClickSound.Madera;
    double clickVolumeDb;
    volatile float[] clickHi = ClickSynth.Render(ClickSound.Madera, true, SampleRate);
    volatile float[] clickLo = ClickSynth.Render(ClickSound.Madera, false, SampleRate);
    float[]? click;
    int clickPos;
    // a single click asked for by the sound menu; the callback takes it, so it also sounds while stopped
    float[]? previewClick;

    DelayParams lastDelay = DelayParams.Defaults;
    double bpm = 120;
    long position; // timeline frame (negative during recording count-in)
    double playStartSec;
    volatile bool playing;
    volatile bool recording;
    volatile bool loop;
    volatile bool softwareMonitor;
    volatile InputTap[] taps = [];
    volatile CalibrationSession? activeCalibration;
    int recCountInBeats;
    bool recUsedCountIn;
    bool recUsedClick;
    long recStartTimestamp;
    double? calibratedLatencyMs;
    int pendingTrackReset;
    int pendingBusReset;
    volatile Exception? lastRecordingError;
    long lastRecordingDroppedFrames;

    long endFrame;
    long nextBeat;
    float inL, inR, outL, outR;
    volatile bool inClip;

    public string Status { get; private set; } = Loc.T("engine.off");
    public string? DriverName => io?.Driver;
    public bool IsOpen => io != null;
    /// <summary>The driver's buffer size in samples (0 with no audio).</summary>
    public int BufferFrames => io?.FramesPerBuffer ?? 0;
    public int InputOffset { get; private set; }
    public int OutputOffset { get; private set; }
    /// <summary>Output channels of the open driver (the Configuración "Dispositivo" lists them in pairs).</summary>
    public int OutputChannels => io?.DriverOutputChannels ?? 0;
    /// <summary>The output side of the calibration key: each input + output pair keeps its own measurement.</summary>
    public string CalibrationOutputId => $"{InputOffset}:{OutputOffset}";
    public bool Playing => playing;
    public bool Recording => recording;
    /// <summary>
    /// Stopwatch time at which the take's first sample (timeline 0) was captured, 0 until then (still counting
    /// in). The camera's video counts its frames from it.
    /// </summary>
    public long RecordingStartTimestamp => Interlocked.Read(ref recStartTimestamp);
    public bool Calibrating => activeCalibration != null;
    public bool ClickOn { get; set; }

    /// <summary>Disk error encountered by the active or last recording session, if any.</summary>
    public Exception? RecordingDiskError
    {
        get
        {
            foreach (var t in taps)
                if (t.Recorder?.DiskError is { } e) return e;
            return lastRecordingError;
        }
    }

    /// <summary>Human-readable message for <see cref="RecordingDiskError"/>, or <c>null</c> if none.</summary>
    public string? RecordingError => RecordingDiskError?.Message;

    /// <summary>Samples dropped because the recording ring buffer overflowed (or could not be flushed on disk error).</summary>
    public long RecordingDroppedFrames => recording ? taps.Sum(t => t.Recorder?.DroppedFrames ?? 0) : Interlocked.Read(ref lastRecordingDroppedFrames);

    /// <summary>Driver inputs the stream opened (all of them), for the armed tracks' input selector.</summary>
    public int InputChannels => io?.InputChannels ?? 0;

    /// <summary>The armed tracks' inputs, in the order <see cref="SetInputs"/> got them (meters read their peaks).</summary>
    public IReadOnlyList<InputTap> Taps => taps;

    public bool Loop
    {
        get => loop;
        set => loop = value;
    }

    public bool SoftwareMonitor
    {
        get => softwareMonitor;
        set => softwareMonitor = value;
    }

    public InputProcessor Input => inputProcessor;

    public InputChannelMode InputChannelMode
    {
        get => inputProcessor.ChannelMode;
        set => inputProcessor.ChannelMode = value;
    }

    public double InputGainDb
    {
        get => inputProcessor.InputGainDb;
        set
        {
            inputProcessor.InputGainDb = value;
            foreach (var t in taps) t.GainDb = value;
        }
    }

    public ClickSound ClickSound
    {
        get => clickSound;
        set => SetClickSound(value, clickVolumeDb);
    }

    public double ClickVolumeDb
    {
        get => clickVolumeDb;
        set => SetClickSound(clickSound, value);
    }

    public double Bpm
    {
        get => bpm;
        set
        {
            bpm = Math.Clamp(value, 20, 300);
            buses.Delay.Set(lastDelay, bpm, glide: true);
        }
    }

    public MixGraphBus Buses => buses;
    public ReverbBus Reverb => buses.Reverb;
    public DelayBus Delay => buses.Delay;
    public MasterLimiter Limiter => buses.Limiter;

    public double LoadPercent { get; private set; }

    /// <summary>Current timeline position in seconds (clamped to &gt;= 0 during count-in, like <c>App.tsx:1068</c>).</summary>
    public double PositionSec => Math.Max(0.0, Interlocked.Read(ref position) / (double)SampleRate);

    /// <summary>During a recording count-in, returns the 1-based beat index (1..countInBeats); otherwise <c>null</c>.</summary>
    public int? CountdownBeat
    {
        get
        {
            if (!recording) return null;
            long pos = Interlocked.Read(ref position);
            if (pos >= 0 || recCountInBeats <= 0) return null;
            double beatLen = SampleRate * 60.0 / bpm;
            long startFrame = -(long)Math.Round(recCountInBeats * beatLen);
            int beat = (int)((pos - startFrame) / beatLen) + 1;
            return Math.Clamp(beat, 1, recCountInBeats);
        }
    }

    public bool IsCountingIn => CountdownBeat != null;

    /// <summary>The open driver asked to be reopened (new buffer size from its panel). Raised on the driver's thread.</summary>
    public event Action? DriverResetRequested;

    /// <summary>Opens the driver's control panel; with no driver open, the one <see cref="Open"/> would pick.</summary>
    public void ShowDriverPanel()
    {
        if (io != null) io.ShowControlPanel();
        else AsioDuplex.ShowControlPanel(AsioDuplex.PickDriver(null));
    }

    public void Open(string? wantedDriver = null, int inputOffset = 0, int outputOffset = 0)
    {
        try
        {
            io?.Dispose();
            io = null;
            string driver = AsioDuplex.PickDriver(wantedDriver);
            io = new AsioDuplex(driver, SampleRate, inputOffset, 0, outputOffset, Process);
            io.ResetRequested += () => DriverResetRequested?.Invoke();
            InputOffset = inputOffset;
            OutputOffset = outputOffset;
            io.Start();
            for (int i = 0; i < 100 && io.FramesPerBuffer == 0; i++) Thread.Sleep(10);
            double ms = 1000.0 * io.FramesPerBuffer / SampleRate;
            Status = Loc.T("engine.status", driver, SampleRate / 1000, io.FramesPerBuffer, $"{ms:0.0}");

            // like the web, a saved measurement applies even when stale (Configuración warns about it);
            // a pair with nothing saved goes back to the driver's estimate
            calibratedLatencyMs = LatencyCalibration.ReadCalibration(driver, CalibrationOutputId)?.LatencyMs;
        }
        catch (Exception e)
        {
            io?.Dispose();
            io = null;
            Status = Loc.T("engine.noAudio", e.Message);
        }
    }

    public void SetTracks(IEnumerable<LiveTrack> list, ProjectSettings? settings = null)
    {
        playing = false;
        var arr = list.ToArray();
        tracks = arr;
        if (settings != null) SetSettings(settings, glideDelay: false);
        Interlocked.Exchange(ref pendingTrackReset, 1);
        double maxEnd = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i].EndSec > maxEnd) maxEnd = arr[i].EndSec;
        }
        Interlocked.Exchange(ref endFrame, (long)(maxEnd * SampleRate));
        Interlocked.Exchange(ref position, 0);
        playStartSec = 0;
    }

    /// <summary>
    /// Swaps in the track list after a timeline edit while it keeps playing: no stop, the playhead stays and
    /// neither the tracks' chains nor the buses are reset (reused tracks keep their compressor and tails).
    /// </summary>
    public void UpdateTracks(IReadOnlyList<LiveTrack> list)
    {
        tracks = list.ToArray();
        RefreshClipTiming();
    }

    /// <summary>
    /// Recomputes <see cref="LengthSec"/> (<c>endFrame</c>) from the current <see cref="LiveTrack"/> list
    /// without stopping playback, moving the playhead or resetting DSP chains/buses.
    /// </summary>
    public void RefreshClipTiming()
    {
        var arr = tracks;
        double maxEnd = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i].EndSec > maxEnd) maxEnd = arr[i].EndSec;
        }
        Interlocked.Exchange(ref endFrame, (long)(maxEnd * SampleRate));
    }

    public void SetSettings(ProjectSettings s, bool glideDelay = false)
    {
        bpm = Math.Clamp(s.Bpm, 20, 300);
        ApplyReverb(s.Reverb);
        ApplyDelay(s.Delay, glideDelay);
        var arr = tracks;
        for (int i = 0; i < arr.Length; i++)
            arr[i].SetProjectSettings(s);
    }

    public void ApplyReverb(ReverbSettings r)
    {
        buses.Reverb.SetLive(new ReverbParams(
            r.Decay,
            r.PreDelayMs,
            r.Tone,
            r.LowCut,
            r.Width,
            r.ReturnGain,
            r.Muted));
    }

    public void ApplyDelay(DelaySettings d, bool glide = true)
    {
        lastDelay = new DelayParams(
            DelayParams.ParseMode(d.Mode),
            d.Sync,
            d.Division,
            d.TimeMs,
            d.Feedback,
            d.Tone,
            d.LowCut,
            d.Width,
            d.Duck,
            d.ReturnGain,
            d.Muted);
        buses.Delay.Set(lastDelay, bpm, glide);
    }

    public double LengthSec => endFrame / (double)SampleRate;
    public double PlayStartSec => playStartSec;

    public void Play()
    {
        if (recording || activeCalibration != null) return;
        if (endFrame > 0 && Interlocked.Read(ref position) >= endFrame) Seek(0);
        long pos = Interlocked.Read(ref position);
        playStartSec = Math.Max(0.0, pos / (double)SampleRate);
        double beatLen = SampleRate * 60.0 / bpm;
        nextBeat = (long)Math.Ceiling(pos / beatLen - 1e-6);
        Interlocked.Exchange(ref pendingTrackReset, 1);
        playing = true;
    }

    /// <summary>Stops playback keeping the current playhead position (pause behavior).</summary>
    public void Stop() => playing = false;

    /// <summary>Pauses playback at the current playhead position.</summary>
    public void Pause() => playing = false;

    /// <summary>
    /// Port of <c>stopTransport</c> (<c>App.tsx:1740</c>): if playing, stops and returns to where playback
    /// started; if already stopped, rewinds to 0.
    /// </summary>
    public void StopTransport()
    {
        if (recording) return;
        bool wasPlaying = playing;
        playing = false;
        Seek(wasPlaying ? playStartSec : 0);
    }

    public void Seek(double sec)
    {
        if (recording) return;
        double maxSec = LengthSec > 0 ? Math.Max(LengthSec, sec) : Math.Max(0, sec);
        long frame = (long)(Math.Clamp(sec, 0, maxSec) * SampleRate);
        Interlocked.Exchange(ref position, frame);
        playStartSec = frame / (double)SampleRate;
        double beatLen = SampleRate * 60.0 / bpm;
        nextBeat = (long)Math.Ceiling(frame / beatLen - 1e-6);
        Interlocked.Exchange(ref pendingTrackReset, 1);
    }

    // --- Recording (§5.8) ------------------------------------------------------------------------

    /// <summary>
    /// The armed tracks' inputs (null = the main input): metered and, with software monitoring, heard, each
    /// input once. A tap with the same route as before is kept. Ignored while recording.
    /// </summary>
    public void SetInputs(IReadOnlyList<InputRoute?> routes)
    {
        if (recording) return;
        var old = taps;
        var next = new InputTap[routes.Count];
        for (int i = 0; i < routes.Count; i++)
        {
            bool first = true;
            for (int k = 0; k < i; k++) first &= routes[k] != routes[i];
            var keep = Array.Find(old, t => t.Route == routes[i] && t.Monitored == first && Array.IndexOf(next, t) < 0);
            next[i] = keep ?? new InputTap(routes[i], InputGainDb) { Monitored = first };
        }
        taps = next;
    }

    /// <summary>
    /// Starts recording one take per input on the shared ASIO sample clock, each to its WAV.
    /// When <paramref name="useCountIn"/> is true, plays <paramref name="countInBeats"/> beats of count-in
    /// before timeline frame 0; at frame 0, backing tracks, click, and disk capture start on the exact same sample.
    /// </summary>
    public void StartRecording(IReadOnlyList<(InputRoute? Route, string WavPath)> takes, bool useCountIn = true, int countInBeats = 4)
    {
        if (io == null) throw new InvalidOperationException(Loc.T("engine.openFirst"));
        if (recording || activeCalibration != null) return;
        if (takes.Count == 0) throw new ArgumentException(Loc.T("engine.nothingToRecord"), nameof(takes));

        SetInputs(takes.Select(t => t.Route).ToList());
        var recorders = new List<TakeRecorder>();
        try
        {
            foreach (var t in takes) recorders.Add(new TakeRecorder(t.WavPath, SampleRate));
        }
        catch
        {
            foreach (var r in recorders) r.Finish(discard: true);
            throw;
        }

        playing = false;
        lastRecordingError = null;
        Interlocked.Exchange(ref lastRecordingDroppedFrames, 0);
        Interlocked.Exchange(ref pendingBusReset, 1);
        Interlocked.Exchange(ref pendingTrackReset, 1);

        int beats = useCountIn ? Math.Max(0, countInBeats) : 0;
        recCountInBeats = beats;
        recUsedCountIn = beats > 0;
        recUsedClick = ClickOn;
        Interlocked.Exchange(ref recStartTimestamp, 0);

        double beatLen = SampleRate * 60.0 / bpm;
        long startFrame = beats > 0 ? -(long)Math.Round(beats * beatLen) : 0;
        nextBeat = -beats;
        click = null;
        clickPos = 0;

        var cur = taps;
        for (int i = 0; i < cur.Length; i++) cur[i].Recorder = recorders[i];
        Interlocked.Exchange(ref position, startFrame);
        playStartSec = 0;
        recording = true;
        playing = true;
    }

    /// <summary>Cancels the recording while still in the count-in (<c>Esc</c> in <c>App.tsx</c>).</summary>
    public bool CancelCountIn()
    {
        if (!recording || !IsCountingIn) return false;
        StopRecording(discard: true);
        return true;
    }

    /// <summary>
    /// Stops the active recording, flushes and closes the WAV files, and returns each take's metadata in the
    /// order they started (null for one discarded or of 0.3 s or less); empty when nothing was recording.
    /// </summary>
    public RecordedTakeResult?[] StopRecording(
        bool discard = false,
        double defaultOverdubOffsetMs = 0,
        bool isFirstTake = false)
    {
        var cur = taps;
        var recs = cur.Select(t => t.Recorder).ToArray();
        if (!recording && recs.All(r => r == null)) return [];
        recording = false;
        playing = false;
        click = null;
        Interlocked.Exchange(ref position, 0);
        foreach (var t in cur) t.Recorder = null;

        double latencyMs = GetLatencyInfo().AppliedMs;
        var results = new RecordedTakeResult?[recs.Length];
        Exception? error = null;
        long dropped = 0;
        for (int i = 0; i < recs.Length; i++)
        {
            if (recs[i] is not { } rec) continue;
            results[i] = rec.Finish(
                discard,
                latencyMs,
                defaultOverdubOffsetMs,
                isFirstTake,
                recUsedCountIn,
                recUsedClick || ClickOn);
            error ??= rec.DiskError;
            dropped += rec.DroppedFrames;
        }
        lastRecordingError = error;
        Interlocked.Exchange(ref lastRecordingDroppedFrames, dropped);
        return results;
    }

    // --- Latency & Calibration (§5.9) ------------------------------------------------------------

    public LatencyInfo GetLatencyInfo()
    {
        int outSamples = io != null ? (io.ReportedOutputLatency > 0 ? io.ReportedOutputLatency : io.FramesPerBuffer) : (int)(0.012 * SampleRate);
        int inSamples = io != null ? Math.Max(io.FramesPerBuffer, 64) : (int)(0.010 * SampleRate);
        double outputMs = Math.Round(((outSamples + buses.Limiter.Latency) / (double)SampleRate) * 1000.0, 1);
        double inputMs = Math.Round((inSamples / (double)SampleRate) * 1000.0, 1);
        double totalMs = Math.Round(outputMs + inputMs, 1);
        double applied = calibratedLatencyMs ?? totalMs;
        return new LatencyInfo(
            outputMs,
            inputMs,
            totalMs,
            SampleRate,
            SampleRate,
            calibratedLatencyMs,
            applied);
    }

    public double GetEstimatedRoundtripLatencyMs() => GetLatencyInfo().TotalMs;

    public void SetCalibratedLatency(double? ms) => calibratedLatencyMs = ms;

    /// <summary>
    /// Measures the real round-trip latency on the open ASIO device (<c>loopback</c> or <c>tap</c>).
    /// Port of <c>StudioAudioEngine.measureLatency</c> (<c>audioEngine.ts:522-606</c>).
    /// </summary>
    public async Task<CalibrationResult?> MeasureLatencyAsync(
        CalibrationMethod method,
        Action<CalibrationProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        if (io == null) throw new InvalidOperationException(Loc.T("engine.openFirst"));
        if (recording) throw new InvalidOperationException(Loc.T("engine.cantCalibrate"));

        playing = false;
        var session = new CalibrationSession(method, SampleRate, buses.Limiter.Latency, clickSound);
        activeCalibration = session;
        try
        {
            while (!session.IsFinished)
            {
                if (ct.IsCancellationRequested) return null;
                if (session.GetProgress() is { } p) onProgress?.Invoke(p);
                await Task.Delay(30, ct).ConfigureAwait(false);
            }
            return session.Analyze();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            activeCalibration = null;
        }
    }

    // --- Meters & Callback -----------------------------------------------------------------------

    /// <summary>Peak levels since the last call (input 1-2 after pre-amp gain + soft limit, master out).</summary>
    public (float InL, float InR, float OutL, float OutR) TakeMeters()
    {
        var m = (inL, inR, outL, outR);
        inL = inR = outL = outR = 0;
        return m;
    }

    public (float Left, float Right, bool Clip) TakeInputPeaks()
    {
        var res = (inL, inR, inClip);
        inL = inR = 0;
        inClip = false;
        return res;
    }

    /// <summary>Peak levels on the reverb and delay return buses since the last call.</summary>
    public (float RevL, float RevR, float DlyL, float DlyR) TakeReturnMeters() => buses.TakeReturnMeters();

    void Process(float[][] input, float[][] output, int n)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (n <= 0 || n > mixL.Length) return;

        var ch0 = input.Length > 0 ? input[0].AsSpan(0, n) : Span<float>.Empty;
        var ch1 = input.Length > 1 ? input[1].AsSpan(0, n) : ch0;
        var procL = inProcL.AsSpan(0, n);
        var procR = inProcR.AsSpan(0, n);

        if (ch0.Length == n)
        {
            inputProcessor.Process(ch0, ch1, procL, procR, out float maxL, out float maxR, out bool clip);
            if (maxL > inL) inL = maxL;
            if (maxR > inR) inR = maxR;
            if (clip) inClip = true;
        }
        else
        {
            procL.Clear();
            procR.Clear();
        }
        var curTaps = taps;
        for (int i = 0; i < curTaps.Length; i++) curTaps[i].Process(input, n, inProcL, inProcR);

        var cal = activeCalibration;
        if (cal != null)
        {
            cal.ProcessBlock(procL, procR, output[0].AsSpan(0, n), output[1].AsSpan(0, n), n);
            UpdateLoad(t0, n);
            return;
        }

        if (Interlocked.Exchange(ref pendingBusReset, 0) != 0)
            buses.Reset();

        if (Interlocked.Exchange(ref pendingTrackReset, 0) != 0)
        {
            var curTracks = tracks;
            for (int i = 0; i < curTracks.Length; i++) curTracks[i].Chain.Reset();
        }

        var ml = mixL.AsSpan(0, n);
        var mr = mixR.AsSpan(0, n);
        ml.Clear();
        mr.Clear();
        var rl = buses.PrepareReverbL(n);
        var rr = buses.PrepareReverbR(n);
        var dl = buses.PrepareDelayL(n);
        var dr = buses.PrepareDelayR(n);

        var preview = Interlocked.Exchange(ref previewClick, null);
        if (preview != null)
        {
            click = preview;
            clickPos = 0;
        }

        if (playing)
        {
            long pos = Interlocked.Read(ref position);
            bool isRec = recording;

            // During recording count-in (pos < 0), backing tracks and disk recording start only once f >= 0.
            int playOffset = pos < 0 ? (int)Math.Min(n, -pos) : 0;
            int playCount = n - playOffset;

            if (playCount > 0)
            {
                long trackPos = Math.Max(0, pos + playOffset);
                var subMl = ml.Slice(playOffset, playCount);
                var subMr = mr.Slice(playOffset, playCount);
                var subRl = rl.Slice(playOffset, playCount);
                var subRr = rr.Slice(playOffset, playCount);
                var subDl = dl.Slice(playOffset, playCount);
                var subDr = dr.Slice(playOffset, playCount);

                var list = tracks;
                bool anySolo = false;
                for (int i = 0; i < list.Length; i++)
                {
                    if (list[i].Channel.Solo) { anySolo = true; break; }
                }

                for (int i = 0; i < list.Length; i++)
                    list[i].MixInto(trackPos, playCount, anySolo, subMl, subMr, subRl, subRr, subDl, subDr);

                if (isRec)
                {
                    // the block's samples were captured over the n/SR seconds before this callback
                    if (Interlocked.Read(ref recStartTimestamp) == 0)
                        Interlocked.Exchange(ref recStartTimestamp, Math.Max(1, t0 - (long)((n - playOffset) * (double)Stopwatch.Frequency / SampleRate)));
                    recUsedClick |= ClickOn;
                    for (int i = 0; i < curTaps.Length; i++)
                        curTaps[i].Recorder?.WriteFromCallback(
                            curTaps[i].BlockL.Slice(playOffset, playCount),
                            curTaps[i].BlockR.Slice(playOffset, playCount));
                }
            }

            buses.ProcessReturnsAndLimiter(ml, mr, rl, rr, dl, dr);

            double beatLen = SampleRate * 60.0 / bpm;
            int limLatency = buses.Limiter.Latency;
            var hi = clickHi;
            var lo = clickLo;
            float ol = outL, or = outR;

            for (int i = 0; i < n; i++)
            {
                long f = pos + i;
                bool inCountIn = isRec && f < 0;
                bool wantClick = inCountIn ? recUsedCountIn : ClickOn;
                long beatFrame = (long)Math.Round(nextBeat * beatLen) + limLatency;

                if (f >= beatFrame)
                {
                    if (wantClick)
                    {
                        bool accent = inCountIn ? (nextBeat == -recCountInBeats) : (nextBeat % 4 == 0);
                        click = accent ? hi : lo;
                        clickPos = 0;
                    }
                    nextBeat++;
                }

                float c = 0;
                if (click != null)
                {
                    c = click[clickPos];
                    if (++clickPos >= click.Length) click = null;
                }

                output[0][i] = ml[i] + c;
                output[1][i] = mr[i] + c;
                if (Math.Abs(output[0][i]) > ol) ol = Math.Abs(output[0][i]);
                if (Math.Abs(output[1][i]) > or) or = Math.Abs(output[1][i]);
            }
            outL = ol;
            outR = or;

            pos += n;
            if (!isRec && endFrame > 0 && pos >= endFrame)
            {
                if (loop)
                {
                    pos = 0;
                    nextBeat = 0;
                    Interlocked.Exchange(ref position, 0);
                    var curTracks = tracks;
                    for (int i = 0; i < curTracks.Length; i++) curTracks[i].Chain.Reset();
                }
                else
                {
                    Interlocked.Exchange(ref position, pos);
                    playing = false;
                }
            }
            else
            {
                Interlocked.Exchange(ref position, pos);
            }
        }
        else
        {
            // Transport stopped: keep processing the shared return buses with silent input so reverb
            // and delay tails decay naturally in real time (matching MixBus in the web app).
            buses.ProcessReturnsAndLimiter(ml, mr, rl, rr, dl, dr);
            float ol = outL, or = outR;
            for (int i = 0; i < n; i++)
            {
                float c = 0;
                if (click != null)
                {
                    c = click[clickPos];
                    if (++clickPos >= click.Length) click = null;
                }
                float l = ml[i] + c;
                float r = mr[i] + c;
                output[0][i] = l;
                output[1][i] = r;
                if (Math.Abs(l) > ol) ol = Math.Abs(l);
                if (Math.Abs(r) > or) or = Math.Abs(r);
            }
            outL = ol;
            outR = or;
        }

        if (softwareMonitor)
        {
            // the armed tracks' inputs, each once; with none armed, input 1-2
            if (curTaps.Length == 0) Monitor(output, procL, procR, n);
            for (int k = 0; k < curTaps.Length; k++)
                if (curTaps[k].Monitored) Monitor(output, curTaps[k].BlockL, curTaps[k].BlockR, n);
        }

        UpdateLoad(t0, n);
    }

    void Monitor(float[][] output, ReadOnlySpan<float> l, ReadOnlySpan<float> r, int n)
    {
        float ol = outL, or = outR;
        for (int i = 0; i < n; i++)
        {
            float a = output[0][i] + l[i];
            float b = output[1][i] + r[i];
            output[0][i] = a;
            output[1][i] = b;
            if (Math.Abs(a) > ol) ol = Math.Abs(a);
            if (Math.Abs(b) > or) or = Math.Abs(b);
        }
        outL = ol;
        outR = or;
    }

    void UpdateLoad(long t0, int n)
    {
        double used = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        double budget = n / (double)SampleRate;
        LoadPercent = LoadPercent * 0.9 + 0.1 * (100 * used / budget);
    }

    /// <summary>Plays one click of the current sound and volume, playing or not (the web's playClick from the sound menu).</summary>
    public void PreviewClick(bool accent = true) => Volatile.Write(ref previewClick, accent ? clickHi : clickLo);

    public void SetClickSound(ClickSound s, double volumeDb = 0)
    {
        clickSound = s;
        clickVolumeDb = volumeDb;
        clickHi = ClickSynth.Render(s, true, SampleRate, volumeDb);
        clickLo = ClickSynth.Render(s, false, SampleRate, volumeDb);
    }

    public void Dispose()
    {
        StopRecording(discard: false);
        io?.Dispose();
        io = null;
    }
}
