using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WildTrack.App.Audio;
using WildTrack.App.Controls;
using WildTrack.App.Model;
using WildTrack.Audio.Dsp;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// MIDI tracks and the piano roll (native only, stage 1 of the MIDI editor, asked by the user): "+ Nueva pista" adds a
/// MIDI track, its clips are made, moved and resized in the timeline (<see cref="TimelineView"/>) and opened in the
/// piano roll, the fourth view in the mixer's place (<see cref="MixerHost"/>, like Mastering: same height, same spot in
/// solo audio and the Vertical layout). Every edit goes through <see cref="EditTimeline"/>, so it is one undo step and
/// the engine takes the notes again without stopping. Until the sampler (stage 2) a MIDI track plays
/// <see cref="NoteSynth"/>'s test tone.
/// <para>
/// While it is on screen the piano roll follows the timeline (asked by the user, <see cref="FollowPiano"/>): a click on
/// a MIDI clip shows that one, a click elsewhere on another MIDI track shows that track whole, and a click inside
/// another clip in the roll picks it there.
/// </para>
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
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 6),
            MinHeight = 40, Child = BuildPianoHeader(),
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
        pianoRoll.NoteAuditioned += (pitch, velocity) =>
            Audition(store != null && MidiEditing.TryFind(store.Project, pianoClipId) != null ? MidiEditing.Find(store.Project, pianoClipId!).Track.Id : null, pitch, velocity);
        pianoRoll.SeekRequested += engine.Seek;
        pianoRoll.ScrubChanged += Scrub;
        pianoRoll.ClipPicked += clipId =>
        {
            if (store == null || MidiEditing.TryFind(store.Project, clipId) == null) return;
            pianoClipId = clipId;
            RefreshPiano();
            Timeline.SelectMidiClip(TrackIndex(MidiEditing.Find(store.Project, clipId).Track.Id), clipId);
        };
        Timeline.SelectionChanged += FollowPiano;
        pianoRoll.GridMenuRequested += () => ShowPianoGridMenu(pianoRoll);
        if (double.TryParse(AppConfig.Get("pianoVelHeight"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double velH))
            pianoRoll.VelocityHeight = velH;
        pianoRoll.VelocityHeightChanged += h => SaveViewPref("pianoVelHeight", h.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        Timeline.ExtractMidiRequested += ExtractMidiDropped;
    }

    Control BuildPianoHeader()
    {
        var row = new ToolbarPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 15, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = Loc.T("piano.title"), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Line, Margin = new Thickness(4, 0) });
        title.Children.Add(pianoTitle);
        // the track's instrument: which one (a menu to change it) and its window
        title.Children.Add(new Border { Width = 1, Height = 20, Background = Palette.Line, Margin = new Thickness(4, 0) });
        pianoInstrumentButton.Click += (_, _) =>
        {
            if (PianoTrack() is not { } t || store == null) return;
            var menu = Popup(InstrumentItems(t, !store.ReadOnly), 280);
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            OpenAnchoredMenu(menu, pianoInstrumentButton);
        };
        ToolTip.SetTip(pianoInstrumentButton, Loc.T("vst.pick.tip"));
        pianoOpenButton.Click += (_, _) => { if (PianoTrack() is { } t) OpenInstrumentWindow(t); };
        ToolTip.SetTip(pianoOpenButton, Loc.T("vst.openWindow.tip"));
        title.Children.Add(pianoInstrumentButton);
        title.Children.Add(pianoOpenButton);

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
        // groups that don't fit next to the title go down a row instead of drawing over it
        row.Children.Add(title);
        row.Children.Add(BuildPianoVelocityTools());
        row.Children.Add(snapRow);
        return row;
    }

    /// <summary>The velocity lane's step choices (0 = free) in its menu; any other comes from the box.</summary>
    static readonly int[] VelSteps = [0, 4, 8, 10, 16, 32];

    /// <summary>
    /// The velocity lane's tools in the header (asked by the user): show or hide it, pencil or line, and the vertical
    /// step (a box for any value and a menu with the common ones), all remembered in config.json.
    /// </summary>
    Control BuildPianoVelocityTools()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var show = new ToggleButton { Classes = { "chip" }, Content = Loc.T("piano.vel.show"), Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(show, Loc.T("piano.vel.show.tip"));
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var toolButtons = new List<ToggleButton>();
        foreach (var (tool, key) in new[] { (PianoRollView.VelTool.Pencil, "pencil"), (PianoRollView.VelTool.Line, "line") })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = Loc.T($"piano.vel.{key}"), FontSize = 12 };
            ToolTip.SetTip(b, Loc.T($"piano.vel.{key}.tip"));
            b.Click += (_, _) =>
            {
                pianoRoll.VelocityTool = tool;
                foreach (var o in toolButtons) o.IsChecked = o == b;
                SaveViewPref("pianoVelTool", key);
            };
            toolButtons.Add(b);
            tools.Children.Add(b);
        }
        var toolSeg = new Border { Classes = { "seg" }, Child = tools, VerticalAlignment = VerticalAlignment.Center };
        var stepBox = new TextBox
        {
            Classes = { "field", "mono" }, Width = 52, Height = 28, MinHeight = 28, FontSize = 12, Padding = new Thickness(6, 0),
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        ToolTip.SetTip(stepBox, Loc.T("piano.vel.step.tip"));
        var stepMenu = new Button { Classes = { "chip" }, Content = "\u25BE", Height = 28, MinHeight = 28, Width = 28, Padding = new Thickness(0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(stepMenu, Loc.T("piano.vel.step.menu"));
        Action<bool>? showLane = null;
        var stepLabel = new TextBlock { Text = Loc.T("piano.vel.step"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };

        void ShowStep() => stepBox.Text = pianoRoll.VelocityStep > 1 ? pianoRoll.VelocityStep.ToString() : Loc.T("piano.snap.off");
        void SetStep(int v)
        {
            pianoRoll.VelocityStep = v;
            ShowStep();
            SaveViewPref("pianoVelStep", pianoRoll.VelocityStep.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        void ApplyBox()
        {
            string t = stepBox.Text?.Trim() ?? "";
            if (int.TryParse(t, out int v) && v >= 0 && v <= 127) SetStep(v);
            else if (t.Length == 0 || t == Loc.T("piano.snap.off")) SetStep(0);
            else ShowStep();
        }
        stepBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { ApplyBox(); pianoRoll.Focus(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { ShowStep(); pianoRoll.Focus(); e.Handled = true; }
        };
        stepBox.LostFocus += (_, _) => ApplyBox();
        stepMenu.Click += (_, _) =>
        {
            var refresh = new List<Action>();
            var list = new List<object>();
            foreach (int v in VelSteps)
                list.Add(MenuCheck(v == 0 ? Loc.T("piano.snap.off") : v.ToString(), () => pianoRoll.VelocityStep == v || v == 0 && pianoRoll.VelocityStep <= 1, () => SetStep(v), refresh));
            var menu = Popup(list, 140);
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            OpenAnchoredMenu(menu, stepMenu);
        };

        void ShowLane(bool on)
        {
            showLane?.Invoke(on);
            pianoRoll.VelocityShown = on;
            show.IsChecked = on;
            toolSeg.IsEnabled = stepBox.IsEnabled = stepMenu.IsEnabled = on;
            stepLabel.Opacity = on ? 1 : 0.35;
        }
        show.Click += (_, _) =>
        {
            ShowLane(show.IsChecked == true);
            SaveViewPref("pianoVelShown", show.IsChecked == true ? "1" : "0");
        };

        ShowLane(AppConfig.Get("pianoVelShown") != "0");
        var savedTool = AppConfig.Get("pianoVelTool") == "line" ? PianoRollView.VelTool.Line : PianoRollView.VelTool.Pencil;
        pianoRoll.VelocityTool = savedTool;
        toolButtons[(int)savedTool].IsChecked = true;
        pianoRoll.VelocityStep = int.TryParse(AppConfig.Get("pianoVelStep"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int step) ? step : 0;
        ShowStep();

        row.Children.Add(show);
        row.Children.Add(toolSeg);
        row.Children.Add(stepLabel);
        row.Children.Add(stepBox);
        row.Children.Add(stepMenu);
        var adjust = new Button { Classes = { "chip" }, Content = Loc.T("piano.vel.adjust"), Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(adjust, Loc.T("piano.vel.adjust.tip"));
        adjust.Click += (_, _) => ShowVelocityAdjust(adjust);
        row.Children.Add(adjust);
        showLane += on => adjust.IsEnabled = on;
        adjust.IsEnabled = pianoRoll.VelocityShown;
        return row;
    }

    /// <summary>
    /// The velocity tools' card (asked by the user): Escalar (around the average, in %) and Humanizar (± at random),
    /// each applied with its button as one undo step, on the selected notes or the whole clip. Both values are remembered.
    /// </summary>
    void ShowVelocityAdjust(Control anchor)
    {
        static TextBox Box(string text) => new()
        {
            Classes = { "field", "mono" }, Text = text, Width = 64, Height = 28, MinHeight = 28, FontSize = 12, Padding = new Thickness(6, 0),
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        static Button Apply() => new() { Classes = { "btn", "sm", "primary" }, Content = Loc.T("piano.vel.apply"), VerticalAlignment = VerticalAlignment.Center };
        var scaleBox = Box(AppConfig.Get("pianoVelScale") ?? "50");
        var humBox = Box(AppConfig.Get("pianoVelHumanize") ?? "10");
        var scaleOk = Apply();
        var humOk = Apply();
        var target = new TextBlock { FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        void ShowTarget() => target.Text = pianoRoll.ToolTargets == 0 ? Loc.T("piano.vel.none") : Loc.T("piano.vel.target", pianoRoll.ToolTargets);
        ShowTarget();

        Control Row(string label, string unit, TextBox box, Button ok, string hint)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new TextBlock { Text = label, Width = 80, FontSize = 12, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(box);
            line.Children.Add(new TextBlock { Text = unit, Classes = { "dim", "mono" }, Width = 16, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(ok);
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(line);
            block.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 });
            return block;
        }
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("piano.vel.adjust.title").ToUpperInvariant() });
        body.Children.Add(Row(Loc.T("piano.vel.scale"), "%", scaleBox, scaleOk, Loc.T("piano.vel.scale.hint")));
        body.Children.Add(Row(Loc.T("piano.vel.humanize"), "±", humBox, humOk, Loc.T("piano.vel.humanize.hint")));
        body.Children.Add(target);
        var card = new Border
        {
            Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        var flyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.BottomEdgeAlignedRight };

        // the result shows in the card, which stays open to apply again (Ctrl+Z undoes each one)
        void Done(int changed)
        {
            if (changed < 0) return;
            target.Text = changed == 0 ? Loc.T("piano.vel.same") : Loc.T("piano.vel.changed", changed);
        }
        void DoScale()
        {
            if (!double.TryParse(scaleBox.Text?.Trim().TrimEnd('%'), out double pct) || pct < 0 || pct > 400) { scaleBox.Text = "50"; return; }
            SaveViewPref("pianoVelScale", scaleBox.Text!.Trim().TrimEnd('%'));
            Done(pianoRoll.ScaleVelocity(pct));
        }
        void DoHumanize()
        {
            if (!int.TryParse(humBox.Text?.Trim().TrimStart('±'), out int amount) || amount < 0 || amount > 127) { humBox.Text = "10"; return; }
            SaveViewPref("pianoVelHumanize", amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Done(pianoRoll.HumanizeVelocity(amount));
        }
        scaleOk.Click += (_, _) => DoScale();
        humOk.Click += (_, _) => DoHumanize();
        scaleBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { DoScale(); e.Handled = true; } };
        humBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { DoHumanize(); e.Handled = true; } };
        flyout.Opened += (_, _) => { scaleBox.Focus(); scaleBox.SelectAll(); };
        flyout.ShowAt(anchor);
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

    /// <summary>
    /// The piano roll in the mixer's place (Mixer, Mastering and Piano roll share it). With a MIDI track selected that
    /// it doesn't show yet, it opens on that track whole.
    /// </summary>
    void ShowPiano()
    {
        var frame = PianoRollView.Frame.Keep;
        if (sel?.Track.IsMidi == true && sel.Track.Id != PianoTrack()?.Track.Id)
        {
            pianoClipId = PianoClipFor(sel.Track);
            frame = PianoRollView.Frame.Track;
        }
        OpenPianoView(frame);
    }

    void OpenInPiano(string clipId)
    {
        pianoClipId = clipId;
        OpenPianoView(PianoRollView.Frame.Clip);
    }

    void OpenPianoView(PianoRollView.Frame frame)
    {
        pianoView = true;
        masteringView = false;
        SetMixerView(true);
        ApplyMasteringView();
        pianoRoll.SetPlayhead(engine.PositionSec);
        RefreshPiano(frame);
        pianoRoll.Focus();
    }

    /// <summary>
    /// The piano roll follows the timeline's selection while it is on screen (asked by the user): a MIDI clip clicked
    /// is shown (scrolled into sight if it is on the same track, else with its track whole); a click elsewhere on another
    /// MIDI track (its header, an empty stretch) shows that track whole, on the clip under the playhead.
    /// </summary>
    void FollowPiano(int i)
    {
        if (!PianoShown || store == null || i < 0 || i >= tracks.Count || !tracks[i].Track.IsMidi) return;
        var track = tracks[i].Track;
        bool sameTrack = PianoTrack()?.Track.Id == track.Id;
        if (Timeline.SelectedMidiId is { } id && track.MidiClips?.Any(c => c.Id == id) == true)
        {
            if (id == pianoClipId) return;
            pianoClipId = id;
            RefreshPiano(sameTrack ? PianoRollView.Frame.Reveal : PianoRollView.Frame.Track);
            return;
        }
        if (sameTrack) return;
        pianoClipId = PianoClipFor(track);
        RefreshPiano(PianoRollView.Frame.Track);
    }

    /// <summary>
    /// The clip to edit when the piano roll opens on a track: the one selected in the timeline, else the one under the
    /// playhead, the next one after it or the last one.
    /// </summary>
    string? PianoClipFor(WildTrack.Projects.Track track)
    {
        var clips = track.MidiClips;
        if (clips == null || clips.Count == 0) return null;
        if (clips.Any(c => c.Id == Timeline.SelectedMidiId)) return Timeline.SelectedMidiId;
        double pos = engine.PositionSec, bpm = store?.Project.Settings.Bpm ?? 120;
        return (clips.LastOrDefault(c => pos >= c.AtSec && pos < c.EndSec(bpm))
            ?? clips.Where(c => c.AtSec > pos).MinBy(c => c.AtSec)
            ?? clips.MaxBy(c => c.AtSec))!.Id;
    }

    /// <summary>
    /// Hands the piano roll its clip again from the project (after an edit, an undo, a tempo change or opening a
    /// project), with the track's other clips around it; <paramref name="frame"/> says how its view moves.
    /// </summary>
    void RefreshPiano(PianoRollView.Frame frame = PianoRollView.Frame.Keep)
    {
        var p = store?.Project;
        var clip = p == null ? null : MidiEditing.TryFind(p, pianoClipId);
        if (clip == null) pianoClipId = null;
        var track = clip == null ? null : MidiEditing.Find(p!, clip.Id).Track;
        pianoRoll.SetClip(clip, track?.MidiClips, track?.Name, track == null ? null : TrackIcons.ColorOf(track), p?.Settings.Bpm ?? 120, frame);
        pianoTitle.Text = clip == null || track == null ? ""
            : Loc.T("piano.clip", track.Name, $"{clip.LengthBeats / 4:0.##}", clip.Notes.Count);
        RefreshPianoInstrument();
    }

    readonly Button pianoInstrumentButton = new() { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Button pianoOpenButton = new()
    {
        Classes = { "chip" }, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center, Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13 },
    };

    /// <summary>The live track of the clip the piano roll shows, or null.</summary>
    LiveTrack? PianoTrack() => store != null && MidiEditing.TryFind(store.Project, pianoClipId) != null
        ? tracks.FirstOrDefault(t => t.Track.Id == MidiEditing.Find(store.Project, pianoClipId!).Track.Id) : null;

    /// <summary>The instrument chip: its name (or the test tone, or "cargando…"), and the window button lit when it is up.</summary>
    void RefreshPianoInstrument()
    {
        var t = PianoTrack();
        pianoInstrumentButton.IsVisible = pianoOpenButton.IsVisible = t != null;
        if (t == null) return;
        var plugin = t.Track.Instrument;
        string name = plugin == null ? Loc.T("vst.testTone") : instrumentLoads.ContainsKey(t.Track.Id) ? Loc.T("vst.loading", plugin.Name) : plugin.Name;
        pianoInstrumentButton.Content = name + "  ▾";
        pianoOpenButton.IsEnabled = instruments.ContainsKey(t.Track.Id);
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
            MenuAction(Loc.T("menu.copy"), () => CopySelection(cut: false), Shortcuts.Label("copy")),
            MenuAction(Loc.T("menu.cut"), () => CopySelection(cut: true), Shortcuts.Label("cut"), enabled),
            MenuAction(Loc.T("midi.quantize.menu", PianoSnapLabel()), () => Quantize(boxed.Count > 1 ? boxed : [clipId]), Shortcuts.Label("quantize"),
                enabled && pianoRoll.SnapBeats > 0, tip: pianoRoll.SnapBeats > 0 ? null : Loc.T("midi.quantize.free")),
            new Separator(),
            MenuAction(boxed.Count > 1 ? Loc.T("menu.removeClips", boxed.Count) : Loc.T("menu.removeClip"),
                () => boxed.Count > 1 ? DeleteTimelineSelection() : RemoveMidiClip(clipId), "Supr", enabled, danger: true),
        };
        OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
    }
}
