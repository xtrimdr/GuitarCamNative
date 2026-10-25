using Avalonia;

namespace GuitarCam.App;

static class Program
{
    // ASIO drivers are COM objects: the UI thread that opens them must be STA.
    [STAThread]
    public static void Main(string[] args)
    {
        // The ASIO callback is managed code, so every blocking GC stalls it: keep full collections in the background.
        // Short ones still stop it, so the UI avoids allocation bursts while audio runs (see MainWindow.compactMixer).
        SessionLog.Start(args);
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        Audio.AudioWarmUp.Start();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
