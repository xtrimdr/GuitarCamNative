using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using GuitarCam.App.Controls;
using GuitarCam.App.Dialogs;

namespace GuitarCam.App;

/// <summary>
/// The window shown while <see cref="MainWindow"/> is being built: the icon, the name, the version (with the
/// commit, and "PRUEBA" in a test copy) and a sliding bar. The bar is a composition animation, so it keeps moving
/// on the render thread while the UI thread is busy building the main window. Colors come from the theme, so it
/// follows the accent the user picked.
/// </summary>
public sealed partial class SplashWindow : Window
{
    /// <summary>Shortest time it stays up, so a fast start does not just flash it.</summary>
    static readonly TimeSpan MinShown = TimeSpan.FromMilliseconds(900);

    /// <summary>Matches the card's opacity transition in the XAML.</summary>
    const int FadeMs = 220;

    /// <summary>One pass of the stripe along the bottom edge.</summary>
    static readonly TimeSpan StripePass = TimeSpan.FromSeconds(1.3);

    readonly DateTime shownAt = DateTime.UtcNow;

    public SplashWindow()
    {
        InitializeComponent();
        Logo.Source = LoadLogo();
        VersionText.Text = Loc.T("about.version", AboutDialog.AppVersion());
        if (TestBuild.Label != null)
        {
            TestChip.IsVisible = true;
            TestText.Text = Loc.T("splash.test");
        }

        var accent = Palette.AccentColor;
        Glow.Fill = new RadialGradientBrush
        {
            GradientStops = [new(Color.FromArgb(0x30, accent.R, accent.G, accent.B), 0), new(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)],
        };
        Stripe.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = [new(Color.FromArgb(0, accent.R, accent.G, accent.B), 0), new(accent, 0.5), new(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)],
        };

        Opened += (_, _) =>
        {
            Card.Opacity = 1;
            StartStripe();
        };
    }

    void StartStripe()
    {
        var visual = ElementComposition.GetElementVisual(Stripe);
        if (visual == null) return;
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, new Vector3((float)-Stripe.Width, 0, 0));
        anim.InsertKeyFrame(1f, new Vector3((float)Track.Bounds.Width, 0, 0));
        anim.Duration = StripePass;
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Offset", anim);
    }

    /// <summary>Fades out and closes, after <see cref="MinShown"/> at least.</summary>
    public async Task Dismiss()
    {
        var wait = MinShown - (DateTime.UtcNow - shownAt);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        Card.Opacity = 0;
        await Task.Delay(FadeMs);
        Close();
    }

    /// <summary>The largest frame of the app icon (the .ico's 256 px one is a PNG), so the logo stays sharp.</summary>
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
