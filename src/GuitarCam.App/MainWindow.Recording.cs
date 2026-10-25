using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Audio;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// Recording a take from the window: port of handleStartRecordingSequence, handleStopRecording,
/// discardRecording and cancelCountIn (GuitarCam/src/App.tsx) with the record buttons of TransportBar.tsx.
/// The camera's video records next to the WAV (<see cref="VideoTakeWriter"/>, its frames counted from the
/// audio's first sample) with the camera latency and the mirror of the moment. Same as the web except: without
/// a camera (or with one that sends nothing) the take is audio only (the web refuses to record without one),
/// and the count-in, click and capture run on the ASIO clock inside <see cref="Audio.PlaybackEngine"/>.
/// A journal is written before the first sample and removed only after the project with the take is
/// saved, so if the program dies mid-take the next open recovers it (<see cref="ProjectStore.BeginRecording"/>);
/// the video survives too, because FFmpeg closes its file when the pipe ends.
/// Native only: tracks are armed by hand (● in their header or mixer strip), several at once, each recording
/// from its own input (<see cref="Track.Input"/>), one take per track on the same sample; a track with clips
/// loses them to its take and stays silent meanwhile. Without one armed the target is the web's pick. The
/// camera's video goes to the first armed track that is in the video. Targets are always tinted red. Recording
/// starts at the playhead (the web always starts at 0): the count-in leads into it and the take lands there,
/// replacing only what it covers on a track with clips (<see cref="TakeRecording.AddTake"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>One track's take while recording: its journal and its number.</summary>
    sealed record RecTake(ProjectStore.RecordingJournal Journal, int Number);

    List<RecTake> recTakes = [];
    VideoTakeWriter? recVideo;
    CameraCapture? recCamera;
    bool recFirstTake;
    bool recStopping;
    bool? recShown;
    readonly List<LiveTrack> recSilenced = [];
    /// <summary>
    /// The tracks armed by hand: null = the web's pick (<see cref="TakeRecording.PickTarget"/>), empty = none
    /// (recording is off until one is armed). Not saved, like the selection.
    /// </summary>
    List<string>? armedIds;
    /// <summary>The live tracks the engine's input taps belong to, in the same order (their meters show the input).</summary>
    LiveTrack?[] armedLive = [];
    string armedKey = "";

    /// <summary>"Ajuste fino de audio (canales nuevos)" of Configuración → Sincronización.</summary>
    double OverdubOffsetMs => Settings.OverdubOffsetMs;

    bool RecordingTake => recTakes.Count > 0;
    bool RecordingBusy => RecordingTake || recStopping;

    void InitRecording()
    {
        RecButton.Click += (_, _) => StartRecording();
        RecStopButton.Click += async (_, _) => await StopRecording(false);
        RecDiscardButton.Click += async (_, _) => await StopRecording(true);
        CountCancelButton.Click += async (_, _) => await StopRecording(true);
        Timeline.ArmRequested += ToggleArm;
        Timeline.InputMenuRequested += (t, at) => ShowInputMenu(t, Timeline, at);
        Timeline.InputLabel = t => InputShort(t.Track);
    }

    /// <summary>Where R records now, in timeline order: the armed tracks, else the web's pick; empty = nothing to record into (Grabar is off).</summary>
    List<Track> RecordTargets()
    {
        if (store == null) return [];
        var p = store.Project;
        if (armedIds == null) return TakeRecording.PickTarget(p, sel?.Track.Id) is { } pick ? [pick] : [];
        return p.Tracks.Where(t => armedIds.Contains(t.Id)).ToList();
    }

    /// <summary>Arms a track, or disarms it when it is lit (armed by hand or picked by the app); the others stay as they are.</summary>
    void ToggleArm(LiveTrack t)
    {
        if (RecordingBusy || store == null || store.ReadOnly) return;
        var lit = RecordTargets().Select(x => x.Id).ToList();
        if (!lit.Remove(t.Track.Id)) lit.Add(t.Track.Id);
        armedIds = lit;
        ShowArm();
    }

    /// <summary>
    /// Tints the targets in the timeline and the mixer and hands their inputs to the engine (meters, software
    /// monitoring); cheap enough for every tick, it changes things only when the targets or their inputs did.
    /// </summary>
    void ShowArm()
    {
        if (RecordingTake) return;
        var targets = RecordTargets();
        string key = string.Join("|", targets.Select(t => $"{t.Id}:{t.Input}:{tracks.FindIndex(l => l.Track == t)}"));
        if (key == armedKey) return;
        armedKey = key;
        armedLive = targets.Select(t => tracks.Find(l => l.Track == t)).ToArray();
        engine.SetInputs(targets.Select(t => InputRoute.Parse(t.Input)).ToList());
        Timeline.ArmedIndices = targets.Select(t => tracks.FindIndex(l => l.Track == t)).Where(i => i >= 0).ToHashSet();
        if (mixerView) RefreshMixer();
    }

    /// <summary>The armed tracks' input peaks go into their meters (UI side, read by the tick).</summary>
    void TakeArmedPeaks()
    {
        var taps = engine.Taps;
        for (int i = 0; i < taps.Count && i < armedLive.Length; i++)
        {
            var (l, r) = taps[i].TakePeaks();
            if (armedLive[i] is not { } t) continue;
            t.InputL = Math.Max(t.InputL, l);
            t.InputR = Math.Max(t.InputR, r);
        }
    }

    // --- input selector ---------------------------------------------------------------------------

    /// <summary>The input as the header chip shows it: "1-2", "3".</summary>
    static string InputShort(Track t) => InputRoute.Parse(t.Input)?.Key ?? "1-2";

    static string InputLabel(Track t) => InputRoute.Parse(t.Input) is { } r ? r.Label : Loc.T("rec.inputDefault");

    /// <summary>
    /// The armed track's input menu: Configuración's input, then each driver input and each pair. From the
    /// timeline it opens at <paramref name="point"/> like its other menus; from the mixer, under the chip.
    /// </summary>
    void ShowInputMenu(LiveTrack t, Control anchor, Point? point = null)
    {
        if (point == null && PressClosedMenuOf(anchor)) return;
        if (store == null || store.ReadOnly || RecordingBusy) return;
        string? check(string? value) => t.Track.Input == value ? "" : null;
        MenuItem Pick(string label, string? value, string? hint = null) =>
            MenuAction(label, () => { SetTrackInput(t, value); return Task.CompletedTask; }, hint, icon: check(value) ?? " ");
        var list = new List<object>
        {
            new MenuItem { Header = Loc.T("rec.from"), IsEnabled = false, FontSize = 10.5 },
            Pick(Loc.T("rec.inputs12"), null, Loc.T("rec.inputs12.hint")),
        };
        int inputs = engine.InputChannels;
        if (inputs == 0) list.Add(new MenuItem { Header = Loc.T("rec.openAudio"), IsEnabled = false, FontSize = 12 });
        else
        {
            list.Add(new Separator());
            foreach (var r in InputRoute.All(inputs).Where(r => !r.Stereo)) list.Add(Pick(r.Label, r.Key, Loc.T("rec.mono")));
            if (inputs > 1) list.Add(new Separator());
            foreach (var r in InputRoute.All(inputs).Where(r => r.Stereo)) list.Add(Pick(r.Label, r.Key, Loc.T("rec.stereo")));
        }
        var menu = Popup(list, 220);
        if (point is { } at) OpenTimelineMenu(menu, at, 60 + list.Count * 32);
        else
        {
            menu.Placement = PlacementMode.Bottom;
            menu.MaxHeight = Math.Min(320, Bounds.Height - 16);
            OpenAnchoredMenu(menu, anchor);
        }
    }

    void SetTrackInput(LiveTrack t, string? input)
    {
        if (store == null || store.ReadOnly || RecordingBusy || t.Track.Input == input) return;
        undo.Checkpoint(store.Project);
        t.Track.Input = input;
        MarkDirty();
        UpdateUndoButtons();
        ShowArm();
        Timeline.InvalidateVisual();
        if (mixerView) RefreshMixer();
        Status(Loc.T("rec.recordsFrom", t.Track.Name, InputLabel(t.Track)));
    }

    // --- recording --------------------------------------------------------------------------------

    void StartRecording()
    {
        if (store == null || restoring || Projects.IsVisible || Export.IsVisible || RecordingBusy || engine.Calibrating) return;
        if (store.ReadOnly)
        {
            Status(store.ReadOnlyReason ?? Loc.T("rec.readOnly"));
            return;
        }
        if (!engine.IsOpen)
        {
            Status(engine.Status);
            return;
        }
        Timeline.CancelGesture();
        Preview.CancelGesture();
        var p = store.Project;
        List<Track?> targets = [.. RecordTargets()];
        if (targets.Count == 0)
        {
            Status(Loc.T("rec.noneArmed"));
            return;
        }
        bool countIn = CountInToggle.IsChecked == true;
        double startSec = engine.PositionSec;
        recFirstTake = TakeRecording.IsFirstTake(p);
        double syncMs = TakeRecorder.ComputeSyncMs(recFirstTake, countIn, engine.ClickOn, OverdubOffsetMs, engine.GetLatencyInfo().AppliedMs);
        var cam = !AudioOnly && camera.Capture is { HasSignal: true } c ? c : null;
        int videoAt = cam == null ? -1 : targets.FindIndex(t => t == null || t.Video);
        int number = TakeRecording.NextTakeNumber(p);
        var takes = new List<RecTake>();
        for (int i = 0; i < targets.Count; i++)
        {
            var id = Ids.New("med");
            takes.Add(new RecTake(new ProjectStore.RecordingJournal
            {
                MediaId = id,
                Name = TakeRecording.TakeName(p, targets[i], number + i),
                Audio = $"{ProjectStore.MediaDir}/{id}.wav",
                Video = i == videoAt ? $"{ProjectStore.MediaDir}/{id}.mp4" : null,
                TrackId = targets[i]?.Id,
                AtSec = startSec + syncMs / 1000,
                StartSec = startSec,
                SyncMs = syncMs,
                VideoOffsetMs = camera.LatencyMs,
                Mirror = camera.Mirror,
            }, number + i));
        }
        string? notice = camera.Capture != null && !AudioOnly && cam == null ? Loc.T("rec.noCamera")
            : cam != null && videoAt < 0 ? Loc.T("rec.noVideoTrack")
            : null;
        try
        {
            foreach (var t in takes) store.BeginRecording(t.Journal);
            engine.StartRecording(targets.Select((t, i) => (InputRoute.Parse(t?.Input), store.Resolve(takes[i].Journal.Audio)!)).ToList(), countIn, startSec: startSec);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            foreach (var t in takes)
                try { store.EndRecording(t.Journal.MediaId); } catch (IOException) { }
            armedKey = ""; // the engine's inputs may have changed: hand them over again
            Status(Loc.T("rec.startFail", e.Message));
            return;
        }
        if (cam != null && videoAt >= 0)
        {
            var j = takes[videoAt].Journal;
            try
            {
                var w = VideoTakeWriter.Start(store.Resolve(j.Video)!, cam.Width, cam.Height, () => engine.RecordingStartTimestamp);
                cam.Attach(w);
                (recVideo, recCamera) = (w, cam);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                j.Video = null;
                try { store.BeginRecording(j); } catch (IOException) { }
                notice = Loc.T("rec.videoFail", e.Message);
            }
        }
        recTakes = takes;
        // a take that replaces the track's clips is played against the rest of the song, not against them
        foreach (var target in targets)
            if (target is { Clips.Count: > 0 } && tracks.Find(t => t.Track == target) is { } silenced)
            {
                silenced.RecordMuted = true;
                recSilenced.Add(silenced);
            }
        Timeline.RecordingStartSec = startSec;
        Timeline.RecordingLanes = targets.Select(t => t == null ? tracks.Count : p.Tracks.IndexOf(t)).ToHashSet();
        Status(notice ?? "");
        RefreshRecording();
    }

    /// <summary>
    /// Stop and keep the takes, or discard them (also cancels the count-in). The engine closes the WAVs off the
    /// UI thread; the takes land in the project as one undo step and are saved before their journals go.
    /// </summary>
    async Task StopRecording(bool discard)
    {
        if (!RecordingTake || recStopping || store == null) return;
        var takes = recTakes;
        recStopping = true;
        bool wasCountingIn = engine.IsCountingIn;
        var st = store;
        bool first = recFirstTake;
        var video = recVideo;
        int videoAt = takes.FindIndex(t => t.Journal.Video != null);
        recVideo?.StopTaking();
        recCamera?.Attach(null);
        (recVideo, recCamera) = (null, null);
        RecordedTakeResult?[] res = [];
        VideoTakeResult? vres = null;
        try
        {
            (res, vres) = await Task.Run(() =>
            {
                var r = engine.StopRecording(discard, OverdubOffsetMs, first);
                if (video == null) return (r, null);
                var withVideo = videoAt >= 0 && videoAt < r.Length ? r[videoAt] : null;
                if (withVideo == null) video.Discard(toRecycleBin: discard);
                var v = withVideo == null ? null : video.Finish(withVideo.DurationSec);
                video.Dispose();
                return (r, v);
            });
        }
        catch (Exception e)
        {
            // the journals stay: whatever reached the WAVs comes back the next time the project opens
            recStopping = false;
            Status(Loc.T("rec.closeFail", e.Message));
            return;
        }
        finally
        {
            foreach (var t in recSilenced) t.RecordMuted = false;
            recSilenced.Clear();
            recTakes = [];
            armedKey = "";
            Timeline.RecordingLanes = null;
            Timeline.SetPlayhead(takes[0].Journal.StartSec, true); // back to where the take started (the web: to 0)
            RefreshRecording();
        }
        try
        {
            bool videoKept = videoAt >= 0 && videoAt < res.Length && res[videoAt] != null;
            string? problem = engine.RecordingError is { } err ? Loc.T("rec.diskFail", err)
                : engine.RecordingDroppedFrames > 0 ? Loc.T("rec.dropped", $"{engine.RecordingDroppedFrames * 1000.0 / Audio.PlaybackEngine.SampleRate:0}")
                : vres?.Error is { } verr ? Loc.T("rec.videoProblem", verr)
                : video != null && videoKept && vres == null ? Loc.T("rec.noImage")
                : null;
            if (videoAt >= 0 && vres == null) takes[videoAt].Journal.Video = null;
            var kept = new List<(RecTake Take, RecordedTakeResult Res)>();
            for (int i = 0; i < takes.Count; i++)
            {
                if (i < res.Length && res[i] is { } r) kept.Add((takes[i], r));
                else st.EndRecording(takes[i].Journal.MediaId);
            }
            if (kept.Count == 0)
            {
                if (problem != null) Status(problem);
                else if (discard && !wasCountingIn) Status(takes.Count > 1
                    ? Loc.T("rec.discarded.many")
                    : Loc.T("rec.discarded.one"));
                return;
            }
            foreach (var (t, r) in kept)
            {
                t.Journal.SyncMs = r.SyncMs;
                t.Journal.AtSec = t.Journal.StartSec + r.SyncMs / 1000;
            }
            var trackIds = new List<string>();
            await EditTimeline(p =>
            {
                foreach (var (t, r) in kept) trackIds.Add(TakeRecording.AddTake(p, t.Journal, r.DurationSec, r.SampleRate, 2, t.Number).Id);
                return null;
            }, null);
            if (st != store || kept.Any(k => st.Project.FindMedia(k.Take.Journal.MediaId) == null))
            {
                // the journals stay, so the takes come back the next time the project opens
                Status(Loc.T("rec.timelineFail", kept[0].Take.Journal.Name));
                return;
            }
            // a preset compressor sets its make-up and the rest from the new take's level (the web's updateComp)
            foreach (var id in trackIds)
                if (tracks.Find(t => t.Track.Id == id) is { } live && live.Channel.Comp.Preset != "custom")
                {
                    RecalculateInspectorComp(live);
                    if (trackIds.Count == 1 || live == sel) ShowTrack(live);
                }
            await FlushProjectOperation();
            foreach (var (t, _) in kept) st.EndRecording(t.Journal.MediaId);
            Status(problem ?? (kept.Count == 1
                ? Loc.T("rec.saved.one", kept[0].Take.Journal.Name, $"{kept[0].Res.DurationSec:0.0}")
                : Loc.T("rec.saved.many", kept.Count, string.Join(", ", kept.Select(k => $"\"{k.Take.Journal.Name}\"")), $"{kept[0].Res.DurationSec:0.0}")));
        }
        catch (IOException e)
        {
            // the journals stay: the takes are in media/ and the next open puts them back if this save never lands
            Status(Loc.T("rec.saveFail", e.Message));
        }
        finally
        {
            recStopping = false;
        }
    }

    /// <summary>
    /// Keys while recording or counting in (App.tsx onKey): the record and play shortcuts stop and keep the take, or cancel
    /// the count-in (the play and record shortcuts); Esc cancels the count-in; delete, undo/redo, to start and TAP stay off. True when handled.
    /// </summary>
    async Task<bool> RecordingKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && engine.IsCountingIn)
        {
            e.Handled = true;
            await StopRecording(true);
            return true;
        }
        switch (Shortcuts.Find(e))
        {
            case "record" or "play":
                e.Handled = true;
                if (transportKeys.Add(e.Key)) await StopRecording(engine.IsCountingIn);
                return true;
            case "delete" or "toStart" or "tap" or "undo" or "redo":
                e.Handled = true;
                return true;
        }
        return false;
    }

    /// <summary>Record button states of TransportBar.tsx, refreshed from the tick (the count-in runs on the audio clock).</summary>
    void RefreshRecording()
    {
        bool recording = RecordingTake;
        int? beat = recording ? engine.CountdownBeat : null;
        RecButton.IsVisible = !recording;
        CountCancelButton.IsVisible = beat != null;
        RecStopButton.IsVisible = RecDiscardButton.IsVisible = recording && beat == null;
        if (beat != null) CountBeatText.Text = $"{beat}/4";
        if (Preview.CountdownBeat != beat)
        {
            Preview.CountdownBeat = beat;
            Preview.InvalidateVisual();
        }
        if (recShown == recording) return;
        recShown = recording;
        TimeText.Foreground = recording ? Palette.Rec : Palette.Fg;
        UpdateUndoButtons();
        SessionChip.IsEnabled = ExportButton.IsEnabled = !recording;
        RefreshHistory();
    }
}
