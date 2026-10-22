using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>ui.tsx range appearance: 4 px track, origin fill and 14 px white thumb, with Slider keyboard/accessibility.</summary>
public sealed class InspectorRange : Slider
{
    IPointer? pointer;
    public InspectorRange()
    {
        Template = new FuncControlTemplate<Slider>((_, _) => new Panel());
        Height = MinHeight = 18;
        Margin = new Thickness(0);
        Focusable = true;
        ValueChanged += (_, _) => InvalidateVisual();
    }
    double X(double v) => 7 + Math.Clamp((v - Minimum) / Math.Max(.001, Maximum - Minimum), 0, 1) * Math.Max(1, Bounds.Width - 14);
    public override void Render(DrawingContext ctx)
    {
        double y = Bounds.Height / 2, x = X(Value), origin = X(Minimum < 0 ? 0 : Minimum);
        ctx.DrawRectangle(Palette.Line, null, new Rect(0, y - 2, Bounds.Width, 4), 2, 2);
        ctx.DrawRectangle(Palette.Accent, null, new Rect(Math.Min(x, origin), y - 2, Math.Abs(x - origin), 4), 2, 2);
        ctx.DrawEllipse(Palette.Fg, Palette.Pen(Palette.App, 1), new Point(x, y), 7, 7);
    }
    void Move(Point p)
    {
        double raw = Minimum + Math.Clamp((p.X - 7) / Math.Max(1, Bounds.Width - 14), 0, 1) * (Maximum - Minimum);
        Value = Math.Clamp(Minimum + Math.Floor((raw - Minimum) / TickFrequency + .5) * TickFrequency, Minimum, Maximum);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); pointer = e.Pointer; pointer.Capture(this); Move(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e) { if (pointer != null) { Move(e.GetPosition(this)); e.Handled = true; } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => pointer = null;
}
