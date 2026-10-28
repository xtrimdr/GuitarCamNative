using Avalonia;
using Avalonia.Controls;
using GuitarCam.App.Audio;
using GuitarCam.App.Model;
using GuitarCam.Audio.Midi;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The MIDI keyboard in the window (stage 4 of the MIDI editor, decided with the user): it plays the selected MIDI
/// track (like Logic and Studio One, nothing to arm) and every armed one, with its instrument and its chain, from the
/// input each track picks (<see cref="Track.MidiInput"/>, all of them at first). An armed MIDI track records what it
/// hears into a new clip when the take ends (<see cref="MidiEditing.AddRecorded"/>), next to the audio takes and the
/// camera, quantized to the piano roll's magnet when Configuración → MIDI says so. The inputs themselves are
/// <see cref="MidiSetup"/>'s.
/// </summary>
public partial class MainWindow
{
    /// <summary>The armed MIDI tracks' takes while recording, by track id; empty otherwise.</summary>
    readonly Dictionary<string, MidiTake> midiTakes = [];
    int midiListenKey;

    void InitMidiInput()
    {
        engine.MidiInputs = MidiSetup.Inputs;
        MidiSetup.Changed += () =>
        {
            midiListenKey = 0;
            Timeline.InvalidateVisual();
            if (mixerView) RefreshMixer();
        };
        MidiSetup.Start();
    }

    /// <summary>A MIDI track armed by hand (audio tracks are <see cref="RecordTargets"/>').</summary>
    bool MidiArmed(Track t) => t.IsMidi && armedIds != null && armedIds.Contains(t.Id);

    /// <summary>The armed MIDI tracks, in timeline order.</summary>
    List<Track> MidiRecordTargets() => store == null || armedIds == null ? [] : store.Project.Tracks.Where(MidiArmed).ToList();

    /// <summary>
    /// Hands the engine the tracks that hear the keyboard (the selected MIDI track and the armed ones, each with its
    /// input and, while recording, its take). Every tick: nothing is made unless something changed.
    /// </summary>
    void ShowMidiListeners()
    {
        var h = new HashCode();
        h.Add(MidiSetup.Version);
        h.Add(midiTakes.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (!Hears(t)) continue;
            h.Add(t);
            h.Add(t.Track.MidiInput);
            h.Add(t.Track.MidiChannel);
        }
        int key = h.ToHashCode();
        if (key == midiListenKey) return;
        midiListenKey = key;
        var list = new List<MidiListener>();
        foreach (var t in tracks)
            if (Hears(t))
                list.Add(new MidiListener(t, MidiSetup.PortOf(t.Track.MidiInput), t.Track.MidiChannel, midiTakes.GetValueOrDefault(t.Track.Id)));
        engine.SetMidiListeners([.. list]);
    }

    bool Hears(LiveTrack t) => t.Track.IsMidi && (t == sel || MidiArmed(t.Track));

    // --- the track's input --------------------------------------------------------------------------

    /// <summary>The input as the header chip shows it, four letters at most: "MIDI" for all of them.</summary>
    static string MidiInputShort(Track t)
    {
        if (t.MidiInput == null) return t.MidiChannel == 0 ? "MIDI" : $"C{t.MidiChannel}";
        string name = MidiSetup.Shown(t.MidiInput).Trim();
        return name.Length <= 4 ? name : name[..4];
    }

    static string MidiInputLabel(Track t)
    {
        string device = t.MidiInput == null ? Loc.T("midi.in.all") : MidiSetup.Shown(t.MidiInput);
        return t.MidiChannel == 0 ? device : Loc.T("midi.in.withChannel", device, t.MidiChannel);
    }

    /// <summary>
    /// A MIDI track's input menu (the chip of an armed track, or its context menu): all inputs, each device plugged in
    /// or remembered, and the channel. The right button picks and leaves it open, like every options menu.
    /// </summary>
    void ShowMidiInputMenu(LiveTrack t, Control anchor, Point? point = null)
    {
        if (point == null && PressClosedMenuOf(anchor)) return;
        if (store == null || store.ReadOnly || RecordingBusy) return;
        var refresh = new List<Action>();
        var list = new List<object>
        {
            new MenuItem { Header = Loc.T("midi.in.from"), IsEnabled = false, FontSize = 10.5 },
            MenuCheck(Loc.T("midi.in.all"), () => t.Track.MidiInput == null, () => SetMidiInput(t, null, t.Track.MidiChannel), refresh, Loc.T("midi.in.all.hint")),
        };
        var ports = MidiSetup.Inputs.Ports;
        var names = ports.Select(p => p.Device.Name).ToList();
        foreach (var s in MidiSetup.Settings.Inputs)
            if (!names.Contains(s.Name)) names.Add(s.Name);
        if (t.Track.MidiInput is { } mine && !names.Contains(mine)) names.Add(mine);
        if (names.Count > 0) list.Add(new Separator());
        else list.Add(new MenuItem { Header = Loc.T("midi.in.none"), IsEnabled = false, FontSize = 12 });
        foreach (var name in names)
        {
            var port = ports.FirstOrDefault(p => p.Device.Name == name);
            string hint = port == null ? Loc.T("midi.in.unplugged") : !port.Open ? (port.Error != null ? Loc.T("midi.in.failed") : Loc.T("midi.in.off")) : "";
            list.Add(MenuCheck(MidiSetup.Shown(name), () => t.Track.MidiInput == name, () => SetMidiInput(t, name, t.Track.MidiChannel), refresh, hint));
        }
        list.Add(new Separator());
        var channels = new MenuItem { Header = Loc.T("midi.in.channel", t.Track.MidiChannel == 0 ? Loc.T("midi.in.channel.all") : t.Track.MidiChannel.ToString()), FontSize = 12 };
        var items = new List<object>
        {
            MenuCheck(Loc.T("midi.in.channel.all"), () => t.Track.MidiChannel == 0, () => SetMidiInput(t, t.Track.MidiInput, 0), refresh),
        };
        for (int c = 1; c <= 16; c++)
        {
            int ch = c;
            items.Add(MenuCheck(ch.ToString(), () => t.Track.MidiChannel == ch, () => SetMidiInput(t, t.Track.MidiInput, ch), refresh));
        }
        channels.ItemsSource = items;
        list.Add(channels);
        list.Add(MenuAction(Loc.T("midi.in.settings"), () => { Settings.Show("midi"); return Task.CompletedTask; }, icon: ""));
        var menu = Popup(list, 240);
        if (point is { } at) OpenTimelineMenu(menu, at, MenuHeight(list));
        else
        {
            menu.Placement = Avalonia.Controls.PlacementMode.Bottom;
            menu.MaxHeight = Math.Min(420, Bounds.Height - 16);
            OpenAnchoredMenu(menu, anchor);
        }
    }

    void SetMidiInput(LiveTrack t, string? device, int channel)
    {
        if (store == null || store.ReadOnly || RecordingBusy || t.Track.MidiInput == device && t.Track.MidiChannel == channel) return;
        undo.Checkpoint(store.Project);
        t.Track.MidiInput = device;
        t.Track.MidiChannel = channel;
        MarkDirty();
        UpdateUndoButtons();
        ShowMidiListeners();
        Timeline.InvalidateVisual();
        if (mixerView) RefreshMixer();
        Status(Loc.T("midi.in.playsFrom", t.Track.Name, MidiInputLabel(t.Track)));
    }

    // --- recording ----------------------------------------------------------------------------------

    /// <summary>Makes an empty take for each armed MIDI track and hands them to the engine, before it starts recording.</summary>
    void BeginMidiTakes(IEnumerable<Track> targets)
    {
        midiTakes.Clear();
        foreach (var t in targets) midiTakes[t.Id] = new MidiTake();
        midiListenKey = 0;
        ShowMidiListeners();
    }

    /// <summary>
    /// The takes' notes once the recording stopped, as clips to add (one per track that got any): from the bar where
    /// the take began (<paramref name="startSec"/>) to <paramref name="endSec"/>. Empty when discarded.
    /// </summary>
    List<(string TrackId, List<(double Sec, double LengthSec, int Pitch, int Velocity)> Notes)> EndMidiTakes(double startSec, double endSec, bool discard)
    {
        var takes = midiTakes.ToList();
        midiTakes.Clear();
        midiListenKey = 0;
        ShowMidiListeners();
        if (discard || store == null) return [];
        double barSec = 4 * 60 / Math.Max(1, store.Project.Settings.Bpm);
        double barStart = Math.Floor(startSec / barSec + 1e-9) * barSec;
        const double sr = PlaybackEngine.SampleRate;
        var result = new List<(string, List<(double, double, int, int)>)>();
        foreach (var (id, take) in takes)
        {
            var notes = take.Notes((long)Math.Round(barStart * sr), (long)Math.Round(endSec * sr));
            if (notes.Count == 0) continue;
            result.Add((id, notes.Select(n => (n.Start / sr, n.Length / sr, n.Pitch, n.Velocity)).ToList()));
            if (take.Dropped > 0) SessionLog.Write($"MIDI: {take.Dropped} notas no entraron en la toma");
        }
        return result;
    }

    /// <summary>The grid a recording is quantized to: the piano roll's magnet when "Cuantizar al grabar" is on; 0 = as played.</summary>
    double RecordQuantizeBeats => MidiSetup.Settings.QuantizeOnRecord ? pianoRoll.SnapBeats : 0;

    // --- quantize -----------------------------------------------------------------------------------

    /// <summary>
    /// Quantize (Q, or a MIDI clip's menu): in the piano roll with the focus, its selected notes (all of them with none
    /// selected); in the timeline, the selected MIDI clips. To the piano roll's magnet; in Libre it says why it can't.
    /// </summary>
    async Task Quantize(IReadOnlyList<string>? clipIds = null)
    {
        if (store == null || store.ReadOnly || restoring || RecordingBusy) return;
        double grid = pianoRoll.SnapBeats;
        if (grid <= 0)
        {
            Toast(Loc.T("midi.quantize.free"));
            return;
        }
        if (clipIds == null && PianoShown && pianoRoll.IsKeyboardFocusWithin)
        {
            int n = pianoRoll.Quantize();
            if (n >= 0) Status(Loc.T("midi.quantize.done", n, PianoSnapLabel()));
            return;
        }
        var p = store.Project;
        IEnumerable<string> picked = clipIds ?? [];
        if (clipIds == null && Timeline.BoxedClipIds.Count > 0) picked = Timeline.BoxedClipIds;
        else if (clipIds == null && Timeline.SelectedMidiId is { } one) picked = [one];
        var ids = picked.Where(id => MidiEditing.TryFind(p, id) != null).Distinct().ToList();
        if (ids.Count == 0)
        {
            Status(Loc.T("midi.quantize.none"));
            return;
        }
        int moved = 0;
        string trackId = MidiEditing.Find(p, ids[0]).Track.Id;
        await EditTimeline(q =>
        {
            foreach (var id in ids) moved += MidiEditing.Quantize(q, id, grid);
            return trackId;
        }, "midi-quantized");
        Status(Loc.T("midi.quantize.done", moved, PianoSnapLabel()));
    }

    /// <summary>The magnet's step as its button says it ("1/16").</summary>
    string PianoSnapLabel() => PianoSnaps.FirstOrDefault(s => Math.Abs(s.Beats - pianoRoll.SnapBeats) < 1e-9).Id ?? "";
}
