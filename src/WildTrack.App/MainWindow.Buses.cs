using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WildTrack.App.Controls;
using WildTrack.App.Model;
using WildTrack.Audio.Dsp;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// The buses (native only, <see cref="ProjectBuses"/>) and where every track, folder and bus sends its sound: a strip per
/// bus, built and selected like a track's (badge with its color and icon, name, mute, its insert slots with the same
/// panels as a track's, where a reverb or a delay is one more slot, pan, fader and meter), the Salida selector and the send panels every strip has, the
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
        SyncEffects();
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
            OwnerId = id,
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

    /// <summary>Each folder's send knobs for the timeline (<see cref="FolderSendKnobsOf"/>) and the edit they were made at.</summary>
    readonly Dictionary<Folder, (int Stamp, SendKnob[] Knobs)> folderSendKnobs = [];

    /// <summary>The sends a folder's row shows as knobs, like <see cref="SendKnobsOf"/> for a track.</summary>
    SendKnob[] FolderSendKnobsOf(Folder f)
    {
        if (folderSendKnobs.TryGetValue(f, out var c) && c.Stamp == editStamp) return c.Knobs;
        if (store == null || folderSendKnobs.Count > store.Project.Folders.Count * 2 + 8) folderSendKnobs.Clear();
        SendKnob[] knobs = store == null ? [] : [.. ProjectBuses.Shown(store.Project, f).Select(b => new SendKnob(b.Id, b.Name, BusIcon(b)))];
        folderSendKnobs[f] = (editStamp, knobs);
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

    /// <summary>
    /// A send's actions, from a right click on it in the mixer or the inspector (asked by the user, like an effect's):
    /// turn it on or off, take it before or after the fader (not a folder's: it has no fader), go to its bus, take it
    /// out of the list. Each one an undo step.
    /// </summary>
    void ShowSendMenu(Router r, string busId, Control anchor)
    {
        if (r.Get() is not { } x || BusById(busId) is not { } bus) return;
        var (_, on, pre) = ProjectBuses.SendOf(x, busId);
        bool enabled = store is { ReadOnly: false } && !restoring;
        var items = new List<object>
        {
            MenuAction(Loc.T(on ? "send.menu.off" : "send.menu.on"), () =>
            {
                r.Edit((p, y) => ProjectBuses.SetSend(p, y, busId, on: !on) != null, $"send.{busId}.on");
                return Task.CompletedTask;
            }, enabled: enabled, icon: "\ue7e8"),
        };
        if (!r.Folder)
            items.Add(MenuAction(Loc.T(pre ? "send.menu.post" : "send.menu.pre"), () =>
            {
                r.Edit((p, y) => ProjectBuses.SetSend(p, y, busId, pre: !pre) != null, $"send.{busId}.pre");
                return Task.CompletedTask;
            }, enabled: enabled, icon: "\ue8cb", tip: Loc.T(pre ? "send.menu.post.tip" : "send.menu.pre.tip")));
        items.Add(MenuAction(Loc.T("send.menu.bus", bus.Name), () => { SelectBus(busId); return Task.CompletedTask; }, icon: "\ue8a7"));
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T("send.menu.remove"), () =>
        {
            r.Edit((p, y) => ProjectBuses.ShowSend(p, y, busId, false), $"send.{busId}.shown");
            return Task.CompletedTask;
        }, enabled: enabled, danger: true, icon: "\ue74d", tip: Loc.T("send.hide.tip", bus.Name)));
        OpenAnchoredMenu(Popup(items, 230), anchor);
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

    // --- bus strips ------------------------------------------------------------------------------------

    /// <summary>
    /// A bus strip, built like a track's (asked by the user: one system for every channel): the same card, selection,
    /// width and rows. Its badge, name and "BUS", its effects as a list, an empty sends section (a bus goes only to the
    /// master), the pan bar and M over the fader, its dB at the foot. A click selects it (the inspector shows
    /// it), a double click on its name renames it, its badge opens the color and icon picker, a right click its menu.
    /// </summary>
    Control BusStrip(Bus bus, double width)
    {
        string id = bus.Id;
        Bus B() => BusById(id) ?? bus;
        var box = StripCard(width);
        mx.Boxes.Add(box);
        var top = StripCardLook(box, () => selBusId == id, () => false, () => TrackIcons.ColorOf(B().Color), () => SelectBus(id));
        box.PointerReleased += (_, e) =>
        {
            if (e.Handled || e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right || store == null || restoring) return;
            e.Handled = true;
            SelectBus(id);
            OpenAnchoredMenu(BusMenu(id, box), box);
        };

        var name = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        name.DoubleTapped += (_, _) => RenameBus(id, name);
        mx.Refresh.Add(() =>
        {
            name.Text = B().Name;
            name.Foreground = B().Muted ? Palette.Faint : Palette.Fg;
            ToolTip.SetTip(name, Loc.T("mix.name.tip", B().Name));
        });
        var badge = new TrackBadge { Width = 22, Height = 22 };
        ToolTip.SetTip(badge, Loc.T("look.bus.tip"));
        badge.Clicked += () => ShowBusLook(id, badge);
        mx.Refresh.Add(() => badge.SetBus(B().Color, B().Icon, BusIcon(B()), selBusId == id));

        var fader = new Fader();
        fader.Changed += v => BusEdit(id, b => b.Volume = v, "volume");
        var db = new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        mx.Refresh.Add(() => { fader.Value = B().Volume; db.Text = $"{Fader.Db(B().Volume)} dB"; });
        var meter = new MixerMeter();
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { fader, meter } };

        var tag = new TextBlock { Text = Loc.T("mixer.busTag"), FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint };
        ToolTip.SetTip(tag, Loc.T("mixer.busTag.tip"));
        var head = StripHead(badge, name, tag);
        // a bus goes to the master: its sends section stays empty
        var (controls, gr) = CompactChain(SlotsOf(bus), null, () => B().Pan, v => BusEdit(id, b => b.Pan = v, "pan"),
            StripToggle("M", Loc.T("mix.bus.mute"), 26, () => B().Muted, false, () => BusEdit(id, b => b.Muted = !b.Muted, "muted")));
        mx.NodeMeters.Add((id, meter, gr));
        var content = new Grid();
        var layout = StripLayout(head, controls, faders, db);
        NarrowParts(layout, faders, fader, meter, db);
        content.Children.Add(layout);
        content.Children.Add(top);
        box.Child = content;
        return box;
    }

    /// <summary>A bus's menu (its right click): rename, color and icon, remove.</summary>
    ContextMenu BusMenu(string id, Control anchor)
    {
        bool enabled = store is { ReadOnly: false };
        return Popup(
        [
            MenuAction(Loc.T("mix.bus.rename"), () => { RenameBus(id, anchor); return Task.CompletedTask; }, enabled: enabled, icon: "\ue8ac"),
            MenuAction(Loc.T("look.bus.menu"), () => { ShowBusLook(id, anchor); return Task.CompletedTask; }, enabled: enabled, icon: "\ue790"),
            new Separator(),
            MenuAction(Loc.T("mix.bus.remove"), () => { BusesEdit(p => ProjectBuses.Remove(p, id)); return Task.CompletedTask; }, enabled: enabled, danger: true, icon: "\ue74d",
                tip: Loc.T("mix.bus.remove.tip")),
        ], 220);
    }

    void RenameBus(string id, Control anchor)
    {
        if (BusById(id) is not { } b || store?.ReadOnly != false) return;
        AskName(anchor, null, Loc.T("mix.bus.rename"), b.Name, Loc.T("folder.rename.ok"), n => BusEdit(id, x => x.Name = n.Trim(), "name"));
    }

    // --- the selected bus ------------------------------------------------------------------------------

    /// <summary>The bus selected in the mixer (then no track or folder is); null when none is.</summary>
    string? selBusId;
    /// <summary>Set while <see cref="SelectBus"/> clears the timeline's selection, so that doesn't clear the bus too.</summary>
    bool selectingBus;

    Bus? SelectedBus => selBusId is { } id ? BusById(id) : null;

    /// <summary>
    /// Selects a bus like a track (asked by the user): the timeline lets go of its track or folder and the inspector
    /// shows the bus. Selecting anything else lets go of it (<see cref="BusSelectionLost"/>).
    /// </summary>
    void SelectBus(string id)
    {
        if (selBusId == id && sel == null && SelectedFolder == null) return;
        selBusId = id;
        selectingBus = true;
        try { Timeline.Select(-1); }
        finally { selectingBus = false; }
    }

    /// <summary>Called on every selection change of the timeline: a track or a folder chosen there lets go of the bus.</summary>
    void BusSelectionLost()
    {
        if (!selectingBus) selBusId = null;
    }

    readonly StackPanel busPanel = new() { Spacing = 12, IsVisible = false };
    readonly TextBox busName = new() { Classes = { "title" } };
    readonly List<Action> busRefresh = [];

    /// <summary>
    /// The inspector of the selected bus, like a folder's: its badge (the color and icon picker) and name, what it
    /// holds, its color and icon, where it goes, and "Quitar bus". Its effects are edited in its mixer strip.
    /// </summary>
    void InitBusInspector()
    {
        var badge = new TrackBadge { Width = 28, Height = 28, Radius = 5, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(badge, Loc.T("look.bus.tip"));
        badge.Clicked += () => { if (selBusId is { } id) ShowBusLook(id, badge); };
        ToolTip.SetTip(busName, Loc.T("folder.name.tip"));
        busName.Margin = new Thickness(4, 0, 0, 0);
        busName.TextChanged += (_, _) =>
        {
            if (syncing || SelectedBus is not { } b) return;
            string name = busName.Text?.Trim() ?? "";
            if (name.Length > 0 && name != b.Name) BusEdit(b.Id, x => x.Name = name, "name");
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
        head.Children.Add(badge);
        Grid.SetColumn(busName, 1); head.Children.Add(busName);
        var info = new TextBlock { FontSize = 11, Foreground = Palette.Faint, Margin = new Thickness(36, -6, 0, 0) };
        busPanel.Children.Add(head);
        busPanel.Children.Add(info);

        var card = new StackPanel { Spacing = 14 };
        card.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("look.color").ToUpperInvariant() });
        card.Children.Add(ColorChips(() => SelectedBus?.Color, hex => { if (selBusId is { } id) BusEdit(id, x => x.Color = hex, "color"); }, busRefresh, 16));
        var icon = new Button { Classes = { "chip" }, Content = Loc.T("bus.icon"), MinHeight = 30, Padding = new Thickness(12, 0), HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(icon, Loc.T("look.bus.tip"));
        icon.Click += (_, _) => { if (selBusId is { } id) ShowBusLook(id, icon); };
        card.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("look.icon").ToUpperInvariant(), Margin = new Thickness(0, 4, 0, 0) });
        card.Children.Add(icon);
        card.Children.Add(Help(Loc.T("bus.route.hint")));
        busPanel.Children.Add(new Border { Classes = { "section" }, Background = Palette.Panel, BorderThickness = new Thickness(0, 1), CornerRadius = new CornerRadius(0),
            Margin = new Thickness(-14, 4, -14, 0), Padding = new Thickness(16, 14, 16, 16), Child = card });

        var remove = RemoveButton(Loc.T("mix.bus.remove"));
        ToolTip.SetTip(remove, Loc.T("mix.bus.remove.tip"));
        remove.Click += (_, _) => { if (selBusId is { } id) BusesEdit(p => ProjectBuses.Remove(p, id)); };
        busPanel.Children.Add(remove);

        busRefresh.Add(() =>
        {
            if (SelectedBus is not { } b) return;
            badge.SetBus(b.Color, b.Icon, BusIcon(b), true);
            if (!busName.IsFocused || busName.Text?.Trim() != b.Name) busName.Text = b.Name;
            info.Text = BusInfo(b);
        });
        Inspector.Children.Add(busPanel);
    }

    /// <summary>Called from the mixer's tick: the folders' and buses' meters.</summary>
    void TickNodes()
    {
        foreach (var (id, meter, gr) in mx.NodeMeters)
        {
            if (engine.Node(id) is not { } r) continue;
            meter.Set(r.ShowL, r.ShowR, Math.Sqrt(r.Rms.MeanSqL), Math.Sqrt(r.Rms.MeanSqR));
            gr?.Invoke(r.GrShow);
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

    void ReverbSlotPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu)
    {
        ReverbSettings V() => o.Slot(id)?.Reverb ?? GoneReverb;
        void Set(Action<ReverbSettings> edit, string key) => SlotEdit(o, id, s => edit(s.Reverb!), "reverb." + key);
        var panel = FxPanel(fx, SlotTitle("Reverb", nth), FxIcon.Reverb, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, s => s.On = !s.On, "reverb.on"),
            on => Loc.T(on ? "mix.slot.reverb.off" : "mix.slot.reverb.on"), menu);
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

    void DelaySlotPanel(StackPanel fx, SlotOwner o, string id, int nth, Control menu)
    {
        DelaySettings V() => o.Slot(id)?.Delay ?? GoneDelay;
        void Set(Action<DelaySettings> edit, string key) => SlotEdit(o, id, s => edit(s.Delay!), "delay." + key);
        double Bpm() => store?.Project.Settings.Bpm ?? 110;
        var panel = FxPanel(fx, SlotTitle("Delay", nth), FxIcon.Delay, () => o.Slot(id)?.On == true, () => SlotEdit(o, id, s => s.On = !s.On, "delay.on"),
            on => Loc.T(on ? "mix.slot.delay.off" : "mix.slot.delay.on"), menu);
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
    /// The Canal tab's routing: the track's output (or "through its folder") and a fixed row per bus in its list
    /// (<see cref="InspectorSendRow"/>). Rebuilt only when the track, its folder or the buses (a name, a slot, the routing between them)
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
        foreach (string title in sendSections) { inspectorSections.Remove(title); sectionLooks.Remove(title); }
        sendSections.Clear();
        sendRows.Clear();
        sendRefresh.Clear();
        inspectorSends.Children.Clear();

        // where the track's output goes: its folder's, or its own choice
        var output = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8, 0, 2, 4) };
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

        foreach (var bus in targets) inspectorSends.Children.Add(InspectorSendRow(router, bus));
        sendsTray.Set(targets.Count, Targets(router).Count > 0 ? AddSendButton(router) : null);
        UpdateCollapseButton();
    }

    readonly StackPanel inspectorSends = new();
    readonly List<Action> sendRefresh = [];
    readonly List<string> sendSections = [];
    /// <summary>The send rows by their stable id "bus:&lt;bus id&gt;", for the collapsed inspector's icons (<see cref="GoToSection"/>).</summary>
    readonly Dictionary<string, Control> sendRows = [];

    /// <summary>
    /// A send in the Canal tab as one fixed row (asked by the user: small enough not to fold): its power, the bus's icon
    /// and name, its level as a bar with its % (drag, wheel with Shift for fine, double click: 0; the dB in its tooltip),
    /// PRE and ×. A right click opens its menu (<see cref="ShowSendMenu"/>). It also feeds the collapsed inspector's
    /// icons, like a section.
    /// </summary>
    Border InspectorSendRow(Router router, Bus bus)
    {
        string id = bus.Id, title = "bus:" + id;
        SendTitles[title] = bus.Name;
        (double Level, bool On, bool Pre) S() => router.Get() is { } x ? ProjectBuses.SendOf(x, id) : (0, true, false);
        void Set(double? level = null, bool? on = null, bool? pre = null, string k = "level") =>
            router.Edit((p, x) => ProjectBuses.SetSend(p, x, id, level, on, pre) != null, $"send.{id}.{k}");
        bool Lit() => S().On && S().Level > 0;
        var power = ListPower(Lit, () => Set(on: !S().On, k: "on"), sendRefresh);
        power.Width = power.Height = power.MinHeight = 22;
        var icon = FxIcon.Create(BusIcon(bus), 12);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(2, 0, 6, 0);
        var name = new TextBlock { Text = bus.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(name, Loc.T("insp.send.hint", bus.Name));
        var bar = new HBar { ResetTo = 0, Height = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        bar.Changed += v => Set(level: v, on: v > 0 ? true : null);
        var pre = PreChip(() => S().Pre, () => Set(pre: !S().Pre, k: "pre"), sendRefresh);
        var remove = RemoveSendButton(router, id, bus.Name);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,4*,5*,Auto,Auto") };
        g.Children.Add(power);
        Grid.SetColumn(icon, 1); g.Children.Add(icon);
        Grid.SetColumn(name, 2); g.Children.Add(name);
        Grid.SetColumn(bar, 3); g.Children.Add(bar);
        Grid.SetColumn(pre, 4); g.Children.Add(pre);
        Grid.SetColumn(remove, 5); g.Children.Add(remove);
        var row = new Border { Child = g, Height = 36, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1) };
        sendRefresh.Add(() =>
        {
            var (level, on, _) = S();
            bool lit = on && level > 0;
            bar.Value = level;
            bar.Lit = on;
            bar.Readout = $"{level * 100:0}%";
            ToolTip.SetTip(bar, $"{Loc.T("mix.list.send.tip", bus.Name, $"{level * 100:0}")} · {SendDb(level)}");
            FxIcon.SetColor(icon, lit ? Palette.Accent : Palette.Faint);
            name.Foreground = lit ? Palette.Fg : Palette.Faint;
            // like an effect's panel: a step above the inspector, sinking back while it is off
            row.Background = Palette.Alpha(Palette.HoverColor, lit ? 0.45 : 0.18);
            row.BorderBrush = lit ? Palette.Line : Palette.Alpha(Palette.LineColor, 0.6);
        });
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right || store == null) return;
            e.Handled = true;
            ShowSendMenu(router, id, row);
        };
        sectionLooks[title] = (BusIcon(bus), Lit);
        sendSections.Add(title);
        sendRows[title] = row;
        return row;
    }
    string inspectorSendsKey = "";
    /// <summary>The send sections' shown titles (the buses' names), by their stable id "bus:&lt;bus id&gt;".</summary>
    static readonly Dictionary<string, string> SendTitles = [];
}
