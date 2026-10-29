using System.Numerics;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using WildTrack.App.Dialogs;

namespace WildTrack.App;

/// <summary>
/// The window shown while <see cref="MainWindow"/> is being built: the icon, the name, the version (with the
/// commit, and "PRUEBA" in a test copy) and a sliding bar. The bar is a composition animation, so it keeps moving
/// on the render thread while the UI thread is busy building the main window. Colors come from the theme (<see cref="BrandArt"/>),
/// so it follows the accent the user picked.
/// </summary>
public sealed partial class SplashWindow : Window
{
    /// <summary>
    /// Shortest time it stays up (asked by the user: the app loads so fast it could not be seen). It stays on top of
    /// the main window meanwhile, so the audio and the project load behind it instead of making the start slower.
    /// </summary>
    static readonly TimeSpan MinShown = TimeSpan.FromSeconds(3);

    /// <summary>Matches the card's opacity transition in the XAML.</summary>
    const int FadeMs = 220;

    /// <summary>One pass of the stripe along the bottom edge.</summary>
    static readonly TimeSpan StripePass = TimeSpan.FromSeconds(1.3);

    readonly DateTime shownAt = DateTime.UtcNow;

    public SplashWindow()
    {
        InitializeComponent();
        Logo.Source = BrandArt.Logo;
        VersionText.Text = Loc.T("about.version", AboutDialog.AppVersion());
        if (TestBuild.Label != null)
        {
            TestChip.IsVisible = true;
            TestText.Text = Loc.T("splash.test");
        }

        Glow.Fill = BrandArt.Glow();
        Stripe.Background = BrandArt.Line();

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
}
