using GuitarCam.Audio;

namespace GuitarCam.Lab;

/// <summary>The top bar's system meter and FFmpeg check without the window.</summary>
static class SystemCommands
{
    public static int Run(Args a)
    {
        string? ffmpeg = FfmpegInfo.Probe();
        Console.WriteLine(ffmpeg == null ? "FFmpeg no encontrado en el PATH: no vas a poder exportar" : $"FFmpeg {ffmpeg}".Trim());
        double seconds = a.Double("segundos", 5);
        using var stats = new SystemStats();
        var end = DateTime.Now.AddSeconds(seconds);
        do
        {
            Thread.Sleep(1500);
            var s = stats.Read();
            string gpu = s.Gpu is { } g
                ? $"GPU {g.Load:0}% ({g.Name}; {Gb(g.VramUsed)} / {Gb(g.VramTotal)} GB)"
                : "GPU midiendo…";
            Console.WriteLine($"CPU {s.Cpu:0}% ({s.Cores} hilos) · {gpu} · RAM {Gb(s.RamUsed)} / {Gb(s.RamTotal)} GB");
        } while (DateTime.Now < end);
        return 0;
    }

    static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0");
}
