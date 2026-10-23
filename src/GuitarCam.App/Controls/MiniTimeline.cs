using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GuitarCam.App.Model;

namespace GuitarCam.App.Controls;

/// <summary>
/// Port of GuitarCam/src/components/MiniTimeline.tsx: the whole project in one strip above the mixer, each
/// track's clips as thin bars and the playhead. Click or drag to move the playhead (not while recording).
/// The control only raises <see cref="SeekRequested"/>; the window moves the engine.
/// </summary>
public sealed class MiniTimeline : Control
{
    const double PadX = 12, BarH = 20;
    IReadOnlyList<LiveTrack> tracks = [];
    double playhead, duration;
    bool recording, scrubbing;
    public event Action<double>? SeekRequested;

    public MiniTimeline()
    {
        Height = 28;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Hacé clic o arrastrá para mover el cabezal");
    }

    public void SetTracks(IReadOnlyList<LiveTrack> list) { tracks = list; InvalidateVisual(); }

    public void SetPlayhead(double sec, double durationSec, bool isRecording)
    {
        if (sec == playhead && durationSec == duration && isRecording == recording) return;
        playhead = sec; duration = durationSec; recording = isRecording;
        Cursor = new Cursor(recording ? StandardCursorType.Arrow : StandardCursorType.Hand);
        InvalidateVisual();
    }

    // Past the clips (open-ended playback or recording) the strip grows so the playhead stays on it.
    double Span => Math.Max(1, Math.Max(duration, playhead * 1.05));
    Rect Bar => new(PadX, (Bounds.Height - BarH) / 2, Math.Max(1, Bounds.Width - 2 * PadX), BarH);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(Bounds.Size));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, Bounds.Height - 0.5), new Point(Bounds.Width, Bounds.Height - 0.5));
        var bar = Bar;
        ctx.DrawRectangle(Palette.Raised, null, bar, 2, 2);
        double span = Span;
        double X(double s) => bar.X + Math.Max(0, s) / span * bar.Width;

        var rows = tracks.Where(t => t.Clips.Count > 0).ToList();
        if (rows.Count > 0)
        {
            double rowH = Math.Max(2, Math.Min(5, 18.0 / rows.Count));
            double total = rows.Count * rowH + (rows.Count - 1);
            double y = bar.Y + (bar.Height - total) / 2;
            foreach (var t in rows)
            {
                var fill = t.Channel.Muted ? Palette.Alpha(Palette.FaintColor, 0.4) : Palette.Alpha(Palette.AccentColor, 0.45);
                foreach (var c in t.Clips)
                    ctx.DrawRectangle(fill, null, new Rect(X(c.ClipStartSec), y, Math.Max(1, X(c.ClipEndSec) - X(c.ClipStartSec)), rowH), 1, 1);
                y += rowH + 1;
            }
        }
        double px = X(playhead);
        ctx.DrawRectangle(recording ? Palette.Rec : Palette.Accent, null, new Rect(px - 1, bar.Y - 2, 2, bar.Height + 4), 1, 1);
    }

    double SecAt(Point p) => Math.Clamp((p.X - Bar.X) / Bar.Width * Span, 0, Span);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || recording) return;
        scrubbing = true;
        e.Pointer.Capture(this);
        SeekRequested?.Invoke(SecAt(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (scrubbing) SeekRequested?.Invoke(SecAt(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!scrubbing) return;
        scrubbing = false;
        e.Pointer.Capture(null);
        SeekRequested?.Invoke(SecAt(e.GetPosition(this)));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => scrubbing = false;
}
