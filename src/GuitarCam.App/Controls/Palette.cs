using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GuitarCam.App.Controls;

/// <summary>
/// The design tokens for code that draws (same values as App.axaml / the web app's index.css). The surface and
/// accent tokens are mutable brushes so the theme picker can change them live (<see cref="UiTheme.Apply"/>): code
/// that keeps one of these brushes follows the theme, code that reads a color (<see cref="AccentColor"/>, …) must
/// read it again when it draws.
/// </summary>
static class Palette
{
    static readonly SolidColorBrush app = new(Color.Parse("#0b0b0d")), panel = new(Color.Parse("#131316")),
        raised = new(Color.Parse("#1c1c20")), hover = new(Color.Parse("#26262b")), line = new(Color.Parse("#27272c")),
        fg = new(Color.Parse("#ececef")), dim = new(Color.Parse("#a1a1aa")), faint = new(Color.Parse("#6b6b74")),
        accent = new(Color.Parse("#22d3ee")), accentSoft = new(Color.Parse("#2422d3ee")), accentFg = new(Color.Parse("#0b0b0d"));

    public static Color AccentColor => accent.Color;
    public static IBrush App => app;
    public static IBrush Panel => panel;
    public static IBrush Raised => raised;
    public static IBrush Hover => hover;
    public static IBrush Line => line;
    public static IBrush Fg => fg;
    public static IBrush Dim => dim;
    public static IBrush Faint => faint;
    public static IBrush Accent => accent;
    public static IBrush AccentSoft => accentSoft;
    /// <summary>Text and icons on an accent fill: dark for light accents, white for deep ones (theme.ts accent-fg).</summary>
    public static IBrush AccentFg => accentFg;
    public static readonly Color RecColor = Color.Parse("#ef4444");
    public static readonly IBrush Rec = new ImmutableSolidColorBrush(RecColor);
    public static readonly IBrush RecSoft = Brush("#24ef4444");
    public static readonly IBrush Composition = Brush("#09090b");
    public static Color AppColor => app.Color;
    public static Color RaisedColor => raised.Color;
    public static Color HoverColor => hover.Color;
    public static Color LineColor => line.Color;
    public static Color FgColor => fg.Color;
    public static Color DimColor => dim.Color;
    public static Color FaintColor => faint.Color;

    /// <summary>Called by <see cref="UiTheme.Apply"/> only.</summary>
    internal static void Set(UiTheme.Surface s, Color accentColor, Color onAccent)
    {
        app.Color = s.App; panel.Color = s.Panel; raised.Color = s.Raised; hover.Color = s.Hover; line.Color = s.Line;
        fg.Color = s.Fg; dim.Color = s.Dim; faint.Color = s.Faint;
        accent.Color = accentColor;
        accentSoft.Color = Color.FromArgb(0x24, accentColor.R, accentColor.G, accentColor.B);
        accentFg.Color = onAccent;
    }

    /// <summary>The mixer meters' gradient (Mixer.tsx METER_BG): green, yellow near 0 dB, red at the top.</summary>
    public static readonly Color MeterGreen = Color.Parse("#22c55e");
    public static readonly Color MeterYellow = Color.Parse("#eab308");
    public static readonly Color MeterRed = Color.Parse("#ef4444");

    public static readonly Typeface Ui = new("Segoe UI Variable Text, Segoe UI");
    public static readonly Typeface UiBold = new("Segoe UI Variable Text, Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
    public static readonly Typeface Mono = new("Cascadia Mono, Consolas");
    public static readonly Typeface MonoBold = new("Cascadia Mono, Consolas", FontStyle.Normal, FontWeight.Bold);

    public static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    public static IBrush Alpha(Color c, double a) => new ImmutableSolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B));

    public static IPen Pen(IBrush b, double w = 1) => new ImmutablePen(b.ToImmutable(), w);

    public static FormattedText Text(string s, double size, IBrush brush, Typeface? face = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face ?? Ui, size, brush);

    public static void DrawText(this DrawingContext ctx, string s, double size, IBrush brush, Point at, Typeface? face = null, double maxWidth = 0)
    {
        var t = Text(s, size, brush, face);
        if (maxWidth > 0)
        {
            t.MaxTextWidth = maxWidth;
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
        }
        ctx.DrawText(t, at);
    }

    public static double ToDb(float x) => x > 1e-6 ? 20 * Math.Log10(x) : -120;
}
