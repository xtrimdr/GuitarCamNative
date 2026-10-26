using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>Port of TakeInspector.tsx inside App.tsx's left sidebar ("Canal", folds to a column of icons and remembers
/// it like gc.sidebarCollapsed). Track effects edit the channel; sync, fades and framing edit the selected native clip.
/// The EQ draws the selected track's live spectrum (<see cref="SpectrumTap"/>, FFT on the UI thread). Native
/// differences: multiple clips per track, offline status; Avalonia Fluent controls and Segoe icons follow the
/// platform. Media corrections remain immutable.</summary>
public partial class MainWindow
{
    readonly List<Action> inspectorRefresh = [];
    readonly StackPanel takeSections = new() { Spacing = 0 };
    readonly StackPanel emptySections = new() { Spacing = 8 };
    readonly StackPanel noSelection = new() { Margin = new Thickness(10, 32), Spacing = 4 };
    readonly TextBlock noSelectionHint = new() { FontSize = 12, Foreground = Palette.Faint, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly Grid takeHeader = new() { ColumnDefinitions = new ColumnDefinitions("24,*,Auto") };
    readonly TrackBadge takeBadge = new() { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox takeName = new() { Classes = { "title" } };
    readonly TextBlock takeNamePencil = new() { Classes = { "icon" }, Text = "\uE70F", FontSize = 11, Foreground = Palette.Faint, Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
    readonly Border takeHead = new() { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(-14, 0, -14, -12), Padding = new Thickness(14, 0, 14, 12) };
    readonly TextBlock takeInfo = new() { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 6, 0, 0) };
    readonly WrapPanel takeFacts = new() { Margin = new Thickness(34, 6, 0, 0) };
    readonly TextBlock takeLength = new(), takeFormat = new(), takeChannels = new();
    readonly TextBlock GrText = new() { Classes = { "dim", "mono" }, FontSize = 11 };
    readonly Border GrBar = new() { Width = 0, HorizontalAlignment = HorizontalAlignment.Right, Background = Palette.Accent, CornerRadius = new CornerRadius(3) };
    readonly Border GrTrack = new() { Height = 6, Background = Palette.App, CornerRadius = new CornerRadius(3) };
    string? inspectorTrack;
    readonly Dictionary<string, (StackPanel Body, Control? Action, SectionHeader Header)> inspectorSections = [];
    readonly HashSet<string> collapsedSections = [];
    Button? collapseAll;
    readonly PathIcon collapseAllIcon = new() { Width = 14, Height = 14 };
    Task inspectorPrefsSaving = Task.CompletedTask;
    const double InspectorCollapsedWidth = 44;
    readonly DockPanel inspectorExpanded = new();
    readonly StackPanel inspectorCollapsed = new() { Spacing = 4, Margin = new Thickness(0, 8), HorizontalAlignment = HorizontalAlignment.Center };
    EqEditor? eqEditor;
    readonly SpectrumTap spectrumTap = new();
    readonly float[] spectrumBins = new float[SpectrumTap.Bins];
    LiveTrack? spectrumTrack;
    long spectrumFrom;

    void SetSectionCollapsed(string title, bool hide)
    {
        if (hide) collapsedSections.Add(title); else collapsedSections.Remove(title);
        if (inspectorSections.TryGetValue(title, out var section))
        {
            section.Body.IsVisible = !hide;
            if (section.Action != null) section.Action.IsVisible = !hide;
            section.Header.SetCollapsed(hide);
        }
    }
    void SaveCollapsedSections()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(collapsedSections);
        inspectorPrefsSaving = inspectorPrefsSaving.ContinueWith(_ =>
        {
            try { AppConfig.Set("collapsed.inspector", json); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* View preference only. */ }
        }, TaskScheduler.Default);
        UpdateCollapseButton();
    }
    /// <summary>TakeInspector.tsx's CollapseFxButton (ChevronsDownUp / ChevronsUpDown), but it folds every section of
    /// the take, not only the effects: the user asked for "colapsar todo".</summary>
    void UpdateCollapseButton()
    {
        if (collapseAll == null) return;
        bool all = inspectorSections.Keys.All(collapsedSections.Contains);
        ToolTip.SetTip(collapseAll, all ? Loc.T("insp.sections.show") : Loc.T("insp.sections.hide"));
        collapseAllIcon.Data = (Geometry)Application.Current!.FindResource(all ? "ExpandAllIcon" : "CollapseAllIcon")!;
    }

    /// <summary>A quiet chip under the take name (duration, format…): Hover fill, Dim mono text.</summary>
    static Border FactBadge(string glyph, TextBlock text)
    {
        text.Classes.Add("mono"); text.FontSize = 11; text.Foreground = Palette.Dim; text.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (glyph != "") row.Children.Add(new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 10, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(text);
        return new Border { Background = Palette.Hover, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2), Margin = new Thickness(0, 0, 4, 4), Child = row };
    }

    /// <summary>App.tsx's left aside: the "Canal" header with Loc.T("insp.hide"), or the folded column of icons.</summary>
    void InitInspectorBar()
    {
        var scroll = (Control)InspectorBar.Child!;
        InspectorBar.Child = null;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 44, Margin = new Thickness(16, 0, 6, 0) };
        header.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 14, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var title = new TextBlock { Text = Loc.T("insp.title"), FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        var hide = RailButton("", Loc.T("insp.hide"), () => SetInspectorCollapsed(true));
        shortcutTips.Add((hide, Loc.T("insp.hide"), "leftPanel"));
        hide.Foreground = Palette.Faint;
        Grid.SetColumn(hide, 3); header.Children.Add(hide);
        var top = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        DockPanel.SetDock(top, Dock.Top);
        inspectorExpanded.Children.Add(top); inspectorExpanded.Children.Add(scroll);
        foreach (var (glyph, tip) in new[] { ("", Loc.T("insp.rail.show")), ("", Loc.T("insp.title")) })
        {
            var b = RailButton(glyph, tip, () => SetInspectorCollapsed(false));
            shortcutTips.Add((b, tip, "leftPanel"));
            b.Foreground = Palette.Faint; inspectorCollapsed.Children.Add(b);
        }
        InitInspectorRail();
        inspectorWidth = SavedSidebarWidth("sidebarWidth");
        inspectorGrip = SidebarGrip(true, () => inspectorWidth, () => ProjectBar.Width, SetInspectorWidth, "sidebarWidth");
        InspectorBar.Child = new Panel { Children = { inspectorExpanded, inspectorCollapsed, inspectorGrip } };
        SetInspectorCollapsed(AppConfig.Get("sidebarCollapsed") == "1", save: false);
    }

    void SetInspectorCollapsed(bool collapsed, bool save = true)
    {
        InspectorBar.Width = collapsed ? InspectorCollapsedWidth : inspectorWidth;
        if (inspectorGrip != null) inspectorGrip.IsVisible = !collapsed;
        inspectorExpanded.IsVisible = !collapsed;
        inspectorCollapsed.IsVisible = collapsed;
        if (save) SaveViewPref("sidebarCollapsed", collapsed ? "1" : "2");
    }

    /// <summary>EqEditor's draw loop fed from the selected track: the tap hangs on that track's chain only while its EQ
    /// is on screen, and the spectrum counts as live (the web's readSpectrum ok) while the transport plays.</summary>
    void TickInspectorSpectrum()
    {
        var want = eqEditor is { IsEffectivelyVisible: true } && SelectedInspectorClip != null ? sel : null;
        if (spectrumTrack != want)
        {
            if (spectrumTrack != null) spectrumTrack.Chain.Spectrum = null;
            spectrumTrack = want;
            spectrumTap.ResetSmoothing();
            spectrumFrom = spectrumTap.Written;
            if (want != null) want.Chain.Spectrum = spectrumTap;
        }
        if (eqEditor == null) return;
        bool live = want != null && engine.Playing && spectrumTap.Written > spectrumFrom;
        if (live) spectrumTap.Read(spectrumBins);
        eqEditor.PushSpectrum(live ? spectrumBins : null, Audio.PlaybackEngine.SampleRate);
    }
    LiveClip? SelectedInspectorClip => sel?.Clips.FirstOrDefault(c => c.Clip.Id == Timeline.SelectedClipId)
        ?? sel?.ShownClip ?? sel?.Clips.FirstOrDefault();

    static string AmountLabel(double a) => Loc.T(a < .2 ? "insp.amount.0" : a < .45 ? "insp.amount.1" : a < .7 ? "insp.amount.2" : a < .9 ? "insp.amount.3" : "insp.amount.4");
    static string Db(double v) => $"{v:+0.0;-0.0;0.0} dB";
    static string Ms(double v) => $"{v:+0.###############;-0.###############;0} ms";
    static string Sec(double v) => $"{v:0.00} s";
    static TextBlock Help(string text) => new() { Text = text, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap };
    static Button InspectorIcon(string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "ghost" }, Content = new TextBlock { Text = glyph, Classes = { "icon" }, FontSize = 12 }, MinHeight = 28, Padding = new Thickness(4) };
        ToolTip.SetTip(b, tip); b.Click += (_, _) => click(); return b;
    }
    /// <summary>Sections are keyed (and remembered folded) by their Spanish id; this is the text shown.</summary>
    static string SectionTitle(string id) => SendTitles.TryGetValue(id, out var send) ? send
        : BaseTitle(id) is var b && b != id ? $"{SectionTitle(b)} {id[(b.Length + 1)..]}" : Loc.T(id switch
    {
        "Mezcla" => "insp.section.mix", "Saturación" => "insp.section.sat", "Compresor" => "insp.section.comp", "EQ" => "insp.section.eq",
        "Reverb" => "insp.section.reverb", "Delay" => "insp.section.delay", "Transponer" => "insp.section.transpose", "Sincronización" => "insp.section.sync", "Clip" => "insp.section.clip",
        "Encuadre" => "insp.section.frame", _ => id,
    });
    /// <summary>"EQ 2" → "EQ": a repeated slot's section, without its number.</summary>
    static string BaseTitle(string id) => id.LastIndexOf(' ') is int sp && sp > 0 && int.TryParse(id[(sp + 1)..], out _) ? id[..sp] : id;

    StackPanel Section(string title, string icon, Func<bool>? on = null, Action<bool>? power = null, Control? action = null,
        StackPanel? host = null, List<Action>? refresh = null)
    {
        var refreshes = refresh ?? inspectorRefresh;
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(16, 2, 16, 16) };
        var outer = new StackPanel();
        var border = new Border { Classes = { "section" }, Child = outer, Background = Palette.Panel,
            BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0), Margin = new Thickness(-14, 0) };
        var header = new SectionHeader(SectionTitle(title), icon);
        var sectionIcon = header.Icon;
        inspectorSections[title] = (content, action, header);
        sectionLooks[title] = (icon, on);
        header.Toggled += () => { SetSectionCollapsed(title, !collapsedSections.Contains(title)); SaveCollapsedSections(); };
        ToggleButton? powerButton = null;
        if (on != null && power != null)
        {
            var toggle = powerButton = new ToggleButton { Classes = { "chip", "inspector-power" }, MinHeight = 24, Padding = new Thickness(6, 2), FontSize = 10.5, Margin = new Thickness(6, 0, 0, 0) };
            toggle.IsCheckedChanged += (_, _) => { if (!syncing) power(toggle.IsChecked == true); };
            refreshes.Add(() =>
            {
                toggle.IsChecked = on();
                var powerLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                powerLabel.Children.Add(new TextBlock { Classes = { "icon" }, Text = "\uE7E8", FontSize = 11, Foreground = on() ? Palette.App : Palette.Faint });
                powerLabel.Children.Add(new TextBlock { Text = on() ? "ON" : "OFF", FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = on() ? Palette.App : Palette.Faint });
                toggle.Content = powerLabel;
                toggle.Classes.Set("lit", on());
                toggle.Background = on() ? Palette.Accent : Brushes.Transparent;
                toggle.Foreground = on() ? Palette.App : Palette.Faint;
                ToolTip.SetTip(toggle, SendTitles.TryGetValue(title, out var send) ? Loc.T(on() ? "send.power.off" : "send.power.on", send)
                    : Loc.T("insp.power." + (BaseTitle(title) switch { "EQ" => "eq", "Saturación" => "sat", "Compresor" => "comp", "Reverb" => "reverb", _ => "delay" }) + (on() ? ".off" : ".on")));
            });
        }
        header.SetTrailing(action, powerButton);
        outer.Children.Add(header); outer.Children.Add(content);
        (host ?? takeSections).Children.Add(border);
        refreshes.Add(() => { bool enabled = on == null || on(); content.Opacity = enabled ? 1 : 0.4; FxIcon.SetColor(sectionIcon, enabled ? Palette.Accent : Palette.Faint); });
        SetSectionCollapsed(title, collapsedSections.Contains(title));
        return content;
    }
    /// <summary>CompControl.tsx's amount row: the big knob, then the uppercase label, the value in words and a mono detail.</summary>
    static (InspectorKnob Knob, TextBlock Value, TextBlock Detail) KnobRow(StackPanel host, string label)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*"), Margin = new Thickness(0, 2) };
        var knob = new InspectorKnob(); row.Children.Add(knob);
        var info = new StackPanel { Spacing = 3, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Classes = { "label" } });
        var value = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold };
        var detail = new TextBlock { Classes = { "dim", "mono" }, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
        info.Children.Add(value); info.Children.Add(detail); Grid.SetColumn(info, 1); row.Children.Add(info);
        host.Children.Add(row);
        return (knob, value, detail);
    }

    /// <summary>A 0–1 effect amount on the big knob instead of a slider (asked by the user, like the compressor's).</summary>
    void EffectKnob(StackPanel host, string label, double reset, Func<double> get, Action<double> set,
        Func<double, string> value, Func<double, string> detail, string hint, List<Action>? refresh = null)
    {
        var (knob, valueText, detailText) = KnobRow(host, label);
        knob.ResetValue = reset;
        knob.Changed += v => { if (!syncing) set(v); };
        host.Children.Add(Help(hint));
        (refresh ?? inspectorRefresh).Add(() =>
        {
            double v = get();
            knob.Set(v); valueText.Text = value(v); detailText.Text = detail(v);
            ToolTip.SetTip(knob, Loc.T("insp.knob.tip", label, value(v), value(reset)));
        });
    }

    /// <summary>A send level as the gain it applies, so 100% reads 0,0 dB.</summary>
    static string SendDb(double v) => v <= 0 ? "-∞ dB" : Db(20 * Math.Log10(v));

    InspectorSlider Slider(StackPanel host, string label, double min, double max, double step, double reset,
        Func<double> get, Action<double> set, Func<double, string> format, string? hint = null, Func<double>? maximum = null, List<Action>? refresh = null)
    {
        var slider = new InspectorSlider(label, min, max, step, reset, format, v => { if (!syncing) set(v); }, hint);
        host.Children.Add(slider); (refresh ?? inspectorRefresh).Add(() => slider.Set(get(), maximum?.Invoke())); return slider;
    }
    void ClipEdit(Action<Clip, Media?> edit, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly || SelectedInspectorClip is not { } live) return;
        undo.Checkpoint(store.Project, $"clip:{live.Clip.Id}:{key}");
        edit(live.Clip, live.Media);
        live.RefreshCurve();
        engine.RefreshClipTiming();
        MarkDirty(); UpdateUndoButtons(); RefreshInspectorText();
        Timeline.LengthSec = tracks.Count == 0 ? 0 : tracks.Max(t => t.EndSec);
        Timeline.InvalidateVisual(); Preview.InvalidateVisual();
    }
    /// <param name="comp">The compressor slot to measure; the channel's first when null.</param>
    static void RecalculateInspectorComp(LiveTrack track, CompSettings? comp = null)
    {
        var c = comp ?? track.Channel.Comp;
        double gr = track.RecalculateCompPreset(c);
        c.Extra ??= [];
        c.Extra["estGrDb"] = System.Text.Json.JsonSerializer.SerializeToElement(gr);
    }

    static double EstimatedCompression(CompSettings c) =>
        c.Extra?.TryGetValue("estGrDb", out var value) == true && value.TryGetDouble(out double gr) && double.IsFinite(gr)
            ? gr : Math.Max(0, -c.Threshold / 4);

    void InitInspector()
    {
        try
        {
            foreach (var title in System.Text.Json.JsonSerializer.Deserialize<string[]>(AppConfig.Get("collapsed.inspector") ?? "[]") ?? []) collapsedSections.Add(title);
        }
        catch (System.Text.Json.JsonException) { }
        InitInspectorBar();
        var header = takeHeader;
        takeBadge.Clicked += () => { if (sel != null) ShowTrackLook(sel, takeBadge); };
        header.Children.Add(takeBadge);
        takeName.InnerRightContent = takeNamePencil;
        takeName.PointerEntered += (_, _) => takeNamePencil.Opacity = takeName.IsFocused ? 0 : 1;
        takeName.PointerExited += (_, _) => takeNamePencil.Opacity = 0;
        takeName.GotFocus += (_, _) => takeNamePencil.Opacity = 0;
        ToolTip.SetTip(takeName, Loc.T("insp.name.tip"));
        takeName.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(takeName, 1); header.Children.Add(takeName);
        // Avalonia raises TextChanged after the fact (outside syncing), so filling the box must not count as an edit.
        takeName.TextChanged += (_, _) => { if (sel != null && (takeName.Text ?? "") != sel.Track.Name) Edit(t => t.Track.Name = takeName.Text ?? "", "name"); };
        collapseAll = new Button { Classes = { "ghost" }, Content = collapseAllIcon, Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        collapseAll.Click += (_, _) =>
        {
            bool hide = !inspectorSections.Keys.All(collapsedSections.Contains);
            foreach (string title in inspectorSections.Keys) SetSectionCollapsed(title, hide);
            SaveCollapsedSections();
        };
        Grid.SetColumn(collapseAll, 2); header.Children.Add(collapseAll);
        takeFacts.Children.Add(FactBadge("\uE916", takeLength));
        takeFacts.Children.Add(FactBadge("\uE8D6", takeFormat));
        takeFacts.Children.Add(FactBadge("", takeChannels));
        noSelection.Children.Add(new TextBlock { Text = Loc.T("insp.none"), FontSize = 14, Foreground = Palette.Dim, TextAlignment = TextAlignment.Center });
        noSelection.Children.Add(noSelectionHint);
        Inspector.Children.Add(noSelection);
        takeHead.Child = new StackPanel { Children = { header, takeInfo, takeFacts } };
        Inspector.Children.Add(takeHead);
        var recordHint = new TextBlock { FontSize = 12, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        recordHint.Inlines!.Add(new Avalonia.Controls.Documents.Run(Loc.T("insp.empty.a")));
        recordHint.Inlines.Add(new Avalonia.Controls.Documents.Run("R") { FontFamily = Palette.Mono.FontFamily, Foreground = Palette.Fg });
        recordHint.Inlines.Add(new Avalonia.Controls.Documents.Run(Loc.T("insp.empty.b")));
        emptySections.Children.Add(recordHint);
        emptySections.Children.Add(new TextBlock { Text = Loc.T("insp.empty.c"), FontSize = 12, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
        var removeEmpty = RemoveButton(Loc.T("insp.removeTrack")); removeEmpty.Margin = new Thickness(0, 12, 0, 0);
        removeEmpty.Click += async (_, _) => await DeleteTimelineSelection(); emptySections.Children.Add(removeEmpty);
        Inspector.Children.Add(emptySections); Inspector.Children.Add(takeSections);
        var mix = Section("Mezcla", "\uE9E9", action: InspectorIcon("\uE74F", Loc.T("insp.mute.tip"), () => Edit(t => t.Channel.Muted = !t.Channel.Muted, "mute")));
        Slider(mix, Loc.T("insp.volume"), 0, 1.5, 0.01, 1, () => sel!.Channel.Volume, v => Edit(t => t.Channel.Volume = v, "volume"), v => $"{v * 100:0}%");
        Slider(mix, Loc.T("insp.pan"), -1, 1, 0.05, 0, () => sel!.Channel.Pan, v => Edit(t => t.Channel.Pan = Math.Abs(v) < 0.04 ? 0 : v, "pan"), v => v == 0 ? "C" : $"{(v < 0 ? "L" : "R")} {Math.Abs(v) * 100:0}");
        // native only: a track that sounds without a panel (a backing track, a click…)
        var inVideo = new CheckBox { Content = Loc.T("insp.inVideo"), FontSize = 12 };
        ToolTip.SetTip(inVideo, Loc.T("insp.inVideo.tip"));
        inVideo.IsCheckedChanged += async (_, _) => { if (!syncing && sel != null) await SetInVideo(sel, inVideo.IsChecked == true); };
        inspectorRefresh.Add(() => inVideo.IsChecked = sel!.Track.Video);
        mix.Children.Add(inVideo);
        videoOnly.Add(inVideo);
        // the insert slots, in their order (RebuildInspectorChain)
        takeSections.Children.Add(inspectorChain);
        // where the track goes and its sends, one section per bus (RebuildInspectorSends); every send can be taken
        // before the fader: its PRE chip, in the section's header
        takeSections.Children.Add(inspectorSends);
        // native only: the track's pitch, same length (MainWindow.Transpose.cs). A card of its own beside the other
        // effects (asked by the user); it reads as off (dimmed) while the track plays at its original pitch.
        var transpose = Section("Transponer", FxIcon.Transpose, () => sel!.Track.Transpose != 0);
        Slider(transpose, Loc.T("insp.transpose.semis"), -PitchShifter.MaxSemitones, PitchShifter.MaxSemitones, 1, 0,
            () => sel!.Track.Transpose, SetTranspose, TransposeText, Loc.T("insp.transpose.tip"));
        transpose.Children.Add(Help(Loc.T("insp.transpose.hint")));
        InitTranspose(transpose);
        InitClipInspector();
        UpdateCollapseButton();
    }

    /// <summary>
    /// The Canal tab's effects: one section per insert slot, in the order the sound goes through them, then "+ Efecto".
    /// Rebuilt only when the track or its list of slots changes (<see cref="inspectorChainKey"/>); every other edit just
    /// refreshes it. A repeated kind is numbered ("EQ 2") and remembers its own folded state.
    /// </summary>
    void RebuildInspectorChain(LiveTrack t)
    {
        // the LiveTrack itself too: a reopened project builds new ones with the same ids
        string key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t)}|{t.Track.Id}|" + string.Join("/", t.Channel.Slots.Select(s => s.Id));
        if (key == inspectorChainKey) return;
        inspectorChainKey = key;
        foreach (string title in chainSections) inspectorSections.Remove(title);
        chainSections.Clear();
        chainRefresh.Clear();
        inspectorChain.Children.Clear();
        eqEditor = null;
        var drag = new SlotDrag(0);
        drag.Moved += (id, anchor, after) => SlotsEdit(t, x => ChannelInserts.MoveNextTo(x.Channel, id, anchor, after), "move");
        var slots = t.Channel.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Known) continue;
            int nth = 1;
            for (int k = 0; k < i; k++) if (slots[k].Type == slot.Type) nth++;
            string id = slot.Id;
            switch (slot.Type)
            {
                case InsertSlot.SatKind: SatSection(t, id, nth, drag); break;
                case InsertSlot.CompKind: CompSection(t, id, nth, drag); break;
                default: EqSection(t, id, nth, drag); break;
            }
        }
        var add = AddSlotButton(t);
        add.Margin = new Thickness(0, 12, 0, 12);
        inspectorChain.Children.Add(add);
        UpdateCollapseButton();
    }

    readonly StackPanel inspectorChain = new();
    readonly List<Action> chainRefresh = [];
    readonly List<string> chainSections = [];
    string inspectorChainKey = "";

    /// <summary>A slot's section: numbered when its kind repeats, dragged by its grip, with the slot's ⋯ menu.</summary>
    StackPanel SlotSection(LiveTrack t, string id, string baseTitle, int nth, string icon, Func<bool> on, Action<bool> power, SlotDrag drag, Control? action = null)
    {
        string title = nth > 1 ? $"{baseTitle} {nth}" : baseTitle;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        if (action != null) actions.Children.Add(action);
        actions.Children.Add(SlotMenuButton(t, id));
        var content = Section(title, icon, on, power, actions, inspectorChain, chainRefresh);
        chainSections.Add(title);
        var card = (Control)content.Parent!.Parent!;
        var header = inspectorSections[title].Header;
        // its ⋯ stays at hand when the section is folded (moving or removing a folded slot is the common case)
        inspectorSections[title] = (content, null, header);
        actions.IsVisible = true;
        var grip = new GripDots { Margin = new Thickness(-10, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(grip, Loc.T("mix.slot.drag.tip"));
        header.SetGrip(grip);
        inspectorChain.Children.Remove(card);
        inspectorChain.Children.Add(drag.Add(card, id, grip));
        return content;
    }

    void SatSection(LiveTrack t, string id, int nth, SlotDrag drag)
    {
        SatSettings S() => t.Channel.Slot(id)?.Sat ?? GoneSat;
        void SatEdit(Action<SatSettings> edit, string key) => SlotEdit(t, id, s => edit(s.Sat!), key);
        var sat = SlotSection(t, id, "Saturación", nth, FxIcon.Sat, () => S().On, on => SatEdit(s => s.On = on, "sat.on"), drag);
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        sat.Children.Add(new Border { Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Left, Child = modes });
        foreach (var (mode, text) in new[] { ("tube", Loc.T("insp.sat.tube")), ("tape", Loc.T("insp.sat.tape")) })
        {
            var b = new ToggleButton { Content = text, Classes = { "seg" } };
            b.Click += (_, _) => SatEdit(s => { s.Mode = mode; s.On = true; }, "sat.mode");
            chainRefresh.Add(() => b.IsChecked = S().Mode == mode); modes.Children.Add(b);
        }
        EffectKnob(sat, Loc.T("insp.sat.life"), 0.3, () => S().Amount, v => SatEdit(s => { s.Amount = v; s.On = true; }, "sat.amount"),
            v => Loc.T(v < .2 ? "insp.life.0" : v < .45 ? "insp.life.1" : v < .7 ? "insp.life.2" : v < .9 ? "insp.life.3" : "insp.life.4"), v => $"{v * 100:0}%",
            Loc.T("insp.sat.hint"), chainRefresh);
        var satHint = Help(""); sat.Children.Add(satHint);
        chainRefresh.Add(() => satHint.Text = S().Mode == "tape" ? Loc.T("insp.sat.tape.hint") : Loc.T("insp.sat.tube.hint"));
    }

    void CompSection(LiveTrack t, string id, int nth, SlotDrag drag)
    {
        CompSettings C() => t.Channel.Slot(id)?.Comp ?? GoneComp;
        void CompEdit(Action<CompSettings> edit, string key, bool manual = false, bool recalculate = false) => SlotEdit(t, id, s =>
        {
            var c = s.Comp!;
            if (SelectedInspectorClip is { } clip)
            {
                clip.Clip.Extra ??= [];
                clip.Clip.Extra.TryAdd("role", System.Text.Json.JsonSerializer.SerializeToElement(c.Preset == "lead" ? "solo" : "base"));
            }
            edit(c);
            if (manual) c.Preset = "custom";
            if (recalculate) RecalculateInspectorComp(t, c);
        }, "comp." + key);
        var reset = InspectorIcon("", Loc.T("insp.comp.reset"), () => SlotEdit(t, id, s =>
        {
            bool on = s.Comp!.On;
            string preset = InspectorEditing.DefaultCompPreset(SelectedInspectorClip?.Clip, tracks.IndexOf(t) == 1 ? "lead" : "rhythm");
            s.Comp = new CompSettings { On = on, Preset = preset };
            RecalculateInspectorComp(t, s.Comp);
        }, "comp.reset"));
        var comp = SlotSection(t, id, "Compresor", nth, FxIcon.Comp, () => C().On, on => CompEdit(c => c.On = on, "on", recalculate: true), drag, reset);
        comp.Children.Add(Help(Loc.T("insp.comp.what")));
        var presets = new UniformGrid { Columns = 3 };
        foreach (var (preset, label) in new[] { ("rhythm", Loc.T("insp.comp.rhythm")), ("arpeggio", Loc.T("insp.comp.arpeggio")), ("lead", Loc.T("insp.comp.lead")) })
        {
            var b = new ToggleButton { Content = label, Classes = { "seg", "pick" }, Padding = new Thickness(3, 0), Margin = new Thickness(2, 0) };
            b.Click += (_, _) => CompEdit(c => { c.Preset = preset; c.On = true; }, "preset", recalculate: true);
            chainRefresh.Add(() => b.IsChecked = C().Preset == preset); presets.Children.Add(b);
        }
        comp.Children.Add(new Border { Classes = { "seg" }, Child = presets });
        var (knob, amountText, summary) = KnobRow(comp, Loc.T("insp.comp.amount"));
        knob.Changed += v => { if (!syncing) CompEdit(c => { c.Amount = v; c.On = true; }, "amount", recalculate: true); };
        var compHint = Help(""); comp.Children.Add(compHint);
        chainRefresh.Add(() =>
        {
            var c = C(); knob.Set(c.Amount); knob.IsEnabled = c.Preset != "custom";
            amountText.Text = c.Preset == "custom" ? Loc.T("insp.comp.manual") : AmountLabel(c.Amount);
            summary.Text = c.Preset == "custom" ? Loc.T("insp.comp.summary.manual") : Loc.T("insp.comp.summary", $"{EstimatedCompression(c):0}")
                + (c.Eq.PresenceDb >= 1 ? Loc.T("insp.comp.presence.up", $"{c.Eq.PresenceDb:0.0}") : c.Eq.PresenceDb <= -1 ? Loc.T("insp.comp.presence.down", $"{c.Eq.PresenceDb:0.0}") : "");
            ToolTip.SetTip(knob, c.Preset == "custom" ? Loc.T("insp.comp.manual.tip") : Loc.T("insp.comp.amount.tip", AmountLabel(c.Amount)));
            compHint.Text = c.Preset switch { "rhythm" => Loc.T("insp.comp.rhythm.hint"), "arpeggio" => Loc.T("insp.comp.arpeggio.hint"), "lead" => Loc.T("insp.comp.lead.hint"), _ => "" };
        });
        // the fine controls start open when the compressor is set by hand
        var fine = new StackPanel { Spacing = 14, IsVisible = C().Preset == "custom" };
        var fineButton = new Button { Content = (fine.IsVisible ? "▾ " : "▸ ") + Loc.T("insp.fine"), Classes = { "ghost" }, FontSize = 11, Padding = new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0) };
        fineButton.Click += (_, _) => { fine.IsVisible = !fine.IsVisible; fineButton.Content = (fine.IsVisible ? "▾ " : "▸ ") + Loc.T("insp.fine"); };
        comp.Children.Add(fineButton); comp.Children.Add(fine);
        InspectorSlider Fine(string label, double min, double max, double step, double reset, Func<CompSettings, double> get, Action<CompSettings, double> set, Func<double, string> format, string? hint = null) =>
            Slider(fine, label, min, max, step, reset, () => get(C()), v => CompEdit(c => set(c, v), label, manual: true), format, hint, refresh: chainRefresh);
        Fine(Loc.T("insp.comp.threshold"), -60, 0, .5, -18, c => c.Threshold, (c, v) => c.Threshold = v, v => $"{v:0.0} dB");
        Fine(Loc.T("insp.comp.ratio"), 1, 20, .1, 4, c => c.Ratio, (c, v) => c.Ratio = v, v => $"{v:0.0}:1");
        Fine(Loc.T("insp.comp.attack"), .1, 100, .1, 10, c => c.Attack * 1000, (c, v) => c.Attack = v / 1000, v => $"{v:0.0} ms");
        Fine(Loc.T("insp.comp.release"), 10, 1000, 5, 150, c => c.Release * 1000, (c, v) => c.Release = v / 1000, v => $"{v:0} ms");
        Fine(Loc.T("insp.comp.knee"), 0, 24, .5, 6, c => c.Knee, (c, v) => c.Knee = v, v => $"{v:0.0} dB");
        Fine(Loc.T("insp.comp.makeup"), -12, 30, .5, 0, c => c.Makeup, (c, v) => c.Makeup = v, Db, Loc.T("insp.comp.makeup.hint"));
        Fine(Loc.T("insp.comp.hpf"), 10, 300, 5, 10, c => c.Eq.HpfHz, (c, v) => c.Eq.HpfHz = v, v => v <= 10 ? "Off" : $"{v:0} Hz");
        Fine(Loc.T("insp.comp.mud"), -6, 6, .5, 0, c => c.Eq.MudDb, (c, v) => c.Eq.MudDb = v, Db);
        var presence = Fine(Loc.T("insp.comp.presence", "2,5"), -6, 6, .5, 0, c => c.Eq.PresenceDb, (c, v) => c.Eq.PresenceDb = v, Db, Loc.T("insp.comp.presence.hint"));
        chainRefresh.Add(() => presence.Label.Text = Loc.T("insp.comp.presence", $"{C().Eq.PresenceHz / 1000:0.0}"));
        Fine(Loc.T("insp.comp.air"), -6, 6, .5, 0, c => c.Eq.AirDb, (c, v) => c.Eq.AirDb = v, Db);
        var limit = new CheckBox { Content = Loc.T("insp.comp.limit"), FontSize = 12 };
        ToolTip.SetTip(limit, Loc.T("insp.comp.limit.tip"));
        limit.IsCheckedChanged += (_, _) => { if (!syncing) CompEdit(c => c.Limit = limit.IsChecked == true, "limit"); };
        chainRefresh.Add(() => limit.IsChecked = C().Limit); fine.Children.Add(limit);
        fine.Children.Add(Help(Loc.T("insp.comp.fine.hint")));
    }

    void EqSection(LiveTrack t, string id, int nth, SlotDrag drag)
    {
        EqSettings E() => t.Channel.Slot(id)?.Eq ?? GoneEq;
        var host = SlotSection(t, id, "EQ", nth, FxIcon.Eq, () => E().On, on => SlotEdit(t, id, s => s.Eq!.On = on, "eq.on"), drag);
        var eq = new EqEditor();
        // the live spectrum (after the whole chain) goes on the first EQ
        eqEditor ??= eq;
        eq.Changed += value => SlotEdit(t, id, s => s.Eq = value, "eq");
        host.Children.Add(eq);
        chainRefresh.Add(() => eq.Set(E()));
    }

    void InitClipInspector()
    {
        Clip C() => SelectedInspectorClip!.Clip;
        Media? M() => SelectedInspectorClip!.Media;
        double Dur() => SelectedInspectorClip!.LengthSec;
        double Len() { var w = ClipEditing.Window(C(), M(), Dur()); return Math.Max(.01, w.End - w.Start); }
        var sync = Section("Sincronización", "\uE823");
        void SetSync(double v) => ClipEdit((c, m) => InspectorEditing.SetSync(c, m, v), "sync");
        // ±1 s instead of the web's ±250 ms (user request): some interfaces and cameras lag more than that
        const double SyncRangeMs = 1000;
        var offset = Slider(sync, Loc.T("insp.sync.offset"), -SyncRangeMs, SyncRangeMs, 1, 0, () => C().EffectiveSyncMs(M()), SetSync, Ms,
            Loc.T("insp.sync.offset.hint"));
        foreach (int delta in new[] { -5, 5 })
        {
            var b = new Button { Content = delta < 0 ? "−5" : "+5", Classes = { "chip", "mono" }, FontSize = 10.5, Padding = new Thickness(4, 0), MinHeight = 22 };
            b.Click += (_, _) => SetSync(C().EffectiveSyncMs(M()) + delta); offset.Trailing.Children.Add(b);
        }
        var cameraLatency = Slider(sync, Loc.T("insp.sync.camera"), -SyncRangeMs, SyncRangeMs, 5, 80, () => C().EffectiveVideoOffsetMs(M()), v => ClipEdit((c, _) => c.VideoOffsetMs = v, "video-offset"), Ms,
            Loc.T("insp.sync.camera.hint"));
        videoOnly.Add(cameraLatency);
        var resetClip = InspectorIcon("\uE777", Loc.T("insp.clip.reset"), () => ClipEdit((c, _) => ClipEditing.Reset(store!.Project, c.Id), "reset"));
        var clip = Section("Clip", "\uE8C6", action: resetClip);
        Slider(clip, Loc.T("insp.clip.gain"), GuitarCam.Projects.Clip.MinGainDb, GuitarCam.Projects.Clip.MaxGainDb, .5, 0, () => C().GainDb, v => ClipEdit((c, _) => c.GainDb = v, "gain"), Db,
            Loc.T("insp.clip.gain.hint"));
        Slider(clip, Loc.T("insp.clip.trimStart"), 0, 10, .01, 0, () => C().TrimStartSec, v => ClipEdit((c, _) => c.TrimStartSec = v, "trim-start"), Sec,
            Loc.T("insp.clip.trimStart.hint"), () => Math.Max(.01, Dur() - C().TrimEndSec - ClipEnvelope.MinClipSec));
        Slider(clip, Loc.T("insp.clip.trimEnd"), 0, 10, .01, 0, () => C().TrimEndSec, v => ClipEdit((c, _) => c.TrimEndSec = v, "trim-end"), Sec,
            maximum: () => Math.Max(.01, Dur() - C().TrimStartSec - ClipEnvelope.MinClipSec));
        ClipEnvelope.FadePair Audio() => ClipEnvelope.FitFades(C().FadeInSec, C().FadeOutSec, Len());
        Slider(clip, Loc.T("insp.clip.fadeIn"), 0, 10, .01, 0, () => Audio().FadeIn, v => ClipEdit((c, _) => c.FadeInSec = v, "fade-in"), Sec, maximum: Len);
        Slider(clip, Loc.T("insp.clip.fadeOut"), 0, 10, .01, 0, () => Audio().FadeOut, v => ClipEdit((c, _) => c.FadeOutSec = v, "fade-out"), Sec, maximum: Len);
        var link = new CheckBox { Content = Loc.T("insp.clip.linkFades"), FontSize = 12 };
        ToolTip.SetTip(link, Loc.T("insp.clip.linkFades.tip"));
        link.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            var audio = Audio(); ClipEdit((c, _) => { c.LinkVideoFades = link.IsChecked == true;
                if (!c.LinkVideoFades) { c.VideoFadeInSec = audio.FadeIn; c.VideoFadeOutSec = audio.FadeOut; } }, "link-fades");
        };
        clip.Children.Add(link);
        videoOnly.Add(link);
        var video = new StackPanel { Spacing = 10 }; clip.Children.Add(video);
        ClipEnvelope.FadePair Video() => ClipEnvelope.FitFades(C().VideoFadeInSec, C().VideoFadeOutSec, Len());
        Slider(video, Loc.T("insp.clip.vFadeIn"), 0, 10, .01, 0, () => Video().FadeIn, v => ClipEdit((c, _) => c.VideoFadeInSec = v, "video-fade-in"), Sec, maximum: Len);
        Slider(video, Loc.T("insp.clip.vFadeOut"), 0, 10, .01, 0, () => Video().FadeOut, v => ClipEdit((c, _) => c.VideoFadeOutSec = v, "video-fade-out"), Sec, maximum: Len);
        inspectorRefresh.Add(() => { link.IsChecked = C().LinkVideoFades; video.IsVisible = !AudioOnly && !C().LinkVideoFades;
            resetClip.IsEnabled = C().GainDb != 0 || C().GainCurve.Count > 0 || C().TrimStartSec > 0 || C().TrimEndSec > 0 || C().FadeInSec > 0 || C().FadeOutSec > 0 || !C().LinkVideoFades && (C().VideoFadeInSec > 0 || C().VideoFadeOutSec > 0); });
        clip.Children.Add(Help(Loc.T("insp.clip.hint")));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var mirror = InspectorIcon("\uE8AB", Loc.T("insp.frame.mirror"), () => ClipEdit((c, _) => c.Framing.Mirror = !c.Framing.Mirror, "mirror"));
        actions.Children.Add(mirror); actions.Children.Add(InspectorIcon("\uE777", Loc.T("insp.frame.reset"), () => ClipEdit((c, _) => InspectorEditing.ResetFrame(c), "framing")));
        var frame = Section("Encuadre", "\uE7A8", action: actions);
        videoOnly.Add((Control)frame.Parent!.Parent!); // the whole card
        inspectorRefresh.Add(() => mirror.Foreground = C().Framing.Mirror ? Palette.Accent : Palette.Dim);
        Slider(frame, Loc.T("insp.frame.zoom"), 1, 2.5, .01, 1, () => C().Framing.Zoom, v => ClipEdit((c, _) => InspectorEditing.Frame(c, zoom: v), "framing"), v => $"{v:0.00}×");
        frame.Children.Add(Help(Loc.T("insp.frame.hint")));
        var remove = RemoveButton(Loc.T("insp.removeClip")); remove.Margin = new Thickness(0, 16, 0, 0);
        remove.Click += async (_, _) => await DeleteTimelineSelection(); takeSections.Children.Add(remove);
        takeSections.Children.Add(Help(Loc.T("insp.removeClip.hint")));
    }

    /// <summary>The web's full-width "Quitar pista" / "Quitar del timeline" button with its MinusCircle icon.</summary>
    static Button RemoveButton(string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return new Button { Content = content, Classes = { "chip" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 32 };
    }

    void ShowTrack(LiveTrack? t)
    {
        sel = t;
        Inspector.IsEnabled = (t != null || SelectedFolder != null) && !restoring && store?.ReadOnly != true;
        inspectorTrack = t?.Track.Id;
        grShown = 0; RefreshInspectorText();
    }
    void RefreshInspectorText()
    {
        syncing = true;
        try
        {
            takeName.Text = sel?.Track.Name ?? "";
            var c = SelectedInspectorClip;
            var folder = sel == null ? SelectedFolder : null;
            noSelection.IsVisible = sel == null && folder == null;
            folderPanel.IsVisible = folder != null;
            if (folder != null) foreach (var refresh in folderRefresh) refresh();
            noSelectionHint.Text = tracks.Any(t => t.Clips.Count > 0) ? Loc.T("insp.none.hint") : Loc.T("insp.none.empty");
            takeHead.IsVisible = sel != null;
            takeBadge.Set(sel?.Track, sel == null ? 0 : tracks.IndexOf(sel) + 1, c != null);
            takeInfo.Text = sel == null ? "" : c == null ? Loc.T("insp.trackEmpty") : c.Offline ? Loc.T("clip.offline") : "";
            takeInfo.IsVisible = takeInfo.Text != "";
            takeFacts.IsVisible = c is { Offline: false };
            if (c is { Offline: false })
            {
                takeLength.Text = $"{c.LengthSec:0.0} s";
                takeFormat.Text = $"WAV {c.Audio!.SampleRate / 1000.0:0} kHz";
                takeChannels.Text = Loc.T("insp.stereo");
            }
            emptySections.IsVisible = sel != null && c == null;
            takeSections.IsVisible = c != null;
            if (collapseAll != null) collapseAll.IsVisible = c != null;
            if (sel != null && c != null)
            {
                RebuildInspectorChain(sel);
                RebuildInspectorSends(sel);
                foreach (var refresh in inspectorRefresh) refresh();
                foreach (var refresh in chainRefresh) refresh();
                foreach (var refresh in sendRefresh) refresh();
            }
        }
        finally { syncing = false; }
        RefreshInspectorRail();
        RefreshMixer();
    }
}

