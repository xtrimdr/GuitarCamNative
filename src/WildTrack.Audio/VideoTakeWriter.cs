using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace WildTrack.Audio;

/// <summary>What a recorded video ended up as.</summary>
public sealed record VideoTakeResult(string Path, double DurationSec, long Frames, long Duplicated, long Dropped, string? Error);

/// <summary>
/// Records the camera next to a take: port of the web's MediaRecorder setup (handleStartRecordingSequence in
/// App.tsx): H.264 in MP4, 8 Mbps, a keyframe every second so scrubbing doesn't stall. FFmpeg encodes from a
/// pipe at a constant 30 fps; each camera frame lands on the slot its arrival time says, counted from the
/// moment the audio take started (<c>startAt</c>, a Stopwatch timestamp, 0 while counting in), so a camera
/// that runs at 29.97 fps or hiccups never drifts from the audio: missing slots repeat the last frame and
/// extra frames are dropped. The file is fragmented MP4 while recording, so if the program dies FFmpeg
/// still closes it on the pipe's end (and even a killed FFmpeg leaves every finished second readable);
/// a finished take is then rewritten as a regular MP4 with the index up front.
/// </summary>
public sealed class VideoTakeWriter : IDisposable
{
    public const double Fps = 30;
    public const int BitsPerSecond = 8_000_000;
    /// <summary>Frames that can wait for the encoder (about a second) before new ones are dropped.</summary>
    const int QueueFrames = 30;

    readonly Process proc;
    readonly Stream stdin;
    readonly Func<long> startAt;
    readonly int frameBytes;
    readonly BlockingCollection<(byte[] Buf, long At)> queue = new(QueueFrames);
    readonly ConcurrentBag<byte[]> pool = [];
    readonly Thread thread;
    readonly List<string> log = [];
    readonly byte[] last;
    bool haveLast;
    long written, duplicated, dropped;
    volatile string? error;
    int done;
    long stopAt = long.MaxValue; // Stopwatch time of the stop: later frames don't count
    long limit = long.MaxValue; // slots in the take, once its length is known

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public long Frames => Interlocked.Read(ref written);
    public string? Error => error;

    VideoTakeWriter(string path, int width, int height, Func<long> startAt, Process proc)
    {
        Path = path;
        Width = width;
        Height = height;
        this.startAt = startAt;
        this.proc = proc;
        stdin = proc.StandardInput.BaseStream;
        frameBytes = width * height * 3 / 2;
        last = new byte[frameBytes];
        for (int i = 0; i < QueueFrames; i++) pool.Add(new byte[frameBytes]);
        thread = new Thread(Run) { IsBackground = true, Name = "video take" };
        thread.Start();
    }

    /// <summary>
    /// Starts FFmpeg writing <paramref name="path"/> for I420 frames of the given size. Frames count from
    /// <paramref name="startAt"/>() once it returns a non-zero Stopwatch timestamp.
    /// </summary>
    public static VideoTakeWriter Start(string path, int width, int height, Func<long> startAt)
    {
        var inv = CultureInfo.InvariantCulture;
        string[] args =
        [
            "-hide_banner", "-nostats", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "yuv420p", "-video_size", $"{width}x{height}", "-framerate", Fps.ToString(inv), "-i", "-",
            "-an", "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-b:v", BitsPerSecond.ToString(inv), "-maxrate", BitsPerSecond.ToString(inv), "-bufsize", (2 * BitsPerSecond).ToString(inv),
            "-g", "30", "-keyint_min", "30", "-sc_threshold", "0",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", path,
        ];
        var psi = new ProcessStartInfo("ffmpeg", args) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("No pude arrancar FFmpeg para grabar el video.");
        var w = new VideoTakeWriter(path, width, height, startAt, p);
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } s) lock (w.log) w.log.Add(s); };
        p.BeginErrorReadLine();
        return w;
    }

    /// <summary>Hands a camera frame over (copied: the caller reuses its buffer). Never blocks.</summary>
    public void Offer(byte[] frame, long at)
    {
        if (Volatile.Read(ref done) != 0) return;
        if (!pool.TryTake(out var buf))
        {
            Interlocked.Increment(ref dropped); // the encoder fell behind
            return;
        }
        Buffer.BlockCopy(frame, 0, buf, 0, frameBytes);
        try
        {
            if (queue.TryAdd((buf, at))) return;
        }
        catch (InvalidOperationException)
        {
            // finished meanwhile
        }
        pool.Add(buf);
    }

    void Run()
    {
        try
        {
            foreach (var (buf, at) in queue.GetConsumingEnumerable())
            {
                Place(buf, at);
                pool.Add(buf);
            }
        }
        catch (InvalidOperationException)
        {
            // completed while waiting
        }
    }

    void Place(byte[] buf, long at)
    {
        long start = startAt();
        long idx = start == 0 ? -1 : (long)Math.Round((at - start) * Fps / Stopwatch.Frequency);
        if (at > Interlocked.Read(ref stopAt) || idx >= Interlocked.Read(ref limit))
        {
            Interlocked.Increment(ref dropped);
            return;
        }
        if (idx >= 0)
        {
            var fill = haveLast ? last : buf;
            while (written < idx) Write(fill, true);
            if (written == idx) Write(buf, false);
            else Interlocked.Increment(ref dropped);
        }
        Buffer.BlockCopy(buf, 0, last, 0, frameBytes);
        haveLast = true;
    }

    void Write(byte[] buf, bool duplicate)
    {
        if (error == null)
        {
            try
            {
                stdin.Write(buf, 0, frameBytes);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                error = LastLog() ?? e.Message;
            }
        }
        Interlocked.Increment(ref written);
        if (duplicate) Interlocked.Increment(ref duplicated);
    }

    string? LastLog()
    {
        lock (log) return log.Count > 0 ? log[^1] : null;
    }

    /// <summary>Frames that arrive from now on are past the end of the take (call it as the take stops).</summary>
    public void StopTaking() => Interlocked.CompareExchange(ref stopAt, Stopwatch.GetTimestamp(), long.MaxValue);

    bool Close()
    {
        if (Interlocked.Exchange(ref done, 1) != 0) return false;
        queue.CompleteAdding();
        thread.Join();
        return true;
    }

    /// <summary>
    /// Stops taking frames, repeats the last one up to <paramref name="durationSec"/> (the audio's length) and
    /// closes the file. Null when no frame was recorded (the file is removed: the take stays audio only).
    /// </summary>
    public VideoTakeResult? Finish(double? durationSec)
    {
        StopTaking();
        if (durationSec is { } len) Interlocked.Exchange(ref limit, (long)Math.Round(len * Fps));
        if (!Close()) return null;
        if (durationSec is { } d && haveLast)
        {
            long target = (long)Math.Round(d * Fps);
            while (written < target) Write(last, true);
        }
        try
        {
            stdin.Close();
        }
        catch (IOException)
        {
            // FFmpeg already gone; its log says why
        }
        if (!proc.WaitForExit(30_000)) Kill();
        proc.WaitForExit();
        if (proc.ExitCode != 0 && error == null) error = LastLog() ?? Texts.T("err.ffmpeg.exit", "FFmpeg terminó con el código {0}", proc.ExitCode);
        if (written == 0 || !File.Exists(Path))
        {
            TryDelete(Path);
            return null;
        }
        Faststart(Path);
        return new VideoTakeResult(Path, written / Fps, written, duplicated, Interlocked.Read(ref dropped), error);
    }

    /// <summary>Stops and removes the file: to the Recycle Bin for "Parar sin guardar", deleted for a take too short to keep.</summary>
    public void Discard(bool toRecycleBin = false)
    {
        if (!Close()) return;
        Kill();
        proc.WaitForExit(5000);
        if (toRecycleBin) TakeRecorder.TryRecycleFile(Path);
        else TryDelete(Path);
    }

    /// <summary>
    /// Rewrites a fragmented MP4 as a regular one with the index first (fast to open and seek), next to it
    /// and then over it. If that fails the fragmented file stays, which plays fine.
    /// </summary>
    public static bool Faststart(string path)
    {
        string tmp = path + ".tmp";
        var psi = new ProcessStartInfo("ffmpeg", ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", path, "-c", "copy", "-movflags", "+faststart", "-f", "mp4", tmp])
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                TryDelete(tmp);
                return false;
            }
            File.Move(tmp, path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TryDelete(tmp);
            return false;
        }
    }

    void Kill()
    {
        try
        {
            if (!proc.HasExited) proc.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left behind; nothing points at it
        }
    }

    public void Dispose()
    {
        if (Close())
        {
            try { stdin.Close(); } catch (IOException) { }
            proc.WaitForExit(30_000);
        }
        proc.Dispose();
    }
}
