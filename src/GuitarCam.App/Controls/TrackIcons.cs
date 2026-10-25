using Avalonia;
using Avalonia.Media;
using GuitarCam.Projects;

namespace GuitarCam.App.Controls;

/// <summary>
/// The tracks' instrument icons and colors (native only, asked by the user): IconOven's stroke icons (MIT, generated
/// into TrackIcons.Data.cs by tools/make-track-icons.py) because Segoe has no instruments and lucide only a handful;
/// they share lucide's 24×24 grid and round strokes, so they sit well beside <see cref="FxIcon"/>. The same badge
/// (<see cref="DrawBadge"/>) shows a track in the timeline, the inspector and the mixer: its icon, else its number,
/// tinted with its color, else the accent.
/// </summary>
public static partial class TrackIcons
{
    /// <summary>The picker's groups, in order (names in Loc as "icon.cat.&lt;id&gt;").</summary>
    public static readonly string[] Categories = ["strings", "drums", "keys", "winds", "more"];

    /// <summary>The track colors: a ring of hues bright enough for the dark surfaces, stored in the project as hex.</summary>
    public static readonly string[] Colors =
        ["#f87171", "#fb923c", "#fbbf24", "#a3e635", "#4ade80", "#2dd4bf", "#38bdf8", "#60a5fa", "#818cf8", "#a78bfa", "#e879f9", "#f472b6"];

    /// <summary>At small sizes a 1.5 stroke of the 24 grid gets thinner than a pixel; no line is drawn thinner than this.</summary>
    const double MinStrokePx = 1.25;

    static readonly Dictionary<string, (double Width, Geometry Path)[]> parsed = [];

    public static IEnumerable<string> InCategory(string category) => Data.Where(d => d.Category == category).Select(d => d.Id);

    public static bool Exists(string? id) => id != null && Data.Any(d => d.Id == id);

    public static string Name(string id) => Loc.T("icon." + id);

    /// <summary>The track's color, or null when it has none (an unknown value counts as none).</summary>
    public static Color? ColorOf(Track t) => t.Color != null && Color.TryParse(t.Color, out var c) ? c : null;

    static (double Width, Geometry Path)[]? Strokes(string? id)
    {
        if (id == null) return null;
        if (parsed.TryGetValue(id, out var hit)) return hit;
        foreach (var d in Data)
            if (d.Id == id) return parsed[id] = d.Strokes.Select(s => (s.Width, Geometry.Parse(s.Data))).ToArray();
        return null;
    }

    /// <summary>Draws an icon into a square box; false if there is no such icon.</summary>
    public static bool Draw(DrawingContext ctx, string? id, Rect box, IBrush brush)
    {
        if (Strokes(id) is not { } strokes) return false;
        double s = box.Width / 24;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(box.X, box.Y)))
            foreach (var (w, path) in strokes)
                ctx.DrawGeometry(null, Palette.Pen(brush, Math.Max(w, MinStrokePx / s), PenLineCap.Round, PenLineJoin.Round), path);
        return true;
    }

    /// <summary>
    /// A track's badge: its icon, or its number when it has none. With a color the badge is tinted with it; without
    /// one it is the accent when <paramref name="active"/> (selected, or the inspector's take) and quiet otherwise.
    /// </summary>
    public static void DrawBadge(DrawingContext ctx, Rect r, Track t, int number, bool active, double radius)
    {
        var color = ColorOf(t);
        var fill = color is { } c ? Palette.Alpha(c, active ? 0.28 : 0.18) : active ? Palette.AccentSoft : Palette.Hover;
        var fg = color is { } k ? Palette.Alpha(k, 1) : active ? Palette.Accent : Palette.Dim;
        ctx.DrawRectangle(fill, null, r, radius, radius);
        double size = Math.Round(r.Height * 0.86);
        if (Draw(ctx, t.Icon, new Rect(r.Center.X - size / 2, r.Center.Y - size / 2, size, size), fg)) return;
        var num = Palette.Text(number.ToString(), r.Height >= 24 ? 12 : 11, fg, r.Height >= 24 ? Palette.MonoBold : Palette.Mono);
        ctx.DrawText(num, new Point(r.Center.X - num.Width / 2, r.Center.Y - num.Height / 2));
    }
}
