using Avalonia;

namespace GuitarCam.App;

static class Program
{
    // ASIO drivers are COM objects: the UI thread that opens them must be STA.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
