using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WildTrack.App.Controls;
using WildTrack.App.Video;
using WildTrack.Audio;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// The right sidebar (the project bar: Color / Biblioteca / Historial, collapsible, the tab remembered) and the Color tab,
/// a port of ColorPanel.tsx: templates, filter, adjustments and effects, every change undoable and live in the
/// preview. Same as the web except: it ends above the transport bar (which runs the whole width here), the
/// templates live in %APPDATA% instead of localStorage. The Biblioteca and Historial tabs are in MainWindow.History.cs.
/// </summary>
public partial class MainWindow
{
    const double ProjectBarCollapsedWidth = 44;

    readonly List<Action> colorRefresh = [];
    readonly HashSet<string> collapsedColor = [], collapsedHistory = [];
    readonly Dictionary<string, (StackPanel Body, Control? Action, SectionHeader Header, HashSet<string> Collapsed)> colorSections = [];
    readonly StackPanel colorPanel = new();
    readonly ScrollViewer projectBarBody = new();
    readonly DockPanel projectBarExpanded = new();
    readonly StackPanel projectBarCollapsed = new() { Spacing = 4, Margin = new Thickness(0, 8), HorizontalAlignment = HorizontalAlignment.Center };
    readonly List<(string Key, Button Tab, Button Rail)> projectTabs = [];
    string projectTab = "history";
    List<VideoTemplate> videoTemplates = [];
    readonly StackPanel templateRows = new() { Spacing = 4 };
    readonly TextBox templateName = new() { Watermark = Loc.T("color.tpl.name"), MaxLength = VideoTemplates.MaxName, FontSize = 12, MinHeight = 32, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
    readonly Grid templateForm = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false };
    Button? templateSaveStart;

    ColorGrade ProjectColor => store?.Project.Settings.Color ?? new();
    VideoFx ProjectFx => store?.Project.Settings.VideoFx ?? new();

    static readonly (string Key, string Label, double Min, string? Hint, Func<ColorGrade, double> Get, Action<ColorGrade, double> Set)[] Adjust =
    [
        ("exposure", Loc.T("color.adj.exposure"), -100, null, c => c.Exposure, (c, v) => c.Exposure = v),
        ("contrast", Loc.T("color.adj.contrast"), -100, null, c => c.Contrast, (c, v) => c.Contrast = v),
        ("highlights", Loc.T("color.adj.highlights"), -100, Loc.T("color.adj.highlights.hint"), c => c.Highlights, (c, v) => c.Highlights = v),
        ("shadows", Loc.T("color.adj.shadows"), -100, Loc.T("color.adj.shadows.hint"), c => c.Shadows, (c, v) => c.Shadows = v),
        ("whites", Loc.T("color.adj.whites"), -100, Loc.T("color.adj.whites.hint"), c => c.Whites, (c, v) => c.Whites = v),
        ("blacks", Loc.T("color.adj.blacks"), -100, Loc.T("color.adj.blacks.hint"), c => c.Blacks, (c, v) => c.Blacks = v),
        ("light", Loc.T("color.adj.light"), -100, Loc.T("color.adj.light.hint"), c => c.Light, (c, v) => c.Light = v),
        ("saturation", Loc.T("color.adj.saturation"), -100, null, c => c.Saturation, (c, v) => c.Saturation = v),
        ("vibrance", Loc.T("color.adj.vibrance"), -100, Loc.T("color.adj.vibrance.hint"), c => c.Vibrance, (c, v) => c.Vibrance = v),
        ("temperature", Loc.T("color.adj.temperature"), -100, Loc.T("color.adj.temperature.hint"), c => c.Temperature, (c, v) => c.Temperature = v),
        ("tint", Loc.T("color.adj.tint"), -100, Loc.T("color.adj.tint.hint"), c => c.Tint, (c, v) => c.Tint = v),
        ("fade", Loc.T("color.adj.fade"), 0, Loc.T("color.adj.fade.hint"), c => c.Fade, (c, v) => c.Fade = v),
    ];

    static string Signed(double v) => v > 0 ? $"+{v:0}" : $"{v:0}";

    void InitProjectBar()
    {
        foreach (var (scope, set) in new[] { ("color", collapsedColor), ("history", collapsedHistory) })
            try
            {
                foreach (var title in System.Text.Json.JsonSerializer.Deserialize<string[]>(AppConfig.Get("collapsed." + scope) ?? "[]") ?? []) set.Add(title);
            }
            catch (System.Text.Json.JsonException) { }
        projectTab = AppConfig.Get("projectTab") is "color" or "library" ? AppConfig.Get("projectTab")! : "history";
        bool collapsed = AppConfig.Get("projectBarCollapsed") == "1";

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"), Height = 44, Margin = new Thickness(6, 0, 16, 0) };
        var hide = RailButton("", Loc.T("insp.hide"), () => SetProjectBarCollapsed(true));
        shortcutTips.Add((hide, Loc.T("insp.hide"), "rightPanel"));
        hide.Margin = new Thickness(0, 0, 12, 0);
        header.Children.Add(hide);
        var rail = RailButton("", Loc.T("bar.show"), () => SetProjectBarCollapsed(false));
        shortcutTips.Add((rail, Loc.T("bar.show"), "rightPanel"));
        projectBarCollapsed.Children.Add(rail);
        int col = 1;
        foreach (var (key, label, glyph) in new[] { ("color", Loc.T("bar.tab.color"), ""), ("library", Loc.T("bar.tab.library"), ""), ("history", Loc.T("bar.tab.history"), "") })
        {
            var tab = new Button { Classes = { "ghost" }, Content = label, FontSize = 13, FontWeight = FontWeight.Medium, Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 20, 0), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0, 0, 0, 2), VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
            tab.Click += (_, _) => SetProjectTab(key);
            Grid.SetColumn(tab, col++); header.Children.Add(tab);
            var icon = RailButton(glyph, label, () => { SetProjectTab(key); SetProjectBarCollapsed(false); });
            projectBarCollapsed.Children.Add(icon);
            projectTabs.Add((key, tab, icon));
        }
        var top = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        DockPanel.SetDock(top, Dock.Top);
        projectBarExpanded.Children.Add(top);
        projectBarExpanded.Children.Add(projectBarBody);
        projectBarWidth = SavedSidebarWidth("projectBarWidth");
        projectBarGrip = SidebarGrip(false, () => projectBarWidth, () => InspectorBar.Width, SetProjectBarWidth, "projectBarWidth");
        ProjectBar.Child = new Panel { Children = { projectBarExpanded, projectBarCollapsed, projectBarGrip } };

        BuildColorPanel();
        BuildHistoryPanel();
        SetProjectTab(projectTab, save: false);
        SetProjectBarCollapsed(collapsed, save: false);
    }

    static Button RailButton(string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "ghost" }, Width = 32, Height = 32, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 14 } };
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    void SetProjectBarCollapsed(bool collapsed, bool save = true)
    {
        ProjectBar.Width = collapsed ? ProjectBarCollapsedWidth : projectBarWidth;
        if (projectBarGrip != null) projectBarGrip.IsVisible = !collapsed;
        projectBarExpanded.IsVisible = !collapsed;
        projectBarCollapsed.IsVisible = collapsed;
        if (save) SaveViewPref("projectBarCollapsed", collapsed ? "1" : "2");
    }

    /// <summary>Solo audio has no picture to color: the Color tab hides and Historial shows (the saved tab stays).</summary>
    void SetColorTabVisible(bool visible)
    {
        foreach (var (key, tab, rail) in projectTabs)
            if (key == "color") tab.IsVisible = rail.IsVisible = visible;
        if (!visible && projectTab == "color") SetProjectTab("history", save: false);
    }

    void SetProjectTab(string key, bool save = true)
    {
        projectTab = key;
        foreach (var (k, tab, rail) in projectTabs)
        {
            bool on = k == key;
            tab.BorderBrush = on ? Palette.Accent : Brushes.Transparent;
            tab.Foreground = on ? Palette.Fg : Palette.Faint;
            rail.Foreground = on ? Palette.Accent : Palette.Faint;
            rail.Background = on ? Palette.AccentSoft : Brushes.Transparent;
        }
        projectBarBody.Content = key == "color" ? colorPanel : key == "library" ? libraryPanel : historyPanel;
        if (key != "color") RefreshHistory();
        if (save) SaveViewPref("projectTab", key);
    }

    void SaveViewPref(string key, string value)
    {
        inspectorPrefsSaving = inspectorPrefsSaving.ContinueWith(_ =>
        {
            try { AppConfig.Set(key, value); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* View preference only. */ }
        }, TaskScheduler.Default);
    }

    /// <summary>ui.tsx Section inside the "color" (or "history") collapse scope: chevron + icon + label, an action and a power button.</summary>
    StackPanel ColorSection(string title, string icon, Func<bool>? on = null, Action? toggle = null, Func<bool, string>? powerTip = null, Control? action = null,
        StackPanel? host = null, string scope = "color")
    {
        var collapsed = scope == "color" ? collapsedColor : collapsedHistory;
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(16, 2, 16, 16) };
        var outer = new StackPanel();
        (host ?? colorPanel).Children.Add(new Border { Child = outer, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1) });
        var header = new SectionHeader(Loc.Or("bar.section." + title, title), icon);
        var sectionIcon = header.Icon;
        colorSections[title] = (content, action, header, collapsed);
        header.Toggled += () =>
        {
            SetColorSectionCollapsed(title, !collapsed.Contains(title));
            string json = System.Text.Json.JsonSerializer.Serialize(collapsed);
            SaveViewPref("collapsed." + scope, json);
        };
        Button? powerButton = null;
        if (on != null && toggle != null)
        {
            var power = powerButton = new Button { Classes = { "chip" }, MinHeight = 24, Height = 24, Padding = new Thickness(6, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
            power.Click += (_, _) => toggle();
            colorRefresh.Add(() =>
            {
                bool lit = on();
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                label.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11, Foreground = lit ? Palette.App : Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = lit ? "ON" : "OFF", FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = lit ? Palette.App : Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
                power.Content = label;
                power.Classes.Set("lit", lit);
                power.Background = lit ? Palette.Accent : Brushes.Transparent;
                power.BorderBrush = lit ? Palette.Accent : Palette.Line;
                ToolTip.SetTip(power, powerTip?.Invoke(lit));
                content.Opacity = lit ? 1 : 0.4;
                FxIcon.SetColor(sectionIcon, lit ? Palette.Accent : Palette.Faint);
            });
        }
        header.SetTrailing(action, powerButton);
        outer.Children.Add(header); outer.Children.Add(content);
        SetColorSectionCollapsed(title, collapsed.Contains(title));
        return content;
    }

    void SetColorSectionCollapsed(string title, bool hide)
    {
        if (!colorSections.TryGetValue(title, out var s)) return;
        if (hide) s.Collapsed.Add(title); else s.Collapsed.Remove(title);
        s.Body.IsVisible = !hide;
        if (s.Action != null) s.Action.IsVisible = !hide && s.Action.Tag is not false;
        s.Header.SetCollapsed(hide);
    }

    /// <summary>A header action that also hides itself when it has nothing to do (Tag = false).</summary>
    void ShowSectionAction(string title, bool show)
    {
        if (!colorSections.TryGetValue(title, out var s) || s.Action == null) return;
        s.Action.Tag = show;
        s.Action.IsVisible = show && !s.Collapsed.Contains(title);
    }

    static Button HeaderAction(string glyph, string text, string tip, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Classes = { "ghost" }, Content = content, Foreground = Palette.Faint, MinHeight = 24, Height = 24, Padding = new Thickness(6, 0) };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    InspectorSlider ColorSlider(StackPanel host, string label, double min, double max, double reset,
        Func<double> get, Action<double> set, Func<double, string> format, string? hint = null)
    {
        var slider = new InspectorSlider(label, min, max, 1, reset, format, v => { if (!syncing) set(v); }, hint);
        host.Children.Add(slider);
        colorRefresh.Add(() => slider.Set(get()));
        return slider;
    }

    void BuildColorPanel()
    {
        // Plantillas
        templateSaveStart = HeaderAction("", Loc.T("color.tpl.saveCurrent"), Loc.T("color.tpl.saveCurrent.tip"), () =>
        {
            templateName.Text = "";
            templateForm.IsVisible = true;
            ShowSectionAction("Plantillas", false);
            RefreshTemplates();
            templateName.Focus();
        });
        var templates = ColorSection("Plantillas", "", action: templateSaveStart);
        var save = new Button { Classes = { "accent" }, Content = Loc.T("color.tpl.save"), Height = 32, MinHeight = 32, FontSize = 12, Padding = new Thickness(10, 0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        Grid.SetColumn(save, 1);
        templateForm.Children.Add(templateName); templateForm.Children.Add(save);
        templateName.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(templateName.Text);
        save.Click += (_, _) => SaveVideoTemplate();
        templateName.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SaveVideoTemplate(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseTemplateForm(); e.Handled = true; }
        };
        templates.Children.Add(templateForm);
        templates.Children.Add(templateRows);
        videoTemplates = VideoTemplates.List();

        // Filtro
        var filter = ColorSection("Filtro", "", () => ProjectColor.On, () => ColorEdit(s => s.Color.On = !s.Color.On, "color:on"),
            lit => lit ? Loc.T("color.filter.off") : Loc.T("color.filter.on"));
        var looks = new UniformGrid { Columns = 3 };
        foreach (var look in VideoColorProcessor.Looks)
        {
            var swatch = new ColorSwatch { Height = 24, Spec = new ExportColorSpec(Look: look.Id, Amount: 100) };
            var name = new TextBlock { Text = Loc.Or("color.look." + look.Id, look.Label), FontSize = 11, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
            var chipContent = new StackPanel { Spacing = 6 };
            chipContent.Children.Add(swatch); chipContent.Children.Add(name);
            var chip = new ToggleButton { Classes = { "chip" }, Content = chipContent, Padding = new Thickness(6), Margin = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Avalonia.Automation.AutomationProperties.SetName(chip, Loc.Or("color.look." + look.Id, look.Label));
            string id = look.Id;
            chip.Click += (_, _) => ColorEdit(s => s.Color.Look = id, "color:look");
            colorRefresh.Add(() =>
            {
                bool active = ProjectColor.Look == id;
                chip.IsChecked = active;
                name.Foreground = active ? Palette.Fg : Palette.Dim;
            });
            looks.Children.Add(chip);
        }
        filter.Children.Add(new Border { Margin = new Thickness(-3), Child = looks });
        var amount = ColorSlider(filter, Loc.T("color.intensity"), 0, 100, 100, () => ProjectColor.Amount,
            v => ColorEdit(s => s.Color.Amount = v, "color:amount"), v => $"{v:0}%");
        colorRefresh.Add(() => amount.IsVisible = ProjectColor.Look != "none");

        // Ajustes
        var reset = HeaderAction("", Loc.T("color.reset"), Loc.T("color.reset.tip"), () => ColorEdit(s =>
        {
            foreach (var a in Adjust) a.Set(s.Color, 0);
        }, "color:" + string.Join(",", Adjust.Select(a => a.Key).Order())));
        var adjust = ColorSection("Ajustes", "", action: reset);
        foreach (var a in Adjust)
        {
            var (key, get, set) = (a.Key, a.Get, a.Set);
            ColorSlider(adjust, a.Label, a.Min, 100, 0, () => get(ProjectColor), v => ColorEdit(s => set(s.Color, v), "color:" + key), Signed, a.Hint);
        }
        colorRefresh.Add(() => ShowSectionAction("Ajustes", Adjust.Any(a => a.Get(ProjectColor) != 0)));

        // Efectos
        var fx = ColorSection("Efectos", "", () => ProjectFx.On, () => ColorEdit(s => s.VideoFx.On = !s.VideoFx.On, "videoFx:on"),
            lit => lit ? Loc.T("color.fx.off") : Loc.T("color.fx.on"));
        ColorSlider(fx, Loc.T("color.fx.sharpness"), 0, 100, 0, () => ProjectFx.Sharpness, v => ColorEdit(s => s.VideoFx.Sharpness = v, "videoFx:sharpness"),
            v => $"{v:0}", Loc.T("color.fx.sharpness.hint"));
        ColorSlider(fx, Loc.T("color.fx.clarity"), 0, 100, 0, () => ProjectFx.Clarity, v => ColorEdit(s => s.VideoFx.Clarity = v, "videoFx:clarity"),
            v => $"{v:0}", Loc.T("color.fx.clarity.hint"));
        ColorSlider(fx, Loc.T("color.fx.glow"), 0, 100, 0, () => ProjectFx.Glow, v => ColorEdit(s => s.VideoFx.Glow = v, "videoFx:glow"),
            v => $"{v:0}", Loc.T("color.fx.glow.hint"));
        ColorSlider(fx, Loc.T("color.fx.vignette"), 0, 100, 0, () => ProjectFx.Vignette, v => ColorEdit(s => s.VideoFx.Vignette = v, "videoFx:vignette"),
            v => $"{v:0}", Loc.T("color.fx.vignette.hint"));
        var size = ColorSlider(fx, Loc.T("color.fx.vignetteSize"), 0, 100, 50, () => ProjectFx.VignetteSize, v => ColorEdit(s => s.VideoFx.VignetteSize = v, "videoFx:vignetteSize"),
            v => $"{v:0}", Loc.T("color.fx.vignetteSize.hint"));
        colorRefresh.Add(() => size.IsVisible = ProjectFx.Vignette > 0);
        ColorSlider(fx, Loc.T("color.fx.grain"), 0, 100, 0, () => ProjectFx.Grain, v => ColorEdit(s => s.VideoFx.Grain = v, "videoFx:grain"),
            v => $"{v:0}", Loc.T("color.fx.grain.hint"));
        // label above, the five options below: side by side they don't fit a narrow inspector
        var bars = new StackPanel { Spacing = 6 };
        var barsLabel = new TextBlock { Text = Loc.T("color.letterbox"), Classes = { "dim" } };
        ToolTip.SetTip(barsLabel, Loc.T("color.letterbox.tip0"));
        bars.Children.Add(barsLabel);
        var seg = new UniformGrid { Rows = 1 };
        foreach (var l in VideoColorProcessor.LetterboxOptions)
        {
            var (text, tip) = l.Id switch
            {
                "off" => (Loc.T("color.letterbox.off"), Loc.T("color.letterbox.none")),
                "custom" => (Loc.T("color.letterbox.custom"), Loc.T("color.letterbox.custom.tip")),
                _ => (l.Label, Loc.T("color.letterbox.format", l.Label)),
            };
            var b = new ToggleButton { Classes = { "seg" }, Padding = new Thickness(4, 0), MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Content = new TextBlock { Text = text, Classes = { "mono" }, FontSize = 12 } };
            ToolTip.SetTip(b, tip);
            string id = l.Id;
            b.Click += (_, _) => ColorEdit(s => s.VideoFx.Letterbox = id, "videoFx:letterbox");
            colorRefresh.Add(() => b.IsChecked = ProjectFx.Letterbox == id);
            seg.Children.Add(b);
        }
        bars.Children.Add(new Border { Classes = { "seg" }, Child = seg });
        fx.Children.Add(bars);
        var barHeight = ColorSlider(fx, Loc.T("color.letterbox.height"), VideoColorProcessor.LetterboxHeightMin, VideoColorProcessor.LetterboxHeightMax,
            VideoColorProcessor.LetterboxHeightDefault, () => ProjectFx.LetterboxHeight, v => ColorEdit(s => s.VideoFx.LetterboxHeight = v, "videoFx:letterboxHeight"),
            v => $"{v:0}%", Loc.T("color.letterbox.height.hint"));
        colorRefresh.Add(() => barHeight.IsVisible = ProjectFx.Letterbox == "custom");
        fx.Children.Add(Help(Loc.T("color.fx.hint")));

        RefreshColorPanel();
    }

    /// <summary>Every color edit is one undo step per control (the web's checkpoint key), saved and shown live.</summary>
    void ColorEdit(Action<ProjectSettings> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project, key);
        change(store.Project.Settings);
        AfterColorEdit();
    }

    void AfterColorEdit()
    {
        MarkDirty();
        UpdateUndoButtons();
        RefreshColorPanel();
        Preview.InvalidateVisual();
    }

    void RefreshColorPanel()
    {
        bool was = syncing;
        syncing = true;
        try
        {
            colorPanel.IsEnabled = store != null && !store.ReadOnly && !restoring;
            foreach (var refresh in colorRefresh) refresh();
            RefreshTemplates();
            RefreshHistory();
        }
        finally { syncing = was; }
    }

    void RefreshTemplates()
    {
        templateRows.Children.Clear();
        if (videoTemplates.Count == 0 && !templateForm.IsVisible)
        {
            templateRows.Children.Add(Help(Loc.T("color.tpl.empty")));
            return;
        }
        var color = ProjectColor; var fx = ProjectFx;
        foreach (var t in videoTemplates)
        {
            bool active = t.Matches(color, fx);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 36 };
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(new ColorSwatch { Width = 56, Height = 16, Spec = PreviewVideoFx.ToSpec(t.Color), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = t.Name, FontSize = 12, Foreground = active ? Palette.Fg : Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var apply = new Button { Classes = { "ghost" }, Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(6, 0), Background = Brushes.Transparent };
            ToolTip.SetTip(apply, Loc.T("color.tpl.apply"));
            var tpl = t;
            apply.Click += (_, _) =>
            {
                if (syncing || restoring || store == null || store.ReadOnly) return;
                undo.Checkpoint(store.Project);
                VideoTemplates.ApplyTo(tpl, store.Project.Settings);
                AfterColorEdit();
            };
            row.Children.Add(apply);
            var remove = new Button { Classes = { "ghost" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), Opacity = 0,
                HorizontalContentAlignment = HorizontalAlignment.Center, Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 } };
            ToolTip.SetTip(remove, Loc.T("color.tpl.delete"));
            Avalonia.Automation.AutomationProperties.SetName(remove, Loc.T("color.tpl.delete.name", t.Name));
            remove.PointerEntered += (_, _) => remove.Foreground = Palette.Rec;
            remove.PointerExited += (_, _) => remove.Foreground = Palette.Faint;
            remove.Foreground = Palette.Faint;
            remove.GotFocus += (_, _) => remove.Opacity = 1;
            remove.Click += (_, _) => RunTemplateStore(() => VideoTemplates.Delete(tpl.Id));
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            var box = new Border { Child = row, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                BorderBrush = active ? Palette.Accent : Palette.Line, Background = active ? Palette.AccentSoft : Brushes.Transparent };
            box.PointerEntered += (_, _) => { remove.Opacity = 1; if (!active) box.Background = Palette.Hover; };
            box.PointerExited += (_, _) => { remove.Opacity = remove.IsFocused ? 1 : 0; if (!active) box.Background = Brushes.Transparent; };
            templateRows.Children.Add(box);
        }
    }

    void SaveVideoTemplate()
    {
        string name = templateName.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        var color = ProjectLibrary.Clone(ProjectColor);
        var fx = ProjectLibrary.Clone(ProjectFx);
        if (RunTemplateStore(() => VideoTemplates.Save(name, color, fx))) CloseTemplateForm();
    }

    void CloseTemplateForm()
    {
        templateForm.IsVisible = false;
        ShowSectionAction("Plantillas", true);
        RefreshTemplates();
    }

    bool RunTemplateStore(Func<List<VideoTemplate>> op)
    {
        try
        {
            videoTemplates = op();
            RefreshTemplates();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(Loc.T("color.tpl.saveError", e.Message));
            return false;
        }
    }
}
