using Avalonia.Controls;
using Avalonia.Threading;
using GuitarCam.App.Audio;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Audio.Vst;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// Instrument plugins on MIDI tracks (native only, stage 3 of the MIDI editor, the user's order: instruments first):
/// each track's <see cref="Track.Instrument"/> runs in its own gcvsthost.exe (<see cref="VstBridge"/>), loaded in the
/// background after every edit that may change them (<see cref="SyncInstruments"/>), and plays through the track's
/// <see cref="LiveTrack.Instrument"/>; until it is up, or if it can't be opened, the track plays the test tone and a
/// toast says why. What the user changes in the plugin's window is saved in the project: when it says "dirty" or its
/// window closes, its state is asked for and kept in <see cref="PluginRef.State"/> (outside the undo, like the tempo).
/// The list of plugins comes from <see cref="PluginScan"/> (scanned once, cached; managed in Configuración → Plugins).
/// </summary>
public partial class MainWindow
{
    sealed class LoadedInstrument(VstBridge bridge, InstrumentPlayer player, PluginRef plugin)
    {
        public readonly VstBridge Bridge = bridge;
        public readonly InstrumentPlayer Player = player;
        public readonly PluginRef Plugin = plugin;
        /// <summary>Its state has to be asked for and saved (it said "dirty", or its window closed).</summary>
        public bool StateDue;
    }

    readonly Dictionary<string, LoadedInstrument> instruments = [];
    /// <summary>Tracks whose plugin is loading, with which plugin (so a change meanwhile drops the old load).</summary>
    readonly Dictionary<string, PluginRef> instrumentLoads = [];
    /// <summary>Plugins that already failed to open, by track and file: not retried (nor toasted) on every edit.</summary>
    readonly HashSet<string> instrumentFailed = [];
    DispatcherTimer? instrumentStateTimer;
    /// <summary>How long after a "dirty" its state is asked for: one ask for a whole knob turn.</summary>
    static readonly TimeSpan InstrumentStateDelay = TimeSpan.FromSeconds(1.5);
    /// <summary>How long the piano roll's keys sound through an instrument.</summary>
    const double AuditionSec = 0.35;

    void InitInstruments()
    {
        Timeline.InstrumentStateOf = InstrumentStateOf;
        Timeline.InstrumentMenuRequested += (t, at) => ShowInstrumentMenu(t, at);
        Timeline.InstrumentWindowRequested += OpenInstrumentWindow;
        // a scan (here or in Configuración → Plugins) or a plugin hidden there: the lists catch up
        PluginScan.Changed += InstrumentsChanged;
    }

    static string FailKey(string trackId, PluginRef p) => trackId + "|" + p.Path + "|" + p.ClassId;

    TimelineView.InstrumentState InstrumentStateOf(string trackId)
    {
        if (instruments.ContainsKey(trackId)) return TimelineView.InstrumentState.Ready;
        var plugin = tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track.Instrument;
        if (plugin == null) return TimelineView.InstrumentState.TestTone;
        return instrumentFailed.Contains(FailKey(trackId, plugin)) ? TimelineView.InstrumentState.Failed : TimelineView.InstrumentState.Loading;
    }

    /// <summary>Something about the instruments changed (one loaded, failed, closed, was chosen): every place that shows them catches up.</summary>
    void InstrumentsChanged()
    {
        Timeline.InvalidateVisual();
        RefreshPianoInstrument();
        RefreshMidiInspector();
    }

    /// <summary>The instrument list as a menu under the timeline's chip (the first time, it looks for plugins on its own).</summary>
    void ShowInstrumentMenu(LiveTrack track, Avalonia.Point at)
    {
        if (store == null) return;
        EnsurePlugins();
        var items = InstrumentItems(track, !store.ReadOnly);
        OpenTimelineMenu(Popup(items, 280), at, MenuHeight(items));
    }

    bool pluginsAsked;

    /// <summary>The first time a list of instruments is shown and none were ever found, it looks for them (no extra
    /// click); same if an older scan didn't tell instruments from effects (it only opens those files again). Never while recording.</summary>
    void EnsurePlugins()
    {
        if (pluginsAsked || PluginScan.Running || PluginScan.Entries.Count > 0 && !PluginScan.Outdated || VstHost.Find() == null || RecordingBusy) return;
        pluginsAsked = true;
        _ = ScanPlugins();
    }

    /// <summary>
    /// Brings the running plugins in line with the project: starts the ones a track wants (in the background), stops the
    /// ones no track wants any more and hands each live track its player. After every <see cref="ApplyTracks"/> and on
    /// opening a project.
    /// </summary>
    void SyncInstruments()
    {
        if (store == null) return;
        var wanted = new Dictionary<string, PluginRef>();
        foreach (var t in tracks)
            if (t.Track.IsMidi && t.Track.Instrument is { } plugin) wanted[t.Track.Id] = plugin;
        foreach (var (id, loaded) in instruments.ToList())
            if (!wanted.TryGetValue(id, out var w) || !w.SamePlugin(loaded.Plugin)) DropInstrument(id);
        foreach (var t in tracks)
        {
            string id = t.Track.Id;
            if (instruments.TryGetValue(id, out var loaded)) { if (t.Instrument != loaded.Player) t.Instrument = loaded.Player; continue; }
            t.Instrument = null;
            if (!wanted.TryGetValue(id, out var plugin)) continue;
            if (instrumentLoads.TryGetValue(id, out var loading) && loading.SamePlugin(plugin)) continue;
            if (instrumentFailed.Contains(FailKey(id, plugin))) continue;
            LoadInstrument(id, plugin);
        }
    }

    void LoadInstrument(string trackId, PluginRef plugin)
    {
        if (VstHost.Find() == null)
        {
            instrumentFailed.Add(FailKey(trackId, plugin));
            Toast(Loc.T("vst.noHost"));
            return;
        }
        instrumentLoads[trackId] = plugin;
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
            bool still = instrumentLoads.TryGetValue(trackId, out var p) && p == plugin;
            if (still) instrumentLoads.Remove(trackId);
            var live = tracks.FirstOrDefault(x => x.Track.Id == trackId);
            if (bridge == null)
            {
                if (still)
                {
                    instrumentFailed.Add(FailKey(trackId, plugin));
                    Toast(Loc.T("vst.loadFailed", name, error ?? ""), sticky: true);
                    InstrumentsChanged();
                }
                return;
            }
            // the track (or its plugin) changed while it loaded
            if (!still || live == null || live.Track.Instrument is not { } now || !now.SamePlugin(plugin))
            {
                _ = Task.Run(bridge.Dispose);
                return;
            }
            var loaded = new LoadedInstrument(bridge, new InstrumentPlayer(bridge), plugin);
            instruments[trackId] = loaded;
            bridge.Message += line => Dispatcher.UIThread.Post(() => InstrumentSaid(trackId, loaded, line));
            bridge.Exited += () => Dispatcher.UIThread.Post(() => InstrumentExited(trackId, loaded));
            live.Instrument = loaded.Player;
            InstrumentsChanged();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Takes a plugin off its track and closes its process (a moment later: the audio thread may hold it one more block).</summary>
    void DropInstrument(string trackId)
    {
        if (!instruments.Remove(trackId, out var loaded)) return;
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is { } live && live.Instrument == loaded.Player) live.Instrument = null;
        _ = Task.Run(async () => { await Task.Delay(200); loaded.Bridge.Dispose(); });
        InstrumentsChanged();
    }

    /// <summary>Closes every plugin (another project, or the window closes).</summary>
    void DropAllInstruments()
    {
        foreach (var id in instruments.Keys.ToList()) DropInstrument(id);
        instrumentLoads.Clear();
        instrumentFailed.Clear();
    }

    void InstrumentSaid(string trackId, LoadedInstrument loaded, string line)
    {
        if (!instruments.TryGetValue(trackId, out var current) || current != loaded) return;
        if (line is "dirty" or "editor-closed")
        {
            loaded.StateDue = true;
            instrumentStateTimer ??= new DispatcherTimer(InstrumentStateDelay, DispatcherPriority.Background, async (_, _) => await SaveInstrumentStates());
            instrumentStateTimer.Stop();
            instrumentStateTimer.Start();
            if (line == "editor-closed") _ = SaveInstrumentStates();
        }
        else if (line.StartsWith("error ", StringComparison.Ordinal)) Toast(Loc.T("vst.said", loaded.Plugin.Name, line[6..]));
    }

    void InstrumentExited(string trackId, LoadedInstrument loaded)
    {
        if (!instruments.TryGetValue(trackId, out var current) || current != loaded) return;
        DropInstrument(trackId);
        instrumentFailed.Add(FailKey(trackId, loaded.Plugin));
        Toast(Loc.T("vst.crashed", loaded.Plugin.Name), sticky: true, action: (Loc.T("vst.reopen"), () =>
        {
            instrumentFailed.Remove(FailKey(trackId, loaded.Plugin));
            SyncInstruments();
        }));
    }

    bool InstrumentStatesDue => instruments.Values.Any(i => i.StateDue);

    /// <summary>Asks the plugins that changed for their state and keeps it in the project (one save for all).</summary>
    async Task SaveInstrumentStates()
    {
        instrumentStateTimer?.Stop();
        bool changed = false;
        foreach (var (id, loaded) in instruments.ToList())
        {
            if (!loaded.StateDue) continue;
            loaded.StateDue = false;
            string? state = await loaded.Bridge.GetStateAsync();
            // the project's track now (an undo may have put another object there), if it still has this plugin
            if (state == null || store?.Project.Tracks.Find(t => t.Id == id)?.Instrument is not { } plugin || !plugin.SamePlugin(loaded.Plugin)) continue;
            if (plugin.State == state) continue;
            plugin.State = state;
            changed = true;
        }
        if (changed && store is { ReadOnly: false }) MarkDirty();
    }

    /// <summary>Sets (or takes away, with null) a MIDI track's instrument: one undo step; the plugin loads right after.</summary>
    Task SetInstrument(LiveTrack track, VstPluginInfo? plugin)
    {
        if (store == null || store.ReadOnly || !track.Track.IsMidi) return Task.CompletedTask;
        string id = track.Track.Id;
        return EditTimeline(p =>
        {
            if (p.Tracks.Find(t => t.Id == id) is not { } t) return null;
            t.Instrument = plugin == null ? null : PluginCatalog.Ref(plugin);
            return id;
        }, "instrument-set");
    }

    void OpenInstrumentWindow(LiveTrack track)
    {
        if (instruments.TryGetValue(track.Track.Id, out var loaded))
            loaded.Bridge.OpenEditor(TryGetPlatformHandle()?.Handle ?? 0);
        else if (track.Track.Instrument is { } plugin) Toast(Loc.T(instrumentLoads.ContainsKey(track.Track.Id) ? "vst.stillLoading" : "vst.notLoaded", plugin.Name));
    }

    /// <summary>The piano roll's key through the track's instrument, or the test tone without one.</summary>
    void Audition(string? trackId, int pitch, int velocity)
    {
        if (trackId != null && instruments.TryGetValue(trackId, out var loaded))
            loaded.Player.Audition(pitch, velocity, (int)(AuditionSec * PlaybackEngine.SampleRate));
        else engine.PreviewNote(GuitarCam.Audio.Dsp.NoteSynth.Audition(pitch, velocity, PlaybackEngine.SampleRate));
    }

    /// <summary>Looks for new or changed plugins (in the background, with a toast that says what it found).</summary>
    async Task ScanPlugins()
    {
        if (PluginScan.Running) return;
        if (VstHost.Find() == null) { Toast(Loc.T("vst.noHost")); return; }
        Toast(Loc.T("vst.scanning"));
        var found = await PluginScan.Run(error: e => Toast(Loc.T("vst.scanFailed", e)));
        if (found == null) return;
        var all = PluginCatalog.Plugins(found);
        int n = all.Count(p => p.IsInstrument);
        Toast(Loc.T("vst.scanned", n, all.Count - n), action: (Loc.T("vst.manage"), () => Settings.Show("plugins")));
    }

    static bool SamePlugin(PluginRef? r, VstPluginInfo p) => r != null && r.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase) && r.ClassId == p.ClassId;

    /// <summary>The instruments a track can pick: the ones not hidden in Configuración → Plugins, plus the one it uses even if hidden.</summary>
    static List<VstPluginInfo> InstrumentChoices(PluginRef? current)
    {
        var found = PluginScan.Visible.Where(p => p.IsInstrument).ToList();
        if (current != null && !found.Any(p => SamePlugin(current, p))
            && PluginCatalog.Plugins(PluginScan.Entries).FirstOrDefault(p => SamePlugin(current, p)) is { } used)
            found.Add(used);
        return found;
    }

    /// <summary>A group's title inside a menu ("NATIVOS", "VST3"): a section label, not an entry (it can't be picked).</summary>
    static MenuItem MenuGroup(string text) => new()
    {
        Header = new TextBlock { Text = text, Classes = { "label" } },
        IsHitTestVisible = false, Focusable = false, MinHeight = 0, Padding = new Avalonia.Thickness(12, 6, 12, 2),
    };

    /// <summary>From this many VST3 instruments on, the menu groups them by maker (a submenu each), so it never gets
    /// taller than the window.</summary>
    const int ByVendorFrom = 12;

    /// <summary>
    /// The "Instrumento" entries of a MIDI track's menu, in two groups (asked by the user): the native ones (today the
    /// test tone) and the VST3 ones found, by maker when there are many (<see cref="ByVendorFrom"/>); then look again,
    /// manage the plugins (Configuración → Plugins), open its window. <paramref name="enabled"/> false (read only) locks the choice.
    /// </summary>
    List<object> InstrumentItems(LiveTrack track, bool enabled)
    {
        var refresh = new List<Action>();
        var current = track.Track.Instrument;
        MenuItem Pick(VstPluginInfo? p, string label, string? tip = null)
        {
            var item = MenuCheck(label, () => p == null ? track.Track.Instrument == null : SamePlugin(track.Track.Instrument, p), () => _ = SetInstrument(track, p), refresh, tip: tip);
            item.IsEnabled = enabled;
            return item;
        }
        var items = new List<object>
        {
            MenuGroup(Loc.T("vst.group.native")),
            Pick(null, Loc.T("vst.testTone"), Loc.T("vst.testTone.tip")),
            new Separator(),
            MenuGroup(Loc.T("vst.group.vst")),
        };
        var found = InstrumentChoices(current);
        if (found.Count == 0) items.Add(new MenuItem { IsEnabled = false, Header = new TextBlock { Text = Loc.T("vst.group.none"), FontSize = 11 } });
        else if (found.Count < ByVendorFrom)
            foreach (var p in found) items.Add(Pick(p, p.Vendor.Length > 0 ? $"{p.Name} · {p.Vendor}" : p.Name));
        else
            foreach (var maker in found.GroupBy(p => p.Vendor.Length > 0 ? p.Vendor : Loc.T("vst.group.noVendor")).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                // the maker of the one in use carries the check, so it is found without opening every submenu
                string Check() => maker.Any(p => SamePlugin(track.Track.Instrument, p)) ? "" : " ";
                var lead = new TextBlock { Classes = { "icon" }, Text = Check(), FontSize = 12, Width = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                refresh.Add(() => lead.Text = Check());
                items.Add(new MenuItem { Header = MenuHeader($"{maker.Key} ({maker.Count()})", lead), ItemsSource = maker.Select(p => Pick(p, p.Name)).ToList() });
            }
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T(found.Count == 0 ? "vst.scan.first" : "vst.scan"), ScanPlugins,
            enabled: !PluginScan.Running, tip: Loc.T("vst.scan.tip")));
        items.Add(MenuAction(Loc.T("vst.manage.menu"), () => { Settings.Show("plugins"); return Task.CompletedTask; }, tip: Loc.T("vst.manage.tip")));
        if (current != null)
            items.Add(MenuAction(Loc.T("vst.open", current.Name), () => { OpenInstrumentWindow(track); return Task.CompletedTask; },
                enabled: instruments.ContainsKey(track.Track.Id), icon: ""));
        return items;
    }
}

/// <summary>
/// The inspector of a MIDI track (asked by the user: choosing its instrument has to be easy and in sight, but not as a
/// list that fills the panel once there are many): the instrument it plays with as a dropdown (the same menu as the
/// timeline's chip, native and VST3 apart), how it is going, its window, and below, where the track goes and its sends
/// (the same sections as an audio track's, <see cref="RebuildInspectorSends"/>, moved here while a MIDI track is shown).
/// </summary>
public partial class MainWindow
{
    readonly StackPanel midiPanel = new() { Spacing = 10, IsVisible = false };
    readonly TextBlock midiNow = new() { FontSize = 12.5, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    readonly TextBlock midiNowKind = new() { FontSize = 11, Foreground = Palette.Faint, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    readonly TextBlock midiNowInfo = new() { FontSize = 12, Foreground = Palette.Dim, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    readonly Button midiPick = new()
    {
        Classes = { "chip" }, MinHeight = 40, Padding = new Avalonia.Thickness(10, 4), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
    };
    readonly Button midiOpen = new() { Classes = { "chip" }, Height = 30, Padding = new Avalonia.Thickness(12, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
    /// <summary>Where <see cref="inspectorSends"/> goes while a MIDI track is shown.</summary>
    readonly StackPanel midiSends = new() { Margin = new Avalonia.Thickness(0, 6, 0, 0) };

    void InitMidiInspector()
    {
        midiPanel.Children.Add(new TextBlock { Text = Loc.T("vst.insp.title"), Classes = { "label" } });
        var pick = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pick.Children.Add(new StackPanel { Spacing = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Children = { midiNow, midiNowKind } });
        var chevron = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10, Foreground = Palette.Dim, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        Grid.SetColumn(chevron, 1);
        pick.Children.Add(chevron);
        midiPick.Content = pick;
        ToolTip.SetTip(midiPick, Loc.T("vst.insp.pick.tip"));
        midiPick.Click += (_, _) =>
        {
            if (sel is not { Track.IsMidi: true } t || store == null) return;
            EnsurePlugins();
            OpenAnchoredMenu(Popup(InstrumentItems(t, !store.ReadOnly), Math.Max(240, midiPick.Bounds.Width)), midiPick);
        };
        var card = new Border
        {
            Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Avalonia.Thickness(1), CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12, 10),
            Child = new StackPanel { Spacing = 8, Children = { midiPick, midiNowInfo, midiOpen } },
        };
        midiPanel.Children.Add(card);
        midiOpen.Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
            Children = { new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 }, new TextBlock { Text = Loc.T("vst.insp.open"), FontSize = 12 } },
        };
        ToolTip.SetTip(midiOpen, Loc.T("vst.openWindow.tip"));
        midiOpen.Click += (_, _) => { if (sel is { Track.IsMidi: true } t) OpenInstrumentWindow(t); };
        midiPanel.Children.Add(Help(Loc.T("vst.insp.hint")));
        midiPanel.Children.Add(midiSends);
        Inspector.Children.Add(midiPanel);
    }

    /// <summary>Fills the panel for the selected MIDI track: what it plays with (and whether it is native or VST3) and how that is going.</summary>
    void RefreshMidiInspector()
    {
        if (sel is not { Track.IsMidi: true } t) return;
        var plugin = t.Track.Instrument;
        var state = InstrumentStateOf(t.Track.Id);
        midiNow.Text = plugin?.Name ?? Loc.T("vst.testTone");
        midiNow.Foreground = state == TimelineView.InstrumentState.Ready ? Palette.Accent : Palette.Fg;
        midiNowKind.Text = plugin == null ? Loc.T("vst.kind.native")
            : plugin.Vendor.Length > 0 ? $"{Loc.T("vst.group.vst")} · {plugin.Vendor}" : Loc.T("vst.group.vst");
        midiNowInfo.Text = state switch
        {
            TimelineView.InstrumentState.Ready => Loc.T("vst.insp.ready"),
            TimelineView.InstrumentState.Loading => Loc.T("vst.insp.loading"),
            TimelineView.InstrumentState.Failed => Loc.T("vst.insp.failed"),
            _ => Loc.T("vst.testTone.tip"),
        };
        midiNowInfo.Text = PluginScan.Running ? Loc.T("vst.scanning") : midiNowInfo.Text;
        midiPick.IsEnabled = store is { ReadOnly: false };
        midiOpen.IsVisible = plugin != null;
        midiOpen.IsEnabled = state == TimelineView.InstrumentState.Ready;
    }

    /// <summary>Puts the output and send sections in <paramref name="host"/>: the Canal tab's place for an audio track
    /// (right after its effects), <see cref="midiSends"/> for a MIDI one. One set of sections, moved, not two.</summary>
    void PlaceInspectorSends(Panel host)
    {
        if (inspectorSends.Parent == host) return;
        (inspectorSends.Parent as Panel)?.Children.Remove(inspectorSends);
        if (host == takeSections) takeSections.Children.Insert(takeSections.Children.IndexOf(inspectorChain) + 1, inspectorSends);
        else host.Children.Add(inspectorSends);
    }
}
