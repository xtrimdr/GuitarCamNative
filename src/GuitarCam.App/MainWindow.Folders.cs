using Avalonia;
using Avalonia.Controls;
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
            return sel?.Track.Id;
        }, "tracks-reordered");
        Timeline.FolderFoldRequested += f => FolderEdit(f, x => x.Collapsed = !x.Collapsed, "fold", undoable: false);
        Timeline.FolderToggleRequested += (f, solo) => FolderEdit(f, x => { if (solo) x.Solo = !x.Solo; else x.Muted = !x.Muted; }, solo ? "solo" : "mute");
        Timeline.FolderRemoveRequested += f => _ = RemoveFolder(f);
        Timeline.FolderRenameRequested += (f, at) => RenameFolder(f, Timeline, at);
        Timeline.FolderContextRequested += ShowFolderMenu;
    }

    /// <summary>Tells every live track what its folder does to it (read by the audio thread on the next block).</summary>
    static void ApplyFolderMix(Project p, IReadOnlyList<LiveTrack> list)
    {
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

    Task RemoveFolder(Folder f) => EditTimeline(p => { TrackFolders.Remove(p, f.Id); return sel?.Track.Id; }, "folder-removed");

    Task NewFolder(IReadOnlyCollection<string>? trackIds = null) => EditTimeline(p =>
    {
        TrackFolders.Add(p, null, trackIds);
        return trackIds?.FirstOrDefault() ?? sel?.Track.Id;
    }, "folder-added");

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
        list.Add(MenuAction(Loc.T("folder.rename") + "…", () => { RenameFolder(f, Timeline, point); return Task.CompletedTask; }, enabled: enabled, icon: ""));
        var colors = new List<object>();
        foreach (string? hex in new string?[] { null }.Concat(TrackIcons.Colors))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(Swatch(hex));
            header.Children.Add(new TextBlock { Text = hex == null ? Loc.T("look.color.none") : Loc.T("look.color." + Array.IndexOf(TrackIcons.Colors, hex)), FontSize = 12,
                FontWeight = f.Color == hex ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => FolderEdit(f, x => x.Color = hex, "color");
            colors.Add(item);
        }
        list.Add(new MenuItem { Header = MenuHeader(Loc.T("look.color"), Swatch(f.Color)), ItemsSource = colors, IsEnabled = enabled });
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
        list.Add(MenuAction(Loc.T("folder.remove"), () => RemoveFolder(f), enabled: enabled, danger: true, tip: Loc.T("folder.remove.tip")));
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

    /// <summary>The track menu's "Carpeta" entries: out of its folder, into another one, or a new folder with it.</summary>
    MenuItem FolderSubmenu(LiveTrack track, bool enabled)
    {
        var items = new List<object>();
        var p = store!.Project;
        var current = TrackFolders.Of(p, track.Track);
        if (current != null)
            items.Add(MenuAction(Loc.T("folder.takeOut", current.Name), () => EditTimeline(x => { TrackFolders.TakeOut(x, track.Track.Id); return track.Track.Id; }, "tracks-reordered"),
                enabled: enabled, icon: ""));
        foreach (var f in p.Folders.Where(f => f != current))
        {
            string id = f.Id;
            var item = new MenuItem { Header = MenuHeader(Loc.T("folder.moveTo", f.Name), Swatch(f.Color)), IsEnabled = enabled };
            item.Click += async (_, _) => await EditTimeline(x => { TrackFolders.MoveInto(x, track.Track.Id, id); return track.Track.Id; }, "tracks-reordered");
            items.Add(item);
        }
        if (items.Count > 0) items.Add(new Separator());
        items.Add(MenuAction(Loc.T("folder.newWith"), () => NewFolder([track.Track.Id]), enabled: enabled, icon: ""));
        return new MenuItem { Header = MenuHeader(Loc.T("folder.menu"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = items, IsEnabled = enabled };
    }

    // --- folders in the mixer ----------------------------------------------------------------------

    IReadOnlyList<Folder> Folders => store?.Project.Folders ?? [];
    Folder? FolderById(string id) => Folders.FirstOrDefault(f => f.Id == id);
    Folder? FolderOfTrack(LiveTrack t) => t.Track.FolderId is { } id ? FolderById(id) : null;
    /// <summary>The band above the strips where folder names sit (every column gets it while the project has folders).</summary>
    const double FolderBandH = 26;
    static double FolderStripW(bool wide) => wide ? 132 : CompactW;

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
        var name = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        ToolTip.SetTip(name, Loc.T("mixer.folder.name.tip"));
        name.DoubleTapped += (_, _) => RenameFolder(F(), name, null);
        var count = new TextBlock { FontSize = 10.5, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
        var band = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { fold, icon, name, count } };
        var decor = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(1, 2, 1, StripGap / 2), IsHitTestVisible = false };
        mx.Refresh.Add(() =>
        {
            var f = F();
            var c = TimelineView.FolderColor(f);
            int n = tracks.Count(t => t.Track.FolderId == id);
            name.Text = f.Name;
            icon.Foreground = Palette.Alpha(c, 1);
            count.Text = n == 0 ? Loc.T("folder.empty") : n == 1 ? Loc.T("folder.count.one") : Loc.T("folder.count", n);
            decor.Background = Palette.Alpha(c, 0.06);
            decor.BorderBrush = Palette.Alpha(c, 0.35);
        });
        return new Grid { Children = { decor, Banded(band, strips) } };
    }

    /// <summary>The folder's own strip: its fader multiplies its tracks' faders, M and S act on all of them, the meter shows the loudest.</summary>
    Control FolderStrip(Folder folder, bool wide)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        var box = StripCard(FolderStripW(wide));
        var top = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
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
            top.Background = Palette.Alpha(c, 1);
            glyph.Foreground = Palette.Alpha(c, 1);
            fader.Value = f.Volume;
            db.Text = $"{Fader.Db(f.Volume)} dB";
        });
        Control head, controls;
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { mute, solo } };
        if (wide)
        {
            head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = 28, Children = { glyph, tag } };
            tag.VerticalAlignment = glyph.VerticalAlignment = VerticalAlignment.Center;
            toggles.Orientation = Orientation.Vertical;
            controls = toggles;
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

    /// <summary>The "Plantilla" submenu of the new-track menu: one entry per saved preset, each with its × to delete it.</summary>
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
            var delete = new Button { Classes = { "ghost" }, Content = new TextBlock { Text = "", Classes = { "icon" }, FontSize = 9 }, Width = 22, Height = 22, MinHeight = 22,
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
}
