using Avalonia;
using Avalonia.Controls;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The A–B loop range and the song's markers (native only, asked by the user), drawn on the strip over the ruler
/// (<see cref="Controls.TimelineView"/>). Markers are edits of the project, one undo step each; the range is transport
/// state like the tempo (saved, outside undo) and turns the loop on when it is drawn or set. The keys work on the
/// playhead, so the guitar never has to leave the hands: I and O set A and B, K drops a marker, comma and period jump.
/// </summary>
public partial class MainWindow
{
    /// <summary>Names offered for a marker in its menu, the parts of a song (they stay in Spanish like "Canal 1").</summary>
    static readonly string[] MarkerNames = ["Intro", "Estrofa", "Estribillo", "Puente", "Solo", "Final"];

    void InitMarkers()
    {
        Timeline.LoopOn = LoopToggle.IsChecked == true;
        LoopToggle.IsCheckedChanged += (_, _) => Timeline.LoopOn = LoopToggle.IsChecked == true;
        Timeline.LoopRangeRequested += (a, b) => SetLoopRange(a, b);
        Timeline.MarkerMoveRequested += (id, sec) => MarkerEdit(p => ProjectMarkers.Move(p, id, sec), $"marker:{id}:move");
        Timeline.MarkerAddRequested += sec => AddMarker(sec, rename: true);
        Timeline.MarkerRenameRequested += BeginNameEdit;
        Timeline.StripContextRequested += ShowStripMenu;
    }

    bool CanEditMarkers => store != null && !store.ReadOnly && !restoring && !RecordingBusy;

    /// <summary>Hands the project's markers and range to the timeline and the range to the engine (after opening, undo, every edit).</summary>
    void RefreshMarkers()
    {
        var p = store?.Project;
        Timeline.Markers = p?.Markers ?? [];
        var loop = p == null ? null : ProjectMarkers.Loop(p.Settings);
        Timeline.LoopRange = loop;
        engine.SetLoopRange(loop);
    }

    void MarkerEdit(Action<Project> change, string key = "")
    {
        if (!CanEditMarkers) return;
        undo.Checkpoint(store!.Project, key);
        try { change(store.Project); }
        catch (ArgumentException e) { Status(e.Message); return; }
        MarkDirty();
        UpdateUndoButtons();
        RefreshMarkers();
    }

    /// <summary>A marker at <paramref name="sec"/>; from the mouse its name opens for writing right away, from a key it doesn't steal the keyboard.</summary>
    void AddMarker(double sec, bool rename)
    {
        if (!CanEditMarkers) return;
        Marker? added = null;
        MarkerEdit(p => added = ProjectMarkers.Add(p, sec));
        if (added == null) return;
        Status(Loc.T("marker.added", added.Name, TransportTime(added.Sec)));
        if (rename) Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Timeline.MarkerRectOf(added.Id) is { } r) BeginNameEdit(added.Id, r);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Sets the A–B range and turns the loop on (a range is drawn to be heard).</summary>
    void SetLoopRange(double a, double b)
    {
        if (store == null || restoring) return;
        try { ProjectMarkers.SetLoop(store.Project.Settings, a, b); }
        catch (ArgumentException e) { Status(e.Message); return; }
        MarkDirty();
        RefreshMarkers();
        LoopToggle.IsChecked = true;
    }

    void ClearLoopRange()
    {
        if (store == null || restoring || ProjectMarkers.Loop(store.Project.Settings) == null) return;
        ProjectMarkers.ClearLoop(store.Project.Settings);
        MarkDirty();
        RefreshMarkers();
        Status(Loc.T("loop.cleared"));
    }

    /// <summary>One bar at the project's tempo: the B a lone A gets when the song has nothing after it.</summary>
    double BarSec => 4 * 60 / engine.Bpm;

    /// <summary>I: A at the playhead; B stays if it is after it, else the song's end (or a bar later).</summary>
    void SetLoopStartHere()
    {
        if (store == null) return;
        double at = engine.PositionSec;
        double end = ProjectMarkers.Loop(store.Project.Settings) is { } r && r.End - at >= ProjectMarkers.MinLoopSec ? r.End
            : engine.LengthSec - at >= ProjectMarkers.MinLoopSec ? engine.LengthSec : at + BarSec;
        SetLoopRange(at, end);
    }

    /// <summary>O: B at the playhead; A stays if it is before it, else the song's start.</summary>
    void SetLoopEndHere()
    {
        if (store == null) return;
        double at = engine.PositionSec;
        double start = ProjectMarkers.Loop(store.Project.Settings) is { } r && at - r.Start >= ProjectMarkers.MinLoopSec ? r.Start : 0;
        SetLoopRange(start, at);
    }

    void JumpToMarker(bool next)
    {
        if (store == null) return;
        double at = engine.PositionSec;
        var m = next ? ProjectMarkers.Next(store.Project, at) : ProjectMarkers.Previous(store.Project, at, engine.Playing && !engine.Stopping);
        if (m != null) engine.Seek(m.Sec);
        else if (!next) engine.Seek(0);
    }

    /// <summary>
    /// Copies the markers as YouTube chapters ("0:00 Intro"…, <see cref="ProjectMarkers.Chapters"/>) to paste in the
    /// video's description; the toast says if YouTube would ignore them and why.
    /// </summary>
    async Task CopyChapters()
    {
        if (store == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var (text, problem) = ProjectMarkers.Chapters(store.Project, engine.LengthSec);
        try
        {
            await clipboard.SetTextAsync(text);
            Toast(problem == null ? Loc.T("marker.chapters.done") : Loc.T("marker.chapters.warn", problem));
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Status(Loc.T("marker.chapters.fail"));
        }
    }

    /// <summary>The strip's right click: on a marker, the marker's things; elsewhere, adding one and the range.</summary>
    void ShowStripMenu(Marker? marker, double sec, Point point)
    {
        if (store == null || restoring) return;
        bool enabled = CanEditMarkers;
        var list = new List<object>();
        if (marker != null)
        {
            string id = marker.Id;
            list.Add(new MenuItem { Header = marker.Name, IsEnabled = false, FontSize = 11 });
            list.Add(MenuAction(Loc.T("marker.go"), () => { engine.Seek(marker.Sec); return Task.CompletedTask; }, icon: ""));
            list.Add(MenuAction(Loc.T("marker.rename") + "…", () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Timeline.MarkerRectOf(id) is { } r) BeginNameEdit(id, r); },
                    Avalonia.Threading.DispatcherPriority.Background);
                return Task.CompletedTask;
            }, enabled: enabled, icon: ""));
            var names = MarkerNames.Select(n => (object)MenuAction(n, () => { MarkerEdit(p => ProjectMarkers.Rename(p, id, n), $"marker:{id}:name"); return Task.CompletedTask; },
                enabled: enabled && marker.Name != n)).ToList();
            list.Add(new MenuItem { Header = MenuHeader(Loc.T("marker.call"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = names, IsEnabled = enabled });
            var next = ProjectMarkers.Next(store.Project, marker.Sec);
            list.Add(MenuAction(Loc.T("marker.loopToNext"), () => { SetLoopRange(marker.Sec, next?.Sec ?? engine.LengthSec); return Task.CompletedTask; },
                enabled: (next?.Sec ?? engine.LengthSec) - marker.Sec >= ProjectMarkers.MinLoopSec, icon: "", tip: Loc.T("marker.loopToNext.tip")));
            list.Add(new Separator());
            list.Add(MenuAction(Loc.T("marker.remove"), () => { MarkerEdit(p => ProjectMarkers.Remove(p, id)); return Task.CompletedTask; }, enabled: enabled, danger: true));
        }
        else
        {
            list.Add(MenuAction(Loc.T("marker.addHere"), () => { AddMarker(sec, rename: true); return Task.CompletedTask; }, Key("marker"), enabled, icon: ""));
            list.Add(new Separator());
            var loop = ProjectMarkers.Loop(store.Project.Settings);
            list.Add(MenuAction(Loc.T("loop.startHere"), () =>
            {
                double end = loop is { } r && r.End - sec >= ProjectMarkers.MinLoopSec ? r.End : Math.Max(engine.LengthSec, sec + BarSec);
                SetLoopRange(sec, end);
                return Task.CompletedTask;
            }, Key("loopIn"), icon: ""));
            list.Add(MenuAction(Loc.T("loop.endHere"), () =>
            {
                SetLoopRange(loop is { } r && sec - r.Start >= ProjectMarkers.MinLoopSec ? r.Start : 0, sec);
                return Task.CompletedTask;
            }, Key("loopOut"), sec >= ProjectMarkers.MinLoopSec));
            list.Add(MenuAction(Loc.T("loop.clear"), () => { ClearLoopRange(); return Task.CompletedTask; }, enabled: loop != null, tip: Loc.T("loop.clear.tip")));
        }
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("marker.chapters"), CopyChapters, enabled: store.Project.Markers.Count > 0, icon: "", tip: Loc.T("marker.chapters.tip")));
        OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));

        static string? Key(string id) => Shortcuts.Label(id) is { Length: > 0 } k ? k : null;
    }
}
