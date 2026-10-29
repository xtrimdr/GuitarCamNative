using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WildTrack.Projects;

namespace WildTrack.App.Controls;

/// <summary>
/// A track's badge as a control (the inspector and the mixer; the timeline draws the same one in its headers):
/// icon or the icon of its kind with its number, tinted with the track's color (<see cref="TrackIcons.DrawBadge"/>).
/// A bus shows the same badge (<see cref="SetBus"/>, <see cref="TrackIcons.DrawBusBadge"/>). A click asks for the
/// color and icon picker.
/// </summary>
public sealed class TrackBadge : Control
{
    (Track? Track, int Number, bool Active, string? Color, string? Icon, bool Video) state;
    /// <summary>A bus's look (<see cref="SetBus"/>); <c>Fx</c> is null while it shows a track.</summary>
    (string? Fx, string? Color, string? Icon, bool Active) bus;

    public double Radius { get; init; } = 4;

    public event Action? Clicked;

    public TrackBadge()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, Loc.T("look.tip"));
    }

    /// <summary>Redraws only when something it shows changed (the mixer refreshes every strip on each edit).</summary>
    public void Set(Track? t, int number, bool active, bool video)
    {
        var next = (t, number, active, t?.Color, t?.Icon, video && t?.Video == true);
        if (next == state && bus.Fx == null) return;
        state = next;
        bus = default;
        InvalidateVisual();
    }

    /// <summary>Shows a bus: its color and icon, or <paramref name="fx"/> (an <see cref="FxIcon"/> key) without an icon.</summary>
    public void SetBus(string? color, string? icon, string fx, bool active)
    {
        var next = ((string?)fx, color, icon, active);
        if (next == bus) return;
        bus = next;
        state = default;
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
        if (bus.Fx is { } fx) TrackIcons.DrawBusBadge(ctx, new Rect(Bounds.Size), bus.Color, bus.Icon, fx, bus.Active, Radius);
        else if (state.Track is { } t) TrackIcons.DrawBadge(ctx, new Rect(Bounds.Size), t, state.Number, state.Active, Radius, state.Video);
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
