using Avalonia;
using Avalonia.Controls;

namespace WildTrack.App;

/// <summary>
/// Full screen (F11, the "fullScreen" shortcut): the window covers the whole monitor without the taskbar or the
/// system's caption buttons, and the same key brings back the state it had before (maximized or not). Our own title
/// bar stays, because its project tools and Exportar live there; without the system buttons it doesn't keep their
/// room on the right (<see cref="CaptionRoom"/>). Not remembered between sessions: the window opens as it was before
/// going full screen (<see cref="SaveWindowPlace"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>The right side of the title bar's first row left free for the system's minimize, maximize and close.</summary>
    const double CaptionRoom = 150;
    WindowState beforeFullScreen = WindowState.Normal;

    void InitFullScreen()
    {
        // any way in or out (the key, a double click on the bar, the system) leaves the bar right
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            var m = TitleRow.Margin;
            TitleRow.Margin = new Thickness(m.Left, m.Top, WindowState == WindowState.FullScreen ? m.Left : CaptionRoom, m.Bottom);
        };
    }

    void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = beforeFullScreen;
        else
        {
            beforeFullScreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowState = WindowState.FullScreen;
        }
    }
}
