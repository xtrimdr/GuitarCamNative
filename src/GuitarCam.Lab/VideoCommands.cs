using GuitarCam.Audio;

namespace GuitarCam.Lab;

/// <summary>
/// Video color looks without the window: lists every look with how it maps a gray ramp and a few colors,
/// checks that each one stays finite and inside 0..1 once baked into the export LUT, and can write a look
/// as a .cube file (optionally with the whites / blacks / light adjustments) to try it in another program.
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

    static string Hex(double r, double g, double b)
    {
        static int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{C(r):x2}{C(g):x2}{C(b):x2}";
    }
}
