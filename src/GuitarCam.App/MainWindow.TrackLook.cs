using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Media;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;

namespace GuitarCam.App;

/// <summary>
/// A track's color and icon (native only, asked by the user): one picker, opened from the track's badge in the
/// timeline, the inspector or the mixer. A color applies at once and leaves the picker open so an icon can follow;
/// an icon closes it. Each pick is an edit like any other (<see cref="EditTrack"/>: undo, autosave).
/// </summary>
public partial class MainWindow
{
    Flyout? lookFlyout;

    void InitTrackLook() => Timeline.LookRequested += (t, at) => ShowTrackLook(t, Timeline, at);

    void ShowTrackLook(LiveTrack t, Control anchor, Point? at = null)
    {
        if (store == null || store.ReadOnly || restoring) return;
        var refresh = new List<Action>();
        var body = new StackPanel { Spacing = 8 };

        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("look.color").ToUpperInvariant() });
        body.Children.Add(ColorChips(() => t.Track.Color, hex => SetTrackLook(t, x => x.Color = hex, "color"), refresh));

        var none = new Button { Classes = { "chip" }, Content = Loc.T("look.icon.none"), FontSize = 11, MinHeight = 26, Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(none, Loc.T("look.icon.none.tip"));
        none.Click += (_, _) => { SetTrackLook(t, x => x.Icon = null, "icon"); lookFlyout?.Hide(); };
        refresh.Add(() => none.IsEnabled = t.Track.Icon != null);
        var iconHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        iconHead.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("look.icon").ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(none, 1); iconHead.Children.Add(none);
        body.Children.Add(iconHead);
        foreach (string category in TrackIcons.Categories)
        {
            body.Children.Add(new TextBlock { Text = Loc.T("icon.cat." + category), FontSize = 11, Foreground = Palette.Faint, Margin = new Thickness(0, 4, 0, 0) });
            var grid = new WrapPanel { Margin = new Thickness(-2, 0) };
            foreach (string id in TrackIcons.InCategory(category))
            {
                var view = new TrackIconView { Icon = id, Width = 20, Height = 20 };
                var b = new Button { Classes = { "ghost" }, Content = view, Width = 34, Height = 34, MinHeight = 34, Padding = new Thickness(0), Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(6), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(b, TrackIcons.Name(id));
                Avalonia.Automation.AutomationProperties.SetName(b, TrackIcons.Name(id));
                b.Click += (_, _) => { SetTrackLook(t, x => x.Icon = id, "icon"); lookFlyout?.Hide(); };
                refresh.Add(() =>
                {
                    bool on = TrackIcons.Resolve(t.Track.Icon) == id;
                    var tint = TrackIcons.ColorOf(t.Track);
                    b.Classes.Set("lit", on);
                    b.Background = on ? tint is { } c ? Palette.Alpha(c, 0.22) : Palette.AccentSoft : Brushes.Transparent;
                    view.Foreground = on ? tint is { } k ? Palette.Alpha(k, 1) : Palette.Accent : Palette.Dim;
                });
                grid.Children.Add(b);
            }
            body.Children.Add(grid);
        }
        foreach (var r in refresh) r();

        var card = new Border
        {
            Width = 344, Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer { Content = body, MaxHeight = Math.Max(240, Bounds.Height - 120), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        lookFlyout?.Hide();
        var flyout = lookFlyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" } };
        if (at is { } p)
        {
            flyout.Placement = PlacementMode.AnchorAndGravity;
            flyout.PlacementAnchor = PopupAnchor.TopLeft;
            flyout.PlacementGravity = PopupGravity.BottomRight;
            flyout.HorizontalOffset = p.X;
            flyout.VerticalOffset = p.Y;
        }
        else flyout.Placement = PlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(anchor);
    }

    void SetTrackLook(LiveTrack t, Action<GuitarCam.Projects.Track> change, string key) => EditTrack(t, x => change(x.Track), key);

    /// <summary>The color row of the pickers (no color first, then <see cref="TrackIcons.Colors"/>): a pick applies at
    /// once and repaints every entry of <paramref name="refresh"/>, so the picker stays open.</summary>
    static WrapPanel ColorChips(Func<string?> current, Action<string?> pick, List<Action> refresh)
    {
        var colors = new WrapPanel { Margin = new Thickness(-2, 0) };
        foreach (string? hex in new string?[] { null }.Concat(TrackIcons.Colors))
        {
            var chip = new Button { Width = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0), Margin = new Thickness(2), CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            if (hex == null) chip.Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10, Foreground = Palette.Faint };
            int n = Array.IndexOf(TrackIcons.Colors, hex);
            ToolTip.SetTip(chip, hex == null ? Loc.T("look.color.none") : Loc.T("look.color." + n));
            chip.Click += (_, _) => { pick(hex); foreach (var r in refresh) r(); };
            refresh.Add(() =>
            {
                bool on = current() == hex;
                chip.Classes.Set("lit", true);
                chip.Background = hex == null ? Palette.Hover : Palette.Brush(hex);
                chip.BorderBrush = on ? Palette.Fg : hex == null ? Palette.Line : Brushes.Transparent;
            });
            colors.Children.Add(chip);
        }
        return colors;
    }

    /// <summary>A folder's color picker (folders have no icon): opened from its icon in the timeline, its menu or the inspector.</summary>
    void ShowFolderLook(GuitarCam.Projects.Folder f, Control anchor, Point? at = null)
    {
        if (store == null || store.ReadOnly || restoring) return;
        string id = f.Id;
        GuitarCam.Projects.Folder F() => FolderById(id) ?? f;
        var refresh = new List<Action>();
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("folder.color").ToUpperInvariant() });
        body.Children.Add(ColorChips(() => F().Color, hex => FolderEdit(F(), x => x.Color = hex, "color"), refresh));
        foreach (var r in refresh) r();
        var card = new Border
        {
            Width = 272, Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        lookFlyout?.Hide();
        var flyout = lookFlyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" } };
        if (at is { } p)
        {
            flyout.Placement = PlacementMode.AnchorAndGravity;
            flyout.PlacementAnchor = PopupAnchor.TopLeft;
            flyout.PlacementGravity = PopupGravity.BottomRight;
            flyout.HorizontalOffset = p.X;
            flyout.VerticalOffset = p.Y;
        }
        else flyout.Placement = PlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(anchor);
    }
}
