using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GuitarCam.App.Audio;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// MIDI tracks and the piano roll (native only, stage 1 of the MIDI editor, asked by the user): "+ Nueva pista" adds a
/// MIDI track, its clips are made, moved and resized in the timeline (<see cref="TimelineView"/>) and opened in the
/// piano roll, the fourth view in the mixer's place (<see cref="MixerHost"/>, like Mastering: same height, same spot in
/// solo audio and the Vertical layout). Every edit goes through <see cref="EditTimeline"/>, so it is one undo step and
/// the engine takes the notes again without stopping. Until the sampler (stage 2) a MIDI track plays
/// <see cref="NoteSynth"/>'s test tone.
/// </summary>
public partial class MainWindow
{
    readonly PianoRollView pianoRoll = new();
    readonly DockPanel pianoHost = new() { IsVisible = false };
    readonly TextBlock pianoTitle = new() { FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
    bool pianoView;
    /// <summary>The MIDI clip the piano roll shows (it stays when the view closes, so it reopens on it).</summary>
    string? pianoClipId;

    /// <summary>The piano roll is on screen.</summary>
    bool PianoShown => pianoView && mixerView;

    /// <summary>The magnet choices of the piano roll (its own, saved apart from the timeline's): label key and beats.</summary>
    static readonly (string Id, double Beats)[] PianoSnaps = [("1/4", 1), ("1/8", 0.5), ("1/16", 0.25), ("1/32", 0.125), ("1/8T", 1.0 / 3), ("off", 0)];

    void InitPiano()
    {
        var header = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 0),
            Height = 40, Child = BuildPianoHeader(),
        };
        DockPanel.SetDock(header, Dock.Top);
        pianoHost.Children.Add(header);
        pianoHost.Children.Add(pianoRoll);

        pianoRoll.NotesCommitted += async (clipId, notes) =>
        {
            if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return;
            string trackId = MidiEditing.Find(store.Project, clipId).Track.Id;
            await EditTimeline(p => { MidiEditing.SetNotes(p, clipId, notes); return trackId; }, "midi-notes");
            Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
        };
        pianoRoll.NoteAuditioned += (pitch, velocity) => engine.PreviewNote(NoteSynth.Audition(pitch, velocity, PlaybackEngine.SampleRate));
        pianoRoll.SeekRequested += engine.Seek;
        pianoRoll.GridMenuRequested += () => ShowPianoGridMenu(pianoRoll);
        PianoViewButton.Click += (_, _) => ShowPiano();

        Timeline.MidiClipChanged += async (clipId, atSec, beats, toTrack) =>
        {
            if (store == null) return;
            string? trackId = null;
            await EditTimeline(p =>
            {
                MidiEditing.Place(p, clipId, atSec, beats);
                if (toTrack != null) MidiEditing.MoveToTrack(p, clipId, toTrack);
                return trackId = MidiEditing.Find(p, clipId).Track.Id;
            }, "midi-clip-moved");
            Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
        };
        Timeline.MidiClipOpenRequested += (_, clipId) => OpenInPiano(clipId);
        Timeline.MidiClipCreateRequested += async (track, sec) =>
        {
            if (store == null || store.ReadOnly) return;
            string? made = null;
            await EditTimeline(p => { made = MidiEditing.AddClip(p, track.Track.Id, sec).Id; return track.Track.Id; }, "midi-clip-added");
            if (made == null) return;
            Timeline.SelectMidiClip(TrackIndex(track.Track.Id), made);
            OpenInPiano(made);
        };
        Timeline.MidiClipContextRequested += ShowMidiClipMenu;
    }

    Control BuildPianoHeader()
    {
        var row = new DockPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 15, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = Loc.T("piano.title"), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Line, Margin = new Thickness(4, 0) });
        title.Children.Add(pianoTitle);
        DockPanel.SetDock(title, Dock.Left);

        // the magnet: its own steps, remembered apart from the timeline's (notes want finer ones than takes)
        string saved = AppConfig.Get("pianoSnap") ?? "1/16";
        var snaps = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<ToggleButton>();
        foreach (var (id, beats) in PianoSnaps)
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = id == "off" ? Loc.T("piano.snap.off") : id, FontSize = 12, IsChecked = id == saved };
            ToolTip.SetTip(b, id == "off" ? Loc.T("piano.snap.off.tip") : Loc.T("piano.snap.tip", id));
            b.Click += (_, _) =>
            {
                pianoRoll.SnapBeats = beats;
                foreach (var o in buttons) o.IsChecked = o == b;
                SaveViewPref("pianoSnap", id);
            };
            buttons.Add(b);
            snaps.Children.Add(b);
            if (id == saved) pianoRoll.SnapBeats = beats;
        }
        var snapRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        snapRow.Children.Add(new TextBlock { Text = Loc.T("piano.snap"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        snapRow.Children.Add(new Border { Classes = { "seg" }, Child = snaps, VerticalAlignment = VerticalAlignment.Center });
        // the grid: the magnet's steps, or its own (the same choices as the timeline's ruler menu)
        pianoRoll.GridMode = AppConfig.Get("pianoGrid") is { } grid && GridIds.Contains(grid) ? grid : "snap";
        pianoGridButton.Click += (_, _) => ShowPianoGridMenu(pianoGridButton);
        ToolTip.SetTip(pianoGridButton, Loc.T("piano.grid.tip"));
        ShowPianoGrid();
        snapRow.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Line, Margin = new Thickness(4, 0) });
        snapRow.Children.Add(pianoGridButton);
        var help = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 14, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        ToolTip.SetTip(help, Loc.T("piano.help"));
        snapRow.Children.Add(help);
        DockPanel.SetDock(snapRow, Dock.Right);
        row.Children.Add(snapRow);
        row.Children.Add(title);
        return row;
    }

    readonly Button pianoGridButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    static string GridLabel(string id) => id switch
    {
        "auto" => Loc.T("grid.auto"), "snap" => Loc.T("grid.snap"), "bar" => Loc.T("snap.label.1"), _ => id,
    };

    void ShowPianoGrid() => pianoGridButton.Content = Loc.T("piano.grid", GridLabel(pianoRoll.GridMode));

    /// <summary>
    /// The piano roll's grid menu (its header chip or the right button on its ruler): the timeline's choices, saved apart
    /// ("pianoGrid", "Igual que el imán" at first). The right button picks and leaves the menu open, like every options menu.
    /// </summary>
    void ShowPianoGridMenu(Control anchor)
    {
        var refresh = new List<Action>();
        var list = new List<object> { new MenuItem { Header = Loc.T("grid.title"), IsEnabled = false, FontSize = 11 } };
        foreach (var id in GridIds)
        {
            string hint = id switch { "auto" => Loc.T("grid.auto.hint"), "snap" => Loc.T("grid.snap.hint"), _ => Loc.T("grid.fixed.hint", GridLabel(id)) };
            list.Add(MenuCheck(GridLabel(id), () => pianoRoll.GridMode == id, () =>
            {
                pianoRoll.GridMode = id;
                ShowPianoGrid();
                SaveViewPref("pianoGrid", id);
            }, refresh, tip: hint));
            if (id == "snap") list.Add(new Separator());
        }
        var menu = Popup(list, 260);
        if (anchor == pianoGridButton) menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        OpenAnchoredMenu(menu, anchor);
    }

    /// <summary>The piano roll in the mixer's place (Mixer, Mastering and Piano roll share it).</summary>
    void ShowPiano()
    {
        pianoView = true;
        masteringView = false;
        // with nothing open yet, the selected MIDI clip (or the first one of the selected track)
        if (pianoClipId == null && store != null)
            pianoClipId = Timeline.SelectedMidiId ?? sel?.Track.MidiClips?.FirstOrDefault()?.Id;
        SetMixerView(true);
        ApplyMasteringView();
        RefreshPiano();
        pianoRoll.Focus();
    }

    void OpenInPiano(string clipId)
    {
        pianoClipId = clipId;
        ShowPiano();
    }

    /// <summary>Hands the piano roll its clip again from the project (after an edit, an undo, a tempo change or opening a project).</summary>
    void RefreshPiano()
    {
        var p = store?.Project;
        var clip = p == null ? null : MidiEditing.TryFind(p, pianoClipId);
        if (clip == null) pianoClipId = null;
        var track = clip == null ? null : MidiEditing.Find(p!, clip.Id).Track;
        pianoRoll.SetClip(clip, track?.Name, track == null ? null : TrackIcons.ColorOf(track), p?.Settings.Bpm ?? 120);
        pianoTitle.Text = clip == null || track == null ? ""
            : Loc.T("piano.clip", track.Name, $"{clip.LengthBeats / 4:0.##}", clip.Notes.Count);
    }

    int TrackIndex(string? trackId) => tracks.FindIndex(t => t.Track.Id == trackId);

    Task NewMidiTrack()
    {
        if (store == null || store.ReadOnly) return Task.CompletedTask;
        return EditTimeline(MidiEditing.AddTrack, "track-added");
    }

    async Task DuplicateMidiClip(string clipId)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return;
        string trackId = MidiEditing.Find(store.Project, clipId).Track.Id;
        string? copy = null;
        await EditTimeline(p => { copy = MidiEditing.Duplicate(p, clipId); return trackId; }, "midi-clip-duplicated");
        if (copy != null) Timeline.SelectMidiClip(TrackIndex(trackId), copy);
    }

    Task RemoveMidiClip(string clipId)
    {
        if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return Task.CompletedTask;
        string trackId = MidiEditing.Find(store.Project, clipId).Track.Id;
        return EditTimeline(p => { MidiEditing.RemoveClip(p, clipId); return trackId; }, "midi-clip-removed");
    }

    void ShowMidiClipMenu(LiveTrack track, string clipId, Point point)
    {
        if (store == null || restoring) return;
        bool enabled = !store.ReadOnly;
        // on one of several boxed clips, duplicate and remove act on them all (takes too)
        var boxed = Timeline.BoxedClipIds.Contains(clipId) ? Timeline.BoxedClipIds.ToList() : [];
        var list = new List<object>
        {
            new MenuItem { Header = track.Track.Name, IsEnabled = false, FontSize = 11 },
            MenuAction(Loc.T("midi.menu.open"), () => { OpenInPiano(clipId); return Task.CompletedTask; }, icon: ""),
            MenuAction(Loc.T("menu.duplicate"), () => boxed.Count > 1 ? DuplicateClips(boxed) : DuplicateMidiClip(clipId), Shortcuts.Label("duplicate"), enabled),
            new Separator(),
            MenuAction(boxed.Count > 1 ? Loc.T("menu.removeClips", boxed.Count) : Loc.T("menu.removeClip"),
                () => boxed.Count > 1 ? DeleteTimelineSelection() : RemoveMidiClip(clipId), "Supr", enabled, danger: true),
        };
        OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
    }
}
