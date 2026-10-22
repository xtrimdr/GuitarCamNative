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
/// Also processes input 1-2 (<see cref="InputProcessor"/>: channel mode, +12 dB pre-amp gain, soft-limiter,
/// optional software monitoring), runs count-in + recording on the same sample clock via <see cref="TakeRecorder"/>,
/// and runs round-trip latency calibration (<see cref="CalibrationSession"/>).
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

    DelayParams lastDelay = DelayParams.Defaults;
    double bpm = 120;
    long position; // timeline frame (negative during recording count-in)
    double playStartSec;
    volatile bool playing;
    volatile bool recording;
    volatile bool loop;
    volatile bool softwareMonitor;
    volatile TakeRecorder? activeRecorder;
    volatile CalibrationSession? activeCalibration;
    int recCountInBeats;
    bool recUsedCountIn;
    bool recUsedClick;
    double? calibratedLatencyMs;
    int pendingTrackReset;
    int pendingBusReset;
    volatile Exception? lastRecordingError;
    long lastRecordingDroppedFrames;

    long endFrame;
    long nextBeat;
    float inL, inR, outL, outR;
    volatile bool inClip;

    public string Status { get; private set; } = "Audio apagado";
    public string? DriverName => io?.Driver;
    public bool IsOpen => io != null;
    public bool Playing => playing;
    public bool Recording => recording;
    public bool Calibrating => activeCalibration != null;
    public bool ClickOn { get; set; }

    /// <summary>Disk error encountered by the active or last recording session, if any.</summary>
    public Exception? RecordingDiskError => activeRecorder?.DiskError ?? lastRecordingError;

    /// <summary>Human-readable message for <see cref="RecordingDiskError"/>, or <c>null</c> if none.</summary>
    public string? RecordingError => RecordingDiskError?.Message;

    /// <summary>Samples dropped because the recording ring buffer overflowed (or could not be flushed on disk error).</summary>
    public long RecordingDroppedFrames => activeRecorder is { } rec ? rec.DroppedFrames : Interlocked.Read(ref lastRecordingDroppedFrames);

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
        set => inputProcessor.InputGainDb = value;
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

    public void Open(string? wantedDriver = null, int inputOffset = 0, int outputOffset = 0)
    {
        try
        {
            io?.Dispose();
            io = null;
            string driver = AsioDuplex.PickDriver(wantedDriver);
            io = new AsioDuplex(driver, SampleRate, inputOffset, 2, outputOffset, Process);
            io.Start();
            for (int i = 0; i < 100 && io.FramesPerBuffer == 0; i++) Thread.Sleep(10);
            double ms = 1000.0 * io.FramesPerBuffer / SampleRate;
            Status = $"{driver} · {SampleRate / 1000} kHz · {io.FramesPerBuffer} muestras ({ms:0.0} ms)";

            var stored = LatencyCalibration.ReadCalibration(driver, $"{inputOffset}:{outputOffset}");
            if (stored != null && !LatencyCalibration.IsCalibrationStale(stored, GetEstimatedRoundtripLatencyMs(), SampleRate))
                calibratedLatencyMs = stored.LatencyMs;
        }
        catch (Exception e)
        {
            io?.Dispose();
            io = null;
            Status = $"Sin audio: {e.Message}";
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
    /// Starts recording a take to <paramref name="wavPath"/> on the shared ASIO sample clock.
    /// When <paramref name="useCountIn"/> is true, plays <paramref name="countInBeats"/> beats of count-in
    /// before timeline frame 0; at frame 0, backing tracks, click, and disk capture start on the exact same sample.
    /// </summary>
    public void StartRecording(string wavPath, bool useCountIn = true, int countInBeats = 4)
    {
        if (io == null) throw new InvalidOperationException("Abrí primero la placa de audio.");
        if (recording || activeCalibration != null) return;

        playing = false;
        lastRecordingError = null;
        Interlocked.Exchange(ref lastRecordingDroppedFrames, 0);
        Interlocked.Exchange(ref pendingBusReset, 1);
        Interlocked.Exchange(ref pendingTrackReset, 1);

        int beats = useCountIn ? Math.Max(0, countInBeats) : 0;
        recCountInBeats = beats;
        recUsedCountIn = beats > 0;
        recUsedClick = ClickOn;

        double beatLen = SampleRate * 60.0 / bpm;
        long startFrame = beats > 0 ? -(long)Math.Round(beats * beatLen) : 0;
        nextBeat = -beats;
        click = null;
        clickPos = 0;

        var rec = new TakeRecorder(wavPath, SampleRate);
        activeRecorder = rec;
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
    /// Stops the active recording, flushes and closes the WAV file, and returns the take metadata
    /// (or <c>null</c> if <paramref name="discard"/> is true or the take lasted ≤ 0.3 s).
    /// </summary>
    public RecordedTakeResult? StopRecording(
        bool discard = false,
        double defaultOverdubOffsetMs = 0,
        bool isFirstTake = false)
    {
        if (!recording && activeRecorder == null) return null;
        var rec = Interlocked.Exchange(ref activeRecorder, null);
        recording = false;
        playing = false;
        click = null;
        Interlocked.Exchange(ref position, 0);
        if (rec == null) return null;

        double latencyMs = GetLatencyInfo().AppliedMs;
        var res = rec.Finish(
            discard,
            latencyMs,
            defaultOverdubOffsetMs,
            isFirstTake,
            recUsedCountIn,
            recUsedClick || ClickOn);
        lastRecordingError = rec.DiskError;
        Interlocked.Exchange(ref lastRecordingDroppedFrames, rec.DroppedFrames);
        return res;
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
        if (io == null) throw new InvalidOperationException("Abrí primero la placa de audio.");
        if (recording) throw new InvalidOperationException("No se puede calibrar mientras grabás.");

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
                    recUsedClick |= ClickOn;
                    activeRecorder?.WriteFromCallback(
                        procL.Slice(playOffset, playCount),
                        procR.Slice(playOffset, playCount));
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
                float l = ml[i];
                float r = mr[i];
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
            float ol = outL, or = outR;
            for (int i = 0; i < n; i++)
            {
                float l = output[0][i] + procL[i];
                float r = output[1][i] + procR[i];
                output[0][i] = l;
                output[1][i] = r;
                if (Math.Abs(l) > ol) ol = Math.Abs(l);
                if (Math.Abs(r) > or) or = Math.Abs(r);
            }
            outL = ol;
            outR = or;
        }

        UpdateLoad(t0, n);
    }

    void UpdateLoad(long t0, int n)
    {
        double used = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        double budget = n / (double)SampleRate;
        LoadPercent = LoadPercent * 0.9 + 0.1 * (100 * used / budget);
    }

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
