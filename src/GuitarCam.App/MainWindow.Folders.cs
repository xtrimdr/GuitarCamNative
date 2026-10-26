using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// Track folders and track presets (native only, asked by the user). Folders group tracks in the timeline and the
/// mixer; their fader, mute and solo act on the children like a VCA (<see cref="ApplyFolderMix"/>). Moving tracks in
/// and out and removing a folder are timeline edits (<see cref="EditTimeline"/>); renaming, color, M / S and fader
/// are folder edits with their own undo step (<see cref="FolderEdit"/>); folding is only a view, saved without undo.
/// Presets keep a track's whole strip to add it again from the "nueva pista" menu (<see cref="TrackPresets"/>).
/// </summary>
public partial class MainWindow
{
    void InitFolders()
    {
        Timeline.FolderMoveRequested += async (id, at) => await EditTimeline(p =>
        {
            TrackFolders.MoveFolder(p, id, at);
            return Timeline.SelectedFolderId ?? sel?.Track.Id;
        }, "tracks-reordered");
        Timeline.FolderFoldRequested += f => FolderEdit(f, x => x.Collapsed = !x.Collapsed, "fold", undoable: false);
        Timeline.FolderToggleRequested += (f, solo) => FolderEdit(f, x => { if (solo) x.Solo = !x.Solo; else x.Muted = !x.Muted; }, solo ? "solo" : "mute");
        Timeline.FolderRemoveRequested += (f, at) => _ = RemoveFolder(f, at);
        Timeline.FolderVolumeRequested += (f, v) => FolderEdit(f, x => x.Volume = v, "volume");
        Timeline.FolderContextRequested += ShowFolderMenu;
        Timeline.FolderLookRequested += (f, at) => ShowFolderLook(f, Timeline, at);
        InitFolderInspector();
        InitNameEditor();
        Timeline.RowHeightRequested += SetRowHeight;
        ResetHeightsButton.Click += (_, _) => ResetRowHeights();
    }

    /// <summary>A track's or a folder's own height in px (null = the shared one). A track and a folded folder keep it as
    /// a multiple of the shared height, so the slider goes on scaling it. A view setting like folding: saved, without undo.</summary>
    void SetRowHeight(string id, double? h)
    {
        if (store == null || store.ReadOnly || restoring) return;
        if (FolderById(id) is { Collapsed: true } folded) folded.HeightScale = Scale(h);
        else if (FolderById(id) is { } f) f.Height = h;
        else if (tracks.Find(t => t.Track.Id == id) is { } t) t.Track.HeightScale = Scale(h);
        else return;
        RowHeightsChanged();
    }

    double? Scale(double? h) => h is { } px && Math.Abs(px - Timeline.TrackHeight) >= 1 ? px / Timeline.TrackHeight : null;

    /// <summary>Every track and folder back to the shared height.</summary>
    void ResetRowHeights()
    {
        if (store == null || store.ReadOnly || restoring) return;
        foreach (var t in store.Project.Tracks) t.HeightScale = null;
        foreach (var f in store.Project.Folders) { f.Height = null; f.HeightScale = null; }
        RowHeightsChanged();
    }

    void RowHeightsChanged()
    {
        MarkDirty();
        Timeline.UpdateTracks(tracks, store!.Project.Folders);
        UpdateResetHeights();
    }

    /// <summary>The reset button is on only while some row has a height of its own.</summary>
    void UpdateResetHeights() => ResetHeightsButton.IsEnabled = store is { ReadOnly: false } st
        && (st.Project.Tracks.Any(t => t.HeightScale != null) || st.Project.Folders.Any(f => f.Height != null || f.HeightScale != null));

    /// <summary>The folder selected in the timeline (or the mixer), or null; then no track is selected.</summary>
    Folder? SelectedFolder => Timeline.SelectedFolderId is { } id ? FolderById(id) : null;

    /// <summary>
    /// Tells every live track what its folder does to it (read by the audio thread on the next block): the VCA, and
    /// the folder's bus with its slots (<see cref="PlaybackEngine.SetFolders"/>). Also hands the engine the extra
    /// returns and every track its sends to them (<see cref="PlaybackEngine.SetReturns"/>).
    /// </summary>
    void ApplyFolderMix(Project p, IReadOnlyList<LiveTrack> list)
    {
        engine.SetFolders(p, list);
        engine.SetReturns(p, list);
        foreach (var t in list)
        {
            var (gain, muted, solo) = TrackFolders.Mix(p, t.Track);
            t.FolderGain = (float)gain;
            t.FolderMuted = muted;
            t.FolderSolo = solo;
        }
    }

    /// <summary>A folder edit: one undo step per control (folding is not one), heard at once, saved, on screen.</summary>
    void FolderEdit(Folder f, Action<Folder> change, string key, bool undoable = true)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        if (undoable) undo.Checkpoint(store.Project, $"folder:{f.Id}:{key}");
        change(f);
        ApplyFolderMix(store.Project, tracks);
        MarkDirty();
        UpdateUndoButtons();
        Timeline.UpdateTracks(tracks, store.Project.Folders);
        RefreshInspectorText();
        RefreshMixer();
    }

    /// <summary>
    /// Removes a folder. With tracks inside it asks first (asked by the user): just the folder, its tracks staying at
    /// the root, or the folder and its tracks. The question opens at <paramref name="at"/> on the timeline, or under
    /// the folder's name.
    /// </summary>
    Task RemoveFolder(Folder f, Point? at = null)
    {
        int n = store?.Project.Tracks.Count(t => t.FolderId == f.Id) ?? 0;
        if (n == 0) return RemoveFolderNow(f, false);
        if (store == null || restoring) return Task.CompletedTask;
        var list = new List<object>
        {
            new MenuItem { Header = Loc.T("folder.remove.ask", f.Name), IsEnabled = false, FontSize = 11 },
            MenuAction(Loc.T("folder.remove.only"), () => RemoveFolderNow(f, false), tip: Loc.T("folder.remove.only.tip")),
            MenuAction(n == 1 ? Loc.T("folder.remove.all.one") : Loc.T("folder.remove.all", n), () => RemoveFolderNow(f, true), danger: true, tip: Loc.T("folder.remove.all.tip")),
        };
        var name = Timeline.FolderNameRect(f);
        OpenTimelineMenu(Popup(list, 240), at ?? (name.Y >= 0 && name.Y < Timeline.Bounds.Height ? name.BottomLeft + new Point(0, 4) : new Point(20, 40)), 110);
        return Task.CompletedTask;
    }

    Task RemoveFolderNow(Folder f, bool withTracks) =>
        EditTimeline(p => { TrackFolders.Remove(p, f.Id, withTracks); return withTracks && sel?.Track.FolderId == f.Id ? null : sel?.Track.Id; }, "folder-removed");

    Task NewFolder(IReadOnlyCollection<string>? trackIds = null) => EditTimeline(p => TrackFolders.Add(p, null, trackIds), "folder-added");

    void RenameFolder(Folder f, Control anchor, Point? at) =>
        AskName(anchor, at, Loc.T("folder.rename"), f.Name, Loc.T("folder.rename.ok"), name => FolderEdit(f, x => x.Name = name, "name"));

    /// <summary>A small card with a name box: Enter or the button accepts, Esc or a click outside cancels.</summary>
    void AskName(Control anchor, Point? at, string title, string initial, string action, Action<string> done, string? note = null)
    {
        var box = new TextBox { Classes = { "field" }, Text = initial, Width = 240 };
        var ok = new Button { Classes = { "btn", "sm", "primary" }, Content = action, HorizontalAlignment = HorizontalAlignment.Right };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Classes = { "label" }, Text = title.ToUpperInvariant() });
        body.Children.Add(box);
        if (note != null) body.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 });
        body.Children.Add(ok);
        var card = new Border
        {
            Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        var flyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" } };
        if (at is { } p)
        {
            flyout.Placement = PlacementMode.AnchorAndGravity;
            flyout.PlacementAnchor = PopupAnchor.TopLeft;
            flyout.PlacementGravity = PopupGravity.BottomRight;
            flyout.HorizontalOffset = p.X;
            flyout.VerticalOffset = p.Y;
        }
        else flyout.Placement = PlacementMode.BottomEdgeAlignedLeft;
        void Commit()
        {
            string name = box.Text?.Trim() ?? "";
            if (name.Length == 0) return;
            flyout.Hide();
            done(name);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; }
        };
        ok.Click += (_, _) => Commit();
        flyout.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        flyout.ShowAt(anchor);
    }

    /// <summary>A color square for menu headers (folder colors).</summary>
    static Control Swatch(string? hex) => new Border
    {
        Width = 12, Height = 12, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
        Background = hex == null ? Palette.Accent : Palette.Brush(hex),
    };

    void ShowFolderMenu(Folder f, Point point)
    {
        if (store == null || restoring) return;
        bool enabled = !store.ReadOnly;
        var list = new List<object> { new MenuItem { Header = f.Name, IsEnabled = false, FontSize = 11 } };
        list.Add(MenuAction(Loc.T("folder.rename") + "…", () => { RenameInPlace(f.Id, () => RenameFolder(f, Timeline, point)); return Task.CompletedTask; }, enabled: enabled, icon: ""));
        var color = new MenuItem { Header = MenuHeader(Loc.T("look.color") + "…", Swatch(f.Color)), IsEnabled = enabled };
        color.Click += (_, _) => ShowFolderLook(f, Timeline, point);
        list.Add(color);
        list.Add(MenuAction(Loc.T(f.Collapsed ? "folder.unfold" : "folder.fold"), () => { FolderEdit(f, x => x.Collapsed = !x.Collapsed, "fold", undoable: false); return Task.CompletedTask; },
            enabled: enabled, icon: f.Collapsed ? "" : ""));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("folder.newTrack"), () => EditTimeline(p =>
        {
            string id = ClipEditing.AddEmpty(p, !AudioOnly);
            TrackFolders.MoveInto(p, id, f.Id);
            return id;
        }, "track-added"), enabled: enabled, icon: ""));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("folder.remove"), () => RemoveFolder(f, point), enabled: enabled, danger: true, tip: Loc.T("folder.remove.tip")));
        OpenTimelineMenu(Popup(list, 240), point, 200);
    }

    /// <summary>A menu header with a leading control (a swatch) and its text.</summary>
    static Control MenuHeader(string text, Control lead)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(lead);
        header.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return header;
    }

    /// <summary>The track menu's "Carpeta" entries: out of its folder, into another one, or a new folder with it. On a
    /// track picked with others they act on all the picked ones.</summary>
    MenuItem FolderSubmenu(LiveTrack track, bool enabled)
    {
        var items = new List<object>();
        var p = store!.Project;
        var group = Timeline.PickedTrackIds.Contains(track.Track.Id) ? Timeline.PickedTrackIds : [track.Track.Id];
        var current = TrackFolders.Of(p, track.Track);
        async Task MoveGroup(Action<Project> move)
        {
            await EditTimeline(x => { move(x); return track.Track.Id; }, "tracks-reordered");
            Timeline.PickTracks(group);
        }
        if (current != null)
            // each one goes right after the folder's end, so the last one first keeps their order
            items.Add(MenuAction(Loc.T("folder.takeOut", current.Name), () => MoveGroup(x => { foreach (var id in group.Reverse()) TrackFolders.TakeOut(x, id); }),
                enabled: enabled, icon: ""));
        foreach (var f in p.Folders.Where(f => f != current))
        {
            string id = f.Id;
            var item = new MenuItem { Header = MenuHeader(Loc.T("folder.moveTo", f.Name), Swatch(f.Color)), IsEnabled = enabled };
            item.Click += async (_, _) => await MoveGroup(x => { foreach (var t in group) TrackFolders.MoveInto(x, t, id); });
            items.Add(item);
        }
        if (items.Count > 0) items.Add(new Separator());
        items.Add(MenuAction(group.Count > 1 ? Loc.T("folder.newWithMany", group.Count) : Loc.T("folder.newWith"), () => NewFolder(group), enabled: enabled, icon: ""));
        return new MenuItem { Header = MenuHeader(Loc.T("folder.menu"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = items, IsEnabled = enabled };
    }

    // --- folders in the mixer ----------------------------------------------------------------------

    IReadOnlyList<Folder> Folders => store?.Project.Folders ?? [];
    Folder? FolderById(string id) => Folders.FirstOrDefault(f => f.Id == id);
    Folder? FolderOfTrack(LiveTrack t) => t.Track.FolderId is { } id ? FolderById(id) : null;
    /// <summary>The band above the strips where folder names sit (every column gets it while the project has folders).</summary>
    const double FolderBandH = 26;
    /// <summary>A wide folder strip is as wide as the narrowest track strip: its slots' panels need the same room.</summary>
    static double FolderStripW(bool wide) => wide ? WideMin : CompactW;

    /// <summary>A folder bus's slots, for the panels (<see cref="SlotPanels"/>): edited as folder edits, with their undo.</summary>
    SlotOwner SlotsOf(Folder folder)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        return new()
        {
            Slots = () => F().Inserts,
            Edit = (change, key) => FolderEdit(F(), x => change(x.Inserts), key),
            Recalculate = c => RecalculateFolderComp(F(), c),
            AddTip = Loc.T("mix.slot.add.folder.tip"),
        };
    }

    /// <summary>
    /// A folder compressor's preset, measured against its first track with audio (a bus has no take of its own; with
    /// none, its values stay as they are, like an empty track's).
    /// </summary>
    void RecalculateFolderComp(Folder f, CompSettings c)
    {
        if (tracks.FirstOrDefault(t => t.Track.FolderId == f.Id && t.Envelope != null)?.Envelope is not { } env) return;
        double gr = ChannelDsp.RecalculateComp(c, env);
        c.Extra ??= [];
        c.Extra["estGrDb"] = System.Text.Json.JsonSerializer.SerializeToElement(gr);
    }

    /// <summary>A mixer column with the folder band on top (empty for strips at the root and the returns).</summary>
    static Grid Banded(Control? band, Control body)
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("26,*") };
        if (band != null) g.Children.Add(band);
        Grid.SetRow(body, 1);
        g.Children.Add(body);
        return g;
    }

    /// <summary>A folder in the mixer: a tinted group with its name, fold arrow and track count above its strips.</summary>
    Control FolderGroup(Folder folder, bool wide, double stripW)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        var strips = new StackPanel { Orientation = Orientation.Horizontal };
        strips.Children.Add(FolderStrip(folder, wide));
        if (!folder.MixerCollapsed)
            foreach (var t in tracks.Where(t => t.Track.FolderId == id).ToList())
                strips.Children.Add(Strip(t, tracks.IndexOf(t), wide, stripW));

        var fold = new Button { Classes = { "ghost" }, Width = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = folder.MixerCollapsed ? "" : "", FontSize = 9, Foreground = Palette.Dim } };
        ToolTip.SetTip(fold, Loc.T(folder.MixerCollapsed ? "mixer.folder.unfold" : "mixer.folder.fold"));
        fold.Click += (_, _) => FolderEdit(F(), x => x.MixerCollapsed = !x.MixerCollapsed, "mixerFold", undoable: false);
        var icon = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        // the folder strip already shows the folder glyph: a compact band leaves that room to the name
        icon.IsVisible = wide;
        icon.Margin = new Thickness(6, 0, 0, 0);
        // name and count are one text so they trim together when the band is as narrow as a folded compact strip
        var nameRun = new Run { FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg };
        var count = new Run { FontSize = 10.5, Foreground = Palette.Faint };
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, Margin = new Thickness(6, 0, 0, 0) };
        name.Inlines!.AddRange([nameRun, new Run("  "), count]);
        ToolTip.SetTip(name, Loc.T("mixer.folder.name.tip"));
        name.DoubleTapped += (_, _) => RenameFolder(F(), name, null);
        // the band never widens the group: the strips set the width and the name trims to it
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(8, 4, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(fold);
        Grid.SetColumn(icon, 1); row.Children.Add(icon);
        Grid.SetColumn(name, 2); row.Children.Add(name);
        var band = new NoWidth { Child = row };
        var decor = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(1, 2, 1, StripGap / 2), IsHitTestVisible = false };
        mx.Refresh.Add(() =>
        {
            var f = F();
            var c = TimelineView.FolderColor(f);
            int n = tracks.Count(t => t.Track.FolderId == id);
            nameRun.Text = f.Name;
            icon.Foreground = Palette.Alpha(c, 1);
            count.Text = n == 0 ? Loc.T("folder.empty") : n == 1 ? Loc.T("folder.count.one") : Loc.T("folder.count", n);
            decor.Background = Palette.Alpha(c, 0.06);
            decor.BorderBrush = Palette.Alpha(c, 0.35);
        });
        return new Grid { Children = { decor, Banded(band, strips) } };
    }

    /// <summary>
    /// The folder's own strip: its fader multiplies its tracks' faders, M and S act on all of them. Wide, it shows the
    /// bus's insert slots like a track strip; the meter shows the bus's output while it has slots, else the loudest track.
    /// </summary>
    Control FolderStrip(Folder folder, bool wide)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        var box = StripCard(FolderStripW(wide));
        var top = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        // a click on the folder's strip selects the folder, like a track's strip selects its track
        box.Cursor = new Cursor(StandardCursorType.Hand);
        box.PointerPressed += (_, e) => { if (e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) Timeline.SelectFolder(id); };
        double btn = wide ? 28 : 24;
        var mute = StripToggle("M", Loc.T("tl.tip.folderMute"), btn, () => F().Muted, false, () => FolderEdit(F(), x => x.Muted = !x.Muted, "mute"));
        var solo = StripToggle("S", Loc.T("tl.tip.folderSolo"), btn, () => F().Solo, true, () => FolderEdit(F(), x => x.Solo = !x.Solo, "solo"));
        var fader = new Fader();
        fader.Changed += v => FolderEdit(F(), x => x.Volume = v, "volume");
        ToolTip.SetTip(fader, Loc.T("mixer.folder.fader.tip"));
        var db = new TextBlock { Classes = { "mono" }, FontSize = wide ? 11 : 10, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        var meter = new MixerMeter();
        mx.FolderMeters.Add((id, meter));
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { fader, meter } };
        var tag = new TextBlock { Text = Loc.T("mixer.folderTag"), FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(tag, Loc.T("mixer.folderTag.tip"));
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = wide ? 16 : 13, HorizontalAlignment = HorizontalAlignment.Center };
        mx.Refresh.Add(() =>
        {
            var f = F();
            var c = TimelineView.FolderColor(f);
            bool selected = Timeline.SelectedFolderId == id;
            top.Background = selected ? Palette.Accent : Palette.Alpha(c, 1);
            box.Background = selected ? Palette.Raised : Palette.Panel;
            box.BorderBrush = selected ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line;
            glyph.Foreground = Palette.Alpha(c, 1);
            fader.Value = f.Volume;
            db.Text = $"{Fader.Db(f.Volume)} dB";
        });
        Control head, controls;
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { mute, solo } };
        if (wide)
        {
            var h = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 28 };
            tag.VerticalAlignment = glyph.VerticalAlignment = VerticalAlignment.Center;
            tag.HorizontalAlignment = HorizontalAlignment.Left;
            tag.Margin = new Thickness(8, 0);
            h.Children.Add(glyph);
            Grid.SetColumn(tag, 1); h.Children.Add(tag);
            Grid.SetColumn(toggles, 2); h.Children.Add(toggles);
            head = h;
            // the bus's slots, scrolling like a track strip's effects
            var fx = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 4, 6) };
            wideGr = null;
            SlotPanels(fx, SlotsOf(folder));
            mx.FolderGr[id] = wideGr ?? (_ => { });
            wideGr = null;
            controls = new ScrollViewer { Content = fx, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        else
        {
            head = new StackPanel { Spacing = 4, Children = { glyph, tag } };
            controls = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = { toggles } };
        }
        var content = new Grid();
        content.Children.Add(StripLayout(wide, head, controls, faders, db));
        content.Children.Add(top);
        box.Child = content;
        return box;
    }

    // --- track presets ------------------------------------------------------------------------------

    void SavePreset(LiveTrack track, Point at)
    {
        string name = track.Track.Name;
        AskName(Timeline, at, Loc.T("preset.save"), name, Loc.T("preset.save.ok"), chosen =>
        {
            try
            {
                bool replaced = TrackPresets.All().Any(x => string.Equals(x.Name, chosen.Trim(), StringComparison.OrdinalIgnoreCase));
                TrackPresets.Save(TrackPresets.From(track.Track, chosen));
                Toast(Loc.T(replaced ? "preset.replaced" : "preset.saved", chosen));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Status(Loc.T("preset.saveFail", e.Message));
            }
        }, Loc.T("preset.save.note"));
    }

    /// <summary>The "Plantilla" submenu of the new-track menu: one entry per saved preset, each with its trash can to delete it.</summary>
    MenuItem PresetSubmenu(bool enabled)
    {
        var items = new List<object>();
        var presets = TrackPresets.All();
        foreach (var preset in presets)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinWidth = 200 };
            var badge = new TrackIconView { Icon = preset.Icon, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                Foreground = preset.Color != null ? Palette.Brush(preset.Color) : Palette.Dim, IsVisible = preset.Icon != null };
            row.Children.Add(badge);
            var name = new TextBlock { Text = preset.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var delete = new Button { Classes = { "ghost" }, Content = new TextBlock { Text = "", Classes = { "icon" }, FontSize = 11 }, Width = 22, Height = 22, MinHeight = 22,
                Padding = new Thickness(0), Margin = new Thickness(12, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(delete, Loc.T("preset.delete.tip"));
            Grid.SetColumn(delete, 2); row.Children.Add(delete);
            var item = new MenuItem { Header = row, IsEnabled = enabled };
            ToolTip.SetTip(item, preset.Summary);
            ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
            item.Click += async (_, _) => await EditTimeline(p => TrackPresets.AddTo(p, preset, AudioOnly), "track-added");
            delete.Click += (_, e) =>
            {
                e.Handled = true;
                timelineMenu?.Close();
                try
                {
                    TrackPresets.Delete(preset.Id);
                    Status(Loc.T("preset.deleted", preset.Name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status(Loc.T("preset.saveFail", ex.Message)); }
            };
            items.Add(item);
        }
        if (presets.Count == 0)
            items.Add(new MenuItem { IsEnabled = false, Header = new TextBlock { Text = Loc.T("preset.none"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 } });
        return new MenuItem { Header = MenuHeader(Loc.T("preset.menu"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = items };
    }

    // --- the folder in the inspector ----------------------------------------------------------------

    readonly StackPanel folderPanel = new() { Spacing = 12, IsVisible = false };
    readonly List<Action> folderRefresh = [];
    readonly TextBox folderName = new() { Classes = { "title" } };

    /// <summary>What the inspector shows while a folder is selected: its name, color, fader, M / S and its actions.</summary>
    void InitFolderInspector()
    {
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 18 };
        var badge = new Button { Classes = { "ghost" }, Content = glyph, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(badge, Loc.T("folder.color.tip"));
        badge.Click += (_, _) => { if (SelectedFolder is { } f) ShowFolderLook(f, badge); };
        ToolTip.SetTip(folderName, Loc.T("folder.name.tip"));
        folderName.Margin = new Thickness(4, 0, 0, 0);
        folderName.TextChanged += (_, _) =>
        {
            if (syncing || SelectedFolder is not { } f) return;
            string name = folderName.Text?.Trim() ?? "";
            if (name.Length > 0 && name != f.Name) FolderEdit(f, x => x.Name = name, "name");
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
        head.Children.Add(badge);
        Grid.SetColumn(folderName, 1); head.Children.Add(folderName);
        var count = new TextBlock { FontSize = 11, Foreground = Palette.Faint, Margin = new Thickness(36, -6, 0, 0) };
        folderPanel.Children.Add(head);
        folderPanel.Children.Add(count);

        var card = new StackPanel { Spacing = 14 };
        card.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("folder.color").ToUpperInvariant() });
        var paintAsk = FolderTracksAsk(() => SelectedFolder, folderRefresh);
        card.Children.Add(ColorChips(() => SelectedFolder?.Color, hex =>
        {
            if (SelectedFolder is not { } f) return;
            FolderEdit(f, x => x.Color = hex, "color");
            paintAsk.Ask(f);
        }, folderRefresh, 16));
        card.Children.Add(paintAsk.View);
        card.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("folder.mix").ToUpperInvariant(), Margin = new Thickness(0, 4, 0, 0) });
        var volume = new InspectorSlider(Loc.T("insp.volume"), 0, 1.5, 0.01, 1, v => $"{v * 100:0}%",
            v => { if (!syncing && SelectedFolder is { } f) FolderEdit(f, x => x.Volume = v, "volume"); });
        card.Children.Add(volume);
        var mute = new ToggleButton { Classes = { "chip" }, Content = "Mute", MinHeight = 30, Padding = new Thickness(14, 0) };
        var solo = new ToggleButton { Classes = { "chip" }, Content = "Solo", MinHeight = 30, Padding = new Thickness(14, 0) };
        ToolTip.SetTip(mute, Loc.T("tl.tip.folderMute"));
        ToolTip.SetTip(solo, Loc.T("tl.tip.folderSolo"));
        mute.IsCheckedChanged += (_, _) => { if (!syncing && SelectedFolder is { } f && f.Muted != (mute.IsChecked == true)) FolderEdit(f, x => x.Muted = mute.IsChecked == true, "mute"); };
        solo.IsCheckedChanged += (_, _) => { if (!syncing && SelectedFolder is { } f && f.Solo != (solo.IsChecked == true)) FolderEdit(f, x => x.Solo = solo.IsChecked == true, "solo"); };
        card.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { mute, solo } });
        card.Children.Add(Help(Loc.T("folder.mix.hint")));
        card.Children.Add(Help(Loc.T("folder.fx.hint")));
        folderPanel.Children.Add(new Border { Classes = { "section" }, Background = Palette.Panel, BorderThickness = new Thickness(0, 1), CornerRadius = new CornerRadius(0),
            Margin = new Thickness(-14, 4, -14, 0), Padding = new Thickness(16, 14, 16, 16), Child = card });

        var add = new Button { Classes = { "chip" }, Content = Loc.T("folder.newTrack"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 32 };
        add.Click += async (_, _) =>
        {
            if (SelectedFolder is not { } f) return;
            string id = f.Id;
            await EditTimeline(p =>
            {
                string track = ClipEditing.AddEmpty(p, !AudioOnly);
                TrackFolders.MoveInto(p, track, id);
                return track;
            }, "track-added");
        };
        var remove = RemoveButton(Loc.T("folder.remove"));
        ToolTip.SetTip(remove, Loc.T("folder.remove.tip"));
        remove.Click += async (_, _) => { if (SelectedFolder is { } f) await RemoveFolder(f); };
        folderPanel.Children.Add(add);
        folderPanel.Children.Add(remove);

        folderRefresh.Add(() =>
        {
            if (SelectedFolder is not { } f) return;
            glyph.Foreground = Palette.Alpha(TimelineView.FolderColor(f), 1);
            if (!folderName.IsFocused || folderName.Text?.Trim() != f.Name) folderName.Text = f.Name;
            int n = tracks.Count(t => t.Track.FolderId == f.Id);
            count.Text = n == 0 ? Loc.T("folder.empty") : n == 1 ? Loc.T("folder.count.one") : Loc.T("folder.count", n);
            volume.Set(f.Volume, null);
            mute.IsChecked = f.Muted;
            solo.IsChecked = f.Solo;
        });
        Inspector.Children.Add(folderPanel);
    }
}
