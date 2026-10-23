using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// Port of Knob in GuitarCam/src/components/ui.tsx (the mixer's knobs): a 270° arc lit from the reset value to the
/// current one, drag up/down (relative, Shift = fine), wheel (50 steps over the range, Shift = fine) and double-click
/// to reset. Big knobs (≥ 32 px) get a filled cap and a thicker pointer, like the web.
/// Avalonia only hit-tests what is painted (the web's svg takes its whole box), so the knob paints a transparent
/// square <see cref="HitPad"/> px bigger than itself to catch the pointer anywhere around it, and its track lights up
/// while hovered or dragged.
/// </summary>
public sealed class Knob : Control
{
    /// <summary>Extra px around the knob that still grab it: small knobs are hard to hit with the guitar on.</summary>
    const double HitPad = 6;

    double value, dragValue, dragY;
    IPointer? pointer;

    public double Min { get; init; } = -1;
    public double Max { get; init; } = 1;
    public double ResetTo { get; init; }
    public double Size { get; }
    public event Action<double>? Changed;

    public Knob(double size = 22, string? tip = null)
    {
        Size = size;
        Width = Height = size;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        Tip = tip;
    }

    public string? Tip
    {
        set { ToolTip.SetTip(this, value); AutomationProperties.SetName(this, value); }
    }

    public double Value
    {
        get => value;
        set { if (this.value == value) return; this.value = value; InvalidateVisual(); }
    }

    bool Big => Size >= 32;
    double Round(double v) => Math.Round(Math.Clamp(v, Min, Max) * 1000) / 1000;

    public override void Render(DrawingContext ctx)
    {
        double stroke = Big ? Math.Max(3, Size / 12) : 2.5;
        double r = Size / 2 - stroke, c = Size / 2;
        Point Polar(double deg, double rad)
        {
            double a = (deg - 90) * Math.PI / 180;
            return new Point(c + rad * Math.Cos(a), c + rad * Math.Sin(a));
        }
        StreamGeometry Arc(double from, double to)
        {
            var g = new StreamGeometry();
            using var s = g.Open();
            s.BeginFigure(Polar(from, r), false);
            s.ArcTo(Polar(to, r), new Size(r, r), 0, Math.Abs(to - from) > 180, SweepDirection.Clockwise);
            s.EndFigure(false);
            return g;
        }
        IPen Round(IBrush b, double w) => new Pen(b, w) { LineCap = PenLineCap.Round };

        double angle = -135 + (value - Min) / (Max - Min) * 270;
        double origin = -135 + (ResetTo - Min) / (Max - Min) * 270;
        bool hot = IsPointerOver || pointer != null;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size).Inflate(HitPad));
        if (Big) ctx.DrawEllipse(Palette.Raised, Palette.Pen(hot ? Palette.Faint : Palette.Line), new Point(c, c), r - stroke * 1.1, r - stroke * 1.1);
        ctx.DrawGeometry(null, Round(hot ? Palette.Faint : Palette.Line, stroke), Arc(-135, 135));
        if (Math.Abs(angle - origin) > 0.5)
            ctx.DrawGeometry(null, Round(Palette.Accent, stroke), Arc(Math.Min(origin, angle), Math.Max(origin, angle)));
        ctx.DrawLine(Round(Palette.Fg, Big ? 2.5 : 1.5), Polar(angle, Big ? r * 0.35 : 0), Polar(angle, Big ? r - stroke * 1.6 : r));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) { Changed?.Invoke(ResetTo); return; }
        Focus();
        pointer = e.Pointer;
        pointer.Capture(this);
        dragY = e.GetPosition(this).Y;
        dragValue = value;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (pointer == null) return;
        double y = e.GetPosition(this).Y;
        // relative steps, so pressing Shift mid-drag doesn't jump
        double next = dragValue + (dragY - y) / (Big ? 160 : 120) * (Max - Min) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1);
        dragY = y;
        dragValue = Math.Clamp(next, Min, Max);
        Changed?.Invoke(Round(next));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; e.Handled = true; InvalidateVisual(); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { pointer = null; InvalidateVisual(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPointerOverProperty) InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        double step = (Max - Min) / 50 * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1) * (e.Delta.Y > 0 ? 1 : -1);
        Changed?.Invoke(Round(value + step));
        e.Handled = true;
    }
}
