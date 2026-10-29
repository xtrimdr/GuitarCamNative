using Avalonia;
using Avalonia.Media;
using WildTrack.Projects;

namespace WildTrack.App.Controls;

/// <summary>
/// The tracks' instrument icons and colors (native only, asked by the user): Fluent Emoji High Contrast's filled
/// shapes (MIT, generated into TrackIcons.Data.cs by tools/make-track-icons.py), picked by the user over IconOven's
/// thin strokes because they hold up when the badge grows with the header column. The same badge
/// (<see cref="DrawBadge"/>) shows a track in the timeline, the inspector and the mixer: its icon, else the icon of its
/// kind (audio, MIDI, video) with its number small in the corner, tinted with its color, else the accent.
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

    /// <summary>
    /// The kind of a track without an icon (asked by the user, so the badge says more than its number), stroked like
    /// <see cref="FxIcon"/> on lucide's 24×24 grid: "audio-waveform", "video", and three piano keys for MIDI.
    /// </summary>
    static readonly Geometry KindAudio = Geometry.Parse("M2 13a2 2 0 0 0 2-2V7a2 2 0 0 1 4 0v13a2 2 0 0 0 4 0V4a2 2 0 0 1 4 0v13a2 2 0 0 0 4 0v-4a2 2 0 0 1 2-2"),
        KindMidi = Geometry.Parse("M5 5h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2z M9 13v6 M15 13v6 M7.5 5v8h3V5 M13.5 5v8h3V5"),
        KindVideo = Geometry.Parse("M16 13l5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5 M4 6h10a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2z");
    /// <summary>How much of the badge the kind icon takes, and its margin from the top left corner: the number goes in
    /// the corner left free.</summary>
    const double KindFill = 0.52, KindInset = 0.1;

    public static IEnumerable<string> InCategory(string category) => Data.Where(d => d.Category == category).Select(d => d.Id);

    /// <summary>The icon a stored id shows today (see <see cref="Legacy"/>); null when it shows none.</summary>
    public static string? Resolve(string? id) => id != null && Legacy.TryGetValue(id, out var now) ? now : id;

    public static bool Exists(string? id) => Resolve(id) is { } r && Data.Any(d => d.Id == r);

    public static string Name(string id) => Loc.T("icon." + id);

    /// <summary>The track's color, or null when it has none (an unknown value counts as none).</summary>
    public static Color? ColorOf(Track t) => ColorOf(t.Color);

    /// <summary>A stored "#rrggbb" as a color, or null (none, or an unknown value).</summary>
    public static Color? ColorOf(string? hex) => hex != null && Color.TryParse(hex, out var c) ? c : null;

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
    /// A track's badge: its icon, or the icon of its kind with its number small in the bottom right corner when it has
    /// none (a video track only while the project shows <paramref name="video"/>). With a color the badge is tinted with
    /// it; without one it is the accent when <paramref name="active"/> (selected, or the inspector's take) and quiet otherwise.
    /// </summary>
    public static void DrawBadge(DrawingContext ctx, Rect r, Track t, int number, bool active, double radius, bool video)
    {
        var tone = DrawBadgeBack(ctx, r, ColorOf(t), active, radius);
        var fg = Palette.Alpha(tone, 1);
        double size = Math.Round(r.Height * BadgeFill);
        if (Draw(ctx, t.Icon, new Rect(r.Center.X - size / 2, r.Center.Y - size / 2, size, size), fg)) return;

        var kind = t.IsMidi ? KindMidi : video && t.Video ? KindVideo : KindAudio;
        double ks = Math.Round(r.Height * KindFill), inset = Math.Round(r.Height * KindInset), s = ks / 24;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(r.X + inset, r.Y + inset)))
            ctx.DrawGeometry(null, Palette.Pen(fg, 2, PenLineCap.Round, PenLineJoin.Round), kind);
        var num = Palette.Text(number.ToString(), Math.Max(8, Math.Round(r.Height * 0.3)), Palette.Alpha(tone, 0.75), Palette.Mono);
        ctx.DrawText(num, new Point(r.Right - inset - num.Width, r.Bottom - inset * 0.6 - num.Baseline));
    }

    /// <summary>
    /// A bus's badge, the same as a track's: its icon tinted with its color, or without an icon the effect icon of its
    /// first reverb or delay (<paramref name="fx"/>, an <see cref="FxIcon"/> key). A bus has no number.
    /// </summary>
    public static void DrawBusBadge(DrawingContext ctx, Rect r, string? color, string? icon, string fx, bool active, double radius)
    {
        var fg = Palette.Alpha(DrawBadgeBack(ctx, r, ColorOf(color), active, radius), 1);
        double size = Math.Round(r.Height * BadgeFill);
        if (Draw(ctx, icon, new Rect(r.Center.X - size / 2, r.Center.Y - size / 2, size, size), fg)) return;
        double fs = Math.Round(r.Height * BusFxFill);
        FxIcon.Draw(ctx, fx, new Rect(r.Center.X - fs / 2, r.Center.Y - fs / 2, fs, fs), fg);
    }

    /// <summary>How much of a bus's badge its effect icon fills (lucide's grid already leaves a margin).</summary>
    const double BusFxFill = 0.7;

    /// <summary>The badge's background: its color, else the accent when active, else quiet. Returns the tone of what goes on it.</summary>
    static Color DrawBadgeBack(DrawingContext ctx, Rect r, Color? color, bool active, double radius)
    {
        var fill = color is { } c ? Palette.Alpha(c, active ? 0.28 : 0.18) : active ? Palette.AccentSoft : Palette.Hover;
        ctx.DrawRectangle(fill, null, r, radius, radius);
        return color ?? (active ? Palette.AccentColor : Palette.DimColor);
    }
}
