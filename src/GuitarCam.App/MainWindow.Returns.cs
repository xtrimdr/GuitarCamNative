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

/// <summary>
/// The returns the user adds (native only, <see cref="ProjectReturns"/>) and the sends to them: a return strip per extra
/// return next to the fixed Reverb and Delay (name, its insert slots with the same panels as a track's, where a reverb
/// or a delay is one more slot, fader, mute and meter), "+ Retorno", the send panels of the wide track strip and the
/// Canal tab's send sections, and the PRE chip every send has (the fixed reverb's and delay's too). Every edit is an undo
/// step (per control, like the strips), heard at once through <see cref="Audio.PlaybackEngine.SetReturns"/>.
/// </summary>
public partial class MainWindow
{
    IReadOnlyList<ReturnBus> ExtraReturns => store?.Project.Settings.Returns ?? [];
    ReturnBus? ReturnById(string id) => ExtraReturns.FirstOrDefault(r => r.Id == id);

    /// <summary>An edit of one extra return (its name, fader, mute or slots): one undo step per key, heard and saved.</summary>
    void ReturnEdit(string id, Action<ReturnBus> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly || ReturnById(id) is not { } r) return;
        undo.Checkpoint(store.Project, $"return:{id}:{key}");
        change(r);
        ReturnsChanged();
    }

    /// <summary>Adds or removes returns (and the sends that go with them): always its own undo step.</summary>
    void ReturnsEdit(Func<Project, bool> change)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project);
        if (!change(store.Project)) return;
        ReturnsChanged();
    }

    void ReturnsChanged()
    {
        engine.SetReturns(store!.Project, tracks);
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        RefreshMixer();
    }

    /// <summary>A return's slots, for the panels (<see cref="SlotPanels"/>): its "+ Efecto" also offers reverb and delay.</summary>
    SlotOwner SlotsOf(ReturnBus ret)
    {
        string id = ret.Id;
        ReturnBus R() => ReturnById(id) ?? ret;
        return new()
        {
            Slots = () => R().Inserts,
            Edit = (change, key) => ReturnEdit(id, x => change(x.Inserts), key),
            Recalculate = c => RecalculateReturnComp(id, c),
            AddTip = Loc.T("mix.slot.add.return.tip"),
            ReturnKinds = true,
        };
    }

    /// <summary>
    /// A return compressor's preset, measured against the first track that sends to it (or any track with audio): a
    /// return has no take of its own; with none, its values stay as they are.
    /// </summary>
    void RecalculateReturnComp(string id, CompSettings c)
    {
        var env = (tracks.FirstOrDefault(t => t.Channel.SendTo(id) is { On: true, Level: > 0 } && t.Envelope != null)
            ?? tracks.FirstOrDefault(t => t.Envelope != null))?.Envelope;
        if (env == null) return;
        double gr = ChannelDsp.RecalculateComp(c, env);
        c.Extra ??= [];
        c.Extra["estGrDb"] = System.Text.Json.JsonSerializer.SerializeToElement(gr);
    }

    /// <summary>The icon of a return: its first reverb or delay slot's.</summary>
    static string ReturnIcon(ReturnBus r) =>
        r.Inserts.FirstOrDefault(s => s.Type is InsertSlot.ReverbKind or InsertSlot.DelayKind)?.Type == InsertSlot.DelayKind ? FxIcon.Delay : FxIcon.Reverb;

    /// <summary>One line on what a return does: its first reverb's or delay's preset and length, or how many effects it has.</summary>
    string ReturnInfo(ReturnBus r)
    {
        var first = r.Inserts.FirstOrDefault(s => s.Type is InsertSlot.ReverbKind or InsertSlot.DelayKind);
        if (first?.Reverb is { } rv) return ReverbInfo(rv);
        if (first?.Delay is { } d) return DelayInfo(d);
        int n = r.Inserts.Count(s => s.Known);
        return n == 0 ? Loc.T("mix.return.noFx") : n == 1 ? Loc.T("mix.return.fx.one") : Loc.T("mix.return.fx", n);
    }

    static string ReverbInfo(ReverbSettings r)
    {
        string p = ReverbBus.PresetOf(ToParams(r));
        return $"{(p == "" ? Loc.T("mix.custom.f") : BusPresetName("reverb", p))} · {r.Decay:0.0} s";
    }

    static string DelayInfo(DelaySettings d)
    {
        var dp = ToParams(d);
        string p = DelayBus.PresetOf(dp);
        return $"{(p == "" ? Loc.T("mix.custom.m") : BusPresetName("delay", p))} · {DelayBus.TimeLabel(dp)}";
    }

    // --- the PRE chip -----------------------------------------------------------------------------------

    /// <summary>A send's PRE chip: lit = taken before the fader (after the effects and the pan). Its tooltip says what that means.</summary>
    Button PreChip(Func<bool> pre, Action toggle, List<Action> refresh)
    {
        var b = new Button { Content = "PRE", Classes = { "chip", "mono" }, FontSize = 10, FontWeight = FontWeight.Bold, Height = 24, MinHeight = 24, Padding = new Thickness(5, 0),
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Focusable = false };
        b.Click += (_, _) => toggle();
        refresh.Add(() =>
        {
            bool lit = pre();
            b.Classes.Set("lit", lit);
            b.Background = lit ? Palette.AccentSoft : Brushes.Transparent;
            b.Foreground = lit ? Palette.Accent : Palette.Faint;
            b.BorderBrush = lit ? Palette.Alpha(Palette.AccentColor, 0.6) : Palette.Line;
            ToolTip.SetTip(b, Loc.T(lit ? "send.pre.on.tip" : "send.pre.off.tip"));
        });
        return b;
    }

    // --- the wide track strip's sends -------------------------------------------------------------------

    /// <summary>The send panel of an extra return: knob and %, the return's name and what it does, ON and PRE.</summary>
    void ExtraSendPanel(StackPanel fx, LiveTrack t, ReturnBus ret)
    {
        string id = ret.Id;
        (double Level, bool On, bool Pre) S() => ProjectReturns.SendOf(t.Channel, id);
        void Set(double? level = null, bool? on = null, bool? pre = null, string key = "level") =>
            EditTrack(t, x => ProjectReturns.SetSend(store!.Project, x.Channel, id, level, on, pre), $"send.{id}.{key}");
        var preChip = PreChip(() => S().Pre, () => Set(pre: !S().Pre, key: "pre"), mx.Refresh);
        var panel = FxPanel(fx, ret.Name, ReturnIcon(ret), () => S().On, () => Set(on: !S().On, key: "on"),
            on => Loc.T(on ? "send.power.off" : "send.power.on", ReturnById(id)?.Name ?? ret.Name), preChip);
        var knob = new Knob(34, Loc.T("send.knob.tip", ret.Name)) { Min = 0, Max = 1, ResetTo = 0 };
        // like the reverb's knob: turning it up turns the send on
        knob.Changed += v => Set(level: v, on: v > 0 ? true : null);
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var pct = new TextBlock { Classes = { "mono" }, FontSize = 14, FontWeight = FontWeight.SemiBold };
        var info = new TextBlock { FontSize = 11, Foreground = Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(info, Loc.T("send.info.tip"));
        side.Children.Add(pct); side.Children.Add(info);
        mx.Refresh.Add(() =>
        {
            double level = S().Level;
            knob.Value = level;
            pct.Text = $"{level * 100:0}%";
            info.Text = ReturnById(id) is { } r ? ReturnInfo(r) : "";
        });
        panel.Body.Children.Add(Row(knob, side));
    }

    // --- return strips ----------------------------------------------------------------------------------

    /// <summary>
    /// An extra return's strip: its name (⋯ → Renombrar / Quitar retorno), mute, its slots' panels with "+ Efecto",
    /// fader and meter (Level and RMS, like every strip).
    /// </summary>
    Control ExtraReturn(ReturnBus ret, bool wide)
    {
        string id = ret.Id;
        ReturnBus R() => ReturnById(id) ?? ret;
        var box = StripCard(wide ? ReturnWideW : ReturnCompactW);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Height = 28 };
        var iconMark = FxIcon.Create(ReturnIcon(ret), 12);
        FxIcon.SetColor(iconMark, Palette.Accent); iconMark.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(iconMark);
        var name = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 6, 0) };
        name.DoubleTapped += (_, _) => RenameReturn(id, name);
        Grid.SetColumn(name, 1); head.Children.Add(name);
        var mute = StripToggle("M", Loc.T("mix.return.mute"), 24, () => R().Muted, false, () => ReturnEdit(id, r => r.Muted = !r.Muted, "muted"));
        mute.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(mute, 2); head.Children.Add(mute);
        var menu = new Button { Classes = { "ghost" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Focusable = false, Margin = new Thickness(2, 0, 0, 0),
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        ToolTip.SetTip(menu, Loc.T("mix.return.menu.tip"));
        menu.Click += (_, _) =>
        {
            if (PressClosedMenuOf(menu)) return;
            OpenAnchoredMenu(Popup(
            [
                MenuAction(Loc.T("mix.return.rename"), () => { RenameReturn(id, name); return Task.CompletedTask; }, icon: ""),
                new Separator(),
                MenuAction(Loc.T("mix.return.remove"), () => { ReturnsEdit(p => ProjectReturns.Remove(p, id)); return Task.CompletedTask; }, danger: true, icon: "",
                    tip: Loc.T("mix.return.remove.tip")),
            ], 220), menu);
        };
        Grid.SetColumn(menu, 3); head.Children.Add(menu);
        mx.Refresh.Add(() => { name.Text = R().Name; ToolTip.SetTip(name, Loc.T("mix.return.name.tip", R().Name)); });

        var fader = new Fader();
        fader.Changed += v => ReturnEdit(id, r => r.Volume = v, "volume");
        var db = new TextBlock { Classes = { "mono" }, FontSize = wide ? 11 : 10, Foreground = Palette.Dim };
        mx.Refresh.Add(() => { fader.Value = R().Volume; db.Text = $"{Fader.Db(R().Volume)} dB"; });
        var meter = new MixerMeter();
        mx.ReturnMeters.Add((id, meter));
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { fader, meter } };

        var fx = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 4, 6) };
        wideGr = null;
        SlotPanels(fx, SlotsOf(ret));
        wideGr = null;
        var controls = new ScrollViewer { Content = fx, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        box.Child = StripLayout(wide, head, controls, faders, db);
        return box;
    }

    void RenameReturn(string id, Control anchor)
    {
        if (ReturnById(id) is not { } r || store?.ReadOnly != false) return;
        AskName(anchor, null, Loc.T("mix.return.rename"), r.Name, Loc.T("folder.rename.ok"), n => ReturnEdit(id, x => x.Name = n.Trim(), "name"));
    }

    /// <summary>"+ Retorno" after the returns: a menu with a new reverb or a new delay return.</summary>
    Control AddReturnButton(bool wide)
    {
        var label = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = Loc.T("mix.return.add"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center } } };
        var b = new Button { Classes = { "chip" }, Content = label, Width = 64, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(StripGap / 2, StripGap), Focusable = false };
        ToolTip.SetTip(b, Loc.T("mix.return.add.tip"));
        b.Click += (_, _) =>
        {
            if (PressClosedMenuOf(b)) return;
            MenuItem Kind(string kind, string title, string hint)
            {
                var icon = FxIcon.Create(kind, 12);
                icon.VerticalAlignment = VerticalAlignment.Center;
                FxIcon.SetColor(icon, Palette.Accent);
                var item = new MenuItem { Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { icon, new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
                ToolTip.SetTip(item, hint);
                ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
                item.Click += (_, _) => ReturnsEdit(p => ProjectReturns.Add(p, kind) != null);
                return item;
            }
            OpenAnchoredMenu(Popup(
            [
                Kind(InsertSlot.ReverbKind, Loc.T("mix.return.add.reverb"), Loc.T("mix.return.add.reverb.tip")),
                Kind(InsertSlot.DelayKind, Loc.T("mix.return.add.delay"), Loc.T("mix.return.add.delay.tip")),
            ], 220), b);
        };
        return b;
    }

    /// <summary>Called from the mixer's tick: the extra returns' meters.</summary>
    void TickReturns()
    {
        foreach (var (id, meter) in mx.ReturnMeters)
        {
            if (engine.Return(id) is not { } r) continue;
            meter.Level = r.Show;
            meter.Rms = Math.Sqrt(r.Rms.MeanSq);
        }
    }

    /// <summary>The extra returns' peaks (with the same fall-off as the tracks') and RMS, once per tick, shown or not.</summary>
    void TakeReturnsRms(double dt)
    {
        // a for over the list itself: the tick doesn't allocate (an interface foreach boxes its enumerator)
        var list = store?.Project.Settings.Returns;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            if (engine.Return(list[i].Id) is not { } r) continue;
            r.Rms.Take(dt);
            r.Show = Math.Max(Math.Max(r.MeterL, r.MeterR), r.Show * 0.82f);
            r.MeterL = r.MeterR = 0;
            r.GrDb = 0;
        }
    }

    // --- reverb and delay slots (in a return's chain) ---------------------------------------------------

    static readonly ReverbSettings GoneReverb = new();
    static readonly DelaySettings GoneDelay = new();

    void ReverbSlotPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu, SlotDrag drag)
    {
        ReverbSettings V() => o.Slot(id)?.Reverb ?? GoneReverb;
        void Set(Action<ReverbSettings> edit, string key) => SlotEdit(o, id, s => edit(s.Reverb!), "reverb." + key);
        var panel = FxPanel(fx, SlotTitle("Reverb", nth), FxIcon.Reverb, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, s => s.On = !s.On, "reverb.on"),
            on => Loc.T(on ? "mix.slot.reverb.off" : "mix.slot.reverb.on"), menu, drag, id);
        var defaults = new ReverbSettings();
        var presets = StripSelect([]);
        ToolTip.SetTip(presets, Loc.T("mix.reverb.presets.tip"));
        presets.Selected += i =>
        {
            int k = i - (ReverbBus.PresetOf(ToParams(V())) == "" ? 1 : 0);
            if (k < 0) return;
            string label = ReverbBus.Presets[k].Label;
            Set(r =>
            {
                var p = ReverbBus.ApplyPreset(ToParams(r), label);
                r.Decay = p.Decay; r.PreDelayMs = p.PreDelayMs; r.Tone = p.Tone; r.LowCut = p.LowCut; r.Width = p.Width;
            }, "preset");
        };
        mx.Refresh.Add(() =>
        {
            string p = ReverbBus.PresetOf(ToParams(V()));
            presets.SetItems((p == "" ? new[] { Loc.T("mix.custom.f") } : []).Concat(ReverbBus.Presets.Select(x => BusPresetName("reverb", x.Label))).ToArray(),
                (p == "" ? new string?[] { null } : []).Concat(ReverbBus.Presets.Select(x => (string?)BusPresetHint("reverb", x.Label, x.Hint))).ToArray());
            presets.SelectedIndex = p == "" ? 0 : Array.FindIndex(ReverbBus.Presets, x => x.Label == p);
        });
        var knobs = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.decay"), 0, 1, DecayMap.To(defaults.Decay), () => DecayMap.To(V().Decay), () => $"{V().Decay:0.0}s",
            p => Set(r => r.Decay = DecayMap.From(p), "decay"), () => Loc.T("mix.hint.reverb.decay")));
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.pre"), 0, 1, defaults.PreDelayMs / 200, () => V().PreDelayMs / 200, () => $"{Math.Round(V().PreDelayMs):0}",
            p => Set(r => r.PreDelayMs = Math.Round(p * 200), "preDelayMs"), () => Loc.T("mix.hint.reverb.pre")));
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.tone"), 0, 1, ToneMap.To(defaults.Tone), () => ToneMap.To(V().Tone), () => FmtHz(V().Tone),
            p => Set(r => r.Tone = Math.Round(ToneMap.From(p)), "tone"), () => Loc.T("mix.hint.reverb.tone")));
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.lowcut"), 0, 1, LowCutMap.To(defaults.LowCut), () => LowCutMap.To(V().LowCut), () => FmtHz(V().LowCut),
            p => Set(r => r.LowCut = Math.Round(LowCutMap.From(p)), "lowCut"), () => Loc.T("mix.hint.reverb.lowcut")));
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.width"), 0, 1, defaults.Width, () => V().Width, () => $"{V().Width * 100:0}%",
            v => Set(r => r.Width = v, "width"), () => Loc.T("mix.hint.reverb.width")));
        panel.Body.Children.Add(presets);
        panel.Body.Children.Add(knobs);
    }

    void DelaySlotPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu, SlotDrag drag)
    {
        DelaySettings V() => o.Slot(id)?.Delay ?? GoneDelay;
        void Set(Action<DelaySettings> edit, string key) => SlotEdit(o, id, s => edit(s.Delay!), "delay." + key);
        double Bpm() => store?.Project.Settings.Bpm ?? 110;
        var panel = FxPanel(fx, SlotTitle("Delay", nth), FxIcon.Delay, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, s => s.On = !s.On, "delay.on"),
            on => Loc.T(on ? "mix.slot.delay.off" : "mix.slot.delay.on"), menu, drag, id);
        var defaults = new DelaySettings();
        var presets = StripSelect([]);
        ToolTip.SetTip(presets, Loc.T("mix.delay.presets.tip"));
        presets.Selected += i =>
        {
            int k = i - (DelayBus.PresetOf(ToParams(V())) == "" ? 1 : 0);
            if (k < 0) return;
            string label = DelayBus.Presets[k].Label;
            Set(d =>
            {
                var p = DelayBus.ApplyPreset(ToParams(d), label);
                d.Mode = DelayParams.ModeToString(p.Mode); d.Sync = p.Sync; d.Division = p.Division; d.TimeMs = p.TimeMs;
                d.Feedback = p.Feedback; d.Tone = p.Tone; d.LowCut = p.LowCut; d.Width = p.Width; d.Duck = p.Duck;
            }, "preset");
        };
        mx.Refresh.Add(() =>
        {
            string p = DelayBus.PresetOf(ToParams(V()));
            presets.SetItems((p == "" ? new[] { Loc.T("mix.custom.m") } : []).Concat(DelayBus.Presets.Select(x => BusPresetName("delay", x.Label))).ToArray(),
                (p == "" ? new string?[] { null } : []).Concat(DelayBus.Presets.Select(x => (string?)BusPresetHint("delay", x.Label, x.Hint))).ToArray());
            presets.SelectedIndex = p == "" ? 0 : Array.FindIndex(DelayBus.Presets, x => x.Label == p);
        });
        // mode, then the note value (or "Libre (ms)") under it
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, label, tip) in new[]
        {
            ("stereo", Loc.T("mix.delay.stereo.short"), Loc.T("mix.delay.stereo.tip")),
            ("pingpong", Loc.T("mix.delay.pingpong.short"), Loc.T("mix.delay.pingpong.tip")),
        })
        {
            var b = new ToggleButton { Classes = { "seg", "pick" }, Content = label, FontSize = 10, Height = 22, MinHeight = 22, Padding = new Thickness(6, 0) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Set(d => d.Mode = mode, "mode");
            mx.Refresh.Add(() => b.IsChecked = V().Mode == mode);
            modes.Children.Add(b);
        }
        var division = StripSelect(DelayBus.Divisions.Select(d => Loc.Or("mix.div." + d.Id, d.Label)).Append(Loc.T("mix.delay.free")).ToArray());
        division.Margin = new Thickness(0, 6, 0, 0);
        division.Selected += i =>
        {
            if (i == DelayBus.Divisions.Length)
                Set(d => { d.TimeMs = Math.Round(DelayBus.DelayTimeSec(ToParams(d), Bpm()) * 1000); d.Sync = false; }, "sync,timeMs");
            else
            {
                string div = DelayBus.Divisions[i].Id;
                Set(d => { d.Sync = true; d.Division = div; }, "division,sync");
            }
        };
        mx.Refresh.Add(() =>
        {
            var d = V();
            division.SelectedIndex = d.Sync ? Math.Max(0, Array.FindIndex(DelayBus.Divisions, x => x.Id == d.Division)) : DelayBus.Divisions.Length;
            ToolTip.SetTip(division, Loc.T("mix.delay.time.tip", $"{Math.Round(DelayBus.DelayTimeSec(ToParams(d), Bpm()) * 1000):0}", d.Sync ? Loc.T("mix.delay.at", $"{Bpm():0.##}") : ""));
        });
        var knobs = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
        var time = MiniKnob(Loc.T("mix.knob.time"), 0, 1, DelayMsMap.To(defaults.TimeMs), () => DelayMsMap.To(V().TimeMs), () => $"{Math.Round(V().TimeMs):0}",
            p => Set(d => d.TimeMs = Math.Round(DelayMsMap.From(p)), "timeMs"), () => Loc.T("mix.hint.delay.time"));
        mx.Refresh.Add(() => time.IsVisible = !V().Sync);
        knobs.Children.Add(time);
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.repeats"), 0, 1, defaults.Feedback / 0.9, () => V().Feedback / 0.9, () => $"{V().Feedback * 100:0}%",
            p => Set(d => d.Feedback = Math.Round(p * 90) / 100, "feedback"), () => Loc.T("mix.hint.delay.repeats")));
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.tone"), 0, 1, ToneMap.To(defaults.Tone), () => ToneMap.To(V().Tone), () => FmtHz(V().Tone),
            p => Set(d => d.Tone = Math.Round(ToneMap.From(p)), "tone"), () => Loc.T("mix.hint.delay.tone")));
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.lowcut"), 0, 1, LowCutMap.To(defaults.LowCut), () => LowCutMap.To(V().LowCut), () => FmtHz(V().LowCut),
            p => Set(d => d.LowCut = Math.Round(LowCutMap.From(p)), "lowCut"), () => Loc.T("mix.hint.delay.lowcut")));
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.width"), 0, 1, defaults.Width, () => V().Width, () => $"{V().Width * 100:0}%",
            v => Set(d => d.Width = v, "width"), () => Loc.T("mix.hint.delay.width")));
        knobs.Children.Add(MiniKnob(Loc.T("mix.knob.duck"), 0, 1, defaults.Duck, () => V().Duck, () => $"{V().Duck * 100:0}%",
            v => Set(d => d.Duck = v, "duck"), () => Loc.T("mix.hint.delay.duck")));
        panel.Body.Children.Add(presets);
        panel.Body.Children.Add(new Border { Classes = { "seg" }, Child = modes, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
        panel.Body.Children.Add(division);
        panel.Body.Children.Add(knobs);
    }

    // --- the Canal tab's send sections ------------------------------------------------------------------

    /// <summary>
    /// The Canal tab's sections for the extra returns' sends (after Reverb and Delay), one per return: level, ON and
    /// PRE. Rebuilt only when the track or the list of returns (or a name) changes; every other edit just refreshes them.
    /// </summary>
    void RebuildInspectorSends(LiveTrack t)
    {
        string key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t)}|" + string.Join("/", ExtraReturns.Select(r => r.Id + ":" + r.Name));
        if (key == inspectorSendsKey) return;
        inspectorSendsKey = key;
        foreach (string title in sendSections) inspectorSections.Remove(title);
        sendSections.Clear();
        sendRefresh.Clear();
        inspectorSends.Children.Clear();
        foreach (var ret in ExtraReturns)
        {
            string id = ret.Id, title = "ret:" + id;
            SendTitles[title] = ret.Name;
            (double Level, bool On, bool Pre) S() => sel == null ? (0, true, false) : ProjectReturns.SendOf(sel.Channel, id);
            void Set(double? level = null, bool? on = null, bool? pre = null, string k = "level") =>
                Edit(x => ProjectReturns.SetSend(store!.Project, x.Channel, id, level, on, pre), $"send.{id}.{k}");
            var pre = PreChip(() => S().Pre, () => Set(pre: !S().Pre, k: "pre"), sendRefresh);
            var section = Section(title, ReturnIcon(ret), () => S().On, on => Set(on: on, k: "on"), pre, inspectorSends, sendRefresh);
            sendSections.Add(title);
            EffectKnob(section, Loc.T("insp.send.level"), 0, () => S().Level, v => Set(level: v, on: v > 0 ? true : null), v => $"{v * 100:0}%", SendDb,
                Loc.T("insp.send.hint", ret.Name), sendRefresh);
        }
        UpdateCollapseButton();
    }

    readonly StackPanel inspectorSends = new();
    readonly List<Action> sendRefresh = [];
    readonly List<string> sendSections = [];
    string inspectorSendsKey = "";
    /// <summary>The send sections' shown titles (the returns' names), by their stable id "ret:&lt;return id&gt;".</summary>
    static readonly Dictionary<string, string> SendTitles = [];
}
