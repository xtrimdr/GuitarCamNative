using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// Port of Fader in GuitarCam/src/components/Mixer.tsx: a vertical fader with a square-root travel so unity (100%)
/// sits around 80% of the throw, like a console. Drag (Shift = fine), wheel (Shift = fine), double-click = 0 dB.
/// </summary>
public sealed class Fader : Control
{
    /// <summary>The web's VOL_MAX: +3.5 dB at the top.</summary>
    public const double VolMax = 1.5;
    /// <summary>Space above and below the travel, so the cap never leaves the control.</summary>
    const double Pad = 4;

    double value = 1, dragPos, dragY;
    IPointer? pointer;
    public event Action<double>? Changed;

    public Fader()
    {
        Width = 28;
        MinHeight = 56;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        ToolTip.SetTip(this, "Arrastrá o usá la rueda (Shift = fino) · doble click: 0 dB");
        AutomationProperties.SetName(this, "Volumen");
    }

    public static double ToPos(double v) => Math.Sqrt(Math.Max(0, v) / VolMax);
    public static double ToVol(double p) => Math.Round(VolMax * Math.Pow(Math.Clamp(p, 0, 1), 2) * 100) / 100;
    /// <summary>"−∞" below -66 dB, otherwise one decimal (Mixer.tsx fmtDb).</summary>
    public static string Db(double v) => v <= 0.0005 ? "−∞" : $"{20 * Math.Log10(v):0.0}";

    public double Value
    {
        get => value;
        set { if (this.value == value) return; this.value = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, cx = w / 2, travel = Math.Max(1, h - 2 * Pad);
        double pos = ToPos(value), unity = ToPos(1);
        ctx.DrawRectangle(Palette.Line, null, new Rect(cx - 2, Pad, 4, travel), 2, 2);
        ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.7), null, new Rect(cx - 2, h - Pad - travel * pos, 4, travel * pos), 2, 2);
        double uy = Math.Round(h - Pad - travel * unity) - 0.5;
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.FaintColor, 0.6)), new Point(0, uy), new Point(w, uy));
        double capY = h - Pad - travel * pos;
        ctx.DrawRectangle(Palette.Fg, null, new Rect(cx - 14, capY - 7, 28, 14), 2, 2);
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.AppColor, 0.6)), new Point(cx - 8, Math.Round(capY) + 0.5), new Point(cx + 8, Math.Round(capY) + 0.5));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) { Changed?.Invoke(1); return; }
        pointer = e.Pointer;
        pointer.Capture(this);
        dragY = e.GetPosition(this).Y;
        dragPos = ToPos(value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (pointer == null) return;
        double scale = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.25 : 1;
        Changed?.Invoke(ToVol(dragPos + (dragY - e.GetPosition(this).Y) / Math.Max(1, Bounds.Height) * scale));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => pointer = null;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Changed?.Invoke(ToVol(ToPos(value) + (e.Delta.Y > 0 ? 1 : -1) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.005 : 0.02)));
        e.Handled = true;
    }
}
