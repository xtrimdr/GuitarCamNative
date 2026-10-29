using Avalonia;
using Avalonia.Controls;

namespace WildTrack.App.Controls;

/// <summary>
/// A toolbar that never overlaps (asked by the user: "se solapa todo"): its first child sits on the left and the rest
/// are packed to the right, like a DockPanel, but when they don't fit the groups that don't go down to a new row (right
/// aligned), so the bar grows instead of drawing one group over another. Each child is a whole group: it is never cut.
/// </summary>
public sealed class ToolbarPanel : Panel
{
    /// <summary>Space between groups in a row, and between rows.</summary>
    public double Spacing { get; set; } = 12;
    public double RowSpacing { get; set; } = 6;

    /// <summary>The rows as child index ranges, from the last measure.</summary>
    readonly List<(int From, int To, double Height)> rows = [];

    protected override Size MeasureOverride(Size available)
    {
        foreach (var c in Children) c.Measure(Size.Infinity);
        Pack(available.Width);
        double w = 0, h = 0;
        foreach (var (from, to, height) in rows)
        {
            w = Math.Max(w, RowWidth(from, to));
            h += height;
        }
        h += Math.Max(0, rows.Count - 1) * RowSpacing;
        return new Size(double.IsInfinity(available.Width) ? w : Math.Min(w, available.Width), h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Pack(final.Width);
        double y = 0;
        foreach (var (from, to, height) in rows)
        {
            double x = final.Width;
            // right to left: the groups after the first go against the right edge
            for (int i = to - 1; i >= from; i--)
            {
                var c = Children[i];
                if (!c.IsVisible) continue;
                var d = c.DesiredSize;
                if (i == 0) x = 0;
                else x -= d.Width;
                c.Arrange(new Rect(x, y + (height - d.Height) / 2, i == 0 ? Math.Min(d.Width, Math.Max(0, final.Width)) : d.Width, d.Height));
                x -= Spacing;
            }
            y += height + RowSpacing;
        }
        return final;
    }

    double RowWidth(int from, int to)
    {
        double w = 0;
        int n = 0;
        for (int i = from; i < to; i++)
        {
            if (!Children[i].IsVisible) continue;
            w += Children[i].DesiredSize.Width;
            n++;
        }
        return w + Math.Max(0, n - 1) * Spacing;
    }

    /// <summary>Greedy rows: a group that would not fit next to the ones before starts a new row.</summary>
    void Pack(double width)
    {
        rows.Clear();
        int from = 0;
        double used = 0, height = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            if (!c.IsVisible) continue;
            var d = c.DesiredSize;
            double need = used == 0 ? d.Width : used + Spacing + d.Width;
            if (used > 0 && need > width)
            {
                rows.Add((from, i, height));
                from = i;
                used = d.Width;
                height = d.Height;
                continue;
            }
            used = need;
            height = Math.Max(height, d.Height);
        }
        if (from < Children.Count) rows.Add((from, Children.Count, height));
    }
}
