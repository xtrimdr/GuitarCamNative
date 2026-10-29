using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WildTrack.App.Controls;
using WildTrack.App.Model;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// Track folders and track presets (native only, asked by the user). Folders group tracks in the timeline and the
/// mixer and only hold them: no fader, mute, solo or effects (the user's call); their tracks' sum goes to the folder's
/// output and sends (<see cref="ApplyRouting"/>). Moving tracks in and out and removing a folder are timeline edits
/// (<see cref="EditTimeline"/>); renaming, color, output and sends are folder edits with their own undo step
/// (<see cref="FolderEdit"/>); folding is only a view, saved without undo.
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
        Timeline.FolderRemoveRequested += (f, at) => _ = RemoveFolder(f, at);
        Timeline.FolderContextRequested += ShowFolderMenu;
        Timeline.FolderLookRequested += (f, at) => ShowFolderLook(f, Timeline, at);
        InitFolderInspector();
        InitBusInspector();
        InitNameEditor();
        Timeline.RowHeightRequested += SetRowHeight;
        ResetHeightsButton.Click += (_, _) => ResetRowHeights();
    }

    /// <summary>A track's or a folder's own height in px (null = its usual one). A track and a folded folder keep it as
    /// a multiple of the shared height and an open folder as px at the default one, so the slider goes on scaling it.
    /// A view setting like folding: saved, without undo.</summary>
    void SetRowHeight(string id, double? h)
    {
        if (store == null || store.ReadOnly || restoring) return;
        // a folded folder's usual height is its own, not the shared one: any height dragged to stays
        if (FolderById(id) is { Collapsed: true } folded) folded.HeightScale = h / Timeline.TrackHeight;
        else if (FolderById(id) is { } f) f.Height = h is { } px ? Timeline.FolderUnzoomed(px) : null;
        else if (tracks.Find(t => t.Track.Id == id) is { } t) t.Track.HeightScale = Scale(h);
        else return;
        RowHeightsChanged();
    }

    double? Scale(double? h) => h is { } px && Math.Abs(px - Timeline.TrackHeight) >= 1 ? px / Timeline.TrackHeight : null;

    /// <summary>Every track and folder back to its usual height.</summary>
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

    /// <summary>A folder edit: one undo step per control (folding is not one), heard at once, saved, on screen.</summary>
    void FolderEdit(Folder f, Action<Folder> change, string key, bool undoable = true)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        if (undoable) undo.Checkpoint(store.Project, $"folder:{f.Id}:{key}");
        change(f);
        ApplyRouting(store.Project, tracks);
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
        Background = hex == null ? Palette.Dim : Palette.Brush(hex),
    };

    void ShowFolderMenu(Folder f, Point point)
    {
        if (store == null || restoring) return;
        if (f.Separator)
        {
            OpenTimelineMenu(Popup(SeparatorMenu(f, Timeline, point), 240), point, 200);
            return;
        }
        bool enabled = !store.ReadOnly;
        string id = f.Id;
        var refresh = new List<Action>();
        var list = new List<object> { new MenuItem { Header = f.Name, IsEnabled = false, FontSize = 11 } };
        list.Add(MenuAction(Loc.T("folder.rename") + "…", () => { RenameInPlace(f.Id, () => RenameFolder(f, Timeline, point)); return Task.CompletedTask; }, enabled: enabled, icon: ""));
        var color = new MenuItem { Header = MenuHeader(Loc.T("look.color") + "…", Swatch(f.Color)), IsEnabled = enabled };
        color.Click += (_, _) => ShowFolderLook(f, Timeline, point);
        list.Add(color);
        // looked up by id at each click: a right click keeps the menu open while the folder changes under it
        list.Add(MenuChoice(() => FolderById(id)?.Collapsed == true ? (Loc.T("folder.unfold"), "") : (Loc.T("folder.fold"), ""),
            () => { if (FolderById(id) is { } x) FolderEdit(x, y => y.Collapsed = !y.Collapsed, "fold", undoable: false); }, refresh, enabled: enabled));
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

    /// <summary>
    /// A separator's menu, from the timeline or its mixer strip: rename, color, show or hide it in the timeline (it
    /// always shows in the mixer) and remove it.
    /// </summary>
    List<object> SeparatorMenu(Folder f, Control anchor, Point? point)
    {
        bool enabled = store is { ReadOnly: false } && !restoring;
        string id = f.Id;
        var refresh = new List<Action>();
        var list = new List<object> { new MenuItem { Header = f.Name, IsEnabled = false, FontSize = 11 } };
        list.Add(MenuAction(Loc.T("separator.rename") + "…", () =>
        {
            if (FolderById(id) is { } x)
            {
                if (point is { } at) RenameInPlace(id, () => RenameFolder(x, anchor, at));
                else AskName(anchor, null, Loc.T("separator.rename"), x.Name, Loc.T("folder.rename.ok"), n => FolderEdit(x, y => y.Name = n.Trim(), "name"));
            }
            return Task.CompletedTask;
        }, enabled: enabled, icon: ""));
        var color = new MenuItem { Header = MenuHeader(Loc.T("look.color") + "…", Swatch(f.Color)), IsEnabled = enabled };
        color.Click += (_, _) => { if (FolderById(id) is { } x) ShowFolderLook(x, anchor, point); };
        list.Add(color);
        list.Add(MenuChoice(() => FolderById(id)?.HiddenInTimeline == true ? (Loc.T("separator.show"), "") : (Loc.T("separator.hide"), ""),
            () => { if (FolderById(id) is { } x) FolderEdit(x, y => y.HiddenInTimeline = !y.HiddenInTimeline, "hidden"); }, refresh,
            tip: Loc.T("separator.hide.tip"), enabled: enabled));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("separator.remove"), () => FolderById(id) is { } x ? RemoveFolderNow(x, false) : Task.CompletedTask, enabled: enabled, danger: true));
        return list;
    }

    /// <summary>A new separator right above <paramref name="track"/> (above its folder, if it is in one): asked for from the timeline, it shows there.</summary>
    Task NewSeparatorAbove(LiveTrack track) => EditTimeline(p =>
    {
        object item = TrackFolders.Of(p, track.Track) is { } folder ? folder : track.Track;
        TrackFolders.AddSeparator(p, null, TrackFolders.Sequence(p).IndexOf(item), hidden: false);
        return track.Track.Id;
    }, "separator-added");

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
        foreach (var f in p.Folders.Where(f => f != current && !f.Separator))
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
    Control FolderGroup(Folder folder, double stripW)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        var strips = new StackPanel { Orientation = Orientation.Horizontal };
        // the folder's strip drags the whole folder; its open tracks drag on their own (MainWindow.MixerOrder.cs)
        var own = FolderStrip(folder, stripW);
        MixerDraggable(own, folder);
        strips.Children.Add(own);
        if (!folder.MixerCollapsed)
        {
            mx.Order.FolderStrips[id] = own;
            foreach (var t in tracks.Where(t => t.Track.FolderId == id).ToList())
            {
                var card = Strip(t, stripW);
                MixerDraggable(card, t.Track);
                mx.Order.Inner.Add((card, t.Track, id));
                strips.Children.Add(card);
            }
        }

        var fold = new Button { Classes = { "ghost" }, Width = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = folder.MixerCollapsed ? "" : "", FontSize = 9, Foreground = Palette.Dim } };
        ToolTip.SetTip(fold, Loc.T(folder.MixerCollapsed ? "mixer.folder.unfold" : "mixer.folder.fold"));
        fold.Click += (_, _) => FolderEdit(F(), x => x.MixerCollapsed = !x.MixerCollapsed, "mixerFold", undoable: false);
        // name and count are one text so they trim together when the band is as narrow as a folded compact strip
        var nameRun = new Run { FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg };
        var count = new Run { FontSize = 10.5, Foreground = Palette.Faint };
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, Margin = new Thickness(6, 0, 0, 0) };
        name.Inlines!.AddRange([nameRun, new Run("  "), count]);
        ToolTip.SetTip(name, Loc.T("mixer.folder.name.tip"));
        name.DoubleTapped += (_, _) => RenameFolder(F(), name, null);
        // the band never widens the group: the strips set the width and the name trims to it
        // the folder strip already shows the folder glyph: the band leaves that room to the name
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8, 4, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(fold);
        Grid.SetColumn(name, 1); row.Children.Add(name);
        var band = new NoWidth { Child = row };
        var decor = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(1, 2, 1, StripGap / 2), IsHitTestVisible = false };
        mx.Refresh.Add(() =>
        {
            var f = F();
            var c = TimelineView.FolderColor(f);
            int n = tracks.Count(t => t.Track.FolderId == id);
            nameRun.Text = f.Name;
            count.Text = n == 0 ? Loc.T("folder.empty") : n == 1 ? Loc.T("folder.count.one") : Loc.T("folder.count", n);
            decor.Background = Palette.Alpha(c, 0.06);
            decor.BorderBrush = Palette.Alpha(c, 0.35);
        });
        return new Grid { Children = { decor, Banded(band, strips) } };
    }

    /// <summary>
    /// The folder's own strip: it only holds its tracks, so no fader, M, S or effects. Its meter shows the sum of its
    /// tracks (what goes to its output); its sends show as bars and its output under them. It takes the mixer's zoom
    /// width and gives way as it narrows, like the other strips.
    /// </summary>
    Control FolderStrip(Folder folder, double width)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        // as wide as the other strips (asked by the user: it follows the mixer's zoom)
        var box = StripCard(width);
        mx.Boxes.Add(box);
        // a click on the folder's strip selects the folder, like a track's strip selects its track
        var top = StripCardLook(box, () => Timeline.SelectedFolderId == id, () => false, () => TimelineView.FolderColor(F()), () => Timeline.SelectFolder(id));
        var meter = new MixerMeter();
        mx.NodeMeters.Add((id, meter, null));
        var tag = new TextBlock { Text = Loc.T("mixer.folderTag"), FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(tag, Loc.T("mixer.folderTag.tip"));
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
        var sum = new TextBlock { Text = Loc.T("mixer.folder.sum"), FontSize = 10, Foreground = Palette.Faint };
        ToolTip.SetTip(sum, Loc.T("mixer.folder.sum.tip"));
        mx.Refresh.Add(() =>
        {
            glyph.Foreground = Palette.Alpha(TimelineView.FolderColor(F()), 1);
        });
        var head = new StackPanel { Spacing = 4, Children = { glyph, tag } };
        // its icon grows with the strip, like a channel's; "CARPETA" goes when it doesn't fit
        mx.Resize.Add(w =>
        {
            glyph.FontSize = Math.Clamp(Math.Round(10 + w * 0.045), 12, 17);
            tag.IsVisible = w >= HeadNameW;
        });
        var content = new Grid();
        var layout = StripLayout(head, CompactFolderControls(RouterOf(folder)), meter, sum);
        NarrowParts(layout, null, null, meter, sum);
        content.Children.Add(layout);
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

    /// <summary>
    /// The track menu's "Usar plantilla": puts a saved preset's sound on this track, keeping its takes, name, look and
    /// routing (<see cref="TrackPresets.ApplyTo"/>). One undo step; the compressors are measured again on its takes.
    /// </summary>
    MenuItem ApplyPresetSubmenu(LiveTrack track, bool enabled)
    {
        var items = new List<object>();
        var presets = TrackPresets.All();
        foreach (var preset in presets)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 180 };
            row.Children.Add(new TrackIconView { Icon = preset.Icon, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                Foreground = preset.Color != null ? Palette.Brush(preset.Color) : Palette.Dim, IsVisible = preset.Icon != null });
            row.Children.Add(new TextBlock { Text = preset.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = row, IsEnabled = enabled };
            ToolTip.SetTip(item, preset.Summary);
            ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
            item.Click += (_, _) => ApplyPreset(track, preset);
            items.Add(item);
        }
        if (presets.Count == 0)
            items.Add(new MenuItem { IsEnabled = false, Header = new TextBlock { Text = Loc.T("preset.none"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 } });
        var menu = new MenuItem { Header = MenuHeader(Loc.T("preset.apply"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = items };
        ToolTip.SetTip(menu, Loc.T("preset.apply.tip"));
        ToolTip.SetShowDelay(menu, (int)MenuTipDelay.TotalMilliseconds);
        return menu;
    }

    void ApplyPreset(LiveTrack track, TrackPreset preset)
    {
        if (store == null) return;
        int skipped = 0;
        EditTrack(track, x =>
        {
            skipped = TrackPresets.ApplyTo(store.Project, x.Track, preset);
            // a preset's compressor was measured on another take: measured again on this one, unless it is manual
            if (x.Envelope != null)
                foreach (var s in x.Channel.Slots)
                    if (s.Comp is { } c && c.Preset != "custom") RecalculateInspectorComp(x, c);
        }, $"preset.{preset.Id}");
        RefreshMixer();
        RebuildInspectorSends(track);
        Toast(Loc.T("preset.applied", preset.Name, track.Track.Name) + (skipped > 0 ? " " + Loc.T("preset.applied.skipped", skipped) : ""));
    }

    // --- the folder in the inspector ----------------------------------------------------------------

    readonly StackPanel folderPanel = new() { Spacing = 12, IsVisible = false };
    readonly List<Action> folderRefresh = [];
    readonly TextBox folderName = new() { Classes = { "title" } };

    /// <summary>What the inspector shows while a folder is selected: its name, color, output, sends and its actions.</summary>
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
        card.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("folder.route").ToUpperInvariant(), Margin = new Thickness(0, 4, 0, 0) });
        card.Children.Add(folderRouting);
        card.Children.Add(Help(Loc.T("folder.route.hint")));
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
            RebuildFolderRouting(f);
            foreach (var r in folderRouteRefresh) r();
        });
        Inspector.Children.Add(folderPanel);
    }

    readonly StackPanel folderRouting = new() { Spacing = 10 };
    readonly List<Action> folderRouteRefresh = [];
    string folderRoutingKey = "";

    /// <summary>
    /// The selected folder's output and its sends (a level per bus it can send to; 0 = no send), rebuilt only when the
    /// folder or the buses change; every other edit just refreshes them.
    /// </summary>
    void RebuildFolderRouting(Folder f)
    {
        var router = RouterOf(f);
        var targets = SendTargets(router);
        string key = f.Id + "|" + string.Join("/", targets.Select(b => b.Id + ":" + b.Name)) + "|" + Targets(router).Count;
        if (key == folderRoutingKey) return;
        folderRoutingKey = key;
        folderRouting.Children.Clear();
        folderRouteRefresh.Clear();
        var output = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        output.Children.Add(new TextBlock { Text = Loc.T("route.output"), FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var select = OutputSelect(router, folderRouteRefresh);
        Grid.SetColumn(select, 1); output.Children.Add(select);
        folderRouting.Children.Add(output);
        foreach (var bus in targets)
        {
            string id = bus.Id;
            var slider = new InspectorSlider(Loc.T("route.sendTo", bus.Name), 0, 1, 0.01, 0, v => $"{v * 100:0}%",
                v => { if (!syncing) router.Edit((p, x) => ProjectBuses.SetSend(p, x, id, v, v > 0 ? true : null) != null, $"send.{id}.level"); });
            folderRouteRefresh.Add(() => slider.Set(router.Get() is { } x ? ProjectBuses.SendOf(x, id).Level : 0, null));
            folderRouting.Children.Add(slider);
        }
        if (Targets(router).Count > 0) folderRouting.Children.Add(AddSendButton(router));
    }
}
