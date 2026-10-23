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
/// knobs; the reverb and delay returns with their presets; the master with its LIM light; pan templates for the whole
/// mix. Every change is an undo step (same keys as the inspector), saved and heard live. Same as the web except: the
/// toggle sits in the bottom transport bar (where the native transport is), the EQ summary has no live spectrum yet
/// (like the inspector), and empty strips edit their channel without the take analysis (as in the web). Also unlike
/// the web (asked by the user): every strip is a card, all strips share the same rows so every meter has the same
/// height, and narrow effect panels rearrange instead of cutting their controls.
/// </summary>
public partial class MainWindow
{
    // Strip widths (px), as in the web (compact a bit wider for the card's border). "Amplio" strips grow to share the free width.
    const double CompactW = 96, WideMin = 200, WideMax = 400, ReturnCompactW = 176, ReturnWideW = 230, MasterW = 76;
    /// <summary>Rows every strip shares (takes, returns and master), so all faders and meters start and end at the same
    /// height: head, controls (compact strips only: in wide ones they sit beside the fader), fader, foot.</summary>
    const double StripHeadH = 38, StripTopH = 124, StripFootH = 22;
    /// <summary>Space between strip cards.</summary>
    const double StripGap = 6;
    /// <summary>Below this width an effect panel drops its ON/OFF text and icon, and puts its knob over its other controls.</summary>
    const double FxNarrowW = 150;
    /// <summary>The mixer keeps its own height: wide strips want more room than the timeline.</summary>
    const double MixerDefaultH = 340, MixerMinH = 250;

    readonly List<Action> mixerRefresh = [];
    readonly Grid mixerStrips = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
    readonly ScrollViewer mixerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly MiniTimeline miniTimeline = new();
    readonly List<Button> panTemplateButtons = [];
    readonly Dictionary<string, (MixerMeter Meter, Action<double> Gr)> stripMeters = [];
    MixerMeter? reverbMeter, delayMeter, masterMeter;
    TextBlock? limLight;
    string mixerKey = "";
    string mixerWidth = "auto";
    double panWidth = 0.6;
    const double TimelineDefaultH = 280, TimelineMinH = 110; // web: TIMELINE_MIN_H (its default is 240; the native layout keeps 280)
    double timelineH = TimelineDefaultH, mixerH = MixerDefaultH;
    float masterShow, reverbShow, delayShow;
    bool mixerView;

    static string FmtPan(double v) => Math.Abs(v) < 0.01 ? "C" : v < 0 ? $"L{Math.Round(-v * 100):0}" : $"R{Math.Round(v * 100):0}";
    static string FmtHz(double v) => v >= 1000 ? $"{v / 1000:0.0}k" : $"{Math.Round(v):0}";

    /// <summary>Knobs for wide ranges move on a log scale (0..1 knob position), like the web's logMap.</summary>
    static (Func<double, double> To, Func<double, double> From) LogMap(double lo, double hi) =>
        (v => Math.Log(Math.Clamp(v, lo, hi) / lo) / Math.Log(hi / lo), p => Math.Round(lo * Math.Pow(hi / lo, p) * 100) / 100);
    static readonly (Func<double, double> To, Func<double, double> From) DecayMap = LogMap(0.2, 10), ToneMap = LogMap(1000, 18000),
        LowCutMap = LogMap(20, 1000), DelayMsMap = LogMap(20, 2000);

    static ReverbParams ToParams(ReverbSettings r) => new(r.Decay, r.PreDelayMs, r.Tone, r.LowCut, r.Width, r.ReturnGain, r.Muted);
    static DelayParams ToParams(DelaySettings d) => new(DelayParams.ParseMode(d.Mode), d.Sync, d.Division, d.TimeMs, d.Feedback, d.Tone, d.LowCut, d.Width, d.Duck, d.ReturnGain, d.Muted);

    ReverbSettings Reverb => store?.Project.Settings.Reverb ?? new();
    DelaySettings Delay => store?.Project.Settings.Delay ?? new();

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
        BottomSplitter.DoubleTapped += (_, _) =>
        {
            bottom.Height = new GridLength(mixerView ? MixerDefaultH : TimelineDefaultH);
            Dispatcher.UIThread.Post(SaveBottomHeight, DispatcherPriority.Background);
        };

        miniTimeline.SeekRequested += sec => engine.Seek(sec);
        var header = BuildMixerHeader();
        mixerScroll.Content = mixerStrips;
        mixerScroll.SizeChanged += (_, _) => { mixerStrips.MinWidth = mixerScroll.Viewport.Width; RefreshMixer(); };
        DockPanel.SetDock(miniTimeline, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        MixerHost.Children.Add(miniTimeline);
        MixerHost.Children.Add(header);
        MixerHost.Children.Add(mixerScroll);

        TimelineViewButton.Click += (_, _) => SetMixerView(false);
        MixerViewButton.Click += (_, _) => SetMixerView(true);
    }

    void SetMixerView(bool mixer)
    {
        if (mixer == mixerView) return;
        var row = Root.RowDefinitions[3];
        if (mixerView) mixerH = row.ActualHeight; else timelineH = row.ActualHeight;
        mixerView = mixer;
        row.MinHeight = mixer ? MixerMinH : TimelineMinH;
        row.Height = new GridLength(mixer ? mixerH : timelineH);
        TimelineHost.IsVisible = !mixer;
        MixerHost.IsVisible = mixer;
        ToolTip.SetTip(BottomSplitter, $"Arrastrá para cambiar la altura {(mixer ? "del mixer" : "de la timeline")}");
        TimelineViewButton.Classes.Set("on", !mixer);
        MixerViewButton.Classes.Set("on", mixer);
        SaveViewPref(mixer ? "timelineHeight" : "mixerHeight", (mixer ? timelineH : mixerH).ToString(System.Globalization.CultureInfo.InvariantCulture));
        RefreshMixer();
    }

    /// <summary>Remembers the height of the view on screen (gc.timelineHeight / gc.mixerHeight in the web).</summary>
    void SaveBottomHeight()
    {
        double h = Root.RowDefinitions[3].ActualHeight;
        if (mixerView) mixerH = h; else timelineH = h;
        SaveViewPref(mixerView ? "mixerHeight" : "timelineHeight", h.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    Control BuildMixerHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "MIXER", Classes = { "label" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Line });
        row.Children.Add(new TextBlock { Text = "Paneo:", FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        var templates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var t in PanTemplates.All)
        {
            var b = new Button { Classes = { "chip" }, Content = t.Label, FontSize = 12, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0) };
            ToolTip.SetTip(b, t.Hint);
            string id = t.Id;
            b.Click += (_, _) => ApplyPanTemplate(id);
            panTemplateButtons.Add(b);
            templates.Children.Add(b);
        }
        row.Children.Add(templates);
        var open = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ToolTip.SetTip(open, "Qué tan abiertas quedan las pistas con la plantilla");
        open.Children.Add(new TextBlock { Text = "Apertura", FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        var width = new InspectorRange { Minimum = 0.1, Maximum = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true, SmallChange = 0.05, Width = 96, Value = panWidth, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(width, "Apertura");
        var widthText = new TextBlock { Text = $"{panWidth * 100:0}%", Classes = { "mono" }, FontSize = 11, Foreground = Palette.Dim, Width = 32, VerticalAlignment = VerticalAlignment.Center };
        width.ValueChanged += (_, e) => { panWidth = Math.Round(e.NewValue * 20) / 20; widthText.Text = $"{panWidth * 100:0}%"; };
        open.Children.Add(width); open.Children.Add(widthText);
        row.Children.Add(open);

        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label, tip) in new[]
        {
            ("auto", "Auto", "Canales amplios cuando entran (2–3 tomas), compactos si hay muchas"),
            ("compact", "Compacto", "Canales angostos"),
            ("wide", "Amplio", "Canales anchos con controles grandes"),
        })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = label, FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { mixerWidth = id; SaveViewPref("mixerWidth", id); RefreshMixer(); };
            // the header's own refreshers survive strip rebuilds
            headerRefresh.Add(() => b.IsChecked = mixerWidth == id);
            seg.Children.Add(b);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 0) };
        grid.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var segBox = new Border { Classes = { "seg" }, Child = seg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(segBox, 1); grid.Children.Add(segBox);
        return new Border { Height = 40, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    readonly List<Action> headerRefresh = [];

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

    /// <summary>Reverb / delay bus edits: one undo step per control (reverb:decay…), applied to the engine live.</summary>
    void BusEdit(Action<ProjectSettings> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project, key);
        var s = store.Project.Settings;
        change(s);
        engine.ApplyReverb(s.Reverb);
        engine.ApplyDelay(s.Delay);
        MarkDirty(); UpdateUndoButtons(); RefreshMixer();
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
        double areaW = mixerScroll.Viewport.Width > 0 ? mixerScroll.Viewport.Width : MixerHost.Bounds.Width;
        int n = Math.Max(1, tracks.Count);
        double freeWide = (areaW - 2 * ReturnWideW - MasterW - 3 * StripGap) / n - StripGap;
        bool wide = mixerWidth == "wide" || (mixerWidth == "auto" && freeWide >= WideMin);
        double stripW = wide ? Math.Max(WideMin, Math.Min(WideMax, Math.Floor(freeWide))) : CompactW;
        string key = $"{wide}|{stripW}|{string.Join(",", tracks.Select(t => t.Track.Id + ":" + t.Clips.Count))}";
        if (key != mixerKey) { mixerKey = key; BuildStrips(wide, stripW); }
        miniTimeline.SetTracks(tracks);
        bool was = syncing;
        syncing = true;
        try
        {
            bool editable = store != null && !store.ReadOnly && !restoring;
            mixerStrips.IsEnabled = editable;
            foreach (var b in panTemplateButtons) b.IsEnabled = editable && tracks.Any(t => t.Clips.Count > 0);
            foreach (var r in headerRefresh) r();
            foreach (var r in mixerRefresh) r();
        }
        finally { syncing = was; }
    }

    /// <summary>Called from the window tick: meters, compressor reduction, LIM and the mini timeline's playhead.</summary>
    void TickMixer(double pos, float outL, float outR)
    {
        var (rl, rr, dl, dr) = engine.TakeReturnMeters();
        double lim = engine.Limiter.TakeReductionDb();
        if (!mixerView) return;
        miniTimeline.SetPlayhead(pos, tracks.Count == 0 ? 0 : tracks.Max(t => t.EndSec), RecordingBusy);
        foreach (var t in tracks)
            if (stripMeters.TryGetValue(t.Track.Id, out var m))
            {
                m.Meter.Level = Math.Max(t.ShowL, t.ShowR);
                m.Gr(t.GrShow);
            }
        masterShow = Math.Max(Math.Max(outL, outR), masterShow * 0.82f);
        reverbShow = Math.Max(Math.Max(rl, rr), reverbShow * 0.82f);
        delayShow = Math.Max(Math.Max(dl, dr), delayShow * 0.82f);
        if (masterMeter != null) masterMeter.Level = masterShow;
        if (reverbMeter != null) reverbMeter.Level = reverbShow;
        if (delayMeter != null) delayMeter.Level = delayShow;
        if (limLight != null) limLight.Opacity = lim < -0.5 ? 1 : 0.25;
    }

    // --- strips ----------------------------------------------------------------------------------------

    void BuildStrips(bool wide, double stripW)
    {
        mixerRefresh.Clear();
        stripMeters.Clear();
        mixerStrips.Children.Clear();
        var strips = new StackPanel { Orientation = Orientation.Horizontal };
        mixerStrips.Children.Add(strips);
        if (tracks.Count == 0)
        {
            var empty = new TextBlock { Text = "Grabá una toma para ver su canal en el mixer.", FontSize = 12, Foreground = Palette.Faint,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(empty, 1);
            mixerStrips.Children.Add(empty);
        }
        for (int i = 0; i < tracks.Count; i++) strips.Children.Add(Strip(tracks[i], i, wide, stripW));

        var reverb = ReverbReturn(wide); Grid.SetColumn(reverb, 2); mixerStrips.Children.Add(reverb);
        var delay = DelayReturn(wide); Grid.SetColumn(delay, 3); mixerStrips.Children.Add(delay);
        var master = Master(wide); Grid.SetColumn(master, 4); mixerStrips.Children.Add(master);
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
    static Grid StripLayout(bool wide, Control head, Control? controls, Control faders, Control foot)
    {
        var g = new Grid { Margin = new Thickness(wide ? 8 : 6, 8, wide ? 8 : 6, 6) };
        g.RowDefinitions.Add(new RowDefinition(StripHeadH, GridUnitType.Pixel));
        if (!wide) g.RowDefinitions.Add(new RowDefinition(StripTopH, GridUnitType.Pixel));
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
            bool selected = sel == t;
            box.Background = selected ? Palette.Raised : hover ? Palette.Alpha(Palette.RaisedColor, 0.6) : Palette.Panel;
            box.BorderBrush = selected ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line;
            top.Background = selected ? Palette.Accent : Brushes.Transparent;
        }
        mixerRefresh.Add(Paint);

        bool empty = t.Clips.Count == 0;
        bool Silent() => empty || t.Channel.Muted || (tracks.Any(x => x.Clips.Count > 0 && x.Channel.Solo) && !t.Channel.Solo);
        var name = new TextBlock { Text = t.Track.Name, FontSize = wide ? 12 : 11, FontWeight = wide ? FontWeight.Medium : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        mixerRefresh.Add(() => { name.Text = t.Track.Name; name.Foreground = Silent() ? Palette.Faint : Palette.Fg; ToolTip.SetTip(name, t.Track.Name); });
        var emptyHint = new TextBlock { Text = "VACÍA", FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, IsVisible = empty, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(emptyHint, "Pista vacía: estos ajustes los toma la próxima toma que grabes acá");
        var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = (index + 1).ToString(), Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        mixerRefresh.Add(() =>
        {
            bool s = sel == t;
            badge.Background = s ? Palette.AccentSoft : Palette.Hover;
            ((TextBlock)badge.Child!).Foreground = s ? Palette.Accent : Palette.Dim;
        });
        double btn = wide ? 28 : 24;
        var mute = StripToggle("M", "Mute (M)", btn, () => t.Channel.Muted, false, () => EditTrack(t, x => x.Channel.Muted = !x.Channel.Muted, "mute"));
        var solo = StripToggle("S", "Solo (S)", btn, () => t.Channel.Solo, true, () => EditTrack(t, x => x.Channel.Solo = !x.Channel.Solo, "solo"));
        var fader = new Fader();
        fader.Changed += v => EditTrack(t, x => x.Channel.Volume = v, "volume");
        var db = new TextBlock { Classes = { "mono" }, FontSize = wide ? 11 : 10, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        mixerRefresh.Add(() => { fader.Value = t.Channel.Volume; db.Text = $"{Fader.Db(t.Channel.Volume)} dB"; });
        var meter = new MixerMeter();
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        faders.Children.Add(fader); faders.Children.Add(meter);

        Control head, controls, foot;
        if (wide)
        {
            var h = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Height = 28 };
            h.Children.Add(badge);
            name.Margin = new Thickness(8, 0); Grid.SetColumn(name, 1); h.Children.Add(name);
            emptyHint.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(emptyHint, 2); h.Children.Add(emptyHint);
            Grid.SetColumn(mute, 3); h.Children.Add(mute);
            solo.Margin = new Thickness(4, 0, 0, 0); Grid.SetColumn(solo, 4); h.Children.Add(solo);
            head = h;
            var fx = new StackPanel { Spacing = 6 };
            WideFx(fx, t);
            controls = new ScrollViewer { Content = fx, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            foot = db;
            stripMeters[t.Track.Id] = (meter, wideGr ?? (_ => { }));
            wideGr = null;
        }
        else
        {
            // the name on one line and "VACÍA" under it, in a head of fixed height: a long name never pushes the fader down
            var h = new StackPanel { Spacing = 2 };
            name.HorizontalAlignment = HorizontalAlignment.Center; name.TextAlignment = TextAlignment.Center; name.Margin = new Thickness(0, 4, 0, 0);
            h.Children.Add(name);
            emptyHint.HorizontalAlignment = HorizontalAlignment.Center; h.Children.Add(emptyHint);
            head = h;
            var stack = new StackPanel { Spacing = 6 };
            var knobs = new UniformGrid { Columns = 2 };
            knobs.Children.Add(MiniKnob("Pan", -1, 1, 0, () => t.Channel.Pan, () => FmtPan(t.Channel.Pan),
                v => EditTrack(t, x => x.Channel.Pan = Math.Abs(v) < 0.04 ? 0 : v, "pan"), () => $"Paneo {FmtPan(t.Channel.Pan)} (doble click: centro)"));
            knobs.Children.Add(MiniKnob("Comp", 0, 1, 0.5, () => t.Channel.Comp.Amount,
                () => t.Channel.Comp.Preset == "custom" ? "man" : $"{t.Channel.Comp.Amount * 100:0}",
                v => StripComp(t, c => { c.Amount = v; c.On = true; }, "amount"),
                () => $"Compresión {CompPresets.PresetLabel(t.Channel.Comp.Preset)}: {CompPresets.AmountLabel(t.Channel.Comp.Amount)} (estilo y limitador en la pestaña Toma)"));
            knobs.Children.Add(MiniKnob("Rev", 0, 1, 0, () => t.Channel.ReverbSend, () => t.Channel.ReverbOn ? $"{t.Channel.ReverbSend * 100:0}" : "off",
                v => EditTrack(t, x => { x.Channel.ReverbSend = v; x.Channel.ReverbOn = true; }, "reverb.send"),
                () => $"Envío a la reverb {t.Channel.ReverbSend * 100:0}% (doble click: 0)"));
            knobs.Children.Add(MiniKnob("Delay", 0, 1, 0, () => t.Channel.DelaySend, () => t.Channel.DelayOn ? $"{t.Channel.DelaySend * 100:0}" : "off",
                v => EditTrack(t, x => { x.Channel.DelaySend = v; x.Channel.DelayOn = true; }, "delay.send"),
                () => $"Envío al delay {t.Channel.DelaySend * 100:0}% (doble click: 0)"));
            stack.Children.Add(knobs);
            var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            toggles.Children.Add(mute); toggles.Children.Add(solo);
            // C = compressor on/off, with its gain reduction as a bar along the bottom
            var grBar = new Border { Height = 3, Width = 0, Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
            var cText = new TextBlock { Text = "C", Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var c = new Button { Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), ClipToBounds = true,
                Content = new Grid { Width = 22, Height = 22, Children = { cText, grBar } }, Classes = { "chip" } };
            c.Click += (_, _) => StripComp(t, x => x.On = !x.On, "on");
            mixerRefresh.Add(() =>
            {
                bool on = t.Channel.Comp.On;
                c.Background = on ? Palette.AccentSoft : Brushes.Transparent;
                c.BorderBrush = on ? Palette.Alpha(Palette.AccentColor, 0.5) : Palette.Line;
                cText.Foreground = on ? Palette.Accent : Palette.Faint;
                ToolTip.SetTip(c, $"Compresor {(on ? "activado" : "apagado")} · {CompPresets.PresetLabel(t.Channel.Comp.Preset)}");
            });
            toggles.Children.Add(c);
            stack.Children.Add(toggles);
            controls = stack;
            var f = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            f.Children.Add(badge); f.Children.Add(db);
            foot = f;
            stripMeters[t.Track.Id] = (meter, gr => grBar.Width = 22 * Math.Clamp(-gr / 24, 0, 1));
        }
        var content = new Grid();
        content.Children.Add(StripLayout(wide, head, controls, faders, foot));
        content.Children.Add(top);
        box.Child = content;
        return box;
    }

    /// <summary>Set by <see cref="WideFx"/> for the strip being built: writes the "Comprime" meter.</summary>
    Action<double>? wideGr;

    void WideFx(StackPanel fx, LiveTrack t)
    {
        var pan = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0) };
        pan.Children.Add(new TextBlock { Text = "PANEO", FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        var panKnob = new Knob(24);
        panKnob.Changed += v => EditTrack(t, x => x.Channel.Pan = Math.Abs(v) < 0.04 ? 0 : v, "pan");
        var panText = new TextBlock { Classes = { "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        mixerRefresh.Add(() => { panKnob.Value = t.Channel.Pan; panKnob.Tip = $"Paneo {FmtPan(t.Channel.Pan)} (doble click: centro)"; panText.Text = FmtPan(t.Channel.Pan); });
        pan.Children.Add(panKnob); pan.Children.Add(panText);
        fx.Children.Add(pan);

        // Sat
        var sat = FxPanel(fx, "Sat", FxIcon.Sat, () => t.Channel.Sat.On, () => EditTrack(t, x => x.Channel.Sat.On = !x.Channel.Sat.On, "sat.on"),
            on => on ? "Apagar la saturación" : "Prender la saturación");
        var satKnob = FxKnob(sat, "vida", 0.3, () => t.Channel.Sat.Amount, v => EditTrack(t, x => { x.Channel.Sat.Amount = v; x.Channel.Sat.On = true; }, "sat.amount"),
            () => "Vida: cuánto empuja la toma contra la válvula o la cinta (doble click: 30%)");
        var satSide = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var modes = new[] { ("tube", "Válvula", "Armónicos pares: más cálido y redondo, los picos se ablandan sin perder brillo"),
            ("tape", "Cinta", "Compacta los golpes, suaviza los agudos ásperos y suma un poco de cuerpo en los graves") };
        var mode = new SelectButton();
        mode.SetItems(modes.Select(m => m.Item2).ToArray(), modes.Select(m => (string?)m.Item3).ToArray());
        mode.Selected += i => { string id = modes[i].Item1; EditTrack(t, x => { x.Channel.Sat.Mode = id; x.Channel.Sat.On = true; }, "sat.mode"); };
        var satLabel = new TextBlock { FontSize = 12, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        mixerRefresh.Add(() =>
        {
            int i = Array.FindIndex(modes, m => m.Item1 == t.Channel.Sat.Mode);
            mode.SelectedIndex = Math.Max(0, i);
            ToolTip.SetTip(mode, modes[Math.Max(0, i)].Item3);
            double a = t.Channel.Sat.Amount;
            satLabel.Text = $"{(a < .2 ? "Apenas" : a < .45 ? "Sutil" : a < .7 ? "Con vida" : a < .9 ? "Caliente" : "Al palo")} · {a * 100:0}%";
        });
        satSide.Children.Add(mode); satSide.Children.Add(satLabel);
        sat.Body.Children.Add(Row(satKnob, satSide));

        // Comp
        var lim = new Button { Content = "LIM", Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold, Height = 24, MinHeight = 24, Padding = new Thickness(5, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        lim.Click += (_, _) => StripComp(t, c => c.Limit = !c.Limit, "limit");
        mixerRefresh.Add(() =>
        {
            var c = t.Channel.Comp;
            lim.Background = c.Limit && c.On ? Palette.AccentSoft : Brushes.Transparent;
            lim.Foreground = c.Limit && c.On ? Palette.Accent : Palette.Faint;
            lim.BorderBrush = c.Limit && c.On ? Palette.Alpha(Palette.AccentColor, 0.6) : c.Limit ? Palette.Alpha(Palette.FaintColor, 0.6) : Palette.Line;
            ToolTip.SetTip(lim, $"Limitador de picos {(c.Limit ? "activado" : "apagado")}: frena golpes de púa y notas fuertes" + (c.On ? "" : " (actúa cuando el compresor está prendido)"));
        });
        var comp = FxPanel(fx, "Comp", FxIcon.Comp, () => t.Channel.Comp.On, () => StripComp(t, c => c.On = !c.On, "on"),
            on => on ? "Apagar el compresor" : "Prender el compresor", lim);
        var compKnob = FxKnob(comp, "cantidad", 0.5, () => t.Channel.Comp.Amount, v => StripComp(t, c => { c.Amount = v; c.On = true; }, "amount"),
            () => t.Channel.Comp.Preset == "custom" ? "Compresor en Manual: elegí un estilo para usar este knob" : "Cantidad de compresión (rueda o arrastre · doble click: media)");
        mixerRefresh.Add(() => { bool custom = t.Channel.Comp.Preset == "custom"; compKnob.Opacity = custom ? 0.4 : 1; compKnob.IsHitTestVisible = !custom; });
        var compSide = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var preset = StripSelect([]);
        preset.Selected += i =>
        {
            if (i >= CompPresets.All.Length) return;
            string id = CompPresets.All[i].Id;
            StripComp(t, c => { c.Preset = id; c.On = true; }, "preset");
        };
        var compLabel = new TextBlock { FontSize = 12, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        mixerRefresh.Add(() =>
        {
            var c = t.Channel.Comp;
            bool custom = c.Preset == "custom";
            preset.SetItems(CompPresets.All.Select(p => p.Label).Concat(custom ? new[] { "Manual" } : []).ToArray(),
                CompPresets.All.Select(p => (string?)p.Hint).Concat(custom ? new string?[] { null } : []).ToArray());
            int i = Array.FindIndex(CompPresets.All, p => p.Id == c.Preset);
            preset.SelectedIndex = custom ? CompPresets.All.Length : i;
            ToolTip.SetTip(preset, i >= 0 ? CompPresets.All[i].Hint : "Valores a mano (Toma → Ajuste fino)");
            compLabel.Text = custom ? "Valores a mano" : CompPresets.AmountLabel(c.Amount);
            ToolTip.SetTip(compLabel, $"{(custom ? "" : $"comprime ~{EstimatedCompression(c):0} dB · ")}{c.Threshold:0} dB · {c.Ratio:0.0}:1 · ganancia {c.Makeup:+0.0;-0.0;0.0} dB");
        });
        compSide.Children.Add(preset); compSide.Children.Add(compLabel);
        comp.Body.Children.Add(Row(compKnob, compSide));
        // gain reduction: 0..12 dB with ticks every 3 dB; label and readout on one line, the bar under them, so it fits any width
        var grRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        ToolTip.SetTip(grRow, "Cuánto está bajando el compresor (y el limitador) el volumen ahora");
        grRow.Children.Add(new TextBlock { Text = "COMPRIME", FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
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
            grText.Text = t.Channel.Comp.On ? $"{(gr > -0.05 ? 0 : gr):0.0} dB" : "—";
        };

        // EQ: a summary; the bands are edited in the Toma tab
        var eq = FxPanel(fx, "EQ", FxIcon.Eq, () => t.Channel.Eq.On, () => EditTrack(t, x => x.Channel.Eq.On = !x.Channel.Eq.On, "eq.on"),
            on => on ? "Apagar el EQ" : "Prender el EQ");
        var graph = new EqGraph(interactive: false, height: 54);
        ToolTip.SetTip(graph, "Editá las bandas en la pestaña Toma (izquierda)");
        var eqPreset = StripSelect([]);
        eqPreset.Margin = new Thickness(0, 6, 0, 0);
        eqPreset.Selected += index =>
        {
            int i = index - (EqEditor.PresetIndex(t.Channel.Eq) < 0 ? 1 : 0);
            if (i >= 0) EditTrack(t, x => x.Channel.Eq = EqEditor.FromPreset(i, x.Channel.Eq), "eq");
        };
        mixerRefresh.Add(() =>
        {
            graph.Set(t.Channel.Eq, 0);
            int p = EqEditor.PresetIndex(t.Channel.Eq);
            eqPreset.SetItems((p < 0 ? new[] { "Personalizado" } : []).Concat(TrackEq.Presets.Select(x => x.Label)).ToArray(),
                (p < 0 ? new string?[] { null } : []).Concat(TrackEq.Presets.Select(x => (string?)x.Hint)).ToArray());
            eqPreset.SelectedIndex = p < 0 ? 0 : p;
            ToolTip.SetTip(eqPreset, p < 0 ? "Ajustado a mano" : TrackEq.Presets[p].Hint);
        });
        eq.Body.Children.Add(graph); eq.Body.Children.Add(eqPreset);

        // Reverb and delay sends
        SendPanel(fx, t, "Reverb", FxIcon.Reverb, true);
        SendPanel(fx, t, "Delay", FxIcon.Delay, false);
    }

    void SendPanel(StackPanel fx, LiveTrack t, string title, string icon, bool reverb)
    {
        var panel = FxPanel(fx, title, icon, () => reverb ? t.Channel.ReverbOn : t.Channel.DelayOn,
            () => EditTrack(t, x => { if (reverb) x.Channel.ReverbOn = !x.Channel.ReverbOn; else x.Channel.DelayOn = !x.Channel.DelayOn; }, reverb ? "reverb.on" : "delay.on"),
            on => (on ? "Apagar " : "Prender ") + (reverb ? "el envío a la reverb" : "el envío al delay"));
        var knob = new Knob(34, reverb ? "Envío a la reverb (rueda o arrastre · doble click: 0)" : "Envío al delay (rueda o arrastre · doble click: 0)") { Min = 0, Max = 1, ResetTo = 0 };
        knob.Changed += v => EditTrack(t, x =>
        {
            if (reverb) { x.Channel.ReverbSend = v; x.Channel.ReverbOn = true; }
            else { x.Channel.DelaySend = v; x.Channel.DelayOn = true; }
        }, reverb ? "reverb.send" : "delay.send");
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var pct = new TextBlock { Classes = { "mono" }, FontSize = 14, FontWeight = FontWeight.SemiBold };
        var info = new TextBlock { FontSize = 11, Foreground = Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(info, reverb ? "El sonido de la reverb se ajusta en el canal Reverb, a la derecha" : "El sonido del delay se ajusta en el canal Delay, a la derecha");
        side.Children.Add(pct); side.Children.Add(info);
        mixerRefresh.Add(() =>
        {
            double send = reverb ? t.Channel.ReverbSend : t.Channel.DelaySend;
            knob.Value = send;
            pct.Text = $"{send * 100:0}%";
            if (reverb)
            {
                string p = ReverbBus.PresetOf(ToParams(Reverb));
                info.Text = $"{(p == "" ? "Personalizada" : p)} · {Reverb.Decay:0.0} s";
            }
            else
            {
                var d = ToParams(Delay);
                string p = DelayBus.PresetOf(d);
                info.Text = $"{(p == "" ? "Personalizado" : p)} · {DelayBus.TimeLabel(d)}";
            }
        });
        panel.Body.Children.Add(Row(knob, side));
    }

    /// <summary>Port of FxPanel: power, icon and name over the effect's controls, which grey out while it is off.
    /// Narrower than <see cref="FxNarrowW"/> the power button keeps only its glyph and the icon goes, so the name fits.</summary>
    (Border Box, StackPanel Body) FxPanel(StackPanel host, string title, string icon, Func<bool> on, Action toggle, Func<bool, string> tip, Control? extra = null)
    {
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var state = new TextBlock { FontSize = 10, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var power = new Button { Classes = { "chip" }, Height = 24, MinHeight = 24, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(4),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { glyph, state } } };
        power.Click += (_, _) => toggle();
        var iconText = FxIcon.Create(icon, 12);
        iconText.VerticalAlignment = VerticalAlignment.Center; iconText.Margin = new Thickness(8, 0, 0, 0);
        var name = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 4, 0) };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 32, Margin = new Thickness(6, 0) };
        head.Children.Add(power);
        Grid.SetColumn(iconText, 1); head.Children.Add(iconText);
        Grid.SetColumn(name, 2); head.Children.Add(name);
        if (extra != null) { extra.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(extra, 3); head.Children.Add(extra); }
        var body = new StackPanel { Margin = new Thickness(8) };
        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = head, BorderBrush = Palette.Alpha(Palette.LineColor, 0.7), BorderThickness = new Thickness(0, 0, 0, 1) });
        stack.Children.Add(body);
        var box = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = stack };
        box.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < FxNarrowW;
            state.IsVisible = iconText.IsVisible = !narrow;
        };
        mixerRefresh.Add(() =>
        {
            bool lit = on();
            glyph.Foreground = state.Foreground = lit ? Palette.App : Palette.Faint;
            state.Text = lit ? "ON" : "OFF";
            power.Background = lit ? Palette.Accent : Brushes.Transparent;
            power.BorderBrush = lit ? Palette.Accent : Palette.Line;
            ToolTip.SetTip(power, tip(lit));
            FxIcon.SetColor(iconText, lit ? Palette.Accent : Palette.Faint);
            name.Foreground = lit ? Palette.Fg : Palette.Faint;
            body.Opacity = lit ? 1 : 0.4;
            box.Background = Palette.Alpha(Palette.AppColor, lit ? 0.6 : 0.3);
            box.BorderBrush = lit ? Palette.Line : Palette.Alpha(Palette.LineColor, 0.6);
        });
        host.Children.Add(box);
        return (box, body);
    }

    Control FxKnob((Border Box, StackPanel Body) panel, string label, double reset, Func<double> get, Action<double> set, Func<string> tip)
    {
        var knob = new Knob(34) { Min = 0, Max = 1, ResetTo = reset };
        knob.Changed += set;
        mixerRefresh.Add(() => { knob.Value = get(); knob.Tip = tip(); });
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

    /// <summary>Port of ToggleBtn: M lights in the accent, S in white.</summary>
    Button StripToggle(string text, string tip, double size, Func<bool> on, bool solo, Action click)
    {
        var b = new Button { Classes = { "chip" }, Width = size, Height = size, MinHeight = size, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = text, Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.Bold } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        mixerRefresh.Add(() =>
        {
            bool lit = on();
            b.Background = lit ? (solo ? Palette.Fg : Palette.Accent) : Brushes.Transparent;
            b.BorderBrush = lit ? (solo ? Palette.Fg : Palette.Accent) : Palette.Line;
            ((TextBlock)b.Content!).Foreground = lit ? Palette.App : Palette.Faint;
        });
        return b;
    }

    /// <summary>MiniKnob: a 22 px knob, its value and a label under it.</summary>
    Control MiniKnob(string label, double min, double max, double reset, Func<double> get, Func<string> readout, Action<double> set, Func<string> tip)
    {
        var knob = new Knob(22) { Min = min, Max = max, ResetTo = reset, HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += set;
        var value = new TextBlock { Classes = { "mono" }, FontSize = 9, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center };
        mixerRefresh.Add(() => { knob.Value = get(); knob.Tip = tip(); value.Text = readout(); });
        var col = new StackPanel();
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
        mixerRefresh.Add(() => { knob.Value = get(); value.Text = readout(); });
        var col = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        col.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        col.Children.Add(knob); col.Children.Add(value);
        return col;
    }

    Control BusKnob(bool wide, string label, double reset, Func<double> get, Func<string> readout, Action<double> set, string tip) =>
        wide ? BigKnob(label, reset, get, readout, set, tip) : MiniKnob(label, 0, 1, reset, get, readout, set, () => tip);

    // --- returns and master ------------------------------------------------------------------------------

    Control ReturnBox(bool wide, string title, string icon, SelectButton presets, Func<bool> muted, Action toggleMute, Control? extraRow,
        IEnumerable<Control> knobs, Func<double> gain, Action<double> setGain, out MixerMeter meter)
    {
        var box = StripCard(wide ? ReturnWideW : ReturnCompactW);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 28 };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var iconMark = FxIcon.Create(icon, 12);
        FxIcon.SetColor(iconMark, Palette.Accent); iconMark.VerticalAlignment = VerticalAlignment.Center;
        name.Children.Add(iconMark);
        name.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(name);
        presets.Margin = new Thickness(6, 0); presets.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(presets, 1); head.Children.Add(presets);
        var mute = StripToggle("M", "Mute del retorno", 24, muted, false, toggleMute);
        mute.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(mute, 2); head.Children.Add(mute);

        var fader = new Fader();
        fader.Changed += setGain;
        var db = new TextBlock { Classes = { "mono" }, FontSize = wide ? 11 : 10, Foreground = Palette.Dim };
        mixerRefresh.Add(() => { fader.Value = gain(); db.Text = $"{Fader.Db(gain())} dB"; });
        meter = new MixerMeter();
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        faders.Children.Add(fader); faders.Children.Add(meter);

        var controls = new StackPanel { Spacing = 6 };
        if (extraRow != null) controls.Children.Add(extraRow);
        var grid = new UniformGrid { Columns = 3 };
        foreach (var k in knobs) grid.Children.Add(k);
        controls.Children.Add(grid);
        box.Child = StripLayout(wide, head, new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            faders, db);
        return box;
    }

    Control ReverbReturn(bool wide)
    {
        var defaults = new ReverbSettings();
        var presets = StripSelect([]);
        ToolTip.SetTip(presets, "Puntos de partida para la reverb");
        presets.Selected += i =>
        {
            int k = i - (ReverbBus.PresetOf(ToParams(Reverb)) == "" ? 1 : 0);
            if (k < 0) return;
            string label = ReverbBus.Presets[k].Label;
            BusEdit(s =>
            {
                var p = ReverbBus.ApplyPreset(ToParams(s.Reverb), label);
                s.Reverb.Decay = p.Decay; s.Reverb.PreDelayMs = p.PreDelayMs; s.Reverb.Tone = p.Tone; s.Reverb.LowCut = p.LowCut; s.Reverb.Width = p.Width;
            }, "reverb:preset");
        };
        mixerRefresh.Add(() =>
        {
            string p = ReverbBus.PresetOf(ToParams(Reverb));
            presets.SetItems((p == "" ? new[] { "Personalizada" } : []).Concat(ReverbBus.Presets.Select(x => x.Label)).ToArray(),
                (p == "" ? new string?[] { null } : []).Concat(ReverbBus.Presets.Select(x => (string?)x.Hint)).ToArray());
            presets.SelectedIndex = p == "" ? 0 : Array.FindIndex(ReverbBus.Presets, x => x.Label == p);
        });
        Control[] knobs =
        [
            BusKnob(wide, "Decay", DecayMap.To(defaults.Decay), () => DecayMap.To(Reverb.Decay), () => $"{Reverb.Decay:0.0} s",
                p => BusEdit(s => s.Reverb.Decay = DecayMap.From(p), "reverb:decay"), "Duración de la cola (RT60)"),
            BusKnob(wide, "Pre", defaults.PreDelayMs / 200, () => Reverb.PreDelayMs / 200, () => $"{Math.Round(Reverb.PreDelayMs):0} ms",
                p => BusEdit(s => s.Reverb.PreDelayMs = Math.Round(p * 200), "reverb:preDelayMs"), "Pre-delay: separación entre el sonido directo y la reverb"),
            BusKnob(wide, "Tono", ToneMap.To(defaults.Tone), () => ToneMap.To(Reverb.Tone), () => FmtHz(Reverb.Tone),
                p => BusEdit(s => s.Reverb.Tone = Math.Round(ToneMap.From(p)), "reverb:tone"), "Brillo de la cola (más bajo = más oscura)"),
            BusKnob(wide, "Graves", LowCutMap.To(defaults.LowCut), () => LowCutMap.To(Reverb.LowCut), () => FmtHz(Reverb.LowCut),
                p => BusEdit(s => s.Reverb.LowCut = Math.Round(LowCutMap.From(p)), "reverb:lowCut"), "Corte de graves a la entrada de la reverb"),
            BusKnob(wide, "Ancho", defaults.Width, () => Reverb.Width, () => $"{Reverb.Width * 100:0}%",
                v => BusEdit(s => s.Reverb.Width = v, "reverb:width"), "Ancho estéreo de la cola"),
        ];
        var box = ReturnBox(wide, "Reverb", FxIcon.Reverb, presets, () => Reverb.Muted, () => BusEdit(s => s.Reverb.Muted = !s.Reverb.Muted, "reverb:muted"), null,
            knobs, () => Reverb.ReturnGain, v => BusEdit(s => s.Reverb.ReturnGain = v, "reverb:returnGain"), out var meter);
        reverbMeter = meter;
        return box;
    }

    Control DelayReturn(bool wide)
    {
        var defaults = new DelaySettings();
        var presets = StripSelect([]);
        ToolTip.SetTip(presets, "Puntos de partida para el delay");
        presets.Selected += i =>
        {
            int k = i - (DelayBus.PresetOf(ToParams(Delay)) == "" ? 1 : 0);
            if (k < 0) return;
            string label = DelayBus.Presets[k].Label;
            BusEdit(s =>
            {
                var p = DelayBus.ApplyPreset(ToParams(s.Delay), label);
                var d = s.Delay;
                d.Mode = DelayParams.ModeToString(p.Mode); d.Sync = p.Sync; d.Division = p.Division; d.TimeMs = p.TimeMs;
                d.Feedback = p.Feedback; d.Tone = p.Tone; d.LowCut = p.LowCut; d.Width = p.Width; d.Duck = p.Duck;
            }, "delay:preset");
        };
        mixerRefresh.Add(() =>
        {
            string p = DelayBus.PresetOf(ToParams(Delay));
            presets.SetItems((p == "" ? new[] { "Personalizado" } : []).Concat(DelayBus.Presets.Select(x => x.Label)).ToArray(),
                (p == "" ? new string?[] { null } : []).Concat(DelayBus.Presets.Select(x => (string?)x.Hint)).ToArray());
            presets.SelectedIndex = p == "" ? 0 : Array.FindIndex(DelayBus.Presets, x => x.Label == p);
        });

        // mode + note value (or "Libre (ms)"): side by side in a compact return, one under the other in a wide one,
        // where the fader takes part of the width
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(wide ? "*" : "Auto,*"), RowDefinitions = new RowDefinitions(wide ? "Auto,Auto" : "Auto") };
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label, tip) in new[]
        {
            ("stereo", wide ? "Estéreo" : "Est.", "Estéreo: cada lado repite sobre sí mismo (el derecho un poco después)"),
            ("pingpong", wide ? "Ping-pong" : "P-P", "Ping-pong: las repeticiones rebotan entre izquierda y derecha"),
        })
        {
            var b = new ToggleButton { Classes = { "seg", "pick" }, Content = label, FontSize = 10, Height = 22, MinHeight = 22, Padding = new Thickness(6, 0) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => BusEdit(s => s.Delay.Mode = id, "delay:mode");
            mixerRefresh.Add(() => b.IsChecked = Delay.Mode == id);
            modes.Children.Add(b);
        }
        row.Children.Add(new Border { Classes = { "seg" }, Child = modes, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left });
        var division = StripSelect(DelayBus.Divisions.Select(d => d.Label).Append("Libre (ms)").ToArray());
        division.Margin = wide ? new Thickness(0, 6, 0, 0) : new Thickness(4, 0, 0, 0);
        division.Selected += i =>
        {
            if (i == DelayBus.Divisions.Length)
                BusEdit(s => { s.Delay.TimeMs = Math.Round(DelayBus.DelayTimeSec(ToParams(s.Delay), s.Bpm) * 1000); s.Delay.Sync = false; }, "delay:sync,timeMs");
            else
            {
                string id = DelayBus.Divisions[i].Id;
                BusEdit(s => { s.Delay.Sync = true; s.Delay.Division = id; }, "delay:division,sync");
            }
        };
        mixerRefresh.Add(() =>
        {
            var d = Delay;
            division.SelectedIndex = d.Sync ? Math.Max(0, Array.FindIndex(DelayBus.Divisions, x => x.Id == d.Division)) : DelayBus.Divisions.Length;
            double bpm = store?.Project.Settings.Bpm ?? 110;
            ToolTip.SetTip(division, $"Tiempo de las repeticiones: {Math.Round(DelayBus.DelayTimeSec(ToParams(d), bpm) * 1000):0} ms{(d.Sync ? $" a {bpm:0.##} BPM" : "")}");
        });
        if (wide) Grid.SetRow(division, 1); else Grid.SetColumn(division, 1);
        row.Children.Add(division);

        var time = BusKnob(wide, "Tiempo", DelayMsMap.To(defaults.TimeMs), () => DelayMsMap.To(Delay.TimeMs), () => $"{Math.Round(Delay.TimeMs):0} ms",
            p => BusEdit(s => s.Delay.TimeMs = Math.Round(DelayMsMap.From(p)), "delay:timeMs"), "Tiempo entre repeticiones");
        mixerRefresh.Add(() => time.IsVisible = !Delay.Sync);
        Control[] knobs =
        [
            time,
            BusKnob(wide, "Repet.", defaults.Feedback / 0.9, () => Delay.Feedback / 0.9, () => $"{Delay.Feedback * 100:0}%",
                p => BusEdit(s => s.Delay.Feedback = Math.Round(p * 90) / 100, "delay:feedback"), "Feedback: cuántas repeticiones se escuchan"),
            BusKnob(wide, "Tono", ToneMap.To(defaults.Tone), () => ToneMap.To(Delay.Tone), () => FmtHz(Delay.Tone),
                p => BusEdit(s => s.Delay.Tone = Math.Round(ToneMap.From(p)), "delay:tone"), "Brillo de las repeticiones (cada una sale más oscura que la anterior)"),
            BusKnob(wide, "Graves", LowCutMap.To(defaults.LowCut), () => LowCutMap.To(Delay.LowCut), () => FmtHz(Delay.LowCut),
                p => BusEdit(s => s.Delay.LowCut = Math.Round(LowCutMap.From(p)), "delay:lowCut"), "Corte de graves de las repeticiones, para que no embarren"),
            BusKnob(wide, "Ancho", defaults.Width, () => Delay.Width, () => $"{Delay.Width * 100:0}%",
                v => BusEdit(s => s.Delay.Width = v, "delay:width"), "Ancho estéreo de las repeticiones"),
            BusKnob(wide, "Ducking", defaults.Duck, () => Delay.Duck, () => $"{Delay.Duck * 100:0}%",
                v => BusEdit(s => s.Delay.Duck = v, "delay:duck"), "Baja las repeticiones mientras tocás y las deja sonar en los silencios"),
        ];
        var box = ReturnBox(wide, "Delay", FxIcon.Delay, presets, () => Delay.Muted, () => BusEdit(s => s.Delay.Muted = !s.Delay.Muted, "delay:muted"), row,
            knobs, () => Delay.ReturnGain, v => BusEdit(s => s.Delay.ReturnGain = v, "delay:returnGain"), out var meter);
        delayMeter = meter;
        return box;
    }

    Control Master(bool wide)
    {
        var box = StripCard(MasterW);
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(new TextBlock { Text = "Master", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
        limLight = new TextBlock { Text = "LIM", Classes = { "mono" }, FontSize = 9, FontWeight = FontWeight.Bold, LetterSpacing = 0.8, Foreground = Palette.Accent, Opacity = 0.25,
            HorizontalAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(limLight, "Limitador del master (-0.3 dB): se ilumina cuando está actuando");
        head.Children.Add(limLight);
        masterMeter = new MixerMeter(wide: true);
        var output = new TextBlock { Text = "Salida", FontSize = 10, Foreground = Palette.Faint };
        box.Child = StripLayout(wide, head, null, masterMeter, output);
        return box;
    }
}
