using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WildTrack.App.Controls;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// The window's layout (asked by the user, step 1 and 2 of PENDIENTES "Paneles reubicables"): where each panel goes.
/// The panels are the named parts of the window (InspectorBar, StageArea with the preview and "En vivo", AudioArea
/// with TimelineHost and MixerHost, ProjectBar, and the title and transport bars, which never move); a layout only
/// says where they sit, and <see cref="ApplyLayout"/> puts them there without rebuilding them. "Clásico" is the
/// original grid (the stage over the timeline); "Vertical" gives the video a full-height column on the left or the
/// right, for 9:16 videos, with the timeline beside it and the mixer under it, the way solo audio stacks them.
/// The layout belongs to the program, not to a project: it and the sizes live in config.json ("layout",
/// "stageWidth", plus the "sidebarWidth", "projectBarWidth", "timelineHeight" and "mixerHeight" kept since before).
/// Solo audio has no stage, so it always looks the same whatever the layout.
/// </summary>
public partial class MainWindow
{
    const string ClassicLayout = "classic", VerticalLeftLayout = "vertical-left", VerticalRightLayout = "vertical-right";
    static readonly string[] Layouts = [ClassicLayout, VerticalLeftLayout, VerticalRightLayout];
    /// <summary>The video's column in a Vertical layout: wide enough for the format bar above the preview.</summary>
    const double StageSideWidth = 460, StageSideMinWidth = 400, StageSideMaxWidth = 1000;
    string layout = ClassicLayout;
    double stageWidth = StageSideWidth;
    Border? stageGripLeft, stageGripRight;
    Flyout? layoutMenu;
    /// <summary>The 9:16 → Vertical suggestion shows once per session: after that the user knows where it is.</summary>
    bool verticalSuggested;

    /// <summary>The video has its own column (a Vertical layout with a stage to show).</summary>
    bool SideStage => layout != ClassicLayout && !AudioOnly;

    /// <summary>What the video's column takes from the width the sidebars share with the timeline.</summary>
    double SideStageWidth => SideStage ? stageWidth : 0;

    void InitLayout()
    {
        layout = AppConfig.Get("layout") is { } saved && Layouts.Contains(saved) ? saved : ClassicLayout;
        if (double.TryParse(AppConfig.Get("stageWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
            stageWidth = Math.Clamp(w, StageSideMinWidth, StageSideMaxWidth);
        // inside the column on its inner edge: the timeline's headers sit right past it
        stageGripLeft = SizeGrip(false, false, () => stageWidth, ClampStage, SetStageWidth, StageSideWidth, "stageWidth", "splitter.stage");
        stageGripRight = SizeGrip(true, false, () => stageWidth, ClampStage, SetStageWidth, StageSideWidth, "stageWidth", "splitter.stage");
        foreach (var g in new[] { stageGripLeft, stageGripRight })
        {
            Grid.SetRowSpan(g, 3);
            Grid.SetColumnSpan(g, 2);
            g.ZIndex = 2;
            StageArea.Children.Add(g);
        }
        LayoutButton.Click += (_, _) => ShowLayoutMenu();
        // a narrower window squeezes the column for the timeline's sake; the chosen width comes back when it grows
        SizeChanged += (_, _) => { if (SideStage) StageArea.Width = ClampStage(stageWidth); };
        ApplyLayout();
    }

    /// <summary>The video's column may grow until the timeline beside it would get narrower than <see cref="StageMinWidth"/>.</summary>
    double ClampStage(double w) =>
        Math.Clamp(w, StageSideMinWidth, Math.Max(StageSideMinWidth, Math.Min(StageSideMaxWidth, Bounds.Width - InspectorBar.Width - ProjectBar.Width - StageMinWidth)));

    void SetStageWidth(double w)
    {
        stageWidth = w;
        if (SideStage) StageArea.Width = w;
    }

    void SetLayout(string id)
    {
        if (id == layout || !Layouts.Contains(id)) return;
        layout = id;
        SaveViewPref("layout", id);
        ApplyLayout();
    }

    /// <summary>
    /// Puts the panels where the layout and the project's mode say: on start, from the layout menu and from
    /// <see cref="ApplyMode"/>. Moving them is cheap (nothing is rebuilt), so it can run again any time.
    /// </summary>
    void ApplyLayout()
    {
        bool side = SideStage, left = layout == VerticalLeftLayout;
        Workspace.ColumnDefinitions = new ColumnDefinitions(!side ? "Auto,*" : left ? "Auto,Auto,*" : "Auto,*,Auto");
        Grid.SetColumn(StageArea, side && !left ? 2 : 1);
        Grid.SetColumn(AudioArea, side && left ? 2 : 1);
        StageArea.IsVisible = !AudioOnly;
        StageArea.Width = side ? stageWidth : double.NaN;
        // in its own column the stage is tall and narrow: "En vivo" goes under the preview instead of beside it
        StageArea.ColumnDefinitions = new ColumnDefinitions(side ? "*" : "*,Auto");
        StageArea.RowDefinitions = new RowDefinitions(side ? "Auto,*,Auto" : "Auto,*");
        Grid.SetRow(Live, side ? 2 : 1);
        Grid.SetColumn(Live, side ? 0 : 1);
        Live.Margin = side ? new Thickness(16, 0, 16, 16) : new Thickness(0, 16, 16, 16);
        Live.HorizontalAlignment = side ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        if (stageGripLeft != null && stageGripRight != null)
        {
            stageGripLeft.IsVisible = side && !left;
            stageGripRight.IsVisible = side && left;
        }
        // solo audio has no video to place: the button stays where it is, off, and says why
        LayoutButton.IsEnabled = !AudioOnly;
        ToolTip.SetTip(LayoutButton, Loc.T(AudioOnly ? "winlayout.audioOnly" : "winlayout.title"));
        MoveBottomView(AudioOnly || side);
        if (side) StageArea.Width = ClampStage(stageWidth);
    }

    /// <summary>The title bar's layout menu: one row per layout with a sketch of it, the current one lit.</summary>
    void ShowLayoutMenu()
    {
        var list = new StackPanel { Width = 300, Spacing = 2 };
        list.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("winlayout.title"), Margin = new Thickness(8, 4, 8, 6) });
        foreach (var id in Layouts) list.Children.Add(LayoutRow(id));
        layoutMenu = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = list };
        layoutMenu.ShowAt(LayoutButton);
    }

    Button LayoutRow(string id)
    {
        bool on = id == layout;
        var (frame, video) = id switch
        {
            VerticalLeftLayout => ("M1.5,1.5 H26.5 V18.5 H1.5 Z M10,1.5 V18.5", "M1.5,1.5 H10 V18.5 H1.5 Z"),
            VerticalRightLayout => ("M1.5,1.5 H26.5 V18.5 H1.5 Z M18,1.5 V18.5", "M18,1.5 H26.5 V18.5 H18 Z"),
            _ => ("M1.5,1.5 H26.5 V18.5 H1.5 Z M1.5,11 H26.5", "M1.5,1.5 H26.5 V11 H1.5 Z"),
        };
        var sketch = new Panel
        {
            Width = 28, Height = 20, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(video), Fill = on ? Palette.AccentSoft : Palette.Hover },
                new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(frame), Stroke = on ? Palette.Accent : Palette.Dim, StrokeThickness = 1.3, StrokeJoin = PenLineJoin.Round },
            },
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Loc.T($"winlayout.{id}"), FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = on ? Palette.Accent : Palette.Fg });
        text.Children.Add(new TextBlock { Text = Loc.T($"winlayout.{id}.hint"), FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { sketch, text } };
        text.Width = 300 - 16 - 28 - 12;
        var item = new Button
        {
            Classes = { "ghost", "rowmain" }, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6),
        };
        item.Click += (_, _) => { layoutMenu?.Hide(); SetLayout(id); };
        return item;
    }

    /// <summary>A project just turned 9:16 in the classic layout: offer the Vertical one, never switch on its own.</summary>
    void SuggestVerticalLayout()
    {
        if (verticalSuggested || layout != ClassicLayout || AudioOnly) return;
        verticalSuggested = true;
        Toast(Loc.T("winlayout.suggest"), action: (Loc.T("winlayout.suggest.action"), () => SetLayout(VerticalRightLayout)));
    }
}
