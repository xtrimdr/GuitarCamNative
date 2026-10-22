using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GuitarCam.App.Controls;
using GuitarCam.Audio;
using GuitarCam.Projects;

namespace GuitarCam.App.Video;

/// <summary>
/// Port of the preview color grade, grain, vignette and letterbox pipeline from
/// <c>GuitarCam/src/components/SplitScreenCanvas.tsx</c> and <c>src/video/colorGL.ts</c>,
/// backed by <see cref="VideoColorProcessor"/> so the preview matches the FFmpeg MP4 export:
/// <list type="bullet">
/// <item><description>Per-frame 3D LUT trilinear grading and monochrome grain run on the decoded frame buffer in <c>LiveClip.UpdateVideo</c>, with the LUT table cached here (<see cref="GetLutTable"/>).</description></item>
/// <item><description>Panel-space elliptical vignette mask (cached per panel size and vignette settings) and cinema letterbox bars are drawn over each panel via <see cref="DrawPanelOverlays"/>.</description></item>
/// </list>
/// </summary>
public static class PreviewVideoFx
{
    static readonly object MaskGate = new();
    static ExportVideoFxSpec cachedMaskSpec = ExportVideoFxSpec.Defaults;
    static readonly Dictionary<(int W, int H), WriteableBitmap> MaskCache = [];

    static readonly object LutGate = new();
    static ExportColorSpec? cachedLutSpec;
    static VideoColorProcessor.Lut3DTable? cachedLutTable;

    public static ExportColorSpec ToSpec(ColorGrade? c) =>
        c == null
            ? ExportColorSpec.Defaults
            : VideoColorProcessor.NormalizeGrade(new ExportColorSpec(
                c.On,
                c.Look,
                c.Amount,
                c.Exposure,
                c.Contrast,
                c.Highlights,
                c.Shadows,
                c.Saturation,
                c.Vibrance,
                c.Temperature,
                c.Tint,
                c.Fade));

    public static ExportVideoFxSpec ToSpec(VideoFx? fx) =>
        fx == null
            ? ExportVideoFxSpec.Defaults
            : VideoColorProcessor.NormalizeVideoFx(new ExportVideoFxSpec(
                fx.On,
                fx.Vignette,
                fx.VignetteSize,
                fx.Grain,
                fx.Letterbox));

    /// <summary>Gets or builds the cached <see cref="VideoColorProcessor.Lut3DTable"/> for <paramref name="spec"/>.</summary>
    public static VideoColorProcessor.Lut3DTable GetLutTable(ExportColorSpec spec)
    {
        var norm = VideoColorProcessor.NormalizeGrade(spec);
        lock (LutGate)
        {
            if (cachedLutTable != null && cachedLutSpec == norm)
                return cachedLutTable;
            cachedLutSpec = norm;
            cachedLutTable = new VideoColorProcessor.Lut3DTable(norm);
            return cachedLutTable;
        }
    }

    /// <summary>
    /// Draws the panel-level vignette multiply mask and cinema letterbox bars over <paramref name="dest"/>
    /// (in the same order as <c>SplitScreenCanvas.tsx:263-275</c>: after the mirrored/cropped video image,
    /// before the video fade-to-black and panel border/label).
    /// </summary>
    public static void DrawPanelOverlays(DrawingContext ctx, Rect dest, VideoFx? videoFx) =>
        DrawPanelOverlays(ctx, dest, ToSpec(videoFx));

    /// <summary>
    /// Overload taking an <see cref="ExportVideoFxSpec"/> directly.
    /// </summary>
    public static void DrawPanelOverlays(DrawingContext ctx, Rect dest, ExportVideoFxSpec fx)
    {
        if (dest.Width <= 1 || dest.Height <= 1) return;
        var norm = VideoColorProcessor.NormalizeVideoFx(fx);
        if (!norm.On) return;

        int pw = Math.Clamp((int)Math.Round(dest.Width, MidpointRounding.AwayFromZero), 2, 960);
        int ph = Math.Clamp((int)Math.Round(dest.Height, MidpointRounding.AwayFromZero), 2, 960);

        if (VideoColorProcessor.HasVignette(norm))
        {
            var maskBmp = GetOrCreateVignetteBitmap(norm, pw, ph);
            ctx.DrawImage(maskBmp, new Rect(0, 0, pw, ph), dest);
        }

        int barPx = VideoColorProcessor.LetterboxBar(norm, pw, ph);
        if (barPx > 0)
        {
            double barH = Math.Min(dest.Height * 0.5, barPx * (dest.Height / ph));
            if (barH > 0.5)
            {
                ctx.FillRectangle(Palette.Composition, new Rect(dest.X, dest.Y, dest.Width, barH));
                ctx.FillRectangle(Palette.Composition, new Rect(dest.X, dest.Bottom - barH, dest.Width, barH));
            }
        }
    }

    static WriteableBitmap GetOrCreateVignetteBitmap(ExportVideoFxSpec norm, int w, int h)
    {
        var keySpec = norm with { Grain = 0, Letterbox = "off" };
        lock (MaskGate)
        {
            if (cachedMaskSpec != keySpec)
            {
                foreach (var old in MaskCache.Values) old.Dispose();
                MaskCache.Clear();
                cachedMaskSpec = keySpec;
            }
            if (MaskCache.TryGetValue((w, h), out var existing))
                return existing;

            byte[] bgra = VideoColorProcessor.VignettePremulBgra(keySpec, w, h);
            var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bmp.Lock())
            {
                int row = w * 4;
                for (int y = 0; y < h; y++)
                    Marshal.Copy(bgra, y * row, fb.Address + y * fb.RowBytes, row);
            }
            MaskCache[(w, h)] = bmp;
            return bmp;
        }
    }
}
