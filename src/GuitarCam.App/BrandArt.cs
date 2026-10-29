using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GuitarCam.App.Controls;

namespace GuitarCam.App;

/// <summary>
/// What the splash and "Acerca de" share: the big logo, the accent glow behind it and the accent line. The brushes
/// are built from the current accent each time (the theme can change it while the app runs), so callers ask again
/// when they show up instead of keeping them.
/// </summary>
static class BrandArt
{
    static Bitmap? logo;

    /// <summary>The largest frame of the app icon (the .ico's 256 px one is a PNG), so the logo stays sharp.</summary>
    public static Bitmap? Logo => logo ??= LoadLogo();

    /// <summary>A soft accent halo that fades out from the center.</summary>
    public static IBrush Glow()
    {
        var a = Palette.AccentColor;
        return new RadialGradientBrush { GradientStops = [new(WithAlpha(a, 0x30), 0), new(WithAlpha(a, 0), 1)] };
    }

    /// <summary>Accent in the middle, fading to nothing at both ends.</summary>
    public static IBrush Line()
    {
        var a = Palette.AccentColor;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = [new(WithAlpha(a, 0), 0), new(a, 0.5), new(WithAlpha(a, 0), 1)],
        };
    }

    static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    static Bitmap? LoadLogo()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://GuitarCam/Assets/GuitarCam.ico"));
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var d = ms.GetBuffer();
            int count = BitConverter.ToUInt16(d, 4), best = -1, bestSize = 0;
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16, size = d[e] == 0 ? 256 : d[e];
                if (size > bestSize) { bestSize = size; best = e; }
            }
            if (best < 0) return null;
            int len = BitConverter.ToInt32(d, best + 8), off = BitConverter.ToInt32(d, best + 12);
            return new Bitmap(new MemoryStream(d, off, len));
        }
        catch (Exception) // a bad frame must never stop the app from starting
        {
            return new Bitmap(AssetLoader.Open(new Uri("avares://GuitarCam/Assets/GuitarCam-64.png")));
        }
    }
}
