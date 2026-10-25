using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GuitarCam.App.Controls;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The width of both sidebars (the inspector on the left, the project bar on the right): a grip on each one's inner
/// edge drags it wider or narrower and a double click puts it back to 290. Each width is saved in config.json
/// ("sidebarWidth", "projectBarWidth") like the folded state, and only while the bar is open: folded, it is the rail.
/// </summary>
public partial class MainWindow
{
    const double SidebarWidth = 290, SidebarMinWidth = 240, SidebarMaxWidth = 600;
    /// <summary>Width the stage keeps whatever the sidebars do, so the video never gets squeezed to nothing.</summary>
    const double StageMinWidth = 360;
    /// <summary>Grab area of a grip; the line that lights up on hover is thinner. The inspector's is centered on its border
    /// (half over the stage, clear of its scrollbar; the bar has ZIndex 1 inside its own grid). The project bar's stays
    /// inside it: a ZIndex there, in the window's root grid, would put the bar above the toasts and dialogs.</summary>
    const double GripWidth = 8, GripLine = 2;
    double inspectorWidth = SidebarWidth, projectBarWidth = SidebarWidth;
    Border? inspectorGrip, projectBarGrip;

    static double SavedSidebarWidth(string key) =>
        double.TryParse(AppConfig.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? Math.Clamp(w, SidebarMinWidth, SidebarMaxWidth) : SidebarWidth;

    /// <summary>A sidebar may grow until the stage would get narrower than <see cref="StageMinWidth"/>.</summary>
    double ClampSidebar(double w, double otherBar) =>
        Math.Clamp(w, SidebarMinWidth, Math.Max(SidebarMinWidth, Math.Min(SidebarMaxWidth, Bounds.Width - otherBar - StageMinWidth)));

    void SetInspectorWidth(double w) { inspectorWidth = w; InspectorBar.Width = w; }

    void SetProjectBarWidth(double w) { projectBarWidth = w; ProjectBar.Width = w; }

    /// <summary>The grip over a bar's inner edge (right for the inspector, left for the project bar).</summary>
    Border SidebarGrip(bool rightEdge, Func<double> get, Func<double> other, Action<double> set, string key)
    {
        var line = new Border { Width = GripLine, Background = Brushes.Transparent, HorizontalAlignment = rightEdge ? HorizontalAlignment.Center : HorizontalAlignment.Left };
        var grip = new Border
        {
            Width = GripWidth, Background = Brushes.Transparent, Child = line, Cursor = new Cursor(StandardCursorType.SizeWestEast),
            HorizontalAlignment = rightEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = rightEdge ? new Thickness(0, 0, -GripWidth / 2, 0) : default,
        };
        ToolTip.SetTip(grip, Loc.T("splitter.sidebar"));
        double startX = 0, startW = 0;
        bool dragging = false;
        void Lit(bool on) => line.Background = on ? Palette.Accent : Brushes.Transparent;
        void Save() => SaveViewPref(key, get().ToString("0", CultureInfo.InvariantCulture));
        grip.PointerEntered += (_, _) => Lit(true);
        grip.PointerExited += (_, _) => Lit(dragging);
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                set(ClampSidebar(SidebarWidth, other()));
                Save();
                return;
            }
            (dragging, startX, startW) = (true, e.GetPosition(this).X, get());
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            double dx = e.GetPosition(this).X - startX;
            set(ClampSidebar(startW + (rightEdge ? dx : -dx), other()));
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            Lit(grip.IsPointerOver);
            Save();
        };
        grip.PointerCaptureLost += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            Lit(false);
            Save();
        };
        return grip;
    }
}
