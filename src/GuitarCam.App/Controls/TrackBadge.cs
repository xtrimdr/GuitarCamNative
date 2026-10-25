using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GuitarCam.Projects;

namespace GuitarCam.App.Controls;

/// <summary>
/// A track's badge as a control (the inspector and the mixer; the timeline draws the same one in its headers):
/// icon or number, tinted with the track's color (<see cref="TrackIcons.DrawBadge"/>). A click asks for the
/// color and icon picker.
/// </summary>
public sealed class TrackBadge : Control
{
    (Track? Track, int Number, bool Active, string? Color, string? Icon) state;

    public double Radius { get; init; } = 4;

    public event Action? Clicked;

    public TrackBadge()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, Loc.T("look.tip"));
    }

    /// <summary>Redraws only when something it shows changed (the mixer refreshes every strip on each edit).</summary>
    public void Set(Track? t, int number, bool active)
    {
        var next = (t, number, active, t?.Color, t?.Icon);
        if (next == state) return;
        state = next;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        Clicked?.Invoke();
    }

    public override void Render(DrawingContext ctx)
    {
        if (state.Track is { } t) TrackIcons.DrawBadge(ctx, new Rect(Bounds.Size), t, state.Number, state.Active, Radius);
    }
}

/// <summary>One instrument icon on its own (the picker's buttons), painted with <see cref="Foreground"/>.</summary>
public sealed class TrackIconView : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<TrackIconView>();

    static TrackIconView() => AffectsRender<TrackIconView>(ForegroundProperty);

    public string? Icon { get; init; }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        double s = Math.Min(Bounds.Width, Bounds.Height);
        TrackIcons.Draw(ctx, Icon, new Rect((Bounds.Width - s) / 2, (Bounds.Height - s) / 2, s, s), Foreground ?? Palette.Dim);
    }
}
