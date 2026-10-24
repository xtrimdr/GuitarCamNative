using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// Port of FX_ICONS (GuitarCam/src/components/fxIcons.ts): one icon per effect, shared by the inspector and the mixer
/// so both always show the same one. Drawn from lucide's own paths (24×24 grid, 2 px round stroke, like the web)
/// because Segoe Fluent Icons has no flame or waves: Sat = Flame, Comp = Gauge, EQ = SlidersVertical,
/// Reverb = Waves, Delay = Repeat. <see cref="Create"/> also takes a Segoe glyph, so a section header accepts either.
/// </summary>
public sealed class FxIcon : Control
{
    public const string Sat = "sat", Comp = "comp", Eq = "eq", Reverb = "reverb", Delay = "delay";

    // lucide-react 1.59 paths; a relative "m" that starts a subpath is written absolute ("M… l…") so it doesn't
    // depend on where the previous subpath ended
    static readonly Dictionary<string, Geometry> Paths = new()
    {
        [Sat] = Geometry.Parse("M12 3q1 4 4 6.5t3 5.5a1 1 0 0 1-14 0 5 5 0 0 1 1-3 1 1 0 0 0 5 0c0-2-1.5-3-1.5-5q0-2 2.5-4"),
        [Comp] = Geometry.Parse("M12 14l4-4 M3.34 19a10 10 0 1 1 17.32 0"),
        [Eq] = Geometry.Parse("M10 8h4 M12 21v-9 M12 8V3 M17 16h4 M19 12V3 M19 21v-5 M3 14h4 M5 10V3 M5 21v-7"),
        [Reverb] = Geometry.Parse("M2 5q2.5 2 5 0t5 0 5 0 5 0 M2 12q2.5 2 5 0t5 0 5 0 5 0 M2 19q2.5 2 5 0t5 0 5 0 5 0"),
        [Delay] = Geometry.Parse("M17 2l4 4-4 4 M3 11v-1a4 4 0 0 1 4-4h14 M7 22l-4-4 4-4 M21 13v1a4 4 0 0 1-4 4H3"),
    };

    /// <summary>The same property as TextBlock.Foreground, so <see cref="SetColor"/> paints either kind of icon.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<FxIcon>();

    static FxIcon() => AffectsRender<FxIcon>(ForegroundProperty);

    readonly Geometry path;

    FxIcon(Geometry path) => this.path = path;

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The effect's icon for one of the keys above, otherwise a Segoe glyph. A drawn icon takes a box 2 px
    /// bigger than the glyph's font size: lucide leaves a margin inside its grid that Segoe glyphs don't.</summary>
    public static Control Create(string icon, double fontSize) => Paths.TryGetValue(icon, out var path)
        ? new FxIcon(path) { Width = fontSize + 2, Height = fontSize + 2 }
        : new TextBlock { Classes = { "icon" }, Text = icon, FontSize = fontSize };

    public static void SetColor(Control icon, IBrush brush) => icon.SetValue(TextElement.ForegroundProperty, brush);

    /// <summary>Draws an effect's icon straight into a drawing context (the timeline's track headers are hand-drawn).</summary>
    public static void Draw(DrawingContext ctx, string icon, Rect box, IBrush brush)
    {
        if (!Paths.TryGetValue(icon, out var path)) return;
        double s = box.Width / 24;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(box.X, box.Y)))
            ctx.DrawGeometry(null, Palette.Pen(brush, 2, PenLineCap.Round, PenLineJoin.Round), path);
    }

    public override void Render(DrawingContext ctx)
    {
        double s = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using (ctx.PushTransform(Matrix.CreateScale(s, s)))
            ctx.DrawGeometry(null, Palette.Pen(Foreground ?? Palette.Dim, 2, PenLineCap.Round, PenLineJoin.Round), path);
    }
}
