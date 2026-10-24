using Avalonia;

namespace GuitarCam.App;

static class Program
{
    // ASIO drivers are COM objects: the UI thread that opens them must be STA.
    [STAThread]
    public static void Main(string[] args)
    {
        // The ASIO callback is managed code, so a blocking full GC stalls it and the audio drops out:
        // keep full collections in the background while the app runs.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
