using System.Diagnostics;
using System.Globalization;
using WildTrack.Audio;

namespace WildTrack.App.Video;

/// <summary>
/// The fallback frame source: ffmpeg.exe (from the PATH) decoding to raw BGRA through a pipe at a constant 30 fps.
/// Each seek starts a new process (-ss before -i: fast and frame-accurate), which costs a few frames, so it is only
/// used when the in-process decoder (<see cref="NativeVideoSource"/>) can't load its DLLs or open the file.
/// </summary>
public sealed class PipeVideoSource : IVideoFrameSource
{
    readonly string path;
    readonly int frameBytes;
    Process? proc;
    Stream? pipe;
    double startSec;
    long index; // frame index (from startSec) the pipe gives next

    public int Width { get; }
    public int Height { get; }

    PipeVideoSource(string path, int width, int height)
    {
        this.path = path;
        Width = width;
        Height = height;
        frameBytes = width * height * 4;
    }

    /// <summary>Probes the file with ffprobe; null if it has no picture or FFmpeg isn't installed.</summary>
    public static PipeVideoSource? Open(string path, int maxWidth = 640)
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
            return new PipeVideoSource(path, ow, oh);
        }
        catch
        {
            return null;
        }
    }

    public void Seek(double sec)
    {
        Dispose();
        startSec = Math.Floor(Math.Max(0, sec) * VideoDecoder.Fps) / VideoDecoder.Fps;
        index = 0;
        var psi = new ProcessStartInfo("ffmpeg",
        [
            "-v", "error", "-nostdin",
            "-ss", startSec.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
            "-an", "-vf", $"fps={VideoDecoder.Fps.ToString(CultureInfo.InvariantCulture)},scale={Width}:{Height}",
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
        }
    }

    public bool Read(byte[] bgra, double notBefore, out double sec)
    {
        while (true)
        {
            sec = startSec + index / VideoDecoder.Fps;
            var p = pipe;
            if (p == null) return false;
            try
            {
                p.ReadExactly(bgra, 0, frameBytes);
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
            {
                return false;
            }
            index++;
            if (sec + 1 / VideoDecoder.Fps > notBefore) return true;
        }
    }

    /// <summary>Ends the process. Safe from another thread: a read waiting on the pipe returns false.</summary>
    public void Dispose()
    {
        var p = proc;
        proc = null;
        pipe = null;
        try
        {
            if (p is { HasExited: false }) p.Kill();
        }
        catch
        {
            // already gone
        }
        p?.Dispose();
    }
}
