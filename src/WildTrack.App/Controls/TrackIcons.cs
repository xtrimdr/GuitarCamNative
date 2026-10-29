using Avalonia;
using Avalonia.Media;
using WildTrack.Projects;

namespace WildTrack.App.Controls;

/// <summary>
/// The tracks' instrument icons and colors (native only, asked by the user): Fluent Emoji High Contrast's filled
/// shapes (MIT, generated into TrackIcons.Data.cs by tools/make-track-icons.py), picked by the user over IconOven's
/// thin strokes because they hold up when the badge grows with the header column. The same badge
/// (<see cref="DrawBadge"/>) shows a track in the timeline, the inspector and the mixer: its icon, else its number,
/// tinted with its color, else the accent.
/// </summary>
public static partial class TrackIcons
{
    /// <summary>The picker's groups, in order (names in Loc as "icon.cat.&lt;id&gt;").</summary>
    public static readonly string[] Categories = ["strings", "drums", "keys", "winds", "more"];

    /// <summary>The track colors: a rainbow ring of hues bright enough for the dark surfaces, stored in the project as
    /// hex. The picker shows it in two rows of <see cref="ColorsPerRow"/>; the first twelve hues it had are still here,
    /// with the same hex, so saved projects keep their colors.</summary>
    public static readonly string[] Colors =
    [
        "#f87171", "#fb7f5a", "#fb923c", "#fba82e", "#fbbf24", "#facc15", "#cfdc24", "#a3e635", "#76e25a", "#4ade80", "#34d399", "#2dd4bf",
        "#22d3ee", "#38bdf8", "#60a5fa", "#7298f9", "#818cf8", "#a78bfa", "#c084fc", "#d47dfa", "#e879f9", "#ee76d8", "#f472b6", "#fb7185",
    ];

    /// <summary>How many colors go on each row of the picker.</summary>
    public const int ColorsPerRow = 12;

    /// <summary>The grid the icons are drawn on.</summary>
    const double Grid = 32;
    /// <summary>How much of the badge the icon fills: Fluent's shapes already carry a little padding of their own.</summary>
    const double BadgeFill = 0.76;

    /// <summary>Ids of the previous icon set (IconOven) that are gone, mapped to the closest one now: projects may still
    /// store them.</summary>
    static readonly Dictionary<string, string> Legacy = new()
    {
        ["ukulele"] = "guitar", ["mandolin"] = "banjo", ["sitar"] = "banjo", ["cello"] = "violin",
        ["snare-drum"] = "drum-kit", ["conga"] = "drum", ["djembe"] = "drum",
        ["drum-machine"] = "synthesizer", ["drum-pads"] = "synthesizer", ["piano-keys"] = "piano",
        ["recorder-flute"] = "flute", ["pan-flute"] = "flute", ["oboe"] = "clarinet", ["bassoon"] = "clarinet",
        ["harmonica"] = "accordion", ["microphone-usb"] = "microphone",
    };

    static readonly Dictionary<string, Geometry[]> parsed = [];

    public static IEnumerable<string> InCategory(string category) => Data.Where(d => d.Category == category).Select(d => d.Id);

    /// <summary>The icon a stored id shows today (see <see cref="Legacy"/>); null when it shows none.</summary>
    public static string? Resolve(string? id) => id != null && Legacy.TryGetValue(id, out var now) ? now : id;

    public static bool Exists(string? id) => Resolve(id) is { } r && Data.Any(d => d.Id == r);

    public static string Name(string id) => Loc.T("icon." + id);

    /// <summary>The track's color, or null when it has none (an unknown value counts as none).</summary>
    public static Color? ColorOf(Track t) => t.Color != null && Color.TryParse(t.Color, out var c) ? c : null;

    static Geometry[]? Shapes(string? id)
    {
        if (Resolve(id) is not { } r) return null;
        if (parsed.TryGetValue(r, out var hit)) return hit;
        foreach (var d in Data)
            if (d.Id == r) return parsed[r] = d.Paths.Select(Geometry.Parse).ToArray();
        return null;
    }

    /// <summary>Draws an icon into a square box; false if there is no such icon.</summary>
    public static bool Draw(DrawingContext ctx, string? id, Rect box, IBrush brush)
    {
        if (Shapes(id) is not { } shapes) return false;
        double s = box.Width / Grid;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(box.X, box.Y)))
            foreach (var path in shapes)
                ctx.DrawGeometry(brush, null, path);
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
        double size = Math.Round(r.Height * BadgeFill);
        if (Draw(ctx, t.Icon, new Rect(r.Center.X - size / 2, r.Center.Y - size / 2, size, size), fg)) return;
        double fontSize = r.Height >= 32 ? Math.Round(r.Height * 0.42) : r.Height >= 24 ? 12 : 11;
        var num = Palette.Text(number.ToString(), fontSize, fg, r.Height >= 24 ? Palette.MonoBold : Palette.Mono);
        ctx.DrawText(num, new Point(r.Center.X - num.Width / 2, r.Center.Y - num.Height / 2));
    }
}
