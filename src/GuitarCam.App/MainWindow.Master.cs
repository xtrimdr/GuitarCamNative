using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GuitarCam.App.Controls;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The Mastering view (native only, asked by the user): the third view beside Timeline and Mixer, in the mixer's place
/// (<see cref="MixerHost"/>, so it keeps the mixer's height and its spot in solo audio), and what the master strip's
/// button opens. It lays the chain out left to right, a panel per stage in the order the sound goes, so it reads at a
/// glance: Limpieza (low cut, mud) → Multibanda (three bands, a single "Cuánto" knob each) → Color (lows, presence,
/// air, tilt) → Cinta → Ancho (width, lows in mono) → Soft clip → Limitador (Empuje, Techo, Release) → Volumen (LUFS,
/// peak and how far from −14), as in <see cref="MasterChain"/> and <see cref="MixGraphBus"/>. Every stage has its ON.
/// The master's ON only reaches up to the soft clip, so those stages sit in a box under it (dimmed while it is off)
/// and the limiter and the meter stand outside it as Salida, which works either way (asked by the user).
/// The view is as tall as the chain and does not resize: there is nothing more to show. Nothing is measured: each band follows its own level as it plays (<see cref="MasterBandComp"/>),
/// so a knob is heard the moment it moves.
/// </summary>
public partial class MainWindow
{
    /// <summary>Width of the reduction bars and the reduction they show full (dB).</summary>
    const double GrBarW = 72, GrBarFullDb = 9;
    /// <summary>The view's knobs, bigger than the strips' (asked by the user), and the column each one takes.</summary>
    const double MasterKnobSize = 44, MasterKnobColW = 64;
    /// <summary>Room left under the chain for the horizontal scroll bar of a narrow window.</summary>
    const double MasteringScrollRoom = 14;

    readonly List<Action> masterRefresh = [];
    readonly Border?[] masterGrBars = new Border?[MasterBands.Count];
    readonly TextBlock?[] masterGrText = new TextBlock?[MasterBands.Count];
    readonly double[] masterGrShow = new double[MasterBands.Count];
    readonly double[] masterGrShown = new double[MasterBands.Count];
    readonly ScrollViewer masteringScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsVisible = false };
    Border? masterLimBar;
    TextBlock? masterLimText, masterStatus;
    /// <summary>The limiter's reduction: as <see cref="TickMixer"/> took it (dB, ≤ 0), as shown and as written.</summary>
    double masterLimDb, masterLimShow, masterLimShown = double.NaN;
    Border? masterClipBar;
    TextBlock? masterClipText, masterShortText, masterIntText, masterPeakText, masterToGoText;
    double masterClipShow, masterClipShown = double.NaN;
    /// <summary>The master's output peak this pass (linear, from <see cref="TickMixer"/>) and what the meter last wrote.</summary>
    float masterOutPeak;
    double masterShortShown = double.NaN, masterIntShown = double.NaN, masterPeakShown = double.NaN;
    bool masteringView;
    /// <summary>What social platforms normalise to; the Volumen panel tells how far the mix is.</summary>
    const double SocialLufs = -14;

    MasterSettings MasterSet => store?.Project.Settings.Master ?? new();

    static readonly (Func<double, double> To, Func<double, double> From) MasterCutMap = LogMap(MasterEq.LowCutMin, MasterEq.LowCutMax),
        LowXMap = LogMap(MasterBands.LowXMin, MasterBands.LowXMax), HighXMap = LogMap(MasterBands.HighXMin, MasterBands.HighXMax),
        LimReleaseMap = LogMap(MasterLimiter.ReleaseMinMs, MasterLimiter.ReleaseMaxMs),
        MonoBassMap = LogMap(40, StereoWidth.MonoBassMaxHz);

    static string FmtDb(double db) => $"{db:+0.0;-0.0;0.0} dB";
    /// <summary>A reduction (dB, ≥ 0) as the meters show it.</summary>
    static string FmtGr(double db) => db <= 0 ? $"{0.0:0.0} dB" : $"{-db:0.0} dB";

    void InitMastering()
    {
        masteringScroll.Padding = new Thickness(14, 10);
        MasteringViewButton.Click += (_, _) => ShowBottomView(mastering: true);
    }

    /// <summary>Mixer or Mastering: both live in the mixer's place, with different content.</summary>
    void ShowBottomView(bool mastering)
    {
        masteringView = mastering;
        SetMixerView(true);
        ApplyMasteringView();
    }

    /// <summary>Shows the mixer's or the mastering's content in <see cref="MixerHost"/> and lights the right button.</summary>
    void ApplyMasteringView()
    {
        if (!mixerView) masteringView = false; // back on the timeline, the next Mixer is the mixer
        bool m = masteringView && mixerView;
        masteringScroll.IsVisible = m;
        mixerScroll.IsVisible = !m;
        if (mixerHeader != null) mixerHeader.IsVisible = !m;
        MixerViewButton.Classes.Set("on", mixerView && !m);
        MasteringViewButton.Classes.Set("on", m);
        BottomSplitter.IsEnabled = AudioSplitter.IsEnabled = !m;
        if (m)
        {
            BuildMastering();
            FitMasteringHeight();
        }
        else
        {
            masteringScroll.Content = null;
            if (mixerView) SetBottomRowHeight(mixerH);
            RefreshMixer();
        }
    }

    /// <summary>The view takes just the chain's height (plus its scroll bar's room): taller there is nothing to show.</summary>
    void FitMasteringHeight()
    {
        if (masteringScroll.Content is not Control body) return;
        body.Measure(Size.Infinity);
        var pad = masteringScroll.Padding;
        SetBottomRowHeight(Math.Ceiling(body.DesiredSize.Height + pad.Top + pad.Bottom + MasteringScrollRoom + (audioMode ? 0 : miniTimeline.Bounds.Height)));
    }

    /// <summary>The height of the place the mixer and the mastering share: the bottom row, or the row under the timeline in solo audio.</summary>
    void SetBottomRowHeight(double h)
    {
        if (audioMode) AudioArea.RowDefinitions[2].Height = new GridLength(Math.Max(0, h));
        else
        {
            var row = Root.RowDefinitions[3];
            row.MinHeight = Math.Min(MixerMinH, h);
            row.Height = new GridLength(h);
        }
    }

    void MasterEdit(Action<MasterSettings> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project, "master:" + key);
        var m = store.Project.Settings.Master;
        change(m);
        engine.ApplyMaster(m);
        MarkDirty();
        UpdateUndoButtons();
        RefreshMasterPanel();
        RefreshMixer();
    }

    /// <summary>A new project or the same one again: the view rebuilds on its settings.</summary>
    void ResetMaster()
    {
        if (masteringView && mixerView) BuildMastering();
    }

    /// <summary>The master strip's title: it opens the Mastering view and lights in the accent while the master is on.</summary>
    Button MasterButton()
    {
        var b = new Button
        {
            Classes = { "chip" }, Focusable = false, Height = 22, MinHeight = 22, Padding = new Thickness(8, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Content = new TextBlock { Text = Loc.T("mix.master"), FontSize = 11, FontWeight = FontWeight.SemiBold },
        };
        ToolTip.SetTip(b, Loc.T("master.open.tip"));
        b.Click += (_, _) => ShowBottomView(mastering: true);
        mx.Refresh.Add(() => b.Classes.Set("on", MasterSet.On));
        return b;
    }

    void BuildMastering()
    {
        masterRefresh.Clear();
        masteringScroll.Content = MasterBody();
        RefreshMasterPanel();
    }

    /// <summary>
    /// Two groups left to right, in the order the sound goes: the master's box (title, what it is doing, ON, and a
    /// panel per stage from Limpieza to Soft clip) and, outside it, Salida (Limitador → Volumen), which the master's ON
    /// does not touch. Arrows between the panels.
    /// </summary>
    Control MasterBody()
    {
        bool editable = store is { ReadOnly: false };
        var suite = new StackPanel { Spacing = 12 };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("master.title").ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center });
        masterStatus = new TextBlock { FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(masterStatus, 1); head.Children.Add(masterStatus);
        var on = OnChip(() => MasterSet.On, () => MasterEdit(m => m.On = !m.On, "on"), Loc.T("master.on.tip"));
        on.IsEnabled = editable;
        on.Height = on.MinHeight = 30;
        Grid.SetColumn(on, 2); head.Children.Add(on);
        suite.Children.Add(head);

        var chain = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        static TextBlock Arrow() => new() { Classes = { "icon" }, Text = "", FontSize = 14, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
        void Add(Control panel)
        {
            if (chain.Children.Count > 0 && chain.Children[^1] is not TextBlock) chain.Children.Add(Arrow());
            chain.Children.Add(panel);
        }

        // 1. cleanup before the multiband
        var clean = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        clean.Children.Add(MasterKnob(Loc.T("master.eq.lowcut"), 0, 1, 0, () => MasterSet.LowCutHz <= 0 ? 0 : MasterCutMap.To(MasterSet.LowCutHz),
            () => MasterSet.LowCutHz <= 0 ? Loc.T("master.off") : $"{FmtHz(MasterSet.LowCutHz)} Hz",
            v => MasterEdit(m => m.LowCutHz = v < 0.02 ? 0 : Math.Round(MasterCutMap.From(v)), "lowCut"), Loc.T("master.eq.lowcut.tip")));
        clean.Children.Add(EqKnob(Loc.T("master.eq.mud"), MasterEq.GainMax, () => MasterSet.MudDb, (m, v) => m.MudDb = v, "mud", Loc.T("master.eq.mud.tip")));
        Add(StagePanel(FxIcon.Eq, Loc.T("master.stage.clean"), Loc.T("master.stage.clean.tip"), () => MasterSet.EqOn,
            () => MasterEdit(m => m.EqOn = !m.EqOn, "eqOn"), clean));

        // 2. multiband
        var bands = new StackPanel { Spacing = 10 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (int i = 0; i < MasterBands.Count; i++) row.Children.Add(BandColumn(i));
        bands.Children.Add(row);
        var cross = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, HorizontalAlignment = HorizontalAlignment.Center };
        cross.Children.Add(MasterKnob(Loc.T("master.cross.low"), 0, 1, LowXMap.To(MasterBands.DefaultLowXHz), () => LowXMap.To(MasterSet.LowXHz),
            () => $"{FmtHz(MasterSet.LowXHz)} Hz", v => MasterEdit(m => m.LowXHz = Math.Round(LowXMap.From(v)), "lowX"), Loc.T("master.cross.low.tip"), small: true));
        cross.Children.Add(MasterKnob(Loc.T("master.cross.high"), 0, 1, HighXMap.To(MasterBands.DefaultHighXHz), () => HighXMap.To(MasterSet.HighXHz),
            () => $"{FmtHz(MasterSet.HighXHz)} Hz", v => MasterEdit(m => m.HighXHz = Math.Round(HighXMap.From(v) / 100) * 100, "highX"), Loc.T("master.cross.high.tip"), small: true));
        bands.Children.Add(cross);
        Add(StagePanel(FxIcon.Comp, Loc.T("master.bands"), Loc.T("master.bands.on.tip"), () => MasterSet.BandsOn,
            () => MasterEdit(m => m.BandsOn = !m.BandsOn, "bandsOn"), bands));

        // 3. colour after it
        var color = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        color.Children.Add(EqKnob(Loc.T("master.eq.lows"), MasterEq.GainMax, () => MasterSet.LowsDb, (m, v) => m.LowsDb = v, "lows", Loc.T("master.eq.lows.tip")));
        color.Children.Add(EqKnob(Loc.T("master.eq.presence"), MasterEq.GainMax, () => MasterSet.PresenceDb, (m, v) => m.PresenceDb = v, "presence", Loc.T("master.eq.presence.tip")));
        color.Children.Add(EqKnob(Loc.T("master.eq.air"), MasterEq.GainMax, () => MasterSet.AirDb, (m, v) => m.AirDb = v, "air", Loc.T("master.eq.air.tip")));
        color.Children.Add(EqKnob(Loc.T("master.eq.tilt"), MasterEq.TiltMax, () => MasterSet.TiltDb, (m, v) => m.TiltDb = v, "tilt", Loc.T("master.eq.tilt.tip")));
        Add(StagePanel(FxIcon.Eq, Loc.T("master.stage.color"), Loc.T("master.stage.color.tip"), () => MasterSet.ColorOn,
            () => MasterEdit(m => m.ColorOn = !m.ColorOn, "colorOn"), color));

        // 4. tape
        Add(StagePanel(null, Loc.T("master.stage.tape"), Loc.T("master.stage.tape.tip"), () => MasterSet.TapeOn,
            () => MasterEdit(m => m.TapeOn = !m.TapeOn, "tapeOn"),
            MasterKnob(Loc.T("master.tape.amount"), 0, 1, 0, () => MasterSet.TapeAmount,
                () => MasterSet.TapeAmount <= 0 ? Loc.T("master.off") : $"{MasterSet.TapeAmount * 100:0}%",
                v => MasterEdit(m => m.TapeAmount = Math.Round(v * 100) / 100, "tape"), Loc.T("master.tape.amount.tip"))));

        // 5. width
        var wide = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        wide.Children.Add(MasterKnob(Loc.T("master.width"), 0, StereoWidth.MaxWidth, 1, () => MasterSet.Width, () => $"{MasterSet.Width * 100:0}%",
            v => MasterEdit(m => m.Width = Math.Round(v * 20) / 20, "width"), Loc.T("master.width.tip")));
        wide.Children.Add(MasterKnob(Loc.T("master.monobass"), 0, 1, 0, () => MasterSet.MonoBassHz <= 0 ? 0 : MonoBassMap.To(MasterSet.MonoBassHz),
            () => MasterSet.MonoBassHz <= 0 ? Loc.T("master.off") : $"{FmtHz(MasterSet.MonoBassHz)} Hz",
            v => MasterEdit(m => m.MonoBassHz = v < 0.02 ? 0 : Math.Round(MonoBassMap.From(v)), "monoBass"), Loc.T("master.monobass.tip")));
        Add(StagePanel(null, Loc.T("master.stage.width"), Loc.T("master.stage.width.tip"), () => MasterSet.WidthOn,
            () => MasterEdit(m => m.WidthOn = !m.WidthOn, "widthOn"), wide));

        // 6. soft clip, into the limiter
        var clip = new StackPanel { Spacing = 10 };
        clip.Children.Add(MasterKnob(Loc.T("master.clip.amount"), 0, SoftClipper.MaxDb, 0, () => MasterSet.ClipDb,
            () => MasterSet.ClipDb <= 0 ? Loc.T("master.off") : $"{MasterSet.ClipDb:0.0} dB",
            v => MasterEdit(m => m.ClipDb = Math.Round(v * 10) / 10, "clip"), Loc.T("master.clip.amount.tip")));
        clip.Children.Add(GrMeter(out var clipBar, out var clipText, Loc.T("master.clip.gr.tip")));
        masterClipBar = clipBar;
        masterClipText = clipText;
        masterClipShown = double.NaN;
        Add(StagePanel(null, Loc.T("master.stage.clip"), Loc.T("master.stage.clip.tip"), () => MasterSet.ClipOn,
            () => MasterEdit(m => m.ClipOn = !m.ClipOn, "clipOn"), clip));
        suite.Children.Add(chain);
        var suiteChain = chain;
        masterRefresh.Add(() => suiteChain.Opacity = MasterSet.On ? 1 : 0.45);

        // what the master's ON does not reach: the limiter and the meter, after the box
        var outHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, MinHeight = 30 };
        outHead.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("master.out").ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center });
        outHead.Children.Add(new TextBlock { FontSize = 11, Foreground = Palette.Faint, Text = Loc.T("master.out.note"), VerticalAlignment = VerticalAlignment.Center });
        chain = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        chain.Children.Add(Arrow());

        // 7. the limiter, with its own ON apart from the master's
        var lim = new StackPanel { Spacing = 10 };
        var limKnobs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        limKnobs.Children.Add(MasterKnob(Loc.T("master.lim.drive"), 0, MasterParams.DriveMaxDb, 0, () => MasterSet.LimDriveDb, () => FmtDb(MasterSet.LimDriveDb),
            v => MasterEdit(m => m.LimDriveDb = Math.Round(v * 2) / 2, "limDrive"), Loc.T("master.lim.drive.tip")));
        limKnobs.Children.Add(MasterKnob(Loc.T("master.lim.ceiling"), MasterLimiter.CeilingMinDb, 0, MasterLimiter.CeilingDb, () => MasterSet.LimCeilingDb,
            () => $"{MasterSet.LimCeilingDb:0.0} dBTP", v => MasterEdit(m => m.LimCeilingDb = Math.Round(v * 10) / 10, "limCeiling"), Loc.T("master.lim.ceiling.tip")));
        limKnobs.Children.Add(MasterKnob(Loc.T("master.lim.release"), 0, 1, LimReleaseMap.To(MasterLimiter.ReleaseMs), () => LimReleaseMap.To(MasterSet.LimReleaseMs),
            () => $"{MasterSet.LimReleaseMs:0} ms", v => MasterEdit(m => m.LimReleaseMs = Math.Round(LimReleaseMap.From(v)), "limRelease"), Loc.T("master.lim.release.tip")));
        lim.Children.Add(limKnobs);
        lim.Children.Add(GrMeter(out var limBar, out var limText, Loc.T("master.lim.gr.tip")));
        masterLimBar = limBar;
        masterLimText = limText;
        masterLimShown = double.NaN;
        Add(StagePanel(null, Loc.T("master.stage.lim"), Loc.T("master.stage.lim.tip"), () => MasterSet.LimOn,
            () => MasterEdit(m => m.LimOn = !m.LimOn, "limOn"), lim));

        // 8. what comes out: a meter, nothing to turn on
        var meter = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        TextBlock Row(int r, string label, string tip)
        {
            var name = new TextBlock { Text = label, FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 14, 3) };
            ToolTip.SetTip(name, tip);
            Grid.SetRow(name, r); meter.Children.Add(name);
            var value = new TextBlock { Classes = { "mono" }, FontSize = 14, Foreground = Palette.Fg, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = "—" };
            Grid.SetRow(value, r); Grid.SetColumn(value, 1); meter.Children.Add(value);
            return value;
        }
        masterShortText = Row(0, Loc.T("master.meter.short"), Loc.T("master.meter.short.tip"));
        masterIntText = Row(1, Loc.T("master.meter.int"), Loc.T("master.meter.int.tip"));
        masterPeakText = Row(2, Loc.T("master.meter.peak"), Loc.T("master.meter.peak.tip"));
        masterToGoText = Row(3, Loc.T("master.meter.togo"), Loc.T("master.meter.togo.tip"));
        masterShortShown = masterIntShown = masterPeakShown = double.NaN;
        Add(MeterPanel(Loc.T("master.stage.meter"), Loc.T("master.stage.meter.tip"), meter));

        // the box's padding and border, so the Salida panels line up with the stages
        const double BoxPad = 10;
        var box = new Border
        {
            Padding = new Thickness(BoxPad), BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = suite,
        };
        var output = new StackPanel { Spacing = 12, Margin = new Thickness(0, BoxPad + 1, 0, 0) };
        output.Children.Add(outHead);
        output.Children.Add(chain);
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(box);
        body.Children.Add(output);
        return body;
    }

    /// <summary>One stage of the chain: a darker card with its icon, name and ON (its content dimmed while off).</summary>
    Border StagePanel(string? icon, string title, string tip, Func<bool> on, Action toggle, Control content)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 10), MinHeight = 26 };
        if (icon != null)
        {
            var mark = FxIcon.Create(icon, 12);
            FxIcon.SetColor(mark, Palette.Accent);
            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(mark);
        }
        var name = new TextBlock { Classes = { "label" }, Text = title.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(name, tip);
        Grid.SetColumn(name, 1); head.Children.Add(name);
        var chip = OnChip(on, toggle, tip);
        chip.IsEnabled = store is { ReadOnly: false };
        chip.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(chip, 2); head.Children.Add(chip);
        masterRefresh.Add(() => content.Opacity = on() ? 1 : 0.45);
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(content);
        return new Border
        {
            Padding = new Thickness(12, 10), Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = stack, VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    /// <summary>The last panel: like a stage, without ON (it only reads).</summary>
    static Border MeterPanel(string title, string tip, Control content)
    {
        var name = new TextBlock { Classes = { "label" }, Text = title.ToUpperInvariant(), MinHeight = 26, Margin = new Thickness(0, 0, 0, 10) };
        ToolTip.SetTip(name, tip);
        var stack = new StackPanel();
        stack.Children.Add(name);
        stack.Children.Add(content);
        return new Border
        {
            Padding = new Thickness(12, 10), Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = stack, VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    /// <summary>An ON chip, lit while <paramref name="on"/>.</summary>
    Button OnChip(Func<bool> on, Action click, string tip)
    {
        var b = new Button { Classes = { "chip" }, Focusable = false, Content = "ON", Height = 26, MinHeight = 26, Padding = new Thickness(10, 0), FontSize = 11 };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        masterRefresh.Add(() => b.Classes.Set("on", on()));
        return b;
    }

    Control EqKnob(string label, double max, Func<double> get, Action<MasterSettings, double> set, string key, string tip) =>
        MasterKnob(label, -max, max, 0, get, () => FmtDb(get()), v => MasterEdit(m => set(m, Math.Round(v * 2) / 2), key), tip);

    /// <summary>A knob of the view: label on top, the knob (<see cref="MasterKnobSize"/>, smaller for the crossovers), the value under it.</summary>
    Control MasterKnob(string label, double min, double max, double reset, Func<double> get, Func<string> readout, Action<double> set, string tip, bool small = false)
    {
        var knob = new Knob(small ? 30 : MasterKnobSize, tip) { Min = min, Max = max, ResetTo = reset, HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += set;
        knob.IsEnabled = store is { ReadOnly: false };
        var value = new TextBlock { Classes = { "mono" }, FontSize = 11, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center };
        masterRefresh.Add(() => { knob.Value = get(); value.Text = readout(); });
        var col = new StackPanel { Spacing = 5, MinWidth = small ? 72 : MasterKnobColW };
        col.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        col.Children.Add(knob);
        col.Children.Add(value);
        return col;
    }

    /// <summary>A reduction bar with its value under it.</summary>
    static StackPanel GrMeter(out Border bar, out TextBlock text, string tip)
    {
        var col = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        var track = new Border { Width = GrBarW, Height = 4, CornerRadius = new CornerRadius(2), Background = Palette.Hover, HorizontalAlignment = HorizontalAlignment.Center };
        bar = new Border { Width = 0, Height = 4, CornerRadius = new CornerRadius(2), Background = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = bar;
        ToolTip.SetTip(track, tip);
        col.Children.Add(track);
        text = new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center, Text = FmtGr(0) };
        col.Children.Add(text);
        return col;
    }

    /// <summary>A band: its "Cuánto" knob and the live reduction.</summary>
    Control BandColumn(int i)
    {
        string id = MasterBands.All[i].Id;
        var col = new StackPanel { Spacing = 8, MinWidth = MasterKnobColW + 8 };
        col.Children.Add(MasterKnob(Loc.T("master.band." + id), 0, 1, MasterBands.DefaultAmount, () => MasterSet.Bands[i].Amount,
            () => MasterSet.Bands[i].Amount <= 0 ? Loc.T("master.off") : $"{MasterSet.Bands[i].Amount * 100:0}%",
            v => MasterEdit(m => m.Bands[i].Amount = Math.Round(v * 100) / 100, "band" + i), Loc.T("master.band." + id + ".tip")));
        col.Children.Add(GrMeter(out var bar, out var text, Loc.T("master.gr.tip")));
        masterGrBars[i] = bar;
        masterGrText[i] = text;
        masterGrShown[i] = double.NaN;
        return col;
    }

    void RefreshMasterPanel()
    {
        if (!masteringScroll.IsVisible) return;
        bool was = syncing;
        syncing = true;
        try { foreach (var r in masterRefresh) r(); }
        finally { syncing = was; }
        if (masterStatus != null) masterStatus.Text = Loc.T(MasterSet.On ? "master.status.on" : "master.status.off");
    }

    /// <summary>
    /// The Volumen panel: short-term and integrated LUFS of the master (the same <see cref="LoudnessMeter"/> as the
    /// transport's LUFS cell, so they start over together each pass), the pass's true peak and how far from −14 LUFS.
    /// Texts change only by 0,1, so it does not allocate every frame.
    /// </summary>
    void TickVolume()
    {
        var lufs = engine.Loudness;
        if (double.IsNegativeInfinity(lufs.Integrated)) masterOutPeak = 0;
        static bool Changed(double v, ref double shown)
        {
            double r = double.IsFinite(v) ? Math.Round(v * 10) / 10 : double.NegativeInfinity;
            if (r.Equals(shown)) return false;
            shown = r;
            return true;
        }
        static string Lufs(double v) => double.IsFinite(v) ? $"{v:0.0} LUFS" : "—";
        if (masterShortText != null && Changed(lufs.ShortTerm, ref masterShortShown)) masterShortText.Text = Lufs(lufs.ShortTerm);
        if (masterIntText != null && Changed(lufs.Integrated, ref masterIntShown))
        {
            masterIntText.Text = Lufs(lufs.Integrated);
            if (masterToGoText != null)
                masterToGoText.Text = double.IsFinite(lufs.Integrated) ? FmtDb(SocialLufs - lufs.Integrated) : "—";
        }
        double peak = masterOutPeak > 0 ? 20 * Math.Log10(masterOutPeak) : double.NegativeInfinity;
        if (masterPeakText != null && Changed(peak, ref masterPeakShown))
        {
            masterPeakText.Text = double.IsFinite(peak) ? $"{peak:0.0} dBTP" : "—";
            // over the Techo (plus the meter's rounding) is what the AAC could clip
            masterPeakText.Foreground = peak > MasterSet.LimCeilingDb + 0.05 ? Palette.Rec : Palette.Fg;
        }
    }

    /// <summary>From the window tick: the bands' and the limiter's live reduction, only while the view is on screen. Text changes only by 0,5 dB.</summary>
    void TickMaster()
    {
        if (!masteringScroll.IsVisible)
        {
            for (int i = 0; i < MasterBands.Count; i++) engine.Master.TakeReductionDb(i);
            engine.Buses.Clip.TakeReductionDb();
            return;
        }
        Meter(-masterLimDb, ref masterLimShow, ref masterLimShown, masterLimBar, masterLimText);
        Meter(-engine.Buses.Clip.TakeReductionDb(), ref masterClipShow, ref masterClipShown, masterClipBar, masterClipText);
        TickVolume();
        for (int i = 0; i < MasterBands.Count; i++)
            Meter(-engine.Master.TakeReductionDb(i), ref masterGrShow[i], ref masterGrShown[i], masterGrBars[i], masterGrText[i]);

        static void Meter(double gr, ref double show, ref double shown, Border? bar, TextBlock? text)
        {
            show = gr > show ? gr : show + (gr - show) * 0.12;
            if (bar != null) bar.Width = Math.Clamp(show / GrBarFullDb, 0, 1) * GrBarW;
            double s = Math.Round(show * 2) / 2;
            if (s != shown && text != null)
            {
                shown = s;
                text.Text = FmtGr(s);
            }
        }
    }
}
