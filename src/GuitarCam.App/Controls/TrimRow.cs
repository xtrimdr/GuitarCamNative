using Avalonia;
using Avalonia.Controls;

namespace GuitarCam.App.Controls;

/// <summary>
/// A horizontal row whose first child gives way to the rest: the web's "truncate" name followed by "shrink-0"
/// badges (a take's name and "No usada" in Historial). A StackPanel would push the badge out instead.
/// </summary>
public sealed class TrimRow : Panel
{
    public double Spacing { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        double rest = 0, height = 0;
        foreach (var c in Children.Skip(1))
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            rest += c.DesiredSize.Width + Spacing;
            height = Math.Max(height, c.DesiredSize.Height);
        }
        if (Children.Count == 0) return default;
        Children[0].Measure(new Size(Math.Max(0, available.Width - rest), available.Height));
        height = Math.Max(height, Children[0].DesiredSize.Height);
        return new Size(Children[0].DesiredSize.Width + rest, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        foreach (var c in Children)
        {
            double w = c == Children[0] ? Math.Min(c.DesiredSize.Width, Math.Max(0, final.Width - (DesiredSize.Width - c.DesiredSize.Width))) : c.DesiredSize.Width;
            c.Arrange(new Rect(x, (final.Height - c.DesiredSize.Height) / 2, w, c.DesiredSize.Height));
            x += w + Spacing;
        }
        return final;
    }
}
