using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// Remembers where the window was (config.json "window": x, y, width, height, maximized) and opens it there again.
/// The normal bounds are read after each move or resize settles (a posted check), because while maximizing Windows
/// moves the window before the state changes and that position isn't the one to keep. Minimized or full screen, the
/// state before counts. A saved place that no longer falls on a monitor (one was unplugged) opens centered as usual.
/// </summary>
public partial class MainWindow
{
    const string WindowPlaceKey = "window";
    /// <summary>A point this far into the title bar has to be on a monitor's working area to reuse the saved place.</summary>
    static readonly PixelPoint GrabPoint = new(80, 16);
    PixelPoint normalPos;
    Size normalSize;
    bool placeKnown, wasMaximized;

    void InitWindowPlace()
    {
        RestoreWindowPlace();
        Opened += (_, _) => NotePlaceSoon();
        PositionChanged += (_, _) => NotePlaceSoon();
        Resized += (_, _) => NotePlaceSoon();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            if (WindowState == WindowState.Maximized) wasMaximized = true;
            else if (WindowState == WindowState.Normal) wasMaximized = false;
            NotePlaceSoon();
        };
    }

    void NotePlaceSoon() => Dispatcher.UIThread.Post(() =>
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        normalPos = Position;
        normalSize = ClientSize;
        placeKnown = true;
    }, DispatcherPriority.Background);

    void RestoreWindowPlace()
    {
        string? saved;
        try { saved = AppConfig.Get(WindowPlaceKey); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var p = saved?.Split(',');
        if (p is not { Length: 5 }
            || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            || !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) return;
        var pos = new PixelPoint(x, y);
        var screen = Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(pos + GrabPoint));
        if (screen == null || !double.IsFinite(w) || !double.IsFinite(h)) return;
        var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = pos;
        Width = Math.Clamp(w, MinWidth, Math.Max(MinWidth, area.Width));
        Height = Math.Clamp(h, MinHeight, Math.Max(MinHeight, area.Height));
        // opened maximized, these stay the normal bounds until the window is restored and moved
        normalPos = pos;
        normalSize = new Size(Width, Height);
        placeKnown = true;
        if (p[4] == "1") WindowState = WindowState.Maximized;
    }

    void SaveWindowPlace()
    {
        if (!placeKnown) return;
        string value = string.Join(',',
            normalPos.X.ToString(CultureInfo.InvariantCulture), normalPos.Y.ToString(CultureInfo.InvariantCulture),
            Math.Round(normalSize.Width).ToString(CultureInfo.InvariantCulture), Math.Round(normalSize.Height).ToString(CultureInfo.InvariantCulture),
            wasMaximized ? "1" : "0");
        try { AppConfig.Set(WindowPlaceKey, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* View preference only. */ }
    }
}
