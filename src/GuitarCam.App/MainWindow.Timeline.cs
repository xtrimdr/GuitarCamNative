using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Threading;
using GuitarCam.App.Audio;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>Timeline commands and popup menus from Timeline.tsx; all model changes remain owned by the window.</summary>
public partial class MainWindow
{
    bool timelineSyncing;
    DispatcherTimer? timelinePrefs;
    ContextMenu? timelineMenu;
    static readonly string[] SnapIds = ["off", "bar", "1/2", "1/4", "1/8", "1/8T", "1/16"];
    static readonly string[] SnapLabels = ["Sin imán", "Compás", "1/2", "1/4", "1/8", "1/8T", "1/16"];
    /// <summary>What each grid snaps to, in musician's words: the snap box's tooltip and the hint of each option.</summary>
    static readonly string[] SnapHints = ["Todo se mueve libre, sin pegarse a nada", "Se pega al principio de cada compás", "Se pega cada blanca (medio compás)",
        "Se pega cada negra (cada tiempo)", "Se pega cada corchea", "Se pega cada corchea de tresillo", "Se pega cada semicorchea"];

    void SetRulerMode(bool bars)
    {
        Timeline.RulerInBars = bars;
        ShowRulerMode();
        timelinePrefs?.Stop(); timelinePrefs?.Start();
    }

    /// <summary>The web's two-button ruler switch: the active mode on AccentSoft (Button.ghost.on).</summary>
    void ShowRulerMode()
    {
        bool bars = Timeline.RulerInBars;
        BarsRulerButton.Classes.Set("on", bars);
        TimeRulerButton.Classes.Set("on", !bars);
    }

    /// <summary>The snap box reads as a toggle: lit with the accent (magnet, text and outline) while the grid is on,
    /// plain with "Sin imán" when it is off; the tooltip says what it snaps to right now.</summary>
    void ShowSnap()
    {
        int i = Math.Max(0, Array.IndexOf(SnapIds, Timeline.SnapMode));
        bool on = i > 0;
        SnapIcon.Foreground = on ? Palette.Accent : Palette.Faint;
        SnapBox.Foreground = on ? Palette.Accent : Palette.Dim;
        SnapBox.Background = on ? Palette.AccentSoft : Palette.Raised;
        SnapBox.BorderBrush = on ? (Avalonia.Media.IBrush)this.FindResource("Accent40")! : Palette.Line;
        ToolTip.SetTip(SnapPanel, on
            ? $"Imán a la grilla ({SnapLabels[i]}): {SnapHints[i].ToLowerInvariant()}. Lo usan el cabezal, mover clips, recortes y fades (Alt: sin imán)"
            : "Imán apagado: todo se mueve libre. Elegí una grilla para que el cabezal, los clips, los recortes y los fades se peguen a los tiempos");
    }

    void InitTimelineEditing()
    {
        SnapBox.SetItems(SnapLabels, SnapHints);
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
        BarsRulerButton.Click += (_, _) => SetRulerMode(true);
        TimeRulerButton.Click += (_, _) => SetRulerMode(false);
        ShowRulerMode();
        ShowSnap();
        SnapBox.Selected += i =>
        {
            SnapBox.SelectedIndex = i;
            Timeline.SnapMode = SnapIds[Math.Clamp(i, 0, SnapIds.Length - 1)];
            ShowSnap();
            Prefs();
        };
        TimelineFitButton.Click += (_, _) => Timeline.FitAll();
        TimelineFitButton.IsEnabled = !Timeline.FitMode;
        Timeline.FitModeChanged += fit => TimelineFitButton.IsEnabled = !fit;
        TrackHeightSlider.ValueChanged += (_, e) => { if (!timelineSyncing) Timeline.SetTrackHeight(e.NewValue); };
        Timeline.TrackHeightChanged += value =>
        {
            timelineSyncing = true;
            TrackHeightSlider.Value = value;
            timelineSyncing = false;
            Prefs();
        };
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

    /// <summary>
    /// One checkpoint per committed gesture, applied in place (<see cref="ApplyTracks"/>): it keeps playing and
    /// nothing on screen is rebuilt, like the web, where editing a lane is just a state change.
    /// </summary>
    async Task EditTimeline(Func<Project, string?> edit, string? activity, string? clipId = null)
    {
        if (restoring || store == null || store.ReadOnly || Projects.IsVisible) return;
        var st = store;
        restoring = true;
        undo.Checkpoint(st.Project);
        UpdateUndoButtons();
        try
        {
            string? selected = edit(st.Project);
            if (activity != null) st.Project.AddLog(activity);
            MarkDirty();
            await ApplyTracks(st);
            int index = st.Project.Tracks.FindIndex(t => t.Id == selected);
            Timeline.SelectClip(index, clipId);
        }
        catch (Exception e)
        {
            Status($"No pude completar la edición del timeline: {e.Message}. Podés deshacer el cambio.");
        }
        finally
        {
            restoring = false;
            Inspector.IsEnabled = sel != null;
            MarkDirty();
            UpdateUndoButtons();
            RefreshHistory();
        }
    }

    /// <summary>
    /// Puts the project's tracks in the engine and on screen reusing what is loaded: tracks and clips are
    /// matched by id, so their audio, video decoder, effect state (compressor, tails) and the playhead carry on.
    /// Only media that isn't loaded yet (a take brought back from Historial) is read, in the background;
    /// everything else happens before the next frame, so a released clip never shows at its old place.
    /// </summary>
    async Task ApplyTracks(ProjectStore st)
    {
        var p = st.Project;
        var liveTracks = tracks.ToDictionary(t => t.Track.Id);
        var liveClips = tracks.SelectMany(t => t.Clips).ToDictionary(c => c.Clip.Id);
        var audio = tracks.SelectMany(t => t.Clips).Where(c => c.Media != null)
            .GroupBy(c => c.Media!.Id).ToDictionary(g => g.Key, g => g.First().Audio);
        var missing = p.Tracks.SelectMany(t => t.Clips).Where(c => !audio.ContainsKey(c.MediaId))
            .Select(c => p.FindMedia(c.MediaId)).OfType<Media>().DistinctBy(m => m.Id).ToList();
        if (missing.Count > 0)
        {
            Status("Cargando la toma…");
            var loaded = await Task.Run(() => missing.Select(m =>
            {
                try { return (m.Id, Audio: st.Resolve(m.Audio) is { } path && File.Exists(path) ? new MediaAudio(path) : null); }
                catch { return (m.Id, Audio: (MediaAudio?)null); }
            }).ToList());
            foreach (var (id, a) in loaded) audio[id] = a;
            Status("");
        }

        var used = new HashSet<LiveClip>();
        var list = new List<LiveTrack>();
        foreach (var t in p.Tracks)
        {
            var clips = new List<LiveClip>();
            foreach (var c in t.Clips)
            {
                var m = p.FindMedia(c.MediaId);
                if (liveClips.TryGetValue(c.Id, out var lc) && lc.Media?.Id == m?.Id && used.Add(lc))
                {
                    lc.Clip = c;
                    lc.Media = m;
                    clips.Add(lc);
                }
                else clips.Add(new LiveClip(c, m, m == null ? null : audio.GetValueOrDefault(m.Id)) { VideoPath = st.Resolve(m?.Video) });
            }
            if (liveTracks.TryGetValue(t.Id, out var live))
            {
                live.Track = t;
                live.Clips = clips;
                live.ApplyChannel();
            }
            else live = new LiveTrack(t, clips, PlaybackEngine.SampleRate);
            live.SetProjectSettings(p.Settings);
            list.Add(live);
        }

        var dropped = liveClips.Values.Where(c => !used.Contains(c)).ToList();
        tracks = list;
        engine.UpdateTracks(list);
        Timeline.LengthSec = engine.LengthSec;
        Timeline.UpdateTracks(list);
        Preview.SetProject(list, p.Settings);
        if (dropped.Count > 0) _ = Task.Run(() => { foreach (var c in dropped) c.CloseVideo(); });

        await OpenMissingVideos(st);
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

    /// <summary>The clip of the selected track under the playhead: the selected one if the playhead is on it, else the one on top.</summary>
    LiveClip? ClipUnderPlayhead()
    {
        double at = engine.PositionSec;
        bool Inside(LiveClip c) => at > c.ClipStartSec && at < c.ClipEndSec;
        return sel?.Clips.FirstOrDefault(c => c.Clip.Id == Timeline.SelectedClipId && Inside(c)) ?? sel?.Clips.LastOrDefault(Inside);
    }

    /// <summary>Alt+X, like Studio One: splits the clip under the playhead and selects the right piece.</summary>
    async Task SplitAtPlayhead()
    {
        if (sel == null) { Status("Elegí una pista y poné el cabezal sobre el clip que querés dividir."); return; }
        if (ClipUnderPlayhead() is not { } clip) { Status("El cabezal no está sobre ningún clip de esta pista: ponelo donde querés cortar."); return; }
        string trackId = sel.Track.Id;
        double at = engine.PositionSec, length = clip.LengthSec;
        string? right = null;
        await EditTimeline(p => { right = ClipEditing.Split(p, clip.Clip.Id, at, length); return trackId; }, "clip-split");
        if (right != null) Timeline.SelectClip(Timeline.Selected, right);
    }

    /// <summary>D, like Studio One: copies the selected clip right after its end and selects the copy.</summary>
    async Task DuplicateSelection()
    {
        var clip = sel?.Clips.FirstOrDefault(c => c.Clip.Id == Timeline.SelectedClipId);
        if (sel == null || clip == null) { Status("Elegí el clip que querés duplicar."); return; }
        string trackId = sel.Track.Id;
        string? copy = null;
        await EditTimeline(p => { copy = ClipEditing.Duplicate(p, clip.Clip.Id, clip.LengthSec); return trackId; }, "clip-duplicated");
        if (copy != null) Timeline.SelectClip(Timeline.Selected, copy);
    }

    static MenuItem MenuAction(string label, Func<Task> action, string? hint = null, bool enabled = true, bool danger = false, string? icon = null)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (icon == "empty") text.Children.Add(new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 12, Height = 12, Stroke = Palette.Dim, StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 2 }, VerticalAlignment = VerticalAlignment.Center,
        });
        else if (icon != null) text.Children.Add(new TextBlock { Text = icon, Classes = { "icon" }, FontSize = 12 });
        text.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = danger ? Palette.Rec : Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(text);
        if (hint != null)
        {
            var value = new TextBlock { Text = hint, Classes = { "mono" }, FontSize = 10.5, Foreground = Palette.Faint, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(value, 1);
            header.Children.Add(value);
        }
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (danger) item.Classes.Add("danger");
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
            MenuAction("Pista vacía", () => EditTimeline(ClipEditing.AddEmpty, "track-added"), "grabás después con R", !store.ReadOnly, icon: "empty"),
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
            if (!AudioOnly)
                list.Add(MenuAction(track.Track.Video ? "Sacar del video" : "Mostrar en el video", () => SetInVideo(track, !track.Track.Video),
                    enabled: enabled, icon: "\uE714"));
            list.Add(new Separator());
            double at = engine.PositionSec;
            bool under = at > clip.ClipStartSec + ClipEnvelope.MinClipSec && at < clip.ClipEndSec - ClipEnvelope.MinClipSec;
            list.Add(MenuAction("Dividir en el cabezal", async () =>
            {
                string? right = null;
                await EditTimeline(p => { right = ClipEditing.Split(p, id, at, clip.LengthSec); return track.Track.Id; }, "clip-split");
                if (right != null) Timeline.SelectClip(Timeline.Selected, right);
            }, "Alt+X", enabled && under));
            list.Add(MenuAction("Duplicar", async () =>
            {
                string? copy = null;
                await EditTimeline(p => { copy = ClipEditing.Duplicate(p, id, clip.LengthSec); return track.Track.Id; }, "clip-duplicated");
                if (copy != null) Timeline.SelectClip(Timeline.Selected, copy);
            }, "D", enabled));
            list.Add(MenuAction("Llevar al inicio", () => Command(p => ClipEditing.ToStart(p, id, clip.LengthSec), "clip-moved"), enabled: enabled));
            var w = ClipEditing.Window(clip.Clip, clip.Media, clip.LengthSec);
            list.Add(MenuAction("Fijar el corte en 0", () => Command(p => ClipEditing.FixCut(p, id, clip.LengthSec), "clip-trimmed"),
                w.AnchorCut > 0.0005 ? $"{w.AnchorCut:0.00} s" : null, enabled && w.AnchorCut > 0.0005));
            var c = clip.Clip;
            list.Add(MenuAction("Restaurar recortes y fades", () => Command(p => ClipEditing.Reset(p, id), "clip-reset"),
                enabled: enabled && (c.TrimStartSec != 0 || c.TrimEndSec != 0 || c.FadeInSec != 0 || c.FadeOutSec != 0 || c.VideoFadeInSec != 0 || c.VideoFadeOutSec != 0)));
            list.Add(new Separator());
            list.Add(MenuAction("Quitar clip", () => Command(p => ClipEditing.RemoveClip(p, id), "clip-removed"), "Supr", enabled, danger: true));
        }
        list.Add(MenuAction("Quitar pista", () => EditTimeline(p => { ClipEditing.RemoveTrack(p, track.Track.Id); return null; }, "track-removed"),
            clip == null ? "Supr" : null, enabled, danger: true));
        if (clip != null) list.Add(new MenuItem { IsEnabled = false, Header = new TextBlock
        {
            Text = track.Clips.Count > 1 ? "El archivo del clip queda en Historial." : "Quitar clip deja la pista vacía. El archivo queda en Historial.", FontSize = 10.5,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 212,
        } });
        OpenTimelineMenu(Popup(list, 240), point, clip == null ? 80 : 390);
    }
}
