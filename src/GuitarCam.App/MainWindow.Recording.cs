using Avalonia.Input;
using GuitarCam.App.Controls;
using GuitarCam.Audio;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// Recording a take from the window: port of handleStartRecordingSequence, handleStopRecording,
/// discardRecording and cancelCountIn (GuitarCam/src/App.tsx) with the record buttons of TransportBar.tsx.
/// Same as the web except: there is no camera yet, so the take is audio only (the web refuses to record
/// without one), and the count-in, click and capture run on the ASIO clock inside <see cref="Audio.PlaybackEngine"/>.
/// A journal is written before the first sample and removed only after the project with the take is
/// saved, so if the program dies mid-take the next open recovers it (<see cref="ProjectStore.BeginRecording"/>).
/// </summary>
public partial class MainWindow
{
    ProjectStore.RecordingJournal? recJournal;
    int recTakeNumber;
    bool recFirstTake;
    bool recStopping;
    bool? recShown;

    /// <summary>Default overdub fine adjustment of the web's Configuración; 0 until that screen is ported.</summary>
    const double OverdubOffsetMs = 0;

    bool RecordingBusy => recJournal != null || recStopping;

    void InitRecording()
    {
        RecButton.Click += (_, _) => StartRecording();
        RecStopButton.Click += async (_, _) => await StopRecording(false);
        RecDiscardButton.Click += async (_, _) => await StopRecording(true);
        CountCancelButton.Click += async (_, _) => await StopRecording(true);
    }

    void StartRecording()
    {
        if (store == null || restoring || Projects.IsVisible || Export.IsVisible || RecordingBusy || engine.Calibrating) return;
        if (store.ReadOnly)
        {
            Status(store.ReadOnlyReason ?? "El proyecto está abierto en solo lectura: no puedo grabar acá.");
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
        var target = TakeRecording.PickTarget(p, sel?.Track.Id);
        bool countIn = CountInToggle.IsChecked == true;
        recFirstTake = TakeRecording.IsFirstTake(p);
        double syncMs = TakeRecorder.ComputeSyncMs(recFirstTake, countIn, engine.ClickOn, OverdubOffsetMs, engine.GetLatencyInfo().AppliedMs);
        var id = Ids.New("med");
        var j = new ProjectStore.RecordingJournal
        {
            MediaId = id,
            Name = TakeRecording.TakeName(p, target),
            Audio = $"{ProjectStore.MediaDir}/{id}.wav",
            TrackId = target?.Id,
            AtSec = syncMs / 1000,
            SyncMs = syncMs,
        };
        try
        {
            store.BeginRecording(j);
            engine.StartRecording(store.Resolve(j.Audio)!, countIn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try { store.EndRecording(id); } catch (IOException) { }
            Status($"No pude empezar a grabar: {e.Message}");
            return;
        }
        recJournal = j;
        recTakeNumber = TakeRecording.NextTakeNumber(p);
        Timeline.RecordingLane = target == null ? tracks.Count : p.Tracks.IndexOf(target);
        Status("");
        RefreshRecording();
    }

    /// <summary>
    /// Stop and keep the take, or discard it (also cancels the count-in). The engine closes the WAV off the
    /// UI thread; the take lands in the project as one undo step and is saved before the journal goes.
    /// </summary>
    async Task StopRecording(bool discard)
    {
        if (recJournal is not { } j || recStopping || store == null) return;
        recStopping = true;
        var st = store;
        bool first = recFirstTake;
        RecordedTakeResult? res = null;
        try
        {
            res = await Task.Run(() => engine.StopRecording(discard, OverdubOffsetMs, first));
        }
        catch (Exception e)
        {
            // the journal stays: whatever reached the WAV comes back the next time the project opens
            recStopping = false;
            Status($"No pude cerrar la toma: {e.Message}. Lo grabado vuelve al abrir el proyecto.");
            return;
        }
        finally
        {
            recJournal = null;
            Timeline.RecordingLane = null;
            Timeline.SetPlayhead(0, true); // back to the start with the playhead, like the web
            RefreshRecording();
        }
        try
        {
            string? problem = engine.RecordingError is { } err ? $"Hubo un problema escribiendo la toma en disco: {err}"
                : engine.RecordingDroppedFrames > 0 ? $"Se perdieron {engine.RecordingDroppedFrames * 1000.0 / Audio.PlaybackEngine.SampleRate:0} ms de audio en la toma (el disco no dio abasto)"
                : null;
            if (res == null)
            {
                st.EndRecording(j.MediaId);
                if (problem != null) Status(problem);
                return;
            }
            j.SyncMs = res.SyncMs;
            j.AtSec = res.SyncMs / 1000;
            string? trackId = null;
            await EditTimeline(p => trackId = TakeRecording.AddTake(p, j, res.DurationSec, res.SampleRate, 2, recTakeNumber).Id, null);
            if (st != store || st.Project.FindMedia(j.MediaId) == null)
            {
                // the journal stays, so the take comes back the next time the project opens
                Status($"No pude poner \"{j.Name}\" en el timeline; la grabación quedó a salvo y vuelve al abrir el proyecto.");
                return;
            }
            // a preset compressor sets its make-up and the rest from the new take's level (the web's updateComp)
            if (tracks.Find(t => t.Track.Id == trackId) is { } live && live.Channel.Comp.Preset != "custom")
            {
                RecalculateInspectorComp(live);
                ShowTrack(live);
            }
            await FlushProjectOperation();
            st.EndRecording(j.MediaId);
            Status(problem ?? $"Guardé \"{j.Name}\" ({res.DurationSec:0.0} s)");
        }
        catch (IOException e)
        {
            // the journal stays: the take is in media/ and the next open puts it back if this save never lands
            Status($"No pude guardar \"{j.Name}\": {e.Message}. Lo vuelvo a intentar…");
        }
        finally
        {
            recStopping = false;
        }
    }

    /// <summary>
    /// Keys while recording or counting in (App.tsx onKey): R and Space stop and keep the take, or cancel
    /// the count-in; Esc cancels the count-in; delete, undo/redo, Home and TAP stay off. True when handled.
    /// </summary>
    async Task<bool> RecordingKey(KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (e.Key is Key.Z or Key.Y) e.Handled = true;
            return e.Handled;
        }
        switch (e.Key)
        {
            case Key.R or Key.Space:
                e.Handled = true;
                if (transportKeys.Add(e.Key)) await StopRecording(engine.IsCountingIn);
                return true;
            case Key.Escape when engine.IsCountingIn:
                e.Handled = true;
                await StopRecording(true);
                return true;
            case Key.Delete or Key.Home or Key.T:
                e.Handled = true;
                return true;
        }
        return false;
    }

    /// <summary>Record button states of TransportBar.tsx, refreshed from the tick (the count-in runs on the audio clock).</summary>
    void RefreshRecording()
    {
        bool recording = recJournal != null;
        int? beat = recording ? engine.CountdownBeat : null;
        RecButton.IsVisible = !recording;
        CountCancelButton.IsVisible = beat != null;
        RecStopButton.IsVisible = RecDiscardButton.IsVisible = recording && beat == null;
        if (beat != null) CountBeatText.Text = $"{beat}/4";
        if (recShown == recording) return;
        recShown = recording;
        TimeText.Foreground = recording ? Palette.Rec : Palette.Fg;
        UpdateUndoButtons();
        SessionChip.IsEnabled = ExportButton.IsEnabled = !recording;
    }
}
