namespace GuitarCam.Audio.Dsp;

public sealed record PanTemplateDef(string Id, string Label, string Hint);

/// <summary>
/// Port of <c>GuitarCam/src/audio/panTemplates.ts</c>: pan positions (−1..1) across all tracks.
/// </summary>
public static class PanTemplates
{
    public static readonly PanTemplateDef[] All =
    [
        new("fan", "Abanico", "La primera pista al centro; cada pista de más abajo se abre un poco más, alternando izquierda y derecha"),
        new("alternate", "Alternado L/R", "Todas igual de abiertas, una a cada lado (ideal para guitarras dobladas)"),
        new("spread", "Distribuido L→R", "Repartidas parejo de izquierda a derecha según el orden de la timeline"),
        new("center", "Centro", "Todas al centro"),
    ];

    static double Round5Pct(double v) => Math.Floor(v * 20.0 + 0.5) / 20.0;

    public static double[] Positions(string template, int count, double width)
    {
        if (count <= 0) return [];
        double w = Math.Clamp(width, 0, 1);
        var res = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (count == 1 || template == "center")
            {
                res[i] = 0;
                continue;
            }
            res[i] = template switch
            {
                "fan" => i == 0
                    ? 0
                    : Round5Pct((i % 2 == 1 ? -1 : 1) * w * (Math.Ceiling(i / 2.0) / Math.Ceiling((count - 1) / 2.0))),
                "alternate" => Round5Pct((i % 2 == 0 ? -1 : 1) * w),
                "spread" => Round5Pct((-1.0 + (2.0 * i) / (count - 1)) * w),
                _ => 0,
            };
        }
        return res;
    }
}
