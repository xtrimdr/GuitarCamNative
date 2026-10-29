using System.Globalization;
using WildTrack.App.Video;
using WildTrack.Projects;

namespace WildTrack.App.Audio;

/// <summary>
/// Diagnostic for audible dropouts: every UI tick it reads the engine's longest wait between ASIO callbacks
/// and longest callback, and when either is past what one block allows, appends a line to
/// <c>%APPDATA%\WildTrack\audio-cortes.log</c> with what happened in that tick (garbage collections
/// per generation, seeks, FFmpeg restarts). Quiet ticks allocate nothing; the file is written off the UI thread.
/// </summary>
public sealed class AudioGapLog
{
    /// <summary>A wait this many blocks long is a dropout (callbacks normally come one block apart).</summary>
    const double GapBlocks = 1.6;

    static readonly string LogPath = Path.Combine(AppConfig.Dir, "audio-cortes.log");
    readonly object fileGate = new();
    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
    int restarts = VideoDecoder.Restarts;
    bool started;

    public void Tick(PlaybackEngine engine)
    {
        var (gap, run, block, seeks) = engine.TakeTimings();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2), vr = VideoDecoder.Restarts;
        bool bad = block > 0 && (gap > block * GapBlocks || run > block);
        if (bad)
        {
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff}  hueco {1,6:0.0} ms  callback {2,5:0.0} ms  bloque {3:0.0} ms  {4}  GC {5}/{6}/{7}  saltos {8}  ffmpeg {9}",
                DateTime.Now, gap, run, block, engine.Playing ? "sonando" : "parado", g0 - gen0, g1 - gen1, g2 - gen2, seeks, vr - restarts);
            bool first = !started;
            started = true;
            ThreadPool.QueueUserWorkItem(_ => Append(line, first));
        }
        (gen0, gen1, gen2, restarts) = (g0, g1, g2, vr);
    }

    void Append(string line, bool first)
    {
        try
        {
            lock (fileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (first) File.AppendAllText(LogPath, $"{Environment.NewLine}--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // a diagnostic never gets in the way
        }
    }
}
