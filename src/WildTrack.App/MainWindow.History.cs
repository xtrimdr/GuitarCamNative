using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WildTrack.App.Controls;
using WildTrack.Audio;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// The Biblioteca and Historial tabs of the project bar (once one tab, HistoryPanel.tsx). Biblioteca: Canales (filter,
/// search, by day, listen to the raw file in a mini player under its row (wave, seek, ±5 s), show in Explorer,
/// take off the timeline or use again, send one unused take to the Recycle Bin) and "Borrar no usados".
/// Historial: Pasos and Versiones (MainWindow.Steps.cs) and Exportaciones (watch, show, go back to the version it was made
/// from, Recycle Bin with an inline confirmation). Actividad, the log as sentences, left the tab when Pasos and
/// Versiones came (the user's call); the log is still written and wtlab still reads it. Both tabs are refreshed together
/// while either is on screen. Same as the web except:
/// there are no "Guardando / Reintentar" rows, as a native take is written to disk while it is recorded, and listening
/// goes through Windows' default output (the web's &lt;audio&gt;), not the ASIO engine. The work is in ProjectHistory.
/// </summary>
public partial class MainWindow
{
    readonly StackPanel historyPanel = new(), libraryPanel = new();
    readonly StackPanel historyTakes = new(), historyExports = new();
    readonly Border historyPurge = new() { Padding = new Thickness(16) };
    readonly TextBlock historyTotal = new() { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel historyFilters = new() { Spacing = 8 };
    readonly TextBox historySearch = new() { Classes = { "field" }, Watermark = Loc.T("hist.search"), Height = 32, MinHeight = 32, FontSize = 12, Padding = new Thickness(30, 0, 28, 0) };
    readonly Panel historySearchBox = new();
    readonly List<(string Key, ToggleButton Button, TextBlock Count)> historyFilterButtons = [];
    readonly Dictionary<string, long> historyBytes = [];
    readonly TakePreview takePreview = new();
    readonly DispatcherTimer historyClock = new() { Interval = TimeSpan.FromMinutes(1) };
    string historyFilter = "all";
    string? historyBusyId, historyBytesDir;
    string? historyDeletingExport, historyDeletingTake;
    bool historyExportBusy, historyTakeBusy, historyConfirmPurge, historyPurging;
    /// <summary>Peaks per take for the mini player's wave: enough bins for a wide sidebar.</summary>
    const int TakePeakBins = 600;
    /// <summary>How far the mini player's back / forward buttons jump.</summary>
    const double TakeSkipSec = 5;
    readonly Dictionary<string, float[]> takePeaks = [];
    readonly TakeScrubber takeScrubber = new();
    readonly TextBlock takeTime = new() { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid takePlayer = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(36, 4, 0, 0) };
    readonly DispatcherTimer takePlayerClock = new() { Interval = TimeSpan.FromMilliseconds(33) };
    int takeTimeShown = -1;

    bool HistoryVisible => projectTab is "history" or "library" && projectBarExpanded.IsVisible;

    void BuildHistoryPanel()
    {
        // Canales
        var reveal = HeaderAction("\uE838", Loc.T("hist.openFolder"), Loc.T("hist.openFolder.tip"), () => RevealInExplorer(store?.Dir, null));
        reveal.Foreground = Palette.Dim;
        var takes = ColorSection("Canales", "\uE714", action: reveal, host: libraryPanel, scope: "history");
        var seg = new UniformGrid { Rows = 1 };
        foreach (var (key, text) in new[] { ("all", Loc.T("hist.filter.all")), ("timeline", Loc.T("hist.filter.timeline")), ("unused", Loc.T("hist.filter.unused")) })
        {
            var count = new TextBlock { Classes = { "mono" }, FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            label.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(count);
            var b = new ToggleButton { Classes = { "seg" }, Content = label, Padding = new Thickness(4, 0) };
            b.Click += (_, _) => { historyFilter = key; RefreshHistory(); };
            historyFilterButtons.Add((key, b, count));
            seg.Children.Add(b);
        }
        historyFilters.Children.Add(new Border { Classes = { "seg" }, Child = seg, HorizontalAlignment = HorizontalAlignment.Stretch });
        historySearchBox.Children.Add(historySearch);
        historySearchBox.Children.Add(new TextBlock { Classes = { "icon" }, Text = "\uE721", FontSize = 12, Foreground = Palette.Faint, Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        var clear = new Button { Classes = { "ghost" }, Width = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center, Foreground = Palette.Faint,
            Content = new TextBlock { Classes = { "icon" }, Text = "\uE711", FontSize = 10 }, IsVisible = false };
        ToolTip.SetTip(clear, Loc.T("hist.search.clear"));
        clear.Click += (_, _) => historySearch.Text = "";
        historySearchBox.Children.Add(clear);
        historySearch.TextChanged += (_, _) =>
        {
            clear.IsVisible = !string.IsNullOrEmpty(historySearch.Text);
            RefreshHistory();
        };
        historySearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(historySearch.Text)) { historySearch.Text = ""; e.Handled = true; }
        };
        historyFilters.Children.Add(historySearchBox);
        takes.Children.Add(historyFilters);
        takes.Children.Add(historyTakes);
        takes.Children.Add(historyTotal);
        libraryPanel.Children.Add(historyPurge);

        BuildStepsPanel();
        ColorSection("Exportaciones", "\uE8B2", host: historyPanel, scope: "history").Children.Add(historyExports);

        BuildTakePlayer();
        takePreview.Stopped += RefreshHistory;
        historyClock.Tick += (_, _) =>
        {
            if (!HistoryVisible) return;
            RefreshHistory();
            ReloadVersions(); // the automatic ones are written in the background as the project saves
        };
        historyClock.Start();
    }

    /// <summary>Rebuilds the lists (only while the tab is on screen; showing it refreshes it).</summary>
    void RefreshHistory()
    {
        if (!HistoryVisible) return;
        historyTakes.Children.Clear();
        historyExports.Children.Clear();
        var st = store;
        if (StepsVisible && versionsDir != st?.Dir) ReloadVersions();
        RefreshSteps();
        RefreshVersions();
        historyPanel.IsEnabled = libraryPanel.IsEnabled = st != null;
        if (st == null)
        {
            historyFilters.IsVisible = historyTotal.IsVisible = false;
            historyPurge.Child = null;
            return;
        }
        var p = st.Project;
        bool editable = !st.ReadOnly && !restoring && !RecordingTake;
        MeasureHistoryBytes(st);

        // Canales
        var used = ProjectHistory.UsedMediaIds(p);
        var takes = p.Media.OrderByDescending(m => m.CreatedAt).ToList();
        var unused = takes.Where(m => !used.Contains(m.Id)).ToList();
        long Bytes(Media m) => historyBytes.GetValueOrDefault(m.Id);
        string q = historySearch.Text?.Trim() ?? "";
        var visible = takes.Where(m => (historyFilter != "timeline" || used.Contains(m.Id)) && (historyFilter != "unused" || !used.Contains(m.Id))
            && (q.Length == 0 || m.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
        historyFilters.IsVisible = takes.Count > 0;
        historySearchBox.IsVisible = takes.Count > 4;
        foreach (var (key, b, count) in historyFilterButtons)
        {
            b.IsChecked = key == historyFilter;
            count.Text = (key switch { "timeline" => takes.Count - unused.Count, "unused" => unused.Count, _ => takes.Count }).ToString();
        }
        if (takes.Count == 0) historyTakes.Children.Add(Help(Loc.T("hist.empty")));
        else
        {
            var list = new StackPanel { Margin = new Thickness(-16, -4) };
            if (visible.Count == 0)
                list.Children.Add(new TextBlock { Text = q.Length > 0 ? Loc.T("hist.noMatch", q) : historyFilter == "unused"
                    ? Loc.T("hist.noUnused") : Loc.T("hist.noneOnTimeline"),
                    FontSize = 12, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 8) });
            foreach (var g in visible.GroupBy(m => m.CreatedAt.ToLocalTime().Date))
            {
                list.Children.Add(DayHeader(ProjectHistory.DayLabel(g.Key), list.Children.Count == 0));
                foreach (var m in g) list.Children.Add(m.Id == historyDeletingTake ? TakeDeleteRow(st, m) : TakeRow(st, m, used.Contains(m.Id), editable));
            }
            historyTakes.Children.Add(list);
        }
        historyTotal.IsVisible = takes.Count > 0;
        historyTotal.Text = Loc.T("hist.total", ProjectHistory.FormatBytes(takes.Sum(Bytes)))
            + (unused.Count > 0 ? Loc.T("hist.totalUnused", ProjectHistory.FormatBytes(unused.Sum(Bytes))) : "");

        // Exportaciones
        var exports = p.Exports.OrderByDescending(e => e.CreatedAt).ToList();
        if (exports.Count == 0) historyExports.Children.Add(Help(Loc.T(AudioOnly ? "hist.exports.empty.audio" : "hist.exports.empty.video")));
        else
        {
            var list = new StackPanel { Margin = new Thickness(-16, -4) };
            foreach (var e in exports) list.Children.Add(e.File == historyDeletingExport ? ExportDeleteRow(st, e) : ExportRow(st, e, editable));
            historyExports.Children.Add(list);
        }

        historyPurge.Child = historyConfirmPurge ? PurgeConfirm(st, unused) : PurgeButton(unused, editable);
    }

    /// <summary>Take sizes come from disk: measured in the background, then the list refreshes.</summary>
    void MeasureHistoryBytes(ProjectStore st)
    {
        if (historyBytesDir != st.Dir) { historyBytes.Clear(); takePeaks.Clear(); } // a duplicated project keeps the same media ids
        historyBytesDir = st.Dir;
        var missing = st.Project.Media.Where(m => !historyBytes.ContainsKey(m.Id)).ToList();
        if (missing.Count == 0) return;
        foreach (var m in missing) historyBytes[m.Id] = 0;
        string dir = st.Dir;
        Task.Run(() => missing.Select(m => (m.Id, Bytes: ProjectHistory.MediaBytes(dir, m))).ToList()).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (store != st) return;
                foreach (var (id, bytes) in t.Result) historyBytes[id] = bytes;
                RefreshHistory();
            });
        }, TaskScheduler.Default);
    }

    static Control DayHeader(string label, bool first) => new TextBlock
    {
        Text = label.ToUpperInvariant(), Classes = { "label" }, Margin = new Thickness(16, first ? 4 : 12, 16, 4),
    };

    /// <summary>A Fluent glyph; without a brush it takes the button's color.</summary>
    static TextBlock HistoryIcon(string glyph, double size = 12, IBrush? brush = null)
    {
        var t = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (brush != null) t.Foreground = brush;
        return t;
    }

    /// <summary>The web's rowButton: 28×28, faint, Fg on hover.</summary>
    static Button RowButton(string glyph, string tip, Action click, bool danger = false)
    {
        var b = new Button { Classes = { "act" }, Content = HistoryIcon(glyph), Foreground = Palette.Faint };
        if (danger) b.Classes.Add("danger");
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A list row: Raised at 60 % on hover, and its hover-only actions show while pointed at or focused.</summary>
    static Border HistoryRow(Control child, IEnumerable<Control> hoverOnly, Thickness padding)
    {
        var hidden = hoverOnly.ToList();
        foreach (var h in hidden) h.Opacity = 0;
        var row = new Border { Child = child, Padding = padding, Background = Brushes.Transparent };
        void Show(bool on)
        {
            bool focus = row.IsKeyboardFocusWithin;
            row.Background = on || focus ? Palette.Alpha(Palette.RaisedColor, 0.6) : Brushes.Transparent;
            foreach (var h in hidden) h.Opacity = on || focus ? 1 : 0;
        }
        row.PointerEntered += (_, _) => Show(true);
        row.PointerExited += (_, _) => Show(false);
        row.GotFocus += (_, _) => Show(true);
        row.LostFocus += (_, _) => Show(row.IsPointerOver);
        return row;
    }

    Control TakeRow(ProjectStore st, Media m, bool inTimeline, bool editable)
    {
        bool current = takePreview.PlayingId == m.Id, playing = current && !takePreview.Paused;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        var play = RowButton(playing ? "\uE769" : "\uF5B0", Loc.T(playing ? "hist.pause" : current ? "hist.resume" : "hist.listen"), () => TogglePreview(st, m));
        if (current) play.Foreground = Palette.Accent;
        grid.Children.Add(play);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new TrimRow { Spacing = 8 };
        nameLine.Children.Add(new TextBlock { Text = m.Name, FontSize = 13, Foreground = inTimeline ? Palette.Fg : Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis });
        Border Badge(string text) => new()
        {
            Background = Palette.Hover, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeight.Medium, Foreground = Palette.Faint },
        };
        // where it came from (native only: the web has no imports)
        nameLine.Children.Add(Badge(m.Kind == "import" ? Loc.T("hist.kind.import") : Loc.T("hist.kind.recording")));
        if (!inTimeline) nameLine.Children.Add(Badge(Loc.T("hist.unusedBadge")));
        text.Children.Add(nameLine);
        text.Children.Add(new TextBlock
        {
            Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis,
            Text = $"{m.CreatedAt.ToLocalTime():HH:mm} · {ProjectHistory.FormatDuration(m.DurationSec)} · {ProjectHistory.FormatBytes(historyBytes.GetValueOrDefault(m.Id))}",
        });
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        var reveal = RowButton("\uE838", Loc.T("hist.reveal"), () => RevealInExplorer(st.Dir, m.Video ?? m.Audio));
        Grid.SetColumn(reveal, 2); grid.Children.Add(reveal);
        var hover = new List<Control> { reveal };
        if (!inTimeline)
        {
            var trash = RowButton("", Loc.T("hist.trashTake"), () => { historyDeletingTake = m.Id; RefreshHistory(); }, danger: true);
            trash.IsEnabled = editable && historyBusyId == null && !historyTakeBusy;
            hover.Add(trash);
            Grid.SetColumn(trash, 3); grid.Children.Add(trash);
        }
        Control action;
        if (historyBusyId == m.Id)
            action = new Panel { Width = 28, Height = 28, Children = { new Avalonia.Controls.Shapes.Arc { Classes = { "spin" }, Width = 14, Height = 14, Stroke = Palette.Dim } } };
        else if (inTimeline)
        {
            var remove = RowButton("\uE738", Loc.T("hist.removeFromTimeline"), () => RemoveFromTimeline(m));
            remove.IsEnabled = editable;
            hover.Add(remove);
            action = remove;
        }
        else
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(HistoryIcon("\uE710", 11, Palette.Dim));
            content.Children.Add(new TextBlock { Text = Loc.T("hist.use"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var use = new Button { Classes = { "chip" }, Content = content, MinHeight = 28, Height = 28, Padding = new Thickness(8, 0), IsEnabled = editable };
            ToolTip.SetTip(use, Loc.T("hist.use.tip"));
            use.Click += async (_, _) => await UseTake(m);
            action = use;
        }
        Grid.SetColumn(action, 4); grid.Children.Add(action);
        if (!current) return HistoryRow(grid, hover, new Thickness(8, 6, 16, 6));
        (takePlayer.Parent as Panel)?.Children.Remove(takePlayer); // it moves with the take across refreshes
        return HistoryRow(new StackPanel { Children = { grid, takePlayer } }, hover, new Thickness(8, 6, 16, 8));
    }

    /// <summary>The inline "send to the Recycle Bin?" of one unused take (like an export's).</summary>
    Control TakeDeleteRow(ProjectStore st, Media m)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(HistoryIcon("", 14, Palette.Rec));
        var text = new TextBlock { FontSize = 12, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Run(Loc.T("hist.trashAsk.a")));
        text.Inlines.Add(new Run(m.Name) { FontWeight = FontWeight.Medium });
        text.Inlines.Add(new Run(Loc.T("hist.trashAsk.b")));
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var no = new Button { Classes = { "btn", "sm" }, Content = Loc.T("hist.no"), Height = 28, IsEnabled = !historyTakeBusy };
        no.Click += (_, _) => { historyDeletingTake = null; RefreshHistory(); };
        Grid.SetColumn(no, 2); grid.Children.Add(no);
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (historyTakeBusy) label.Children.Add(new Avalonia.Controls.Shapes.Arc { Classes = { "spin" }, Width = 12, Height = 12, Stroke = Brushes.White });
        label.Children.Add(new TextBlock { Text = Loc.T("hist.delete") });
        var yes = new Button { Classes = { "btn", "sm", "danger" }, Content = label, Height = 28, IsEnabled = !historyTakeBusy };
        yes.Click += async (_, _) => await DeleteTake(st, m);
        Grid.SetColumn(yes, 3); grid.Children.Add(yes);
        if (!historyTakeBusy) Dispatcher.UIThread.Post(() => yes.Focus());
        return new Border { Child = grid, Background = Palette.RecSoft, Padding = new Thickness(16, 6, 8, 6) };
    }

    /// <summary>The mini player under the row that is playing: the wave (click or drag to jump), ±5 s, the time and stop.
    /// Built once and moved to that row on every refresh; its clock only runs while a take is loaded.</summary>
    void BuildTakePlayer()
    {
        Grid.SetColumnSpan(takeScrubber, 4);
        takePlayer.Children.Add(takeScrubber);
        takeScrubber.SeekRequested += f =>
        {
            takePreview.Seek(f * takePreview.DurationSec);
            TickTakePlayer();
        };
        var back = RowButton("", Loc.T("hist.back5"), () => SkipTake(-TakeSkipSec));
        var fwd = RowButton("", Loc.T("hist.fwd5"), () => SkipTake(TakeSkipSec));
        var stop = RowButton("", Loc.T("hist.stop"), () => { takePreview.Stop(); RefreshHistory(); });
        takeTime.Margin = new Thickness(8, 0);
        takeTime.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var (c, col) in new (Control, int)[] { (back, 0), (fwd, 1), (takeTime, 2), (stop, 3) })
        {
            Grid.SetRow(c, 1); Grid.SetColumn(c, col);
            takePlayer.Children.Add(c);
        }
        takePlayerClock.Tick += (_, _) => TickTakePlayer();
    }

    void SkipTake(double sec)
    {
        takePreview.Seek(takePreview.PositionSec + sec);
        TickTakePlayer();
    }

    /// <summary>Moves the wave's played edge every frame; the time text changes (and allocates) once a second.</summary>
    void TickTakePlayer()
    {
        if (takePreview.PlayingId == null) { takePlayerClock.Stop(); return; }
        double dur = takePreview.DurationSec, pos = takePreview.PositionSec;
        takeScrubber.SetProgress(dur > 0 ? pos / dur : 0);
        int whole = (int)pos;
        if (whole == takeTimeShown) return;
        takeTimeShown = whole;
        takeTime.Text = $"{ProjectHistory.FormatDuration(whole)} / {ProjectHistory.FormatDuration(dur)}";
    }

    /// <summary>The take's wave for the mini player: read once in the background, then kept while the project is open.</summary>
    void LoadTakePeaks(string id, string path)
    {
        if (takePeaks.TryGetValue(id, out var known)) { takeScrubber.SetPeaks(known); return; }
        takeScrubber.SetPeaks(null);
        Task.Run(() => TakePreview.Peaks(path, TakePeakBins)).ContinueWith(t =>
        {
            if (t.IsFaulted) return; // the flat line stays; listening still works
            Dispatcher.UIThread.Post(() =>
            {
                takePeaks[id] = t.Result;
                if (takePreview.PlayingId == id) takeScrubber.SetPeaks(t.Result);
            });
        }, TaskScheduler.Default);
    }

    Control ExportRow(ProjectStore st, ExportRecord e, bool editable)
    {
        string name = Path.GetFileName(e.File.Replace('/', '\\'));
        bool audioFile = WildTrack.Audio.AudioExporter.IsAudioFile(name); // "Exportar audio" of a solo audio project
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 4 };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        body.Children.Add(HistoryIcon(audioFile ? "\uE8D6" : "\uE8B2", 14, Palette.Faint));
        var text = new StackPanel();
        var title = new TextBlock { Text = name, FontSize = 13, Foreground = Palette.Fg, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(title, e.File);
        text.Children.Add(title);
        var meta = new List<string> { ProjectHistory.FormatAgo(e.CreatedAt) };
        if (e.Aspect != null) meta.Add(e.Aspect);
        meta.Add(Loc.T(e.TakeIds.Count == 1 ? "hist.channels.one" : "hist.channels.many", e.TakeIds.Count));
        if (e.DurationSec > 0) meta.Add(ProjectHistory.FormatDuration(e.DurationSec));
        meta.Add(ProjectHistory.FormatBytes(e.Bytes));
        text.Children.Add(new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, Text = string.Join(" · ", meta), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); body.Children.Add(text);
        var open = new Button { Classes = { "ghost" }, Content = body, Padding = new Thickness(16, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, CornerRadius = new CornerRadius(0) };
        ToolTip.SetTip(open, audioFile ? Loc.T("hist.viewFile") : Loc.T("hist.viewVideo"));
        open.Click += (_, _) =>
        {
            if (RecordingTake || Export.IsRunning) return;
            engine.Pause();
            Export.ShowExported(st.Dir, e);
        };
        grid.Children.Add(open);
        var reveal = RowButton("\uE838", Loc.T("hist.reveal"), () => RevealInExplorer(null, ProjectHistory.ExportPath(st.Dir, e)));
        Grid.SetColumn(reveal, 1); grid.Children.Add(reveal);
        var delete = RowButton("\uE74D", audioFile ? Loc.T("hist.trashFile") : Loc.T("hist.trashVideo"), () => { historyDeletingExport = e.File; RefreshHistory(); }, danger: true);
        delete.IsEnabled = editable;
        Grid.SetColumn(delete, 3); grid.Children.Add(delete);
        var hover = new List<Control> { reveal, delete };
        // the mix it was made with, saved as a version when it finished (exports older than that have none)
        if (versionsDir == st.Dir && versions.FirstOrDefault(v => v.Export == e.File) is { Path: not null } version)
        {
            var back = RowButton("\uE7A7", Loc.T("hist.exportVersion"), async () => await RestoreVersion(st, version));
            back.IsEnabled = editable && !Export.IsRunning;
            Grid.SetColumn(back, 2); grid.Children.Add(back);
            hover.Insert(0, back);
        }
        return HistoryRow(grid, hover, new Thickness(0, 0, 8, 0));
    }

    Control ExportDeleteRow(ProjectStore st, ExportRecord e)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(HistoryIcon("\uE74D", 14, Palette.Rec));
        var text = new TextBlock { FontSize = 12, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Run(Loc.T("hist.trashAsk.a")));
        text.Inlines.Add(new Run(Path.GetFileName(e.File.Replace('/', '\\'))) { FontWeight = FontWeight.Medium });
        text.Inlines.Add(new Run(Loc.T("hist.trashAsk.b")));
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var no = new Button { Classes = { "btn", "sm" }, Content = Loc.T("hist.no"), Height = 28, IsEnabled = !historyExportBusy };
        no.Click += (_, _) => { historyDeletingExport = null; RefreshHistory(); };
        Grid.SetColumn(no, 2); grid.Children.Add(no);
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (historyExportBusy) label.Children.Add(new Avalonia.Controls.Shapes.Arc { Classes = { "spin" }, Width = 12, Height = 12, Stroke = Brushes.White });
        label.Children.Add(new TextBlock { Text = Loc.T("hist.delete") });
        var yes = new Button { Classes = { "btn", "sm", "danger" }, Content = label, Height = 28, IsEnabled = !historyExportBusy };
        yes.Click += async (_, _) => await DeleteExport(st, e);
        Grid.SetColumn(yes, 3); grid.Children.Add(yes);
        if (!historyExportBusy) Dispatcher.UIThread.Post(() => yes.Focus());
        return new Border { Child = grid, Background = Palette.RecSoft, Padding = new Thickness(16, 6, 8, 6) };
    }

    Control PurgeButton(List<Media> unused, bool editable)
    {
        long bytes = unused.Sum(m => historyBytes.GetValueOrDefault(m.Id));
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(HistoryIcon("\uE74D"));
        content.Children.Add(new TextBlock
        {
            Text = unused.Count == 0 ? Loc.T("hist.purge.none") : Loc.T("hist.purge.button", unused.Count, ProjectHistory.FormatBytes(bytes)),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        });
        var b = new Button { Classes = { "chip", "purge" }, Content = content, Height = 32, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = editable && unused.Count > 0 };
        b.Click += (_, _) => { historyConfirmPurge = true; RefreshHistory(); };
        return b;
    }

    Control PurgeConfirm(ProjectStore st, List<Media> unused)
    {
        long bytes = unused.Sum(m => historyBytes.GetValueOrDefault(m.Id));
        var stack = new StackPanel { Spacing = 12 };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        var warn = HistoryIcon("\uE7BA", 14, Palette.Rec);
        warn.VerticalAlignment = VerticalAlignment.Top;
        line.Children.Add(warn);
        var text = new TextBlock
        {
            FontSize = 12, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap,
            Text = Loc.T("hist.purge.confirm", Loc.T(unused.Count == 1 ? "hist.channels.one" : "hist.channels.many", unused.Count), ProjectHistory.FormatBytes(bytes)),
        };
        Grid.SetColumn(text, 1); line.Children.Add(text);
        stack.Children.Add(line);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Classes = { "btn", "sm" }, Content = Loc.T("transport.count.cancel"), IsEnabled = !historyPurging };
        cancel.Click += (_, _) => { historyConfirmPurge = false; RefreshHistory(); };
        buttons.Children.Add(cancel);
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (historyPurging) label.Children.Add(new Avalonia.Controls.Shapes.Arc { Classes = { "spin" }, Width = 12, Height = 12, Stroke = Brushes.White });
        label.Children.Add(new TextBlock { Text = Loc.T("hist.purge.go") });
        var go = new Button { Classes = { "btn", "sm", "danger" }, Content = label, IsEnabled = !historyPurging && unused.Count > 0 };
        go.Click += async (_, _) => await PurgeUnused(st, unused);
        buttons.Children.Add(go);
        stack.Children.Add(buttons);
        return new Border
        {
            Child = stack, Padding = new Thickness(12), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = Palette.Alpha(Palette.RecColor, 0.4), Background = Palette.RecSoft,
        };
    }

    void TogglePreview(ProjectStore st, Media m)
    {
        if (takePreview.PlayingId == m.Id)
        {
            if (takePreview.Paused) takePreview.Resume(); else takePreview.Pause();
            RefreshHistory();
            return;
        }
        try
        {
            if (st.Resolve(m.Audio) is not { } path || !File.Exists(path)) throw new FileNotFoundException();
            takePreview.Play(m.Id, path);
            takeTimeShown = -1;
            LoadTakePeaks(m.Id, path);
            TickTakePlayer();
            takePlayerClock.Start();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or NAudio.MmException or FormatException or InvalidDataException)
        {
            takePreview.Stop();
            Status(e is FileNotFoundException ? Loc.T("hist.err.notFound", m.Name) : Loc.T("hist.err.play", m.Name, e.Message));
        }
        RefreshHistory();
    }

    Task RemoveFromTimeline(Media m) => EditTimeline(p => { ProjectHistory.RemoveFromTimeline(p, m.Id); return null; }, null);

    async Task UseTake(Media m)
    {
        if (store == null || historyBusyId != null) return;
        historyBusyId = m.Id;
        RefreshHistory();
        try
        {
            string? selected = sel?.Track.Id;
            await EditTimeline(p => ClipEditing.AddMedia(p, m.Id, selected), null);
        }
        finally
        {
            historyBusyId = null;
            RefreshHistory();
        }
    }

    async Task PurgeUnused(ProjectStore st, List<Media> unused)
    {
        if (st != store || st.ReadOnly || historyPurging) return;
        takePreview.Stop();
        historyPurging = true;
        RefreshHistory();
        try
        {
            st.LogTimelineChanges(); // like the web, which saves the timeline before deciding what is unused
            var (purged, bytes, failure) = await Task.Run(() => ProjectHistory.TrashMedia(st.Dir, unused));
            ForgetTrashed(st, purged, bytes);
            if (failure != null) Status(Loc.T("hist.err.purge", failure.Message));
            else historyConfirmPurge = false;
        }
        finally
        {
            historyPurging = false;
            RefreshHistory();
        }
    }

    /// <summary>One unused take to the Recycle Bin, after its inline confirmation.</summary>
    async Task DeleteTake(ProjectStore st, Media m)
    {
        if (st != store || st.ReadOnly || historyTakeBusy) return;
        if (takePreview.PlayingId == m.Id) takePreview.Stop(); // the file is open while it plays
        historyTakeBusy = true;
        RefreshHistory();
        try
        {
            st.LogTimelineChanges();
            if (ProjectHistory.UsedMediaIds(st.Project).Contains(m.Id)) return; // it went back to the timeline meanwhile
            var (purged, bytes, failure) = await Task.Run(() => ProjectHistory.TrashMedia(st.Dir, [m]));
            ForgetTrashed(st, purged, bytes);
            if (failure != null) Status(Loc.T("hist.err.trashTake", m.Name, failure.Message));
        }
        finally
        {
            historyTakeBusy = false;
            historyDeletingTake = null;
            RefreshHistory();
        }
    }

    void ForgetTrashed(ProjectStore st, List<Media> purged, long bytes)
    {
        ProjectHistory.ForgetPurged(st.Project, purged, bytes);
        foreach (var m in purged) { historyBytes.Remove(m.Id); takePeaks.Remove(m.Id); }
        if (purged.Count == 0) return;
        undo.Clear(); // purged files are gone; older states could reference them
        ResetSteps(Loc.T("steps.origin.purged"));
        UpdateUndoButtons();
        MarkDirty();
        SaveSoon();
    }

    async Task DeleteExport(ProjectStore st, ExportRecord e)
    {
        if (st != store || st.ReadOnly || historyExportBusy) return;
        historyExportBusy = true;
        RefreshHistory();
        try
        {
            await Task.Run(() =>
            {
                ProjectHistory.TrashExport(st.Dir, e);
                st.UnlinkExport(e.File);
            });
            ProjectHistory.ForgetExport(st.Project, e);
            ReloadVersions();
            MarkDirty();
            SaveSoon();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Status(Loc.T("hist.err.export", ex.Message));
        }
        finally
        {
            historyExportBusy = false;
            historyDeletingExport = null;
            RefreshHistory();
        }
    }

    /// <summary>Shows a file selected in Explorer (or opens the folder when there is no file).</summary>
    void RevealInExplorer(string? dir, string? file)
    {
        string? path = file == null ? dir : dir == null || Path.IsPathRooted(file) ? file : Path.Combine(dir, file.Replace('/', '\\'));
        if (path == null) return;
        try
        {
            if (file != null && File.Exists(path)) Explorer.Select(path);
            else if ((file == null ? path : Path.GetDirectoryName(path)) is { } folder && Directory.Exists(folder))
                Explorer.Open(folder);
            else Status(Loc.T("hist.err.moved"));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Status(Loc.T("hist.err.explorer", e.Message));
        }
    }
}
