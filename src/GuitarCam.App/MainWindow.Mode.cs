using Avalonia.Controls;
using GuitarCam.App.Video;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The project's mode (native only, asked by the user): "Video" or "Solo audio", switched from the title bar at
/// any time and saved with the project as one undo step. Solo audio puts the timeline (or the mixer) in the stage's
/// place, lets go of the camera, hides what only makes sense with a picture (thumbnails, Color, Encuadre, camera
/// latency, video fades, "En el video") and turns "Exportar MP4" into "Exportar audio". Nothing is deleted: back in
/// video, everything is where it was.
/// </summary>
public partial class MainWindow
{
    bool audioMode;
    /// <summary>The timeline and the mixer live in <see cref="AudioArea"/>, one over the other (<see cref="MoveBottomView"/>).</summary>
    bool stackedViews;
    /// <summary>Inspector controls that only mean something with a picture; hidden in solo audio.</summary>
    readonly List<Control> videoOnly = [];

    bool AudioOnly => store?.Project.Settings.AudioOnly == true;

    void InitMode()
    {
        ModeVideoButton.Click += (_, _) => ChangeMode("video");
        ModeAudioButton.Click += (_, _) => ChangeMode("audio");
        RefreshModeButtons();
    }

    void ChangeMode(string mode)
    {
        if (store != null && !store.ReadOnly && !restoring && !Projects.IsVisible && !RecordingBusy && store.Project.Settings.Mode != mode)
        {
            undo.Checkpoint(store.Project);
            store.Project.Settings.SetMode(mode);
            MarkDirty();
            UpdateUndoButtons();
            ApplyMode();
            Status(mode == "audio"
                ? Loc.T("mode.status.audio")
                : Loc.T("mode.status.video"));
        }
        // a click on the checked option would uncheck it: always show the project's mode again
        RefreshModeButtons();
    }

    void RefreshModeButtons()
    {
        ModeVideoButton.IsChecked = store != null && !AudioOnly;
        ModeAudioButton.IsChecked = AudioOnly;
        ModeVideoButton.IsEnabled = ModeAudioButton.IsEnabled = store?.ReadOnly == false && !RecordingBusy;
    }

    /// <summary>Puts the window in the project's mode: on open, after undo / redo and from the switch.</summary>
    void ApplyMode()
    {
        RefreshModeButtons();
        bool audio = AudioOnly;
        ExportButtonText.Text = audio ? Loc.T("export.audio") : Loc.T("export.mp4");
        ToolTip.SetTip(ExportButton, audio ? Loc.T("export.audio.tip") : Loc.T("export.mp4"));
        Timeline.ShowVideo = !audio;
        foreach (var c in videoOnly) c.IsVisible = !audio;
        SetColorTabVisible(!audio);
        RefreshInspectorText();
        RefreshHistory();
        _ = camera.SetPaused(audio);
        ApplyLayout();
        if (audio == audioMode) return;
        audioMode = audio;
        if (audio)
        {
            // the decoders are FFmpeg processes: none runs while nothing is shown. Detached here, on the UI thread,
            // because the tick keeps drawing these clips; only the slow closing goes to the background
            var decoders = tracks.SelectMany(t => t.Clips).Select(c => c.DetachVideo()).OfType<VideoDecoder>().ToList();
            _ = Task.Run(() => { foreach (var d in decoders) d.Dispose(); });
        }
        else if (store != null) _ = OpenMissingVideos(store);
    }

    /// <summary>
    /// Stacked (solo audio, or the video in its own column in a Vertical layout): the timeline moves up beside the
    /// inspector, the mixer opens under it and the bottom row closes; otherwise both go back to the bottom with the
    /// height they had (the remembered heights stay untouched meanwhile).
    /// </summary>
    void MoveBottomView(bool stacked)
    {
        if (stacked == stackedViews) return;
        stackedViews = stacked;
        var row = Root.RowDefinitions[3];
        if (stacked)
        {
            if (row.ActualHeight > 0) { if (mixerView) { if (!masteringView) mixerH = row.ActualHeight; } else timelineH = row.ActualHeight; }
            Root.Children.Remove(MixerHost);
            Root.Children.Remove(TimelineHost);
            // the timeline always stays on screen; the mixer opens under it (so no mini timeline there)
            Grid.SetRow(TimelineHost, 0);
            Grid.SetRow(MixerHost, 2);
            AudioArea.Children.Add(TimelineHost);
            AudioArea.Children.Add(MixerHost);
            row.MinHeight = 0;
            row.Height = new GridLength(0);
        }
        else
        {
            AudioArea.Children.Remove(MixerHost);
            AudioArea.Children.Remove(TimelineHost);
            Grid.SetRow(TimelineHost, 3);
            Grid.SetRow(MixerHost, 3);
            // under the dialogs and toasts: added at the end they'd be drawn over them and keep taking the clicks
            int at = Root.Children.IndexOf(Projects);
            Root.Children.Insert(at, TimelineHost);
            Root.Children.Insert(at, MixerHost);
            row.MinHeight = mixerView ? MixerMinH : TimelineMinH;
            row.Height = new GridLength(mixerView ? mixerH : timelineH);
        }
        Root.RowDefinitions[2].Height = new GridLength(stacked ? 0 : 4);
        AudioArea.IsVisible = stacked;
        BottomSplitter.IsVisible = !stacked;
        TimelineHost.IsVisible = ToolBox.IsEnabled = !mixerView || stacked;
        miniRow.IsVisible = !stacked;
        ApplyAudioMixerRows();
        if (mixerView && masteringView) FitMasteringHeight();
        RefreshMixer();
    }

    /// <summary>Stacked: the mixer's row under the timeline (resizable with its splitter) is open only while the mixer is.</summary>
    void ApplyAudioMixerRows()
    {
        bool open = stackedViews && mixerView;
        AudioSplitter.IsVisible = open;
        AudioArea.RowDefinitions[0].MinHeight = open ? TimelineMinH : 0;
        AudioArea.RowDefinitions[1].Height = new GridLength(open ? 4 : 0);
        AudioArea.RowDefinitions[2].MinHeight = open ? MixerMinH : 0;
        AudioArea.RowDefinitions[2].Height = new GridLength(open ? Math.Max(MixerMinH, mixerH) : 0);
    }

    /// <summary>Opens the decoders of the clips shown in the video that don't have one yet (after an edit, or back from solo audio).</summary>
    async Task OpenMissingVideos(ProjectStore st)
    {
        if (st.Project.Settings.AudioOnly) return;
        var open = tracks.Where(t => t.Track.Video).SelectMany(t => t.Clips).Where(c => !c.VideoTried && c.Media?.Video != null).ToList();
        if (open.Count > 0) await Task.WhenAll(open.Select(c => Task.Run(() => c.OpenVideo(st))));
        Preview.SetProject(tracks, st.Project.Settings);
        Preview.InvalidateVisual();
    }

    /// <summary>"En el video" of the inspector and the timeline menu: whether the track gets a panel (native only).</summary>
    async Task SetInVideo(Model.LiveTrack t, bool on)
    {
        if (store == null || store.ReadOnly || restoring || t.Track.Video == on) return;
        undo.Checkpoint(store.Project);
        t.Track.Video = on;
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        Timeline.InvalidateVisual();
        await OpenMissingVideos(store);
        Status(on ? $"\"{t.Track.Name}\" vuelve a tener su panel en el video." : $"\"{t.Track.Name}\" suena pero no sale en el video.");
    }
}
