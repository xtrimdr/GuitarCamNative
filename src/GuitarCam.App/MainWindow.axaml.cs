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

namespace GuitarCam.App;

public partial class MainWindow : Window
{
    readonly PlaybackEngine engine = new();
    readonly DispatcherTimer timer;
    Session? session;
    List<Track> tracks = [];
    Track? sel;
    bool syncing;
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

        SessionChip.Click += async (_, _) => await PickSession();
        ExportButton.Click += (_, _) => Status("Exportar todavía no está en la versión nativa");
        RecButton.Click += (_, _) => Status("Grabar todavía no está en la ventana (probalo con gclab grabar)");
        PlayButton.Click += (_, _) => TogglePlay();
        ToStartButton.Click += (_, _) => engine.Seek(0);
        FitButton.Click += (_, _) => Timeline.FitAll();
        ClickToggle.IsCheckedChanged += (_, _) => engine.ClickOn = ClickToggle.IsChecked == true;
        ClickSoundBox.ItemsSource = Enum.GetNames<ClickSound>().Select(n => n == "Bateria" ? "Batería" : n).ToList();
        ClickSoundBox.SelectedIndex = (int)ClickSound.Madera;
        ClickSoundBox.SelectionChanged += (_, _) => engine.SetClickSound((ClickSound)Math.Max(0, ClickSoundBox.SelectedIndex));

        Timeline.SeekRequested += engine.Seek;
        Timeline.SelectionChanged += i => ShowTake(i >= 0 && i < tracks.Count ? tracks[i] : null);
        Timeline.TrackToggled += _ => { };

        VolSlider.ValueChanged += (_, e) => Edit(t => t.Take.Volume = (float)e.NewValue);
        PanSlider.ValueChanged += (_, e) => Edit(t => t.Take.Pan = (float)(Math.Abs(e.NewValue) < 0.04 ? 0 : e.NewValue));
        SatSwitch.IsCheckedChanged += (_, _) => Edit(t => { t.Take.SatOn = SatSwitch.IsChecked == true; t.ApplySat(); });
        TubeButton.Click += (_, _) => Edit(t => { t.Take.SatMode = SatMode.Tube; t.ApplySat(); });
        TapeButton.Click += (_, _) => Edit(t => { t.Take.SatMode = SatMode.Tape; t.ApplySat(); });
        VidaSlider.ValueChanged += (_, e) => Edit(t => { t.Take.SatAmount = e.NewValue; t.ApplySat(); });
        CompSwitch.IsCheckedChanged += (_, _) => Edit(t => { t.Take.CompOn = CompSwitch.IsChecked == true; t.ApplyComp(); });
        LimitCheck.IsCheckedChanged += (_, _) => Edit(t => { t.Take.Limit = LimitCheck.IsChecked == true; t.ApplyComp(); });

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        ShowTake(null);

        Opened += async (_, _) =>
        {
            Status("Abriendo el audio…");
            await Task.Delay(50);
            engine.Open();
            AudioStatus.Text = engine.Status;
            timer.Start();
            var d = dir ?? Session.MostRecentDir();
            if (d != null) await LoadSession(d);
            else Status("Elegí un proyecto de GuitarCam");
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            engine.Dispose();
            foreach (var t in tracks) t.CloseVideo();
        };
    }

    void Status(string s) => StatusText.Text = s;

    async Task PickSession()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Abrir un proyecto de GuitarCam" });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        if (!File.Exists(Path.Combine(path, "session.json")))
        {
            Status("Esa carpeta no tiene un session.json");
            return;
        }
        await LoadSession(path);
    }

    async Task LoadSession(string dir)
    {
        engine.Stop();
        Status("Cargando el proyecto…");
        try
        {
            var s = Session.Load(dir);
            var skipped = new List<string>();
            var list = await Task.Run(() =>
            {
                var l = new List<Track>();
                foreach (var t in s.Takes)
                {
                    try
                    {
                        l.Add(new Track(t, dir));
                    }
                    catch
                    {
                        skipped.Add(t.Name);
                    }
                }
                return l;
            });
            session = s;
            foreach (var old in tracks) old.CloseVideo();
            tracks = list;
            engine.Bpm = s.Bpm;
            engine.SetTracks(list);
            SessionName.Text = s.Name;
            Title = $"{s.Name} — GuitarCam Studio";
            BpmText.Text = s.Bpm.ToString("0.00");
            Timeline.Bpm = s.Bpm;
            Timeline.LengthSec = engine.LengthSec;
            Timeline.SetTracks(list);
            Preview.SetSession(list, s);
            ShowTake(list.FirstOrDefault());
            Status(skipped.Count > 0
                ? $"{list.Count} tomas · no pude cargar: {string.Join(", ", skipped)}"
                : $"{list.Count} tomas · {engine.LengthSec / 60:0}:{engine.LengthSec % 60:00} · {s.AspectW}:{s.AspectH}");

            await Task.WhenAll(list.Select(t => Task.Run(() => t.OpenVideo(dir))));
            var noVideo = list.Where(t => t.Video == null).Select(t => t.Take.Name).ToList();
            if (noVideo.Count > 0) Status($"Sin video (¿falta ffmpeg/ffprobe?): {string.Join(", ", noVideo)}");
        }
        catch (Exception e)
        {
            Status($"No pude abrir el proyecto: {e.Message}");
        }
    }

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

    void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
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
                sel.Take.Muted = !sel.Take.Muted;
                break;
            case Key.S when sel != null:
                sel.Take.Solo = !sel.Take.Solo;
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
        foreach (var t in tracks) newFrame |= t.UpdateVideo(pos);
        if (newFrame) Preview.InvalidateVisual();
        TimeText.Text = $"{(int)(pos / 60):00}:{pos % 60:00.000}";
        double beats = pos * engine.Bpm / 60;
        BarText.Text = $"{(int)(beats / 4) + 1}.{(int)(beats % 4) + 1}";
        PlayIcon.Text = engine.Playing ? "" : "";
        var m = engine.TakeMeters();
        InputMeter.Push(m.InL, m.InR);
        if (engine.IsOpen) DspText.Text = $"DSP {engine.LoadPercent:0.0}%";

        if (sel != null)
        {
            grShown = Math.Min(sel.GrDb, grShown * 0.85);
            sel.GrDb = 0;
            GrText.Text = sel.Take.CompOn ? $"{grShown:0.0} dB" : "—";
            GrBar.Width = GrTrack.Bounds.Width * Math.Clamp(-grShown / 24, 0, 1);
        }
    }

    void Edit(Action<Track> change)
    {
        if (syncing || sel == null) return;
        change(sel);
        RefreshInspectorText();
        Timeline.InvalidateVisual();
    }

    void ShowTake(Track? t)
    {
        sel = t;
        syncing = true;
        Inspector.IsEnabled = t != null;
        if (t == null)
        {
            TakeTitle.Text = "Ninguna seleccionada";
            TakeInfo.Text = "Elegí una pista en la línea de tiempo";
        }
        else
        {
            var k = t.Take;
            TakeTitle.Text = k.Name;
            TakeInfo.Text = $"{t.LengthSec / 60:0}:{t.LengthSec % 60:00.0} · offset de audio {k.SyncMs:0.0} ms · nivel {t.RmsDb:0.0} dB RMS";
            VolSlider.Value = k.Volume;
            PanSlider.Value = k.Pan;
            SatSwitch.IsChecked = k.SatOn;
            VidaSlider.Value = k.SatAmount;
            CompSwitch.IsChecked = k.CompOn;
            LimitCheck.IsChecked = k.Limit;
            grShown = 0;
        }
        syncing = false;
        RefreshInspectorText();
    }

    void RefreshInspectorText()
    {
        if (sel == null) return;
        var k = sel.Take;
        double db = 20 * Math.Log10(Math.Max(1e-4, k.Volume));
        VolText.Text = k.Volume < 1e-3 ? "-∞ dB" : $"{db:+0.0;-0.0;0.0} dB";
        PanText.Text = Math.Abs(k.Pan) < 0.01 ? "Centro" : k.Pan < 0 ? $"{-k.Pan * 100:0} L" : $"{k.Pan * 100:0} R";
        TubeButton.IsChecked = k.SatMode == SatMode.Tube;
        TapeButton.IsChecked = k.SatMode == SatMode.Tape;
        double a = k.SatAmount;
        string label = a < 0.2 ? "Apenas" : a < 0.45 ? "Sutil" : a < 0.7 ? "Con vida" : a < 0.9 ? "Caliente" : "Al palo";
        VidaText.Text = $"{a * 100:0}% · {label}";
        SatSection.Classes.Set("off", !k.SatOn);
        CompSection.Classes.Set("off", !k.CompOn);
        string preset = k.CompPreset switch
        {
            "rhythm" => "Rasgueo",
            "arpeggio" => "Arpegio",
            "lead" => "Punteo",
            _ => "Manual",
        };
        CompPreset.Text = k.CompPreset == "custom" ? "Manual" : $"{preset} · {k.CompAmount * 100:0}%";
        CompRaw.Text = $"{k.Threshold:0.0} dB · {k.Ratio:0.#}:1 · {k.Attack * 1000:0}/{k.Release * 1000:0} ms · +{k.Makeup:0.0} dB";
    }
}
