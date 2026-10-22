using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GuitarCam.App.Model;
using GuitarCam.App.Video;
using GuitarCam.Audio;
using GuitarCam.Projects;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App.Controls;

/// <summary>
/// The composition: one panel per video track (shared layout math with the export), showing the clip
/// under the playhead, cover-fit with its framing. Each clip's decoder fills its Frame; the window
/// redraws this when one changes. Audio-only and empty tracks get no panel.
/// Selection, pan and wheel zoom are ports of SplitScreenCanvas.tsx; the window owns the clip edits.
/// The color grade and grain come already applied in each clip's Frame; vignette and letterbox are
/// drawn here per panel (<see cref="PreviewVideoFx"/>).
/// </summary>
public sealed class PreviewView : Control
{
    IReadOnlyList<LiveTrack> tracks = [];
    string layout = "side-by-side";
    ProjectSettings? settings;
    double aspectW = 16, aspectH = 9;
    LiveClip? dragged;
    Point down;
    double panX, panY;
    IPointer? pointer;
    public double Playhead { get; set; }
    public event Action<LiveTrack, LiveClip>? ClipSelected;
    public event Action<LiveClip, double?, double?, double?>? FramingRequested;

    public PreviewView()
    {
        ClipToBounds = true;
        ToolTip.SetTip(this, "Arrastrá el panel en el video para mover el encuadre; la rueda del mouse hace zoom.");
    }

    static readonly RenderOptions Smooth = new() { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality };

    public void SetProject(IReadOnlyList<LiveTrack> list, ProjectSettings s)
    {
        CancelGesture();
        tracks = list.Where(t => t.HasPanel).ToList();
        settings = s;
        layout = s.Layout;
        (aspectW, aspectH) = s.AspectWH();
        InvalidateVisual();
    }

    Rect CompositionRect()
    {
        const double pad = 18;
        double scale = Math.Min(Math.Max(0, Bounds.Width - 2 * pad) / aspectW, Math.Max(0, Bounds.Height - 2 * pad) / aspectH);
        return new Rect((Bounds.Width - aspectW * scale) / 2, (Bounds.Height - aspectH * scale) / 2, aspectW * scale, aspectH * scale);
    }

    (LiveTrack Track, LiveClip Clip)? Hit(Point p)
    {
        var frame = CompositionRect();
        if (!frame.Contains(p) || frame.Width <= 0) return null;
        int width = (int)Math.Round(1080 * aspectW / aspectH);
        var panels = PanelLayout.Compute(tracks.Count, layout, width, 1080);
        double k = frame.Width / width;
        for (int i = panels.Count - 1; i >= 0; i--)
        {
            var (x, y, w, h) = panels[i];
            if (!new Rect(frame.X + x * k, frame.Y + y * k, w * k, h * k).Contains(p)) continue;
            var clip = tracks[i].ShownClip ?? tracks[i].Clips.FirstOrDefault();
            if (clip != null) return (tracks[i], clip);
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Hit(e.GetPosition(this)) is not { } hit) return;
        ClipSelected?.Invoke(hit.Track, hit.Clip);
        dragged = hit.Clip;
        down = e.GetPosition(this);
        panX = dragged.Clip.Framing.PanX;
        panY = dragged.Clip.Framing.PanY;
        pointer = e.Pointer;
        pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        Cursor = new Cursor(Hit(p) != null ? StandardCursorType.Hand : StandardCursorType.Arrow);
        if (dragged == null) return;
        var (x, y) = InspectorEditing.Pan(panX, panY, p.X - down.X, p.Y - down.Y);
        FramingRequested?.Invoke(dragged, null, x, y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        CancelGesture();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragged = null;
        pointer = null;
    }

    public void CancelGesture()
    {
        dragged = null;
        var captured = pointer;
        pointer = null;
        captured?.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0 || Hit(e.GetPosition(this)) is not { } hit) return;
        FramingRequested?.Invoke(hit.Clip, InspectorEditing.Zoom(hit.Clip.Clip.Framing.Zoom, e.Delta.Y > 0), null, null);
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        using var _ = ctx.PushRenderOptions(Smooth);
        ctx.FillRectangle(Palette.App, new Rect(0, 0, b.Width, b.Height));
        var frame = CompositionRect();
        if (frame.Width <= 0 || frame.Height <= 0) return;
        ctx.DrawRectangle(Palette.Composition, Palette.Pen(Palette.Line), frame.Inflate(1));

        // panels in a 1080-high canvas, like the export, then scaled to the frame
        int W = (int)Math.Round(1080 * aspectW / aspectH), H = 1080;
        var rects = PanelLayout.Compute(tracks.Count, layout, W, H);
        double k = frame.Width / W;
        for (int i = 0; i < rects.Count; i++)
        {
            var (x, y, w, h) = rects[i];
            var dest = new Rect(frame.X + x * k, frame.Y + y * k, w * k, h * k);
            var t = tracks[i];
            var clip = t.ShownClip;
            var bmp = clip?.Frame;
            if (bmp != null)
            {
                // cover-fit, then zoom around the pan point (panX/panY in -1..1)
                double sw = bmp.Size.Width, sh = bmp.Size.Height;
                var fr = clip!.Clip.Framing;
                double cover = Math.Max(dest.Width / sw, dest.Height / sh) * Math.Max(1, fr.Zoom);
                double cw = Math.Min(sw, dest.Width / cover), ch = Math.Min(sh, dest.Height / cover);
                double cx = (sw - cw) / 2 * (1 + fr.PanX), cy = (sh - ch) / 2 * (1 + fr.PanY);
                var src = new Rect(Math.Clamp(cx, 0, Math.Max(0, sw - cw)), Math.Clamp(cy, 0, Math.Max(0, sh - ch)), cw, ch);
                var c = clip.Clip;
                var window = ClipEditing.Window(c, clip.Media, clip.LengthSec);
                var fades = ClipEnvelope.Fades(window, c.FadeInSec, c.FadeOutSec, c.LinkVideoFades, c.VideoFadeInSec, c.VideoFadeOutSec).Video;
                using var opacity = ctx.PushOpacity(ClipEnvelope.GainAt(Playhead, window.Start, window.End, fades.FadeIn, fades.FadeOut));
                using (ctx.PushClip(dest))
                {
                    if (fr.Mirror)
                    {
                        using (ctx.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(2 * dest.X + dest.Width, 0)))
                            ctx.DrawImage(bmp, src, dest);
                    }
                    else ctx.DrawImage(bmp, src, dest);
                    // vignette and letterbox sit on the picture, under the fade (same order as the export)
                    PreviewVideoFx.DrawPanelOverlays(ctx, dest, settings?.VideoFx);
                }
            }
            else if (clip is { Video: not null })
            {
                var msg = Palette.Text("Cargando video…", 11, Palette.Faint);
                ctx.DrawText(msg, new Point(dest.Center.X - msg.Width / 2, dest.Center.Y - msg.Height / 2));
            }
            var label = Palette.Text(t.Track.Name, 10.5, Palette.Fg, Palette.UiBold);
            var lr = new Rect(dest.X + 8, dest.Bottom - label.Height - 14, label.Width + 12, label.Height + 6);
            ctx.DrawRectangle(Palette.Alpha(Colors.Black, 0.55), null, lr, 4, 4);
            ctx.DrawText(label, new Point(lr.X + 6, lr.Y + 3));
        }
    }
}
