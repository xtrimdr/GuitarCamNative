using System.Diagnostics;
using System.Globalization;
using WildTrack.Audio;

namespace WildTrack.Lab;

/// <summary>The camera without the window: list devices, record a video like a take does, and check that a crash keeps it.</summary>
static class CameraCommands
{
    public static int Run(Args a)
    {
        string sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : "listar";
        return sub switch
        {
            "listar" => List(),
            "grabar" => Record(a),
            "verificar-corte" => VerifyCrash(a),
            _ => throw new ArgumentException("camara listar|grabar|verificar-corte"),
        };
    }

    static int List()
    {
        var devices = CameraDevices.List();
        if (devices.Length == 0)
        {
            Console.WriteLine("No encontré cámaras (¿FFmpeg está en el PATH?).");
            return 1;
        }
        string? pick = CameraDevices.Pick(devices, null);
        foreach (var d in devices)
        {
            Console.WriteLine($"{(d == pick ? "*" : " ")} {d}");
            foreach (var o in CameraDevices.Options(d)) Console.WriteLine($"      {o}");
        }
        Console.WriteLine("* = la que elige la app si no le decís otra");
        return 0;
    }

    static CameraSource Source(Args a)
    {
        if (a.Get("ip") is { } ip) return new CameraSource(null, CameraResMode.Native, ip);
        string? device = a.Get("dispositivo") ?? CameraDevices.Pick(CameraDevices.List(), null)
            ?? throw new InvalidOperationException("No encontré cámaras.");
        return new CameraSource(device, CameraSource.ParseMode(a.Get("resolucion")));
    }

    static CameraCapture OpenCamera(CameraSource src)
    {
        var cam = CameraCapture.Open(src);
        var sw = Stopwatch.StartNew();
        while ((cam.Opening || !cam.HasSignal) && cam.Error == null && sw.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(50);
        if (cam.Error != null || !cam.HasSignal)
        {
            cam.Dispose();
            throw new InvalidOperationException(cam.Error ?? "La cámara no mandó imagen.");
        }
        return cam;
    }

    /// <summary>Records like a take: frames placed by arrival time from the start, padded to the length, then made faststart.</summary>
    static int Record(Args a)
    {
        double seconds = a.Double("segundos", 5);
        string path = Path.GetFullPath(a.Get("salida", "camara.mp4"));
        var src = Source(a);
        using var cam = OpenCamera(src);
        Console.WriteLine($"{(src.IsIp ? src.IpHost : src.Device)}: {cam.Resolution}");
        long start = Stopwatch.GetTimestamp();
        var w = VideoTakeWriter.Start(path, cam.Width, cam.Height, () => start);
        cam.Attach(w);
        Console.WriteLine($"Grabando {seconds:0.#} s en {path}…");
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds) Thread.Sleep(5);
        w.StopTaking();
        cam.Attach(null);
        var res = w.Finish(seconds) ?? throw new InvalidOperationException("No llegó ningún cuadro.");
        w.Dispose();
        Console.WriteLine($"{res.Frames} cuadros ({res.DurationSec:0.00} s) · repetidos {res.Duplicated} · descartados {res.Dropped}{(res.Error != null ? $" · error: {res.Error}" : "")}");
        var probe = Probe(path);
        Console.WriteLine($"ffprobe: {probe.Width}×{probe.Height} · {probe.DurationSec:0.00} s · {probe.Frames} cuadros · keyframe cada {probe.MaxKeyGapSec:0.00} s como mucho");
        if (Math.Abs(probe.DurationSec - seconds) > 0.1) throw new InvalidOperationException($"El video dura {probe.DurationSec:0.00} s y la toma {seconds:0.00} s.");
        if (probe.MaxKeyGapSec > 1.01) throw new InvalidOperationException("Hay keyframes a más de 1 s.");
        return 0;
    }

    /// <summary>
    /// Records from a child wtlab and kills it mid-take: once alone (FFmpeg closes the file when the pipe ends)
    /// and once with FFmpeg (the fragmented MP4 keeps every finished second). Both files must play.
    /// </summary>
    static int VerifyCrash(Args a)
    {
        string dir = Path.Combine(Path.GetTempPath(), "wtlab-camara");
        Directory.CreateDirectory(dir);
        string self = Environment.ProcessPath!;
        foreach (bool tree in new[] { false, true })
        {
            string path = Path.Combine(dir, tree ? "corte-con-ffmpeg.mp4" : "corte.mp4");
            File.Delete(path);
            var args = new List<string> { "camara", "grabar", "--segundos", "60", "--salida", path };
            if (a.Get("dispositivo") is { } d) args.AddRange(["--dispositivo", d]);
            if (a.Get("ip") is { } ip) args.AddRange(["--ip", ip]);
            var psi = new ProcessStartInfo(self, args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var child = Process.Start(psi)!;
            // wait until it's recording, then a few seconds more
            while (child.StandardOutput.ReadLine() is { } line && !line.StartsWith("Grabando")) { }
            Thread.Sleep(4500);
            child.Kill(tree);
            child.WaitForExit();
            // without the tree, the orphaned FFmpeg finishes the file once its pipe closes
            ProbeResult probe = default;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                Thread.Sleep(500);
                probe = Probe(path);
                if (probe.DurationSec >= 3 && !tree) break;
            }
            string how = tree ? "matando también FFmpeg" : "matando solo el programa";
            Console.WriteLine($"Corte {how}: quedó {probe.DurationSec:0.00} s ({probe.Frames} cuadros)");
            if (probe.DurationSec < (tree ? 2.5 : 3.5)) throw new InvalidOperationException($"Corte {how}: el video quedó de {probe.DurationSec:0.00} s, se perdió la toma.");
        }
        Console.WriteLine("Cámara: los dos cortes dejaron un video que se puede abrir.");
        return 0;
    }

    readonly record struct ProbeResult(int Width, int Height, double DurationSec, int Frames, double MaxKeyGapSec);

    static ProbeResult Probe(string path)
    {
        if (!File.Exists(path)) return default;
        var psi = new ProcessStartInfo("ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:packet=pts_time,flags", "-of", "csv=p=0", path])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEndAsync();
        var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        p.WaitForExit();
        int w = 0, h = 0, frames = 0;
        double maxPts = 0, lastKey = 0, maxGap = 0;
        var inv = CultureInfo.InvariantCulture;
        foreach (var l in lines)
        {
            var parts = l.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[0], inv, out int pw) && int.TryParse(parts[1], inv, out int ph) && !parts[1].Contains('.'))
            {
                (w, h) = (pw, ph);
                continue;
            }
            if (!double.TryParse(parts[0], NumberStyles.Float, inv, out double t)) continue;
            frames++;
            maxPts = Math.Max(maxPts, t);
            if (parts.Length > 1 && parts[1].Contains('K'))
            {
                maxGap = Math.Max(maxGap, t - lastKey);
                lastKey = t;
            }
        }
        maxGap = Math.Max(maxGap, maxPts - lastKey);
        return new ProbeResult(w, h, frames > 0 ? maxPts + 1 / VideoTakeWriter.Fps : 0, frames, maxGap);
    }
}
