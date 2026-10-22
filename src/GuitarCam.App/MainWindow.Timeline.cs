using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Threading;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>Timeline commands and popup menus from Timeline.tsx; all model changes remain owned by the window.</summary>
public partial class MainWindow
{
    bool timelineSyncing;
    DispatcherTimer? timelinePrefs;
    ContextMenu? timelineMenu;
    static readonly string[] SnapIds = ["off", "bar", "1/2", "1/4", "1/8", "1/8T", "1/16"];

    void InitTimelineEditing()
    {
        SnapBox.ItemsSource = new[] { "No", "Compás", "1/2", "1/4", "1/8", "1/8T", "1/16" };
        string snap = AppConfig.Get("timelineSnap") ?? "1/4";
        SnapBox.SelectedIndex = Math.Max(0, Array.IndexOf(SnapIds, snap));
        Timeline.SnapMode = SnapIds[SnapBox.SelectedIndex];
        Timeline.RulerInBars = AppConfig.Get("rulerMode") != "time";
        if (double.TryParse(AppConfig.Get("trackHeight"), CultureInfo.InvariantCulture, out double height))
        {
            Timeline.SetTrackHeight(height);
            TrackHeightSlider.Value = Timeline.TrackHeight;
        }
        timelinePrefs = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timelinePrefs.Tick += async (_, _) =>
        {
            timelinePrefs.Stop();
            string mode = Timeline.RulerInBars ? "bars" : "time";
            string grid = Timeline.SnapMode;
            string h = Timeline.TrackHeight.ToString(CultureInfo.InvariantCulture);
            try
            {
                await Task.Run(() =>
                {
                    AppConfig.Set("rulerMode", mode);
                    AppConfig.Set("timelineSnap", grid);
                    AppConfig.Set("trackHeight", h);
                });
            }
            catch (Exception e) { Status($"No pude recordar la vista del timeline: {e.Message}"); }
        };
        Closed += (_, _) => timelinePrefs.Stop();
        void Prefs() { timelinePrefs.Stop(); timelinePrefs.Start(); }
        void Ruler(bool bars)
        {
            Timeline.RulerInBars = bars;
            BarsRulerButton.Foreground = bars ? Palette.Accent : Palette.Faint;
            TimeRulerButton.Foreground = bars ? Palette.Faint : Palette.Accent;
            Prefs();
        }
        BarsRulerButton.Click += (_, _) => Ruler(true);
        TimeRulerButton.Click += (_, _) => Ruler(false);
        BarsRulerButton.Foreground = Timeline.RulerInBars ? Palette.Accent : Palette.Faint;
        TimeRulerButton.Foreground = Timeline.RulerInBars ? Palette.Faint : Palette.Accent;
        SnapBox.SelectionChanged += (_, _) =>
        {
            Timeline.SnapMode = SnapIds[Math.Clamp(SnapBox.SelectedIndex, 0, SnapIds.Length - 1)];
            Prefs();
        };
        TimelineFitButton.Click += (_, _) => Timeline.FitAll();
        TrackHeightSlider.ValueChanged += (_, e) => { if (!timelineSyncing) Timeline.SetTrackHeight(e.NewValue); };
        Timeline.TrackHeightChanged += value =>
        {
            timelineSyncing = true;
            TrackHeightSlider.Value = value;
            timelineSyncing = false;
            Prefs();
        };
        Timeline.EditGestureStarted += engine.Stop;
        Timeline.ChannelRequested += (track, volume, pan) => EditTrack(track, t =>
        {
            if (volume is { } v) t.Channel.Volume = v;
            if (pan is { } p) t.Channel.Pan = p;
        }, volume != null ? "volume" : "pan");
        Timeline.ClipEditCommitted += async (clip, target, swap) => await EditTimeline(p =>
        {
            ClipEditing.Replace(p, clip);
            if (target != null) return ClipEditing.MoveToTrack(p, clip.Id, target, swap);
            return ClipEditing.Find(p, clip.Id).Track.Id;
        }, "clip-edited", clip.Id);
        Timeline.ReorderRequested += async (from, to) => await EditTimeline(p =>
        {
            string id = p.Tracks[from].Id;
            ClipEditing.Reorder(p, from, to);
            return id;
        }, "tracks-reordered");
        Timeline.RemoveTrackRequested += async id => await EditTimeline(p => { ClipEditing.RemoveTrack(p, id); return null; }, "track-removed");
        Timeline.NewTrackRequested += ShowNewTrackMenu;
        Timeline.ClipContextRequested += ShowTimelineMenu;
    }

    /// <summary>One checkpoint per committed gesture; audio stays cached and disk work runs in the background.</summary>
    async Task EditTimeline(Func<Project, string?> edit, string activity, string? clipId = null)
    {
        if (restoring || store == null || store.ReadOnly || Projects.IsVisible) return;
        var st = store;
        var audio = tracks.SelectMany(t => t.Clips).Where(c => c.Media != null)
            .GroupBy(c => c.Media!.Id).ToDictionary(g => g.Key, g => g.First().Audio);
        double position = engine.PositionSec;
        restoring = true;
        engine.Stop();
        undo.Checkpoint(st.Project);
        Inspector.IsEnabled = Timeline.IsEnabled = TimelineToolbar.IsEnabled = SessionChip.IsEnabled = false;
        UpdateUndoButtons();
        try
        {
            string? selected = edit(st.Project);
            st.Project.AddLog(activity);
            MarkDirty();
            saveTimer.Stop();
            await ShowProject(st, audio, true);
            int index = st.Project.Tracks.FindIndex(t => t.Id == selected);
            Timeline.SelectClip(index, clipId);
            engine.Seek(position);
        }
        catch (Exception e)
        {
            Status($"No pude completar la edición del timeline: {e.Message}. Podés deshacer el cambio.");
        }
        finally
        {
            restoring = false;
            Timeline.IsEnabled = TimelineToolbar.IsEnabled = SessionChip.IsEnabled = true;
            Inspector.IsEnabled = sel != null;
            MarkDirty();
            UpdateUndoButtons();
        }
    }

    Task DeleteTimelineSelection()
    {
        if (sel == null || store == null) return Task.CompletedTask;
        string trackId = sel.Track.Id;
        string? clipId = Timeline.SelectedClipId ?? sel.Clips.FirstOrDefault()?.Clip.Id;
        return EditTimeline(p =>
        {
            if (clipId != null) { ClipEditing.RemoveClip(p, clipId); return trackId; }
            ClipEditing.RemoveTrack(p, trackId);
            return null;
        }, clipId != null ? "clip-removed" : "track-removed");
    }

    static MenuItem MenuAction(string label, Func<Task> action, string? hint = null, bool enabled = true)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        if (hint != null)
        {
            var value = new TextBlock { Text = hint, Classes = { "mono" }, FontSize = 10.5, Foreground = Palette.Faint, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(value, 1);
            header.Children.Add(value);
        }
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += async (_, _) => await action();
        return item;
    }

    ContextMenu Popup(IEnumerable<object> items, double width) => new()
    {
        ItemsSource = items,
        Width = width,
        Background = Palette.Panel,
        Foreground = Palette.Fg,
        BorderBrush = Palette.Line,
        BorderThickness = new Thickness(1),
    };

    void OpenTimelineMenu(ContextMenu menu, Point point, double height)
    {
        // Keep menus inside the app viewport, as Timeline.tsx does even near the bottom edge.
        var origin = Timeline.TranslatePoint(default, this) ?? default;
        menu.MaxHeight = Math.Min(320, Bounds.Height - 16);
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementAnchor = PopupAnchor.TopLeft;
        menu.PlacementGravity = PopupGravity.BottomRight;
        menu.HorizontalOffset = Math.Clamp(point.X, 8 - origin.X, Math.Max(8 - origin.X, Bounds.Width - origin.X - menu.Width - 8));
        menu.VerticalOffset = Math.Clamp(point.Y, 8 - origin.Y, Math.Max(8 - origin.Y, Bounds.Height - origin.Y - Math.Min(320, height) - 8));
        timelineMenu?.Close();
        timelineMenu = menu;
        menu.Open(Timeline);
    }

    void ShowNewTrackMenu(Point point)
    {
        if (store == null || restoring) return;
        var p = store.Project;
        var list = new List<object>
        {
            MenuAction("Pista vacía", () => EditTimeline(ClipEditing.AddEmpty, "track-added"), "grabás después con R", !store.ReadOnly),
            new Separator(), new MenuItem { Header = "DESDE EL HISTORIAL", IsEnabled = false, FontSize = 10.5 },
        };
        var used = p.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaId).ToHashSet();
        var unused = p.Media.Where(m => !used.Contains(m.Id)).ToList();
        if (unused.Count == 0) list.Add(new MenuItem { Header = "No hay tomas sin usar.", IsEnabled = false, FontSize = 12 });
        foreach (var m in unused)
        {
            string? selected = sel?.Track.Id;
            list.Add(MenuAction(m.Name, () => EditTimeline(project => ClipEditing.AddMedia(project, m.Id, selected), "clip-added"),
                $"{m.DurationSec:0.0} s", !store.ReadOnly));
        }
        OpenTimelineMenu(Popup(list, 288), point, 100 + Math.Max(1, unused.Count) * 36);
    }

    void ShowTimelineMenu(LiveTrack track, LiveClip? clicked, Point point)
    {
        if (store == null || restoring) return;
        bool enabled = !store.ReadOnly;
        var clip = clicked ?? track.Clips.FirstOrDefault(c => c.Clip.Id == Timeline.SelectedClipId) ?? track.Clips.FirstOrDefault();
        var list = new List<object> { new MenuItem { Header = track.Track.Name, IsEnabled = false, FontSize = 11 } };
        Task Command(Action<Project> action, string type) => EditTimeline(p => { action(p); return track.Track.Id; }, type, clip?.Clip.Id);
        if (clip != null)
        {
            string id = clip.Clip.Id;
            list.Add(MenuAction(track.Channel.Muted ? "Activar sonido" : "Silenciar", () =>
            {
                EditTrack(track, t => t.Channel.Muted = !t.Channel.Muted, "mute");
                return Task.CompletedTask;
            }, "M", enabled));
            list.Add(MenuAction(track.Channel.Solo ? "Quitar solo" : "Solo", () =>
            {
                EditTrack(track, t => t.Channel.Solo = !t.Channel.Solo, "solo");
                return Task.CompletedTask;
            }, "S", enabled));
            list.Add(new Separator());
            list.Add(MenuAction("Llevar al inicio", () => Command(p => ClipEditing.ToStart(p, id), "clip-moved"), enabled: enabled));
            var w = ClipEditing.Window(clip.Clip, clip.Media);
            list.Add(MenuAction("Fijar el corte en 0", () => Command(p => ClipEditing.FixCut(p, id), "clip-trimmed"),
                w.AnchorCut > 0.0005 ? $"{w.AnchorCut:0.00} s" : null, enabled && w.AnchorCut > 0.0005));
            var c = clip.Clip;
            list.Add(MenuAction("Restaurar recortes y fades", () => Command(p => ClipEditing.Reset(p, id), "clip-reset"),
                enabled: enabled && (c.TrimStartSec != 0 || c.TrimEndSec != 0 || c.FadeInSec != 0 || c.FadeOutSec != 0 || c.VideoFadeInSec != 0 || c.VideoFadeOutSec != 0)));
            list.Add(new Separator());
            list.Add(MenuAction("Quitar clip", () => Command(p => ClipEditing.RemoveClip(p, id), "clip-removed"), "Supr", enabled));
        }
        list.Add(MenuAction("Quitar pista", () => EditTimeline(p => { ClipEditing.RemoveTrack(p, track.Track.Id); return null; }, "track-removed"),
            clip == null ? "Supr" : null, enabled));
        if (clip != null) list.Add(new MenuItem { IsEnabled = false, Header = new TextBlock
        {
            Text = "Quitar clip deja la pista vacía. El archivo queda en Historial.", FontSize = 10.5,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 212,
        } });
        OpenTimelineMenu(Popup(list, 240), point, clip == null ? 80 : 320);
    }
}
