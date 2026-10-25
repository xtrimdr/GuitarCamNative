using System.Diagnostics;
using System.Globalization;

namespace GuitarCam.App.Video;

/// <summary>
/// Decodes one video file with FFmpeg into raw BGRA frames through a pipe, at a constant 30 fps and a
/// fixed size. A background thread stays a few frames ahead of the time the UI asks for; jumping back,
/// or more than a second ahead, restarts FFmpeg at that point (-ss before -i: fast and frame-accurate).
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    public const double Fps = 30;
    const double Lookahead = 3 / Fps;

    readonly string path;
    readonly int frameBytes;
    readonly Thread thread;
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    volatile bool disposed;

    double wanted = -1; // video time the UI wants (s), < 0 = idle
    Process? proc;
    Stream? pipe;
    double startSec;
    long nextIndex; // frame index (from startSec) the pipe gives next
    bool ended;

    // latest: the frame to show; pending: read ahead, not due yet; spare: free buffer
    byte[] latest, pending, spare;
    double latestTime = double.NaN, pendingTime = double.NaN;
    long version;

    /// <summary>FFmpeg restarts across every decoder, for <see cref="Audio.AudioGapLog"/>.</summary>
    public static int Restarts;

    public int Width { get; }
    public int Height { get; }

    VideoDecoder(string path, int width, int height)
    {
        this.path = path;
        Width = width;
        Height = height;
        frameBytes = width * height * 4;
        latest = new byte[frameBytes];
        pending = new byte[frameBytes];
        spare = new byte[frameBytes];
        thread = new Thread(Run) { IsBackground = true, Name = $"video {Path.GetFileName(path)}" };
        thread.Start();
    }

    /// <summary>Probes the file and opens a decoder that outputs at most <paramref name="maxWidth"/> pixels wide.</summary>
    public static VideoDecoder? Open(string path, int maxWidth = 640)
    {
        if (!File.Exists(path)) return null;
        var psi = new ProcessStartInfo("ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:stream_side_data=rotation", "-of", "csv=p=0", path])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            string[] lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            p.WaitForExit();
            var parts = lines[0].Split(',');
            int w = int.Parse(parts[0], CultureInfo.InvariantCulture), h = int.Parse(parts[1], CultureInfo.InvariantCulture);
            // phone videos can be stored landscape with a rotation tag; FFmpeg's output follows the tag
            if (lines.Skip(1).Concat(parts.Skip(2)).Any(s => s.Trim() is "90" or "-90" or "270" or "-270")) (w, h) = (h, w);
            int ow = Math.Min(maxWidth, w) & ~1;
            int oh = (int)Math.Round((double)h * ow / w) & ~1;
            return new VideoDecoder(path, ow, oh);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Asks for the frame at <paramref name="sec"/> (video time). Cheap: call it every UI tick.</summary>
    public void Want(double sec)
    {
        lock (gate) wanted = sec;
        wake.Set();
    }

    /// <summary>Copies the newest frame out if it changed since <paramref name="seen"/>.</summary>
    public bool TryCopyLatest(ref long seen, Action<byte[]> copy)
    {
        lock (gate)
        {
            if (version == seen || double.IsNaN(latestTime)) return false;
            seen = version;
            copy(latest);
            return true;
        }
    }

    double TimeOf(long index) => startSec + index / Fps;

    void Run()
    {
        while (!disposed)
        {
            double w;
            lock (gate) w = wanted;
            if (w < 0)
            {
                wake.WaitOne(100);
                continue;
            }

            // restart when there's no process, or the wanted time is behind the earliest frame we can still give
            // (the one shown, else the one read ahead, else the pipe's next: right after a restart nothing is
            // shown yet, and a sync edit moving back past the restart point would otherwise wait forever) / far ahead
            double earliest = !double.IsNaN(latestTime) ? latestTime : !double.IsNaN(pendingTime) ? pendingTime : TimeOf(nextIndex);
            bool behind = w < earliest - 0.5 / Fps;
            bool farAhead = w > TimeOf(nextIndex) + 1.0;
            if (proc == null || behind || farAhead)
            {
                Restart(Math.Max(0, w));
                continue;
            }

            // a frame read ahead becomes current once it's due
            if (!double.IsNaN(pendingTime))
            {
                if (pendingTime <= w + 0.5 / Fps)
                {
                    lock (gate)
                    {
                        (latest, pending) = (pending, latest);
                        latestTime = pendingTime;
                        pendingTime = double.NaN;
                        version++;
                    }
                    continue;
                }
                wake.WaitOne(5);
                continue;
            }

            if (ended || TimeOf(nextIndex) > w + Lookahead)
            {
                wake.WaitOne(5);
                continue;
            }

            if (!ReadFrame(spare))
            {
                ended = true;
                continue;
            }
            double t = TimeOf(nextIndex++);
            // frames that are already late are skipped instead of shown
            if (t + 1 / Fps <= w) continue;
            lock (gate)
            {
                (pending, spare) = (spare, pending);
                pendingTime = t;
            }
        }
        Kill();
    }

    bool ReadFrame(byte[] buf)
    {
        if (pipe == null) return false;
        try
        {
            pipe.ReadExactly(buf, 0, frameBytes);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    void Restart(double sec)
    {
        Interlocked.Increment(ref Restarts);
        Kill();
        startSec = Math.Floor(sec * Fps) / Fps;
        nextIndex = 0;
        ended = false;
        lock (gate)
        {
            pendingTime = double.NaN;
            latestTime = double.NaN; // so a backwards seek doesn't keep restarting
        }
        var psi = new ProcessStartInfo("ffmpeg",
        [
            "-v", "error", "-nostdin",
            "-ss", startSec.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
            "-an", "-vf", $"fps={Fps.ToString(CultureInfo.InvariantCulture)},scale={Width}:{Height}",
            "-pix_fmt", "bgra", "-f", "rawvideo", "-",
        ])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            proc = Process.Start(psi);
            if (proc == null) return;
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            pipe = proc.StandardOutput.BaseStream;
        }
        catch
        {
            proc = null;
            pipe = null;
            ended = true;
        }
    }

    void Kill()
    {
        try
        {
            if (proc is { HasExited: false }) proc.Kill();
        }
        catch
        {
            // already gone
        }
        proc?.Dispose();
        proc = null;
        pipe = null;
    }

    public void Dispose()
    {
        disposed = true;
        wake.Set();
        thread.Join(500);
        Kill();
    }
}
