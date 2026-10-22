using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GuitarCam.App.Audio;
using GuitarCam.App.Controls;
using GuitarCam.Audio;
using GuitarCam.Projects;

namespace GuitarCam.App.Dialogs;

/// <summary>
/// Port of SettingsDialog (GuitarCam/src/components/SettingsDialog.tsx): tabs "Entradas y salidas",
/// "Cámara" and "Sincronización", every control live. Same as the web except what ASIO imposes:
/// "Interfaz" lists the ASIO drivers and "Dispositivo" the driver's output pairs (no "Predeterminada de
/// Windows"), texts that say "navegador" say "driver", and the driver and output are remembered between
/// sessions (the web remembers the output; it re-picks the interface by name). The camera tab keeps its
/// shape, disabled, until the camera is ported. Like the web, gain, channels, monitoring and the fine
/// adjustment live only in memory, and the calibration is stored per input + output pair.
/// </summary>
public sealed partial class SettingsDialog : UserControl
{
    const string NotYet = "Todavía no está en la versión nativa";

    PlaybackEngine? engine;
    Func<bool> busy = () => false;
    CancellationTokenSource? calibration;
    CalibrationMethod? running;
    CalibrationProgress? progress;
    string? calError;
    bool reopening;
    bool filling;

    readonly StackPanel audioPage = new(), cameraPage = new(), syncPage = new();
    readonly ComboBox driverBox = Combo(), outputBox = Combo();
    readonly List<ToggleButton> modeButtons = [];
    readonly InspectorSlider gain;
    readonly TextBlock outputHelp = Help("");
    readonly ToggleSwitch monitorSwitch = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock pairText = new() { FontSize = 11, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel readout = new() { Spacing = 4 };
    readonly StackPanel calBox = new() { Spacing = 10 };
    readonly InspectorSlider fine;

    /// <summary>The input meter of the "Entrada" section; the window feeds it from the same read as the title bar's.</summary>
    public StereoMeter Meter { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    /// <summary>"Ajuste fino de audio (tomas nuevas)": added to the latency when recording.</summary>
    public double OverdubOffsetMs { get; private set; }
    public event Action? Dismissed;
    /// <summary>The driver was reopened or the calibration changed: the window refreshes what it shows.</summary>
    public event Action? AudioChanged;

    public SettingsDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => HideDialog();
        AudioTab.Click += (_, _) => ShowTab("audio");
        CameraTab.Click += (_, _) => ShowTab("camera");
        SyncTab.Click += (_, _) => ShowTab("sync");
        PointerPressed += (_, e) => { if (e.Source == this) HideDialog(); };
        SizeChanged += (_, _) => Panel.Height = Math.Min(640, Bounds.Height * 0.88);

        // --- Entradas y salidas
        var refresh = Act("", "Refrescar dispositivos", () => Refresh());
        var input = Section("Entrada", refresh);
        driverBox.SelectionChanged += async (_, _) =>
        {
            if (filling || driverBox.SelectedItem is not string d || d == engine?.DriverName) return;
            await Reopen(d, 0);
        };
        input.Children.Add(Field("Interfaz", driverBox));
        input.Children.Add(Meter);
        var modes = new UniformGrid { Columns = 3 };
        foreach (var (mode, text, tip) in new[]
        {
            (InputChannelMode.Stereo, "Estéreo", "Input 1 → L, Input 2 → R"),
            (InputChannelMode.Ch1Mono, "Mono 1", "Solo Input 1, duplicado a ambos lados"),
            (InputChannelMode.Ch2Mono, "Mono 2", "Solo Input 2, duplicado a ambos lados"),
        })
        {
            var b = new ToggleButton { Content = text, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { if (engine != null) engine.InputChannelMode = mode; Refresh(); };
            b.Tag = mode;
            modeButtons.Add(b);
            modes.Children.Add(b);
        }
        input.Children.Add(Field("Canales", new Border { Classes = { "seg" }, Child = modes }));
        gain = new InspectorSlider("Ganancia de entrada", 0, 24, 1, InputProcessor.DefaultInputGainDb, v => $"+{v:0} dB",
            v => { if (engine != null) engine.InputGainDb = v; Refresh(); }, "Afecta el vúmetro y el WAV grabado");
        input.Children.Add(gain);
        audioPage.Children.Add(Card(input));

        var output = Section("Salida");
        outputBox.SelectionChanged += async (_, _) =>
        {
            if (filling || engine == null || outputBox.SelectedIndex < 0 || outputBox.SelectedIndex * 2 == engine.OutputOffset) return;
            await Reopen(engine.DriverName, outputBox.SelectedIndex * 2);
        };
        output.Children.Add(Field("Dispositivo", outputBox));
        output.Children.Add(outputHelp);
        var monitorLabel = new TextBlock { Text = "Monitorear la entrada por esta salida", FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        ToolTip.SetTip(monitorLabel, "Activalo solo si no tenés monitoreo directo en la interfaz (agrega latencia)");
        monitorSwitch.IsCheckedChanged += (_, _) => { if (!filling && engine != null) engine.SoftwareMonitor = monitorSwitch.IsChecked == true; };
        output.Children.Add(Row(monitorLabel, monitorSwitch));
        audioPage.Children.Add(Card(output));

        // --- Cámara: the web's shape, disabled until the native camera exists
        var camera = Section("Cámara");
        var camKind = new UniformGrid { Columns = 2 };
        camKind.Children.Add(new ToggleButton { Content = "Webcam / Driver", IsChecked = true, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center });
        camKind.Children.Add(new ToggleButton { Content = "IP DroidCam", Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center });
        camera.Children.Add(new Border { Classes = { "seg" }, Child = camKind });
        var camBox = Combo();
        camBox.ItemsSource = new[] { "Sin cámaras" };
        camBox.SelectedIndex = 0;
        var camRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        camRow.Children.Add(camBox);
        var camRefresh = Act("", "Refrescar dispositivos", () => { });
        camRefresh.Width = camRefresh.Height = 36;
        Grid.SetColumn(camRefresh, 1);
        camRow.Children.Add(camRefresh);
        camera.Children.Add(Field("Dispositivo", camRow));
        var resolution = Combo();
        resolution.ItemsSource = new[] { "Auto (nativa)", "1080p · 30 fps", "720p · 30 fps", "480p · 30 fps" };
        resolution.SelectedIndex = 0;
        camera.Children.Add(Field("Resolución", resolution));
        camera.Children.Add(Row(new TextBlock { Text = "Espejar imagen en vivo", FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center },
            Act("", "Espejar cámara en vivo", () => { })));
        var cameraOff = new Border { Child = camera, IsEnabled = false, Opacity = 0.4 };
        ToolTip.SetTip(cameraOff, NotYet);
        ToolTip.SetShowOnDisabled(cameraOff, true);
        cameraPage.Children.Add(Card(cameraOff));

        // --- Sincronización
        var sync = Section("Sincronización por defecto");
        sync.Children.Add(pairText);
        sync.Children.Add(new Border { Background = Palette.Raised, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Child = readout });
        sync.Children.Add(new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = calBox });
        fine = new InspectorSlider("Ajuste fino de audio (tomas nuevas)", -100, 100, 1, 0, v => v > 0 ? $"+{v:0} ms" : $"{v:0} ms",
            v => { OverdubOffsetMs = v; Refresh(); }, "Se suma a la latencia (calibrada o estimada). Negativo = la toma nueva queda más adelantada");
        sync.Children.Add(fine);
        var cameraLatency = new InspectorSlider("Latencia de cámara", 0, 200, 5, 80, v => $"{v:0} ms", _ => { }, "Cuánto llega tarde la imagen respecto del audio");
        cameraLatency.Set(80);
        var cameraLatencyOff = new Border { Child = cameraLatency, IsEnabled = false, Opacity = 0.4 };
        ToolTip.SetTip(cameraLatencyOff, NotYet);
        ToolTip.SetShowOnDisabled(cameraLatencyOff, true);
        sync.Children.Add(cameraLatencyOff);
        syncPage.Children.Add(Card(sync));
    }

    /// <summary>The engine whose devices and calibration this edits, and what says recording is going on.</summary>
    public void Attach(PlaybackEngine e, Func<bool> recordingBusy)
    {
        engine = e;
        busy = recordingBusy;
    }

    public void Show(string startTab = "audio")
    {
        IsVisible = true;
        ShowTab(startTab);
        Focus();
    }

    public void HideDialog()
    {
        calibration?.Cancel();
        IsVisible = false;
        Dismissed?.Invoke();
    }

    /// <summary>Escape closes (cancelling a running calibration first, like the web's close()).</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) HideDialog();
        e.Handled = true;
        return true;
    }

    void ShowTab(string id)
    {
        AudioTab.Classes.Set("active", id == "audio");
        CameraTab.Classes.Set("active", id == "camera");
        SyncTab.Classes.Set("active", id == "sync");
        Body.Content = id switch { "camera" => cameraPage, "sync" => syncPage, _ => audioPage };
        Refresh();
    }

    async Task Reopen(string? driver, int outputOffset)
    {
        if (engine == null || reopening || busy() || engine.Calibrating) { Refresh(); return; }
        var e = engine;
        reopening = true;
        Refresh();
        await Task.Run(() => e.Open(driver, 0, outputOffset));
        reopening = false;
        if (e.IsOpen)
        {
            string name = e.DriverName!, offset = e.OutputOffset.ToString(CultureInfo.InvariantCulture);
            _ = Task.Run(() =>
            {
                try { AppConfig.Set("audio.driver", name); AppConfig.Set("audio.outputOffset", offset); }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
            });
        }
        AudioChanged?.Invoke();
        Refresh();
    }

    async Task Calibrate(CalibrationMethod method)
    {
        if (engine is not { IsOpen: true } e || busy() || running != null) return;
        e.StopTransport();
        var cts = calibration = new CancellationTokenSource();
        running = method;
        progress = null;
        calError = null;
        Refresh();
        try
        {
            var res = await e.MeasureLatencyAsync(method, p => Dispatcher.UIThread.Post(() =>
            {
                if (running != method) return;
                progress = p;
                Refresh();
            }), cts.Token);
            if (res is { } r)
            {
                var info = e.GetLatencyInfo();
                var stored = new StoredCalibration
                {
                    LatencyMs = Math.Round(r.LatencyMs, 1),
                    SpreadMs = Math.Round(r.SpreadMs, 1),
                    Method = LatencyCalibration.MethodToString(method),
                    EstimateMs = info.TotalMs,
                    SampleRate = info.SampleRate,
                    At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                LatencyCalibration.WriteCalibration(e.DriverName!, e.CalibrationOutputId, stored);
                e.SetCalibratedLatency(stored.LatencyMs);
            }
            else if (!cts.IsCancellationRequested)
                calError = method == CalibrationMethod.Tap
                    ? "No detecté suficientes golpes. Tocá una nota corta y apagada en cada click, fuerte y sin dejarla sonar."
                    : "No volvió la señal. Revisá que el cable vaya de la salida a una entrada de la interfaz (o el micrófono frente al parlante) y que la ganancia no esté al mínimo.";
        }
        catch (InvalidOperationException x)
        {
            calError = x.Message;
        }
        finally
        {
            running = null;
            calibration = null;
            AudioChanged?.Invoke();
            Refresh();
        }
    }

    void Forget()
    {
        if (engine?.DriverName is not { } d) return;
        engine.SetCalibratedLatency(null);
        LatencyCalibration.WriteCalibration(d, engine.CalibrationOutputId, null);
        calError = null;
        AudioChanged?.Invoke();
        Refresh();
    }

    /// <summary>Fills every control from the engine (the flag keeps that from looking like an edit).</summary>
    void Refresh()
    {
        if (!IsVisible || engine == null) return;
        var e = engine;
        bool locked = busy() || e.Calibrating || reopening;
        filling = true;
        try
        {
            var drivers = AsioDuplex.DriverNames();
            driverBox.ItemsSource = drivers.Length > 0 ? drivers : ["Sin dispositivos"];
            driverBox.SelectedItem = drivers.Length > 0 ? e.DriverName : "Sin dispositivos";
            driverBox.IsEnabled = drivers.Length > 0 && !locked;
            foreach (var b in modeButtons) b.IsChecked = (InputChannelMode)b.Tag! == e.InputChannelMode;
            gain.Set(e.InputGainDb);

            int pairs = e.OutputChannels / 2;
            outputBox.ItemsSource = Enumerable.Range(0, pairs).Select(i => $"Salida {2 * i + 1}-{2 * i + 2}").ToArray();
            outputBox.SelectedIndex = pairs > 0 ? e.OutputOffset / 2 : -1;
            outputBox.IsEnabled = pairs > 0 && !locked;
            outputHelp.Text = locked && !reopening ? "No se puede cambiar mientras grabás o calibrás."
                : "Por acá suenan las pistas, el click y el monitoreo. Usá la salida de la interfaz para la menor latencia. Cada combinación de entrada y salida guarda su propia calibración.";
            monitorSwitch.IsChecked = e.SoftwareMonitor;
            fine.Set(OverdubOffsetMs);
        }
        finally
        {
            filling = false;
        }
        RefreshSync(e);
    }

    void RefreshSync(PlaybackEngine e)
    {
        string pair = $"Entrada {e.InputOffset + 1}-{e.InputOffset + 2} → Salida {e.OutputOffset + 1}-{e.OutputOffset + 2}";
        pairText.Text = e.DriverName is { } d ? $"{d} · {pair}" : "Sin entrada → Salida predeterminada";
        ToolTip.SetTip(pairText, pairText.Text);
        var stored = e.DriverName is { } dn ? LatencyCalibration.ReadCalibration(dn, e.CalibrationOutputId) : null;

        readout.Children.Clear();
        readout.IsVisible = e.IsOpen;
        if (e.IsOpen)
        {
            var info = e.GetLatencyInfo();
            bool calibrated = info.CalibratedMs != null;
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            top.Children.Add(new TextBlock { Text = calibrated ? "Latencia calibrada" : "Latencia estimada", FontSize = 12 });
            var ms = new TextBlock { Text = $"{Math.Round(info.AppliedMs):0} ms", FontSize = 12, Classes = { "mono" } };
            Grid.SetColumn(ms, 1);
            top.Children.Add(ms);
            readout.Children.Add(top);
            string how = calibrated
                ? $"{(stored?.Method == "tap" ? "medida tocando" : "medida por loopback")} · el driver estima {info.TotalMs:0.#} ms"
                : $"salida {info.OutputMs:0.#} ms + entrada {info.InputMs:0.#} ms, según el driver";
            readout.Children.Add(new TextBlock { Text = how + " · se compensa sola en cada toma nueva", FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap });
            if (info.OutputMs > 60)
                readout.Children.Add(Warning("La salida tiene mucha latencia: ¿auriculares Bluetooth, HDMI o un parlante externo? Escuchá por la salida de la interfaz."));
            if (stored != null && LatencyCalibration.IsCalibrationStale(stored, info.TotalMs, info.SampleRate))
                readout.Children.Add(Warning("Cambió la configuración de audio desde que calibraste (frecuencia o dispositivo de salida). Conviene volver a calibrar."));
        }

        calBox.Children.Clear();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Text = "Calibrar latencia", FontSize = 12, FontWeight = FontWeight.SemiBold });
        if (stored != null && running == null)
        {
            var useEstimate = new Button { Content = "Usar la estimación", Classes = { "link" } };
            ToolTip.SetTip(useEstimate, "Descartar la medición y usar lo que informa el driver");
            useEstimate.Click += (_, _) => Forget();
            Grid.SetColumn(useEstimate, 1);
            head.Children.Add(useEstimate);
        }
        calBox.Children.Add(head);
        const string cableHow = "Conectá un cable de la salida de la interfaz a una entrada (o poné un micrófono frente al parlante). Bajá los auriculares: suenan 8 pulsos de ruido.";
        const string tapHow = "Escuchá 4 clicks de cuenta y después tocá una nota corta y apagada justo en cada uno de los 12 siguientes. Es menos precisa que el cable.";
        if (running is { } m)
        {
            calBox.Children.Add(Help(m == CalibrationMethod.Tap ? tapHow : cableHow));
            string status = progress is not { } p ? "Preparando…"
                : p.CountIn ? "Escuchá…"
                : m == CalibrationMethod.Tap ? $"Tocá {p.Step} / {p.Total}" : $"Pulso {p.Step} / {p.Total}";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new Border
            {
                Background = Palette.Raised, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 6),
                Child = new TextBlock { Text = status, FontSize = 14, Classes = { "mono" }, Foreground = progress is { CountIn: true } ? Palette.Accent : Palette.Fg },
            });
            var cancel = new Button { Content = "Cancelar", Classes = { "chip" } };
            cancel.Click += (_, _) => calibration?.Cancel();
            Grid.SetColumn(cancel, 1);
            row.Children.Add(cancel);
            calBox.Children.Add(row);
        }
        else
        {
            bool can = e.IsOpen && !busy();
            var buttons = new UniformGrid { Columns = 2 };
            foreach (var (method, text, tip) in new[] { (CalibrationMethod.Loopback, "Con cable", cableHow), (CalibrationMethod.Tap, "Tocando", tapHow) })
            {
                var b = new Button { Content = text, Classes = { "chip" }, Height = 32, FontSize = 11, IsEnabled = can, Margin = new Thickness(method == CalibrationMethod.Tap ? 4 : 0, 0, method == CalibrationMethod.Loopback ? 4 : 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
                ToolTip.SetTip(b, tip);
                b.Click += async (_, _) => await Calibrate(method);
                buttons.Children.Add(b);
            }
            calBox.Children.Add(buttons);
            calBox.Children.Add(Help(can ? "Mide la latencia real. Con un cable de la salida a una entrada es lo más preciso; si no, tocá sobre el click."
                : "Abrí la placa de audio (y no estés grabando) para calibrar."));
            if (stored != null)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(stored.At).LocalDateTime;
                calBox.Children.Add(new TextBlock { Text = $"Última: {stored.LatencyMs:0.#} ms ± {stored.SpreadMs:0.#} ms · {at:d/M/yyyy, HH:mm:ss}", FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" } });
                bool tap = stored.Method == "tap";
                if (stored.SpreadMs > (tap ? 20 : 2))
                    calBox.Children.Add(Warning($"Las mediciones variaron bastante entre sí. Repetila{(tap ? " o probá con el cable" : "")}."));
            }
        }
        if (calError != null) calBox.Children.Add(Warning(calError));
    }

    void RefreshSync() { if (engine != null) RefreshSync(engine); }

    // --- building blocks (SettingsDialog.tsx Section / Field) ------------------------------------

    static StackPanel Section(string title, Control? action = null)
    {
        var s = new StackPanel { Spacing = 14 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 20 };
        head.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        if (action != null)
        {
            Grid.SetColumn(action, 1);
            head.Children.Add(action);
        }
        s.Children.Add(head);
        return s;
    }

    static Border Card(Control c) => new() { Padding = new Thickness(16), BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = c };

    static Control Field(string label, Control control)
    {
        var f = new StackPanel { Spacing = 6 };
        f.Children.Add(new TextBlock { Text = label, Classes = { "dim" } });
        f.Children.Add(control);
        return f;
    }

    static Grid Row(Control left, Control right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        g.Children.Add(left);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    static ComboBox Combo() => new() { Classes = { "field" } };

    static TextBlock Help(string text) => new() { Text = text, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap };

    static TextBlock Warning(string text) => new() { Text = text, FontSize = 11, Foreground = Palette.Accent, TextWrapping = TextWrapping.Wrap };

    static Button Act(string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "act" }, Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 13 } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }
}
