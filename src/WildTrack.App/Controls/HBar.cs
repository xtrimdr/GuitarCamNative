using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WildTrack.App.Controls;

/// <summary>
/// A thin horizontal value bar for the compact mixer strip (asked by the user, like a console's send and pan rows): a
/// send's level filled from the left with its bus's name written over it, or the pan filled from the centre
/// (<see cref="Bipolar"/>). Drag sideways (relative, Shift = fine), wheel (Shift = fine), double-click = its
/// <see cref="ResetTo"/>. Draws from <see cref="Palette"/>'s caches: a redraw allocates nothing.
/// </summary>
public sealed class HBar : Control
{
    /// <summary>Px of drag for the whole range.</summary>
    const double DragPx = 160;
    /// <summary>Px below which nothing is drawn (the value mark needs w − 4 ≥ 1 and the bar h − 4 &gt; 0).</summary>
    const double MinSize = 6;

    double value, dragValue, dragX;
    IPointer? pointer;
    bool hover;

    /// <summary>From the pan's centre (−1..1) instead of from the left (0..1).</summary>
    public bool Bipolar { get; init; }
    public double ResetTo { get; init; }
    /// <summary>Written left (a send's bus); <see cref="Readout"/> goes right.</summary>
    public string Label { get; set { field = value; InvalidateVisual(); } } = "";
    public string Readout { get; set { field = value; InvalidateVisual(); } } = "";
    /// <summary>Off, the fill goes grey (a send that is off still shows its level).</summary>
    public bool Lit { get; set { field = value; InvalidateVisual(); } } = true;

    public event Action<double>? Changed;

    public HBar()
    {
        Height = 16;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    double Min => Bipolar ? -1 : 0;

    public double Value
    {
        get => value;
        set { if (this.value == value) return; this.value = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        // a strip that was just added can render once before it is laid out (zero size): the clamps below need room
        if (w < MinSize || h < MinSize) return;
        var box = new Rect(0, 0, w, h);
        ctx.DrawRectangle(hover ? Palette.Hover : Palette.Raised, Palette.Pen(Palette.Line), box.Deflate(0.5), 3, 3);
        var fill = Lit ? Palette.Alpha(Palette.AccentColor, Bipolar ? 0.85 : 0.32) : Palette.Alpha(Palette.FaintColor, 0.3);
        double x = (Math.Clamp(value, Min, 1) - Min) / (1 - Min) * (w - 2) + 1;
        if (Bipolar)
        {
            double cx = Math.Round(w / 2);
            ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.FaintColor, 0.7)), new Point(cx - 0.5, 2), new Point(cx - 0.5, h - 2));
            // a short bar from the centre to the value, with a mark at the value
            ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.3), new Rect(Math.Min(cx, x), 2, Math.Abs(x - cx), h - 4));
            ctx.FillRectangle(fill, new Rect(Math.Clamp(x - 1.5, 1, w - 4), 1, 3, h - 2));
        }
        else if (x > 1) ctx.DrawRectangle(fill, null, new Rect(1, 1, x - 1, h - 2), 2, 2);
        var fg = Lit ? Palette.Fg : Palette.Faint;
        double right = w - 4;
        // a bar too narrow for its readout keeps only its fill
        if (Readout.Length > 0 && Palette.Text(Readout, 10, Palette.Dim, Palette.Mono) is var r && r.Width + 8 <= w)
        {
            right -= r.Width;
            ctx.DrawText(r, new Point(right, (h - r.Height) / 2));
            right -= 4;
        }
        if (Label.Length > 0 && right > 12)
        {
            var t = Palette.Text(Label, 10.5, fg, Palette.Ui);
            using (ctx.PushClip(new Rect(4, 0, right - 4, h)))
                ctx.DrawText(t, new Point(4, (h - t.Height) / 2));
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); hover = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); hover = false; InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) { Changed?.Invoke(ResetTo); return; }
        pointer = e.Pointer;
        pointer.Capture(this);
        dragX = e.GetPosition(this).X;
        dragValue = value;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (pointer == null) return;
        double x = e.GetPosition(this).X;
        dragValue = Math.Clamp(dragValue + (x - dragX) / DragPx * (1 - Min) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1), Min, 1);
        dragX = x;
        Changed?.Invoke(Snap(dragValue));
    }

    // only the release of its own drag: a right click goes on to whatever holds the bar (a send's menu)
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (pointer == null) return;
        pointer.Capture(null);
        pointer = null;
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => pointer = null;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        double step = (1 - Min) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.005 : 0.025);
        Changed?.Invoke(Snap(Math.Clamp(value + Math.Sign(e.Delta.Y) * step, Min, 1)));
        e.Handled = true;
    }

    /// <summary>Thousandths; the pan sticks to the centre (|x| &lt; 0,04, like the knobs).</summary>
    double Snap(double v) => Bipolar && Math.Abs(v) < 0.04 ? 0 : Math.Round(v * 1000) / 1000;
}
