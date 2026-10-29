using Avalonia;
using Avalonia.Controls;

namespace WildTrack.App.Controls;

/// <summary>
/// Lays its child out in whatever width the parent gives it but asks for none, so a label (a folder's name above
/// its mixer strips) never widens its column: the column takes the strips' width and the label trims to fit.
/// </summary>
public sealed class NoWidth : Decorator
{
    protected override Size MeasureOverride(Size available)
    {
        Child?.Measure(available);
        return new Size(0, Child?.DesiredSize.Height ?? 0);
    }

    protected override Size ArrangeOverride(Size final)
    {
        // measured with unbounded width above: measure again in the real width so text trims
        Child?.Measure(final);
        Child?.Arrange(new Rect(final));
        return final;
    }
}
