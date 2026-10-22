using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>CompControl.tsx amount knob; ui.tsx drag, wheel, fine adjustment and reset behavior.</summary>
public sealed class InspectorKnob : Control
{
    double value, initial;
    Point down;
    IPointer? pointer;
    public event Action<double>? Changed;
    public InspectorKnob() { Width = Height = 64; Focusable = true; }
    public void Set(double v) { value = v; InvalidateVisual(); }
    void Change(double v) => Changed?.Invoke(Math.Round(Math.Clamp(v, 0, 1), 3));
    public override void Render(DrawingContext ctx)
    {
        var center = new Point(32, 32);
        ctx.DrawEllipse(Palette.Raised, Palette.Pen(Palette.Line, 4), center, 27, 27);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i <= 60; i++)
            {
                double a = (135 + i * 270 * value / 60) * Math.PI / 180;
                var p = new Point(32 + 27 * Math.Cos(a), 32 + 27 * Math.Sin(a));
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, Palette.Pen(Palette.Accent, 4), geo);
        double angle = (135 + value * 270) * Math.PI / 180;
        ctx.DrawLine(Palette.Pen(Palette.Fg, 2), center,
            new Point(32 + 19 * Math.Cos(angle), 32 + 19 * Math.Sin(angle)));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { Change(0.5); return; }
        down = e.GetPosition(this); initial = value; pointer = e.Pointer; pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (pointer == null) return;
        var p = e.GetPosition(this);
        initial = Math.Clamp(initial + (down.Y - p.Y) / 160 * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1), 0, 1);
        down = p;
        Change(initial);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => pointer = null;
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Change(value + Math.Sign(e.Delta.Y) / 50.0 * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1)); e.Handled = true;
    }
}
