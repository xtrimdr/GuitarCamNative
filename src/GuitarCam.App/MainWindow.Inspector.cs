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

/// <summary>Port of TakeInspector.tsx. Track effects edit the channel; sync, fades and framing edit the selected
/// native clip. Native differences: multiple clips per track, offline status, no live EQ spectrum yet;
/// Avalonia Fluent controls and Segoe icons follow the platform. Media corrections remain immutable.</summary>
public partial class MainWindow
{
    readonly List<Action> inspectorRefresh = [];
    readonly StackPanel takeSections = new() { Spacing = 0 };
    readonly StackPanel emptySections = new() { Spacing = 12 };
    readonly TextBlock takeNumber = new() { Classes = { "mono" }, Foreground = Palette.Accent, FontSize = 12 };
    readonly TextBox takeName = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
    readonly TextBlock takeInfo = new() { FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock GrText = new() { Classes = { "dim", "mono" }, FontSize = 11 };
    readonly Border GrBar = new() { Width = 0, HorizontalAlignment = HorizontalAlignment.Right, Background = Palette.Accent, CornerRadius = new CornerRadius(3) };
    readonly Border GrTrack = new() { Height = 6, Background = Palette.App, CornerRadius = new CornerRadius(3) };
    readonly StackPanel fineControls = new() { Spacing = 14, IsVisible = false };
    readonly Button fineButton = new() { Content = "▸ Ajuste fino", Classes = { "ghost" }, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left };
    string? inspectorTrack;
    readonly Dictionary<string, (StackPanel Body, Control? Action, Button Heading, TextBlock Chevron)> inspectorSections = [];
    readonly HashSet<string> collapsedSections = [];
    Button? collapseEffects;
    static readonly string[] EffectTitles = ["Saturación", "Compresor", "EQ", "Reverb", "Delay"];
    Task inspectorPrefsSaving = Task.CompletedTask;

    void SetSectionCollapsed(string title, bool hide)
    {
        if (hide) collapsedSections.Add(title); else collapsedSections.Remove(title);
        if (inspectorSections.TryGetValue(title, out var section))
        {
            section.Body.IsVisible = !hide;
            if (section.Action != null) section.Action.IsVisible = !hide;
            section.Chevron.Text = hide ? "\uE76C" : "\uE70D";
            ToolTip.SetTip(section.Heading, hide ? "Mostrar" : "Ocultar");
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
    void UpdateCollapseButton()
    {
        if (collapseEffects != null) ToolTip.SetTip(collapseEffects, EffectTitles.All(collapsedSections.Contains) ? "Mostrar todos los efectos" : "Colapsar todos los efectos");
    }
    LiveClip? SelectedInspectorClip => sel?.Clips.FirstOrDefault(c => c.Clip.Id == Timeline.SelectedClipId)
        ?? sel?.ShownClip ?? sel?.Clips.FirstOrDefault();

    static string AmountLabel(double a) => a < .2 ? "Muy suave" : a < .45 ? "Suave" : a < .7 ? "Media" : a < .9 ? "Fuerte" : "Muy fuerte";
    static string Db(double v) => $"{v:+0.0;-0.0;0.0} dB";
    static string Ms(double v) => $"{v:+0.###############;-0.###############;0} ms";
    static string Sec(double v) => $"{v:0.00} s";
    static TextBlock Help(string text) => new() { Text = text, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap };
    static Button InspectorIcon(string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "ghost" }, Content = new TextBlock { Text = glyph, Classes = { "icon" }, FontSize = 12 }, MinHeight = 28, Padding = new Thickness(4) };
        ToolTip.SetTip(b, tip); b.Click += (_, _) => click(); return b;
    }
    StackPanel Section(string title, string icon, Func<bool>? on = null, Action<bool>? power = null, Control? action = null)
    {
        var content = new StackPanel { Spacing = 14 };
        var outer = new StackPanel { Spacing = 12 };
        var border = new Border { Classes = { "section" }, Child = outer, Background = Palette.Panel,
            BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(16), Margin = new Thickness(-14, 0) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var chevron = new TextBlock { Classes = { "icon" }, Text = "\uE70D", Foreground = Palette.Faint, FontSize = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(chevron);
        var sectionIcon = new TextBlock { Text = icon, Classes = { "icon" }, Foreground = Palette.Accent, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = title.ToUpperInvariant(), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };
        var headingContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headingContent.Children.Add(sectionIcon); headingContent.Children.Add(label);
        var heading = new Button { Classes = { "ghost" }, Content = headingContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetName(heading, title.ToUpperInvariant());
        inspectorSections[title] = (content, action, heading, chevron);
        heading.Click += (_, _) => { SetSectionCollapsed(title, !collapsedSections.Contains(title)); SaveCollapsedSections(); };
        chevron.PointerPressed += (_, e) => { SetSectionCollapsed(title, !collapsedSections.Contains(title)); SaveCollapsedSections(); e.Handled = true; };
        Grid.SetColumn(heading, 1); header.Children.Add(heading);
        if (action != null) { Grid.SetColumn(action, 2); header.Children.Add(action); }
        if (on != null && power != null)
        {
            var toggle = new ToggleButton { Classes = { "chip", "inspector-power" }, MinHeight = 24, Padding = new Thickness(6, 2), FontSize = 10.5 };
            toggle.IsCheckedChanged += (_, _) => { if (!syncing) power(toggle.IsChecked == true); };
            Grid.SetColumn(toggle, 3); header.Children.Add(toggle);
            inspectorRefresh.Add(() =>
            {
                toggle.IsChecked = on();
                var powerLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                powerLabel.Children.Add(new TextBlock { Classes = { "icon" }, Text = "\uE7E8", FontSize = 11, Foreground = on() ? Palette.App : Palette.Faint });
                powerLabel.Children.Add(new TextBlock { Text = on() ? "ON" : "OFF", FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = on() ? Palette.App : Palette.Faint });
                toggle.Content = powerLabel;
                toggle.Background = on() ? Palette.Accent : Brushes.Transparent;
                toggle.Foreground = on() ? Palette.App : Palette.Faint;
                ToolTip.SetTip(toggle, (on() ? "Apagar " : "Prender ") + (title == "EQ" ? "el EQ" : title == "Saturación" ? "la saturación" : title == "Compresor" ? "el compresor" : title == "Reverb" ? "el envío a la reverb" : "el envío al delay"));
            });
        }
        outer.Children.Add(header); outer.Children.Add(content);
        takeSections.Children.Add(border);
        inspectorRefresh.Add(() => { bool enabled = on == null || on(); content.Opacity = enabled ? 1 : 0.4; sectionIcon.Foreground = enabled ? Palette.Accent : Palette.Faint; });
        SetSectionCollapsed(title, collapsedSections.Contains(title));
        return content;
    }
    InspectorSlider Slider(StackPanel host, string label, double min, double max, double step, double reset,
        Func<double> get, Action<double> set, Func<double, string> format, string? hint = null, Func<double>? maximum = null)
    {
        var slider = new InspectorSlider(label, min, max, step, reset, format, v => { if (!syncing) set(v); }, hint);
        host.Children.Add(slider); inspectorRefresh.Add(() => slider.Set(get(), maximum?.Invoke())); return slider;
    }
    void ClipEdit(Action<Clip, Media?> edit, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly || SelectedInspectorClip is not { } live) return;
        undo.Checkpoint(store.Project, $"clip:{live.Clip.Id}:{key}");
        edit(live.Clip, live.Media);
        engine.RefreshClipTiming();
        MarkDirty(); UpdateUndoButtons(); RefreshInspectorText();
        Timeline.LengthSec = tracks.Count == 0 ? 0 : tracks.Max(t => t.EndSec);
        Timeline.InvalidateVisual(); Preview.InvalidateVisual();
    }
    void CompEdit(Action<CompSettings> edit, string key, bool manual = false, bool recalculate = false) => Edit(t =>
    {
        if (SelectedInspectorClip is { } clip)
        {
            clip.Clip.Extra ??= [];
            clip.Clip.Extra.TryAdd("role", System.Text.Json.JsonSerializer.SerializeToElement(t.Channel.Comp.Preset == "lead" ? "solo" : "base"));
        }
        edit(t.Channel.Comp);
        if (manual) t.Channel.Comp.Preset = "custom";
        if (recalculate) RecalculateInspectorComp(t);
    }, "comp." + key);

    static void RecalculateInspectorComp(LiveTrack track)
    {
        double gr = track.RecalculateCompPreset();
        var c = track.Channel.Comp;
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
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*,Auto") };
        header.Children.Add(new Border { Background = Palette.Alpha(Palette.AccentColor, 0.14), CornerRadius = new CornerRadius(4), Child = takeNumber, Height = 24, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(takeName, 1); header.Children.Add(takeName);
        takeName.TextChanged += (_, _) => Edit(t => t.Track.Name = takeName.Text ?? "", "name");
        collapseEffects = InspectorIcon("\uE70E", "Colapsar todos los efectos", () =>
        {
            bool hide = !EffectTitles.All(collapsedSections.Contains);
            foreach (string title in EffectTitles) SetSectionCollapsed(title, hide);
            SaveCollapsedSections();
        });
        Grid.SetColumn(collapseEffects, 2); header.Children.Add(collapseEffects); UpdateCollapseButton();
        Inspector.Children.Add(header); Inspector.Children.Add(takeInfo);
        emptySections.Children.Add(Help("Con esta pista seleccionada, apretá R (o el botón de grabar) y la toma nueva queda en este lugar del timeline."));
        emptySections.Children.Add(Help("También podés agregar acá una toma del historial desde «Nueva pista»."));
        var removeEmpty = new Button { Content = "Quitar pista", Classes = { "chip" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        removeEmpty.Click += async (_, _) => await DeleteTimelineSelection(); emptySections.Children.Add(removeEmpty);
        Inspector.Children.Add(emptySections); Inspector.Children.Add(takeSections);
        var mix = Section("Mezcla", "\uE9E9", action: InspectorIcon("\uE74F", "Silenciar toma", () => Edit(t => t.Channel.Muted = !t.Channel.Muted, "mute")));
        Slider(mix, "Volumen", 0, 1.5, 0.01, 1, () => sel!.Channel.Volume, v => Edit(t => t.Channel.Volume = v, "volume"), v => $"{v * 100:0}%");
        Slider(mix, "Paneo", -1, 1, 0.05, 0, () => sel!.Channel.Pan, v => Edit(t => t.Channel.Pan = Math.Abs(v) < 0.04 ? 0 : v, "pan"), v => v == 0 ? "C" : $"{(v < 0 ? "L" : "R")} {Math.Abs(v) * 100:0}");
        var sat = Section("Saturación", "\uE945", () => sel!.Channel.Sat.On, on => Edit(t => t.Channel.Sat.On = on, "sat.on"));
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        sat.Children.Add(new Border { Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Left, Child = modes });
        foreach (var (id, text) in new[] { ("tube", "Válvula"), ("tape", "Cinta") })
        {
            var b = new ToggleButton { Content = text, Classes = { "seg" } };
            b.Click += (_, _) => Edit(t => { t.Channel.Sat.Mode = id; t.Channel.Sat.On = true; }, "sat.mode");
            inspectorRefresh.Add(() => b.IsChecked = sel!.Channel.Sat.Mode == id); modes.Children.Add(b);
        }
        Slider(sat, "Vida", 0, 1, 0.01, 0.3, () => sel!.Channel.Sat.Amount, v => Edit(t => { t.Channel.Sat.Amount = v; t.Channel.Sat.On = true; }, "sat.amount"),
            v => $"{(v < .2 ? "Apenas" : v < .45 ? "Sutil" : v < .7 ? "Con vida" : v < .9 ? "Caliente" : "Al palo")} · {v * 100:0}%",
            "Cuánto empuja la toma contra la válvula o la cinta. Se calibra con el nivel de la toma");
        var satHint = Help(""); sat.Children.Add(satHint);
        inspectorRefresh.Add(() => satHint.Text = sel!.Channel.Sat.Mode == "tape" ? "Compacta los golpes, suaviza los agudos ásperos y suma un poco de cuerpo en los graves." : "Armónicos pares: más cálido y redondo, los picos se ablandan sin perder brillo.");
        var comp = Section("Compresor", "\uE9D9", () => sel!.Channel.Comp.On, on => CompEdit(c => c.On = on, "on", recalculate: true),
            InspectorIcon("\uE777", "Volver al estilo por defecto", () => Edit(t =>
            {
                bool on = t.Channel.Comp.On;
                string preset = InspectorEditing.DefaultCompPreset(SelectedInspectorClip?.Clip, tracks.IndexOf(t) == 1 ? "lead" : "rhythm");
                t.Track.Channel.Comp = new CompSettings { On = on, Preset = preset }; RecalculateInspectorComp(t);
            }, "comp.reset")));
        comp.Children.Add(Help("¿Qué estás tocando?"));
        var presets = new UniformGrid { Columns = 3 };
        foreach (var (id, label) in new[] { ("rhythm", "Rasgueo"), ("arpeggio", "Arpegio"), ("lead", "Punteo") })
        {
            var b = new ToggleButton { Content = label, Classes = { "seg", "pick" }, Padding = new Thickness(3, 0), Margin = new Thickness(2, 0) };
            b.Click += (_, _) => CompEdit(c => { c.Preset = id; c.On = true; }, "preset", recalculate: true);
            inspectorRefresh.Add(() => b.IsChecked = sel!.Channel.Comp.Preset == id); presets.Children.Add(b);
        }
        comp.Children.Add(new Border { Classes = { "seg" }, Child = presets });
        var amountRow = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*"), Margin = new Thickness(0, 2) };
        var knob = new InspectorKnob(); knob.Changed += v => CompEdit(c => { c.Amount = v; c.On = true; }, "amount", recalculate: true);
        amountRow.Children.Add(knob);
        var amountInfo = new StackPanel { Spacing = 3, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        amountInfo.Children.Add(new TextBlock { Text = "CANTIDAD", Classes = { "label" } });
        var amountText = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold };
        var summary = new TextBlock { Classes = { "dim", "mono" }, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
        amountInfo.Children.Add(amountText); amountInfo.Children.Add(summary); Grid.SetColumn(amountInfo, 1); amountRow.Children.Add(amountInfo); comp.Children.Add(amountRow);
        var compHint = Help(""); comp.Children.Add(compHint);
        inspectorRefresh.Add(() =>
        {
            var c = sel!.Channel.Comp; knob.Set(c.Amount); knob.IsEnabled = c.Preset != "custom";
            amountText.Text = c.Preset == "custom" ? "Manual" : AmountLabel(c.Amount);
            summary.Text = c.Preset == "custom" ? "Ajuste fino a mano" : $"comprime ~{EstimatedCompression(c):0} dB"
                + (c.Eq.PresenceDb >= 1 ? $" · presencia +{c.Eq.PresenceDb:0.0} dB" : c.Eq.PresenceDb <= -1 ? $" · lugar al punteo {c.Eq.PresenceDb:0.0} dB" : "");
            ToolTip.SetTip(knob, c.Preset == "custom" ? "Valores a mano: elegí un estilo para volver al control simple" : $"Cantidad: {AmountLabel(c.Amount)}. Mueve todo junto (compresión, ataque, release, EQ) y mantiene el volumen parejo (arrastrá o usá la rueda · doble click: media)");
            compHint.Text = c.Preset switch { "rhythm" => "Rítmicas y riffs: empareja los golpes, conserva el ataque de la púa y deja lugar al punteo", "arpeggio" => "Arpegios y limpias: iguala las cuerdas, las notas suaves se escuchan y suena abierto", "lead" => "Solos y melodías: más sustain, notas parejas y presencia para que quede adelante", _ => "" };
        });
        fineButton.Click += (_, _) => { fineControls.IsVisible = !fineControls.IsVisible; fineButton.Content = fineControls.IsVisible ? "▾ Ajuste fino" : "▸ Ajuste fino"; };
        comp.Children.Add(fineButton); comp.Children.Add(fineControls);
        InspectorSlider Fine(string label, double min, double max, double step, double reset, Func<CompSettings, double> get, Action<CompSettings, double> set, Func<double, string> format, string? hint = null) =>
            Slider(fineControls, label, min, max, step, reset, () => get(sel!.Channel.Comp), v => CompEdit(c => set(c, v), label, manual: true), format, hint);
        Fine("Umbral", -60, 0, .5, -18, c => c.Threshold, (c, v) => c.Threshold = v, v => $"{v:0.0} dB");
        Fine("Ratio", 1, 20, .1, 4, c => c.Ratio, (c, v) => c.Ratio = v, v => $"{v:0.0}:1");
        Fine("Ataque", .1, 100, .1, 10, c => c.Attack * 1000, (c, v) => c.Attack = v / 1000, v => $"{v:0.0} ms");
        Fine("Release", 10, 1000, 5, 150, c => c.Release * 1000, (c, v) => c.Release = v / 1000, v => $"{v:0} ms");
        Fine("Knee", 0, 24, .5, 6, c => c.Knee, (c, v) => c.Knee = v, v => $"{v:0.0} dB");
        Fine("Ganancia", -12, 30, .5, 0, c => c.Makeup, (c, v) => c.Makeup = v, Db, "Make-up: compensa el volumen que saca la compresión");
        Fine("Corte de graves", 10, 300, 5, 10, c => c.Eq.HpfHz, (c, v) => c.Eq.HpfHz = v, v => v <= 10 ? "Off" : $"{v:0} Hz");
        Fine("Barro (280 Hz)", -6, 6, .5, 0, c => c.Eq.MudDb, (c, v) => c.Eq.MudDb = v, Db);
        var presence = Fine("Presencia (2,5 kHz)", -6, 6, .5, 0, c => c.Eq.PresenceDb, (c, v) => c.Eq.PresenceDb = v, Db, "Positivo: la guitarra pasa adelante. Negativo: le deja lugar a otra");
        inspectorRefresh.Add(() => presence.Label.Text = $"Presencia ({sel!.Channel.Comp.Eq.PresenceHz / 1000:0.0} kHz)");
        Fine("Brillo (9 kHz)", -6, 6, .5, 0, c => c.Eq.AirDb, (c, v) => c.Eq.AirDb = v, Db);
        var limit = new CheckBox { Content = "Limitar picos", FontSize = 12 };
        ToolTip.SetTip(limit, "Un limitador después del compresor frena golpes de púa y notas fuertes");
        limit.IsCheckedChanged += (_, _) => { if (!syncing) CompEdit(c => c.Limit = limit.IsChecked == true, "limit"); };
        inspectorRefresh.Add(() => limit.IsChecked = sel!.Channel.Comp.Limit); fineControls.Children.Add(limit);
        fineControls.Children.Add(Help("Al mover estos valores el compresor pasa a «Manual». Elegí un estilo para volver al control simple."));
        GrTrack.Child = GrBar;
        var eqHost = Section("EQ", "\uE9D9", () => sel!.Channel.Eq.On, on => Edit(t => t.Channel.Eq.On = on, "eq.on"));
        var eq = new EqEditor(); eq.Changed += value => Edit(t => t.Channel.Eq = value, "eq"); eqHost.Children.Add(eq);
        inspectorRefresh.Add(() => eq.Set(sel!.Channel.Eq));
        var reverb = Section("Reverb", "\uE767", () => sel!.Channel.ReverbOn, on => Edit(t => t.Channel.ReverbOn = on, "reverb.on"));
        Slider(reverb, "Envío a reverb", 0, 1, .01, 0, () => sel!.Channel.ReverbSend, v => Edit(t => t.Channel.ReverbSend = v, "reverb.send"), v => $"{v * 100:0}%", "Cuánto de esta pista va al bus de reverb (después del fader)");
        var delay = Section("Delay", "\uE823", () => sel!.Channel.DelayOn, on => Edit(t => t.Channel.DelayOn = on, "delay.on"));
        Slider(delay, "Envío a delay", 0, 1, .01, 0, () => sel!.Channel.DelaySend, v => Edit(t => { t.Channel.DelaySend = v; t.Channel.DelayOn = true; }, "delay.send"), v => $"{v * 100:0}%", "Cuánto de esta pista va al bus de delay (después del fader)");
        InitClipInspector();
    }

    void InitClipInspector()
    {
        Clip C() => SelectedInspectorClip!.Clip;
        Media? M() => SelectedInspectorClip!.Media;
        double Dur() => SelectedInspectorClip!.LengthSec;
        double Len() { var w = ClipEditing.Window(C(), M(), Dur()); return Math.Max(.01, w.End - w.Start); }
        var sync = Section("Sincronización", "\uE823");
        void SetSync(double v) => ClipEdit((c, m) => InspectorEditing.SetSync(c, m, v), "sync");
        var offset = Slider(sync, "Offset de audio", -250, 250, 1, 0, () => C().EffectiveSyncMs(M()), SetSync, Ms,
            "Corrige la sincronía del audio de esta toma (negativo = antes). Incluye la compensación de latencia; el imán del timeline la respeta");
        foreach (int delta in new[] { -5, 5 })
        {
            var b = new Button { Content = delta < 0 ? "−5" : "+5", Classes = { "chip", "mono" }, FontSize = 10.5, Padding = new Thickness(4, 0), MinHeight = 22 };
            b.Click += (_, _) => SetSync(C().EffectiveSyncMs(M()) + delta); offset.Trailing.Children.Add(b);
        }
        Slider(sync, "Latencia de cámara", -250, 250, 5, 80, () => C().EffectiveVideoOffsetMs(M()), v => ClipEdit((c, _) => c.VideoOffsetMs = v, "video-offset"), Ms,
            "Compensa el retraso de la cámara para que los dedos coincidan con el audio");
        var resetClip = InspectorIcon("\uE777", "Restaurar clip (sin recortes ni fades)", () => ClipEdit((c, _) => ClipEditing.Reset(store!.Project, c.Id), "reset"));
        var clip = Section("Clip", "\uE8C6", action: resetClip);
        Slider(clip, "Recorte inicio", 0, 10, .01, 0, () => C().TrimStartSec, v => ClipEdit((c, _) => c.TrimStartSec = v, "trim-start"), Sec,
            "Oculta el principio de la toma sin mover el resto", () => Math.Max(.01, Dur() - C().TrimEndSec - ClipEnvelope.MinClipSec));
        Slider(clip, "Recorte final", 0, 10, .01, 0, () => C().TrimEndSec, v => ClipEdit((c, _) => c.TrimEndSec = v, "trim-end"), Sec,
            maximum: () => Math.Max(.01, Dur() - C().TrimStartSec - ClipEnvelope.MinClipSec));
        ClipEnvelope.FadePair Audio() => ClipEnvelope.FitFades(C().FadeInSec, C().FadeOutSec, Len());
        Slider(clip, "Fade in (audio)", 0, 10, .01, 0, () => Audio().FadeIn, v => ClipEdit((c, _) => c.FadeInSec = v, "fade-in"), Sec, maximum: Len);
        Slider(clip, "Fade out (audio)", 0, 10, .01, 0, () => Audio().FadeOut, v => ClipEdit((c, _) => c.FadeOutSec = v, "fade-out"), Sec, maximum: Len);
        var link = new CheckBox { Content = "El video sigue los fades del audio", FontSize = 12 };
        ToolTip.SetTip(link, "El video funde a negro junto con el volumen");
        link.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            var audio = Audio(); ClipEdit((c, _) => { c.LinkVideoFades = link.IsChecked == true;
                if (!c.LinkVideoFades) { c.VideoFadeInSec = audio.FadeIn; c.VideoFadeOutSec = audio.FadeOut; } }, "link-fades");
        };
        clip.Children.Add(link);
        var video = new StackPanel { Spacing = 10 }; clip.Children.Add(video);
        ClipEnvelope.FadePair Video() => ClipEnvelope.FitFades(C().VideoFadeInSec, C().VideoFadeOutSec, Len());
        Slider(video, "Fade in (video)", 0, 10, .01, 0, () => Video().FadeIn, v => ClipEdit((c, _) => c.VideoFadeInSec = v, "video-fade-in"), Sec, maximum: Len);
        Slider(video, "Fade out (video)", 0, 10, .01, 0, () => Video().FadeOut, v => ClipEdit((c, _) => c.VideoFadeOutSec = v, "video-fade-out"), Sec, maximum: Len);
        inspectorRefresh.Add(() => { link.IsChecked = C().LinkVideoFades; video.IsVisible = !C().LinkVideoFades;
            resetClip.IsEnabled = C().TrimStartSec > 0 || C().TrimEndSec > 0 || C().FadeInSec > 0 || C().FadeOutSec > 0 || !C().LinkVideoFades && (C().VideoFadeInSec > 0 || C().VideoFadeOutSec > 0); });
        clip.Children.Add(Help("En el timeline: arrastrá el clip para moverlo, sus bordes para recortar y las esquinas de arriba para los fades."));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var mirror = InspectorIcon("\uE8AB", "Espejar horizontalmente", () => ClipEdit((c, _) => c.Framing.Mirror = !c.Framing.Mirror, "mirror"));
        actions.Children.Add(mirror); actions.Children.Add(InspectorIcon("\uE777", "Resetear encuadre", () => ClipEdit((c, _) => InspectorEditing.ResetFrame(c), "framing")));
        var frame = Section("Encuadre", "\uE7A8", action: actions);
        inspectorRefresh.Add(() => mirror.Foreground = C().Framing.Mirror ? Palette.Accent : Palette.Dim);
        Slider(frame, "Zoom", 1, 2.5, .01, 1, () => C().Framing.Zoom, v => ClipEdit((c, _) => InspectorEditing.Frame(c, zoom: v), "framing"), v => $"{v:0.00}×");
        frame.Children.Add(Help("Arrastrá el panel en el video para mover el encuadre; la rueda del mouse hace zoom."));
        var remove = new Button { Content = "Quitar del timeline", Classes = { "chip" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        remove.Click += async (_, _) => await DeleteTimelineSelection(); takeSections.Children.Add(remove);
        takeSections.Children.Add(Help("El archivo queda guardado; la volvés a usar desde Historial."));
    }

    void ShowTrack(LiveTrack? t)
    {
        sel = t;
        Inspector.IsEnabled = t != null && !restoring && store?.ReadOnly != true;
        if (inspectorTrack != t?.Track.Id)
        {
            inspectorTrack = t?.Track.Id;
            fineControls.IsVisible = t?.Channel.Comp.Preset == "custom";
        }
        grShown = 0; RefreshInspectorText();
    }
    void RefreshInspectorText()
    {
        syncing = true;
        try
        {
            takeName.Text = sel?.Track.Name ?? "Seleccioná una toma";
            takeNumber.Text = sel == null ? "" : (tracks.IndexOf(sel) + 1).ToString();
            var c = SelectedInspectorClip;
            takeInfo.Text = sel == null ? (tracks.Count == 0 ? "Todavía no hay tomas grabadas." : "Hacé click en una pista o en su panel del video.")
                : c == null ? "Pista vacía" : c.Offline ? "Sin conexión: falta el archivo de audio"
                : $"{c.LengthSec:0.0} s · WAV {c.Audio!.SampleRate / 1000.0:0} kHz estéreo";
            emptySections.IsVisible = sel != null && c == null;
            takeSections.IsVisible = c != null;
            if (collapseEffects != null) collapseEffects.IsVisible = c != null;
            if (sel != null && c != null) foreach (var refresh in inspectorRefresh) refresh();
        }
        finally { syncing = false; }
        RefreshMixer();
    }
}

