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
/// redraws this when one changes. Audio-only and empty tracks, and tracks whose first clip has no
/// video (the export skips them), get no panel.
/// Selection, pan and wheel zoom are ports of SplitScreenCanvas.tsx; the window owns the clip edits.
/// The color grade and grain come already applied in each clip's Frame; vignette and letterbox are
/// drawn here per panel (<see cref="PreviewVideoFx"/>). Like the web, the video fades only apply while
/// playing or recording (<see cref="Timed"/>); the gap between panels is the export's border. A video transition
/// between two clips of a track (<see cref="LiveTrack.Transition"/>) draws both, weighed like the export, also while
/// paused, so you can stop in the middle of one and see it.
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
    /// <summary>The web's isPlaying || isRecording: panels follow their video fades.</summary>
    public bool Timed { get; set; }
    /// <summary>The selected track, whose panel gets the accent border.</summary>
    public Func<LiveTrack?>? SelectedTrack { get; set; }
    /// <summary>The count-in beat (1..4) before a take, drawn big over the whole stage; null otherwise.</summary>
    public int? CountdownBeat { get; set; }
    public event Action<LiveTrack, LiveClip>? ClipSelected;
    public event Action<LiveClip, double?, double?, double?>? FramingRequested;

    public PreviewView()
    {
        ClipToBounds = true;
        ToolTip.SetTip(this, Loc.T("insp.frame.hint"));
    }

    /// <summary>ExportJobSpec.BorderSize: the gap the export leaves around each panel, in output pixels.</summary>
    const int ExportBorder = 4;

    static readonly RenderOptions Smooth = new() { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality };

    public void SetProject(IReadOnlyList<LiveTrack> list, ProjectSettings s)
    {
        CancelGesture();
        // same takes as ExportDialog.ExtractVideoTakes: a video track whose first clip has a picture
        tracks = list.Where(t => t.HasPanel && !string.IsNullOrEmpty(t.Clips[0].Media?.Video)).ToList();
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
        DrawComposition(ctx);
        if (CountdownBeat is { } beat) DrawCountdown(ctx, beat);
    }

    /// <summary>
    /// A clip's picture in its panel, cover-fit and zoomed around the pan point, with its vignette, at
    /// <paramref name="alpha"/> (a transition's weight) times its own video fade while playing. Past its edges (a
    /// crossfade's overhang) its fade is the one at the edge, as the export's fade filters leave it.
    /// </summary>
    void DrawPicture(DrawingContext ctx, LiveClip clip, Rect dest, double alpha)
    {
        var bmp = clip.Frame!;
        // cover-fit, then zoom around the pan point (panX/panY in -1..1)
        double sw = bmp.Size.Width, sh = bmp.Size.Height;
        var fr = clip.Clip.Framing;
        double cover = Math.Max(dest.Width / sw, dest.Height / sh) * Math.Max(1, fr.Zoom);
        double cw = Math.Min(sw, dest.Width / cover), ch = Math.Min(sh, dest.Height / cover);
        double cx = (sw - cw) / 2 * (1 + fr.PanX), cy = (sh - ch) / 2 * (1 + fr.PanY);
        var src = new Rect(Math.Clamp(cx, 0, Math.Max(0, sw - cw)), Math.Clamp(cy, 0, Math.Max(0, sh - ch)), cw, ch);
        // sharpness and clarity size their radius by what the panel shows (the next tick filters again if it changed)
        clip.VisibleHeight = ch;
        var c = clip.Clip;
        var window = ClipEditing.Window(c, clip.Media, clip.LengthSec);
        var fades = ClipEnvelope.Fades(window, c.FadeInSec, c.FadeOutSec, c.LinkVideoFades, c.VideoFadeInSec, c.VideoFadeOutSec).Video;
        double fade = Timed ? ClipEnvelope.GainAt(Math.Clamp(Playhead, window.Start, window.End), window.Start, window.End, fades.FadeIn, fades.FadeOut) : 1;
        // the fade goes towards the composition background, like the export's fade=color
        using (ctx.PushOpacity(fade * alpha))
        {
            if (fr.Mirror)
            {
                using (ctx.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(2 * dest.X + dest.Width, 0)))
                    ctx.DrawImage(bmp, src, dest);
            }
            else ctx.DrawImage(bmp, src, dest);
            PreviewVideoFx.DrawVignette(ctx, dest, settings?.VideoFx);
        }
    }

    static readonly Typeface MonoBold = new("Cascadia Mono, Consolas", FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>SplitScreenCanvas.tsx's countdown overlay: black at 60 % over the stage, the beat at 120 px and four dots.</summary>
    void DrawCountdown(DrawingContext ctx, int beat)
    {
        var b = Bounds;
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), new Rect(0, 0, b.Width, b.Height));
        const double size = 120, gap = 12, dot = 10, dotGap = 8;
        var num = Palette.Text(beat.ToString(), size, Palette.Fg, MonoBold);
        double top = (b.Height - size - gap - dot) / 2;
        // leading-none: the digit's box is the font size, centered in the text's line height
        ctx.DrawText(num, new Point((b.Width - num.Width) / 2, top - (num.Height - size) / 2));
        double x = (b.Width - (4 * dot + 3 * dotGap)) / 2, y = top + size + gap;
        for (int i = 1; i <= 4; i++, x += dot + dotGap)
            ctx.DrawEllipse(i <= beat ? Palette.Accent : Palette.Hover, null, new Rect(x, y, dot, dot));
    }

    void DrawComposition(DrawingContext ctx)
    {
        var b = Bounds;
        using var _ = ctx.PushRenderOptions(Smooth);
        ctx.FillRectangle(Palette.App, new Rect(0, 0, b.Width, b.Height));
        var frame = CompositionRect();
        if (frame.Width <= 0 || frame.Height <= 0) return;
        ctx.DrawRectangle(Palette.Composition, Palette.Pen(Palette.Line), frame.Inflate(1));
        if (tracks.Count == 0)
        {
            var title = Palette.Text("La composición aparece cuando termines el primer canal", 12.5, Palette.Dim, Palette.UiBold);
            var hint = Palette.Text("Mientras grabás, la cámara se ve en el monitor \"En vivo\"", 11, Palette.Faint);
            ctx.DrawText(title, new Point(frame.Center.X - title.Width / 2, frame.Center.Y - title.Height - 2));
            ctx.DrawText(hint, new Point(frame.Center.X - hint.Width / 2, frame.Center.Y + 4));
            return;
        }

        // panels in a 1080-high canvas, like the export, then scaled to the frame
        int W = (int)Math.Round(1080 * aspectW / aspectH), H = 1080;
        var rects = PanelLayout.Compute(tracks.Count, layout, W, H);
        double k = frame.Width / W;
        double pad = rects.Count > 1 ? ExportBorder * frame.Width / Mp4Exporter.ExportSize(settings?.Aspect).Width : 0;
        var selected = SelectedTrack?.Invoke();
        for (int i = 0; i < rects.Count; i++)
        {
            var (x, y, w, h) = rects[i];
            var dest = new Rect(frame.X + x * k, frame.Y + y * k, w * k, h * k).Deflate(pad);
            var t = tracks[i];
            var clip = t.ShownClip;
            // a video transition shows the clip going out and the one coming in (ClipTransitions, like the export)
            LiveClip? under = null;
            double alpha = 1;
            if (t.Transition is { } j && j.Out < t.Clips.Count && j.In < t.Clips.Count)
            {
                var (wOut, wIn) = ClipTransitions.Weights(j, t.TransitionAt);
                if (j.Kind == TransitionKind.Crossfade) (under, clip, alpha) = (t.Clips[j.Out], t.Clips[j.In], wIn);
                else (clip, alpha) = t.TransitionAt < j.At ? (t.Clips[j.Out], wOut) : (t.Clips[j.In], wIn);
            }
            bool top = clip is { Fresh: true, Frame: not null }, below = under is { Fresh: true, Frame: not null };
            if (top || below)
            {
                using (ctx.PushClip(dest))
                {
                    if (below) DrawPicture(ctx, under!, dest, 1);
                    if (top) DrawPicture(ctx, clip!, dest, alpha);
                    PreviewVideoFx.DrawLetterbox(ctx, dest, settings?.VideoFx);
                }
            }
            else if (clip is { Video: not null })
            {
                ctx.FillRectangle(Palette.Panel, dest);
                var msg = Palette.Text("Cargando video…", 12.5, Palette.Dim, Palette.UiBold);
                ctx.DrawText(msg, new Point(dest.Center.X - msg.Width / 2, dest.Center.Y - msg.Height / 2));
            }
            if (t == selected)
            {
                // 3 px of the web's 720-high canvas
                double bw = Math.Max(1.5, 4.5 * k);
                ctx.DrawRectangle(new Pen(Palette.Accent, bw), dest.Deflate(bw / 2));
            }
            var label = Palette.Text(t.Track.Name, 10.5, Palette.Fg, Palette.UiBold);
            var lr = new Rect(dest.X + 8, dest.Bottom - label.Height - 14, label.Width + 12, label.Height + 6);
            ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, lr, 6, 6);
            ctx.DrawText(label, new Point(lr.X + 6, lr.Y + 3));
        }
    }
}
