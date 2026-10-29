using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WildTrack.Audio;

namespace WildTrack.App.Controls;

/// <summary>
/// Port of Swatch in GuitarCam/src/components/ColorPanel.tsx: skin, red, green, blue and a grey ramp drawn
/// through a grade, so each look chip and template shows what it does. The web calls gradeFn directly; this
/// samples a small LUT built by the same code the preview and the export use.
/// </summary>
public sealed class ColorSwatch : Control
{
    static readonly (float R, float G, float B)[] Samples =
    [
        (0.85f, 0.62f, 0.48f),
        (0.78f, 0.16f, 0.14f),
        (0.25f, 0.55f, 0.3f),
        (0.2f, 0.38f, 0.72f),
        (0.12f, 0.12f, 0.12f),
        (0.5f, 0.5f, 0.5f),
        (0.92f, 0.92f, 0.92f),
    ];
    /// <summary>17³ is plenty for seven flat colors and keeps building a chip's LUT under a millisecond.</summary>
    const int SwatchLut = 17;
    static readonly Dictionary<ExportColorSpec, IBrush[]> Cache = [];

    IBrush[] colors = Colors(ExportColorSpec.Defaults);

    public double Radius { get; set; } = 4;

    public ExportColorSpec Spec
    {
        set
        {
            var next = Colors(value);
            if (ReferenceEquals(next, colors)) return;
            colors = next;
            InvalidateVisual();
        }
    }

    static IBrush[] Colors(ExportColorSpec spec)
    {
        if (Cache.TryGetValue(spec, out var hit)) return hit;
        var lut = VideoColorProcessor.BuildLut(spec, SwatchLut);
        var brushes = Samples.Select(s =>
        {
            var (r, g, b) = VideoColorProcessor.SampleTrilinear(lut, SwatchLut, s.R, s.G, s.B);
            static byte B8(float v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            return (IBrush)new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(B8(r), B8(g), B8(b)));
        }).ToArray();
        // Slider drags make a new grade per step; keep the cache from growing without bound.
        if (Cache.Count > 256) Cache.Clear();
        Cache[spec] = brushes;
        return brushes;
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        if (r.Width <= 0 || r.Height <= 0) return;
        using (ctx.PushClip(new RoundedRect(r, Radius)))
        {
            double w = r.Width / colors.Length;
            for (int i = 0; i < colors.Length; i++)
                ctx.FillRectangle(colors[i], new Rect(Math.Floor(i * w), 0, Math.Ceiling(w) + 1, r.Height));
        }
    }
}
