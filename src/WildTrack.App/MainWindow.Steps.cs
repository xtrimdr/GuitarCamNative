using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WildTrack.App.Controls;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// Historial's Pasos and Versiones (asked by the user: the tab said what had happened but couldn't take you back).
/// Pasos: this session's undo steps by name (<see cref="UndoManager.Steps"/>), newest on top; a click goes to that step
/// (<see cref="UndoManager.JumpTo"/>, through the same restore as Ctrl+Z) and the undone ones stay grey until a new edit
/// drops them. It redraws a moment after the edits stop: a fader drag edits every frame and the list would be rebuilt
/// (and its garbage made) every frame. Versiones: the saved versions of historial/ (the automatic ones, the ones saved
/// with a name and one per export), read off the UI thread, with Volver and a name; going back keeps what there was as
/// one more version (<see cref="ProjectStore.Restore"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>Steps and versions shown before "Ver más".</summary>
    const int StepsPage = 30, VersionsPage = 15;
    /// <summary>How long after the last edit the step list redraws.</summary>
    static readonly TimeSpan StepsDelay = TimeSpan.FromMilliseconds(400);
    readonly StackPanel historySteps = new(), historyVersions = new();
    readonly DispatcherTimer stepsTimer = new() { Interval = StepsDelay };
    readonly TextBox versionName = new() { Classes = { "field" }, Watermark = Loc.T("ver.name"), MaxLength = ProjectStore.MaxVersionName, FontSize = 12, Height = 32, MinHeight = 32 };
    readonly Grid versionForm = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false };
    Button? versionSaveStart;
    int stepsShown = StepsPage, versionsShown = VersionsPage;
    /// <summary>Where the step list starts: the project opened, a version went back, unused takes were deleted.</summary>
    string stepsOrigin = Loc.T("steps.origin.open");
    List<ProjectStore.Snapshot> versions = [];
    string? versionsDir, renamingVersion;
    bool versionBusy, versionsLoading;

    bool StepsVisible => projectTab == "history" && projectBarExpanded.IsVisible;

    void BuildStepsPanel()
    {
        ColorSection("Pasos", "", host: historyPanel, scope: "history").Children.Add(historySteps);
        stepsTimer.Tick += (_, _) => RefreshSteps();

        versionSaveStart = HeaderAction("", Loc.T("ver.save"), Loc.T("ver.save.tip"), () =>
        {
            versionName.Text = "";
            versionForm.IsVisible = true;
            ShowSectionAction("Versiones", false);
            versionName.Focus();
        });
        var section = ColorSection("Versiones", "", action: versionSaveStart, host: historyPanel, scope: "history");
        var save = new Button { Classes = { "accent" }, Content = Loc.T("ver.save.go"), Height = 32, MinHeight = 32, FontSize = 12, Padding = new Thickness(10, 0), Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(save, 1);
        versionForm.Children.Add(versionName); versionForm.Children.Add(save);
        save.Click += async (_, _) => await SaveNamedVersion();
        versionName.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SaveNamedVersion(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CloseVersionForm(); }
        };
        section.Children.Add(versionForm);
        section.Children.Add(historyVersions);
    }

    void CloseVersionForm()
    {
        versionForm.IsVisible = false;
        ShowSectionAction("Versiones", true);
        Timeline.Focus();
    }

    /// <summary>After an edit (called from <see cref="UpdateUndoButtons"/>): the list redraws once the edits stop.</summary>
    void ScheduleSteps()
    {
        if (!StepsVisible) return;
        stepsTimer.Stop();
        stepsTimer.Start();
    }

    /// <summary>The step list starts over (a new project, a version brought back, takes deleted): what it starts from.</summary>
    void ResetSteps(string origin)
    {
        stepsOrigin = origin;
        stepsShown = StepsPage;
    }

    void RefreshSteps()
    {
        stepsTimer.Stop();
        if (!StepsVisible) return;
        historySteps.Children.Clear();
        var st = store;
        if (st == null) return;
        var p = st.Project;
        var steps = undo.Steps(p, id => p.FindMedia(id)?.Name);
        if (steps.Count == 0)
        {
            historySteps.Children.Add(Help(Loc.T("steps.empty")));
            return;
        }
        bool editable = !st.ReadOnly && !restoring && !RecordingBusy && !Projects.IsVisible;
        int at = undo.Position;
        var list = new StackPanel { Margin = new Thickness(-16, -4) };
        for (int i = steps.Count - 1; i >= 0 && steps.Count - i <= stepsShown; i--)
        {
            var s = steps[i];
            list.Children.Add(StepRow(s.Index, s.Label.Icon, s.Label.Parts, s.At, s.Index == at, s.Done, editable));
        }
        if (steps.Count <= stepsShown)
        {
            string origin = undo.Trimmed ? Loc.T("steps.origin.trimmed") : stepsOrigin;
            list.Children.Add(StepRow(0, "origin", [new(origin)], null, at == 0, true, editable));
        }
        historySteps.Children.Add(list);
        if (steps.Count > stepsShown)
        {
            var more = new Button { Classes = { "ghost" }, Content = Loc.T("hist.more", steps.Count - stepsShown), FontSize = 11, Foreground = Palette.Dim,
                Padding = new Thickness(0), MinHeight = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            more.Click += (_, _) => { stepsShown += StepsPage; RefreshSteps(); };
            historySteps.Children.Add(more);
        }
    }

    static string StepGlyph(string icon) => icon switch
    {
        "plus" => "", "trash" => "", "move" => "", "cut" => "", "mic" => "", "volume" => "",
        "fx" => "", "folder" => "", "marker" => "", "color" => "", "video" => "", "lock" => "",
        "origin" => "", _ => "",
    };

    /// <summary>One step: where it leads, lit if it is where the project is now, greyed if it was undone.</summary>
    Control StepRow(int index, string icon, IReadOnlyList<ProjectHistory.Part> parts, DateTime? at, bool current, bool done, bool editable)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Opacity = done ? 1 : 0.5 };
        var glyph = HistoryIcon(StepGlyph(icon), 12, current ? Palette.Accent : Palette.Faint);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(glyph);
        var text = new TextBlock { FontSize = 12, Foreground = current ? Palette.Fg : Palette.Dim, TextWrapping = TextWrapping.Wrap };
        foreach (var part in parts)
        {
            var run = new Run(part.Text);
            if (part.Strong) run.Foreground = Palette.Fg;
            text.Inlines!.Add(run);
        }
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (at is { } t)
        {
            var time = new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, Text = t.ToLocalTime().ToString("HH:mm"), Margin = new Thickness(0, 1, 0, 0) };
            Grid.SetColumn(time, 2); grid.Children.Add(time);
        }
        var row = new Button
        {
            Classes = { "ghost", "rowmain" }, Content = grid, Padding = new Thickness(14, 6, 16, 6), CornerRadius = new CornerRadius(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = editable || current,
        };
        ToolTip.SetTip(row, Loc.T(current ? "steps.here" : done ? "steps.back" : "steps.forward"));
        row.Click += async (_, _) => { if (!current) await JumpToStep(index); };
        return new Border
        {
            Child = row, BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = current ? Palette.Accent : Brushes.Transparent, Background = current ? Palette.AccentSoft : Brushes.Transparent,
        };
    }

    async Task JumpToStep(int index)
    {
        if (store == null) return;
        bool back = index < undo.Position;
        await RestoreEdit(p => undo.JumpTo(p, index), Loc.T(back ? "steps.wentBack" : "steps.wentForward"));
    }

    // ---------- versions ----------

    /// <summary>Reads historial/ off the UI thread, then draws; a newer read wins over an older one still going.</summary>
    void ReloadVersions()
    {
        var st = store;
        if (st == null) { versions = []; versionsDir = null; RefreshVersions(); return; }
        if (versionsLoading) return;
        versionsLoading = true;
        string dir = st.Dir;
        Task.Run(() => ProjectStore.Snapshots(dir)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            versionsLoading = false;
            if (store?.Dir != dir) { ReloadVersions(); return; }
            if (!t.IsFaulted)
            {
                if (versionsDir != dir) versionsShown = VersionsPage;
                versions = t.Result;
                versionsDir = dir;
            }
            RefreshVersions();
        }), TaskScheduler.Default);
    }

    void RefreshVersions()
    {
        if (!StepsVisible) return;
        historyVersions.Children.Clear();
        var st = store;
        if (versionSaveStart != null) versionSaveStart.IsEnabled = st is { ReadOnly: false };
        if (st == null || versionsDir != st.Dir) return;
        if (versions.Count == 0)
        {
            historyVersions.Children.Add(Help(Loc.T("ver.none")));
            return;
        }
        bool editable = !st.ReadOnly && !restoring && !RecordingBusy && !Export.IsRunning && !versionBusy;
        var list = new StackPanel { Margin = new Thickness(-16, -4) };
        foreach (var g in versions.Take(versionsShown).GroupBy(s => s.Time.Date))
        {
            list.Children.Add(DayHeader(ProjectHistory.DayLabel(g.Key), list.Children.Count == 0));
            foreach (var s in g) list.Children.Add(s.File == renamingVersion ? VersionRenameRow(st, s) : VersionRow(st, s, editable));
        }
        historyVersions.Children.Add(list);
        if (versions.Count > versionsShown)
        {
            var more = new Button { Classes = { "ghost" }, Content = Loc.T("hist.more", versions.Count - versionsShown), FontSize = 11, Foreground = Palette.Dim,
                Padding = new Thickness(0), MinHeight = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            more.Click += (_, _) => { versionsShown += VersionsPage; RefreshVersions(); };
            historyVersions.Children.Add(more);
        }
    }

    /// <summary>What a version is called: its name, its export, what going back left it for, or "guardada sola".</summary>
    string VersionTitle(ProjectStore.Snapshot s) =>
        s.Name ?? (s.Export != null ? Loc.T("ver.ofExport", Path.GetFileName(s.Export.Replace('/', '\\')))
            : s.Before is { } before && versions.FirstOrDefault(v => v.File == before) is { Path: not null } from ? Loc.T("ver.before", VersionTime(from.Time))
            : s.Before != null ? Loc.T("ver.beforeOld")
            : Loc.T("ver.auto"));

    Control VersionRow(ProjectStore st, ProjectStore.Snapshot s, bool editable)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        string glyph = s.Name != null ? "" : s.Export != null ? "" : s.Before != null ? "" : "";
        grid.Children.Add(HistoryIcon(glyph, 12, s.Name != null ? Palette.Accent : Palette.Faint));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = VersionTitle(s), FontSize = 13, Foreground = s.Kept ? Palette.Fg : Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, Text = VersionTime(s.Time) });
        ToolTip.SetTip(text, s.Time.ToString("dd/MM/yyyy HH:mm:ss"));
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var rename = RowButton("", Loc.T(s.Name != null ? "ver.rename" : "ver.giveName"), () => { renamingVersion = s.File; RefreshVersions(); });
        rename.IsEnabled = editable;
        Grid.SetColumn(rename, 2); grid.Children.Add(rename);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(HistoryIcon("", 11, Palette.Dim));
        content.Children.Add(new TextBlock { Text = Loc.T("ver.restore"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var back = new Button { Classes = { "chip" }, Content = content, MinHeight = 28, Height = 28, Padding = new Thickness(8, 0), IsEnabled = editable };
        ToolTip.SetTip(back, Loc.T("ver.restore.tip"));
        back.Click += async (_, _) => await RestoreVersion(st, s);
        Grid.SetColumn(back, 3); grid.Children.Add(back);
        return HistoryRow(grid, [rename, back], new Thickness(16, 6, 16, 6));
    }

    /// <summary>The inline name box of one version: Enter keeps it, Esc leaves it as it was, empty takes the name off.</summary>
    Control VersionRenameRow(ProjectStore st, ProjectStore.Snapshot s)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        var box = new TextBox { Classes = { "field" }, Text = s.Name ?? "", Watermark = Loc.T("ver.name"), MaxLength = ProjectStore.MaxVersionName, FontSize = 12, Height = 32, MinHeight = 32 };
        grid.Children.Add(box);
        var ok = new Button { Classes = { "accent" }, Content = Loc.T("ver.save.go"), Height = 32, MinHeight = 32, FontSize = 12, Padding = new Thickness(10, 0) };
        Grid.SetColumn(ok, 1); grid.Children.Add(ok);
        async Task Keep()
        {
            renamingVersion = null;
            try
            {
                string? name = box.Text;
                await Task.Run(() => st.RenameVersion(s, name));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Status(Loc.T("ver.renameFail", e.Message));
            }
            ReloadVersions();
        }
        ok.Click += async (_, _) => await Keep();
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await Keep(); }
            else if (e.Key == Key.Escape) { e.Handled = true; renamingVersion = null; RefreshVersions(); }
        };
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
        return new Border { Child = grid, Padding = new Thickness(16, 6) };
    }

    async Task SaveNamedVersion()
    {
        string name = versionName.Text?.Trim() ?? "";
        if (name.Length == 0) { versionName.Focus(); return; }
        if (await SaveVersionNow(name, null) != null)
        {
            CloseVersionForm();
            Toast(Loc.T("ver.saved", name));
        }
    }

    /// <summary>The version an export leaves (asked by the user: to go back to the mix of a video already uploaded).</summary>
    async Task SaveExportVersion(string exportFile) => await SaveVersionNow(null, exportFile);

    /// <summary>Saves the project and a kept version of it now, waiting for the save in progress first.</summary>
    async Task<ProjectStore.Snapshot?> SaveVersionNow(string? name, string? export)
    {
        if (store is not { ReadOnly: false } st || versionBusy) return null;
        versionBusy = true;
        RefreshVersions();
        try
        {
            saveTimer.Stop();
            await saving;
            if (st != store) return null;
            string json = st.Serialize();
            dirty = false;
            var job = Task.Run(() => st.SaveVersion(json, name, export));
            saving = job;
            var s = await job;
            saveFailure = null;
            return s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            saving = Task.CompletedTask;
            MarkDirty();
            SaveSoon();
            Toast(Loc.T("ver.saveFail", e.Message));
            return null;
        }
        finally
        {
            versionBusy = false;
            ReloadVersions();
        }
    }
}
