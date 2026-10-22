using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GuitarCam.App.Controls;

/// <summary>The design tokens for code that draws (same values as App.axaml / the web app's index.css).</summary>
static class Palette
{
    public static readonly Color AccentColor = Color.Parse("#22d3ee");
    public static readonly IBrush App = Brush("#0b0b0d");
    public static readonly IBrush Panel = Brush("#131316");
    public static readonly IBrush Raised = Brush("#1c1c20");
    public static readonly IBrush Hover = Brush("#26262b");
    public static readonly IBrush Line = Brush("#27272c");
    public static readonly IBrush Fg = Brush("#ececef");
    public static readonly IBrush Dim = Brush("#a1a1aa");
    public static readonly IBrush Faint = Brush("#6b6b74");
    public static readonly IBrush Accent = new ImmutableSolidColorBrush(AccentColor);
    public static readonly IBrush AccentSoft = Brush("#2422d3ee");
    public static readonly Color RecColor = Color.Parse("#ef4444");
    public static readonly IBrush Rec = new ImmutableSolidColorBrush(RecColor);
    public static readonly IBrush RecSoft = Brush("#24ef4444");
    public static readonly IBrush Composition = Brush("#09090b");

    public static readonly Typeface Ui = new("Segoe UI Variable Text, Segoe UI");
    public static readonly Typeface UiBold = new("Segoe UI Variable Text, Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
    public static readonly Typeface Mono = new("Cascadia Mono, Consolas");

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
