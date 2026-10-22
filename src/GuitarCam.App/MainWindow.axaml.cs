using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GuitarCam.App.Audio;
using GuitarCam.App.Model;
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
        ExportButton.Click += (_, _) => Status("Exportar todavía no está en la versión nativa");
        RecButton.Click += (_, _) => Status("Grabar todavía no está en la ventana (probalo con gclab grabar)");
        PlayButton.Click += (_, _) => TogglePlay();
        ToStartButton.Click += (_, _) => engine.Seek(0);
        FitButton.Click += (_, _) => Timeline.FitAll();
        UndoButton.Click += async (_, _) => await RestoreEdit(false);
        RedoButton.Click += async (_, _) => await RestoreEdit(true);
        ClickToggle.IsCheckedChanged += (_, _) => engine.ClickOn = ClickToggle.IsChecked == true;
        ClickSoundBox.ItemsSource = Enum.GetNames<ClickSound>().Select(n => n == "Bateria" ? "Batería" : n).ToList();
        ClickSoundBox.SelectedIndex = (int)ClickSound.Madera;
        ClickSoundBox.SelectionChanged += (_, _) => engine.SetClickSound((ClickSound)Math.Max(0, ClickSoundBox.SelectedIndex));

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
        };
        Timeline.TrackToggleRequested += (t, solo) => EditTrack(t, x =>
        {
            if (solo) x.Channel.Solo = !x.Channel.Solo;
            else x.Channel.Muted = !x.Channel.Muted;
        }, solo ? "solo" : "mute");
        InitTimelineEditing();

        VolSlider.ValueChanged += (_, e) => Edit(t => t.Channel.Volume = e.NewValue, "volume");
        PanSlider.ValueChanged += (_, e) => Edit(t => t.Channel.Pan = Math.Abs(e.NewValue) < 0.04 ? 0 : e.NewValue, "pan");
        SatSwitch.IsCheckedChanged += (_, _) => Edit(t => { t.Channel.Sat.On = SatSwitch.IsChecked == true; t.ApplySat(); }, "sat.on");
        TubeButton.Click += (_, _) => Edit(t => { t.Channel.Sat.Mode = "tube"; t.ApplySat(); }, "sat.mode");
        TapeButton.Click += (_, _) => Edit(t => { t.Channel.Sat.Mode = "tape"; t.ApplySat(); }, "sat.mode");
        VidaSlider.ValueChanged += (_, e) => Edit(t => { t.Channel.Sat.Amount = e.NewValue; t.ApplySat(); }, "sat.amount");
        CompSwitch.IsCheckedChanged += (_, _) => Edit(t => { t.Channel.Comp.On = CompSwitch.IsChecked == true; t.ApplyComp(); }, "comp.on");
        LimitCheck.IsCheckedChanged += (_, _) => Edit(t => { t.Channel.Comp.Limit = LimitCheck.IsChecked == true; t.ApplyComp(); }, "comp.limit");

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        Projects.OpenProject = async d =>
        {
            await OpenProject(d);
            Projects.Hide();
        };
        Projects.ImportWebProject = async d =>
        {
            string msg = await ImportWebProject(d);
            Projects.Hide();
            Status(msg);
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
            Status("Abriendo el audio…");
            await Task.Delay(50);
            engine.Open();
            AudioStatus.Text = engine.Status;
            timer.Start();
            // like the web app, it starts on the project manager (a folder on the command line opens directly)
            if (dir != null && ProjectStore.IsProject(dir)) await LoadProject(dir);
            else await ShowProjects();
        };
        Closing += (_, _) => CloseProject();
        Closed += (_, _) =>
        {
            timer.Stop();
            engine.Dispose();
        };
    }

    void Status(string s) => StatusText.Text = s;

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
        string json = st.Serialize();
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
                    Status($"No pude guardar: {e.Message}. Lo vuelvo a intentar…");
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
            throw new IOException($"No pude guardar: {e.Message}. Lo vuelvo a intentar…", e);
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
                Status($"No pude guardar: {e.Message}");
            }
        }
    }

    void CloseProject(bool clearUndo = true)
    {
        engine.Stop();
        FlushSave();
        foreach (var t in tracks) t.CloseVideo();
        store?.Dispose();
        store = null;
        if (clearUndo) undo.Clear();
        UpdateUndoButtons();
    }

    // --- projects ---------------------------------------------------------------------------------

    async Task ShowProjects()
    {
        Status("");
        await Projects.Show(store?.Dir);
    }

    /// <summary>Copies the web project into "Name (nativa)" next to it and opens the copy. Returns what to tell the user.</summary>
    async Task<string> ImportWebProject(string webDir)
    {
        string target = WebImporter.SuggestTarget(webDir);
        var r = await Task.Run(() =>
        {
            var res = WebImporter.Import(webDir, target);
            res.Store.Dispose();
            return res;
        });
        await OpenProject(target);
        string how = r.Copied == 0 ? "" : $" · {r.Copied} archivo(s) copiados (otra unidad)";
        string miss = r.Missing.Count == 0 ? "" : $" · faltaban {r.Missing.Count} archivo(s) en la web";
        return $"Importado en \"{Path.GetFileName(target)}\"; el de la web quedó igual{how}{miss}";
    }

    async Task LoadProject(string dir)
    {
        try
        {
            await OpenProject(dir);
        }
        catch (Exception e)
        {
            Status($"No pude abrir el proyecto: {e.Message}");
            await ShowProjects();
        }
    }

    /// <summary>Opens a project in place of the current one (throws, so the manager can show why not).</summary>
    async Task OpenProject(string dir, bool preserveUndo = false)
    {
        await FlushProjectOperation();
        Status("Cargando el proyecto…");
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
                Dispatcher.UIThread.Post(() => Projects.ReportProgress($"Cargando tomas… {index}/{total} · {m.Name}"));
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
                return new LiveClip(c, m, m == null ? null : AudioOf(m));
            }).ToList(), PlaybackEngine.SampleRate)).ToList();
        });
        var previous = tracks;
        tracks = [];
        await Task.Run(() => { foreach (var old in previous) old.CloseVideo(); });
        tracks = list;
        if (!preserveView) dirty = false;
        engine.SetTracks(list, p.Settings);
        SessionName.Text = p.Name;
        Title = $"{p.Name} — GuitarCam Studio";
        BpmText.Text = p.Settings.Bpm.ToString("0.00");
        Timeline.Bpm = p.Settings.Bpm;
        Timeline.LengthSec = engine.LengthSec;
        Timeline.SetTracks(list, preserveView);
        Preview.SetProject(list, p.Settings);
        ShowTrack(list.FirstOrDefault());

        var notes = new List<string>();
        if (st.ReadOnlyReason != null) notes.Add(st.ReadOnlyReason);
        notes.AddRange(st.Notices);
        if (failed.Count > 0) notes.Add($"no pude leer: {string.Join(", ", failed)}");
        Status(notes.Count > 0
            ? string.Join(" · ", notes)
            : $"{list.Count} pistas · {engine.LengthSec / 60:0}:{engine.LengthSec % 60:00} · {p.Settings.Aspect}");

        var clips = list.Where(t => t.Track.Video).SelectMany(t => t.Clips).ToList();
        await Task.WhenAll(clips.Select(c => Task.Run(() => c.OpenVideo(st))));
        var noVideo = clips.Where(c => c.Video == null && c.Media?.Video != null).Select(c => c.Name).ToList();
        if (noVideo.Count > 0) Status($"Sin video (¿falta el archivo, ffmpeg o ffprobe?): {string.Join(", ", noVideo)}");
    }

    // --- transport and inspector -----------------------------------------------------------------

    void TogglePlay()
    {
        if (!engine.IsOpen)
        {
            Status(engine.Status);
            return;
        }
        if (engine.Playing) engine.Stop();
        else engine.Play();
    }

    async void OnKey(object? sender, KeyEventArgs e)
    {
        // shortcuts are off while the manager is open; Esc goes to it
        if (Projects.IsVisible)
        {
            if (Projects.HandleKey(e)) e.Handled = true;
            return;
        }
        if (e.Source is Control source && source.FindAncestorOfType<TextBox>(true) != null) return;
        if (restoring) { e.Handled = true; return; }
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (e.Key is Key.Z or Key.Y)
            {
                e.Handled = true;
                await RestoreEdit(e.Key == Key.Y || (e.KeyModifiers & KeyModifiers.Shift) != 0);
            }
            else if (e.Key == Key.S)
            {
                e.Handled = true;
                SaveSoon();
            }
            return;
        }
        if ((e.KeyModifiers & KeyModifiers.Alt) != 0) return;
        switch (e.Key)
        {
            case Key.Escape:
                Timeline.CancelGesture();
                Preview.CancelGesture();
                break;
            case Key.Delete:
                await DeleteTimelineSelection();
                break;
            case Key.Space:
                TogglePlay();
                break;
            case Key.Home:
                engine.Seek(0);
                break;
            case Key.F:
                Timeline.FitAll();
                break;
            case Key.C:
                ClickToggle.IsChecked = ClickToggle.IsChecked != true;
                break;
            case Key.M when sel != null:
                Edit(t => t.Channel.Muted = !t.Channel.Muted, "mute");
                break;
            case Key.S when sel != null:
                Edit(t => t.Channel.Solo = !t.Channel.Solo, "solo");
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
        foreach (var t in tracks)
        {
            t.ShowL = Math.Max(t.MeterL, t.ShowL * 0.82f);
            t.ShowR = Math.Max(t.MeterR, t.ShowR * 0.82f);
            t.MeterL = t.MeterR = 0;
        }
        Timeline.SetPlayhead(pos, engine.Playing);
        bool newFrame = false;
        foreach (var t in tracks)
            foreach (var c in t.Clips)
                newFrame |= c.UpdateVideo(pos);
        if (newFrame) Preview.InvalidateVisual();
        TimeText.Text = $"{(int)(pos / 60):00}:{pos % 60:00.000}";
        double beats = pos * engine.Bpm / 60;
        BarText.Text = $"{(int)(beats / 4) + 1}.{(int)(beats % 4) + 1}";
        PlayIcon.Text = engine.Playing ? "" : "";
        var m = engine.TakeMeters();
        InputMeter.Push(m.InL, m.InR);
        if (engine.IsOpen) DspText.Text = $"DSP {engine.LoadPercent:0.0}%";

        if (sel != null)
        {
            grShown = Math.Min(sel.GrDb, grShown * 0.85);
            sel.GrDb = 0;
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

    void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = !restoring && store?.ReadOnly == false && undo.CanUndo;
        RedoButton.IsEnabled = !restoring && store?.ReadOnly == false && undo.CanRedo;
    }

    /// <summary>Restores editable data with cached audio; recordings and project history never roll back.</summary>
    async Task RestoreEdit(bool redo)
    {
        if (restoring || store == null || store.ReadOnly || Projects.IsVisible) return;
        var st = store;
        string? selected = sel?.Track.Id;
        double position = engine.PositionSec;
        var audio = tracks.SelectMany(t => t.Clips).Where(c => c.Media != null)
            .GroupBy(c => c.Media!.Id).ToDictionary(g => g.Key, g => g.First().Audio);
        if (!(redo ? undo.Redo(st.Project) : undo.Undo(st.Project))) return;
        restoring = true;
        engine.Stop();
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
                Preview.SetProject(tracks, st.Project.Settings);
            }
            else await ShowProject(st, audio, true);
            int index = tracks.FindIndex(t => t.Track.Id == selected);
            Timeline.Select(index >= 0 ? index : tracks.Count - 1);
            engine.Seek(position);
            Status(redo ? "Rehice el último cambio" : "Deshice el último cambio");
        }
        catch (Exception e)
        {
            Status($"Recuperé los ajustes, pero no pude actualizar la vista: {e.Message}");
        }
        finally
        {
            restoring = false;
            Timeline.IsEnabled = TimelineToolbar.IsEnabled = SessionChip.IsEnabled = true;
            Inspector.IsEnabled = sel != null;
            MarkDirty();
            UpdateUndoButtons();
        }
    }

    void ShowTrack(LiveTrack? t)
    {
        sel = t;
        syncing = true;
        Inspector.IsEnabled = t != null && !restoring;
        if (t == null)
        {
            TakeTitle.Text = "Ninguna seleccionada";
            TakeInfo.Text = "Elegí una pista en la línea de tiempo";
        }
        else
        {
            var ch = t.Channel;
            TakeTitle.Text = t.Track.Name;
            var first = t.Clips.FirstOrDefault();
            TakeInfo.Text = first == null
                ? "Pista vacía"
                : first.Offline
                    ? $"{first.Name}: sin conexión (falta el archivo)"
                    : $"{first.LengthSec / 60:0}:{first.LengthSec % 60:00.0} · offset de audio {first.Media?.SyncMs ?? 0:0.0} ms · nivel {t.RmsDb:0.0} dB RMS"
                      + (t.Clips.Count > 1 ? $" · {t.Clips.Count} clips" : "");
            VolSlider.Value = ch.Volume;
            PanSlider.Value = ch.Pan;
            SatSwitch.IsChecked = ch.Sat.On;
            VidaSlider.Value = ch.Sat.Amount;
            CompSwitch.IsChecked = ch.Comp.On;
            LimitCheck.IsChecked = ch.Comp.Limit;
            grShown = 0;
        }
        syncing = false;
        RefreshInspectorText();
    }

    void RefreshInspectorText()
    {
        if (sel == null) return;
        var ch = sel.Channel;
        double db = 20 * Math.Log10(Math.Max(1e-4, ch.Volume));
        VolText.Text = ch.Volume < 1e-3 ? "-∞ dB" : $"{db:+0.0;-0.0;0.0} dB";
        PanText.Text = Math.Abs(ch.Pan) < 0.01 ? "Centro" : ch.Pan < 0 ? $"{-ch.Pan * 100:0} L" : $"{ch.Pan * 100:0} R";
        TubeButton.IsChecked = ch.Sat.Mode != "tape";
        TapeButton.IsChecked = ch.Sat.Mode == "tape";
        double a = ch.Sat.Amount;
        string label = a < 0.2 ? "Apenas" : a < 0.45 ? "Sutil" : a < 0.7 ? "Con vida" : a < 0.9 ? "Caliente" : "Al palo";
        VidaText.Text = $"{a * 100:0}% · {label}";
        SatSection.Classes.Set("off", !ch.Sat.On);
        CompSection.Classes.Set("off", !ch.Comp.On);
        var c = ch.Comp;
        string preset = c.Preset switch
        {
            "rhythm" => "Rasgueo",
            "arpeggio" => "Arpegio",
            "lead" => "Punteo",
            _ => "Manual",
        };
        CompPreset.Text = c.Preset == "custom" ? "Manual" : $"{preset} · {c.Amount * 100:0}%";
        CompRaw.Text = $"{c.Threshold:0.0} dB · {c.Ratio:0.#}:1 · {c.Attack * 1000:0}/{c.Release * 1000:0} ms · +{c.Makeup:0.0} dB";
    }
}
