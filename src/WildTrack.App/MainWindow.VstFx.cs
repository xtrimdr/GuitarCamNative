using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WildTrack.App.Audio;
using WildTrack.App.Controls;
using WildTrack.App.Model;
using WildTrack.Audio.Vst;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// VST3 effects in the slots of tracks and buses (<see cref="InsertSlot.VstKind"/>), one more kind of slot mixed with the
/// built-in ones. Each slot's plugin runs in its own wtvsthost.exe (<see cref="VstBridge"/>), loaded in the background
/// after every edit that may add or take one away (<see cref="SyncEffects"/>), and the live engine finds it by owner and
/// slot (<see cref="LiveVst"/>); until it is up, or if it can't be opened, the slot lets the sound through and a toast says
/// why. What the user changes in the plugin's window is kept in the slot's <see cref="PluginRef.State"/> (outside the
/// undo, like an instrument's) when it says "dirty" or its window closes. The export opens its own copies
/// (<see cref="VstInsert"/>). Live, each effect is heard a driver block (plus its own latency) late, and the rest of the
/// mix waits as much (<see cref="PlaybackEngine.UpdateLatency"/>).
/// </summary>
public partial class MainWindow
{
    sealed class LoadedEffect(VstBridge bridge, PluginRef plugin, string owner, string slot)
    {
        public readonly VstBridge Bridge = bridge;
        public readonly PluginRef Plugin = plugin;
        public readonly string Owner = owner, Slot = slot;
        /// <summary>Its state has to be asked for and saved (it said "dirty", or its window closed).</summary>
        public bool StateDue;
    }

    /// <summary>The running effects, by owner and slot (<see cref="EffectKey"/>).</summary>
    readonly Dictionary<string, LoadedEffect> effects = [];
    /// <summary>Slots whose plugin is loading, with which plugin (so a change meanwhile drops the old load).</summary>
    readonly Dictionary<string, PluginRef> effectLoads = [];
    /// <summary>Plugins that already failed to open, by slot and file: not retried (nor toasted) on every edit.</summary>
    readonly HashSet<string> effectFailed = [];
    DispatcherTimer? effectStateTimer;
    /// <summary>How long a plugin taken off a chain keeps running: the audio thread may still be fading over it.</summary>
    static readonly TimeSpan EffectCloseDelay = TimeSpan.FromMilliseconds(200);

    static string EffectKey(string owner, string slot) => owner + "|" + slot;
    static string EffectFailKey(string key, PluginRef p) => key + "|" + p.Path + "|" + p.ClassId;

    void InitEffects() => LiveVst.Find = (owner, slot) => effects.TryGetValue(EffectKey(owner, slot), out var e) ? e.Bridge : null;

    /// <summary>Every VST3 slot of the project with its owner (a track or a bus).</summary>
    static IEnumerable<(string Owner, InsertSlot Slot)> VstSlots(Project p)
    {
        foreach (var t in p.Tracks)
            foreach (var s in t.Channel.Slots)
                if (s.Type == InsertSlot.VstKind && s.Known) yield return (t.Id, s);
        foreach (var b in p.Settings.Buses)
            foreach (var s in b.Inserts)
                if (s.Type == InsertSlot.VstKind && s.Known) yield return (b.Id, s);
    }

    /// <summary>The slot as the project has it now (an undo puts other objects there), or null.</summary>
    InsertSlot? EffectSlot(string owner, string slot)
    {
        if (store == null) return null;
        var p = store.Project;
        var list = p.Tracks.Find(t => t.Id == owner)?.Channel.Slots ?? p.Settings.Buses.Find(b => b.Id == owner)?.Inserts;
        return list?.Find(s => s.Id == slot);
    }

    /// <summary>
    /// Brings the running effects in line with the project: starts the ones a slot wants (in the background) and stops the
    /// ones no slot wants any more. After every edit of tracks, buses and their slots, and on opening a project.
    /// </summary>
    void SyncEffects()
    {
        if (store == null) return;
        var wanted = new Dictionary<string, PluginRef>();
        foreach (var (owner, slot) in VstSlots(store.Project)) wanted[EffectKey(owner, slot.Id)] = slot.Vst!.Plugin;
        foreach (var (key, e) in effects.ToList())
            if (!wanted.TryGetValue(key, out var w) || !w.SamePlugin(e.Plugin)) DropEffect(key);
        foreach (var (key, plugin) in wanted)
        {
            if (effects.ContainsKey(key)) continue;
            if (effectLoads.TryGetValue(key, out var loading) && loading.SamePlugin(plugin)) continue;
            if (effectFailed.Contains(EffectFailKey(key, plugin))) continue;
            LoadEffect(key, plugin);
        }
    }

    void LoadEffect(string key, PluginRef plugin)
    {
        if (VstHost.Find() == null)
        {
            effectFailed.Add(EffectFailKey(key, plugin));
            Toast(Loc.T("vst.fx.noHost"));
            return;
        }
        effectLoads[key] = plugin;
        string path = plugin.Path, cid = plugin.ClassId, name = plugin.Name;
        string? state = plugin.State;
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException(Loc.T("vst.missing", path));
                var bridge = VstBridge.Start(path, cid, name, PlaybackEngine.SampleRate, offline: false);
                if (state != null && !bridge.SetState(state))
                    Dispatcher.UIThread.Post(() => Toast(Loc.T("vst.stateFailed", name)));
                return (Bridge: bridge, Error: (string?)null);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                return (Bridge: (VstBridge?)null, Error: e.Message);
            }
        }).ContinueWith(t =>
        {
            var (bridge, error) = t.Result;
            bool still = effectLoads.TryGetValue(key, out var p) && p == plugin;
            if (still) effectLoads.Remove(key);
            int bar = key.IndexOf('|');
            string owner = key[..bar], slotId = key[(bar + 1)..];
            if (bridge == null)
            {
                if (still)
                {
                    effectFailed.Add(EffectFailKey(key, plugin));
                    Toast(Loc.T("vst.fx.loadFailed", name, error ?? ""), sticky: true);
                    EffectsChanged();
                }
                return;
            }
            // the slot (or its plugin) changed while it loaded
            if (!still || EffectSlot(owner, slotId)?.Vst?.Plugin is not { } now || !now.SamePlugin(plugin))
            {
                _ = Task.Run(bridge.Dispose);
                return;
            }
            var loaded = new LoadedEffect(bridge, plugin, owner, slotId);
            effects[key] = loaded;
            bridge.Message += line => Dispatcher.UIThread.Post(() => EffectSaid(key, loaded, line));
            bridge.Exited += () => Dispatcher.UIThread.Post(() => EffectExited(key, loaded));
            ReapplyOwner(owner);
            EffectsChanged();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Hands an owner's chain its slots again (an effect came up or went away).</summary>
    void ReapplyOwner(string owner)
    {
        if (tracks.FirstOrDefault(t => t.Track.Id == owner) is { } live) live.ApplyInserts();
        else if (store != null) engine.ApplyBusSlots(store.Project.Settings.Buses);
    }

    /// <summary>Takes a plugin off its slot and closes its process a moment later (the audio thread may hold it one more block).</summary>
    void DropEffect(string key)
    {
        if (!effects.Remove(key, out var loaded)) return;
        ReapplyOwner(loaded.Owner);
        _ = Task.Run(async () => { await Task.Delay(EffectCloseDelay); loaded.Bridge.Dispose(); });
        EffectsChanged();
    }

    /// <summary>Closes every effect (another project, or the window closes).</summary>
    void DropAllEffects()
    {
        foreach (var key in effects.Keys.ToList()) DropEffect(key);
        effectLoads.Clear();
        effectFailed.Clear();
    }

    /// <summary>An effect loaded, failed or closed: the panels that show it catch up.</summary>
    void EffectsChanged()
    {
        RefreshMixer();
        RefreshInspectorText();
    }

    void EffectSaid(string key, LoadedEffect loaded, string line)
    {
        if (!effects.TryGetValue(key, out var current) || current != loaded) return;
        if (line is "dirty" or "editor-closed")
        {
            loaded.StateDue = true;
            effectStateTimer ??= new DispatcherTimer(InstrumentStateDelay, DispatcherPriority.Background, async (_, _) => await SaveEffectStates());
            effectStateTimer.Stop();
            effectStateTimer.Start();
            if (line == "editor-closed") _ = SaveEffectStates();
        }
        // its own latency changed: the slot makes room for it (the tick's PDC follows)
        else if (line.StartsWith("latency ", StringComparison.Ordinal)) ReapplyOwner(loaded.Owner);
        else if (line.StartsWith("error ", StringComparison.Ordinal)) Toast(Loc.T("vst.said", loaded.Plugin.Name, line[6..]));
    }

    void EffectExited(string key, LoadedEffect loaded)
    {
        if (!effects.TryGetValue(key, out var current) || current != loaded) return;
        DropEffect(key);
        effectFailed.Add(EffectFailKey(key, loaded.Plugin));
        Toast(Loc.T("vst.fx.crashed", loaded.Plugin.Name), sticky: true, action: (Loc.T("vst.reopen"), () =>
        {
            effectFailed.Remove(EffectFailKey(key, loaded.Plugin));
            SyncEffects();
        }));
    }

    bool EffectStatesDue => effects.Values.Any(e => e.StateDue);

    /// <summary>Asks the effects that changed for their state and keeps it in their slots (one save for all).</summary>
    async Task SaveEffectStates()
    {
        effectStateTimer?.Stop();
        bool changed = false;
        foreach (var loaded in effects.Values.ToList())
        {
            if (!loaded.StateDue) continue;
            loaded.StateDue = false;
            string? state = await loaded.Bridge.GetStateAsync();
            if (state == null || EffectSlot(loaded.Owner, loaded.Slot)?.Vst?.Plugin is not { } plugin || !plugin.SamePlugin(loaded.Plugin)) continue;
            if (plugin.State == state) continue;
            plugin.State = state;
            changed = true;
        }
        if (changed && store is { ReadOnly: false }) MarkDirty();
    }

    /// <summary>A plugin window (an instrument's or an effect's) was changed and its state isn't in the project yet.</summary>
    bool PluginStatesDue => InstrumentStatesDue || EffectStatesDue;

    async Task SavePluginStates()
    {
        await SaveInstrumentStates();
        await SaveEffectStates();
    }

    enum EffectState { Ready, Loading, Failed }

    EffectState EffectStateOf(string owner, string slotId)
    {
        string key = EffectKey(owner, slotId);
        if (effects.ContainsKey(key)) return EffectState.Ready;
        if (effectLoads.ContainsKey(key)) return EffectState.Loading;
        var plugin = EffectSlot(owner, slotId)?.Vst?.Plugin;
        return plugin == null || effectFailed.Contains(EffectFailKey(key, plugin)) || VstHost.Find() == null ? EffectState.Failed : EffectState.Loading;
    }

    /// <summary>How the slot is going, in a line: ready (and how late it comes, compensated), opening, or why not.</summary>
    string EffectStatus(string owner, string slotId)
    {
        switch (EffectStateOf(owner, slotId))
        {
            case EffectState.Ready:
                var b = effects[EffectKey(owner, slotId)].Bridge;
                double ms = (VstInsert.LiveBlock + b.Latency) * 1000.0 / PlaybackEngine.SampleRate;
                return Loc.T("vst.fx.ready", $"{ms:0.#}");
            case EffectState.Loading: return Loc.T("vst.fx.loading");
            default: return Loc.T("vst.fx.failed");
        }
    }

    void OpenEffectWindow(string owner, string slotId)
    {
        if (effects.TryGetValue(EffectKey(owner, slotId), out var loaded))
            loaded.Bridge.OpenEditor(TryGetPlatformHandle()?.Handle ?? 0);
        else if (EffectSlot(owner, slotId)?.Vst?.Plugin is { } plugin)
            Toast(Loc.T(EffectStateOf(owner, slotId) == EffectState.Loading ? "vst.stillLoading" : "vst.fx.notLoaded", plugin.Name));
    }

    /// <summary>A VST3 slot's title: the plugin's name, numbered when the chain has it more than once.</summary>
    static string VstTitle(List<InsertSlot> slots, string id)
    {
        int i = slots.FindIndex(s => s.Id == id);
        if (i < 0 || slots[i].Vst?.Plugin is not { } p) return "VST3";
        int nth = 1;
        for (int k = 0; k < i; k++) if (slots[k].Vst?.Plugin.SamePlugin(p) == true) nth++;
        return SlotTitle(p.Name, nth);
    }

    /// <summary>What a slot is called in a list: the musician's word (numbered when it repeats), or a VST3's own name.</summary>
    static string SlotLabel(List<InsertSlot> slots, InsertSlot slot, int nth) =>
        slot.Type == InsertSlot.VstKind ? VstTitle(slots, slot.Id) : SlotTitle(SlotName(slot.Type), nth);

    static readonly VstSlotSettings GoneVst = new();

    /// <summary>"Abrir" with the window glyph: opens the plugin's own window.</summary>
    Button EffectOpenButton(string owner, string id)
    {
        var b = new Button
        {
            Classes = { "chip" }, Height = 30, Padding = new Thickness(12, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Loc.T("vst.fx.open"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        ToolTip.SetTip(b, Loc.T("vst.fx.open.tip"));
        b.Click += (_, _) => OpenEffectWindow(owner, id);
        return b;
    }

    /// <summary>A VST3 slot's panel in a mixer strip: its name, ON, "Abrir" and how it is going.</summary>
    void VstPanel(StackPanel fx, SlotOwner o, string id, Control menu)
    {
        VstSlotSettings V() => o.Slot(id)?.Vst ?? GoneVst;
        var panel = FxPanel(fx, VstTitle(o.Slots(), id), FxIcon.Vst, () => V().On, () => SlotEdit(o, id, s => s.On = !s.On, "vst.on"),
            on => Loc.T(on ? "vst.fx.power.off" : "vst.fx.power.on"), menu);
        var open = EffectOpenButton(o.OwnerId, id);
        var status = new TextBlock { FontSize = 11, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        mx.Refresh.Add(() =>
        {
            status.Text = EffectStatus(o.OwnerId, id);
            open.IsEnabled = EffectStateOf(o.OwnerId, id) == EffectState.Ready;
        });
        panel.Body.Children.Add(open);
        panel.Body.Children.Add(status);
    }

    /// <summary>A VST3 slot's section in the Canal tab: ON, "Abrir" and how it is going, in one short line.</summary>
    void VstSection(LiveTrack t, string id, SlotDrag drag)
    {
        string owner = t.Track.Id;
        VstSlotSettings V() => t.Channel.Slot(id)?.Vst ?? GoneVst;
        // its name alone (asked by the user: the maker adds nothing once it is in the chain)
        var section = SlotSection(t, id, VstTitle(t.Channel.Slots, id), 1, FxIcon.Vst, () => V().On, on => SlotEdit(t, id, s => s.On = on, "vst.on"), drag);
        var open = EffectOpenButton(owner, id);
        var status = new TextBlock { FontSize = 12, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap };
        chainRefresh.Add(() =>
        {
            status.Text = EffectStatus(owner, id);
            open.IsEnabled = EffectStateOf(owner, id) == EffectState.Ready;
        });
        section.Children.Add(open);
        section.Children.Add(status);
    }

    /// <summary>
    /// The VST3 side of "+ Efecto": one entry that opens as the pointer goes over it, with the effects not hidden in
    /// Configuración → Plugins (by maker when there are many, each maker opening the same way); then look again and
    /// manage them.
    /// </summary>
    List<object> VstEffectItems(SlotOwner o)
    {
        MenuItem Pick(VstPluginInfo p, string label)
        {
            var icon = FxIcon.Create(FxIcon.Vst, 12);
            icon.VerticalAlignment = VerticalAlignment.Center;
            FxIcon.SetColor(icon, Palette.Accent);
            var item = new MenuItem { Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { icon, new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
            if (p.Categories.Length > 0)
            {
                ToolTip.SetTip(item, p.Categories.Replace("|", " · "));
                ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
            }
            var plugin = PluginCatalog.Ref(p);
            item.Click += (_, _) => SlotsEdit(o, x => ChannelInserts.AddVst(x, plugin), "add");
            return item;
        }
        var plugins = new List<object>();
        var found = PluginScan.Visible.Where(p => !p.IsInstrument).ToList();
        if (found.Count == 0) plugins.Add(new MenuItem { IsEnabled = false, Header = new TextBlock { Text = Loc.T("vst.fx.none"), FontSize = 11 } });
        else if (found.Count < ByVendorFrom)
            foreach (var p in found) plugins.Add(Pick(p, p.Vendor.Length > 0 ? $"{p.Name} · {p.Vendor}" : p.Name));
        else
            foreach (var maker in found.GroupBy(p => p.Vendor.Length > 0 ? p.Vendor : Loc.T("vst.group.noVendor")).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                plugins.Add(new MenuItem { Header = new TextBlock { Text = $"{maker.Key} ({maker.Count()})", FontSize = 12 }, ItemsSource = maker.Select(p => Pick(p, p.Name)).ToList() });
        var items = new List<object> { AddSlotGroup(found.Count > 0 ? $"{Loc.T("vst.group.vst")} ({found.Count})" : Loc.T("vst.group.vst"), FxIcon.Vst, plugins) };
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T(found.Count == 0 ? "vst.scan.first" : "vst.scan"), ScanPlugins, enabled: !PluginScan.Running, tip: Loc.T("vst.scan.tip")));
        items.Add(MenuAction(Loc.T("vst.manage.menu"), () => { Settings.Show("plugins"); return Task.CompletedTask; }, tip: Loc.T("vst.manage.tip")));
        return items;
    }

    /// <summary>A group of "+ Efecto" (Nativos, VST3): its icon and name, and its effects in a submenu that opens on hover.</summary>
    static MenuItem AddSlotGroup(string name, string icon, List<object> items)
    {
        var lead = FxIcon.Create(icon, 12);
        lead.VerticalAlignment = VerticalAlignment.Center;
        FxIcon.SetColor(lead, Palette.Accent);
        return new MenuItem { Header = MenuHeader(name, lead), ItemsSource = items };
    }
}
