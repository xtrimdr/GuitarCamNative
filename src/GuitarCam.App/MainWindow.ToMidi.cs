using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GuitarCam.App.Model;
using GuitarCam.Audio;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// "Extraer MIDI" (asked by the user, like Studio Pro): the notes of what a take's clip plays become a MIDI clip over
/// the same stretch, on a new MIDI track right under it (clip menu → Extraer MIDI → Acordes / Melodía) or on the MIDI
/// track the clip is dropped on (the clip stays where it was; the drop uses the mode chosen last). <see cref="AudioToMidi"/>
/// listens on a worker thread to what the clip sounds like (transposed, when its track is), one clip at a time, and
/// the clip shows how far it is, like "Detectar tempo". The new clip is one undo step and the toast opens it in the piano roll.
/// </summary>
public partial class MainWindow
{
    /// <summary>The clip being listened to and how far it is (0 to 1, written by the worker); null when idle.</summary>
    sealed class ToMidiJob(LiveClip clip)
    {
        public readonly LiveClip Clip = clip;
        public volatile float Progress;
    }
    ToMidiJob? toMidiJob;
    /// <summary>Redraws the clip's bar while it listens.</summary>
    DispatcherTimer? toMidiClock;
    const string ToMidiModeKey = "toMidiMode";

    /// <summary>The mode chosen last (the drop on a MIDI track uses it): chords unless melody was picked.</summary>
    static AudioToMidi.Mode LastToMidiMode
    {
        get
        {
            try { return AppConfig.Get(ToMidiModeKey) == "melody" ? AudioToMidi.Mode.Melody : AudioToMidi.Mode.Chords; }
            catch { return AudioToMidi.Mode.Chords; }
        }
        set
        {
            try { AppConfig.Set(ToMidiModeKey, value == AudioToMidi.Mode.Melody ? "melody" : "chords"); }
            catch { }
        }
    }

    MenuItem ExtractMidiItem(LiveTrack track, LiveClip clip, bool enabled)
    {
        bool can = enabled && clip.Audio != null && !RecordingBusy && toMidiJob == null && AudioToMidi.Available;
        string tip = Loc.T(!AudioToMidi.Available ? "toMidi.noModel" : clip.Audio == null ? "toMidi.offline" : toMidiJob != null ? "toMidi.busy" : "toMidi.tip");
        var item = new MenuItem
        {
            Header = MenuHeader(Loc.T("toMidi.menu"), new TextBlock { Classes = { "icon" }, Text = Controls.TimelineView.ToMidiGlyph, FontSize = 12 }),
            IsEnabled = can,
            ItemsSource = new object[]
            {
                MenuAction(Loc.T("toMidi.chords"), () => ExtractMidi(track, clip, null, AudioToMidi.Mode.Chords), tip: Loc.T("toMidi.chords.tip")),
                MenuAction(Loc.T("toMidi.melody"), () => ExtractMidi(track, clip, null, AudioToMidi.Mode.Melody), tip: Loc.T("toMidi.melody.tip")),
            },
        };
        ToolTip.SetTip(item, tip);
        return item;
    }

    /// <summary>A take's clip dropped on a MIDI track (<see cref="Controls.TimelineView.ExtractMidiRequested"/>).</summary>
    void ExtractMidiDropped(LiveClip clip, LiveTrack into)
    {
        if (store == null || store.ReadOnly || restoring) return;
        var from = tracks.FirstOrDefault(t => t.Clips.Contains(clip));
        if (from == null) return;
        if (!AudioToMidi.Available) { Toast(Loc.T("toMidi.noModel")); return; }
        if (clip.Audio == null) { Toast(Loc.T("toMidi.offline")); return; }
        if (toMidiJob != null || RecordingBusy) { Toast(Loc.T("toMidi.busy")); return; }
        _ = ExtractMidi(from, clip, into, LastToMidiMode);
    }

    async Task ExtractMidi(LiveTrack from, LiveClip clip, LiveTrack? into, AudioToMidi.Mode mode)
    {
        if (clip.Audio is not { } audio || store == null || toMidiJob != null) return;
        LastToMidiMode = mode;
        // what it sounds like: the take, or its transposed sound
        var sound = clip.Sound ?? audio.Sound;
        var w = clip.Window;
        int sr = audio.SampleRate;
        int start = (int)Math.Max(0, (w.Start - w.Origin) * sr);
        int count = (int)Math.Max(0, (w.End - w.Start) * sr);
        string name = clip.Name, fromId = from.Track.Id, fromName = from.Track.Name;
        string? intoId = into?.Track.Id;
        var st = store;
        var job = toMidiJob = new ToMidiJob(clip);
        Timeline.ToMidiProgress ??= c => toMidiJob is { } j && j.Clip == c ? j.Progress : null;
        if (toMidiClock == null)
        {
            toMidiClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            toMidiClock.Tick += (_, _) => Timeline.InvalidateVisual();
        }
        toMidiClock.Start();
        Timeline.InvalidateVisual();
        List<AudioToMidi.Note> notes;
        try
        {
            notes = await Task.Run(() => AudioToMidi.Extract(sound.L, sound.R, sr, start, count, new(mode), p => job.Progress = (float)p));
        }
        catch (Exception e)
        {
            Toast(Loc.T("toMidi.failed", name, e.Message));
            return;
        }
        finally
        {
            toMidiJob = null;
            toMidiClock.Stop();
            Timeline.InvalidateVisual();
        }
        // another project opened meanwhile: these notes aren't about this one
        if (store == null || store != st || store.ReadOnly || restoring) return;
        if (notes.Count == 0)
        {
            Toast(Loc.T("toMidi.none", name));
            return;
        }
        string? clipId = null, trackId = null;
        await EditTimeline(p =>
        {
            // the MIDI track it was dropped on may be gone by now: then it gets a new one, like from the menu
            if (intoId != null && p.Tracks.All(t => t.Id != intoId)) intoId = null;
            var (tid, c) = MidiEditing.AddExtracted(p, intoId, fromId, w.Start, w.End, notes, intoId == null ? $"{fromName} (MIDI)" : null);
            clipId = c.Id;
            trackId = tid;
            return tid;
        }, "midi-extracted");
        if (clipId == null || trackId == null) return;
        Timeline.SelectMidiClip(TrackIndex(trackId), clipId);
        string id = clipId;
        Toast(Loc.T(mode == AudioToMidi.Mode.Melody ? "toMidi.done.melody" : "toMidi.done", notes.Count, name),
            action: (Loc.T("toMidi.open"), () => { if (store == st) OpenInPiano(id); }));
    }
}
