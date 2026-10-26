using System.Globalization;
using System.Text;

namespace GuitarCam.Audio;

public sealed record LookSpec(string Id, string Label);

public sealed record LetterboxSpec(string Id, string Label, double Ratio);

/// <summary>
/// Port of <c>GuitarCam/src/shared/colorGrade.js</c>, <c>src/shared/videoFx.js</c> and <c>src/video/colorGL.ts</c>.
/// Shared by the live preview and the FFmpeg MP4 export so that what you see in the preview matches the exported video:
/// <list type="bullet">
/// <item><description>29 looks (the fitted ones live in <see cref="FittedLooks"/>) + 9 color adjustments baked into a <c>33³</c> 3D LUT (<see cref="BuildLut"/> / <see cref="LutToCube"/>).</description></item>
/// <item><description>Trilinear 3D LUT interpolation + monochrome film grain (<see cref="Lut3DTable"/> / <see cref="GradeAndGrainBgra"/>), matching WebGL <c>sampler3D</c> (<c>colorGL.ts</c>) and FFmpeg <c>lut3d=interp=trilinear</c>.</description></item>
/// <item><description>Elliptical panel vignette mask (<see cref="VignetteMask"/> / <see cref="VignettePremulBgra"/>) and cinema letterbox bars (<see cref="LetterboxBar"/>).</description></item>
/// </list>
/// </summary>
public static class VideoColorProcessor
{
    public const int LutSize = 33;

    public static readonly IReadOnlyList<LookSpec> Looks =
    [
        new("none", "Ninguno"),
        new("brujo", "Brujo"),
        new("sepia", "Sepia"),
        new("bw", "B/N"),
        new("noir", "Noir"),
        new("warm", "Cálido"),
        new("cool", "Frío"),
        new("cine", "Cine"),
        new("vintage", "Vintage"),
        new("film", "Película"),
        new("blockbuster", "Blockbuster"),
        new("bleach", "Bleach"),
        new("golden", "Dorado"),
        new("night", "Noche"),
        new("nordic", "Nórdico"),
        new("neon", "Neón"),
        new("scifi", "Sci-fi"),
        new("desert", "Desierto"),
        new("redonly", "Solo rojo"),
        new("silver", "Plata"),
        new("bluenight", "Noche azul"),
        new("vivid", "Vivo"),
        new("ice", "Hielo"),
        new("fresh", "Fresco"),
        new("clean", "Limpio"),
        new("moody", "Moody"),
        new("cream", "Crema"),
        new("dreamy", "Soñado"),
        new("hdr", "HDR"),
    ];

    public static readonly IReadOnlyList<LetterboxSpec> LetterboxOptions =
    [
        new("off", "No", 0),
        new("1.85", "1.85", 1.85),
        new("2.00", "2.00", 2.0),
        new("2.39", "2.39", 2.39),
        new("custom", "Libre", 0),
    ];

    /// <summary>Height of each bar with the "custom" letterbox, in % of the panel height (default and range).</summary>
    public const double LetterboxHeightDefault = 12, LetterboxHeightMin = 1, LetterboxHeightMax = 40;

    static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static double Mix(double a, double b, double t) => a + (b - a) * t;
    static double Smoothstep(double a, double b, double x)
    {
        double t = Clamp01((x - a) / (b - a));
        return t * t * (3.0 - 2.0 * t);
    }
    static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
    static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    static double ToGamma(double v) => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
    static double ContrastK(double v, double k) => 0.5 + (v - 0.5) * k;
    static double SCurve(double v, double a) => Mix(v, Smoothstep(0, 1, v), a);

    static (double R, double G, double B) SatK(double r, double g, double b, double k)
    {
        double l = Luma(r, g, b);
        return (l + (r - l) * k, l + (g - l) * k, l + (b - l) * k);
    }

    static (double R, double G, double B) SplitTone(
        double r, double g, double b,
        (double R, double G, double B) sh,
        (double R, double G, double B) hi)
    {
        double l = Clamp01(Luma(r, g, b));
        double ws = (1.0 - l) * (1.0 - l);
        double wh = l * l;
        return (
            r + sh.R * ws + hi.R * wh,
            g + sh.G * ws + hi.G * wh,
            b + sh.B * ws + hi.B * wh);
    }

    public static bool IsKnownLook(string? look) => look is
        "none" or "brujo" or "sepia" or "bw" or "noir" or "warm" or "cool" or "cine" or "vintage" or
        "film" or "blockbuster" or "bleach" or "golden" or "night" or "nordic" or "neon" or "scifi" or "desert" or
        "redonly" or "silver" or "bluenight" or "vivid" or "ice" or "fresh" or "clean" or "moody" or "cream" or
        "dreamy" or "hdr";

    public static (double R, double G, double B) ApplyLook(string look, double r, double g, double b)
    {
        switch (look)
        {
            case "brujo":
                return FittedLooks.Brujo.Apply(r, g, b);
            case "redonly":
                return FittedLooks.SoloRojo.Apply(r, g, b);
            case "silver":
                return FittedLooks.Plata.Apply(r, g, b);
            case "bluenight":
                return FittedLooks.NocheAzul.Apply(r, g, b);
            case "vivid":
                return FittedLooks.Vivo.Apply(r, g, b);
            case "ice":
                return FittedLooks.Hielo.Apply(r, g, b);
            case "fresh":
            {
                // Brighter mids, a bit more color in the dull parts, clean slightly cool whites.
                var c = SatK(r, g, b, 1.1);
                c = SplitTone(c.R, c.G, c.B, (0.0, 0.005, 0.01), (-0.012, 0.0, 0.012));
                static double Lift(double v) => v + 0.22 * v * (1.0 - v) * (1.0 - v);
                return (Lift(Clamp01(c.R)), Lift(Clamp01(c.G)), Lift(Clamp01(c.B)));
            }
            case "clean":
            {
                // Barely there: a gentle S-curve, true blacks and a touch of saturation.
                var c = SatK(r, g, b, 1.06);
                return (
                    SCurve(Clamp01(c.R), 0.22),
                    SCurve(Clamp01(c.G), 0.22),
                    SCurve(Clamp01(c.B), 0.22));
            }
            case "moody":
            {
                // Darker and muted, teal-green shadows, neutral-warm highlights and a slightly matte black.
                var c = SatK(r, g, b, 0.62);
                c = SplitTone(c.R, c.G, c.B, (-0.04, 0.025, 0.035), (0.03, 0.012, -0.025));
                static double Tone(double v) => 0.03 + Math.Pow(SCurve(Clamp01(ContrastK(v, 1.12)), 0.3), 1.28) * 0.9;
                return (Tone(c.R), Tone(c.G), Tone(c.B));
            }
            case "cream":
            {
                // Soft and creamy: low contrast, lifted blacks, warm highlights, colors toned down but not grey.
                var c = SatK(r, g, b, 0.82);
                c = SplitTone(c.R, c.G, c.B, (0.012, 0.006, 0.0), (0.045, 0.025, -0.03));
                static double Tone(double v) => 0.07 + (ContrastK(Clamp01(v), 0.9) - 0.05) * 0.97;
                return (Tone(c.R), Tone(c.G), Tone(c.B));
            }
            case "dreamy":
            {
                // Soft and bright: lifted blacks, opened mids, a warm pink haze in the highlights; the glow that makes it
                // dreamy is VideoDetail's, scaled by the same intensity
                var c = SatK(r, g, b, 0.88);
                c = SplitTone(c.R, c.G, c.B, (0.01, 0.0, 0.02), (0.035, 0.012, 0.01));
                static double Tone(double v) { v = Clamp01(v); return 0.06 + (v + 0.18 * v * (1.0 - v)) * 0.94; }
                return (Tone(c.R), Tone(c.G), Tone(c.B));
            }
            case "hdr":
            {
                // Open shadows and held-back highlights with richer color; the local contrast that makes it read as HDR
                // is VideoDetail's clarity, scaled by the same intensity
                var c = SatK(r, g, b, 1.15);
                static double Tone(double v)
                {
                    v = Clamp01(v);
                    return v + 0.36 * v * (1.0 - v) * (1.0 - v) - 0.24 * v * v * (1.0 - v);
                }
                return (Tone(c.R), Tone(c.G), Tone(c.B));
            }
            case "sepia":
                return (
                    0.393 * r + 0.769 * g + 0.189 * b,
                    0.349 * r + 0.686 * g + 0.168 * b,
                    0.272 * r + 0.534 * g + 0.131 * b);
            case "bw":
            {
                double l = ContrastK(Luma(r, g, b), 1.08);
                return (l, l, l);
            }
            case "noir":
            {
                double l = ContrastK(0.45 * r + 0.45 * g + 0.1 * b, 1.45);
                double v = Math.Pow(Clamp01(l), 1.1);
                return (v, v, v);
            }
            case "warm":
            {
                double l = Luma(r, g, b);
                return (Mix(l, r, 1.1) * 1.07 + 0.01, Mix(l, g, 1.1) * 1.01, Mix(l, b, 1.1) * 0.86);
            }
            case "cool":
                return (r * 0.9, g * 0.99 + 0.005, b * 1.08 + 0.02);
            case "cine":
            {
                double l = Luma(r, g, b);
                double sh = (1.0 - l) * (1.0 - l);
                double hi = l * l;
                const double k = 1.12;
                return (
                    ContrastK(Mix(l, r, k) - 0.07 * sh + 0.07 * hi, 1.06),
                    ContrastK(Mix(l, g, k) + 0.02 * sh + 0.015 * hi, 1.06),
                    ContrastK(Mix(l, b, k) + 0.06 * sh - 0.08 * hi, 1.06));
            }
            case "vintage":
            {
                double l = Luma(r, g, b);
                double sh = (1.0 - l) * (1.0 - l);
                double dr = Mix(l, r, 0.7);
                double dg = Mix(l, g, 0.7);
                double db = Mix(l, b, 0.7);
                return (0.07 + dr * 0.88 + 0.03 * l, 0.06 + dg * 0.86 + 0.03 * sh, 0.05 + db * 0.78);
            }
            case "film":
            {
                var c = SatK(r, g, b, 0.88);
                c = SplitTone(c.R, c.G, c.B, (-0.02, 0.01, 0.035), (0.035, 0.015, -0.03));
                return (
                    0.035 + SCurve(Clamp01(c.R), 0.45) * 0.94,
                    0.035 + SCurve(Clamp01(c.G), 0.45) * 0.94,
                    0.035 + SCurve(Clamp01(c.B), 0.45) * 0.94);
            }
            case "blockbuster":
            {
                var c = SatK(r, g, b, 1.15);
                c = SplitTone(c.R, c.G, c.B, (-0.1, 0.03, 0.09), (0.09, 0.02, -0.1));
                return (
                    SCurve(Clamp01(ContrastK(c.R, 1.08)), 0.35),
                    SCurve(Clamp01(ContrastK(c.G, 1.08)), 0.35),
                    SCurve(Clamp01(ContrastK(c.B, 1.08)), 0.35));
            }
            case "bleach":
            {
                var (dr, dg, db) = SatK(r, g, b, 0.45);
                double l = Luma(r, g, b);
                double k = SCurve(Clamp01(ContrastK(l, 1.25)), 0.6) / Math.Max(l, 1e-4);
                return (
                    Mix(dr, dr * k, 0.6) * 0.98 + 0.01,
                    Mix(dg, dg * k, 0.6) * 0.98 + 0.01,
                    Mix(db, db * k, 0.6) * 0.98 + 0.01);
            }
            case "golden":
            {
                var c = SatK(r, g, b, 1.08);
                c = SplitTone(c.R, c.G, c.B, (0.02, -0.005, -0.03), (0.1, 0.05, -0.08));
                return (
                    0.025 + SCurve(Clamp01(c.R * 1.04), 0.25) * 0.96,
                    0.025 + SCurve(Clamp01(c.G * 1.0), 0.25) * 0.96,
                    0.025 + SCurve(Clamp01(c.B * 0.92), 0.25) * 0.96);
            }
            case "night":
            {
                var c = SatK(r, g, b, 0.45);
                c = (
                    Math.Pow(Clamp01(c.R), 1.35) * 0.72,
                    Math.Pow(Clamp01(c.G), 1.35) * 0.72,
                    Math.Pow(Clamp01(c.B), 1.35) * 0.72);
                c = SplitTone(c.R, c.G, c.B, (0.0, 0.025, 0.08), (-0.03, 0.0, 0.05));
                return (c.R * 0.85, c.G * 0.95, c.B * 1.15);
            }
            case "nordic":
            {
                var c = SatK(r, g, b, 0.6);
                c = SplitTone(c.R, c.G, c.B, (-0.01, 0.015, 0.04), (-0.015, 0.0, 0.02));
                return (
                    0.06 + ContrastK(Clamp01(c.R), 0.9) * 0.88,
                    0.06 + ContrastK(Clamp01(c.G), 0.9) * 0.88,
                    0.06 + ContrastK(Clamp01(c.B), 0.9) * 0.88);
            }
            case "neon":
            {
                var c = SatK(r, g, b, 1.3);
                c = SplitTone(c.R, c.G, c.B, (-0.06, 0.04, 0.12), (0.1, -0.04, 0.08));
                return (
                    SCurve(Clamp01(ContrastK(c.R, 1.12)), 0.4),
                    SCurve(Clamp01(ContrastK(c.G, 1.12)), 0.4),
                    SCurve(Clamp01(ContrastK(c.B, 1.12)), 0.4));
            }
            case "scifi":
            {
                var c = SatK(r, g, b, 0.55);
                c = SplitTone(c.R, c.G, c.B, (-0.02, 0.05, 0.0), (-0.01, 0.06, -0.03));
                return (
                    SCurve(Clamp01(ContrastK(c.R * 0.92, 1.15)), 0.3),
                    SCurve(Clamp01(ContrastK(c.G * 1.05, 1.15)), 0.3),
                    SCurve(Clamp01(ContrastK(c.B * 0.9, 1.15)), 0.3));
            }
            case "desert":
            {
                var c = SatK(r, g, b, 1.25);
                c = SplitTone(c.R, c.G, c.B, (-0.06, 0.02, 0.05), (0.12, 0.04, -0.12));
                return (
                    SCurve(Clamp01(ContrastK(c.R * 1.06, 1.15)), 0.3),
                    SCurve(Clamp01(ContrastK(c.G * 0.98, 1.15)), 0.3),
                    SCurve(Clamp01(ContrastK(c.B * 0.85, 1.15)), 0.3));
            }
            default:
                return (r, g, b);
        }
    }

    public static ExportColorSpec NormalizeGrade(ExportColorSpec? g)
    {
        if (g == null) return ExportColorSpec.Defaults;
        static double Num(double v, double lo, double hi, double d) =>
            double.IsFinite(v) ? Math.Clamp(v, lo, hi) : d;
        return new ExportColorSpec(
            On: g.On,
            Look: IsKnownLook(g.Look) ? g.Look : "none",
            Amount: Num(g.Amount, 0, 100, 100),
            Exposure: Num(g.Exposure, -100, 100, 0),
            Contrast: Num(g.Contrast, -100, 100, 0),
            Highlights: Num(g.Highlights, -100, 100, 0),
            Shadows: Num(g.Shadows, -100, 100, 0),
            Saturation: Num(g.Saturation, -100, 100, 0),
            Vibrance: Num(g.Vibrance, -100, 100, 0),
            Temperature: Num(g.Temperature, -100, 100, 0),
            Tint: Num(g.Tint, -100, 100, 0),
            Fade: Num(g.Fade, 0, 100, 0),
            Whites: Num(g.Whites, -100, 100, 0),
            Blacks: Num(g.Blacks, -100, 100, 0),
            Light: Num(g.Light, -100, 100, 0));
    }

    /// <summary>Port of <c>isNeutralGrade</c> in <c>src/shared/colorGrade.js</c>.</summary>
    public static bool IsNeutralGrade(ExportColorSpec? g)
    {
        var n = NormalizeGrade(g);
        if (!n.On) return true;
        if (n.Look != "none" && n.Amount > 0) return false;
        return n.Exposure == 0 && n.Contrast == 0 && n.Highlights == 0 && n.Shadows == 0 &&
               n.Saturation == 0 && n.Vibrance == 0 && n.Temperature == 0 && n.Tint == 0 && n.Fade == 0 &&
               n.Whites == 0 && n.Blacks == 0 && n.Light == 0;
    }

    /// <summary>
    /// Port of <c>buildLut</c> in <c>src/shared/colorGrade.js</c>:
    /// returns <c>size³ * 3</c> floats in <c>0..1</c>, red varying fastest (the <c>.cube</c> order).
    /// </summary>
    public static float[] BuildLut(ExportColorSpec? g, int size = LutSize)
    {
        var n = NormalizeGrade(g);
        double amount = n.Amount / 100.0;
        double expo = Math.Pow(2.0, (n.Exposure / 100.0) * 2.0);
        double temp = n.Temperature / 100.0;
        double tint = n.Tint / 100.0;
        double wr = 1.0 + 0.18 * temp;
        double wg = 1.0 - 0.12 * tint;
        double wb = 1.0 - 0.22 * temp;
        double c = n.Contrast / 100.0;
        double ck = c >= 0 ? 1.0 + 0.7 * c : 1.0 + 0.55 * c;
        double hl = n.Highlights / 100.0;
        double sd = n.Shadows / 100.0;
        double sat = 1.0 + n.Saturation / 100.0;
        double vib = n.Vibrance / 100.0;
        double fade = n.Fade / 100.0;
        // Whites / blacks move the ends of each channel (monotonic for the whole -100..100 range);
        // light lifts or lowers the mid-tones on the luma, leaving pure black and white in place.
        double whites = 0.15 * n.Whites / 100.0;
        double blacks = 0.12 * n.Blacks / 100.0;
        double light = 0.35 * n.Light / 100.0;
        bool lin = expo != 1.0 || temp != 0.0 || tint != 0.0;

        var data = new float[size * size * size * 3];
        int idx = 0;
        double inv = 1.0 / (size - 1);

        for (int bi = 0; bi < size; bi++)
        {
            double b0 = bi * inv;
            for (int gi = 0; gi < size; gi++)
            {
                double g0 = gi * inv;
                for (int ri = 0; ri < size; ri++)
                {
                    double r0 = ri * inv;
                    double r = r0, gg = g0, b = b0;
                    if (n.On)
                    {
                        if (amount > 0 && n.Look != "none")
                        {
                            var (lr, lg, lb) = ApplyLook(n.Look, r, gg, b);
                            r = Mix(r, Clamp01(lr), amount);
                            gg = Mix(gg, Clamp01(lg), amount);
                            b = Mix(b, Clamp01(lb), amount);
                        }
                        if (lin)
                        {
                            r = Clamp01(ToGamma(ToLinear(r) * expo * wr));
                            gg = Clamp01(ToGamma(ToLinear(gg) * expo * wg));
                            b = Clamp01(ToGamma(ToLinear(b) * expo * wb));
                        }
                        if (ck != 1.0)
                        {
                            r = ContrastK(r, ck);
                            gg = ContrastK(gg, ck);
                            b = ContrastK(b, ck);
                        }
                        if (hl != 0.0 || sd != 0.0)
                        {
                            double l = Clamp01(Luma(r, gg, b));
                            if (l > 1e-4)
                            {
                                double dL = 1.4 * sd * l * (1.0 - l) * (1.0 - l) + 1.4 * hl * l * l * (1.0 - l);
                                double k = Math.Max(0.0, l + dL) / l;
                                r *= k;
                                gg *= k;
                                b *= k;
                            }
                        }
                        if (whites != 0.0 || blacks != 0.0)
                        {
                            r = Clamp01(r + whites * Smoothstep(0.55, 1.0, r) + blacks * (1.0 - Smoothstep(0.0, 0.45, r)));
                            gg = Clamp01(gg + whites * Smoothstep(0.55, 1.0, gg) + blacks * (1.0 - Smoothstep(0.0, 0.45, gg)));
                            b = Clamp01(b + whites * Smoothstep(0.55, 1.0, b) + blacks * (1.0 - Smoothstep(0.0, 0.45, b)));
                        }
                        if (light != 0.0)
                        {
                            double l = Clamp01(Luma(r, gg, b));
                            if (l > 1e-4)
                            {
                                double k = Math.Max(0.0, l + light * l * (1.0 - l)) / l;
                                r *= k;
                                gg *= k;
                                b *= k;
                            }
                        }
                        if (sat != 1.0 || vib != 0.0)
                        {
                            double l = Luma(r, gg, b);
                            double s = Math.Max(r, Math.Max(gg, b)) - Math.Min(r, Math.Min(gg, b));
                            double k = Math.Max(0.0, sat * (1.0 + vib * (1.0 - Clamp01(s * 1.5))));
                            r = l + (r - l) * k;
                            gg = l + (gg - l) * k;
                            b = l + (b - l) * k;
                        }
                        if (fade > 0.0)
                        {
                            r = 0.13 * fade + r * (1.0 - 0.2 * fade);
                            gg = 0.13 * fade + gg * (1.0 - 0.2 * fade);
                            b = 0.13 * fade + b * (1.0 - 0.2 * fade);
                        }
                        r = Clamp01(r);
                        gg = Clamp01(gg);
                        b = Clamp01(b);
                    }
                    data[idx++] = (float)r;
                    data[idx++] = (float)gg;
                    data[idx++] = (float)b;
                }
            }
        }
        return data;
    }

    /// <summary>Port of <c>lutToCube</c> in <c>src/shared/colorGrade.js</c>.</summary>
    public static string LutToCube(ExportColorSpec? g, int size = LutSize)
    {
        float[] data = BuildLut(g, size);
        var sb = new StringBuilder(size * size * size * 28 + 128);
        sb.Append("TITLE \"GuitarCam\"\n");
        sb.Append(CultureInfo.InvariantCulture, $"LUT_3D_SIZE {size}\n");
        sb.Append("DOMAIN_MIN 0 0 0\n");
        sb.Append("DOMAIN_MAX 1 1 1\n");
        for (int i = 0; i < data.Length; i += 3)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F6} {1:F6} {2:F6}\n", data[i], data[i + 1], data[i + 2]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Samples a <c>size³</c> 3D LUT with trilinear interpolation (matching WebGL <c>sampler3D</c> in
    /// <c>colorGL.ts</c> and FFmpeg <c>lut3d=interp=trilinear</c>).
    /// </summary>
    public static (float R, float G, float B) SampleTrilinear(ReadOnlySpan<float> lut, int size, float r, float g, float b)
    {
        float scale = size - 1;
        float rx = Math.Clamp(r, 0f, 1f) * scale;
        float gx = Math.Clamp(g, 0f, 1f) * scale;
        float bx = Math.Clamp(b, 0f, 1f) * scale;

        int r0 = Math.Min(size - 2, (int)rx);
        int g0 = Math.Min(size - 2, (int)gx);
        int b0 = Math.Min(size - 2, (int)bx);

        float rf = rx - r0;
        float gf = gx - g0;
        float bf = bx - b0;

        float rf0 = 1f - rf;
        float w00 = (1f - gf) * (1f - bf);
        float w10 = gf * (1f - bf);
        float w01 = (1f - gf) * bf;
        float w11 = gf * bf;

        float w000 = rf0 * w00, w100 = rf * w00;
        float w010 = rf0 * w10, w110 = rf * w10;
        float w001 = rf0 * w01, w101 = rf * w01;
        float w011 = rf0 * w11, w111 = rf * w11;

        int sG = size * 3;
        int sB = size * sG;
        int idx = b0 * sB + g0 * sG + r0 * 3;

        int i000 = idx;
        int i100 = idx + 3;
        int i010 = idx + sG;
        int i110 = idx + sG + 3;
        int i001 = idx + sB;
        int i101 = idx + sB + 3;
        int i011 = idx + sB + sG;
        int i111 = idx + sB + sG + 3;

        float outR = lut[i000] * w000 + lut[i100] * w100 + lut[i010] * w010 + lut[i110] * w110
                   + lut[i001] * w001 + lut[i101] * w101 + lut[i011] * w011 + lut[i111] * w111;
        float outG = lut[i000 + 1] * w000 + lut[i100 + 1] * w100 + lut[i010 + 1] * w010 + lut[i110 + 1] * w110
                   + lut[i001 + 1] * w001 + lut[i101 + 1] * w101 + lut[i011 + 1] * w011 + lut[i111 + 1] * w111;
        float outB = lut[i000 + 2] * w000 + lut[i100 + 2] * w100 + lut[i010 + 2] * w010 + lut[i110 + 2] * w110
                   + lut[i001 + 2] * w001 + lut[i101 + 2] * w101 + lut[i011 + 2] * w011 + lut[i111 + 2] * w111;

        return (Math.Clamp(outR, 0f, 1f), Math.Clamp(outG, 0f, 1f), Math.Clamp(outB, 0f, 1f));
    }

    /// <summary>Port of the fragment shader <c>hash(vec2 p)</c> in <c>src/video/colorGL.ts:24-28</c>.</summary>
    public static float ShaderHash(float px, float py)
    {
        float p3x = px * 0.1031f;
        p3x -= MathF.Floor(p3x);
        float p3y = py * 0.1031f;
        p3y -= MathF.Floor(p3y);
        float p3z = p3x;
        float dot = p3x * (p3y + 33.33f) + p3y * (p3z + 33.33f) + p3z * (p3x + 33.33f);
        p3x += dot;
        p3y += dot;
        p3z += dot;
        float res = (p3x + p3y) * p3z;
        return res - MathF.Floor(res);
    }

    /// <summary>
    /// Precomputed 3D LUT table for fast 8-bit <c>Bgra8888</c> trilinear grading on the CPU.
    /// Pre-calculates the 256 base offsets and fractional weights for R, G and B bytes and scales LUT values
    /// to <c>0..255</c> so grading a <c>640×360</c> preview frame takes ~0,3–0,6 ms.
    /// </summary>
    public sealed class Lut3DTable
    {
        public ExportColorSpec Spec { get; }
        public bool IsNeutral { get; }
        public int Size { get; }
        internal readonly float[] Lut255;
        internal readonly int[] ROffset = new int[256];
        internal readonly int[] GOffset = new int[256];
        internal readonly int[] BOffset = new int[256];
        internal readonly float[] Frac = new float[256];

        public Lut3DTable(ExportColorSpec? spec, int size = LutSize)
        {
            Spec = NormalizeGrade(spec);
            IsNeutral = IsNeutralGrade(Spec);
            Size = size;
            float[] raw = BuildLut(IsNeutral ? ExportColorSpec.Defaults : Spec, size);
            Lut255 = new float[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                Lut255[i] = raw[i] * 255f;

            int sG = size * 3;
            int sB = size * sG;
            float scale = (size - 1) / 255f;
            for (int c = 0; c < 256; c++)
            {
                float x = c * scale;
                int i0 = Math.Min(size - 2, (int)x);
                ROffset[c] = i0 * 3;
                GOffset[c] = i0 * sG;
                BOffset[c] = i0 * sB;
                Frac[c] = x - i0;
            }
        }
    }

    /// <summary>
    /// <see cref="GradeAndGrainBgra"/> split in bands of rows over the thread pool, so a 640-wide preview
    /// frame costs well under a millisecond of the UI tick instead of several. Same result byte for byte.
    /// </summary>
    public static void GradeAndGrainBgraParallel(byte[] srcBgra, byte[] dstBgra, int width, int height, Lut3DTable table, int grainLevels = 0, int frameSeed = 0)
    {
        const int Band = 45;
        int bands = (height + Band - 1) / Band;
        Parallel.For(0, bands, i => GradeAndGrainBgra(srcBgra, dstBgra, width, height, table, grainLevels, frameSeed, i * Band, Math.Min(height, (i + 1) * Band)));
    }

    /// <summary>
    /// Applies the 3D LUT (trilinear interpolation) and monochrome film grain (<c>colorGL.ts</c>) to a
    /// <c>Bgra8888</c> frame buffer of <paramref name="width"/> × <paramref name="height"/>.
    /// <paramref name="firstRow"/>..<paramref name="endRow"/> limits it to a band of rows (the grain depends on
    /// the absolute row, so bands processed apart give the same picture as the whole frame).
    /// </summary>
    public static void GradeAndGrainBgra(
        ReadOnlySpan<byte> srcBgra,
        Span<byte> dstBgra,
        int width,
        int height,
        Lut3DTable table,
        int grainLevels = 0,
        int frameSeed = 0,
        int firstRow = 0,
        int endRow = -1)
    {
        if (endRow < 0 || endRow > height) endRow = height;
        int totalBytes = width * height * 4;
        if (srcBgra.Length < totalBytes || dstBgra.Length < totalBytes)
            throw new ArgumentException("Frame buffer too small for width × height × 4.");

        if (table.IsNeutral && grainLevels <= 0)
        {
            if (!srcBgra.Overlaps(dstBgra))
                srcBgra[(firstRow * width * 4)..(endRow * width * 4)].CopyTo(dstBgra[(firstRow * width * 4)..]);
            return;
        }

        float[] lut = table.Lut255;
        int[] rOff = table.ROffset;
        int[] gOff = table.GOffset;
        int[] bOff = table.BOffset;
        float[] frac = table.Frac;

        int sG = table.Size * 3;
        int sB = table.Size * sG;
        int d100 = 3;
        int d010 = sG;
        int d110 = sG + 3;
        int d001 = sB;
        int d101 = sB + 3;
        int d011 = sB + sG;
        int d111 = sB + sG + 3;

        bool hasGrain = grainLevels > 0;
        // colorGL.ts: uniform grain spread = (levels / 219.0) in 0..1 RGB -> levels * (255 / 219) in 0..255 RGB.
        float grainSpread255 = hasGrain ? grainLevels * (255f / 219f) : 0f;
        float seedOffset = (frameSeed % 1000) * 113.7f + 0.5f;
        bool neutralLut = table.IsNeutral;

        for (int y = firstRow; y < endRow; y++)
        {
            int rowBase = y * width * 4;
            float py = y + seedOffset;
            for (int x = 0; x < width; x++)
            {
                int p = rowBase + x * 4;
                byte bByte = srcBgra[p];
                byte gByte = srcBgra[p + 1];
                byte rByte = srcBgra[p + 2];
                byte aByte = srcBgra[p + 3];

                float outR, outG, outB;
                if (neutralLut)
                {
                    outR = rByte;
                    outG = gByte;
                    outB = bByte;
                }
                else
                {
                    int idx = bOff[bByte] + gOff[gByte] + rOff[rByte];
                    float rf = frac[rByte];
                    float gf = frac[gByte];
                    float bf = frac[bByte];
                    float rf0 = 1f - rf;
                    float w00 = (1f - gf) * (1f - bf);
                    float w10 = gf * (1f - bf);
                    float w01 = (1f - gf) * bf;
                    float w11 = gf * bf;

                    float w000 = rf0 * w00, w100 = rf * w00;
                    float w010 = rf0 * w10, w110 = rf * w10;
                    float w001 = rf0 * w01, w101 = rf * w01;
                    float w011 = rf0 * w11, w111 = rf * w11;

                    int i000 = idx;
                    int i100 = idx + d100;
                    int i010 = idx + d010;
                    int i110 = idx + d110;
                    int i001 = idx + d001;
                    int i101 = idx + d101;
                    int i011 = idx + d011;
                    int i111 = idx + d111;

                    outR = lut[i000] * w000 + lut[i100] * w100 + lut[i010] * w010 + lut[i110] * w110
                         + lut[i001] * w001 + lut[i101] * w101 + lut[i011] * w011 + lut[i111] * w111;
                    outG = lut[i000 + 1] * w000 + lut[i100 + 1] * w100 + lut[i010 + 1] * w010 + lut[i110 + 1] * w110
                         + lut[i001 + 1] * w001 + lut[i101 + 1] * w101 + lut[i011 + 1] * w011 + lut[i111 + 1] * w111;
                    outB = lut[i000 + 2] * w000 + lut[i100 + 2] * w100 + lut[i010 + 2] * w010 + lut[i110 + 2] * w110
                         + lut[i001 + 2] * w001 + lut[i101 + 2] * w101 + lut[i011 + 2] * w011 + lut[i111 + 2] * w111;
                }

                if (hasGrain)
                {
                    float gDelta = (ShaderHash(x + seedOffset, py) - 0.5f) * grainSpread255;
                    outR += gDelta;
                    outG += gDelta;
                    outB += gDelta;
                }

                dstBgra[p] = (byte)Math.Clamp((int)(outB + 0.5f), 0, 255);
                dstBgra[p + 1] = (byte)Math.Clamp((int)(outG + 0.5f), 0, 255);
                dstBgra[p + 2] = (byte)Math.Clamp((int)(outR + 0.5f), 0, 255);
                dstBgra[p + 3] = aByte;
            }
        }
    }

    public static ExportVideoFxSpec NormalizeVideoFx(ExportVideoFxSpec? fx)
    {
        if (fx == null) return ExportVideoFxSpec.Defaults;
        static double Clamp100(double v, double d) => double.IsFinite(v) ? Math.Clamp(v, 0, 100) : d;
        string lb = fx.Letterbox is "1.85" or "2.00" or "2.39" or "custom" ? fx.Letterbox : "off";
        return new ExportVideoFxSpec(
            On: fx.On,
            Vignette: Clamp100(fx.Vignette, 0),
            VignetteSize: Clamp100(fx.VignetteSize, 50),
            Grain: Clamp100(fx.Grain, 0),
            Letterbox: lb,
            LetterboxHeight: double.IsFinite(fx.LetterboxHeight) ? Math.Clamp(fx.LetterboxHeight, LetterboxHeightMin, LetterboxHeightMax) : LetterboxHeightDefault,
            Sharpness: Clamp100(fx.Sharpness, 0),
            Clarity: Clamp100(fx.Clarity, 0),
            Glow: Clamp100(fx.Glow, 0));
    }

    public static bool HasVignette(ExportVideoFxSpec? fx)
    {
        var n = NormalizeVideoFx(fx);
        return n.On && n.Vignette > 0;
    }

    public static int GrainLevels(ExportVideoFxSpec? fx)
    {
        var n = NormalizeVideoFx(fx);
        return n.On ? (int)Math.Round(n.Grain * 0.4, MidpointRounding.AwayFromZero) : 0;
    }

    public static int LetterboxBar(ExportVideoFxSpec? fx, int w, int h)
    {
        var n = NormalizeVideoFx(fx);
        if (!n.On) return 0;
        if (n.Letterbox == "custom") return (int)Math.Round(h * n.LetterboxHeight / 100, MidpointRounding.AwayFromZero);
        double ratio = n.Letterbox switch
        {
            "1.85" => 1.85,
            "2.00" => 2.0,
            "2.39" => 2.39,
            _ => 0,
        };
        if (ratio <= 0) return 0;
        return Math.Max(0, (int)Math.Round((h - w / ratio) / 2.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>Port of <c>vignetteMask</c> in <c>src/shared/videoFx.js</c> (1 byte per pixel, 255 = untouched).</summary>
    public static byte[] VignetteMask(ExportVideoFxSpec? fx, int w, int h)
    {
        var n = NormalizeVideoFx(fx);
        var buf = new byte[w * h];
        double amount = n.On ? (n.Vignette / 100.0) * 0.85 : 0;
        double r0 = 0.2 + 0.6 * (n.VignetteSize / 100.0);
        double r1 = r0 + 0.55;
        for (int y = 0; y < h; y++)
        {
            double v = ((y + 0.5) / h) * 2.0 - 1.0;
            for (int x = 0; x < w; x++)
            {
                double u = ((x + 0.5) / w) * 2.0 - 1.0;
                double d = Math.Sqrt((u * u + v * v) / 2.0);
                buf[y * w + x] = (byte)Math.Clamp((int)Math.Round(255.0 * (1.0 - amount * Smoothstep(r0, r1, d)), MidpointRounding.AwayFromZero), 0, 255);
            }
        }
        return buf;
    }

    /// <summary>
    /// Builds a premultiplied <c>Bgra8888</c> overlay (<c>B=0, G=0, R=0, A = 255 - mask[i]</c>) for a
    /// <paramref name="w"/> × <paramref name="h"/> panel. Drawing this overlay with standard premultiplied
    /// alpha blending produces <c>dst.rgb * (mask[i] / 255)</c>, identical to multiplying by <see cref="VignetteMask"/>.
    /// </summary>
    public static byte[] VignettePremulBgra(ExportVideoFxSpec? fx, int w, int h)
    {
        byte[] mask = VignetteMask(fx, w, h);
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < mask.Length; i++)
        {
            bgra[i * 4 + 3] = (byte)(255 - mask[i]);
        }
        return bgra;
    }

    /// <summary>
    /// Applies the panel-level vignette multiply mask and cinema letterbox bars directly to a <c>Bgra8888</c>
    /// panel buffer of <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    public static void ApplyVignetteAndLetterboxBgra(
        Span<byte> panelBgra,
        int width,
        int height,
        ExportVideoFxSpec? fx,
        byte bgR = 0x09,
        byte bgG = 0x09,
        byte bgB = 0x0b)
    {
        int totalBytes = width * height * 4;
        if (panelBgra.Length < totalBytes)
            throw new ArgumentException("Panel buffer too small for width × height × 4.");

        if (HasVignette(fx))
        {
            byte[] mask = VignetteMask(fx, width, height);
            for (int i = 0; i < mask.Length; i++)
            {
                int m = mask[i];
                if (m == 255) continue;
                int p = i * 4;
                panelBgra[p] = (byte)((panelBgra[p] * m + 127) / 255);
                panelBgra[p + 1] = (byte)((panelBgra[p + 1] * m + 127) / 255);
                panelBgra[p + 2] = (byte)((panelBgra[p + 2] * m + 127) / 255);
            }
        }

        int bar = Math.Min(height / 2, LetterboxBar(fx, width, height));
        if (bar > 0)
        {
            for (int y = 0; y < bar; y++)
            {
                FillRow(panelBgra, y * width * 4, width, bgB, bgG, bgR);
                FillRow(panelBgra, (height - 1 - y) * width * 4, width, bgB, bgG, bgR);
            }
        }
    }

    static void FillRow(Span<byte> buf, int rowOffset, int width, byte b, byte g, byte r)
    {
        for (int x = 0; x < width; x++)
        {
            int p = rowOffset + x * 4;
            buf[p] = b;
            buf[p + 1] = g;
            buf[p + 2] = r;
            buf[p + 3] = 255;
        }
    }
}
