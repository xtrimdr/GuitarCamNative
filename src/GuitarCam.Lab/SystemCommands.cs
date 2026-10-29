using GuitarCam.Audio;

namespace GuitarCam.Lab;

/// <summary>The top bar's system meter and FFmpeg check without the window.</summary>
static class SystemCommands
{
    public static int Run(Args a)
    {
        if (a.Flag("instalar-ffmpeg"))
        {
            if (!FfmpegSetup.HasWinget())
            {
                Console.WriteLine($"No encontré winget: bajalo de {FfmpegSetup.DownloadUrl}");
                return 1;
            }
            Console.WriteLine($"Instalando FFmpeg con winget ({FfmpegSetup.WingetId})…");
            var r = FfmpegSetup.InstallAsync().GetAwaiter().GetResult();
            Console.WriteLine($"winget terminó con el código {r.ExitCode}");
            if (r.Log.Length > 0) Console.WriteLine(r.Log);
            Console.WriteLine(r.Ready ? "FFmpeg listo" : "FFmpeg sigue sin aparecer en el PATH");
            if (!r.Ready) return 1;
        }
        string? ffmpeg = FfmpegInfo.Probe();
        Console.WriteLine(ffmpeg == null ? "FFmpeg no encontrado en el PATH: no vas a poder exportar" : $"FFmpeg {ffmpeg}".Trim());
        if (ffmpeg != null)
        {
            var caps = VideoEncoders.DetectAsync().GetAwaiter().GetResult();
            Console.WriteLine(caps.Gpu is { } g
                ? $"Exporta con la placa de video: {VideoEncoders.Brand(g)} ({VideoEncoders.Id(g)})"
                : "Ninguna placa de video que FFmpeg pueda usar: exporta con el procesador");
            Console.WriteLine(caps.Loopback ? "Cada toma se filtra en su propio hilo" : "Las tomas se filtran de a una (FFmpeg anterior a 7)");
        }
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

/// <summary>The update check of "Acerca de" without the window: this build's version against GitHub's latest release.</summary>
static class VersionCommands
{
    public static int Run(Args a)
    {
        Console.WriteLine($"Esta versión: {GuitarCam.Projects.UpdateCheck.Current.ToString(3)}");
        var latest = GuitarCam.Projects.UpdateCheck.LatestAsync().GetAwaiter().GetResult();
        if (latest == null)
        {
            Console.WriteLine("No pude saber cuál es la última versión en GitHub (¿sin internet, sin releases o límite de la API?)");
            return 1;
        }
        Console.WriteLine($"Última en GitHub: {latest.Version.ToString(3)} ({latest.Tag}) {latest.Url}");
        Console.WriteLine(GuitarCam.Projects.UpdateCheck.IsNewer(latest) ? "Hay una versión nueva: la ventana avisa" : "Estás al día");
        return 0;
    }
}
