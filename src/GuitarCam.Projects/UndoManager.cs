using System.Text.Json;

namespace GuitarCam.Projects;

/// <summary>
/// Port of the editable snapshots in GuitarCam/src/App.tsx: lanes, layout, aspect and bus/video settings,
/// plus the native project mode (video / solo audio), track folders, the extra returns and markers (the A–B loop range is transport
/// state and stays out, like the tempo).
/// Mutable native objects are serialized for isolation; media, exports, log, tempo and project identity
/// stay outside undo, like the web. Same 200-step limit and 800 ms coalescing per nonempty key.
/// </summary>
public sealed class UndoManager(TimeProvider? timeProvider = null)
{
    // These limits match checkpoint() in App.tsx.
    const int Capacity = 200;
    static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(800);
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    readonly List<string> undo = [], redo = [];
    string lastKey = "";
    long lastAt;

    /// <summary>Only the editable state; serialized before every edit, never the immutable recordings.</summary>
    sealed record Editable(List<Track> Tracks, string Layout, string Aspect, ReverbSettings Reverb,
        DelaySettings Delay, ColorGrade Color, VideoFx VideoFx, string? Mode = null, List<Folder>? Folders = null,
        List<Marker>? Markers = null, List<ReturnBus>? Returns = null);

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Checkpoint(Project project, string key = "")
    {
        long now = clock.GetTimestamp();
        bool merge = key.Length > 0 && key == lastKey && clock.GetElapsedTime(lastAt, now) < MergeWindow;
        lastKey = key;
        lastAt = now;
        if (merge) return;
        undo.Add(Capture(project));
        if (undo.Count > Capacity) undo.RemoveAt(0);
        redo.Clear();
    }

    public bool Undo(Project project) => Move(project, undo, redo);
    public bool Redo(Project project) => Move(project, redo, undo);

    bool Move(Project project, List<string> from, List<string> to)
    {
        if (from.Count == 0) return false;
        string json = from[^1];
        var state = JsonSerializer.Deserialize<Editable>(json, ProjectStore.Json)!;
        to.Add(Capture(project));
        from.RemoveAt(from.Count - 1);
        project.Tracks = state.Tracks;
        project.Folders = state.Folders ?? [];
        project.Markers = state.Markers ?? [];
        var s = project.Settings;
        s.Layout = state.Layout;
        s.Aspect = state.Aspect;
        s.Reverb = state.Reverb;
        s.Delay = state.Delay;
        s.Color = state.Color;
        s.VideoFx = state.VideoFx;
        s.Mode = state.Mode ?? s.Mode;
        s.Returns = state.Returns ?? [];
        project.Normalize();
        lastKey = "";
        return true;
    }

    static string Capture(Project p) => JsonSerializer.Serialize(new Editable(p.Tracks, p.Settings.Layout,
        p.Settings.Aspect, p.Settings.Reverb, p.Settings.Delay, p.Settings.Color, p.Settings.VideoFx, p.Settings.Mode, p.Folders, p.Markers, p.Settings.Returns), ProjectStore.Json);

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        lastKey = "";
        lastAt = 0;
    }
}
