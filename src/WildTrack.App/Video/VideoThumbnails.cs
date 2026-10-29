using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WildTrack.App.Video;

/// <summary>
/// Port of GuitarCam/src/session/thumbnails.ts: evenly spaced frames of a take's video for its timeline lane,
/// generated once per file and kept in memory for the session. FFmpeg pulls each frame with a fast seek
/// (-ss before -i, a keyframe every second in our takes) on a single background thread, one take at a time,
/// like the web's queue, so the UI never waits for it.
/// </summary>
public static class VideoThumbnails
{
    const int Count = 16; // THUMB_COUNT
    const int Width = 160; // THUMB_WIDTH

    sealed class Entry
    {
        public Bitmap[]? Frames; // null while generating; empty when the file could not be read
    }

    static readonly Dictionary<string, Entry> cache = [];
    static readonly Queue<(string Key, string Path, double DurationSec)> queue = [];
    static readonly object gate = new();
    static bool running;

    /// <summary>Raised on the UI thread when a take's frames are ready.</summary>
    public static event Action? Ready;

    /// <summary>The frames for a video file, or null while they are still being generated (this queues them).</summary>
    public static Bitmap[]? Get(string path, double durationSec)
    {
        string key = path; // media files are never rewritten, so the path is enough (and Render calls this)
        lock (gate)
        {
            if (cache.TryGetValue(key, out var hit)) return hit.Frames;
            cache[key] = new Entry();
            queue.Enqueue((key, path, durationSec));
            if (!running)
            {
                running = true;
                new Thread(Run) { IsBackground = true, Name = "video thumbnails", Priority = ThreadPriority.BelowNormal }.Start();
            }
        }
        return null;
    }

    static void Run()
    {
        while (true)
        {
            (string Key, string Path, double DurationSec) job;
            lock (gate)
            {
                if (queue.Count == 0) { running = false; return; }
                job = queue.Dequeue();
            }
            var frames = Generate(job.Path, job.DurationSec);
            lock (gate) cache[job.Key].Frames = frames;
            Dispatcher.UIThread.Post(() => Ready?.Invoke());
        }
    }

    static Bitmap[] Generate(string path, double durationSec)
    {
        if (!File.Exists(path) || durationSec <= 0) return [];
        var frames = new List<Bitmap>();
        for (int k = 0; k < Count; k++)
        {
            double t = Math.Max(0, Math.Min(durationSec - 0.05, (k + 0.5) * durationSec / Count));
            var frame = Frame(path, t);
            if (frame == null) break; // like the web: stop at the first frame that does not come
            frames.Add(frame);
        }
        return [.. frames];
    }

    static Bitmap? Frame(string path, double sec)
    {
        var psi = new ProcessStartInfo("ffmpeg",
        [
            "-v", "error", "-nostdin",
            "-ss", sec.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
            "-an", "-frames:v", "1", "-vf", $"scale={Width}:-2",
            "-c:v", "bmp", "-f", "image2pipe", "-",
        ])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            using var data = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(data);
            if (!p.WaitForExit(10_000)) p.Kill();
            if (data.Length == 0) return null;
            data.Position = 0;
            return new Bitmap(data);
        }
        catch
        {
            return null; // no FFmpeg, or a file it cannot read: the lane keeps its plain look
        }
    }
}
