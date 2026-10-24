using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GuitarCam.App.Audio;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.App.Video;
using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>Code-behind studio window: owns editable project data on the UI thread and publishes playback state.</summary>
public partial class MainWindow : Window
{
    /// <summary>Edits are saved this long after the last one.</summary>
    static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1.5);

    readonly PlaybackEngine engine = new();
    readonly LiveCamera camera = new();
    readonly UndoManager undo = new();
    readonly DispatcherTimer timer;
    readonly DispatcherTimer saveTimer;
    ProjectStore? store;
    Task saving = Task.CompletedTask; // writes run in order, one after the other
    Exception? saveFailure;
    bool dirty;
    List<LiveTrack> tracks = [];
    LiveTrack? sel;
    bool syncing;
    bool restoring;
    double grShown;

    public MainWindow() : this(null) { }

    public MainWindow(string? dir)
    {
        InitializeComponent();

        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.Source is Control c && c.FindAncestorOfType<Button>(true) != null) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };

        SessionChip.Click += async (_, _) => await ShowProjects();
        ExportButton.Click += (_, _) =>
        {
            if (store == null || restoring || Projects.IsVisible || RecordingBusy) return;
            if (Export.TryOpen(store, tracks, out var error)) { engine.Pause(); engine.Seek(0); }
            else if (error != null) Status(error);
        };
        Export.ExportCompleted += _ => { MarkDirty(); SaveSoon(); RefreshHistory(); };
        Export.Dismissed += () => Timeline.Focus();
        Settings.Attach(engine, camera, () => RecordingBusy);
        Settings.AudioChanged += ShowAudioStatus;
        Settings.Dismissed += () => Timeline.Focus();
        SettingsButton.Click += (_, _) =>
        {
            if (Projects.IsVisible || Export.IsVisible || RecordingBusy) return;
            Settings.Show();
        };
        About.Dismissed += () => Timeline.Focus();
        LogoButton.Click += (_, _) =>
        {
            if (Projects.IsVisible || Export.IsVisible || Settings.IsVisible || RecordingBusy) return;
            About.Show();
        };
        Live.OpenInputs += () =>
        {
            if (Projects.IsVisible || Export.IsVisible || RecordingBusy) return;
            Settings.Show("audio");
        };
        // the web's md / lg / xl widths of the monitor
        SizeChanged += (_, e) => Live.Width = e.NewSize.Width >= 1280 ? 256 : 224;
        InitRecording();
        PlayButton.Click += (_, _) => TogglePlay();
        ToStartButton.Click += (_, _) => engine.Seek(0);
        FitButton.Click += (_, _) => Timeline.FitAll();
        UndoButton.Click += async (_, _) => await RestoreEdit(false);
        RedoButton.Click += async (_, _) => await RestoreEdit(true);
        ClickToggle.IsCheckedChanged += (_, _) => engine.ClickOn = ClickToggle.IsChecked == true;

        Timeline.SeekRequested += engine.Seek;
        Timeline.SelectionChanged += i => ShowTrack(i >= 0 && i < tracks.Count ? tracks[i] : null);
        Preview.ClipSelected += (track, clip) => Timeline.SelectClip(tracks.IndexOf(track), clip.Clip.Id);
        Preview.FramingRequested += (clip, zoom, x, y) =>
        {
            if (store == null || store.ReadOnly || restoring || Projects.IsVisible) return;
            var f = clip.Clip.Framing;
            if ((zoom == null || zoom == f.Zoom) && (x == null || x == f.PanX) && (y == null || y == f.PanY)) return;
            undo.Checkpoint(store.Project, $"clip:{clip.Clip.Id}:framing");
            InspectorEditing.Frame(clip.Clip, zoom, x, y);
            MarkDirty();
            UpdateUndoButtons();
            Preview.InvalidateVisual();
            RefreshInspectorText();
        };
        Timeline.TrackToggleRequested += (t, solo) => EditTrack(t, x =>
        {
            if (solo) x.Channel.Solo = !x.Channel.Solo;
            else x.Channel.Muted = !x.Channel.Muted;
        }, solo ? "solo" : "mute");
        ClearMutesButton.Click += (_, _) => ClearAllToggles(false);
        ClearSolosButton.Click += (_, _) => ClearAllToggles(true);
        InitTimelineEditing();
        InitTransport();
        InitPreview();
        InitMode();
        InitImport();

        InitInspector();
        InitProjectBar();
        InitMixer();
        InitTopBar();
        InitMeters();
        InitShortcutTips();

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        Projects.OpenProject = async d =>
        {
            await OpenProject(d);
            Projects.Hide();
        };
        Projects.FlushCurrent = FlushProjectOperation;
        Projects.CloseCurrent = async () =>
        {
            await FlushProjectOperation();
            CloseProject(clearUndo: false);
        };
        Projects.ReopenCurrent = d => OpenProject(d, preserveUndo: true);
        Projects.CurrentTemplate = name => ProjectLibrary.TemplateOf(store!.Project, name);
        // the manager covers the title bar: its top strip still drags the window
        Projects.PointerPressed += (_, e) =>
        {
            if (e.Source == Projects && e.GetPosition(this).Y < 44 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        saveTimer = new DispatcherTimer { Interval = SaveDelay };
        saveTimer.Tick += (_, _) => SaveSoon();
        ShowTrack(null);

        Opened += async (_, _) =>
        {
            Status(Loc.T("main.audioOpening"));
            await Task.Delay(50);
            string? driver = AppConfig.Get("audio.driver");
            int.TryParse(AppConfig.Get("audio.outputOffset"), out int outputOffset);
            if (double.TryParse(AppConfig.Get("audio.inputGainDb"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gainDb))
                engine.InputGainDb = Math.Clamp(gainDb, -24, 24);
            engine.Open(driver, 0, Math.Max(0, outputOffset));
            ShowAudioStatus();
            _ = camera.Refresh();
            timer.Start();
            // like the web app, it starts on the project manager (a folder on the command line opens directly)
            if (dir != null && ProjectStore.IsProject(dir)) await LoadProject(dir);
            else await ShowProjects();
        };
        Closing += async (_, e) =>
        {
            if (RecordingBusy)
            {
                // keep the take: stop, put it in the project and save, then close
                e.Cancel = true;
                if (recStopping) return;
                await StopRecording(engine.IsCountingIn);
                Close();
                return;
            }
            if (Export.IsRunning)
            {
                e.Cancel = true;
                Status(Loc.T("main.exportBusy"));
                return;
            }
            CloseProject();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            // in solo audio the bottom row is closed: the heights remembered before stay
            try { if (!audioMode) AppConfig.Set(mixerView ? "mixerHeight" : "timelineHeight", Root.RowDefinitions[3].ActualHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* View preference only. */ }
            engine.Dispose();
            camera.Dispose();
        };
    }

    // messages go to the bell in the title bar; Status("") (the old "clear") is ignored there
    void Status(string s) => Bell.Add(s);

    // --- saving ----------------------------------------------------------------------------------

    void MarkDirty()
    {
        if (store == null || store.ReadOnly) return;
        dirty = true;
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// <summary>Serializes here (the project belongs to the UI thread) and writes in the background.</summary>
    void SaveSoon()
    {
        saveTimer.Stop();
        if (store == null || !dirty) return;
        dirty = false;
        var st = store;
        int logged = st.Project.Log.Count;
        string json = st.Serialize();
        if (st.Project.Log.Count != logged) RefreshHistory(); // the save logged takes that left or came back
        saving = saving.ContinueWith(_ =>
        {
            try
            {
                st.Write(json);
                saveFailure = null;
            }
            catch (Exception e)
            {
                saveFailure = e;
                Dispatcher.UIThread.Post(() =>
                {
                    if (st != store) return;
                    Status(Loc.T("main.saveRetry", e.Message));
                    MarkDirty();
                });
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Flushes before a manager operation without blocking the UI; a failed save aborts the operation.</summary>
    async Task FlushProjectOperation()
    {
        if (store == null) return;
        saveTimer.Stop();
        await saving;
        saveTimer.Stop();
        if ((!dirty && saveFailure == null) || store.ReadOnly) return;
        var st = store;
        string json = st.Serialize();
        try
        {
            saving = Task.Run(() => st.Write(json));
            await saving;
            dirty = false;
            saveFailure = null;
        }
        catch (Exception e)
        {
            saving = Task.CompletedTask;
            saveFailure = e;
            MarkDirty();
            throw new IOException(Loc.T("main.saveRetry", e.Message), e);
        }
    }

    /// <summary>Writes whatever is pending and waits for it: before closing or switching projects.</summary>
    void FlushSave()
    {
        if (store == null) return;
        saveTimer.Stop();
        saving.Wait();
        if ((dirty || saveFailure != null) && !store.ReadOnly)
        {
            try
            {
                store.Save();
                dirty = false;
                saveFailure = null;
            }
            catch (Exception e)
            {
                Status(Loc.T("main.saveFail", e.Message));
            }
        }
    }

    void CloseProject(bool clearUndo = true)
    {
        engine.Stop();
        takePreview.Stop();
        historyConfirmPurge = false;
        historyDeletingExport = null;
        FlushSave();
        foreach (var t in tracks) t.CloseVideo();
        store?.Dispose();
        store = null;
        RefreshColorPanel();
        if (clearUndo)
        {
            undo.Clear();
            armedIds = null;
        }
        UpdateUndoButtons();
    }

    // --- projects ---------------------------------------------------------------------------------

    async Task ShowProjects()
    {
        Status("");
        await Projects.Show(store?.Dir);
    }

    async Task LoadProject(string dir)
    {
        try
        {
            await OpenProject(dir);
        }
        catch (Exception e)
        {
            Status(Loc.T("main.openFail", e.Message));
            await ShowProjects();
        }
    }

    /// <summary>Opens a project in place of the current one (throws, so the manager can show why not).</summary>
    async Task OpenProject(string dir, bool preserveUndo = false)
    {
        await FlushProjectOperation();
        Status(Loc.T("main.loading"));
        var next = await Task.Run(() => ProjectStore.Open(dir));
        CloseProject(clearUndo: !preserveUndo);
        store = next;
        RecentProjects.Touch(dir, store.Project.Name);
        await ShowProject(store);
        UpdateUndoButtons();
    }

    /// <summary>Loads every clip's audio (offline if its file is missing) and puts the project on screen.</summary>
    async Task ShowProject(ProjectStore st, Dictionary<string, MediaAudio?>? cachedAudio = null, bool preserveView = false)
    {
        engine.Stop();
        var p = st.Project;
        var failed = new List<string>();
        var list = await Task.Run(() =>
        {
            var audio = cachedAudio ?? new Dictionary<string, MediaAudio?>();
            int total = p.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaId).Distinct().Count();
            MediaAudio? AudioOf(Media m)
            {
                if (audio.TryGetValue(m.Id, out var a)) return a;
                int index = audio.Count + 1;
                Dispatcher.UIThread.Post(() => Projects.ReportProgress(Loc.T("main.loadingChannels", index, total, m.Name)));
                try
                {
                    a = st.Resolve(m.Audio) is { } path && File.Exists(path) ? new MediaAudio(path) : null;
                }
                catch
                {
                    a = null;
                    lock (failed) failed.Add(m.Name);
                }
                return audio[m.Id] = a;
            }
            return p.Tracks.Select(t => new LiveTrack(t, t.Clips.Select(c =>
            {
                var m = p.FindMedia(c.MediaId);
                return new LiveClip(c, m, m == null ? null : AudioOf(m)) { VideoPath = st.Resolve(m?.Video) };
            }).ToList(), PlaybackEngine.SampleRate)).ToList();
        });
        var previous = tracks;
        tracks = [];
        await Task.Run(() => { foreach (var old in previous) old.CloseVideo(); });
        tracks = list;
        if (!preserveView) dirty = false;
        engine.SetTracks(list, p.Settings);
        SessionName.Text = p.Name;
        Title = $"{p.Name} — GuitarCam Studio Native";
        Tempo.Set(p.Settings.Bpm);
        Timeline.Bpm = p.Settings.Bpm;
        Timeline.LengthSec = engine.LengthSec;
        Timeline.SetTracks(list, preserveView);
        Preview.SetProject(list, p.Settings);
        RefreshFormat();
        RefreshColorPanel();
        ShowTrack(list.FirstOrDefault());
        ApplyMode();

        var notes = new List<string>();
        if (st.ReadOnlyReason != null) notes.Add(st.ReadOnlyReason);
        notes.AddRange(st.Notices);
        if (failed.Count > 0) notes.Add(Loc.T("main.cantRead", string.Join(", ", failed)));
        Status(notes.Count > 0
            ? string.Join(" · ", notes)
            : Loc.T("main.summary", list.Count, $"{engine.LengthSec / 60:0}:{engine.LengthSec % 60:00}", p.Settings.Aspect));

        if (p.Settings.AudioOnly) return;
        var clips = list.Where(t => t.Track.Video).SelectMany(t => t.Clips).ToList();
        await Task.WhenAll(clips.Select(c => Task.Run(() => c.OpenVideo(st))));
        var noVideo = clips.Where(c => c.Video == null && c.Media?.Video != null).Select(c => c.Name).ToList();
        if (noVideo.Count > 0) Status(Loc.T("main.noVideo", string.Join(", ", noVideo)));
    }

    // --- transport and inspector -----------------------------------------------------------------

    void TogglePlay()
    {
        if (!engine.IsOpen)
        {
            Status(engine.Status);
            return;
        }
        if (engine.Playing) engine.Pause();
        else engine.Play();
    }

    /// <summary>Controls whose tooltip names a shortcut: text and action, refreshed when the user changes a key.</summary>
    readonly List<(Control Control, string Text, string Action)> shortcutTips = [];

    /// <summary>Tooltips with keys come from <see cref="Shortcuts"/>, so a key changed in Configuración shows up everywhere.</summary>
    void InitShortcutTips()
    {
        shortcutTips.AddRange(
        [
            (UndoButton, Loc.T("title.undo"), "undo"), (RedoButton, Loc.T("title.redo"), "redo"), (TimelineFitButton, Loc.T("timeline.fit.tip"), "fit"),
            (ToStartButton, Loc.T("transport.start.tip"), "toStart"), (CountInToggle, Loc.T("transport.countin.tip"), "countIn"),
            (ClickToggle, Loc.T("transport.click.tip"), "click"), (TunerToggle, Loc.T("transport.tuner.tip"), "tuner"), (ViewSwitch, Loc.T("transport.view.tip"), "mixer"), (FitButton, Loc.T("transport.fit.tip"), "fit"),
        ]);
        ApplyShortcutTips();
        Shortcuts.Changed += ApplyShortcutTips;
    }

    void ApplyShortcutTips()
    {
        foreach (var (c, text, action) in shortcutTips) ToolTip.SetTip(c, Shortcuts.Tip(text, action));
        string keys = string.Join(" / ", new[] { Shortcuts.Label("play"), Shortcuts.Label("record") }.Where(k => k.Length > 0));
        ToolTip.SetTip(RecStopButton, keys.Length > 0 ? $"{Loc.T("transport.rec.stop.tip")} ({keys})" : Loc.T("transport.rec.stop.tip"));
        ToolTip.SetTip(PlayButton, Shortcuts.Tip(Loc.T(engine.Playing ? "transport.pause.tip" : "transport.play.head.tip"), "play"));
        recTip = null;
        mixerKey = "";
        RefreshMixer();
    }

    async void OnKey(object? sender, KeyEventArgs e)
    {
        if (Export.IsVisible)
        {
            Export.HandleKey(e);
            return;
        }
        if (Settings.IsVisible)
        {
            Settings.HandleKey(e);
            return;
        }
        if (About.IsVisible)
        {
            About.HandleKey(e);
            return;
        }
        // shortcuts are off while the manager is open; Esc goes to it
        if (Projects.IsVisible)
        {
            if (Projects.HandleKey(e)) e.Handled = true;
            return;
        }
        if (e.Source is Control source && source.FindAncestorOfType<TextBox>(true) != null) return;
        if (restoring) { e.Handled = true; return; }
        if (RecordingBusy && await RecordingKey(e)) return;
        if (e.Key == Key.Escape)
        {
            Timeline.CancelGesture();
            Preview.CancelGesture();
            e.Handled = true;
            return;
        }
        var action = Shortcuts.Find(e);
        if (action == null) return;
        // held transport keys don't repeat (App.tsx ignores e.repeat)
        if (action is "play" or "toStart" or "click" or "countIn" or "tap" or "record" && !transportKeys.Add(e.Key)) { e.Handled = true; return; }
        switch (action)
        {
            case "undo" or "redo":
                e.Handled = true;
                await RestoreEdit(action == "redo");
                return;
            case "save":
                SaveSoon();
                break;
            case "newTrack":
                e.Handled = true;
                await NewTrackShortcut();
                return;
            case "split":
                e.Handled = true;
                await SplitAtPlayhead();
                return;
            case "delete":
                await (Timeline.Range != null ? DeleteRangeSelection() : DeleteTimelineSelection());
                break;
            case "play":
                TogglePlay();
                break;
            case "record":
                StartRecording();
                break;
            case "arm" when sel != null:
                ToggleArm(sel);
                break;
            case "toStart":
                engine.Seek(0);
                break;
            case "fit":
                Timeline.FitAll();
                break;
            case "duplicate":
                await DuplicateSelection();
                break;
            case "mixer":
                SetMixerView(!mixerView);
                break;
            case "click":
                ClickToggle.IsChecked = ClickToggle.IsChecked != true;
                break;
            case "countIn":
                CountInToggle.IsChecked = CountInToggle.IsChecked != true;
                break;
            case "tap":
                Tempo.Tap();
                break;
            case "tuner":
                TunerToggle.IsChecked = TunerToggle.IsChecked != true;
                break;
            case "mute" when sel != null:
                Edit(t => t.Channel.Muted = !t.Channel.Muted, "mute");
                break;
            case "solo" when sel != null:
                Edit(t => t.Channel.Solo = !t.Channel.Solo, "solo");
                break;
            case "leftPanel":
                SetInspectorCollapsed(inspectorExpanded.IsVisible);
                break;
            case "rightPanel":
                SetProjectBarCollapsed(projectBarExpanded.IsVisible);
                break;
            default:
                return;
        }
        e.Handled = true;
        Timeline.InvalidateVisual();
    }

    void Tick()
    {
        double pos = engine.PositionSec;
        bool videoFade = Preview.Playhead != pos && tracks.Any(t => t.Clips.Any(c => c.Clip.FadeInSec > 0 || c.Clip.FadeOutSec > 0 || c.Clip.VideoFadeInSec > 0 || c.Clip.VideoFadeOutSec > 0));
        Preview.Playhead = pos;
        bool timed = engine.Playing || RecordingBusy;
        videoFade |= timed != Preview.Timed;
        Preview.Timed = timed;
        TakeArmedPeaks();
        RefreshClearButtons();
        foreach (var t in tracks)
        {
            t.ShowL = Math.Max(Math.Max(t.MeterL, t.InputL), t.ShowL * 0.82f);
            t.ShowR = Math.Max(Math.Max(t.MeterR, t.InputR), t.ShowR * 0.82f);
            t.MeterL = t.MeterR = t.InputL = t.InputR = 0;
            double gr = t.GrDb;
            t.GrDb = 0;
            t.GrPeak = gr;
            t.GrShow = gr < t.GrShow ? gr : t.GrShow + (gr - t.GrShow) * 0.12;
        }
        Timeline.SetPlayhead(pos, engine.Playing);
        bool newFrame = false;
        var look = store?.Project.Settings;
        foreach (var t in tracks)
            foreach (var c in t.Clips)
                newFrame |= c.UpdateVideo(pos, look?.Color, look?.VideoFx);
        if (newFrame || videoFade) Preview.InvalidateVisual();
        RefreshTransport(pos);
        if (RecordingBusy) RefreshRecording();
        var m = engine.TakeMeters();
        TickMixer(pos, m.OutL, m.OutR);
        InputMeter.Push(m.InL, m.InR);
        Live.Meter.Push(m.InL, m.InR);
        TickCamera();
        TickTopBar();
        TickInspectorSpectrum();
        TickMeters();
        if (Settings.IsVisible) Settings.Meter.Push(m.InL, m.InR);
        if (engine.IsOpen) ShowDsp(engine.LoadPercent);

        if (sel != null)
        {
            grShown = Math.Min(sel.GrPeak, grShown * 0.85);
            GrText.Text = sel.Channel.Comp.On ? $"{grShown:0.0} dB" : "—";
            GrBar.Width = GrTrack.Bounds.Width * Math.Clamp(-grShown / 24, 0, 1);
        }
    }

    void Edit(Action<LiveTrack> change, string key)
    {
        if (syncing || sel == null) return;
        EditTrack(sel, change, key);
    }

    void EditTrack(LiveTrack track, Action<LiveTrack> change, string key)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project, $"track:{track.Track.Id}:{key}");
        change(track);
        track.ApplyChannel();
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        Timeline.InvalidateVisual();
    }

    /// <summary>Cubase's "deactivate all mute / solo states" (native only): one undo step for every track.</summary>
    void ClearAllToggles(bool solo)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        var lit = tracks.Where(t => solo ? t.Channel.Solo : t.Channel.Muted).ToList();
        if (lit.Count == 0) return;
        undo.Checkpoint(store.Project);
        foreach (var t in lit)
        {
            if (solo) t.Channel.Solo = false; else t.Channel.Muted = false;
            t.ApplyChannel();
        }
        MarkDirty();
        UpdateUndoButtons();
        RefreshInspectorText();
        Timeline.InvalidateVisual();
        string what = lit.Count == 1 ? Loc.T("tracks.one") : Loc.T("tracks.many", lit.Count);
        Status(Loc.T(solo ? "mute.clearedSolo" : "mute.clearedMute", what));
    }

    /// <summary>The timeline corner's clear-all M / S, lit like the track toggles while any track has one.</summary>
    void RefreshClearButtons()
    {
        bool editable = store != null && !store.ReadOnly && !restoring;
        foreach (var (b, solo) in new[] { (ClearMutesButton, false), (ClearSolosButton, true) })
        {
            bool lit = tracks.Any(t => solo ? t.Channel.Solo : t.Channel.Muted);
            if (b.Classes.Contains("lit") == lit && b.IsEnabled == editable) continue;
            b.IsEnabled = editable;
            b.Classes.Set("lit", lit);
            b.Background = lit ? (solo ? Palette.Accent : Palette.Brush("#f59e0b")) : Brushes.Transparent;
            b.BorderBrush = lit ? Brushes.Transparent : Palette.Line;
            ((TextBlock)b.Content!).Foreground = lit ? Palette.App : Palette.Dim;
            ToolTip.SetTip(b, lit ? Loc.T(solo ? "mixer.clearSolos" : "mixer.clearMutes") : Loc.T(solo ? "timeline.noSolos" : "timeline.noMutes"));
        }
    }

    void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = !restoring && !RecordingBusy && store?.ReadOnly == false && undo.CanUndo;
        RedoButton.IsEnabled = !restoring && !RecordingBusy && store?.ReadOnly == false && undo.CanRedo;
    }

    /// <summary>Restores editable data with cached audio; recordings and project history never roll back.</summary>
    async Task RestoreEdit(bool redo)
    {
        if (restoring || RecordingBusy || store == null || store.ReadOnly || Projects.IsVisible) return;
        var st = store;
        string? selected = sel?.Track.Id;
        if (!(redo ? undo.Redo(st.Project) : undo.Undo(st.Project))) return;
        restoring = true;
        MarkDirty();
        saveTimer.Stop();
        Inspector.IsEnabled = Timeline.IsEnabled = TimelineToolbar.IsEnabled = SessionChip.IsEnabled = false;
        UpdateUndoButtons();
        try
        {
            bool sameClips = tracks.Count == st.Project.Tracks.Count && tracks.Select((t, i) =>
            {
                var restored = st.Project.Tracks[i];
                return t.Track.Id == restored.Id && t.Track.Video == restored.Video
                    && JsonSerializer.Serialize(t.Track.Clips, ProjectStore.Json) == JsonSerializer.Serialize(restored.Clips, ProjectStore.Json);
            }).All(same => same);
            if (sameClips)
            {
                // Channel edits keep their decoded audio/video and playhead; no files need reopening.
                for (int i = 0; i < tracks.Count; i++)
                {
                    var restored = st.Project.Tracks[i];
                    tracks[i].Track.Name = restored.Name;
                    tracks[i].ApplyChannel(restored.Channel);
                    tracks[i].Track.Extra = restored.Extra;
                }
                st.Project.Tracks = tracks.Select(t => t.Track).ToList();
                engine.SetSettings(st.Project.Settings, glideDelay: true);
                Timeline.Bpm = st.Project.Settings.Bpm;
                Timeline.InvalidateVisual();
                Preview.SetProject(tracks, st.Project.Settings);
                RefreshFormat();
                RefreshColorPanel();
            }
            else await ApplyTracks(st);
            int index = tracks.FindIndex(t => t.Track.Id == selected);
            Timeline.Select(index >= 0 ? index : tracks.Count - 1);
            Status(Loc.T(redo ? "main.redone" : "main.undone"));
        }
        catch (Exception e)
        {
            Status(Loc.T("main.restoredNoView", e.Message));
        }
        finally
        {
            restoring = false;
            Timeline.IsEnabled = TimelineToolbar.IsEnabled = SessionChip.IsEnabled = true;
            Inspector.IsEnabled = sel != null;
            RefreshColorPanel();
            ApplyMode();
            RefreshMixer();
            MarkDirty();
            UpdateUndoButtons();
        }
    }

}
