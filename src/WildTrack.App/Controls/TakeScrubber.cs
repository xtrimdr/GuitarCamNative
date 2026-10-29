using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WildTrack.App.Controls;

/// <summary>
/// The Biblioteca's mini player wave: the take's peaks as thin bars, what was already heard in the accent and the rest
/// faint, plus a hover line. Click or drag to jump. It only raises <see cref="SeekRequested"/> (a fraction 0..1);
/// the window moves the preview. Without peaks yet (they are read in the background) it shows a flat line.
/// </summary>
public sealed class TakeScrubber : Control
{
    /// <summary>Bar width and gap in px: thin enough to read as a wave, wide enough to stay crisp.</summary>
    const double BarW = 2, Gap = 1;
    float[]? peaks;
    float peakMax = 1;
    double progress, hoverX = -1;
    bool dragging;

    public event Action<double>? SeekRequested;

    public TakeScrubber()
    {
        Height = 32;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void SetPeaks(float[]? p)
    {
        peaks = p;
        peakMax = 0;
        if (p != null) foreach (var v in p) peakMax = Math.Max(peakMax, v);
        if (peakMax < 1e-4f) peakMax = 1; // silence stays flat instead of blowing up the noise
        InvalidateVisual();
    }

    /// <summary>0..1 of the take; redraws only when the played edge moves by a pixel.</summary>
    public void SetProgress(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (Math.Abs((fraction - progress) * Bounds.Width) < 0.5) return;
        progress = fraction;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, mid = h / 2;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // the whole box takes the pointer
        double playedX = progress * w;
        var played = Palette.Accent;
        var rest = Palette.Alpha(Palette.FaintColor, 0.7);
        if (peaks is not { Length: > 0 } p)
        {
            ctx.FillRectangle(rest, new Rect(0, mid - 0.5, w, 1));
            ctx.FillRectangle(played, new Rect(0, mid - 0.5, playedX, 1));
        }
        else
        {
            int bars = Math.Max(1, (int)((w + Gap) / (BarW + Gap)));
            for (int i = 0; i < bars; i++)
            {
                int from = i * p.Length / bars, to = Math.Max(from + 1, (i + 1) * p.Length / bars);
                float v = 0;
                for (int k = from; k < to && k < p.Length; k++) v = Math.Max(v, p[k]);
                double bh = Math.Max(1, v / peakMax * (h - 2));
                double x = i * (BarW + Gap);
                ctx.FillRectangle(x + BarW / 2 <= playedX ? played : rest, new Rect(x, mid - bh / 2, BarW, bh));
            }
        }
        if (hoverX >= 0) ctx.FillRectangle(Palette.Fg, new Rect(Math.Round(hoverX) - 0.5, 0, 1, h));
    }

    double FractionAt(Point pt) => Bounds.Width > 0 ? Math.Clamp(pt.X / Bounds.Width, 0, 1) : 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        dragging = true;
        e.Pointer.Capture(this);
        Jump(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        hoverX = Math.Clamp(pt.X, 0, Bounds.Width);
        if (dragging) Jump(pt);
        else InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => dragging = false;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        hoverX = -1;
        InvalidateVisual();
    }

    void Jump(Point pt)
    {
        progress = FractionAt(pt);
        InvalidateVisual();
        SeekRequested?.Invoke(progress);
    }
}
