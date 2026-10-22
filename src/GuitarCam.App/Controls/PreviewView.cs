using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GuitarCam.App.Model;

namespace GuitarCam.App.Controls;

/// <summary>
/// The composition: each take's video in its panel (shared layout math with the export), cover-fit with
/// the take's zoom/pan. Frames are 1 per second for now (decoded video comes later).
/// </summary>
public sealed class PreviewView : Control
{
    IReadOnlyList<Track> tracks = [];
    string layout = "side-by-side";
    double aspectW = 16, aspectH = 9;
    double time;

    public PreviewView()
    {
        ClipToBounds = true;
    }

    static readonly RenderOptions Smooth = new() { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality };

    public void SetSession(IReadOnlyList<Track> list, Session s)
    {
        tracks = list;
        layout = s.Layout;
        aspectW = s.AspectW;
        aspectH = s.AspectH;
        InvalidateVisual();
    }

    public void SetTime(double sec)
    {
        time = sec;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        using var _ = ctx.PushRenderOptions(Smooth);
        ctx.FillRectangle(Palette.App, new Rect(0, 0, b.Width, b.Height));
        double pad = 18;
        double aw = b.Width - 2 * pad, ah = b.Height - 2 * pad;
        if (aw <= 0 || ah <= 0) return;
        double scale = Math.Min(aw / aspectW, ah / aspectH);
        double fw = aspectW * scale, fh = aspectH * scale;
        var frame = new Rect((b.Width - fw) / 2, (b.Height - fh) / 2, fw, fh);
        ctx.DrawRectangle(Palette.Composition, Palette.Pen(Palette.Line), frame.Inflate(1));

        // panels in a 1080-high canvas, like the export, then scaled to the frame
        int W = (int)Math.Round(1080 * aspectW / aspectH), H = 1080;
        var rects = PanelLayout.Compute(tracks.Count, layout, W, H);
        double k = fw / W;
        for (int i = 0; i < rects.Count; i++)
        {
            var (x, y, w, h) = rects[i];
            var dest = new Rect(frame.X + x * k, frame.Y + y * k, w * k, h * k);
            var t = tracks[i];
            var bmp = t.FrameAt(time);
            if (bmp != null)
            {
                // cover-fit, then zoom around the pan point (panX/panY in -1..1)
                double sw = bmp.Size.Width, sh = bmp.Size.Height;
                double cover = Math.Max(dest.Width / sw, dest.Height / sh) * Math.Max(1, t.Take.Zoom);
                double cw = Math.Min(sw, dest.Width / cover), ch = Math.Min(sh, dest.Height / cover);
                double cx = (sw - cw) / 2 * (1 + t.Take.PanX), cy = (sh - ch) / 2 * (1 + t.Take.PanY);
                var src = new Rect(Math.Clamp(cx, 0, Math.Max(0, sw - cw)), Math.Clamp(cy, 0, Math.Max(0, sh - ch)), cw, ch);
                using (ctx.PushClip(dest))
                {
                    if (t.Take.Mirror)
                    {
                        using (ctx.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(2 * dest.X + dest.Width, 0)))
                            ctx.DrawImage(bmp, src, dest);
                    }
                    else ctx.DrawImage(bmp, src, dest);
                }
            }
            else if (t.FramesDir == null)
            {
                var msg = Palette.Text("Cargando video…", 11, Palette.Faint);
                ctx.DrawText(msg, new Point(dest.Center.X - msg.Width / 2, dest.Center.Y - msg.Height / 2));
            }
            var label = Palette.Text(t.Take.Name, 10.5, Palette.Fg, Palette.UiBold);
            var lr = new Rect(dest.X + 8, dest.Bottom - label.Height - 14, label.Width + 12, label.Height + 6);
            ctx.DrawRectangle(Palette.Alpha(Colors.Black, 0.55), null, lr, 4, 4);
            ctx.DrawText(label, new Point(lr.X + 6, lr.Y + 3));
        }
    }
}
