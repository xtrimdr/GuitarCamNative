using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// Port of Mixer.tsx and MiniTimeline.tsx: the bottom view that alternates with the timeline (X). One strip per track,
/// empty ones too ("vacía": what you set there goes to the next take), wide with a panel per effect or compact with
/// knobs; the buses (MainWindow.Buses.cs); the master with its LIM light and its card (MainWindow.Master.cs); pan templates for the whole
/// mix. Every change is an undo step (same keys as the inspector), saved and heard live. Same as the web except: the
/// toggle sits in the bottom transport bar (where the native transport is), the EQ summary has no live spectrum yet
/// (like the inspector), and empty strips edit their channel without the take analysis (as in the web). Also unlike
/// the web (asked by the user): every strip is a card, all strips share the same rows so every meter has the same
/// height, and narrow effect panels rearrange instead of cutting their controls. Wide strips keep their head and pan
/// fixed and scroll only the effects, each in its own panel; compact strips put the record-arm ● under the name, since
/// ● M S C don't fit side by side.
/// Track folders (native only) show as a tinted group with their name above it: a folder strip first (no fader: its
/// output, its sends and the meter of its tracks' sum), then its tracks, unless the group is folded.
/// </summary>
public partial class MainWindow
{
    // Strip widths (px), as in the web (compact a bit wider for the card's border). "Amplio" strips grow to share the free width.
    // Buses are racks (their chain beside the fader), so they take more room than a track.
    const double CompactW = 104, WideMin = 200, WideMax = 400, ReturnCompactW = 270, ReturnWideW = 330, MasterW = 76;
    /// <summary>Rows every strip shares (takes, returns and master), so all faders and meters start and end at the same
    /// height: head, controls (compact strips only: in wide ones they sit beside the fader), fader, foot.</summary>
    const double StripHeadH = 38, StripFootH = 22;
    /// <summary>The compact strip's fixed rows (px): a list's title, the pan bar and M S ●.</summary>
    const double ListHeadH = 20, PanRowH = 26, TogglesRowH = 30;
    /// <summary>The default heights of the effects and sends lists (px): four effects and two sends.</summary>
    const double FxListH = 4 * (CompactRowH + 1), SendsListH = 2 * (CompactRowH + 1);
    /// <summary>The most a list can be stretched to (px).</summary>
    const double ListMaxH = 400;

    /// <summary>
    /// The compact strips' controls row (px), the same on every strip so faders stay level: the effects and the sends
    /// (each shown or not, at the height the user dragged it to) plus pan and M S ●.
    /// </summary>
    double StripTopH => (mixerFx ? ListHeadH + mixerFxH : 0) + (SendsRow ? ListHeadH + mixerSendsH : 0) + PanRowH + TogglesRowH;
    /// <summary>The sends' section takes its room when shown and there is a bus to send to.</summary>
    bool SendsRow => mixerSends && Buses.Count > 0;
    /// <summary>Space between strip cards.</summary>
    const double StripGap = 6;
    /// <summary>Below this width an effect panel drops its ON/OFF text and icon, and puts its knob over its other controls.</summary>
    const double FxNarrowW = 150;
    /// <summary>The mixer keeps its own height: wide strips want more room than the timeline.</summary>
    const double MixerDefaultH = 440, MixerMinH = 250;

    /// <summary>One way of drawing the strips (compact or wide) with what the window updates on it.</summary>
    sealed class MixerLayout
    {
        public readonly Grid Strips = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), IsVisible = false };
        /// <summary>The order and sides of its strips, for dragging them (MainWindow.MixerOrder.cs).</summary>
        public readonly MixerOrder Order = new();
        public readonly List<Action> Refresh = [];
        /// <summary>The strips' Transponer tags, refreshed while a transpose is being made (its progress).</summary>
        public readonly List<Action> Transpose = [];
        public readonly Dictionary<string, (MixerMeter Meter, Action<double> Gr)> Meters = [];
        /// <summary>The track strips' cards, resized in place when only the strip width changes.</summary>
        public readonly List<Border> Boxes = [];
        /// <summary>Every strip's controls row and the compact lists' rows with what they hold, resized in place (<see cref="ApplyMixerHeights"/>).</summary>
        public readonly List<RowDefinition> TopRows = [];
        public readonly List<(RowDefinition Row, Control What, bool Head)> FxRows = [], SendRows = [];
        /// <summary>The folders' and buses' meters, by node id (<see cref="Audio.PlaybackEngine.Node"/>).</summary>
        public readonly List<(string Id, MixerMeter Meter)> NodeMeters = [];
        public MixerMeter? Master;
        public TextBlock? Lim;
        /// <summary>The master's short-term LUFS readout and the tenth it shows (int.MaxValue = not yet written).</summary>
        public TextBlock? Lufs;
        public int LufsTenths = int.MaxValue;
    }

    // Both layouts are built together whenever the tracks change, and "Compacto / Amplio" only shows the other one:
    // building a mixer's worth of controls makes the GC stop every thread, the audio callback's too, for 10-40 ms.
    readonly MixerLayout compactMixer = new(), wideMixer = new();
    /// <summary>The layout on screen (or the one being built).</summary>
    MixerLayout mx = null!;
    readonly Panel mixerStrips = new();
    readonly ScrollViewer mixerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly MiniTimeline miniTimeline = new();
    /// <summary>The mini timeline's own ruler switch (asked by the user), the same mode as the timeline's (<see cref="SetRulerMode"/>).</summary>
    readonly Button miniBarsButton = RulerButton("", "ruler.bars.tip"), miniTimeButton = RulerButton("", "ruler.time.tip");
    /// <summary>The switch and the mini timeline, the row over the mixer and the Mastering view.</summary>
    Border miniRow = null!;
    Button? panButton;
    string mixerKey = "";
    string mixerWidth = "auto";
    double panWidth = 0.3; // the templates' opening; asked by the user (the web starts at 60%)
    const double TimelineDefaultH = 280, TimelineMinH = 110; // web: TIMELINE_MIN_H (its default is 240; the native layout keeps 280)
    double timelineH = TimelineDefaultH, mixerH = MixerDefaultH;
    float masterShowL, masterShowR;
    bool mixerView;
    /// <summary>The mixer's head row (title, width…): hidden while the Mastering view uses the mixer's place.</summary>
    Control? mixerHeader;

    /// <summary>Preset names and hints come from the engine in Spanish: they are looked up by id (or by name for the buses) and fall back to that text.</summary>
    static string CompPresetName(CompPresetDef p) => Loc.Or("insp.comp." + p.Id, p.Label);
    static string CompPresetHint(CompPresetDef p) => Loc.Or("insp.comp." + p.Id + ".hint", p.Hint);
    static string PresetLabel(string? id) => id == "custom" ? Loc.T("insp.comp.manual") : CompPresets.All.FirstOrDefault(p => p.Id == id) is { } p ? CompPresetName(p) : CompPresets.PresetLabel(id);
    static string EqPresetName(EqPresetDef p) => Loc.Or("mix.eq." + p.Id, p.Label);
    static string EqPresetHint(EqPresetDef p) => Loc.Or("mix.eq." + p.Id + ".hint", p.Hint);
    static string BusPresetName(string kind, string label) => Loc.Or($"mix.{kind}.preset.{label}", label);
    static string BusPresetHint(string kind, string label, string hint) => Loc.Or($"mix.{kind}.preset.{label}.hint", hint);

    static string FmtPan(double v) => Math.Abs(v) < 0.01 ? "C" : v < 0 ? $"L{Math.Round(-v * 100):0}" : $"R{Math.Round(v * 100):0}";
    static string FmtHz(double v) => v >= 1000 ? $"{v / 1000:0.0}k" : $"{Math.Round(v):0}";

    /// <summary>Knobs for wide ranges move on a log scale (0..1 knob position), like the web's logMap.</summary>
    static (Func<double, double> To, Func<double, double> From) LogMap(double lo, double hi) =>
        (v => Math.Log(Math.Clamp(v, lo, hi) / lo) / Math.Log(hi / lo), p => Math.Round(lo * Math.Pow(hi / lo, p) * 100) / 100);
    static readonly (Func<double, double> To, Func<double, double> From) DecayMap = LogMap(0.2, 10), ToneMap = LogMap(1000, 18000),
        LowCutMap = LogMap(20, 1000), DelayMsMap = LogMap(20, 2000);

    static ReverbParams ToParams(ReverbSettings r) => new(r.Decay, r.PreDelayMs, r.Tone, r.LowCut, r.Width, r.ReturnGain, r.Muted);
    static DelayParams ToParams(DelaySettings d) => new(DelayParams.ParseMode(d.Mode), d.Sync, d.Division, d.TimeMs, d.Feedback, d.Tone, d.LowCut, d.Width, d.Duck, d.ReturnGain, d.Muted);

    void InitMixer()
    {
        mixerWidth = AppConfig.Get("mixerWidth") is "compact" or "wide" ? AppConfig.Get("mixerWidth")! : "auto";
        if (double.TryParse(AppConfig.Get("mixerHeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h))
            mixerH = Math.Max(MixerMinH, h);
        if (double.TryParse(AppConfig.Get("timelineHeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double th))
            timelineH = Math.Max(TimelineMinH, th);
        var bottom = Root.RowDefinitions[3];
        bottom.MinHeight = TimelineMinH;
        bottom.Height = new GridLength(timelineH);
        // Like the web's resize handle: the bottom view keeps its height, double click goes back to the default.
        BottomSplitter.DragCompleted += (_, _) => SaveBottomHeight();
        AudioSplitter.DragCompleted += (_, _) =>
        {
            mixerH = AudioArea.RowDefinitions[2].ActualHeight;
            SaveViewPref("mixerHeight", mixerH.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        AudioSplitter.DoubleTapped += (_, _) => { mixerH = MixerDefaultH; ApplyAudioMixerRows(); SaveViewPref("mixerHeight", mixerH.ToString(System.Globalization.CultureInfo.InvariantCulture)); };
        BottomSplitter.DoubleTapped += (_, _) =>
        {
            bottom.Height = new GridLength(mixerView ? MixerDefaultH : TimelineDefaultH);
            Dispatcher.UIThread.Post(SaveBottomHeight, DispatcherPriority.Background);
        };

        miniTimeline.SeekRequested += sec => engine.Seek(sec);
        miniTimeline.ScrubChanged += Scrub;
        var header = mixerHeader = BuildMixerHeader();
        mx = compactMixer;
        mixerStrips.Children.Add(compactMixer.Strips);
        mixerStrips.Children.Add(wideMixer.Strips);
        mixerScroll.Content = mixerStrips;
        InitMixerMenu();
        mixerScroll.SizeChanged += (_, _) => { mixerStrips.MinWidth = mixerScroll.Viewport.Width; RefreshMixer(); };
        // the same two-button switch as the timeline's corner
        var rulerSwitch = new Border
        {
            Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { miniBarsButton, miniTimeButton } },
        };
        miniBarsButton.Click += (_, _) => SetRulerMode(true);
        miniTimeButton.Click += (_, _) => SetRulerMode(false);
        DockPanel.SetDock(rulerSwitch, Dock.Left);
        miniRow = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Height = miniTimeline.Height + 1,
            Child = new DockPanel { Children = { rulerSwitch, miniTimeline } },
        };
        DockPanel.SetDock(miniRow, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        MixerHost.Children.Add(miniRow);
        MixerHost.Children.Add(header);
        // the DockPanel fills only its last child: the mixer and the Mastering view share it, so both fill the place
        MixerHost.Children.Add(new Panel { Children = { mixerScroll, masteringScroll } });
        InitMastering();

        TimelineViewButton.Click += (_, _) => SetMixerView(false);
        MixerViewButton.Click += (_, _) => ShowBottomView(mastering: false);
    }

    void SetMixerView(bool mixer)
    {
        if (mixer == mixerView) return;
        var row = Root.RowDefinitions[3];
        // stacked (solo audio, Vertical layout) both views fill the place beside the inspector and the bottom row stays closed
        if (!stackedViews)
        {
            // the Mastering view's fitted height is not the mixer's
            if (mixerView) { if (!masteringView) mixerH = row.ActualHeight; } else timelineH = row.ActualHeight;
            row.MinHeight = mixer ? MixerMinH : TimelineMinH;
            row.Height = new GridLength(mixer ? mixerH : timelineH);
        }
        mixerView = mixer;
        TimelineHost.IsVisible = ToolBox.IsEnabled = !mixer || stackedViews;
        MixerHost.IsVisible = mixer;
        miniRow.IsVisible = !stackedViews;
        ApplyAudioMixerRows();
        ToolTip.SetTip(BottomSplitter, Loc.T(mixer ? "splitter.mixer" : "splitter.timeline"));
        TimelineViewButton.Classes.Set("on", !mixer);
        MixerViewButton.Classes.Set("on", mixer);
        SaveViewPref(mixer ? "timelineHeight" : "mixerHeight", (mixer ? timelineH : mixerH).ToString(System.Globalization.CultureInfo.InvariantCulture));
        RefreshMixer();
        ApplyMasteringView();
    }

    /// <summary>Remembers the height of the view on screen (gc.timelineHeight / gc.mixerHeight in the web).</summary>
    void SaveBottomHeight()
    {
        if (stackedViews || (mixerView && masteringView)) return;
        double h = Root.RowDefinitions[3].ActualHeight;
        if (mixerView) mixerH = h; else timelineH = h;
        SaveViewPref(mixerView ? "mixerHeight" : "timelineHeight", h.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    Control BuildMixerHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        // the title (asked by the user: one with some presence): the mixer's glyph in the accent and its name
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Classes = { "icon" }, Text = "\ue9e9", FontSize = 16, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = Loc.T("mixer.title.name"), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(title);
        row.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Line });
        row.Children.Add(panButton = BuildPanMenu());
        row.Children.Add(AddSeparatorButton());
        row.Children.Add(SectionSwitch(FxIcon.Eq, "mixer.fx", () => mixerFx, v => mixerFx = v, "mixer.fx"));
        row.Children.Add(SectionSwitch(FxIcon.Reverb, "mixer.sends", () => mixerSends, v => mixerSends = v, "mixer.sends"));

        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label, tip) in new[]
        {
            ("auto", Loc.T("mixer.width.auto"), Loc.T("mixer.width.auto.tip")),
            ("compact", Loc.T("mixer.width.compact"), Loc.T("mixer.width.compact.tip")),
            ("wide", Loc.T("mixer.width.wide"), Loc.T("mixer.width.wide.tip")),
        })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = label, FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { mixerWidth = id; SaveViewPref("mixerWidth", id); RefreshMixer(); };
            // the header's own refreshers survive strip rebuilds
            headerRefresh.Add(() => b.IsChecked = mixerWidth == id);
            seg.Children.Add(b);
        }

        // Cubase's "deactivate all mute / solo states" (native only), lit while any track has one
        var clear = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        foreach (bool solo in new[] { false, true })
        {
            var b = new Button { Classes = { "chip" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = solo ? "S" : "M", Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold } };
            b.Click += (_, _) => ClearAllToggles(solo);
            headerRefresh.Add(() =>
            {
                bool lit = tracks.Any(t => solo ? t.Channel.Solo : t.Channel.Muted);
                b.IsEnabled = store != null && !store.ReadOnly && !restoring && tracks.Any(t => t.Clips.Count > 0);
                b.Classes.Set("lit", lit);
                b.Background = lit ? (solo ? Palette.Fg : Palette.Accent) : Brushes.Transparent;
                b.BorderBrush = lit ? (solo ? Palette.Fg : Palette.Accent) : Palette.Line;
                ((TextBlock)b.Content!).Foreground = lit ? Palette.App : Palette.Faint;
                ToolTip.SetTip(b, lit ? Loc.T(solo ? "mixer.clearSolos" : "mixer.clearMutes") : Loc.T(solo ? "timeline.noSolos" : "timeline.noMutes"));
            });
            clear.Children.Add(b);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 0) };
        grid.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(clear, 1); grid.Children.Add(clear);
        var segBox = new Border { Classes = { "seg" }, Child = seg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(segBox, 2); grid.Children.Add(segBox);
        return new Border { Height = 40, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    readonly List<Action> headerRefresh = [];

    /// <summary>Whether the strips show their effects and their sends to the buses (the header's switches, kept in config.json).</summary>
    bool mixerFx = AppConfig.Get("mixer.fx") != "off", mixerSends = AppConfig.Get("mixer.sends") != "off";
    /// <summary>The compact lists' heights (px), the same on every strip: the user drags them (<see cref="ListBox"/>).</summary>
    double mixerFxH = ListHeight("mixer.fxH", FxListH), mixerSendsH = ListHeight("mixer.sendsH", SendsListH);

    static double ListHeight(string key, double fallback) =>
        double.TryParse(AppConfig.Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h)
            ? Math.Clamp(h, CompactRowH + 6, ListMaxH) : fallback;

    /// <summary>
    /// A switch of the mixer's header (asked by the user): "Efectos" or "Envíos" shows or hides that section on every
    /// strip. <paramref name="key"/> names its text, its tooltips (".show" / ".hide") and its config.json entry.
    /// </summary>
    ToggleButton SectionSwitch(string iconKey, string key, Func<bool> get, Action<bool> set, string configKey)
    {
        var icon = FxIcon.Create(iconKey, 12);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var b = new ToggleButton { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), Focusable = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon,
                new TextBlock { Text = Loc.T(key), FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
        b.Click += (_, _) =>
        {
            set(!get());
            SaveViewPref(configKey, get() ? "on" : "off");
            ApplyMixerHeights();
            RefreshMixer();
        };
        headerRefresh.Add(() =>
        {
            b.IsChecked = get();
            FxIcon.SetColor(icon, get() ? Palette.Accent : Palette.Faint);
            ToolTip.SetTip(b, Loc.T(get() ? key + ".hide" : key + ".show"));
        });
        return b;
    }

    /// <summary>The pan templates in one drop-down with their opening (asked by the user; the web shows a chip per
    /// template and the "Apertura" slider in the header). Picking a template applies it and closes the menu.</summary>
    Button BuildPanMenu()
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(new TextBlock { Text = Loc.T("mixer.pan"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Classes = { "chip" }, Content = label, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0) };
        ToolTip.SetTip(button, Loc.T("mixer.pan.tip"));
        Avalonia.Automation.AutomationProperties.SetName(button, Loc.T("mixer.pan"));

        var list = new StackPanel { Width = 280, Spacing = 2 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = list };
        var open = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(8, 4, 8, 8) };
        ToolTip.SetTip(open, Loc.T("mixer.pan.width.tip"));
        open.Children.Add(new TextBlock { Text = Loc.T("mixer.pan.width"), FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var width = new InspectorRange { Minimum = 0.1, Maximum = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true, SmallChange = 0.05, Value = panWidth, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(width, Loc.T("mixer.pan.width"));
        var widthText = new TextBlock { Text = $"{panWidth * 100:0}%", Classes = { "mono" }, FontSize = 11, Foreground = Palette.Dim, Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        width.ValueChanged += (_, e) => { panWidth = Math.Round(e.NewValue * 20) / 20; widthText.Text = $"{panWidth * 100:0}%"; };
        Grid.SetColumn(width, 1); open.Children.Add(width);
        Grid.SetColumn(widthText, 2); open.Children.Add(widthText);
        list.Children.Add(open);
        list.Children.Add(new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var t in PanTemplates.All)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = t.Label, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg });
            text.Children.Add(new TextBlock { Text = t.Hint, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
            var item = new Button { Classes = { "ghost" }, Content = text, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6) };
            string id = t.Id;
            item.Click += (_, _) => { flyout.Hide(); ApplyPanTemplate(id); };
            list.Children.Add(item);
        }
        button.Click += (_, _) => flyout.ShowAt(button);
        return button;
    }

    void ApplyPanTemplate(string id)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        var takes = tracks.Where(t => t.Clips.Count > 0).ToList();
        if (takes.Count == 0) return;
        undo.Checkpoint(store.Project);
        var pans = PanTemplates.Positions(id, takes.Count, panWidth);
        for (int i = 0; i < takes.Count; i++) { takes[i].Channel.Pan = pans[i]; takes[i].ApplyChannel(); }
        MarkDirty(); UpdateUndoButtons(); RefreshInspectorText(); Timeline.InvalidateVisual();
    }

    /// <summary>The compressor from a strip: like the inspector, the preset is recomputed against the take
    /// (an empty track just keeps the values until a take fills it, as updateComp needs the audio).</summary>
    void StripComp(LiveTrack t, Action<CompSettings> edit, string key) => EditTrack(t, x =>
    {
        edit(x.Channel.Comp);
        if (x.Envelope != null && x.Channel.Comp.Preset != "custom") RecalculateInspectorComp(x);
    }, "comp." + key);

    void RefreshMixer()
    {
        if (!mixerView) return;
        miniTimeline.SetTracks(tracks);
        if (masteringView) return;
        double areaW = mixerScroll.Viewport.Width > 0 ? mixerScroll.Viewport.Width : MixerHost.Bounds.Width;
        // folder strips take their own width; folded groups hide their tracks
        int n = Math.Max(1, tracks.Count(t => FolderOfTrack(t)?.MixerCollapsed != true));
        double freeWide = (areaW - Buses.Count * (ReturnWideW + StripGap) - MasterW - StripGap
            - Folders.Count(f => !f.Separator) * (FolderStripW(true) + StripGap) - Folders.Count(f => f.Separator) * (SeparatorW + StripGap)) / n - StripGap;
        bool wide = mixerWidth == "wide" || (mixerWidth == "auto" && freeWide >= WideMin);
        double stripW = wide ? Math.Max(WideMin, Math.Min(WideMax, Math.Floor(freeWide))) : CompactW;
        // a new width or layout only resizes the cards or shows the other layout (see compactMixer)
        string key = string.Join(",", tracks.Select(t => t.Track.Id + ":" + t.Clips.Count + ":" + t.Track.FolderId + ":" + t.Track.MixerSide + ":" + string.Join("/", t.Channel.Slots.Select(s => s.Id))
                + ":" + string.Join("/", t.Channel.Sends.Where(s => s.Hidden).Select(s => s.BusId))))
            + "|" + string.Join(",", Folders.Select(f => f.Id + ":" + f.Index + ":" + f.MixerCollapsed + ":" + f.Separator + ":" + f.MixerSide + ":" + string.Join("/", f.Sends.Where(s => s.Hidden).Select(s => s.BusId))))
            // a bus's routing changes which buses the others can send to: their send panels come and go
            + "|" + string.Join(",", Buses.Select(b => b.Id + ":" + b.Name + ":" + b.MixerSide + ":" + b.Output + ":" + string.Join("/", b.Sends.Select(x => x.BusId)) + ":" + string.Join("/", b.Inserts.Select(s => s.Id + s.Type))));
        if (key != mixerKey)
        {
            mixerKey = key;
            BuildStrips(compactMixer, false, CompactW);
            BuildStrips(wideMixer, true, stripW);
        }
        mx = wide ? wideMixer : compactMixer;
        ApplyMixerHeights();
        wideMixer.Strips.IsVisible = wide;
        compactMixer.Strips.IsVisible = !wide;
        if (wide) foreach (var box in mx.Boxes) box.Width = stripW;
        bool was = syncing;
        syncing = true;
        try
        {
            bool editable = store != null && !store.ReadOnly && !restoring;
            mixerStrips.IsEnabled = editable;
            if (panButton != null) panButton.IsEnabled = editable && tracks.Any(t => t.Clips.Count > 0);
            foreach (var r in headerRefresh) r();
            foreach (var r in mx.Refresh) r();
            if (slotWindowRefresh is { } w) foreach (var r in w) r();
        }
        finally { syncing = was; }
    }

    /// <summary>Called from the window tick: meters, compressor reduction, LIM and the mini timeline's playhead.</summary>
    void TickMixer(double pos, float outL, float outR)
    {
        double lim = masterLimDb = engine.Limiter.TakeReductionDb();
        masterOutPeak = Math.Max(masterOutPeak, engine.TruePeak.TakeMax());
        TakeMixerRms();
        if (!mixerView) return;
        // the mini timeline sits over the Mastering view too
        miniTimeline.Bpm = Timeline.Bpm;
        miniTimeline.SetPlayhead(pos, tracks.Count == 0 ? 0 : tracks.Max(t => t.EndSec), RecordingBusy);
        if (masteringView) return;
        foreach (var t in tracks)
            if (mx.Meters.TryGetValue(t.Track.Id, out var m))
            {
                m.Meter.Mono = TrackMono(t);
                m.Meter.Set(t.ShowL, t.ShowR, Math.Sqrt(t.Chain.Rms.MeanSqL), Math.Sqrt(t.Chain.Rms.MeanSqR));
                m.Gr(t.GrShow);
            }
        masterShowL = Math.Max(outL, masterShowL * 0.82f); masterShowR = Math.Max(outR, masterShowR * 0.82f);
        mx.Master?.Set(masterShowL, masterShowR, Math.Sqrt(engine.OutRms.MeanSqL), Math.Sqrt(engine.OutRms.MeanSqR));
        TickNodes();
        ShowMasterLufs();
        if (mx.Lim != null) mx.Lim.Opacity = lim < -0.5 ? 1 : 0.25;
    }

    long rmsStamp;

    /// <summary>
    /// Whether a track's strip shows one bar: every take it plays is mono (<see cref="MediaAudio.Mono"/>); with none, its
    /// input is (a single one, or Configuración's input in a mono mode). Runs in the tick, so it allocates nothing.
    /// </summary>
    bool TrackMono(LiveTrack t)
    {
        int online = 0;
        var clips = t.Clips;
        for (int i = 0; i < clips.Count; i++)
        {
            if (clips[i].Audio is not { } a) continue;
            if (!a.Mono) return false;
            online++;
        }
        if (online > 0) return true;
        return t.Track.Input is { } input ? !input.Contains('-') : engine.InputChannelMode != GuitarCam.Audio.InputChannelMode.Stereo;
    }

    /// <summary>
    /// Integrates every meter's RMS (<see cref="RmsTap"/>) once per tick, shown or not, so a meter that comes back
    /// into view starts from the last 300 ms and not from everything that played while it was hidden.
    /// </summary>
    void TakeMixerRms()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = rmsStamp == 0 ? 0 : (now - rmsStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        rmsStamp = now;
        for (int i = 0; i < tracks.Count; i++) tracks[i].Chain.Rms.Take(dt);
        engine.OutRms.Take(dt);
        TakeNodesRms(dt);
    }

    /// <summary>The master strip's short-term LUFS: the transport's live measurement (<see cref="Audio.PlaybackEngine.Loudness"/>),
    /// not a second meter. The text changes only with the shown tenth.</summary>
    void ShowMasterLufs()
    {
        if (mx.Lufs == null) return;
        int s = Tenths(engine.Loudness.ShortTerm);
        if (s == mx.LufsTenths) return;
        mx.LufsTenths = s;
        mx.Lufs.Text = s == int.MinValue ? "—" : $"{s / 10.0:0.0}";
    }

    // --- strips ----------------------------------------------------------------------------------------

    void BuildStrips(MixerLayout layout, bool wide, double stripW)
    {
        mx = layout;
        mx.Refresh.Clear();
        mx.Transpose.Clear();
        mx.Meters.Clear();
        mx.Boxes.Clear();
        mx.TopRows.Clear();
        mx.FxRows.Clear();
        mx.SendRows.Clear();
        mx.NodeMeters.Clear();
        var order = mx.Order;
        order.Top.Clear();
        order.Inner.Clear();
        order.FolderStrips.Clear();
        var mixerStrips = mx.Strips;
        mixerStrips.Children.Clear();
        // the two sides (asked by the user): tracks and folders on the left by default, buses on the right, and
        // anything can be moved to the other one; the free space between them grows with the window
        var strips = order.Left = new StackPanel { Orientation = Orientation.Horizontal };
        var returns = order.Right = new StackPanel { Orientation = Orientation.Horizontal };
        mixerStrips.Children.Add(strips);
        Grid.SetColumn(returns, 2); mixerStrips.Children.Add(returns);
        if (tracks.Count == 0)
        {
            var empty = new TextBlock { Text = Loc.T("mixer.empty"), FontSize = 12, Foreground = Palette.Faint,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(empty, 1);
            mixerStrips.Children.Add(empty);
        }
        // with folders every column gets the band their names sit in, so all faders stay level
        bool bands = Folders.Any(f => !f.Separator);
        Control Column(Control c) => bands ? Banded(null, c) : c;
        void Put(Control column, object item, bool right)
        {
            (right ? returns : strips).Children.Add(column);
            order.Top.Add((column, item, right));
        }
        foreach (var item in store == null ? [] : TrackFolders.Sequence(tracks.Select(t => t.Track).ToList(), Folders))
        {
            if (item is Folder { Separator: true } sep)
            {
                var card = SeparatorStrip(sep);
                MixerDraggable(card, sep);
                Put(Column(card), sep, MixerSides.IsRight(sep));
            }
            else if (item is Folder f) Put(FolderGroup(f, wide, stripW), f, MixerSides.IsRight(f));
            else if (item is GuitarCam.Projects.Track { FolderId: null } tr && tracks.FirstOrDefault(t => t.Track == tr) is { } lt)
            {
                var card = Strip(lt, tracks.IndexOf(lt), wide, stripW);
                MixerDraggable(card, tr);
                Put(Column(card), tr, MixerSides.IsRight(tr));
            }
        }

        // the buses, on their side after the tracks (a new one comes from the mixer's right-click menu)
        foreach (var b in Buses)
        {
            var card = BusStrip(b, wide);
            MixerDraggable(card, b);
            Put(Column(card), b, MixerSides.IsRight(b));
        }
        var master = Column(Master(wide)); Grid.SetColumn(master, 3); mixerStrips.Children.Add(master);
    }

    /// <summary>A strip as a card on the mixer's background, so each channel reads apart from its neighbours.</summary>
    static Border StripCard(double width) => new()
    {
        Width = width, Margin = new Thickness(StripGap / 2, StripGap), Background = Palette.Panel, BorderBrush = Palette.Line,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true,
    };

    /// <summary>Puts a strip's parts on the rows every strip shares (<see cref="StripHeadH"/>…), so all faders and meters
    /// start and end at the same height whatever the name, the strip type or its controls: compact strips stack the
    /// controls over the fader, wide ones put them beside it. Without controls (the master) the meter is centred.</summary>
    Grid StripLayout(bool wide, Control head, Control? controls, Control faders, Control foot)
    {
        var g = new Grid { Margin = new Thickness(wide ? 8 : 6, 8, wide ? 8 : 6, 6) };
        g.RowDefinitions.Add(new RowDefinition(StripHeadH, GridUnitType.Pixel));
        if (!wide)
        {
            var top = new RowDefinition(StripTopH, GridUnitType.Pixel);
            mx.TopRows.Add(top);
            g.RowDefinitions.Add(top);
        }
        g.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        g.RowDefinitions.Add(new RowDefinition(StripFootH, GridUnitType.Pixel));
        void Put(Control c, int row, int col = 0, int span = 1, int rowSpan = 1)
        {
            Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span); Grid.SetRowSpan(c, rowSpan);
            g.Children.Add(c);
        }
        head.VerticalAlignment = VerticalAlignment.Top;
        foot.VerticalAlignment = VerticalAlignment.Bottom;
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        if (wide && controls != null)
        {
            g.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            Put(head, 0, 0, 2);
            controls.Margin = new Thickness(0, 0, 8, 0);
            Put(controls, 1, rowSpan: 2);
            Put(faders, 1, 1);
            Put(foot, 2, 1);
        }
        else
        {
            int row = wide ? 1 : 2;
            Put(head, 0);
            if (controls != null) { controls.VerticalAlignment = VerticalAlignment.Top; Put(controls, 1); }
            faders.HorizontalAlignment = HorizontalAlignment.Center;
            faders.Margin = new Thickness(0, wide ? 0 : 6, 0, 0);
            Put(faders, row);
            Put(foot, row + 1);
        }
        return g;
    }

    Control Strip(LiveTrack t, int index, bool wide, double width)
    {
        var box = StripCard(width);
        mx.Boxes.Add(box);
        box.Cursor = new Cursor(StandardCursorType.Hand);
        var top = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        bool hover = false;
        box.PointerEntered += (_, _) => { hover = true; Paint(); };
        box.PointerExited += (_, _) => { hover = false; Paint(); };
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            int i = tracks.IndexOf(t);
            if (i >= 0 && sel != t) Timeline.Select(i);
        };
        void Paint()
        {
            bool selected = sel == t, armed = Timeline.ArmedIndices.Contains(tracks.IndexOf(t));
            // the armed strip (where R records) is tinted red, like its lane in the timeline
            box.Background = armed && !selected ? Palette.Alpha(Palette.RecColor, 0.08)
                : selected ? Palette.Raised : hover ? Palette.Alpha(Palette.RaisedColor, 0.6) : Palette.Panel;
            box.BorderBrush = armed ? Palette.Alpha(Palette.RecColor, 0.5) : selected ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line;
            // a track with a color shows it along the card's top edge; selected, the accent takes its place
            top.Background = selected ? Palette.Accent : TrackIcons.ColorOf(t.Track) is { } c ? Palette.Alpha(c, 1) : Brushes.Transparent;
        }
        mx.Refresh.Add(Paint);

        bool empty = t.Clips.Count == 0;
        bool Silent() => empty || t.Silenced || (tracks.Any(x => x.Clips.Count > 0 && x.Soloed) && !t.Soloed);
        var name = new TextBlock { Text = t.Track.Name, FontSize = wide ? 12 : 11, FontWeight = wide ? FontWeight.Medium : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        mx.Refresh.Add(() => { name.Text = t.Track.Name; name.Foreground = Silent() ? Palette.Faint : Palette.Fg; ToolTip.SetTip(name, t.Track.Name); });
        var emptyHint = new TextBlock { Text = Loc.T("mixer.emptyTag"), FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, IsVisible = empty, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(emptyHint, Loc.T("mixer.emptyTag.tip"));
        // armed, the strip shows where it records from in the place of "VACÍA" (native only)
        var input = InputChip(t);
        mx.Refresh.Add(() =>
        {
            bool armed = Timeline.ArmedIndices.Contains(tracks.IndexOf(t)) && !RecordingTake;
            input.IsVisible = armed;
            emptyHint.IsVisible = empty && !armed;
        });
        double badgeSize = wide ? 28 : 22;
        var badge = new TrackBadge { Width = badgeSize, Height = badgeSize, VerticalAlignment = VerticalAlignment.Center };
        badge.Clicked += () => ShowTrackLook(t, badge);
        mx.Refresh.Add(() => badge.Set(t.Track, tracks.IndexOf(t) + 1, sel == t));
        double btn = wide ? 28 : 24;
        var mute = StripToggle("M", Shortcuts.Tip("Mute", "mute"), btn, () => t.Channel.Muted, false, () => EditTrack(t, x => x.Channel.Muted = !x.Channel.Muted, "mute"));
        var solo = StripToggle("S", Shortcuts.Tip("Solo", "solo"), btn, () => t.Channel.Solo, true, () => EditTrack(t, x => x.Channel.Solo = !x.Channel.Solo, "solo"));
        var arm = ArmToggle(t, wide ? btn : 18);
        var fader = new Fader();
        fader.Changed += v => EditTrack(t, x => x.Channel.Volume = v, "volume");
        var db = new TextBlock { Classes = { "mono" }, FontSize = wide ? 11 : 10, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        mx.Refresh.Add(() => { fader.Value = t.Channel.Volume; db.Text = $"{Fader.Db(t.Channel.Volume)} dB"; });
        var meter = new MixerMeter();
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        faders.Children.Add(fader); faders.Children.Add(meter);

        Control head, controls, foot;
        if (wide)
        {
            var h = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), Height = 28 };
            h.Children.Add(badge);
            name.Margin = new Thickness(8, 0); Grid.SetColumn(name, 1); h.Children.Add(name);
            emptyHint.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(emptyHint, 2); h.Children.Add(emptyHint);
            input.Margin = new Thickness(0, 0, 6, 0); Grid.SetColumn(input, 2); h.Children.Add(input);
            arm.Margin = new Thickness(0, 0, 4, 0); Grid.SetColumn(arm, 3); h.Children.Add(arm);
            Grid.SetColumn(mute, 4); h.Children.Add(mute);
            solo.Margin = new Thickness(4, 0, 0, 0); Grid.SetColumn(solo, 5); h.Children.Add(solo);
            head = h;
            // pan stays put with the head and the fader; only the effects scroll under the line (asked by the user)
            var c = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
            c.Children.Add(PanRow(t));
            var line = new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetRow(line, 1); c.Children.Add(line);
            // the scroll bar takes its own room beside the panels (App.axaml); the margin is the gap to it
            var fx = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 4, 6) };
            WideFx(fx, t);
            var scroll = new ScrollViewer { Content = fx, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 2); c.Children.Add(scroll);
            controls = c;
            foot = db;
            mx.Meters[t.Track.Id] = (meter, wideGr ?? (_ => { }));
            wideGr = null;
        }
        else
        {
            // the name on one line and "VACÍA" under it, in a head of fixed height: a long name never pushes the fader down
            var h = new StackPanel { Spacing = 2 };
            name.HorizontalAlignment = HorizontalAlignment.Center; name.TextAlignment = TextAlignment.Center; name.Margin = new Thickness(0, 2, 0, 0);
            h.Children.Add(name);
            // "VACÍA", its input while armed or its transpose under the name (● goes with M and S, over the fader)
            var under = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            under.Children.Add(emptyHint); under.Children.Add(input); under.Children.Add(TransposeTagText(t));
            h.Children.Add(under);
            head = h;
            // like a console's strip (asked by the user): its effects and its sends as lists, the pan as a bar, then M S ●
            var (list, gr) = CompactChain(t);
            controls = list;
            var f = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            f.Children.Add(badge); f.Children.Add(db);
            foot = f;
            mx.Meters[t.Track.Id] = (meter, gr);
        }
        var content = new Grid();
        content.Children.Add(StripLayout(wide, head, controls, faders, foot));
        content.Children.Add(top);
        box.Child = content;
        return box;
    }

    /// <summary>Set by <see cref="WideFx"/> for the strip being built: writes the "Comprime" meter.</summary>
    Action<double>? wideGr;

    /// <summary>The wide strip's pan: label, knob and readout, fixed above the scrolling effects.</summary>
    Control PanRow(LiveTrack t)
    {
        var pan = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0) };
        pan.Children.Add(new TextBlock { Text = Loc.T("mix.pan.upper"), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        var panKnob = new Knob(24);
        panKnob.Changed += v => EditTrack(t, x => x.Channel.Pan = Math.Abs(v) < 0.04 ? 0 : v, "pan");
        var panText = new TextBlock { Classes = { "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        mx.Refresh.Add(() => { panKnob.Value = t.Channel.Pan; panKnob.Tip = Loc.T("tl.tip.pan", FmtPan(t.Channel.Pan)); panText.Text = FmtPan(t.Channel.Pan); });
        pan.Children.Add(panKnob); pan.Children.Add(panText);
        return pan;
    }

    /// <summary>Height (px) of a row of the compact strip's lists.</summary>
    const double CompactRowH = 18;

    /// <summary>
    /// The compact strip's controls (asked by the user, like a console's strip): the effects as a list (a click opens
    /// one in its window, <see cref="ShowSlotWindow"/>; dragged, it moves in the chain; its power switch turns it on or off), the
    /// sends as bars with the bus's name (shown with the mixer's "Envíos"), the pan as a bar from the centre, and M S ●.
    /// The action writes the first compressor's reduction under its row.
    /// </summary>
    (Control Controls, Action<double> Gr) CompactChain(LiveTrack t)
    {
        var o = SlotsOf(t);
        var g = new Grid();
        void Section(List<(RowDefinition, Control, bool)> rows, Control head, Control list) => ListSection(g, rows, head, list);

        var fxList = new StackPanel { Spacing = 1 };
        var drag = new SlotDrag(1);
        drag.Moved += (id, anchor, after) => SlotsEdit(o, x => ChannelInserts.MoveNextTo(x, id, anchor, after), "move");
        Border? grBar = null;
        var slots = o.Slots();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Known) continue;
            int nth = 1;
            for (int k = 0; k < i; k++) if (slots[k].Type == slot.Type) nth++;
            string id = slot.Id, name = SlotTitle(SlotName(slot.Type), nth);
            var row = ListRow(name, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, x => x.On = !x.On, "on"));
            if (slot.Type == InsertSlot.CompKind && grBar == null)
            {
                grBar = new Border { Height = 2, Width = 0, Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
                ((Grid)row.Child!).Children.Add(grBar);
                Grid.SetColumnSpan(grBar, 2);
            }
            ToolTip.SetTip(row, Loc.T("mix.list.fx.tip", name));
            row.Tag = id;
            fxList.Children.Add(drag.Add(row, id, row));
        }
        drag.Clicked += id => { if (fxList.Children.OfType<Border>().FirstOrDefault(c => c.Tag as string == id) is { } r) ShowSlotWindow(o, id, r); };
        foreach (var c in fxList.Children.OfType<Border>()) c.Cursor = new Cursor(StandardCursorType.Hand);
        Section(mx.FxRows, ListHead(Loc.T("mix.list.fx"), Loc.T("mix.slot.add.tip"), b => ShowAddSlotMenu(o, b)), ListBox(fxList, false));

        var router = RouterOf(t);
        Section(mx.SendRows, ListHead(Loc.T("mix.list.sends"), Loc.T("send.add.tip"), b => OpenAnchoredMenu(Popup(SendChoices(router), 220), b)), ListBox(CompactSends(router), true));

        var pan = new HBar { Bipolar = true, ResetTo = 0, Margin = new Thickness(0, 4, 0, 6) };
        pan.Changed += v => EditTrack(t, x => x.Channel.Pan = v, "pan");
        mx.Refresh.Add(() =>
        {
            pan.Value = t.Channel.Pan;
            pan.Readout = FmtPan(t.Channel.Pan);
            ToolTip.SetTip(pan, Loc.T("tl.tip.pan", FmtPan(t.Channel.Pan)));
        });
        Grid.SetRow(pan, g.RowDefinitions.Count); g.RowDefinitions.Add(new RowDefinition(PanRowH, GridUnitType.Pixel)); g.Children.Add(pan);

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        toggles.Children.Add(StripToggle("M", Shortcuts.Tip("Mute", "mute"), 26, () => t.Channel.Muted, false, () => EditTrack(t, x => x.Channel.Muted = !x.Channel.Muted, "mute")));
        toggles.Children.Add(StripToggle("S", Shortcuts.Tip("Solo", "solo"), 26, () => t.Channel.Solo, true, () => EditTrack(t, x => x.Channel.Solo = !x.Channel.Solo, "solo")));
        toggles.Children.Add(ArmToggle(t, 26));
        Grid.SetRow(toggles, g.RowDefinitions.Count); g.RowDefinitions.Add(new RowDefinition(TogglesRowH, GridUnitType.Pixel)); g.Children.Add(toggles);
        var meterBar = grBar;
        return (g, gr => { if (meterBar != null && meterBar.Parent is Control p) meterBar.Width = p.Bounds.Width * Math.Clamp(-gr / 24, 0, 1); });
    }

    /// <summary>
    /// A compact list's section (its title and its list) as two more rows of <paramref name="g"/>. Every strip's
    /// sections are as tall as the others' (asked by the user: all level), whatever each one holds.
    /// </summary>
    static void ListSection(Grid g, List<(RowDefinition, Control, bool)> rows, Control head, Control list)
    {
        var headRow = new RowDefinition(ListHeadH, GridUnitType.Pixel);
        var listRow = new RowDefinition(0, GridUnitType.Pixel);
        Grid.SetRow(head, g.RowDefinitions.Count); g.RowDefinitions.Add(headRow); g.Children.Add(head);
        Grid.SetRow(list, g.RowDefinitions.Count); g.RowDefinitions.Add(listRow); g.Children.Add(list);
        rows.Add((headRow, head, true));
        rows.Add((listRow, list, false));
    }

    /// <summary>The compact strip's sends: a bar per bus in the list of <paramref name="router"/> (a track or a folder), its power switch turning it on or off.</summary>
    StackPanel CompactSends(Router router)
    {
        var sends = new StackPanel { Spacing = 1 };
        foreach (var bus in SendTargets(router))
        {
            string id = bus.Id;
            (double Level, bool On, bool Pre) S() => router.Get() is { } r ? ProjectBuses.SendOf(r, id) : (0, true, false);
            var bar = new HBar { ResetTo = 0 };
            bar.Changed += v => router.Edit((p, x) => ProjectBuses.SetSend(p, x, id, v, v > 0 ? true : null) != null, $"send.{id}.level");
            var power = ListPower(() => S().On && S().Level > 0, () => router.Edit((p, x) => ProjectBuses.SetSend(p, x, id, on: !S().On) != null, $"send.{id}.on"));
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = CompactRowH };
            line.Children.Add(power);
            Grid.SetColumn(bar, 1); line.Children.Add(bar);
            mx.Refresh.Add(() =>
            {
                var (level, on, _) = S();
                bar.Value = level;
                bar.Lit = on;
                bar.Label = BusById(id)?.Name ?? bus.Name;
                bar.Readout = $"{level * 100:0}";
                ToolTip.SetTip(bar, Loc.T("mix.list.send.tip", bar.Label, $"{level * 100:0}"));
            });
            sends.Children.Add(line);
        }
        return sends;
    }

    /// <summary>
    /// The compact folder strip's controls, level with the track strips beside it: an empty section where their effects
    /// go (a folder has none), its sends like theirs (asked by the user: not only from the inspector), and its output
    /// where their pan and M S ● go, so it shows whichever sections are on.
    /// </summary>
    Control CompactFolderControls(Router router)
    {
        var g = new Grid();
        ListSection(g, mx.FxRows, new Border(), new Border());
        ListSection(g, mx.SendRows, ListHead(Loc.T("mix.list.sends"), Loc.T("send.add.tip"), b => OpenAnchoredMenu(Popup(SendChoices(router), 220), b)), ListBox(CompactSends(router), true));
        var output = OutputSelect(router, mx.Refresh);
        output.VerticalAlignment = VerticalAlignment.Top;
        output.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetRow(output, g.RowDefinitions.Count); g.RowDefinitions.Add(new RowDefinition(PanRowH + TogglesRowH, GridUnitType.Pixel)); g.Children.Add(output);
        return g;
    }

    /// <summary>
    /// A compact list in its section: it scrolls when it holds more than fits, and a grip along its bottom stretches the
    /// section on every strip at once (asked by the user), remembered in config.json; a double click puts it back.
    /// </summary>
    Control ListBox(Control list, bool sends)
    {
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 6) };
        var line = new Border { Height = 1, Background = Palette.Line, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var grip = new Border { Height = 6, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Bottom, Child = line,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
        ToolTip.SetTip(grip, Loc.T("mix.list.resize.tip"));
        grip.PointerEntered += (_, _) => line.Background = Palette.Accent;
        grip.PointerExited += (_, _) => line.Background = Palette.Line;
        double fromY = 0, fromH = 0;
        bool dragging = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                SetListH(sends, sends ? SendsListH : FxListH, true);
                return;
            }
            dragging = true;
            fromY = e.GetPosition(this).Y;
            fromH = sends ? mixerSendsH : mixerFxH;
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (dragging) SetListH(sends, fromH + e.GetPosition(this).Y - fromY, false);
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            SetListH(sends, sends ? mixerSendsH : mixerFxH, true);
        };
        grip.PointerCaptureLost += (_, _) => dragging = false;
        return new Grid { Children = { scroll, grip } };
    }

    /// <summary>A list section's new height, on every strip (and saved when <paramref name="save"/>).</summary>
    void SetListH(bool sends, double h, bool save)
    {
        h = Math.Round(Math.Clamp(h, CompactRowH + 6, ListMaxH));
        if (sends) mixerSendsH = h; else mixerFxH = h;
        ApplyMixerHeights();
        if (save) SaveViewPref(sends ? "mixer.sendsH" : "mixer.fxH", h.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Sets every strip's controls row and the compact lists' sections to the shown sections and their heights, in both
    /// layouts: only row heights change, nothing is rebuilt (it runs on every move of a grip).
    /// </summary>
    void ApplyMixerHeights()
    {
        foreach (var layout in new[] { compactMixer, wideMixer })
        {
            foreach (var r in layout.TopRows) r.Height = new GridLength(StripTopH, GridUnitType.Pixel);
            foreach (var (row, what, head) in layout.FxRows)
            {
                row.Height = new GridLength(mixerFx ? head ? ListHeadH : mixerFxH : 0, GridUnitType.Pixel);
                what.IsVisible = mixerFx;
            }
            foreach (var (row, what, head) in layout.SendRows)
            {
                row.Height = new GridLength(SendsRow ? head ? ListHeadH : mixerSendsH : 0, GridUnitType.Pixel);
                what.IsVisible = SendsRow;
            }
        }
    }

    /// <summary>What a slot is called in the compact list: the musician's word (Vida, Comp…).</summary>
    static string SlotName(string type) => type switch
    {
        InsertSlot.SatKind => Loc.T("fx.life"),
        InsertSlot.CompKind => Loc.T("fx.comp"),
        InsertSlot.ReverbKind => "Reverb",
        InsertSlot.DelayKind => "Delay",
        _ => "EQ",
    };

    /// <summary>A compact list's title ("EFECTOS", "ENVÍOS") with its + on the right.</summary>
    Grid ListHead(string title, string addTip, Action<Button> add)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 20 };
        g.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 9, LetterSpacing = 0.8, FontWeight = FontWeight.SemiBold, Foreground = Palette.Faint,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
        var plus = SlotHeadButton("\ue710", addTip);
        plus.Width = plus.Height = plus.MinHeight = 18;
        plus.Click += (_, _) => { if (!PressClosedMenuOf(plus)) add(plus); };
        Grid.SetColumn(plus, 1); g.Children.Add(plus);
        return g;
    }

    /// <summary>
    /// The power switch of a compact list's row (asked by the user: the ⏻ of a real switch, the same glyph as the wide
    /// strip's ON/OFF): accent when on, grey when off.
    /// </summary>
    Button ListPower(Func<bool> on, Action toggle)
    {
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var b = new Button { Classes = { "ghost", "slothead" }, Width = 16, Height = 16, MinHeight = 16, Padding = new Thickness(0), Focusable = false, Margin = new Thickness(0, 0, 3, 0),
            Cursor = new Cursor(StandardCursorType.Hand), Content = glyph, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => toggle();
        mx.Refresh.Add(() =>
        {
            bool lit = on();
            glyph.Foreground = lit ? Palette.Accent : Palette.Faint;
            ToolTip.SetTip(b, Loc.T(lit ? "mix.list.off" : "mix.list.on"));
        });
        return b;
    }

    /// <summary>A row of the compact effects list: its power switch and its name, on a block that lights under the pointer.</summary>
    Border ListRow(string name, Func<bool> on, Action toggle)
    {
        var text = new TextBlock { Text = name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(ListPower(on, toggle));
        Grid.SetColumn(text, 1); g.Children.Add(text);
        var row = new Border { Height = CompactRowH, CornerRadius = new CornerRadius(3), Padding = new Thickness(1, 0, 4, 0), Child = g, Background = Palette.Raised, ClipToBounds = true };
        row.PointerEntered += (_, _) => row.Background = Palette.Hover;
        row.PointerExited += (_, _) => row.Background = Palette.Raised;
        mx.Refresh.Add(() => text.Foreground = on() ? Palette.Fg : Palette.Faint);
        return row;
    }

    void WideFx(StackPanel fx, LiveTrack t)
    {
        // the mixer header's "Efectos" shows or hides them
        var slots = new StackPanel { Spacing = fx.Spacing };
        SlotPanels(slots, SlotsOf(t));
        mx.Refresh.Add(() => slots.IsVisible = mixerFx);
        fx.Children.Add(slots);

        // where it goes (a track in a folder goes through the folder) and its sends, one per bus
        var router = RouterOf(t);
        if (t.Track.FolderId == null) fx.Children.Add(OutputRow(router));
        SendPanels(fx, router);
        TransposePanel(fx, t);
    }

    /// <summary>
    /// Whose insert slots the panels edit: a track's channel or a bus. The panels only know this, so a bus strip shows
    /// its slots exactly like a track strip.
    /// </summary>
    sealed class SlotOwner
    {
        /// <summary>The slots as they are now (the channel or the bus object changes with every undo).</summary>
        public required Func<List<InsertSlot>> Slots { get; init; }
        /// <summary>An edit of the slots: one undo step per key, heard at once, saved.</summary>
        public required Action<Action<List<InsertSlot>>, string> Edit { get; init; }
        /// <summary>Measures a compressor's preset again (after its preset or amount changed, or a new one).</summary>
        public required Action<CompSettings> Recalculate { get; init; }
        /// <summary>The "+ Efecto" tooltip: what a new effect works on.</summary>
        public string AddTip { get; init; } = Loc.T("mix.slot.add.tip");
        /// <summary>A bus's chain: "+ Efecto" also offers reverb and delay (a track's doesn't).</summary>
        public bool ReturnKinds { get; init; }
        public InsertSlot? Slot(string id) => Slots().Find(s => s.Id == id);
    }

    /// <summary>A track's slots: its channel's, the compressor measured against its take.</summary>
    SlotOwner SlotsOf(LiveTrack t) => new()
    {
        Slots = () => t.Channel.Slots,
        Edit = (change, key) => EditTrack(t, x => change(x.Channel.Slots), key),
        Recalculate = c => { if (t.Envelope != null) RecalculateInspectorComp(t, c); },
    };

    /// <summary>The insert slots' panels, in the order the sound goes through them, and "+ Efecto" under them.</summary>
    void SlotPanels(StackPanel fx, SlotOwner o)
    {
        // a repeated kind is numbered (EQ 2) and the reduction meter goes on the first compressor, which shows the
        // whole chain's
        var slots = o.Slots();
        bool grShown = false;
        var drag = new SlotDrag(fx.Spacing);
        drag.Moved += (id, anchor, after) => SlotsEdit(o, x => ChannelInserts.MoveNextTo(x, id, anchor, after), "move");
        drag.Clicked += id => ToggleSlotFolded(o, id);
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Known) continue;
            int nth = 1;
            for (int k = 0; k < i; k++) if (slots[k].Type == slot.Type) nth++;
            bool gr = slot.Type == InsertSlot.CompKind && !grShown;
            grShown |= gr;
            SlotPanel(fx, o, slot, nth, gr, drag);
        }
        fx.Children.Add(AddSlotButton(o));
    }

    /// <summary>One slot's panel by its kind; <paramref name="gr"/> = it carries the chain's reduction meter. Without
    /// <paramref name="drag"/> it is a panel on its own (<see cref="ShowSlotWindow"/>): never folded.</summary>
    void SlotPanel(StackPanel fx, SlotOwner o, InsertSlot slot, int nth, bool gr, SlotDrag? drag)
    {
        var menu = SlotMenuButton(o, slot.Id);
        switch (slot.Type)
        {
            case InsertSlot.SatKind: SatPanel(fx, o, slot.Id, nth, menu, drag); break;
            case InsertSlot.CompKind: CompPanel(fx, o, slot.Id, nth, menu, gr, drag); break;
            case InsertSlot.ReverbKind: ReverbSlotPanel(fx, o, slot.Id, nth, menu, drag); break;
            case InsertSlot.DelayKind: DelaySlotPanel(fx, o, slot.Id, nth, menu, drag); break;
            default: EqPanel(fx, o, slot.Id, nth, menu, drag); break;
        }
    }

    // what a panel reads while its slot is already gone (removed, an undo) and the strips aren't rebuilt yet
    static readonly SatSettings GoneSat = new();
    static readonly CompSettings GoneComp = new();
    static readonly EqSettings GoneEq = new();

    static string SlotTitle(string name, int nth) => nth > 1 ? $"{name} {nth}" : name;

    /// <summary>An edit of one slot's settings, found by id (the channel object changes with every undo).</summary>
    void SlotEdit(LiveTrack t, string id, Action<InsertSlot> edit, string key) => SlotEdit(SlotsOf(t), id, edit, key);

    void SlotEdit(SlotOwner o, string id, Action<InsertSlot> edit, string key) =>
        o.Edit(slots => { if (slots.Find(s => s.Id == id) is { } s) edit(s); }, $"{id}.{key}");

    /// <summary>Adds, removes or moves slots: each its own undo step; the strips rebuild with the new list.</summary>
    Task SlotsEdit(LiveTrack t, Action<LiveTrack> change, string key)
    {
        EditTrack(t, change, $"fx.{key}.{++slotEdits}");
        return Task.CompletedTask;
    }

    Task SlotsEdit(SlotOwner o, Action<List<InsertSlot>> change, string key)
    {
        o.Edit(change, $"fx.{key}.{++slotEdits}");
        return Task.CompletedTask;
    }

    /// <summary>An edit of one compressor slot of the wide strip, found by id; its preset is measured again unless it is manual.</summary>
    void StripComp(SlotOwner o, string id, Action<CompSettings> edit, string key) => o.Edit(slots =>
    {
        if (slots.Find(s => s.Id == id)?.Comp is not { } c) return;
        edit(c);
        if (c.Preset != "custom") o.Recalculate(c);
    }, $"{id}.comp.{key}");

    int slotEdits;

    void SatPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu, SlotDrag? drag)
    {
        SatSettings S() => o.Slot(id)?.Sat ?? GoneSat;
        var sat = FxPanel(fx, SlotTitle("Sat", nth), FxIcon.Sat, () => S().On, () => SlotEdit(o, id, s => s.On = !s.On, "sat.on"),
            on => Loc.T(on ? "insp.power.sat.off" : "insp.power.sat.on"), menu, drag, id, o,
            () => $"{Loc.T(S().Mode == "tape" ? "insp.sat.tape" : "insp.sat.tube")} · {S().Amount * 100:0}%");
        var satKnob = FxKnob(sat, "vida", 0.3, () => S().Amount, v => SlotEdit(o, id, s => { s.Sat!.Amount = v; s.Sat.On = true; }, "sat.amount"),
            () => Loc.T("mix.sat.knob.tip"));
        var satSide = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var modes = new[] { ("tube", Loc.T("insp.sat.tube"), Loc.T("insp.sat.tube.hint")),
            ("tape", Loc.T("insp.sat.tape"), Loc.T("insp.sat.tape.hint")) };
        var mode = new SelectButton();
        mode.SetItems(modes.Select(m => m.Item2).ToArray(), modes.Select(m => (string?)m.Item3).ToArray());
        mode.Selected += i => { string m = modes[i].Item1; SlotEdit(o, id, s => { s.Sat!.Mode = m; s.Sat.On = true; }, "sat.mode"); };
        var satLabel = new TextBlock { FontSize = 12, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        mx.Refresh.Add(() =>
        {
            int i = Array.FindIndex(modes, m => m.Item1 == S().Mode);
            mode.SelectedIndex = Math.Max(0, i);
            ToolTip.SetTip(mode, modes[Math.Max(0, i)].Item3);
            double a = S().Amount;
            satLabel.Text = $"{Loc.T(a < .2 ? "insp.life.0" : a < .45 ? "insp.life.1" : a < .7 ? "insp.life.2" : a < .9 ? "insp.life.3" : "insp.life.4")} · {a * 100:0}%";
        });
        satSide.Children.Add(mode); satSide.Children.Add(satLabel);
        sat.Body.Children.Add(Row(satKnob, satSide));
    }

    void CompPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu, bool meter, SlotDrag? drag)
    {
        CompSettings C() => o.Slot(id)?.Comp ?? GoneComp;
        var lim = new Button { Content = "LIM", Classes = { "chip", "mono" }, FontSize = 10, FontWeight = FontWeight.Bold, Height = 24, MinHeight = 24, Padding = new Thickness(5, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        lim.Click += (_, _) => StripComp(o, id, c => c.Limit = !c.Limit, "limit");
        mx.Refresh.Add(() =>
        {
            var c = C();
            lim.Classes.Set("lit", c.Limit && c.On);
            lim.Background = c.Limit && c.On ? Palette.AccentSoft : Brushes.Transparent;
            lim.Foreground = c.Limit && c.On ? Palette.Accent : Palette.Faint;
            lim.BorderBrush = c.Limit && c.On ? Palette.Alpha(Palette.AccentColor, 0.6) : c.Limit ? Palette.Alpha(Palette.FaintColor, 0.6) : Palette.Line;
            ToolTip.SetTip(lim, Loc.T(c.Limit ? "mix.lim.on" : "mix.lim.off") + (c.On ? "" : Loc.T("mix.lim.note")));
        });
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { lim, menu } };
        var comp = FxPanel(fx, SlotTitle("Comp", nth), FxIcon.Comp, () => C().On, () => StripComp(o, id, c => c.On = !c.On, "on"),
            on => Loc.T(on ? "insp.power.comp.off" : "insp.power.comp.on"), head, drag, id, o, () => PresetLabel(C().Preset));
        var compKnob = FxKnob(comp, "cantidad", 0.5, () => C().Amount, v => StripComp(o, id, c => { c.Amount = v; c.On = true; }, "amount"),
            () => C().Preset == "custom" ? Loc.T("mix.comp.knob.manual") : Loc.T("mix.comp.knob.tip"));
        mx.Refresh.Add(() => { bool custom = C().Preset == "custom"; compKnob.Opacity = custom ? 0.4 : 1; compKnob.IsHitTestVisible = !custom; });
        var compSide = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var preset = StripSelect([]);
        preset.Selected += i =>
        {
            if (i >= CompPresets.All.Length) return;
            string p = CompPresets.All[i].Id;
            StripComp(o, id, c => { c.Preset = p; c.On = true; }, "preset");
        };
        var compLabel = new TextBlock { FontSize = 12, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        mx.Refresh.Add(() =>
        {
            var c = C();
            bool custom = c.Preset == "custom";
            preset.SetItems(CompPresets.All.Select(CompPresetName).Concat(custom ? new[] { Loc.T("insp.comp.manual") } : []).ToArray(),
                CompPresets.All.Select(p => (string?)CompPresetHint(p)).Concat(custom ? new string?[] { null } : []).ToArray());
            int i = Array.FindIndex(CompPresets.All, p => p.Id == c.Preset);
            preset.SelectedIndex = custom ? CompPresets.All.Length : i;
            ToolTip.SetTip(preset, i >= 0 ? CompPresetHint(CompPresets.All[i]) : Loc.T("mix.comp.custom.tip"));
            compLabel.Text = custom ? Loc.T("mix.comp.custom") : AmountLabel(c.Amount);
            ToolTip.SetTip(compLabel, (custom ? "" : Loc.T("mix.comp.reduces", $"{EstimatedCompression(c):0}") + " · ") + Loc.T("mix.comp.detail", $"{c.Threshold:0}", $"{c.Ratio:0.0}", $"{c.Makeup:+0.0;-0.0;0.0}"));
        });
        compSide.Children.Add(preset); compSide.Children.Add(compLabel);
        comp.Body.Children.Add(Row(compKnob, compSide));
        if (!meter) return;
        // gain reduction: 0..12 dB with ticks every 3 dB; label and readout on one line, the bar under them, so it fits any width
        var grRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        ToolTip.SetTip(grRow, Loc.T("mix.gr.tip"));
        grRow.Children.Add(new TextBlock { Text = Loc.T("mix.gr"), FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        var grFill = new Border { Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var grTrack = new Grid { Height = 10, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        grTrack.Children.Add(new Border { Background = Palette.Line, CornerRadius = new CornerRadius(2) });
        grTrack.Children.Add(grFill);
        foreach (double x in new[] { 0.25, 0.5, 0.75 })
        {
            var tick = new Border { Width = 1, Background = Palette.Alpha(Palette.AppColor, 0.7), HorizontalAlignment = HorizontalAlignment.Left };
            grTrack.SizeChanged += (_, e) => tick.Margin = new Thickness(Math.Round(e.NewSize.Width * x), 0, 0, 0);
            grTrack.Children.Add(tick);
        }
        Grid.SetRow(grTrack, 1); Grid.SetColumnSpan(grTrack, 2); grRow.Children.Add(grTrack);
        var grText = new TextBlock { Classes = { "mono" }, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetColumn(grText, 1); grRow.Children.Add(grText);
        comp.Body.Children.Add(grRow);
        wideGr = gr =>
        {
            grFill.Width = grTrack.Bounds.Width * Math.Clamp(-gr / 12, 0, 1);
            grText.Text = o.Slots().Any(s => s.Type == InsertSlot.CompKind && s.On) ? $"{(gr > -0.05 ? 0 : gr):0.0} dB" : "—";
        };
    }

    void EqPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu, SlotDrag? drag)
    {
        EqSettings E() => o.Slot(id)?.Eq ?? GoneEq;
        var eq = FxPanel(fx, SlotTitle("EQ", nth), FxIcon.Eq, () => E().On, () => SlotEdit(o, id, s => s.On = !s.On, "eq.on"),
            on => Loc.T(on ? "insp.power.eq.off" : "insp.power.eq.on"), menu, drag, id, o,
            () => EqEditor.PresetIndex(E()) is var p and >= 0 ? EqPresetName(TrackEq.Presets[p]) : Loc.T("mix.custom.m"));
        // a summary; a click opens its bands (the first EQ is also the one in the Canal tab)
        var graph = new EqGraph(interactive: false, height: 54) { Cursor = new Cursor(StandardCursorType.Hand) };
        ToolTip.SetTip(graph, Loc.T("mix.eq.open.tip"));
        graph.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(graph).Properties.IsLeftButtonPressed || !mixerStrips.IsEnabled) return;
            e.Handled = true;
            ShowSlotEq(o, id, graph);
        };
        var eqPreset = StripSelect([]);
        eqPreset.Margin = new Thickness(0, 6, 0, 0);
        eqPreset.Selected += index =>
        {
            int i = index - (EqEditor.PresetIndex(E()) < 0 ? 1 : 0);
            if (i >= 0) SlotEdit(o, id, s => s.Eq = EqEditor.FromPreset(i, s.Eq!), "eq");
        };
        mx.Refresh.Add(() =>
        {
            var e = E();
            graph.Set(e, 0);
            int p = EqEditor.PresetIndex(e);
            eqPreset.SetItems((p < 0 ? new[] { Loc.T("mix.custom.m") } : []).Concat(TrackEq.Presets.Select(EqPresetName)).ToArray(),
                (p < 0 ? new string?[] { null } : []).Concat(TrackEq.Presets.Select(x => (string?)EqPresetHint(x))).ToArray());
            eqPreset.SelectedIndex = p < 0 ? 0 : p;
            ToolTip.SetTip(eqPreset, p < 0 ? Loc.T("mix.eq.manual") : EqPresetHint(TrackEq.Presets[p]));
        });
        eq.Body.Children.Add(graph); eq.Body.Children.Add(eqPreset);
    }

    /// <summary>The bands of one EQ slot, in a flyout over the strip: the same editor as the Canal tab.</summary>
    void ShowSlotEq(SlotOwner o, string id, Control anchor)
    {
        if (o.Slot(id)?.Eq is not { } start) return;
        var editor = new EqEditor { Width = 300 };
        editor.Set(start);
        editor.Changed += value =>
        {
            SlotEdit(o, id, s => s.Eq = value, "eq");
            if (o.Slot(id)?.Eq is { } now) editor.Set(now);
        };
        var flyout = new Flyout { Content = editor, Placement = PlacementMode.LeftEdgeAlignedTop, ShowMode = FlyoutShowMode.Standard };
        flyout.ShowAt(anchor);
    }

    /// <summary>The ⋯ in a slot's title bar: move it earlier or later in the chain, repeat it, remove it.</summary>
    Button SlotMenuButton(LiveTrack t, string id) => SlotMenuButton(SlotsOf(t), id);

    Button SlotMenuButton(SlotOwner o, string id)
    {
        var b = new Button { Classes = { "ghost" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Focusable = false,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        ToolTip.SetTip(b, Loc.T("mix.slot.menu.tip"));
        b.Click += (_, _) =>
        {
            if (PressClosedMenuOf(b)) return;
            var slots = o.Slots();
            int i = slots.FindIndex(s => s.Id == id);
            if (i < 0) return;
            OpenAnchoredMenu(Popup(
            [
                MenuAction(Loc.T("mix.slot.up"), () => SlotsEdit(o, x => ChannelInserts.Move(x, id, -1), "move"), enabled: i > 0, icon: "",
                    tip: Loc.T("mix.slot.up.tip")),
                MenuAction(Loc.T("mix.slot.down"), () => SlotsEdit(o, x => ChannelInserts.Move(x, id, 1), "move"), enabled: i < slots.Count - 1, icon: "",
                    tip: Loc.T("mix.slot.down.tip")),
                MenuAction(Loc.T("mix.slot.duplicate"), () => SlotsEdit(o, x => ChannelInserts.Duplicate(x, id), "duplicate"), icon: "",
                    tip: Loc.T("mix.slot.duplicate.tip")),
                new Separator(),
                MenuAction(Loc.T("mix.slot.remove"), () => SlotsEdit(o, x => ChannelInserts.Remove(x, id), "remove"), danger: true, icon: "",
                    tip: Loc.T("mix.slot.remove.tip")),
            ], 220), b);
        };
        return b;
    }

    /// <summary>"+ Efecto" under the slots: adds one at the end of the chain, already on.</summary>
    Button AddSlotButton(LiveTrack t) => AddSlotButton(SlotsOf(t));

    Button AddSlotButton(SlotOwner o)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.T("mix.slot.add"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } };
        var b = new Button { Classes = { "chip" }, Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 30, Focusable = false };
        ToolTip.SetTip(b, o.AddTip);
        b.Click += (_, _) => { if (!PressClosedMenuOf(b)) ShowAddSlotMenu(o, b); };
        return b;
    }

    /// <summary>The kinds of effect a chain can take: one more at its end, already on.</summary>
    void ShowAddSlotMenu(SlotOwner o, Control b)
    {
        MenuItem Kind(string kind, string name, string hint)
        {
            var icon = FxIcon.Create(kind, 12);
            icon.VerticalAlignment = VerticalAlignment.Center;
            FxIcon.SetColor(icon, Palette.Accent);
            var item = new MenuItem { Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { icon, new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
            ToolTip.SetTip(item, hint);
            ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
            item.Click += (_, _) => SlotsEdit(o, x =>
            {
                var slot = ChannelInserts.Add(x, kind);
                // a compressor's preset is measured against the take, like a new track's
                if (slot?.Comp is { } c) o.Recalculate(c);
            }, "add");
            return item;
        }
        var kinds = new List<object>();
        if (o.ReturnKinds)
        {
            kinds.Add(Kind(InsertSlot.ReverbKind, "Reverb", Loc.T("mix.slot.add.reverb")));
            kinds.Add(Kind(InsertSlot.DelayKind, "Delay", Loc.T("mix.slot.add.delay")));
        }
        kinds.Add(Kind(InsertSlot.SatKind, Loc.T("fx.life"), Loc.T("mix.slot.add.sat")));
        kinds.Add(Kind(InsertSlot.CompKind, Loc.T("insp.section.comp"), Loc.T("mix.slot.add.comp")));
        kinds.Add(Kind(InsertSlot.EqKind, "EQ", Loc.T("mix.slot.add.eq")));
        OpenAnchoredMenu(Popup(kinds, 200), b);
    }

    /// <summary>
    /// Port of FxPanel: power, icon and name over the effect's controls, which grey out while it is off. Narrower than
    /// <see cref="FxNarrowW"/> the power button keeps only its glyph and the icon goes, so the name fits. A slot in a
    /// chain (with its <paramref name="owner"/>) folds to its title row (asked by the user): a click on the bar or its
    /// arrow folds or unfolds it, and folded it shows a <paramref name="summary"/> and a button that opens the whole
    /// panel in a window of its own (<see cref="ShowSlotWindow"/>).
    /// </summary>
    (Border Box, StackPanel Body) FxPanel(StackPanel host, string title, string icon, Func<bool> on, Action? toggle,Func<bool, string> tip, Control? extra = null,
        SlotDrag? drag = null, string? slotId = null, SlotOwner? owner = null, Func<string>? summary = null)
    {
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var state = new TextBlock { FontSize = 10, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        // a hand over it, not the bar's drag cursor, and off it lights up in the accent under the pointer (asked by the
        // user, like the row's other buttons; class slotpower in App.axaml)
        var power = new Button { Classes = { "chip", "slotpower" }, Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand), Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { glyph, state } } };
        // no toggle: an effect that is on by its amount alone (Transponer); the panel still lights up with it
        power.Click += (_, _) => toggle?.Invoke();
        bool hot = false;
        void PaintPower() => glyph.Foreground = state.Foreground = on() ? Palette.App : hot ? Palette.Accent : Palette.Faint;
        power.PointerEntered += (_, _) => { hot = true; PaintPower(); };
        power.PointerExited += (_, _) => { hot = false; PaintPower(); };
        power.IsVisible = toggle != null;
        var iconText = FxIcon.Create(icon, 12);
        iconText.VerticalAlignment = VerticalAlignment.Center; iconText.Margin = new Thickness(toggle == null ? 0 : 8, 0, 0, 0);
        var name = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 4, 0) };
        var brief = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        // the name takes the free width (and trims); folded, the summary does and the name keeps its own size
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto"), Height = 32, Margin = new Thickness(6, 0) };
        head.Children.Add(power);
        Grid.SetColumn(iconText, 1); head.Children.Add(iconText);
        Grid.SetColumn(name, 2); head.Children.Add(name);
        Grid.SetColumn(brief, 3); head.Children.Add(brief);
        if (extra != null) { extra.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(extra, 4); head.Children.Add(extra); }
        var body = new StackPanel { Margin = new Thickness(8) };
        var stack = new StackPanel();
        var bar = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(5, 5, 0, 0) };
        stack.Children.Add(bar);
        stack.Children.Add(body);
        var box = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = stack, ClipToBounds = true };
        bool foldable = drag != null && slotId != null && owner != null;
        Button? window = null, fold = null;
        if (foldable)
        {
            window = SlotHeadButton("", Loc.T("mix.slot.window.tip"));
            window.Click += (_, _) => ShowSlotWindow(owner!, slotId!, box);
            Grid.SetColumn(window, 5); head.Children.Add(window);
            fold = SlotHeadButton("", "");
            fold.Click += (_, _) => ToggleSlotFolded(owner!, slotId!);
            Grid.SetColumn(fold, 6); head.Children.Add(fold);
        }
        bool narrow = false, folded = false;
        void Shape()
        {
            state.IsVisible = !narrow && !folded;
            iconText.IsVisible = !narrow;
            head.Margin = new Thickness(narrow ? 4 : 6, 0);
            name.Margin = new Thickness(narrow ? 4 : 6, 0, narrow ? 2 : 4, 0);
            head.ColumnDefinitions[2].Width = folded ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            head.ColumnDefinitions[3].Width = folded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            name.MaxWidth = folded ? FoldedNameW : double.PositiveInfinity;
        }
        box.SizeChanged += (_, e) =>
        {
            bool n = e.NewSize.Width < FxNarrowW;
            if (n == narrow) return;
            narrow = n;
            Shape();
        };
        mx.Refresh.Add(() =>
        {
            bool lit = on();
            PaintPower();
            state.Text = lit ? "ON" : "OFF";
            power.Classes.Set("lit", lit);
            power.Background = lit ? Palette.Accent : Brushes.Transparent;
            power.BorderBrush = lit ? Palette.Accent : Palette.Line;
            ToolTip.SetTip(power, tip(lit));
            FxIcon.SetColor(iconText, lit ? Palette.Accent : Palette.Faint);
            name.Foreground = lit ? Palette.Fg : Palette.Faint;
            body.Opacity = lit ? 1 : 0.4;
            // a panel a step above the strip's card (asked by the user: each effect reads as its own block), its title
            // bar one more step up; off, both sink back toward the card
            box.Background = Palette.Alpha(Palette.HoverColor, lit ? 0.45 : 0.18);
            box.BorderBrush = lit ? Palette.Line : Palette.Alpha(Palette.LineColor, 0.6);
            bar.Background = Palette.Alpha(Palette.HoverColor, lit ? 0.6 : 0.2);
            bar.BorderBrush = box.BorderBrush;
            if (!foldable) return;
            // folded: just the title row, as small as it goes, with what it is set to beside the name
            folded = owner!.Slot(slotId!)?.Collapsed == true;
            body.IsVisible = !folded;
            brief.IsVisible = folded;
            brief.Text = folded ? summary?.Invoke() ?? "" : "";
            brief.Foreground = lit ? Palette.Dim : Palette.Faint;
            ToolTip.SetTip(brief, folded ? brief.Text : null);
            if (extra != null) extra.IsVisible = !folded;
            window!.IsVisible = folded;
            ((TextBlock)fold!.Content!).Text = folded ? "" : "";
            ToolTip.SetTip(fold, Loc.T(folded ? "mix.slot.unfold.tip" : "mix.slot.fold.tip"));
            head.Height = folded ? 26 : 32;
            bar.BorderThickness = new Thickness(0, 0, 0, folded ? 0 : 1);
            bar.CornerRadius = folded ? new CornerRadius(5) : new CornerRadius(5, 5, 0, 0);
            Shape();
        });
        // a slot's panel is dragged by its title bar to change the order; in a chain a click on it folds or unfolds it
        if (drag != null && slotId != null)
        {
            ToolTip.SetTip(bar, Loc.T(foldable ? "mix.slot.bar.tip" : "mix.slot.drag.tip"));
            host.Children.Add(drag.Add(box, slotId, bar));
        }
        else host.Children.Add(box);
        return (box, body);
    }

    /// <summary>The widest a folded slot's name gets (px): the rest of the row is its summary.</summary>
    const double FoldedNameW = 90;

    /// <summary>A small icon button of a slot's title row (open in a window, fold): a hand over it, not the bar's drag
    /// cursor, and it lights up in the accent (asked by the user; class <c>slothead</c> in App.axaml).</summary>
    static Button SlotHeadButton(string glyph, string tip)
    {
        var b = new Button { Classes = { "ghost", "slothead" }, Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0), Focusable = false, Margin = new Thickness(2, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (tip.Length > 0) ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>Folds or unfolds a slot in the mixer: a view choice, saved with the project but not an undo step.</summary>
    void ToggleSlotFolded(SlotOwner o, string id)
    {
        if (store is not { ReadOnly: false } || restoring || o.Slot(id) is not { } s) return;
        s.Collapsed = !s.Collapsed;
        MarkDirty();
        RefreshMixer();
    }

    /// <summary>The slot shown in a window of its own, and its panel's refreshes (run with the mixer's while it is open).</summary>
    Flyout? slotWindow;
    List<Action>? slotWindowRefresh;

    /// <summary>
    /// One slot's whole panel in a small window over the mixer (asked by the user: a folded slot opens on its own without
    /// unfolding the chain). It is built like the strip's, into a layout of its own, and edits the same slot.
    /// </summary>
    void ShowSlotWindow(SlotOwner o, string id, Control anchor)
    {
        var slots = o.Slots();
        int i = slots.FindIndex(s => s.Id == id);
        if (i < 0 || !slots[i].Known) return;
        int nth = 1;
        for (int k = 0; k < i; k++) if (slots[k].Type == slots[i].Type) nth++;
        var saved = mx;
        var own = new MixerLayout();
        var host = new StackPanel { Width = 300 };
        mx = own;
        try { SlotPanel(host, o, slots[i], nth, false, null); }
        finally { mx = saved; wideGr = null; }
        bool was = syncing;
        syncing = true;
        try { foreach (var r in own.Refresh) r(); }
        finally { syncing = was; }
        var card = new Border { Padding = new Thickness(10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = host };
        slotWindow?.Hide();
        var flyout = slotWindow = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.LeftEdgeAlignedTop, ShowMode = FlyoutShowMode.Standard };
        slotWindowRefresh = own.Refresh;
        flyout.Closed += (_, _) =>
        {
            if (slotWindow != flyout) return;
            slotWindow = null;
            slotWindowRefresh = null;
        };
        flyout.ShowAt(anchor);
    }

    Control FxKnob((Border Box, StackPanel Body) panel, string label, double reset, Func<double> get, Action<double> set, Func<string> tip)
    {
        var knob = new Knob(34) { Min = 0, Max = 1, ResetTo = reset };
        knob.Changed += set;
        mx.Refresh.Add(() => { knob.Value = get(); knob.Tip = tip(); });
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(knob);
        col.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        return col;
    }

    /// <summary>A knob beside its other controls; in a panel narrower than <see cref="FxNarrowW"/> the knob goes on top,
    /// centred, so the controls keep the whole width instead of being cut.</summary>
    static Grid Row(Control left, Control right)
    {
        var g = new Grid();
        g.Children.Add(left); g.Children.Add(right);
        bool? stacked = null;
        void Arrange(bool s)
        {
            if (s == stacked) return;
            stacked = s;
            g.ColumnDefinitions = new ColumnDefinitions(s ? "*" : "Auto,*");
            g.RowDefinitions = new RowDefinitions(s ? "Auto,Auto" : "Auto");
            Grid.SetColumn(right, s ? 0 : 1); Grid.SetRow(right, s ? 1 : 0);
            left.HorizontalAlignment = s ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            right.Margin = s ? new Thickness(0, 6, 0, 0) : new Thickness(8, 0, 0, 0);
        }
        Arrange(false);
        // the panel's body has 8 px of margin on each side and a 1 px border
        g.SizeChanged += (_, e) => Arrange(e.NewSize.Width + 18 < FxNarrowW);
        return g;
    }

    static SelectButton StripSelect(string[] items)
    {
        var s = new SelectButton();
        s.SetItems(items);
        return s;
    }

    /// <summary>Port of ToggleBtn: M lights in the accent, S in white. Like every strip button it takes no keyboard
    /// focus, so Space and Enter stay shortcuts after a click in the mixer.</summary>
    Button StripToggle(string text, string tip, double size, Func<bool> on, bool solo, Action click)
    {
        var b = new Button { Classes = { "chip" }, Focusable = false, Width = size, Height = size, MinHeight = size, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = text, Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        mx.Refresh.Add(() =>
        {
            bool lit = on();
            b.Classes.Set("lit", lit);
            b.Background = lit ? (solo ? Palette.Fg : Palette.Accent) : Brushes.Transparent;
            b.BorderBrush = lit ? (solo ? Palette.Fg : Palette.Accent) : Palette.Line;
            ((TextBlock)b.Content!).Foreground = lit ? Palette.App : Palette.Faint;
        });
        return b;
    }

    /// <summary>The strip's record-arm toggle (native only), the same ● as the timeline header: lit red on the track R records to.</summary>
    Button ArmToggle(LiveTrack t, double size)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 9, Height = 9 };
        var b = new Button { Classes = { "chip" }, Focusable = false, Width = size, Height = size, MinHeight = size, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = dot };
        b.Click += (_, _) => ToggleArm(t);
        mx.Refresh.Add(() =>
        {
            bool lit = Timeline.ArmedIndices.Contains(tracks.IndexOf(t));
            b.Classes.Set("lit", lit);
            b.Background = lit ? Palette.Rec : Brushes.Transparent;
            b.BorderBrush = lit ? Palette.Rec : Palette.Line;
            dot.Fill = lit ? Palette.Fg : Palette.Alpha(Palette.RecColor, 0.7);
            ToolTip.SetTip(b, lit ? Loc.T("tl.tip.armed", Shortcuts.Paren("record"))
                : t.Clips.Count == 0 ? Loc.T("tl.tip.arm.empty") + Shortcuts.Note("arm", Loc.T("tl.note.arm"))
                : Loc.T("tl.tip.arm.replace") + Shortcuts.Note("arm", Loc.T("tl.note.arm")));
        });
        return b;
    }

    /// <summary>The armed strip's input ("1-2 ▾"): opens the same menu as the chip in the timeline header.</summary>
    Border InputChip(LiveTrack t)
    {
        var text = new TextBlock { Classes = { "mono" }, FontSize = 9, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        row.Children.Add(text);
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 7, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border { Child = row, Height = 14, Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
            Background = Palette.Hover, BorderBrush = Palette.Alpha(Palette.RecColor, 0.45), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false };
        ToolTip.SetTip(chip, Loc.T("tl.tip.input"));
        chip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            ShowInputMenu(t, chip);
        };
        mx.Refresh.Add(() => text.Text = InputShort(t.Track));
        return chip;
    }

    /// <summary>MiniKnob: a 22 px knob, its value and a label under it.</summary>
    Control MiniKnob(string label, double min, double max, double reset, Func<double> get, Func<string> readout, Action<double> set, Func<string> tip)
    {
        var knob = new Knob(22) { Min = min, Max = max, ResetTo = reset, HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += set;
        var value = new TextBlock { Classes = { "mono" }, FontSize = 9, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center };
        mx.Refresh.Add(() => { knob.Value = get(); knob.Tip = tip(); value.Text = readout(); });
        var col = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        col.Children.Add(knob); col.Children.Add(value);
        col.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        return col;
    }

    /// <summary>BigKnob: label on top, a 34 px knob, the value under it.</summary>
    Control BigKnob(string label, double reset, Func<double> get, Func<string> readout, Action<double> set, string tip)
    {
        var knob = new Knob(34, tip) { Min = 0, Max = 1, ResetTo = reset, HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += set;
        var value = new TextBlock { Classes = { "mono" }, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        mx.Refresh.Add(() => { knob.Value = get(); value.Text = readout(); });
        var col = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        col.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        col.Children.Add(knob); col.Children.Add(value);
        return col;
    }

    Control Master(bool wide)
    {
        var box = StripCard(MasterW);
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(MasterButton());
        mx.Lim = new TextBlock { Text = "LIM", Classes = { "mono" }, FontSize = 9, FontWeight = FontWeight.Bold, LetterSpacing = 0.8, Foreground = Palette.Accent, Opacity = 0.25,
            HorizontalAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(mx.Lim, Loc.T("mix.master.lim.tip"));
        head.Children.Add(mx.Lim);
        mx.Master = new MixerMeter(wide: true);
        // the foot shows the output's short-term LUFS (the transport's measurement), where "Salida" was
        mx.Lufs = new TextBlock { Text = "—", Classes = { "mono" }, FontSize = 10, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        mx.LufsTenths = int.MaxValue;
        var output = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Background = Brushes.Transparent,
            Children = { mx.Lufs, new TextBlock { Text = "LUFS", FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center } } };
        ToolTip.SetTip(output, Loc.T("mix.master.lufs.tip"));
        box.Child = StripLayout(wide, head, null, mx.Master, output);
        return box;
    }
}
