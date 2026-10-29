using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WildTrack.App.Controls;
using WildTrack.Projects;

namespace WildTrack.App;

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

    /// <summary>A sidebar may grow until the stage (or, in a Vertical layout, the timeline beside the video's column)
    /// would get narrower than <see cref="StageMinWidth"/>.</summary>
    double ClampSidebar(double w, double otherBar) =>
        Math.Clamp(w, SidebarMinWidth, Math.Max(SidebarMinWidth, Math.Min(SidebarMaxWidth, Bounds.Width - otherBar - SideStageWidth - StageMinWidth)));

    void SetInspectorWidth(double w) { inspectorWidth = w; InspectorBar.Width = w; }

    void SetProjectBarWidth(double w) { projectBarWidth = w; ProjectBar.Width = w; }

    /// <summary>What a sidebar takes from the window: nothing while it is hidden.</summary>
    static double ShownWidth(Control bar) => bar.IsVisible ? bar.Width : 0;

    /// <summary>
    /// The title bar's two panel buttons (asked by the user): each hides its sidebar entirely, rail and all, and shows it
    /// back as it was (open or folded). Saved in config.json ("sidebarHidden", "projectBarHidden") like the folded state.
    /// </summary>
    void InitSidebarToggles()
    {
        LeftPanelButton.Click += (_, _) => SetInspectorHidden(InspectorBar.IsVisible);
        RightPanelButton.Click += (_, _) => SetProjectBarHidden(ProjectBar.IsVisible);
        SetInspectorHidden(AppConfig.Get("sidebarHidden") == "1", save: false);
        SetProjectBarHidden(AppConfig.Get("projectBarHidden") == "1", save: false);
    }

    void SetInspectorHidden(bool hidden, bool save = true)
    {
        InspectorBar.IsVisible = !hidden;
        LeftPanelFill.IsVisible = !hidden;
        SetPanelTip(LeftPanelButton, Loc.T(hidden ? "panel.left.show" : "panel.left.hide"));
        if (save) SaveViewPref("sidebarHidden", hidden ? "1" : "2");
    }

    void SetProjectBarHidden(bool hidden, bool save = true)
    {
        ProjectBar.IsVisible = !hidden;
        RightPanelFill.IsVisible = !hidden;
        SetPanelTip(RightPanelButton, Loc.T(hidden ? "panel.right.show" : "panel.right.hide"));
        if (save) SaveViewPref("projectBarHidden", hidden ? "1" : "2");
    }

    static void SetPanelTip(Button b, string tip)
    {
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
    }

    /// <summary>The grip over a bar's inner edge (right for the inspector, left for the project bar).</summary>
    Border SidebarGrip(bool rightEdge, Func<double> get, Func<double> other, Action<double> set, string key) =>
        SizeGrip(rightEdge, rightEdge, get, w => ClampSidebar(w, other()), set, SidebarWidth, key, "splitter.sidebar");

    /// <summary>
    /// A grip on a panel's edge that drags its width: <paramref name="overhang"/> centers it on the border, half
    /// outside the panel; a double click goes back to <paramref name="reset"/>. The width is saved under
    /// <paramref name="key"/> when the drag ends.
    /// </summary>
    Border SizeGrip(bool rightEdge, bool overhang, Func<double> get, Func<double, double> clamp, Action<double> set, double reset, string key, string tipKey)
    {
        var line = new Border { Width = GripLine, Background = Brushes.Transparent, HorizontalAlignment = overhang ? HorizontalAlignment.Center : rightEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        var grip = new Border
        {
            Width = GripWidth, Background = Brushes.Transparent, Child = line, Cursor = new Cursor(StandardCursorType.SizeWestEast),
            HorizontalAlignment = rightEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = overhang ? new Thickness(0, 0, -GripWidth / 2, 0) : default,
        };
        ToolTip.SetTip(grip, Loc.T(tipKey));
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
                set(clamp(reset));
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
            set(clamp(startW + (rightEdge ? dx : -dx)));
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
