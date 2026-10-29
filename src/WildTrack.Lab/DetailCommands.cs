using System.Diagnostics;
using WildTrack.Audio;

namespace WildTrack.Lab;

/// <summary>
/// <c>nitidez</c>: checks that sharpness, clarity and glow (<see cref="VideoDetail"/>) give the same picture in the preview
/// and in the export. Runs the export's FFmpeg filters and the preview's C# on the same frame, at the export's size
/// (the math matches) and at the preview's 640 wide against the export scaled down (what the eye compares), and
/// measures what the preview pays per frame and that it allocates nothing. With <c>--look</c> it adds that look's share
/// (HDR, Soñado) by its intensity; only the spatial part, the look's LUT is checked by <c>looks</c>.
/// </summary>
static class DetailCommands
{
    const int PreviewWidth = 640;

    public static int Run(Args a)
    {
        string? video = a.Positional.FirstOrDefault();
        int h = a.Int("alto", 1080);
        var fx = new ExportVideoFxSpec(Sharpness: a.Double("nitidez", 60), Clarity: a.Double("claridad", 60), Glow: a.Double("resplandor", 0));
        var look = new ExportColorSpec(Look: a.Get("look", "none"), Amount: a.Double("intensidad", 100));
        var amounts = VideoDetail.For(look, fx);
        string dir = a.Get("carpeta") ?? Path.Combine(Path.GetTempPath(), "wtlab-nitidez");
        Directory.CreateDirectory(dir);

        // the frame: from a video, or a pattern with edges, gradients and fine lines
        byte[] src;
        int w;
        if (video != null)
        {
            src = Ffmpeg(["-ss", a.Get("en", "1"), "-i", video, "-frames:v", "1", "-vf", $"scale=-2:{h}", "-f", "rawvideo", "-pix_fmt", "bgra", "-"], null);
            w = src.Length / 4 / h;
            if (w <= 0) throw new ArgumentException($"no pude leer un cuadro de {video}");
        }
        else
        {
            w = (h * 16 / 9) & ~1;
            src = Pattern(w, h);
        }
        Console.WriteLine($"Cuadro {w}×{h}, nitidez {fx.Sharpness:0}, claridad {fx.Clarity:0}, resplandor {fx.Glow:0}, look {look.Look} {look.Amount:0}%"
            + $" · radio de claridad {VideoDetail.ClarityRadius(h, w, h)} px, unsharp {2 * VideoDetail.SharpSteps(h) + 1}×{2 * VideoDetail.SharpSteps(h) + 1}, radio del resplandor {VideoDetail.GlowRadius(h, w, h)} px");

        // export: the exporter's filters on the frame, and the same trip through YUV without them
        byte[] exported = Export(src, w, h, amounts);
        byte[] plain = Export(src, w, h, default);
        byte[] mine = (byte[])src.Clone();
        new VideoDetail.Processor().Apply(mine, w, h, h, amounts);
        Console.WriteLine($"Ida y vuelta a YUV sin filtros (piso): {Diff(plain, src)}");
        Console.WriteLine($"Cuánto cambia el filtro:              {Diff(exported, plain)}");
        Console.WriteLine($"Exportación vs C#, a {w}×{h}:     {Diff(exported, mine)}");

        // preview: the preview's C# at 640 wide against the export scaled down to that size
        // --ancho-preview: how many decoded pixels the preview's panel shows across (less than 640 with zoom or a narrow panel)
        int pw = a.Int("ancho-preview", PreviewWidth), ph = (int)Math.Round((double)h * pw / w) & ~1;
        byte[] small = Scale(src, w, h, pw, ph);
        byte[] smallPlain = Scale(plain, w, h, pw, ph);
        byte[] exportedSmall = Scale(exported, w, h, pw, ph);
        byte[] preview = (byte[])small.Clone();
        new VideoDetail.Processor().Apply(preview, pw, ph, ph, amounts);
        Console.WriteLine($"Preview ({pw}×{ph}) vs exportación achicada: {Diff(preview, exportedSmall)}  (sin filtro serían {Diff(smallPlain, exportedSmall)})");

        Png(src, w, h, Path.Combine(dir, "original.png"));
        Png(exported, w, h, Path.Combine(dir, "exportado.png"));
        Png(mine, w, h, Path.Combine(dir, "csharp.png"));
        Png(preview, pw, ph, Path.Combine(dir, "preview.png"));
        Png(exportedSmall, pw, ph, Path.Combine(dir, "exportado-640.png"));
        Console.WriteLine($"Imágenes en {dir}");

        // cost in the preview: a landscape and a vertical frame, as decoded at 640 wide
        foreach (var (bw, bh) in new[] { (640, 360), (640, 1138) })
        {
            byte[] frame = Scale(src, w, h, bw, bh);
            byte[] work = new byte[frame.Length];
            var proc = new VideoDetail.Processor();
            // past the JIT's first tiers, as in the app after a few seconds of playing
            for (int i = 0; i < 60; i++)
            {
                Buffer.BlockCopy(frame, 0, work, 0, frame.Length);
                proc.Apply(work, bw, bh, bh, amounts);
            }
            Thread.Sleep(300);
            const int Iters = 40;
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iters; i++)
            {
                Buffer.BlockCopy(frame, 0, work, 0, frame.Length);
                proc.Apply(work, bw, bh, bh, amounts);
            }
            sw.Stop();
            long alloc = GC.GetAllocatedBytesForCurrentThread() - bytes0;
            Console.WriteLine($"Costo en la preview ({bw}×{bh}): {sw.Elapsed.TotalMilliseconds / Iters:0.00} ms/cuadro, {alloc / Iters} bytes asignados por cuadro");
        }
        return 0;
    }

    /// <summary>The exporter's filter chain (<see cref="VideoDetail.ExportFilters"/>) over a raw BGRA frame.</summary>
    static byte[] Export(byte[] src, int w, int h, VideoDetail.Amounts amounts)
    {
        var chain = new List<string>();
        string picture = VideoDetail.ExportFilters(amounts, w, h, "[0:v]null", chain, "0");
        if (!picture.Contains("yuv444p")) picture += ",format=yuv444p";
        chain.Add($"{picture},format=bgra[out]");
        return Ffmpeg(["-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-i", "-",
            "-filter_complex", string.Join(";", chain), "-map", "[out]", "-f", "rawvideo", "-pix_fmt", "bgra", "-"], src);
    }

    static byte[] Scale(byte[] src, int w, int h, int nw, int nh) =>
        Ffmpeg(["-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-i", "-", "-vf", $"scale={nw}:{nh}:flags=area",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-"], src);

    static void Png(byte[] src, int w, int h, string path) =>
        Ffmpeg(["-y", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-i", "-", "-frames:v", "1", path], src);

    /// <summary>Mean and max difference per channel (0..255) and the share of values more than 2 apart.</summary>
    static string Diff(byte[] x, byte[] y)
    {
        long sum = 0, over = 0, n = 0;
        int max = 0;
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            if ((i & 3) == 3) continue;
            int d = Math.Abs(x[i] - y[i]);
            sum += d; n++;
            if (d > max) max = d;
            if (d > 2) over++;
        }
        return $"media {(double)sum / Math.Max(1, n):0.00}, máx {max}, {100.0 * over / Math.Max(1, n):0.0}% a más de 2";
    }

    /// <summary>A test picture: soft gradients, hard edges, thin lines and some texture, as a guitar on a wall has.</summary>
    static byte[] Pattern(int w, int h)
    {
        var buf = new byte[w * h * 4];
        var rnd = new Random(7);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double u = (double)x / w, v = (double)y / h;
            double r = 0.15 + 0.7 * u, g = 0.2 + 0.5 * v, b = 0.35 + 0.3 * Math.Sin(6 * u + 3 * v);
            if (((x / (w / 8)) + (y / (h / 6))) % 2 == 0) { r *= 0.6; g *= 0.6; b *= 0.6; }
            if (x % 37 < 2 || y % 53 < 1) { r = g = b = 0.9; }
            double t = (rnd.NextDouble() - 0.5) * 0.06;
            int p = (y * w + x) * 4;
            buf[p] = C(b + t); buf[p + 1] = C(g + t); buf[p + 2] = C(r + t); buf[p + 3] = 255;
        }
        return buf;

        static byte C(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    static byte[] Ffmpeg(string[] args, byte[]? input)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = input != null, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var s in (string[])["-hide_banner", "-loglevel", "error", .. args]) psi.ArgumentList.Add(s);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("no pude abrir ffmpeg");
        var err = p.StandardError.ReadToEndAsync();
        var outTask = Task.Run(() =>
        {
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            return ms.ToArray();
        });
        if (input != null)
        {
            p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
        }
        var bytes = outTask.GetAwaiter().GetResult();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg falló: {err.Result.Trim()}");
        return bytes;
    }
}
