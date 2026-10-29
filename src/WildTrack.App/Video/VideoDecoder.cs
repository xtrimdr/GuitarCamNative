using WildTrack.Audio;

namespace WildTrack.App.Video;

/// <summary>
/// Gives the preview the frame of one video file at the time the UI asks for. A background thread reads from an
/// <see cref="IVideoFrameSource"/> (in-process FFmpeg, <see cref="NativeVideoSource"/>, or ffmpeg.exe through a pipe
/// when that can't be used) and stays a few frames ahead; jumping back, or more than a second ahead, seeks the
/// source. A decoder nobody asks for (<see cref="Idle"/>) closes its source after <see cref="IdleCloseMs"/>: every
/// cut is a clip with its own decoder, and an open one holds the file and memory on the video card.
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    public const double Fps = 30;
    const double Lookahead = 3 / Fps;
    /// <summary>A frame further than this from the wanted time is from before a jump: <see cref="TryCopyLatest"/> skips it.</summary>
    const double StaleSec = 1.0;
    /// <summary>How long a decoder goes unasked before it closes its source (it opens again in a few ms when asked).</summary>
    const long IdleCloseMs = 3000;

    readonly Func<IVideoFrameSource?> reopen;
    readonly Thread thread;
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    volatile bool disposed;

    double wanted = -1; // video time the UI wants (s), < 0 = idle
    long idleSince = Environment.TickCount64;

    // decoder thread only
    IVideoFrameSource? source;
    bool positioned; // the source was sought since it was opened
    double nextTime; // about when the frame the source gives next starts
    bool ended;

    // latest: the frame to show; pending: read ahead, not due yet; spare: free buffer
    byte[] latest, pending, spare;
    double latestTime = double.NaN, pendingTime = double.NaN;
    long version;

    /// <summary>Seeks across every decoder, for <see cref="Audio.AudioGapLog"/>.</summary>
    public static int Restarts;

    public int Width { get; }
    public int Height { get; }
    /// <summary>True when FFmpeg decodes inside the process; false on the ffmpeg.exe fallback.</summary>
    public bool Native { get; }

    VideoDecoder(IVideoFrameSource first, Func<IVideoFrameSource?> reopen, string path)
    {
        source = first;
        this.reopen = reopen;
        Native = first is NativeVideoSource;
        Width = first.Width;
        Height = first.Height;
        int frameBytes = Width * Height * 4;
        latest = new byte[frameBytes];
        pending = new byte[frameBytes];
        spare = new byte[frameBytes];
        thread = new Thread(Run) { IsBackground = true, Name = $"video {Path.GetFileName(path)}" };
        thread.Start();
    }

    /// <summary>Opens a decoder that outputs at most <paramref name="maxWidth"/> pixels wide; null if the file has no readable picture.</summary>
    public static VideoDecoder? Open(string path, int maxWidth = 640)
    {
        if (NativeVideoSource.Open(path, maxWidth) is { } native)
            return new VideoDecoder(native, () => NativeVideoSource.Open(path, maxWidth), path);
        if (PipeVideoSource.Open(path, maxWidth) is { } pipe)
            return new VideoDecoder(pipe, () => PipeVideoSource.Open(path, maxWidth), path);
        return null;
    }

    /// <summary>
    /// Asks for the frame at <paramref name="sec"/> (video time). Cheap: call it every UI tick. A frame read ahead
    /// that is already due is shown right away, so a clip that was pre-rolled (<see cref="Model.LiveClip"/>) has its
    /// first picture on the same tick it starts, instead of waiting for the decoder thread.
    /// </summary>
    public void Want(double sec)
    {
        lock (gate)
        {
            wanted = sec;
            PromoteDue(sec);
        }
        wake.Set();
    }

    /// <summary>Nothing is wanted from it for now (the playhead is away from its clip): it stops reading, and later closes its source.</summary>
    public void Idle()
    {
        lock (gate)
        {
            if (wanted < 0) return;
            wanted = -1;
            idleSince = Environment.TickCount64;
        }
    }

    /// <summary>Makes the frame read ahead current if it's due. Called with <see cref="gate"/> held.</summary>
    bool PromoteDue(double w)
    {
        if (double.IsNaN(pendingTime) || pendingTime > w + 0.5 / Fps) return false;
        (latest, pending) = (pending, latest);
        latestTime = pendingTime;
        pendingTime = double.NaN;
        version++;
        return true;
    }

    /// <summary>
    /// Copies the newest frame out if it changed since <paramref name="seen"/> and is from around the wanted time
    /// (not one left from before a jump that the decoder thread hasn't caught up with yet).
    /// </summary>
    public bool TryCopyLatest(ref long seen, Action<byte[]> copy)
    {
        lock (gate)
        {
            if (version == seen || double.IsNaN(latestTime) || Math.Abs(latestTime - wanted) > StaleSec) return false;
            seen = version;
            copy(latest);
            return true;
        }
    }

    void Run()
    {
        while (!disposed)
        {
            double w;
            long idle;
            lock (gate)
            {
                w = wanted;
                idle = idleSince;
            }
            if (w < 0)
            {
                if (source != null && Environment.TickCount64 - idle > IdleCloseMs) Close();
                wake.WaitOne(100);
                continue;
            }

            // seek when nothing was sought yet, or the wanted time is behind the earliest frame we can still give
            // (the one shown, else the one read ahead, else the source's next: right after a seek nothing is
            // shown yet, and a sync edit moving back past the seek point would otherwise wait forever) / far ahead
            double earliest = !double.IsNaN(latestTime) ? latestTime : !double.IsNaN(pendingTime) ? pendingTime : nextTime;
            bool behind = w < earliest - 0.5 / Fps;
            bool farAhead = w > nextTime + 1.0;
            if (!positioned || behind || farAhead)
            {
                Seek(Math.Max(0, w));
                continue;
            }

            // a frame read ahead becomes current once it's due (Want may have promoted it meanwhile: hence the check under the lock)
            if (!double.IsNaN(pendingTime))
            {
                bool promoted;
                lock (gate) promoted = PromoteDue(w);
                if (!promoted) wake.WaitOne(5);
                continue;
            }

            if (ended || source == null || nextTime > w + Lookahead)
            {
                wake.WaitOne(5);
                continue;
            }

            // frames that are already late are dropped by the source instead of shown
            if (!source.Read(spare, w, out double t))
            {
                ended = true;
                continue;
            }
            nextTime = t + 1 / Fps;
            lock (gate)
            {
                (pending, spare) = (spare, pending);
                pendingTime = t;
            }
        }
        Close();
    }

    void Seek(double sec)
    {
        Interlocked.Increment(ref Restarts);
        lock (gate)
        {
            pendingTime = double.NaN;
            latestTime = double.NaN; // so a backwards seek doesn't keep seeking
        }
        positioned = true;
        nextTime = sec;
        ended = false;
        source ??= reopen();
        // a file that can't be opened again (moved, unplugged drive) stays ended until the next jump
        if (source == null || source.Width != Width || source.Height != Height)
        {
            Close();
            positioned = true;
            ended = true;
            return;
        }
        source.Seek(sec);
    }

    void Close()
    {
        source?.Dispose();
        source = null;
        positioned = false;
        lock (gate)
        {
            pendingTime = double.NaN;
            latestTime = double.NaN;
        }
    }

    public void Dispose()
    {
        disposed = true;
        wake.Set();
        // the thread closes the source itself; a pipe stuck on a read is unblocked by ending its process
        if (!thread.Join(1000) && source is PipeVideoSource pipe) pipe.Dispose();
    }
}
