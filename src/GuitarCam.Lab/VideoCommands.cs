using System.Diagnostics;
using System.Globalization;
using GuitarCam.Audio;

namespace GuitarCam.Lab;

/// <summary>
/// Video color looks without the window: lists every look with how it maps a gray ramp and a few colors,
/// checks that each one stays finite and inside 0..1 once baked into the export LUT, and can write a look
/// as a .cube file (optionally with the whites / blacks / light adjustments) to try it in another program.
/// <c>leer-video</c> measures the preview's in-process decoder (<see cref="NativeVideoSource"/>): open, seeks,
/// straight reading, bytes allocated per frame, and how its picture and its time to first frame compare with ffmpeg.exe.
/// </summary>
static class VideoCommands
{
    static readonly (string Name, double R, double G, double B)[] Probes =
    [
        ("negro", 0, 0, 0), ("gris", 0.5, 0.5, 0.5), ("blanco", 1, 1, 1),
        ("piel", 0.86, 0.62, 0.5), ("madera", 0.6, 0.35, 0.18), ("verde", 0.3, 0.6, 0.3), ("azul", 0.2, 0.35, 0.75),
    ];

    public static int Looks(Args a)
    {
        string? only = a.Get("look");
        string? output = a.Get("salida");
        bool allOk = true;
        foreach (var look in VideoColorProcessor.Looks)
        {
            if (only != null && look.Id != only) continue;
            var lut = VideoColorProcessor.BuildLut(new ExportColorSpec(Look: look.Id, Amount: 100));
            bool ok = lut.All(v => float.IsFinite(v) && v >= 0 && v <= 1);
            allOk &= ok;
            Console.WriteLine($"{look.Id,-10} {look.Label,-12} {(ok ? "OK" : "FUERA DE RANGO")}");
            foreach (var (name, r, g, b) in Probes)
            {
                var (lr, lg, lb) = VideoColorProcessor.ApplyLook(look.Id, r, g, b);
                Console.WriteLine($"    {name,-7} {Hex(r, g, b)} -> {Hex(lr, lg, lb)}");
            }
        }
        if (output != null)
        {
            if (only == null) throw new ArgumentException("--salida necesita --look");
            var spec = new ExportColorSpec(Look: only, Amount: 100,
                Whites: a.Double("blancos", 0), Blacks: a.Double("negros", 0), Light: a.Double("luz", 0));
            File.WriteAllText(output, VideoColorProcessor.LutToCube(spec));
            Console.WriteLine($"LUT escrito en {output}");
        }
        return allOk ? 0 : 1;
    }

    public static int ReadVideo(Args a)
    {
        string path = a.Positional.FirstOrDefault() ?? throw new ArgumentException("falta el archivo de video");
        int maxWidth = a.Int("ancho", 640), seeks = a.Int("saltos", 20);
        if (!NativeVideoSource.Available)
        {
            Console.WriteLine($"NO SE PUEDE: {NativeVideoSource.Error}");
            return 1;
        }
        Console.WriteLine($"FFmpeg {NativeVideoSource.Version} en {NativeVideoSource.LibDir}");
        var sw = Stopwatch.StartNew();
        using var src = NativeVideoSource.Open(path, maxWidth, !a.Flag("cpu")) ?? throw new ArgumentException($"no pude abrir {path}");
        double openMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"{src.Width}x{src.Height}, {src.DurationSec:0.00} s, {(src.Hardware ? "placa de video (D3D11)" : "procesador")}, abrir {openMs:0} ms");
        var buf = new byte[src.Width * src.Height * 4];
        double len = Math.Max(0.1, src.DurationSec);

        // straight reading, as while playing
        src.Seek(0);
        src.Read(buf, 0, out _);
        long bytes0 = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        int n = 0;
        while (n < 150 && src.Read(buf, 0, out _)) n++;
        double seqMs = sw.Elapsed.TotalMilliseconds;
        long perFrame = n > 0 ? (GC.GetAllocatedBytesForCurrentThread() - bytes0) / n : 0;
        Console.WriteLine($"leer seguido: {n} cuadros en {seqMs:0} ms = {n / (seqMs / 1000):0} cuadros/s, {perFrame} bytes asignados por cuadro");

        // seeks to random points, as with scrubbing and cuts
        var rnd = new Random(1);
        var times = new List<double>();
        for (int i = 0; i < seeks; i++)
        {
            double t = rnd.NextDouble() * len * 0.95;
            sw.Restart();
            src.Seek(t);
            bool ok = src.Read(buf, 0, out double got);
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (!ok || Math.Abs(got - t) > 0.1) Console.WriteLine($"  salto a {t:0.000} s: {(ok ? $"dio {got:0.000} s" : "no dio cuadro")}");
        }
        times.Sort();
        Console.WriteLine($"saltar y dar el cuadro: mediana {times[times.Count / 2]:0} ms, peor {times[^1]:0} ms ({seeks} saltos)");

        if (a.Get("cuadro") is { } at)
        {
            double t = double.Parse(at, CultureInfo.InvariantCulture);
            src.Seek(t);
            if (!src.Read(buf, 0, out double got)) throw new InvalidOperationException($"no hay cuadro en {t} s");
            string out1 = a.Get("salida", "cuadro.bmp");
            WriteBmp(out1, buf, src.Width, src.Height);
            Console.WriteLine($"cuadro de {got:0.000} s en {out1}");
        }

        if (a.Flag("comparar"))
        {
            // ffmpeg.exe the way the old preview used it: one process per seek
            var pipeTimes = new List<double>();
            double diffSum = 0;
            int diffN = 0;
            var other = new byte[buf.Length];
            for (int i = 0; i < Math.Min(seeks, 8); i++)
            {
                double t = Math.Floor(rnd.NextDouble() * len * 0.9 * 30) / 30;
                sw.Restart();
                bool ok = PipeFrame(path, t, src.Width, src.Height, other);
                pipeTimes.Add(sw.Elapsed.TotalMilliseconds);
                src.Seek(t);
                if (!ok || !src.Read(buf, 0, out _)) continue;
                long d = 0;
                for (int k = 0; k < buf.Length; k++) if ((k & 3) != 3) d += Math.Abs(buf[k] - other[k]);
                diffSum += d / (buf.Length * 0.75);
                diffN++;
            }
            pipeTimes.Sort();
            Console.WriteLine($"ffmpeg.exe por salto: mediana {pipeTimes[pipeTimes.Count / 2]:0} ms, peor {pipeTimes[^1]:0} ms");
            if (diffN > 0) Console.WriteLine($"diferencia con ffmpeg.exe: {diffSum / diffN:0.00} de 255 por canal en promedio ({diffN} cuadros)");
        }
        return 0;
    }

    static bool PipeFrame(string path, double t, int w, int h, byte[] into)
    {
        var psi = new ProcessStartInfo("ffmpeg",
            ["-v", "error", "-nostdin", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
             "-an", "-frames:v", "1", "-vf", $"scale={w}:{h}", "-pix_fmt", "bgra", "-f", "rawvideo", "-"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        try
        {
            p.StandardOutput.BaseStream.ReadExactly(into, 0, into.Length);
            p.WaitForExit();
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    static void WriteBmp(string path, byte[] bgra, int w, int h)
    {
        using var f = new BinaryWriter(File.Create(path));
        int size = w * h * 4;
        f.Write((byte)'B');
        f.Write((byte)'M');
        f.Write(54 + size);
        f.Write(0);
        f.Write(54);
        f.Write(40);
        f.Write(w);
        f.Write(-h); // top-down
        f.Write((short)1);
        f.Write((short)32);
        f.Write(0);
        f.Write(size);
        f.Write(2835);
        f.Write(2835);
        f.Write(0);
        f.Write(0);
        f.Write(bgra, 0, size);
    }

    static string Hex(double r, double g, double b)
    {
        static int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{C(r):x2}{C(g):x2}{C(b):x2}";
    }
}
