using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GuitarCam.Audio;

namespace GuitarCam.App.Controls;

/// <summary>
/// The networks' length marks (native only, asked by the user): a dashed line across the lanes where each chosen
/// limit falls (<see cref="SocialLengths"/>), with its name just under the ruler, on the side that fits. A limit the
/// song (or the take being recorded) already passed turns amber and says so; the stories' mark repeats at every cut
/// the song needs, numbering the pieces. Labels are built once per mark and piece, never per frame.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>The amber of the mute and the meters' −6 to 0 dB: "careful", not the red of recording.</summary>
    static readonly Color Over = Color.Parse("#f59e0b");
    static readonly ImmutableDashStyle MarkDash = new([4, 3], 0);
    IPen? markPen, overPen;
    Color markPenColor;

    IReadOnlyList<SocialLength> socialMarks = [];
    /// <summary>Per mark id: [normal, over] for a single limit; for the repeating ones piece k (from 1) at
    /// [2(k - 1)] with its networks and [2(k - 1) + 1] without them (when the pieces are close), grown as the song grows.</summary>
    readonly Dictionary<string, List<string>> socialLabels = [];

    /// <summary>The marks the user chose (the window sets them from config.json); empty = none.</summary>
    public IReadOnlyList<SocialLength> SocialMarks
    {
        get => socialMarks;
        set { socialMarks = value; socialLabels.Clear(); InvalidateVisual(); }
    }

    string SocialLabel(SocialLength s, int i)
    {
        if (!socialLabels.TryGetValue(s.Id, out var list)) socialLabels[s.Id] = list = [];
        if (s.Repeats)
            while (list.Count <= i)
            {
                int k = list.Count / 2 + 1;
                list.Add(Loc.T("social.piece", k));
                list.Add(Loc.T("social.piece.short", k));
            }
        else if (list.Count == 0)
        {
            string name = Loc.T($"social.short.{s.Id}"), at = SocialLengths.Clock(s.Sec);
            list.Add(Loc.T("social.mark", name, at));
            list.Add(Loc.T("social.over", name, at));
        }
        return list[i];
    }

    void DrawSocialMarks(DrawingContext ctx, double W)
    {
        if (socialMarks.Count == 0) return;
        if (markPen == null || markPenColor != Palette.FgColor)
        {
            markPenColor = Palette.FgColor;
            markPen = new ImmutablePen(Color.FromArgb(90, markPenColor.R, markPenColor.G, markPenColor.B).ToUInt32(), 1, MarkDash);
            overPen = new ImmutablePen(Color.FromArgb(200, Over.R, Over.G, Over.B).ToUInt32(), 1, MarkDash);
        }
        // the take being recorded counts: you see it pass the line while you play
        double end = Math.Max(LengthSec, Recording ? Playhead : 0);
        for (int row = 0; row < socialMarks.Count; row++)
        {
            var s = socialMarks[row];
            int n = s.Repeats ? Math.Max(1, (int)Math.Ceiling(end / s.Sec - 1e-6)) : 1;
            bool over = !s.Repeats && end > s.Sec + 1e-6;
            for (int k = 1; k <= n; k++)
            {
                double x = Math.Round(X(k * s.Sec)) + 0.5;
                if (x < HeaderW) continue;
                if (x > W) break;
                ctx.DrawLine(over ? overPen! : markPen!, new Point(x, RulerH), new Point(x, Bounds.Height));
                // the name sits left of the line, over what fits; with close pieces, without the networks, or left out
                var brush = over ? Palette.Brush("#f59e0b") : Palette.Dim;
                var label = Palette.Text(SocialLabel(s, s.Repeats ? 2 * (k - 1) : over ? 1 : 0), 11, brush, Palette.Ui);
                if (s.Repeats && s.Sec * pps < label.Width + 18)
                    label = Palette.Text(SocialLabel(s, 2 * (k - 1) + 1), 11, brush, Palette.Ui);
                double w = label.Width + 10;
                if (s.Repeats && s.Sec * pps < w + 8) continue;
                // one row per mark, so two limits at the same time (3:00) don't cover each other
                var box = new Rect(x - 3 - w, RulerH + 4 + row * (label.Height + 8), w, label.Height + 4);
                ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.85), Palette.Pen(over ? Palette.Alpha(Over, 0.6) : Palette.Line), box, 4, 4);
                ctx.DrawText(label, new Point(box.X + 5, box.Y + 2));
            }
        }
    }
}
