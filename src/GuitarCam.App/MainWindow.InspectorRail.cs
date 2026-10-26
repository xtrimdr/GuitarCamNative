using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using GuitarCam.App.Controls;

namespace GuitarCam.App;

/// <summary>
/// The folded Canal tab's column also shows the selected channel (asked by the user): under the open/show buttons, one
/// icon per section of the channel in the order it sounds (Mezcla, the insert slots, Reverb, Delay, the extra
/// returns' sends, Transponer), in accent when it's on and faint when it's off. A click opens the tab, unfolds that
/// section and scrolls it to the top. Rebuilt only when the list of sections changes; edits just repaint it.
/// </summary>
public partial class MainWindow
{
    readonly StackPanel inspectorRail = new() { Spacing = 4 };
    readonly Border inspectorRailLine = new() { Height = 1, Background = Palette.Line, Margin = new Thickness(6, 4) };
    readonly List<(string Title, Control Icon, Button Button)> railItems = [];
    /// <summary>Each section's icon and power, kept by <see cref="Section"/> for the rail.</summary>
    readonly Dictionary<string, (string Icon, Func<bool>? On)> sectionLooks = [];
    string inspectorRailKey = "";

    void InitInspectorRail()
    {
        inspectorCollapsed.Children.Add(inspectorRailLine);
        inspectorCollapsed.Children.Add(inspectorRail);
    }

    /// <summary>The channel's sections in the order the Canal tab shows them; empty when there's nothing to show.</summary>
    List<string> RailSections()
    {
        if (sel == null || SelectedInspectorClip == null) return [];
        List<string> list = ["Mezcla", .. chainSections, "Reverb", "Delay", .. sendSections, "Transponer"];
        list.RemoveAll(t => !sectionLooks.ContainsKey(t));
        return list;
    }

    void RefreshInspectorRail()
    {
        var titles = RailSections();
        string key = string.Join("/", titles);
        if (key != inspectorRailKey)
        {
            inspectorRailKey = key;
            inspectorRail.Children.Clear();
            railItems.Clear();
            foreach (string title in titles)
            {
                var icon = FxIcon.Create(sectionLooks[title].Icon, 14);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.VerticalAlignment = VerticalAlignment.Center;
                var b = RailButton("", SectionTitle(title), () => GoToSection(title));
                b.Content = icon;
                inspectorRail.Children.Add(b);
                railItems.Add((title, icon, b));
            }
        }
        inspectorRailLine.IsVisible = railItems.Count > 0;
        foreach (var (title, icon, b) in railItems)
        {
            var on = sectionLooks.TryGetValue(title, out var look) ? look.On : null;
            bool lit = on == null || on();
            FxIcon.SetColor(icon, lit ? Palette.Accent : Palette.Faint);
            ToolTip.SetTip(b, on == null ? SectionTitle(title) : Loc.T(lit ? "insp.rail.on" : "insp.rail.off", SectionTitle(title)));
        }
    }

    /// <summary>Opens the Canal tab on a section: unfolded and scrolled to the top of the tab.</summary>
    void GoToSection(string title)
    {
        SetInspectorCollapsed(false);
        if (collapsedSections.Contains(title)) { SetSectionCollapsed(title, false); SaveCollapsedSections(); }
        Dispatcher.UIThread.Post(() =>
        {
            if (!inspectorSections.TryGetValue(title, out var section) || inspectorExpanded.Children[^1] is not ScrollViewer scroll
                || scroll.Content is not Visual content || section.Header.TranslatePoint(new Point(0, 0), content) is not { } at) return;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, at.Y));
        }, DispatcherPriority.Loaded);
    }
}
