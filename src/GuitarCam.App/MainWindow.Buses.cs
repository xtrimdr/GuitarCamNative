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
/// The buses (native only, <see cref="ProjectBuses"/>) and where every track, folder and bus sends its sound: a strip per
/// bus (name, mute, its insert slots with the same panels as a track's, where a reverb or a delay is one more slot, pan,
/// output, its own sends, fader and meter), the Salida selector and the send panels every strip has, the
/// Canal tab's output and send sections, and the PRE chip every send has (but a folder's: it has no fader). Every edit is
/// an undo step (per control, like the strips), heard at once through <see cref="Audio.PlaybackEngine.SetBuses"/>.
/// </summary>
public partial class MainWindow
{
    IReadOnlyList<Bus> Buses => store?.Project.Settings.Buses ?? [];
    Bus? BusById(string id) => Buses.FirstOrDefault(b => b.Id == id);

    /// <summary>An edit of one bus (its name, fader, pan, mute, output, sends or slots): one undo step per key, heard and saved.</summary>
    void BusEdit(string id, Action<Bus> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly || BusById(id) is not { } b) return;
        undo.Checkpoint(store.Project, $"bus:{id}:{key}");
        change(b);
        RoutingChanged();
    }

    /// <summary>Adds or removes buses (and the outputs and sends that go with them): always its own undo step.</summary>
    void BusesEdit(Func<Project, bool> change)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project);
        if (!change(store.Project)) return;
        RoutingChanged();
    }

    void RoutingChanged()
    {
        ApplyRouting(store!.Project, tracks);
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        RefreshMixer();
        // the headers show a knob per bus (its name and icon too)
        Timeline.InvalidateVisual();
    }

    /// <summary>Hands the engine the folders and buses and every track its output and sends (<see cref="Audio.PlaybackEngine.SetBuses"/>).</summary>
    void ApplyRouting(Project p, IReadOnlyList<LiveTrack> list) => engine.SetBuses(p, list);

    /// <summary>
    /// Whose output and sends a control edits: a track's channel, a folder or a bus. The output selector and the send
    /// panels only know this, so every strip shows them the same way.
    /// </summary>
    sealed class Router
    {
        /// <summary>The object as it is now (it changes with every undo); null once it is gone.</summary>
        public required Func<IRouted?> Get { get; init; }
        /// <summary>An edit: one undo step per key, heard at once, saved. The function says whether anything changed.</summary>
        public required Action<Func<Project, IRouted, bool>, string> Edit { get; init; }
        /// <summary>A folder: no fader, so no PRE.</summary>
        public bool Folder { get; init; }
        /// <summary>A bus's own id: it never sends to itself.</summary>
        public string? BusId { get; init; }
    }

    Router RouterOf(LiveTrack t) => new()
    {
        Get = () => t.Channel,
        Edit = (change, key) =>
        {
            bool output = key == "output";
            EditTrack(t, x => change(store!.Project, x.Channel), key);
            // the sends reach the engine with the channel; where the output goes, through the routing
            if (output && store != null) ApplyRouting(store.Project, tracks);
            RefreshMixer();
        },
    };

    Router RouterOf(Folder folder)
    {
        string id = folder.Id;
        return new()
        {
            Get = () => FolderById(id),
            Edit = (change, key) => { if (FolderById(id) is { } f) FolderEdit(f, x => change(store!.Project, x), key); },
            Folder = true,
        };
    }

    Router RouterOf(Bus bus)
    {
        string id = bus.Id;
        return new() { Get = () => BusById(id), Edit = (change, key) => BusEdit(id, b => change(store!.Project, b), key), BusId = id };
    }

    /// <summary>A bus's slots, for the panels (<see cref="SlotPanels"/>): its "+ Efecto" also offers reverb and delay.</summary>
    SlotOwner SlotsOf(Bus bus)
    {
        string id = bus.Id;
        Bus B() => BusById(id) ?? bus;
        return new()
        {
            Slots = () => B().Inserts,
            Edit = (change, key) => BusEdit(id, x => change(x.Inserts), key),
            Recalculate = c => RecalculateBusComp(id, c),
            AddTip = Loc.T("mix.slot.add.bus.tip"),
            ReturnKinds = true,
        };
    }

    /// <summary>
    /// A bus compressor's preset, measured against the first track that reaches it (or any track with audio): a bus has
    /// no take of its own; with none, its values stay as they are.
    /// </summary>
    void RecalculateBusComp(string id, CompSettings c)
    {
        var env = (tracks.FirstOrDefault(t => (ChannelDsp.TrackOutput(t.Track) == id || t.Channel.SendTo(id) is { On: true, Level: > 0 }) && t.Envelope != null)
            ?? tracks.FirstOrDefault(t => t.Envelope != null))?.Envelope;
        if (env == null) return;
        double gr = ChannelDsp.RecalculateComp(c, env);
        c.Extra ??= [];
        c.Extra["estGrDb"] = System.Text.Json.JsonSerializer.SerializeToElement(gr);
    }

    /// <summary>Counts every edit (<see cref="MarkDirty"/>): what is cached from the project is made again when it moves.</summary>
    int editStamp;
    /// <summary>Each track's send knobs for the timeline (<see cref="SendKnobsOf"/>) and the edit they were made at.</summary>
    readonly Dictionary<LiveTrack, (int Stamp, SendKnob[] Knobs)> sendKnobs = [];

    /// <summary>
    /// The sends a track's header shows as knobs: the buses it has (<see cref="ProjectBuses.Shown"/>), with their name
    /// and icon. Asked while the timeline draws, so it is made again only after an edit.
    /// </summary>
    SendKnob[] SendKnobsOf(LiveTrack t)
    {
        if (sendKnobs.TryGetValue(t, out var c) && c.Stamp == editStamp) return c.Knobs;
        if (sendKnobs.Count > tracks.Count * 2 + 8) sendKnobs.Clear();
        SendKnob[] knobs = store == null ? [] : [.. ProjectBuses.Shown(store.Project, t.Channel).Select(b => new SendKnob(b.Id, b.Name, BusIcon(b)))];
        sendKnobs[t] = (editStamp, knobs);
        return knobs;
    }

    /// <summary>The icon of a bus: its first reverb or delay slot's, or the bus glyph's stand-in (the reverb's waves) without one.</summary>
    static string BusIcon(Bus b) =>
        b.Inserts.FirstOrDefault(s => s.Type is InsertSlot.ReverbKind or InsertSlot.DelayKind)?.Type == InsertSlot.DelayKind ? FxIcon.Delay : FxIcon.Reverb;

    /// <summary>One line on what a bus does: its first reverb's or delay's preset and length, or how many effects it has.</summary>
    string BusInfo(Bus b)
    {
        var first = b.Inserts.FirstOrDefault(s => s.Type is InsertSlot.ReverbKind or InsertSlot.DelayKind);
        if (first?.Reverb is { } rv) return ReverbInfo(rv);
        if (first?.Delay is { } d) return DelayInfo(d);
        int n = b.Inserts.Count(s => s.Known);
        return n == 0 ? Loc.T("mix.bus.noFx") : n == 1 ? Loc.T("mix.bus.fx.one") : Loc.T("mix.bus.fx", n);
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

    /// <summary>The buses <paramref name="r"/> can send to or route into: all but itself and those its sound would come back through.</summary>
    List<Bus> Targets(Router r) => store == null || r.Get() is not { } x ? [] : [.. Buses.Where(b => ProjectBuses.CanFeed(store.Project, x, b.Id))];

    /// <summary>The buses <paramref name="r"/> shows a send to: its list, without the ones taken out (<see cref="ProjectBuses.Shown"/>).</summary>
    List<Bus> SendTargets(Router r) => store == null || r.Get() is not { } x ? [] : ProjectBuses.Shown(store.Project, x);

    /// <summary>
    /// The buses <paramref name="r"/> can send to, each with a check when it is in its list (asked by the user: not every
    /// track wants every bus); a click takes it out (off, its level kept) or adds it back. The same menu in the mixer's
    /// "+ Envío", the inspector and a track's right click in the timeline.
    /// </summary>
    List<object> SendChoices(Router r)
    {
        var items = new List<object>();
        var shown = SendTargets(r);
        foreach (var b in Targets(r))
        {
            string id = b.Id;
            bool on = shown.Contains(b);
            var check = new TextBlock { Classes = { "icon" }, Text = "\ue73e", FontSize = 12, Foreground = Palette.Accent, Opacity = on ? 1 : 0, VerticalAlignment = VerticalAlignment.Center };
            var icon = FxIcon.Create(BusIcon(b), 12);
            icon.VerticalAlignment = VerticalAlignment.Center;
            FxIcon.SetColor(icon, on ? Palette.Accent : Palette.Faint);
            var item = new MenuItem { Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { check, icon, new TextBlock { Text = b.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
            ToolTip.SetTip(item, Loc.T(on ? "send.hide.tip" : "send.show.tip", b.Name));
            item.Click += (_, _) => r.Edit((p, x) => ProjectBuses.ShowSend(p, x, id, !on), $"send.{id}.shown");
            items.Add(item);
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = Loc.T("send.none"), IsEnabled = false, FontSize = 11 });
        return items;
    }

    /// <summary>"+ Envío" under the sends: the buses to tick (<see cref="SendChoices"/>).</summary>
    Button AddSendButton(Router r)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { new TextBlock { Classes = { "icon" }, Text = "\ue710", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.T("send.add"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } };
        var b = new Button { Classes = { "chip" }, Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 30, Focusable = false };
        ToolTip.SetTip(b, Loc.T("send.add.tip"));
        b.Click += (_, _) =>
        {
            if (PressClosedMenuOf(b)) return;
            OpenAnchoredMenu(Popup(SendChoices(r), 220), b);
        };
        return b;
    }

    /// <summary>The × in a send's title row: takes that bus out of the list (off, its level kept for when it comes back).</summary>
    Button RemoveSendButton(Router r, string busId, string name)
    {
        var b = SlotHeadButton("\ue711", Loc.T("send.hide.tip", name));
        b.Click += (_, _) => r.Edit((p, x) => ProjectBuses.ShowSend(p, x, busId, false), $"send.{busId}.shown");
        return b;
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

    // --- output and sends, in any strip ---------------------------------------------------------------

    /// <summary>
    /// The Salida selector: Master, or a bus that wouldn't send the sound back to <paramref name="r"/>. Its items follow
    /// the buses on every refresh.
    /// </summary>
    SelectButton OutputSelect(Router r, List<Action> refresh)
    {
        var select = StripSelect([]);
        var ids = new List<string?>();
        ToolTip.SetTip(select, Loc.T("route.output.tip"));
        select.Selected += i => { if (i >= 0 && i < ids.Count) { string? to = ids[i]; r.Edit((p, x) => ProjectBuses.SetOutput(p, x, to), "output"); } };
        refresh.Add(() =>
        {
            ids.Clear();
            ids.Add(null);
            var names = new List<string> { Loc.T("route.master") };
            foreach (var b in Targets(r)) { ids.Add(b.Id); names.Add(b.Name); }
            select.SetItems([.. names]);
            select.SelectedIndex = Math.Max(0, ids.IndexOf(r.Get()?.Output));
        });
        return select;
    }

    /// <summary>The output selector as a labeled row ("SALIDA" and its drop-down), for a strip's scrolling controls.</summary>
    Control OutputRow(Router r)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(4, 0, 0, 0) };
        row.Children.Add(new TextBlock { Text = Loc.T("route.output.upper"), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0) });
        var select = OutputSelect(r, mx.Refresh);
        Grid.SetColumn(select, 1); row.Children.Add(select);
        return row;
    }

    /// <summary>A send panel per bus in <paramref name="r"/>'s list, in the buses' order, and "+ Envío" to tick which.</summary>
    void SendPanels(StackPanel fx, Router r)
    {
        var box = new StackPanel { Spacing = fx.Spacing };
        foreach (var b in SendTargets(r)) SendPanel(box, r, b);
        if (Targets(r).Count > 0) box.Children.Add(AddSendButton(r));
        // the mixer header's "Envíos" shows or hides them
        mx.Refresh.Add(() => box.IsVisible = mixerSends);
        fx.Children.Add(box);
    }

    /// <summary>The send panel of a bus: knob and %, the bus's name and what it does, ON and PRE (none on a folder's).</summary>
    void SendPanel(StackPanel fx, Router r, Bus bus)
    {
        string id = bus.Id;
        (double Level, bool On, bool Pre) S() => r.Get() is { } x ? ProjectBuses.SendOf(x, id) : (0, true, false);
        void Set(double? level = null, bool? on = null, bool? pre = null, string key = "level") =>
            r.Edit((p, x) => ProjectBuses.SetSend(p, x, id, level, on, pre) != null, $"send.{id}.{key}");
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        if (!r.Folder) head.Children.Add(PreChip(() => S().Pre, () => Set(pre: !S().Pre, key: "pre"), mx.Refresh));
        head.Children.Add(RemoveSendButton(r, id, bus.Name));
        var panel = FxPanel(fx, bus.Name, BusIcon(bus), () => S().On && S().Level > 0, () => Set(on: !S().On, key: "on"),
            on => Loc.T(on ? "send.power.off" : "send.power.on", BusById(id)?.Name ?? bus.Name), head);
        var knob = new Knob(34, Loc.T("send.knob.tip", bus.Name)) { Min = 0, Max = 1, ResetTo = 0 };
        // turning it up turns the send on
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
            info.Text = BusById(id) is { } b ? BusInfo(b) : "";
        });
        panel.Body.Children.Add(Row(knob, side));
    }

    // --- bus strips ------------------------------------------------------------------------------------

    /// <summary>
    /// A bus strip: its name (⋯ → Renombrar / Quitar bus), mute, pan and output, its slots' panels with "+ Efecto", its own
    /// sends, fader and meter (peaks and RMS, like every strip).
    /// </summary>
    Control BusStrip(Bus bus, bool wide)
    {
        string id = bus.Id;
        Bus B() => BusById(id) ?? bus;
        var box = StripCard(wide ? ReturnWideW : ReturnCompactW);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Height = 28 };
        var iconMark = FxIcon.Create(BusIcon(bus), 12);
        FxIcon.SetColor(iconMark, Palette.Accent); iconMark.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(iconMark);
        var name = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 6, 0) };
        name.DoubleTapped += (_, _) => RenameBus(id, name);
        Grid.SetColumn(name, 1); head.Children.Add(name);
        var mute = StripToggle("M", Loc.T("mix.bus.mute"), 24, () => B().Muted, false, () => BusEdit(id, b => b.Muted = !b.Muted, "muted"));
        mute.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(mute, 2); head.Children.Add(mute);
        // the glyph keeps its own color: it has to show without hovering (asked by the user)
        var menu = new Button { Classes = { "ghost" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Focusable = false, Margin = new Thickness(2, 0, 0, 0),
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        ToolTip.SetTip(menu, Loc.T("mix.bus.menu.tip"));
        menu.Click += (_, _) =>
        {
            if (PressClosedMenuOf(menu)) return;
            OpenAnchoredMenu(Popup(
            [
                MenuAction(Loc.T("mix.bus.rename"), () => { RenameBus(id, name); return Task.CompletedTask; }, icon: ""),
                new Separator(),
                MenuAction(Loc.T("mix.bus.remove"), () => { BusesEdit(p => ProjectBuses.Remove(p, id)); return Task.CompletedTask; }, danger: true, icon: "",
                    tip: Loc.T("mix.bus.remove.tip")),
            ], 220), menu);
        };
        Grid.SetColumn(menu, 3); head.Children.Add(menu);
        mx.Refresh.Add(() => { name.Text = B().Name; ToolTip.SetTip(name, Loc.T("mix.bus.name.tip", B().Name)); });

        var fader = new Fader();
        fader.Changed += v => BusEdit(id, b => b.Volume = v, "volume");
        var db = new TextBlock { Classes = { "mono" }, FontSize = wide ? 11 : 10, Foreground = Palette.Dim };
        mx.Refresh.Add(() => { fader.Value = B().Volume; db.Text = $"{Fader.Db(B().Volume)} dB"; });
        var meter = new MixerMeter();
        mx.NodeMeters.Add((id, meter));
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { fader, meter } };

        // the chain: its slots (each folds to one row) and "+ Efecto"; a bus goes to the master, so no output nor sends
        var fx = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 4, 6) };
        wideGr = null;
        SlotPanels(fx, SlotsOf(bus));
        wideGr = null;
        var chain = new ScrollViewer { Content = fx, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panKnob = new Knob(24);
        panKnob.Changed += v => BusEdit(id, b => b.Pan = Math.Abs(v) < 0.04 ? 0 : v, "pan");
        var panText = new TextBlock { Classes = { "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        mx.Refresh.Add(() => { panKnob.Value = B().Pan; panKnob.Tip = Loc.T("tl.tip.pan", FmtPan(B().Pan)); panText.Text = FmtPan(B().Pan); });
        box.Child = BusRack(wide, head, chain, panKnob, panText, faders, db);
        return box;
    }

    /// <summary>
    /// A bus as a rack (asked by the user: a bus is mostly its chain of effects): the chain scrolls down the left, and
    /// the fader with its meter stays on the right, on the rows every strip shares so its fader is level with the
    /// tracks'. The pan goes where the strip has room: above the chain in a wide mixer (like a wide track's), over the
    /// fader in a compact one (where the tracks keep their knobs).
    /// </summary>
    Grid BusRack(bool wide, Control head, Control chain, Knob panKnob, TextBlock panText, Control faders, Control foot)
    {
        var g = new Grid { Margin = new Thickness(8, 8, 8, 6), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.RowDefinitions.Add(new RowDefinition(StripHeadH, GridUnitType.Pixel));
        var top = new RowDefinition(wide ? 0 : StripTopH, GridUnitType.Pixel);
        if (!wide) mx.TopRows.Add(top);
        g.RowDefinitions.Add(top);
        g.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        g.RowDefinitions.Add(new RowDefinition(StripFootH, GridUnitType.Pixel));
        void Put(Control c, int row, int col = 0, int span = 1, int rowSpan = 1)
        {
            Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span); Grid.SetRowSpan(c, rowSpan);
            g.Children.Add(c);
        }
        head.VerticalAlignment = VerticalAlignment.Top;
        Put(head, 0, 0, 2);
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(0, 0, 8, 0) };
        if (wide)
        {
            var pan = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0, 0, 8) };
            pan.Children.Add(new TextBlock { Text = Loc.T("mix.pan.upper"), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
            pan.Children.Add(panKnob); pan.Children.Add(panText);
            left.Children.Add(pan);
        }
        else
        {
            var pan = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
            pan.Children.Add(new TextBlock { Text = Loc.T("mix.pan.upper"), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
            panKnob.HorizontalAlignment = HorizontalAlignment.Center;
            panText.HorizontalAlignment = HorizontalAlignment.Center;
            pan.Children.Add(panKnob); pan.Children.Add(panText);
            Put(pan, 1, 1);
        }
        Grid.SetRow(chain, 1); left.Children.Add(chain);
        Put(left, 1, 0, 1, 3);
        faders.HorizontalAlignment = HorizontalAlignment.Center;
        Put(faders, 2, 1);
        foot.VerticalAlignment = VerticalAlignment.Bottom;
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        Put(foot, 3, 1);
        return g;
    }

    void RenameBus(string id, Control anchor)
    {
        if (BusById(id) is not { } b || store?.ReadOnly != false) return;
        AskName(anchor, null, Loc.T("mix.bus.rename"), b.Name, Loc.T("folder.rename.ok"), n => BusEdit(id, x => x.Name = n.Trim(), "name"));
    }

    /// <summary>Called from the mixer's tick: the folders' and buses' meters.</summary>
    void TickNodes()
    {
        foreach (var (id, meter) in mx.NodeMeters)
        {
            if (engine.Node(id) is not { } r) continue;
            meter.Set(r.ShowL, r.ShowR, Math.Sqrt(r.Rms.MeanSqL), Math.Sqrt(r.Rms.MeanSqR));
        }
    }

    /// <summary>The folders' and buses' peaks (with the same fall-off as the tracks'), reduction and RMS, once per tick, shown or not.</summary>
    void TakeNodesRms(double dt)
    {
        // a for over the lists themselves: the tick doesn't allocate (an interface foreach boxes its enumerator)
        var folders = store?.Project.Folders;
        for (int i = 0; folders != null && i < folders.Count; i++) TakeNode(folders[i].Id, dt);
        var list = store?.Project.Settings.Buses;
        for (int i = 0; list != null && i < list.Count; i++) TakeNode(list[i].Id, dt);
    }

    void TakeNode(string id, double dt)
    {
        if (engine.Node(id) is not { } r) return;
        r.Rms.Take(dt);
        r.ShowL = Math.Max(r.MeterL, r.ShowL * 0.82f);
        r.ShowR = Math.Max(r.MeterR, r.ShowR * 0.82f);
        r.MeterL = r.MeterR = 0;
        double gr = r.GrDb;
        r.GrDb = 0;
        r.GrShow = gr < r.GrShow ? gr : r.GrShow + (gr - r.GrShow) * 0.12;
    }

    // --- reverb and delay slots (in a bus's chain) ---------------------------------------------------

    static readonly ReverbSettings GoneReverb = new();
    static readonly DelaySettings GoneDelay = new();

    void ReverbSlotPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu, SlotDrag? drag)
    {
        ReverbSettings V() => o.Slot(id)?.Reverb ?? GoneReverb;
        void Set(Action<ReverbSettings> edit, string key) => SlotEdit(o, id, s => edit(s.Reverb!), "reverb." + key);
        var panel = FxPanel(fx, SlotTitle("Reverb", nth), FxIcon.Reverb, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, s => s.On = !s.On, "reverb.on"),
            on => Loc.T(on ? "mix.slot.reverb.off" : "mix.slot.reverb.on"), menu, drag, id, o, () =>
            {
                string p = ReverbBus.PresetOf(ToParams(V()));
                return $"{(p == "" ? Loc.T("mix.custom.f") : BusPresetName("reverb", p))} · {V().Decay:0.0}s";
            });
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

    void DelaySlotPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu, SlotDrag? drag)
    {
        DelaySettings V() => o.Slot(id)?.Delay ?? GoneDelay;
        void Set(Action<DelaySettings> edit, string key) => SlotEdit(o, id, s => edit(s.Delay!), "delay." + key);
        double Bpm() => store?.Project.Settings.Bpm ?? 110;
        var panel = FxPanel(fx, SlotTitle("Delay", nth), FxIcon.Delay, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, s => s.On = !s.On, "delay.on"),
            on => Loc.T(on ? "mix.slot.delay.off" : "mix.slot.delay.on"), menu, drag, id, o, () =>
            {
                var d = V();
                string time = !d.Sync ? $"{Math.Round(d.TimeMs):0} ms"
                    : Array.Find(DelayBus.Divisions, x => x.Id == d.Division) is { } div ? Loc.Or("mix.div." + div.Id, div.Label) : d.Division;
                return $"{time} · {d.Feedback * 100:0}%";
            });
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

    // --- the Canal tab's output and send sections -----------------------------------------------------

    /// <summary>
    /// The Canal tab's routing: the track's output (or "through its folder") and a section per bus it can send to: level,
    /// ON and PRE. Rebuilt only when the track, its folder or the buses (a name, a slot, the routing between them)
    /// change; every other edit just refreshes them.
    /// </summary>
    void RebuildInspectorSends(LiveTrack t)
    {
        var router = RouterOf(t);
        var targets = SendTargets(router);
        string key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t)}|{t.Track.FolderId}|"
            + string.Join("/", targets.Select(b => b.Id + ":" + b.Name + ":" + BusIcon(b))) + "|" + Targets(router).Count;
        if (key == inspectorSendsKey) return;
        inspectorSendsKey = key;
        foreach (string title in sendSections) inspectorSections.Remove(title);
        sendSections.Clear();
        sendRefresh.Clear();
        inspectorSends.Children.Clear();

        // where the track's output goes: its folder's, or its own choice
        var output = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 4, 2, 12) };
        output.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("route.output.upper"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        if (t.Track.FolderId is { } folder)
        {
            var through = new TextBlock { Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            sendRefresh.Add(() => through.Text = Loc.T("route.throughFolder", FolderById(folder)?.Name ?? ""));
            ToolTip.SetTip(through, Loc.T("route.throughFolder.tip"));
            Grid.SetColumn(through, 1); output.Children.Add(through);
        }
        else
        {
            var select = OutputSelect(router, sendRefresh);
            Grid.SetColumn(select, 1); output.Children.Add(select);
        }
        inspectorSends.Children.Add(output);

        foreach (var bus in targets)
        {
            string id = bus.Id, title = "bus:" + id;
            SendTitles[title] = bus.Name;
            (double Level, bool On, bool Pre) S() => sel == null ? (0, true, false) : ProjectBuses.SendOf(sel.Channel, id);
            void Set(double? level = null, bool? on = null, bool? pre = null, string k = "level") =>
                Edit(x => ProjectBuses.SetSend(store!.Project, x.Channel, id, level, on, pre), $"send.{id}.{k}");
            var pre = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2,
                Children = { PreChip(() => S().Pre, () => Set(pre: !S().Pre, k: "pre"), sendRefresh), RemoveSendButton(router, id, bus.Name) } };
            var section = Section(title, BusIcon(bus), () => S().On && S().Level > 0, on => Set(on: on, k: "on"), pre, inspectorSends, sendRefresh);
            sendSections.Add(title);
            EffectKnob(section, Loc.T("insp.send.level"), 0, () => S().Level, v => Set(level: v, on: v > 0 ? true : null), v => $"{v * 100:0}%", SendDb,
                Loc.T("insp.send.hint", bus.Name), sendRefresh);
        }
        if (Targets(router).Count > 0)
        {
            var add = AddSendButton(router);
            add.Margin = new Thickness(0, 4, 0, 0);
            inspectorSends.Children.Add(add);
        }
        UpdateCollapseButton();
    }

    readonly StackPanel inspectorSends = new();
    readonly List<Action> sendRefresh = [];
    readonly List<string> sendSections = [];
    string inspectorSendsKey = "";
    /// <summary>The send sections' shown titles (the buses' names), by their stable id "bus:&lt;bus id&gt;".</summary>
    static readonly Dictionary<string, string> SendTitles = [];
}
