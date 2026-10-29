using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WildTrack.App.Controls;
using WildTrack.App.Model;
using WildTrack.Audio.Dsp;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// Port of Mixer.tsx and MiniTimeline.tsx: the bottom view that alternates with the timeline (X). One strip per track,
/// empty ones too ("vacía": what you set there goes to the next take), its effects as a list over the fader at any
/// width (the mixer's zoom); the buses (MainWindow.Buses.cs); the master with its LIM light and its card (MainWindow.Master.cs); pan templates for the whole
/// mix. Every change is an undo step (same keys as the inspector), saved and heard live. Same as the web except: the
/// toggle sits in the bottom transport bar (where the native transport is), the EQ summary has no live spectrum yet
/// (like the inspector), and empty strips edit their channel without the take analysis (as in the web). Also unlike
/// the web (asked by the user): every strip is a card, all strips share the same rows so every meter has the same
/// height, and the strips give way part by part as they narrow (<see cref="NarrowParts"/>) instead of cutting their
/// controls. An effect opens in a small window of its own (<see cref="ShowSlotWindow"/>).
/// Track folders (native only) show as a tinted group with their name above it: a folder strip first (no fader: its
/// output, its sends and the meter of its tracks' sum), then its tracks, unless the group is folded.
/// </summary>
public partial class MainWindow
{
    // Strip widths (px). Tracks and buses take one width, the mixer's zoom (mixerStripW, asked by the user: one flow
    // instead of Compacto / Amplio, and one strip, the list, at every width): MinStripW to WideMax (asked by the user:
    // wider than that adds nothing). Folder strips follow it too.
    const double CompactW = 104, WideMax = 150, MasterW = 76;
    /// <summary>The narrowest strip (px): a slim fader and the meter's bars, the effects as icons, M and S, the badge.</summary>
    const double MinStripW = 42;
    /// <summary>Under this width (px) a compact strip drops its meter's scale and its dB, and tightens its margins.</summary>
    const double NarrowW = 90;
    /// <summary>Under this width (px) a compact strip takes the slim fader and its thinnest margins.</summary>
    const double TinyW = 64;
    /// <summary>The strips' width before the user zooms the mixer (px).</summary>
    const double DefaultStripW = 120;
    /// <summary>How much one wheel notch widens or narrows the strips (the timeline's zoom is 1.18 per notch).</summary>
    const double MixerZoomStep = 1.12;
    /// <summary>Rows every strip shares (takes, returns and master), so all faders and meters start and end at the same
    /// height: head, controls, fader, foot.</summary>
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
    /// <summary>The extra room (px) a folder keeps from a channel or separator beside it.</summary>
    const double FolderGap = 6;
    /// <summary>Below this width an effect panel drops its ON/OFF text and icon, and puts its knob over its other controls.</summary>
    const double FxNarrowW = 150;
    /// <summary>The mixer keeps its own height: its strips want more room than the timeline.</summary>
    const double MixerDefaultH = 440, MixerMinH = 250;

    /// <summary>The strips with what the window updates on them (and an effect's window, <see cref="ShowSlotWindow"/>, its own).</summary>
    sealed class MixerLayout
    {
        public readonly Grid Strips = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        /// <summary>The order and sides of its strips, for dragging them (MainWindow.MixerOrder.cs).</summary>
        public readonly MixerOrder Order = new();
        public readonly List<Action> Refresh = [];
        /// <summary>The strips' Transponer tags, refreshed while a transpose is being made (its progress).</summary>
        public readonly List<Action> Transpose = [];
        public readonly Dictionary<string, (MixerMeter Meter, Action<double> Gr)> Meters = [];
        /// <summary>The track and bus strips' cards, resized in place when only the strip width changes.</summary>
        public readonly List<Border> Boxes = [];
        /// <summary>Every strip's controls row and the compact lists' rows with what they hold, resized in place (<see cref="ApplyMixerHeights"/>).</summary>
        public readonly List<RowDefinition> TopRows = [];
        public readonly List<(RowDefinition Row, Control What, bool Head)> FxRows = [], SendRows = [];
        /// <summary>The folders' and buses' meters, by node id (<see cref="Audio.PlaybackEngine.Node"/>), and what writes a bus's "Comprime".</summary>
        public readonly List<(string Id, MixerMeter Meter, Action<double>? Gr)> NodeMeters = [];
        /// <summary>What a strip changes with its width (the zoom) without being rebuilt.</summary>
        public readonly List<Action<double>> Resize = [];
        public MixerMeter? Master;
        public TextBlock? Lim;
        /// <summary>The master's correlation readout and the hundredth it shows (so the text changes only with it).</summary>
        public TextBlock? Cor;
        public int CorHundredths = int.MaxValue;
        /// <summary>The master's short-term LUFS readout and the tenth it shows (int.MaxValue = not yet written).</summary>
        public TextBlock? Lufs;
        public int LufsTenths = int.MaxValue;
    }

    // Built whenever the tracks change, and only resized by the zoom: building a mixer's worth of controls makes the GC
    // stop every thread, the audio callback's too, for 10-40 ms.
    readonly MixerLayout mixerLayout = new();
    /// <summary>The layout being filled: the mixer's, or an effect's window while it is built.</summary>
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
    /// <summary>Every track and bus strip's width (px, <see cref="MinStripW"/>..<see cref="WideMax"/>), kept in config.json ("mixerStripW").</summary>
    double mixerStripW = DefaultStripW;
    /// <summary>The header's zoom bar (like the timeline's) and its icon; the wheel gesture is the timeline's zoom.</summary>
    readonly Slider mixerZoomSlider = new() { Minimum = 0, Maximum = 1, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock mixerZoomIcon = new() { Classes = { "icon" }, Text = "", FontSize = 15, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
    bool mixerZoomSyncing;
    /// <summary>Saves the width once the wheel or the bar stops, not on every notch.</summary>
    readonly DispatcherTimer mixerZoomSaver = new() { Interval = TimeSpan.FromMilliseconds(500) };
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
        // before the zoom there were three widths: "Compacto" keeps its narrow strips
        mixerStripW = double.TryParse(AppConfig.Get("mixerStripW"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sw)
            ? Math.Round(Math.Clamp(sw, MinStripW, WideMax)) : AppConfig.Get("mixerWidth") == "compact" ? CompactW : DefaultStripW;
        mixerZoomSaver.Tick += (_, _) =>
        {
            mixerZoomSaver.Stop();
            SaveViewPref("mixerStripW", mixerStripW.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        mixerZoomSlider.ValueChanged += (_, e) => { if (!mixerZoomSyncing) ZoomMixer(MinStripW * Math.Pow(WideMax / MinStripW, e.NewValue)); };
        Avalonia.Automation.AutomationProperties.SetName(mixerZoomSlider, Loc.T("mixer.zoom"));
        mixerZoomIcon.Margin = new Thickness(0, 0, 8, 0);
        mixerScroll.AddHandler(PointerWheelChangedEvent, OnMixerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
        mx = mixerLayout;
        mixerStrips.Children.Add(mixerLayout.Strips);
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
        MixerDock.Children.Add(miniRow);
        MixerDock.Children.Add(header);
        // the DockPanel fills only its last child: the mixer and the Mastering view share it, so both fill the place
        MixerDock.Children.Add(new Panel { Children = { mixerScroll, masteringScroll, pianoHost } });
        InitMastering();
        InitPiano();
        InitInstruments();
        InitEffects();

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

        // the strips' width, like the timeline's zoom bar (the header's own refreshers survive strip rebuilds)
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
            Children = { mixerZoomIcon, mixerZoomSlider } };
        headerRefresh.Add(() =>
        {
            mixerZoomSyncing = true;
            mixerZoomSlider.Value = Math.Log(mixerStripW / MinStripW) / Math.Log(WideMax / MinStripW);
            mixerZoomSyncing = false;
        });

        // Cubase's "deactivate all mute / solo states" (native only), lit while any track has one
        var clear = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        clear.Children.Add(FxBypassButton());
        foreach (bool solo in new[] { false, true })
        {
            var b = new Button { Classes = { "chip" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = solo ? "S" : "M", Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold } };
            b.Click += (_, _) => ClearAllToggles(solo);
            headerRefresh.Add(() =>
            {
                bool lit = tracks.Any(t => solo ? t.Channel.Solo : t.Channel.Muted);
                b.IsEnabled = store != null && !store.ReadOnly && !restoring && tracks.Any(t => t.Track.HasContent);
                b.Classes.Set("lit", lit);
                b.Background = lit ? (solo ? Palette.Fg : Palette.Accent) : Brushes.Transparent;
                b.BorderBrush = lit ? (solo ? Palette.Fg : Palette.Accent) : Palette.Line;
                ((TextBlock)b.Content!).Foreground = lit ? Palette.App : Palette.Faint;
                ToolTip.SetTip(b, lit ? Loc.T(solo ? "mixer.clearSolos" : "mixer.clearMutes") : Loc.T(solo ? "timeline.noSolos" : "timeline.noMutes"));
            });
            clear.Children.Add(b);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(12, 0) };
        grid.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(clear, 1); grid.Children.Add(clear);
        // the mix's A | B (MainWindow.MixCompare.cs)
        var compare = BuildMixCompare();
        Grid.SetColumn(compare, 2); grid.Children.Add(compare);
        Grid.SetColumn(zoom, 3); grid.Children.Add(zoom);
        return new Border { Height = 40, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    readonly List<Action> headerRefresh = [];

    static readonly IBrush BypassAmber = Palette.Brush("#f59e0b");

    /// <summary>
    /// The mixer's FX switch (asked by the user): every effect of every track and bus in bypass, or back, to hear the mix
    /// without them. Only what you hear (<see cref="Audio.PlaybackEngine.FxBypass"/>): lit amber like a mute, and the
    /// strips' effect lists dim while it is on.
    /// </summary>
    Button FxBypassButton()
    {
        var text = new TextBlock { Text = "FX", Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold };
        var b = new Button { Classes = { "chip" }, Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(4), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = text };
        b.Click += (_, _) => SetFxBypass(!Audio.PlaybackEngine.FxBypass);
        headerRefresh.Add(() =>
        {
            bool off = Audio.PlaybackEngine.FxBypass;
            b.Classes.Set("lit", off);
            b.Background = off ? BypassAmber : Brushes.Transparent;
            b.BorderBrush = off ? BypassAmber : Palette.Line;
            text.Foreground = off ? Palette.App : Palette.Faint;
            ToolTip.SetTip(b, Loc.T(off ? "mixer.fxBypass.on.tip" : "mixer.fxBypass.off.tip"));
        });
        return b;
    }

    void SetFxBypass(bool on)
    {
        Audio.PlaybackEngine.FxBypass = on;
        foreach (var t in tracks) t.ApplyInserts();
        engine.ApplyBusSlots(Buses);
        RefreshMixer();
    }

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
        var takes = tracks.Where(t => t.Track.HasContent).ToList();
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
        // tracks and buses take the zoom's width (folder strips, separators and the master keep their own)
        double stripW = mixerStripW;
        // a new width or layout only resizes the cards or shows the other layout (see compactMixer)
        string key = string.Join(",", tracks.Select(t => t.Track.Id + ":" + t.Clips.Count + ":" + t.Track.FolderId + ":" + t.Track.MixerSide + ":" + string.Join("/", t.Channel.Slots.Select(s => s.Id))
                + ":" + string.Join("/", t.Channel.Sends.Where(s => !s.Hidden).Select(s => s.BusId))))
            + "|" + string.Join(",", Folders.Select(f => f.Id + ":" + f.Index + ":" + f.MixerCollapsed + ":" + f.Separator + ":" + f.MixerSide + ":" + string.Join("/", f.Sends.Where(s => !s.Hidden).Select(s => s.BusId))))
            // a bus's routing changes which buses the others can send to: their send panels come and go
            + "|" + string.Join(",", Buses.Select(b => b.Id + ":" + b.Name + ":" + b.MixerSide + ":" + b.Output + ":" + string.Join("/", b.Sends.Select(x => x.BusId)) + ":" + string.Join("/", b.Inserts.Select(s => s.Id + s.Type))))
;
        if (key != mixerKey)
        {
            mixerKey = key;
            BuildStrips(stripW);
        }
        mx = mixerLayout;
        ApplyMixerHeights();
        foreach (var box in mx.Boxes) box.Width = stripW;
        foreach (var resize in mx.Resize) resize(stripW);
        bool was = syncing;
        syncing = true;
        try
        {
            bool editable = store != null && !store.ReadOnly && !restoring;
            mixerStrips.IsEnabled = editable;
            if (panButton != null) panButton.IsEnabled = editable && tracks.Any(t => t.Track.HasContent);
            foreach (var r in headerRefresh) r();
            foreach (var r in mx.Refresh) r();
            if (slotWindowRefresh is { } w) foreach (var r in w) r();
        }
        finally { syncing = was; }
    }

    /// <summary>
    /// The mixer's zoom (asked by the user: one flow, no Compacto / Amplio): every track and bus strip takes width
    /// <paramref name="w"/>, kept between <see cref="MinStripW"/> and <see cref="WideMax"/>, without rebuilding them.
    /// What sits under <paramref name="atX"/> (px from the view's left; its middle by default) stays there, as the
    /// timeline's zoom keeps the playhead.
    /// </summary>
    void ZoomMixer(double w, double? atX = null)
    {
        w = Math.Round(Math.Clamp(w, MinStripW, WideMax));
        if (w == mixerStripW || !mixerView || masteringView) return;
        double x = atX ?? mixerScroll.Viewport.Width / 2;
        // the strip under that point and where in it; elsewhere (a folder strip, a gap) the same share of the whole width
        int under = -1;
        double frac = 0, share = mixerStrips.Bounds.Width > 0 ? (mixerScroll.Offset.X + x) / mixerStrips.Bounds.Width : 0;
        for (int i = 0; i < mx.Boxes.Count; i++)
        {
            var box = mx.Boxes[i];
            if (!box.IsEffectivelyVisible || box.Bounds.Width <= 0 || box.TranslatePoint(default, mixerScroll) is not { } p) continue;
            if (x < p.X || x >= p.X + box.Bounds.Width) continue;
            under = i;
            frac = (x - p.X) / box.Bounds.Width;
            break;
        }
        mixerStripW = w;
        mixerZoomSaver.Stop();
        mixerZoomSaver.Start();
        RefreshMixer();
        mixerScroll.UpdateLayout();
        double content = under >= 0 && under < mx.Boxes.Count && mx.Boxes[under].TranslatePoint(default, mixerStrips) is { } at
            ? at.X + frac * mx.Boxes[under].Bounds.Width
            : share * mixerStrips.Bounds.Width;
        mixerScroll.Offset = new Vector(Math.Clamp(content - x, 0, Math.Max(0, mixerScroll.Extent.Width - mixerScroll.Viewport.Width)), mixerScroll.Offset.Y);
    }

    /// <summary>
    /// The mixer's wheel, with the timeline's modifiers (<see cref="Shortcuts.Wheel"/>): its zoom widens or narrows the
    /// strips around the pointer; its sideways gesture, and the plain wheel where nothing under the pointer scrolls,
    /// move the mixer sideways. Over a knob, fader or bar the wheel is theirs (Shift = fine), as over the timeline's knobs.
    /// </summary>
    void OnMixerWheel(object? sender, PointerWheelEventArgs e)
    {
        string? wheel = Shortcuts.FindWheel(e.KeyModifiers);
        bool listScrolls = false;
        for (var v = e.Source as Visual; v != null && v != mixerScroll; v = v.GetVisualParent())
        {
            if (v is Knob or InspectorKnob or Fader or HBar or EqEditor) return;
            if (v is ScrollViewer s && s.Extent.Height > s.Viewport.Height + 0.5) listScrolls = true;
        }
        // some mice and drivers turn a modified wheel into a sideways delta, so take whichever axis moved
        double d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (wheel == "wheelZoom") ZoomMixer(mixerStripW * Math.Pow(MixerZoomStep, d * Shortcuts.WheelSign(wheel)), e.GetPosition(mixerScroll).X);
        // wheel up goes right, as in the timeline (reversed: down)
        else if (wheel == "wheelSideways") ScrollMixerBy((e.Delta.Y + e.Delta.X) * Shortcuts.WheelSign(wheel) * 80);
        // a list or an effects column that has more to show takes the plain wheel; a touchpad's sideways swipe never
        else if (wheel == null && (!listScrolls || e.Delta.Y == 0)) ScrollMixerBy(-(e.Delta.Y * Shortcuts.WheelSign(Shortcuts.WheelScroll) + e.Delta.X) * 80);
        else return;
        e.Handled = true;
    }

    void ScrollMixerBy(double dx) =>
        mixerScroll.Offset = new Vector(Math.Clamp(mixerScroll.Offset.X + dx, 0, Math.Max(0, mixerScroll.Extent.Width - mixerScroll.Viewport.Width)), mixerScroll.Offset.Y);

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
        ShowMasterCorrelation();
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
        return t.Track.Input is { } input ? !input.Contains('-') : engine.InputChannelMode != WildTrack.Audio.InputChannelMode.Stereo;
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
        engine.Correlation.Take(dt);
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

    /// <summary>
    /// The master's COR: the correlation in tenths, red under zero (what cancels in mono), amber under +0.3 (wide), dim
    /// otherwise. Changes only with the shown hundredth, so the tick allocates nothing while it holds.
    /// </summary>
    static readonly IBrush CorWide = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Palette.MeterYellow);

    void ShowMasterCorrelation()
    {
        if (mx.Cor == null) return;
        double c = engine.Correlation.Value;
        int h = double.IsNaN(c) ? int.MinValue : (int)Math.Round(c * 10) * 10;
        if (h == mx.CorHundredths) return;
        mx.CorHundredths = h;
        mx.Cor.Text = h == int.MinValue ? "—" : h == 0 ? "0.0" : $"{h / 100.0:+0.0;-0.0}";
        mx.Cor.Foreground = h == int.MinValue ? Palette.Faint : h < 0 ? Palette.Rec : h < 30 ? CorWide : Palette.Dim;
    }

    // --- strips ----------------------------------------------------------------------------------------

    /// <summary>Empties a layout: its controls and everything the window updates on them.</summary>
    static void ClearStrips(MixerLayout l)
    {
        l.Refresh.Clear();
        l.Transpose.Clear();
        l.Meters.Clear();
        l.Boxes.Clear();
        l.TopRows.Clear();
        l.FxRows.Clear();
        l.SendRows.Clear();
        l.NodeMeters.Clear();
        l.Resize.Clear();
        l.Order.Top.Clear();
        l.Order.Inner.Clear();
        l.Order.FolderStrips.Clear();
        l.Strips.Children.Clear();
    }

    void BuildStrips(double stripW)
    {
        mx = mixerLayout;
        ClearStrips(mixerLayout);
        var order = mx.Order;
        var mixerStrips = mx.Strips;
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
            else if (item is Folder f) Put(FolderGroup(f, stripW), f, MixerSides.IsRight(f));
            else if (item is WildTrack.Projects.Track { FolderId: null } tr && tracks.FirstOrDefault(t => t.Track == tr) is { } lt)
            {
                var card = Strip(lt, stripW);
                MixerDraggable(card, tr);
                Put(Column(card), tr, MixerSides.IsRight(tr));
            }
        }

        // the buses, on their side after the tracks (a new one comes from the mixer's right-click menu)
        foreach (var b in Buses)
        {
            var card = BusStrip(b, stripW);
            MixerDraggable(card, b);
            Put(Column(card), b, MixerSides.IsRight(b));
        }
        // a folder keeps a little room from the column beside it (asked by the user), not from the mixer's start nor
        // the free space: on the left side, from its neighbours; on the right one, also from the master after it
        foreach (var (side, right) in new[] { (strips, false), (returns, true) })
            for (int i = 0; i < side.Children.Count; i++)
            {
                var column = side.Children[i];
                if (order.Top.Find(x => x.Column == column).Item is Folder { Separator: false })
                    column.Margin = new Thickness(i > 0 ? FolderGap : 0, 0, right || i < side.Children.Count - 1 ? FolderGap : 0, 0);
            }
        var master = Column(Master()); Grid.SetColumn(master, 3); mixerStrips.Children.Add(master);
    }

    /// <summary>A strip as a card on the mixer's background, so each channel reads apart from its neighbours.</summary>
    static Border StripCard(double width) => new()
    {
        Width = width, Margin = new Thickness(StripGap / 2, StripGap), Background = Palette.Panel, BorderBrush = Palette.Line,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true,
    };

    /// <summary>Puts a strip's parts on the rows every strip shares (<see cref="StripHeadH"/>…), so all faders and meters
    /// start and end at the same height whatever the name, the strip type or its controls (the master has none: its
    /// row stays empty).</summary>
    Grid StripLayout(Control head, Control? controls, Control faders, Control foot)
    {
        var g = new Grid { Margin = new Thickness(6, 8, 6, 6) };
        g.RowDefinitions.Add(new RowDefinition(StripHeadH, GridUnitType.Pixel));
        var top = new RowDefinition(StripTopH, GridUnitType.Pixel);
        mx.TopRows.Add(top);
        g.RowDefinitions.Add(top);
        g.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        g.RowDefinitions.Add(new RowDefinition(StripFootH, GridUnitType.Pixel));
        void Put(Control c, int row)
        {
            Grid.SetRow(c, row);
            g.Children.Add(c);
        }
        head.VerticalAlignment = VerticalAlignment.Top;
        foot.VerticalAlignment = VerticalAlignment.Bottom;
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        Put(head, 0);
        if (controls != null) { controls.VerticalAlignment = VerticalAlignment.Top; Put(controls, 1); }
        faders.HorizontalAlignment = HorizontalAlignment.Center;
        faders.Margin = new Thickness(0, 6, 0, 0);
        Put(faders, 2);
        Put(foot, 3);
        return g;
    }

    Control Strip(LiveTrack t, double width)
    {
        var box = StripCard(width);
        mx.Boxes.Add(box);
        var top = StripCardLook(box, () => sel == t, () => Timeline.ArmedIndices.Contains(tracks.IndexOf(t)), () => TrackIcons.ColorOf(t.Track), () =>
        {
            int i = tracks.IndexOf(t);
            if (i >= 0 && sel != t) Timeline.Select(i);
        });

        bool empty = !t.Track.HasContent;
        bool Silent() => empty || t.Silenced || (tracks.Any(x => x.Track.HasContent && x.Soloed) && !t.Soloed);
        var name = new TextBlock { Text = t.Track.Name, FontSize = 11.5, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        mx.Refresh.Add(() => { name.Text = t.Track.Name; name.Foreground = Silent() ? Palette.Faint : Palette.Fg; ToolTip.SetTip(name, Loc.T("mix.name.tip", t.Track.Name)); });
        name.DoubleTapped += (_, _) => RenameTrackIn(t, name);
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
        var badge = new TrackBadge { Width = 22, Height = 22 };
        badge.Clicked += () => ShowTrackLook(t, badge);
        mx.Refresh.Add(() => badge.Set(t.Track, tracks.IndexOf(t) + 1, sel == t, !AudioOnly));
        var fader = new Fader();
        fader.Changed += v => EditTrack(t, x => x.Channel.Volume = v, "volume");
        var db = new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        mx.Refresh.Add(() => { fader.Value = t.Channel.Volume; db.Text = $"{Fader.Db(t.Channel.Volume)} dB"; });
        var meter = new MixerMeter();
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        faders.Children.Add(fader); faders.Children.Add(meter);

        // its icon, its name and "VACÍA", its input while armed or its transpose under the name
        var under = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        under.Children.Add(emptyHint); under.Children.Add(input); under.Children.Add(TransposeTagText(t));
        var head = StripHead(badge, name, under);
        // like a console's strip (asked by the user): its effects and its sends as lists, the pan as a bar, then M S ●
        var (controls, gr) = CompactChain(SlotsOf(t), RouterOf(t), () => t.Channel.Pan, v => EditTrack(t, x => x.Channel.Pan = v, "pan"),
            StripToggle("M", Shortcuts.Tip("Mute", "mute"), 26, () => t.Channel.Muted, false, () => EditTrack(t, x => x.Channel.Muted = !x.Channel.Muted, "mute")),
            StripToggle("S", Shortcuts.Tip("Solo", "solo"), 26, () => t.Channel.Solo, true, () => EditTrack(t, x => x.Channel.Solo = !x.Channel.Solo, "solo")),
            ArmToggle(t, 26));
        mx.Meters[t.Track.Id] = (meter, gr);
        var content = new Grid();
        var layout = StripLayout(head, controls, faders, db);
        NarrowParts(layout, faders, fader, meter, db);
        content.Children.Add(layout);
        content.Children.Add(top);
        box.Child = content;
        return box;
    }

    /// <summary>
    /// A track's or a bus's head, in its fixed row (asked by the user: the channel's icon always in sight, its size
    /// following the strip): the badge beside the name and the line under it, or, under <see cref="HeadNameW"/>, the
    /// badge alone over that line (the name is in its tooltip and the inspector).
    /// </summary>
    Control StripHead(TrackBadge badge, TextBlock name, Control under)
    {
        var text = new StackPanel { Spacing = 1, Children = { name, under } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = StripHeadH };
        g.Children.Add(badge);
        g.Children.Add(text);
        mx.Resize.Add(w =>
        {
            badge.Width = badge.Height = Math.Clamp(Math.Round(16 + w * 0.08), 20, 28);
            bool alone = w < HeadNameW;
            name.IsVisible = !alone;
            Grid.SetColumnSpan(badge, alone ? 2 : 1);
            badge.HorizontalAlignment = alone ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            badge.VerticalAlignment = alone ? VerticalAlignment.Top : VerticalAlignment.Center;
            Grid.SetColumn(text, alone ? 0 : 1);
            Grid.SetColumnSpan(text, alone ? 2 : 1);
            text.VerticalAlignment = alone ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            text.Margin = new Thickness(alone ? 0 : 7, 0, 0, 0);
            under.HorizontalAlignment = alone ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        });
        return g;
    }

    /// <summary>A compact strip's parts that give way as it narrows: under <see cref="NarrowW"/> the meter's scale, the
    /// dB under the fader and some margin; under <see cref="TinyW"/> the fader's cap slims down.</summary>
    void NarrowParts(Grid layout, StackPanel? faders, Fader? fader, MixerMeter meter, Control db) => mx.Resize.Add(w =>
    {
        bool narrow = w < NarrowW, tiny = w < TinyW;
        meter.Scale = !narrow;
        db.IsVisible = !narrow;
        // a folder strip has no fader: only its meter
        if (fader != null) fader.Slim = tiny;
        if (faders != null) faders.Spacing = tiny ? 2 : 4;
        double side = tiny ? 2 : narrow ? 3 : 6;
        layout.Margin = new Thickness(side, 8, side, 6);
    });

    /// <summary>
    /// A strip's card look, the same for every channel (asked by the user: a bus selects like a track): hover, selected
    /// (raised, accent border and top edge), armed (tinted red), and its color along the top edge. A press selects it.
    /// Returns the top edge, which the strip lays over its content.
    /// </summary>
    Border StripCardLook(Border box, Func<bool> selected, Func<bool> armed, Func<Color?> color, Action select)
    {
        var top = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        box.Cursor = new Cursor(StandardCursorType.Hand);
        bool hover = false;
        box.PointerEntered += (_, _) => { hover = true; Paint(); };
        box.PointerExited += (_, _) => { hover = false; Paint(); };
        box.PointerPressed += (_, e) => { if (e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) select(); };
        void Paint()
        {
            bool on = selected(), rec = armed();
            // the armed strip (where R records) is tinted red, like its lane in the timeline
            box.Background = rec && !on ? Palette.Alpha(Palette.RecColor, 0.08)
                : on ? Palette.Raised : hover ? Palette.Alpha(Palette.RaisedColor, 0.6) : Palette.Panel;
            box.BorderBrush = rec ? Palette.Alpha(Palette.RecColor, 0.5) : on ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line;
            // a channel with a color shows it along the card's top edge; selected, the accent takes its place
            top.Background = on ? Palette.Accent : color() is { } c ? Palette.Alpha(c, 1) : Brushes.Transparent;
        }
        mx.Refresh.Add(Paint);
        return top;
    }

    /// <summary>A track's name from the mixer: a double click on it asks for the new one (one undo step).</summary>
    void RenameTrackIn(LiveTrack t, Control anchor)
    {
        if (store?.ReadOnly != false || restoring) return;
        AskName(anchor, null, Loc.T("menu.renameTrack"), t.Track.Name, Loc.T("folder.rename.ok"), n =>
        {
            if (n.Trim() is { Length: > 0 } name && name != t.Track.Name) EditTrack(t, x => x.Track.Name = name, "name");
        });
    }

    /// <summary>Height (px) of a row of the compact strip's lists.</summary>
    const double CompactRowH = 18;
    /// <summary>The compact strip's M S ● at their usual width (px); narrower strips squeeze them down to <see cref="MinToggleW"/>.</summary>
    const double ToggleMaxW = 26, MinToggleW = 16;
    /// <summary>
    /// What a compact row shows by its own width (px): its icon always; its power switch from <see cref="RowPowerW"/>
    /// (under it a click opens the effect, where it is switched); its name from <see cref="RowNameW"/>; and what the
    /// effect is set to from <see cref="RowBriefW"/>, in at most <see cref="RowBriefShare"/> of the row. A list's title
    /// shows from <see cref="RowNameW"/>.
    /// </summary>
    const double RowPowerW = 40, RowNameW = 58, RowBriefW = 110, RowBriefShare = 0.42;
    /// <summary>A strip's head shows its name beside its icon from this width (px); under it, the icon alone (asked by
    /// the user: the channel's icon always in sight), its size following the strip's.</summary>
    const double HeadNameW = 84;

    /// <summary>
    /// The compact strip's controls, the same for a track and a bus (asked by the user, like a console's strip): the
    /// effects as a list (a click opens one in its window, <see cref="ShowSlotWindow"/>; dragged, it moves in the chain;
    /// its power switch turns it on or off), the sends as bars with the bus's name (shown with the mixer's "Envíos"; a
    /// bus, which has none, keeps the section empty so its fader stays level), the pan as a bar from the centre, and its
    /// <paramref name="toggles"/> (M S ● or a bus's M). The action writes the first compressor's reduction under its row.
    /// </summary>
    (Control Controls, Action<double> Gr) CompactChain(SlotOwner o, Router? sends, Func<double> panOf, Action<double> setPan, params Control[] toggles)
    {
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
            string id = slot.Id, name = SlotLabel(slots, slot, nth);
            var row = ListRow(name, slot.Type, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, x => x.On = !x.On, "on"),
                () => o.Slot(id) is { } s ? SlotBrief(s) : "");
            if (slot.Type == InsertSlot.CompKind && grBar == null)
            {
                grBar = new Border { Height = 2, Width = 0, Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
                ((Grid)row.Child!).Children.Add(grBar);
                Grid.SetColumnSpan(grBar, 2);
            }
            ToolTip.SetTip(row, Loc.T(slot.Type == InsertSlot.VstKind ? "mix.list.vst.tip" : "mix.list.fx.tip", name));
            row.Tag = id;
            row.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right || store == null) return;
                e.Handled = true; // not the strip's or the mixer's menu too
                ShowSlotMenu(o, id, row, row);
            };
            fxList.Children.Add(drag.Add(row, id, row));
        }
        // a VST3 that is loaded opens its own window at once (asked by the user: no window in between); the others,
        // and a plugin still loading or that failed, open their panel
        drag.Clicked += id =>
        {
            if (o.Slot(id)?.Type == InsertSlot.VstKind && EffectStateOf(o.OwnerId, id) == EffectState.Ready) OpenEffectWindow(o.OwnerId, id);
            else if (fxList.Children.OfType<Border>().FirstOrDefault(c => c.Tag as string == id) is { } r) ShowSlotWindow(o, id, r);
        };
        foreach (var c in fxList.Children.OfType<Border>()) c.Cursor = new Cursor(StandardCursorType.Hand);
        var fxBox = ListBox(fxList, false);
        // the mixer's FX switch puts them all in bypass
        mx.Refresh.Add(() => fxBox.Opacity = Audio.PlaybackEngine.FxBypass ? 0.4 : 1);
        Section(mx.FxRows, ListHead(Loc.T("mix.list.fx"), Loc.T("mix.slot.add.tip"), b => ShowAddSlotMenu(o, b)), fxBox);

        if (sends != null)
            Section(mx.SendRows, ListHead(Loc.T("mix.list.sends"), Loc.T("send.add.tip"), b => OpenAnchoredMenu(Popup(SendChoices(sends), 220), b)), ListBox(CompactSends(sends), true));
        else Section(mx.SendRows, new Border(), new Border());

        var pan = new HBar { Bipolar = true, ResetTo = 0, Margin = new Thickness(0, 4, 0, 6) };
        pan.Changed += setPan;
        mx.Refresh.Add(() =>
        {
            double v = panOf();
            pan.Value = v;
            pan.Readout = FmtPan(v);
            ToolTip.SetTip(pan, Loc.T("tl.tip.pan", FmtPan(v)));
        });
        Grid.SetRow(pan, g.RowDefinitions.Count); g.RowDefinitions.Add(new RowDefinition(PanRowH, GridUnitType.Pixel)); g.Children.Add(pan);

        // M S ● share the width (each at most its usual size), so they still fit a very narrow strip
        var buttons = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, MaxWidth = toggles.Length * (ToggleMaxW + 2) };
        foreach (var toggle in toggles)
        {
            toggle.Width = double.NaN;
            toggle.MaxWidth = ToggleMaxW;
            toggle.HorizontalAlignment = HorizontalAlignment.Stretch;
            toggle.Margin = new Thickness(1, 0);
            Grid.SetColumn(toggle, buttons.ColumnDefinitions.Count);
            buttons.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            buttons.Children.Add(toggle);
        }
        // too narrow for three, ● gives way (it is also in the track's header in the timeline)
        if (toggles.Length > 2)
            buttons.SizeChanged += (_, e) =>
            {
                bool fits = e.NewSize.Width >= toggles.Length * MinToggleW;
                toggles[^1].IsVisible = fits;
                buttons.ColumnDefinitions[^1].Width = fits ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            };
        Grid.SetRow(buttons, g.RowDefinitions.Count); g.RowDefinitions.Add(new RowDefinition(TogglesRowH, GridUnitType.Pixel)); g.Children.Add(buttons);
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
            line.SizeChanged += (_, e) => power.IsVisible = e.NewSize.Width >= RowPowerW;
            // a right click: the send's menu (asked by the user, like an effect's)
            line.Background = Brushes.Transparent;
            line.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right || store == null) return;
                e.Handled = true;
                ShowSendMenu(router, id, line);
            };
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
    /// Sets every strip's controls row and the lists' sections to the shown sections and their heights: only row
    /// heights change, nothing is rebuilt (it runs on every move of a grip).
    /// </summary>
    void ApplyMixerHeights()
    {
        var layout = mixerLayout;
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

    /// <summary>What a slot is called in the compact list: the musician's word (Vida, Comp…).</summary>
    static string SlotName(string type) => type switch
    {
        InsertSlot.SatKind => Loc.T("fx.life"),
        InsertSlot.CompKind => Loc.T("fx.comp"),
        InsertSlot.ReverbKind => "Reverb",
        InsertSlot.DelayKind => "Delay",
        InsertSlot.GateKind => Loc.T("fx.gate"),
        _ => "EQ",
    };

    /// <summary>A compact list's title ("EFECTOS", "ENVÍOS") with its + on the right.</summary>
    Grid ListHead(string title, string addTip, Action<Button> add)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 20 };
        var text = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 9, LetterSpacing = 0.8, FontWeight = FontWeight.SemiBold, Foreground = Palette.Faint,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(text, title);
        g.Children.Add(text);
        g.SizeChanged += (_, e) => text.IsVisible = e.NewSize.Width >= RowNameW;
        var plus = SlotHeadButton("\ue710", addTip);
        plus.Width = plus.Height = plus.MinHeight = 18;
        plus.Click += (_, _) => { if (!PressClosedMenuOf(plus)) add(plus); };
        Grid.SetColumn(plus, 1); g.Children.Add(plus);
        return g;
    }

    /// <summary>
    /// The power switch of a list's row (asked by the user: the ⏻ of a real switch, the same glyph as an effect panel's
    /// ON/OFF): accent when on, grey when off.
    /// </summary>
    Button ListPower(Func<bool> on, Action toggle, List<Action>? refresh = null)
    {
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var b = new Button { Classes = { "ghost", "slothead" }, Width = 16, Height = 16, MinHeight = 16, Padding = new Thickness(0), Focusable = false, Margin = new Thickness(0, 0, 3, 0),
            Cursor = new Cursor(StandardCursorType.Hand), Content = glyph, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => toggle();
        (refresh ?? mx.Refresh).Add(() =>
        {
            bool lit = on();
            glyph.Foreground = lit ? Palette.Accent : Palette.Faint;
            ToolTip.SetTip(b, Loc.T(lit ? "mix.list.off" : "mix.list.on"));
        });
        return b;
    }

    /// <summary>A row of the compact effects list: its power switch and its name, on a block that lights under the pointer.</summary>
    Border ListRow(string name, string type, Func<bool> on, Action toggle, Func<string> brief)
    {
        // its icon stays at any width (asked by the user); the name and what it is set to come in as the row widens
        var icon = FxIcon.Create(type, 11);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 5, 0);
        var text = new TextBlock { Text = name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock { FontSize = 10.5, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), IsVisible = false };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var power = ListPower(on, toggle);
        g.Children.Add(power);
        Grid.SetColumn(icon, 1); g.Children.Add(icon);
        Grid.SetColumn(text, 2); g.Children.Add(text);
        Grid.SetColumn(detail, 3); g.Children.Add(detail);
        var row = new Border { Height = CompactRowH, CornerRadius = new CornerRadius(3), Padding = new Thickness(1, 0, 3, 0), Child = g, Background = Palette.Raised, ClipToBounds = true };
        row.PointerEntered += (_, _) => row.Background = Palette.Hover;
        row.PointerExited += (_, _) => row.Background = Palette.Raised;
        row.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width;
            power.IsVisible = w >= RowPowerW;
            text.IsVisible = w >= RowNameW;
            detail.IsVisible = w >= RowBriefW;
            detail.MaxWidth = Math.Floor(w * RowBriefShare);
            // alone, the icon sits in the middle of the row
            bool alone = !power.IsVisible;
            Grid.SetColumnSpan(icon, alone ? 3 : 1);
            icon.HorizontalAlignment = alone ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            icon.Margin = new Thickness(0, 0, alone ? 0 : 5, 0);
        };
        mx.Refresh.Add(() =>
        {
            bool lit = on();
            text.Foreground = lit ? Palette.Fg : Palette.Faint;
            FxIcon.SetColor(icon, lit ? Palette.Accent : Palette.Faint);
            detail.Text = brief();
        });
        return row;
    }

    /// <summary>What a slot is set to, in a few words (a list row's detail).</summary>
    string SlotBrief(InsertSlot s)
    {
        switch (s.Type)
        {
            case InsertSlot.SatKind when s.Sat is { } x:
                return $"{Loc.T(x.Mode == "tape" ? "insp.sat.tape" : "insp.sat.tube")} · {x.Amount * 100:0}%";
            case InsertSlot.CompKind when s.Comp is { } c:
                return PresetLabel(c.Preset);
            case InsertSlot.GateKind when s.Gate is { } x:
                return $"{Loc.T("gate.mode." + x.Mode)} · {x.Threshold:0} dB";
            case InsertSlot.ReverbKind when s.Reverb is { } r:
                string p = ReverbBus.PresetOf(ToParams(r));
                return $"{(p == "" ? Loc.T("mix.custom.f") : BusPresetName("reverb", p))} · {r.Decay:0.0}s";
            case InsertSlot.DelayKind when s.Delay is { } d:
                string time = !d.Sync ? $"{Math.Round(d.TimeMs):0} ms"
                    : Array.Find(DelayBus.Divisions, x => x.Id == d.Division) is { } div ? Loc.Or("mix.div." + div.Id, div.Label) : d.Division;
                return $"{time} · {d.Feedback * 100:0}%";
            case InsertSlot.EqKind when s.Eq is { } e:
                return EqEditor.PresetIndex(e) is var i and >= 0 ? EqPresetName(TrackEq.Presets[i]) : Loc.T("mix.custom.m");
            default:
                return "";
        }
    }

    /// <summary>
    /// Whose insert slots the panels edit: a track's channel or a bus. The panels only know this, so a bus strip shows
    /// its slots exactly like a track strip.
    /// </summary>
    sealed class SlotOwner
    {
        /// <summary>The track's or the bus's id: whose VST3 effects these are (<see cref="LiveVst"/>).</summary>
        public required string OwnerId { get; init; }
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
        OwnerId = t.Track.Id,
        Slots = () => t.Channel.Slots,
        Edit = (change, key) => EditTrack(t, x => change(x.Channel.Slots), key),
        Recalculate = c => { if (t.Envelope != null) RecalculateInspectorComp(t, c); },
    };

    /// <summary>One slot's panel by its kind, for its window (<see cref="ShowSlotWindow"/>).</summary>
    void SlotPanel(StackPanel fx, SlotOwner o, InsertSlot slot, int nth)
    {
        var menu = SlotMenuButton(o, slot.Id);
        switch (slot.Type)
        {
            case InsertSlot.SatKind: SatPanel(fx, o, slot.Id, nth, menu); break;
            case InsertSlot.CompKind: CompPanel(fx, o, slot.Id, nth, menu); break;
            case InsertSlot.ReverbKind: ReverbSlotPanel(fx, o, slot.Id, nth, menu); break;
            case InsertSlot.DelayKind: DelaySlotPanel(fx, o, slot.Id, nth, menu); break;
            case InsertSlot.GateKind: GatePanel(fx, o, slot.Id, nth, menu); break;
            case InsertSlot.VstKind: VstPanel(fx, o, slot.Id, menu); break;
            default: EqPanel(fx, o, slot.Id, nth, menu); break;
        }
    }

    // what a panel reads while its slot is already gone (removed, an undo) and the strips aren't rebuilt yet
    static readonly SatSettings GoneSat = new();
    static readonly CompSettings GoneComp = new();
    static readonly EqSettings GoneEq = new();
    static readonly GateSettings GoneGate = new();

    /// <summary>The gate's modes in their order, with their names' and hints' keys.</summary>
    static readonly string[] GateModes = [GateParams.Soft, GateParams.Normal, GateParams.Hard];

    /// <summary>The gate's threshold (dBFS) on a 0–1 knob and back, in half dB steps.</summary>
    static double GateKnob(double db) => (db - GateParams.MinThresholdDb) / (GateParams.MaxThresholdDb - GateParams.MinThresholdDb);
    static double GateDb(double v) => Math.Round((GateParams.MinThresholdDb + Math.Clamp(v, 0, 1) * (GateParams.MaxThresholdDb - GateParams.MinThresholdDb)) * 2) / 2;

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

    /// <summary>An edit of one compressor slot, found by id; its preset is measured again unless it is manual.</summary>
    void StripComp(SlotOwner o, string id, Action<CompSettings> edit, string key) => o.Edit(slots =>
    {
        if (slots.Find(s => s.Id == id)?.Comp is not { } c) return;
        edit(c);
        if (c.Preset != "custom") o.Recalculate(c);
    }, $"{id}.comp.{key}");

    int slotEdits;

    void SatPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu)
    {
        SatSettings S() => o.Slot(id)?.Sat ?? GoneSat;
        var sat = FxPanel(fx, SlotTitle("Sat", nth), FxIcon.Sat, () => S().On, () => SlotEdit(o, id, s => s.On = !s.On, "sat.on"),
            on => Loc.T(on ? "insp.power.sat.off" : "insp.power.sat.on"), menu);
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

    void GatePanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu)
    {
        GateSettings G() => o.Slot(id)?.Gate ?? GoneGate;
        var gate = FxPanel(fx, SlotTitle(Loc.T("fx.gate"), nth), FxIcon.Gate, () => G().On, () => SlotEdit(o, id, s => s.On = !s.On, "gate.on"),
            on => Loc.T(on ? "insp.power.gate.off" : "insp.power.gate.on"), menu);
        var knob = FxKnob(gate, Loc.T("mix.gate.knob"), GateKnob(GateParams.DefaultThresholdDb), () => GateKnob(G().Threshold),
            v => SlotEdit(o, id, s => { s.Gate!.Threshold = GateDb(v); s.Gate.On = true; }, "gate.threshold"), () => Loc.T("mix.gate.knob.tip"));
        var side = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var mode = new SelectButton();
        mode.SetItems(GateModes.Select(m => Loc.T("gate.mode." + m)).ToArray(), GateModes.Select(m => (string?)Loc.T($"gate.mode.{m}.hint")).ToArray());
        mode.Selected += i => { string m = GateModes[i]; SlotEdit(o, id, s => { s.Gate!.Mode = m; s.Gate.On = true; }, "gate.mode"); };
        var label = new TextBlock { FontSize = 12, FontWeight = FontWeight.Medium, Classes = { "mono" }, TextTrimming = TextTrimming.CharacterEllipsis };
        mx.Refresh.Add(() =>
        {
            int i = Math.Max(0, Array.IndexOf(GateModes, G().Mode));
            mode.SelectedIndex = i;
            ToolTip.SetTip(mode, Loc.T($"gate.mode.{GateModes[i]}.hint"));
            label.Text = $"{G().Threshold:0.#} dB";
        });
        side.Children.Add(mode); side.Children.Add(label);
        gate.Body.Children.Add(Row(knob, side));
    }

    void CompPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu)
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
            on => Loc.T(on ? "insp.power.comp.off" : "insp.power.comp.on"), head);
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
    }

    void EqPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu)
    {
        EqSettings E() => o.Slot(id)?.Eq ?? GoneEq;
        var eq = FxPanel(fx, SlotTitle("EQ", nth), FxIcon.Eq, () => E().On, () => SlotEdit(o, id, s => s.On = !s.On, "eq.on"),
            on => Loc.T(on ? "insp.power.eq.off" : "insp.power.eq.on"), menu);
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

    /// <summary>The ⋯ in a slot's title bar: its menu (<see cref="ShowSlotMenu"/>).</summary>
    Button SlotMenuButton(LiveTrack t, string id) => SlotMenuButton(SlotsOf(t), id);

    Button SlotMenuButton(SlotOwner o, string id)
    {
        var b = new Button { Classes = { "ghost" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Focusable = false,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        ToolTip.SetTip(b, Loc.T("mix.slot.menu.tip"));
        b.Click += (_, _) =>
        {
            if (!PressClosedMenuOf(b)) ShowSlotMenu(o, id, b, null);
        };
        return b;
    }

    /// <summary>
    /// A slot's actions, the same from its ⋯ and from a right click on it in the mixer's list or the inspector (asked by
    /// the user): open it (a loaded VST3, its own window; another effect from the mixer, its panel in a window over
    /// <paramref name="panelAnchor"/>), turn it on or off, turn the rest of the chain off, move it, repeat it, remove it.
    /// </summary>
    void ShowSlotMenu(SlotOwner o, string id, Control anchor, Control? panelAnchor)
    {
        var slots = o.Slots();
        int i = slots.FindIndex(s => s.Id == id);
        if (i < 0) return;
        var slot = slots[i];
        bool enabled = store is { ReadOnly: false } && !restoring;
        var items = new List<object>();
        if (slot.Type == InsertSlot.VstKind)
            items.Add(MenuAction(Loc.T("mix.slot.openPlugin"), () => { OpenEffectWindow(o.OwnerId, id); return Task.CompletedTask; },
                enabled: EffectStateOf(o.OwnerId, id) == EffectState.Ready, icon: ""));
        else if (panelAnchor != null)
            items.Add(MenuAction(Loc.T("mix.slot.open"), () => { ShowSlotWindow(o, id, panelAnchor); return Task.CompletedTask; }, icon: ""));
        items.Add(MenuAction(Loc.T(slot.On ? "mix.slot.turnOff" : "mix.slot.turnOn"), () => { SlotEdit(o, id, s => s.On = !s.On, "on"); return Task.CompletedTask; },
            enabled: enabled, icon: ""));
        // to hear it alone in the chain: one undo step, like any edit of the list
        items.Add(MenuAction(Loc.T("mix.slot.others"), () => SlotsEdit(o, x => { foreach (var s in x) if (s.Id != id && s.Known) s.On = false; }, "others"),
            enabled: enabled && slots.Exists(s => s.Id != id && s.Known && s.On), icon: "", tip: Loc.T("mix.slot.others.tip")));
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T("mix.slot.up"), () => SlotsEdit(o, x => ChannelInserts.Move(x, id, -1), "move"), enabled: enabled && i > 0, icon: "",
            tip: Loc.T("mix.slot.up.tip")));
        items.Add(MenuAction(Loc.T("mix.slot.down"), () => SlotsEdit(o, x => ChannelInserts.Move(x, id, 1), "move"), enabled: enabled && i < slots.Count - 1, icon: "",
            tip: Loc.T("mix.slot.down.tip")));
        items.Add(MenuAction(Loc.T("mix.slot.duplicate"), () => SlotsEdit(o, x => ChannelInserts.Duplicate(x, id), "duplicate"), enabled: enabled, icon: "",
            tip: Loc.T("mix.slot.duplicate.tip")));
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T("mix.slot.remove"), () => SlotsEdit(o, x => ChannelInserts.Remove(x, id), "remove"), enabled: enabled, danger: true, icon: "",
            tip: Loc.T("mix.slot.remove.tip")));
        OpenAnchoredMenu(Popup(items, 220), anchor);
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

    /// <summary>
    /// The kinds of effect a chain can take: one more at its end, already on. Two entries that open as the pointer goes
    /// over them (asked by the user: all at once the list was too long), NATIVOS (the built-in ones) and VST3 (the ones
    /// found, by maker when there are many), then look for plugins again and manage them.
    /// </summary>
    void ShowAddSlotMenu(SlotOwner o, Control b)
    {
        // the first time, it looks for plugins on its own
        EnsurePlugins();
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
        kinds.Add(Kind(InsertSlot.GateKind, Loc.T("fx.gate"), Loc.T("mix.slot.add.gate")));
        var items = new List<object> { AddSlotGroup(Loc.T("vst.fx.group.native"), FxIcon.Sat, kinds) };
        items.AddRange(VstEffectItems(o));
        OpenAnchoredMenu(Popup(items, 220), b);
    }

    /// <summary>
    /// Port of FxPanel: power, icon and name over the effect's controls, which grey out while it is off. Narrower than
    /// <see cref="FxNarrowW"/> the power button keeps only its glyph and the icon goes, so the name fits.
    /// </summary>
    (Border Box, StackPanel Body) FxPanel(StackPanel host, string title, string icon, Func<bool> on, Action? toggle, Func<bool, string> tip, Control? extra = null)
    {
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var state = new TextBlock { FontSize = 10, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        // a hand over it, and off it lights up in the accent under the pointer (asked by the user, like the row's other
        // buttons; class slotpower in App.axaml)
        var power = new Button { Classes = { "chip", "slotpower" }, Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand), Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { glyph, state } } };
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
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 32, Margin = new Thickness(6, 0) };
        head.Children.Add(power);
        Grid.SetColumn(iconText, 1); head.Children.Add(iconText);
        Grid.SetColumn(name, 2); head.Children.Add(name);
        if (extra != null) { extra.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(extra, 3); head.Children.Add(extra); }
        var body = new StackPanel { Margin = new Thickness(8) };
        var stack = new StackPanel();
        var bar = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(5, 5, 0, 0) };
        stack.Children.Add(bar);
        stack.Children.Add(body);
        var box = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = stack, ClipToBounds = true };
        box.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < FxNarrowW;
            state.IsVisible = !narrow;
            iconText.IsVisible = !narrow;
            head.Margin = new Thickness(narrow ? 4 : 6, 0);
            name.Margin = new Thickness(narrow ? 4 : 6, 0, narrow ? 2 : 4, 0);
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
            // a panel a step above the card (asked by the user: each effect reads as its own block), its title bar one
            // more step up; off, both sink back toward the card
            box.Background = Palette.Alpha(Palette.HoverColor, lit ? 0.45 : 0.18);
            box.BorderBrush = lit ? Palette.Line : Palette.Alpha(Palette.LineColor, 0.6);
            bar.Background = Palette.Alpha(Palette.HoverColor, lit ? 0.6 : 0.2);
            bar.BorderBrush = box.BorderBrush;
        });
        host.Children.Add(box);
        return (box, body);
    }

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

    /// <summary>The slot shown in a window of its own, and its panel's refreshes (run with the mixer's while it is open).</summary>
    Flyout? slotWindow;
    List<Action>? slotWindowRefresh;

    /// <summary>
    /// One slot's whole panel in a small window over the mixer (a click on its row): built into a layout of its own,
    /// it edits the same slot and refreshes with the mixer while it is open.
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
        try { SlotPanel(host, o, slots[i], nth); }
        finally { mx = saved; }
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
            ToolTip.SetTip(b, lit ? Loc.T(t.Track.IsMidi ? "midi.arm.armed" : "tl.tip.armed", Shortcuts.Paren("record"))
                : t.Track.IsMidi ? Loc.T("midi.arm.tip") + Shortcuts.Note("arm", Loc.T("tl.note.arm"))
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
        mx.Refresh.Add(() => text.Text = t.Track.IsMidi ? MidiInputShort(t.Track) : InputShort(t.Track));
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

    Control Master()
    {
        var box = StripCard(MasterW);
        // the head's fixed row (StripHeadH): the button, then LIM and MONO side by side
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(MasterButton());
        mx.Lim = new TextBlock { Text = "LIM", Classes = { "mono" }, FontSize = 9, FontWeight = FontWeight.Bold, LetterSpacing = 0.8, Foreground = Palette.Accent, Opacity = 0.25,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(mx.Lim, Loc.T("mix.master.lim.tip"));
        var mono = StripToggle("MONO", Loc.T("mix.master.mono.tip"), 14, () => engine.MonoCheck, false, () =>
        {
            engine.MonoCheck = !engine.MonoCheck;
            RefreshMixer();
        });
        mono.Width = 32;
        mono.CornerRadius = new CornerRadius(3);
        ((TextBlock)mono.Content!).FontSize = 8;
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { mx.Lim, mono } });
        mx.Master = new MixerMeter(wide: true);
        // the foot shows the output's short-term LUFS (the transport's measurement), where "Salida" was, and under it
        // the correlation (two short lines in the fixed StripFootH)
        mx.Lufs = new TextBlock { Text = "—", Classes = { "mono" }, FontSize = 10, LineHeight = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        mx.LufsTenths = int.MaxValue;
        var lufs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
            Children = { mx.Lufs, new TextBlock { Text = "LUFS", FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center } } };
        ToolTip.SetTip(lufs, Loc.T("mix.master.lufs.tip"));
        mx.Cor = new TextBlock { Text = "—", Classes = { "mono" }, FontSize = 8.5, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
        mx.CorHundredths = int.MaxValue;
        var cor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 10, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
            Children = { mx.Cor, new TextBlock { Text = "COR", FontSize = 8, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center } } };
        ToolTip.SetTip(cor, Loc.T("mix.master.cor.tip"));
        var output = new StackPanel { Children = { lufs, cor } };
        box.Child = StripLayout(head, null, mx.Master, output);
        return box;
    }
}
