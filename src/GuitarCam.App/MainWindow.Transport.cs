using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using GuitarCam.App.Controls;
using GuitarCam.Audio;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>Port of TransportBar.tsx and App.tsx transport actions (the Timeline / Mixer toggle is in
/// MainWindow.Mixer.cs). The click menu auditions through <see cref="PlaybackEngine.PreviewClick"/>. View preferences and
/// click settings belong to the app, while tempo belongs to the project (outside undo, like the web).</summary>
public partial class MainWindow
{
    readonly HashSet<Key> transportKeys = [];
    Flyout? clickMenu;
    readonly List<Button> clickChoices = [];
    InspectorSlider? clickVolume;
    // like ClickSoundMenu's volTimer: one audition 150 ms after the volume stops moving
    readonly Avalonia.Threading.DispatcherTimer clickVolumeTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    Task clickPrefsSaving = Task.CompletedTask;
    bool? transportPlaying;
    string? clockTip;
    string? recTip;
    static readonly string[] ClickLabels = [Loc.T("click.label.0"), Loc.T("click.label.1"), Loc.T("click.label.2"), Loc.T("click.label.3"), Loc.T("click.label.4"), Loc.T("click.label.5")];
    static readonly string[] ClickHints = [Loc.T("click.hint.0"), Loc.T("click.hint.1"), Loc.T("click.hint.2"), Loc.T("click.hint.3"), Loc.T("click.hint.4"), Loc.T("click.hint.5")];

    void InitTransport()
    {
        engine.Loop = LoopToggle.IsChecked == true;
        LoopToggle.IsCheckedChanged += (_, _) => engine.Loop = LoopToggle.IsChecked == true;
        StopButton.Click += (_, _) => engine.StopTransport();
        ClockButton.Click += (_, _) => SetRulerMode(!Timeline.RulerInBars);
        Tempo.Changed += SetTempo;
        AddHandler(KeyUpEvent, OnTransportKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Deactivated += (_, _) => transportKeys.Clear();
        if (!Enum.TryParse<ClickSound>(AppConfig.Get("click.sound"), out var sound)) sound = ClickSound.Bip;
        if (!double.TryParse(AppConfig.Get("click.volumeDb"), NumberStyles.Float, CultureInfo.InvariantCulture, out double volume) || !double.IsFinite(volume)) volume = 0;
        engine.SetClickSound(sound, Math.Clamp(volume, -24, 9));
        BuildClickMenu();
        Closed += (_, _) => clickMenu?.Hide();
        RefreshClickMenu();
    }

    /// <summary>Releasing a transport key ends it here: a focused button clicks on Space's key-up (the last M or S
    /// clicked in the mixer), so the key that just played or stopped must not reach it.</summary>
    void OnTransportKeyUp(object? sender, KeyEventArgs e)
    {
        if (transportKeys.Remove(e.Key)) e.Handled = true;
    }

    void SetTempo(double value)
    {
        if (store == null || store.ReadOnly || restoring || store.Project.Settings.Bpm == value) return;
        // no undo step: the tempo stays outside the snapshots, like the web's setBpm
        store.Project.Settings.Bpm = value;
        engine.Bpm = value;
        Timeline.Bpm = value;
        // MIDI notes are in beats: they follow the tempo
        foreach (var t in tracks) t.SetMidi(value);
        engine.RefreshClipTiming();
        Timeline.LengthSec = engine.LengthSec;
        Tempo.Set(value);
        Timeline.InvalidateVisual();
        MarkDirty();
    }

    void BuildClickMenu()
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "SONIDO DEL CLICK", Classes = { "label" }, Margin = new Thickness(4, 0) });
        var sounds = new StackPanel();
        for (int i = 0; i < ClickLabels.Length; i++)
        {
            var sound = (ClickSound)i;
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = ClickLabels[i], FontSize = 12 });
            text.Children.Add(new TextBlock { Text = ClickHints[i], FontSize = 11, Foreground = Palette.Faint, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var b = new Button { Content = text, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6) };
            ToolTip.SetTip(b, Loc.T("click.pick.tip"));
            b.Click += (_, _) =>
            {
                engine.SetClickSound(sound, engine.ClickVolumeDb);
                RefreshClickMenu(); SaveClickPreferences();
                PreviewClick();
            };
            clickChoices.Add(b); sounds.Children.Add(b);
        }
        body.Children.Add(sounds);
        clickVolume = new InspectorSlider(Loc.T("click.volume"), -24, 9, .5, 0, Db, v =>
        {
            engine.SetClickSound(engine.ClickSound, v);
            RefreshClickMenu(); SaveClickPreferences();
            clickVolumeTimer.Stop();
            clickVolumeTimer.Start();
        });
        clickVolumeTimer.Tick += (_, _) => { clickVolumeTimer.Stop(); PreviewClick(); };
        body.Children.Add(new Border { Child = clickVolume, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 8, 4, 0) });
        var panel = new Border { Width = 240, Padding = new Thickness(8), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = body };
        clickMenu = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedRight };
        ClickSoundButton.Flyout = clickMenu;
    }

    void RefreshClickMenu()
    {
        ToolTip.SetTip(ClickSoundButton, Loc.T("click.sound.now", ClickLabels[(int)engine.ClickSound]));
        for (int i = 0; i < clickChoices.Count; i++)
        {
            clickChoices[i].Classes.Set("lit", (int)engine.ClickSound == i);
            clickChoices[i].Background = (int)engine.ClickSound == i ? Palette.Alpha(Palette.AccentColor, .14) : Avalonia.Media.Brushes.Transparent;
            clickChoices[i].Foreground = (int)engine.ClickSound == i ? Palette.Accent : Palette.Fg;
        }
        clickVolume?.Set(engine.ClickVolumeDb);
    }

    void SaveClickPreferences()
    {
        string sound = engine.ClickSound.ToString(), volume = engine.ClickVolumeDb.ToString(CultureInfo.InvariantCulture);
        clickPrefsSaving = clickPrefsSaving.ContinueWith(_ =>
        {
            try { AppConfig.Set("click.sound", sound); AppConfig.Set("click.volumeDb", volume); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Keep this session's choice. */ }
        }, TaskScheduler.Default);
    }

    void PreviewClick()
    {
        if (engine.IsOpen) engine.PreviewClick();
        else Status(engine.Status);
    }

    static string TransportBars(double sec, double bpm)
    {
        long n = (long)Math.Floor(Math.Max(0, sec) * bpm / 15 + 1e-6);
        return $"{n / 16 + 1}.{n % 16 / 4 + 1}.{n % 4 + 1}";
    }
    static string TransportTime(double sec) => $"{(long)(Math.Max(0, sec) / 60):00}:{Math.Max(0, sec) % 60:00.0}";

    // under the input meter, small: the format without the driver ("Focusrite USB ASIO · 48 kHz · …"), which goes to the tooltip
    void ShowAudioStatus()
    {
        var parts = engine.Status.Split(" · ", 2);
        AudioStatus.Text = parts.Length == 2 ? $"· {parts[1]}" : "";
        ToolTip.SetTip(AudioCell, engine.Status);
    }

    bool audioStallShown;

    /// <summary>The interface stopped calling back (unplugged, driver hung): a toast once, until it calls again or is reopened.</summary>
    void TickAudioDriver()
    {
        bool stalled = engine.Stalled;
        if (stalled == audioStallShown) return;
        audioStallShown = stalled;
        if (stalled) Toast(Loc.T("toast.audio.lost", engine.DriverName));
    }

    void RefreshTransport(double pos)
    {
        bool bars = Timeline.RulerInBars;
        TimeText.Text = bars ? TransportBars(pos, engine.Bpm) : TransportTime(pos);
        // while recording the total is the take so far, like the web
        double total = RecordingBusy ? pos : engine.LengthSec;
        BarText.Text = "/ " + (bars ? TransportBars(total, engine.Bpm) : TransportTime(total));
        string tip = bars ? Loc.T("transport.clock.bars.tip", TransportTime(pos))
            : Loc.T("transport.clock.time.tip", TransportBars(pos, engine.Bpm));
        if (tip != clockTip) { clockTip = tip; ToolTip.SetTip(ClockButton, tip); }
        if (transportPlaying != engine.Playing)
        {
            transportPlaying = engine.Playing;
            PlayIcon.Text = engine.Playing ? "\uF8AE" : "\uF5B0";
            PlayButton.Classes.Set("on", engine.Playing);
            ToolTip.SetTip(PlayButton, Shortcuts.Tip(engine.Playing ? Loc.T("transport.pause.tip") : Loc.T("transport.play.head.tip"), "play"));
        }
        StopButton.IsEnabled = !engine.Recording && (engine.Playing || pos > 0);
        PlayButton.IsEnabled = ToStartButton.IsEnabled = !engine.Recording;
        Tempo.Set(engine.Bpm);
        Tempo.IsEnabled = store != null && !store.ReadOnly && !restoring && !RecordingBusy;
        ShowArm();
        var targets = RecordTargets();
        // nothing armed, nothing to record into: armed tracks are the only way to say where a take goes
        RecButton.IsEnabled = targets.Count > 0;
        string recordTip = Shortcuts.Tip(targets.Count == 0 ? Loc.T("transport.rec.none")
            : targets.Count > 1 ? Loc.T("transport.rec.many", targets.Count, string.Join(", ", targets.Select(t => $"\"{t.Name}\"")))
            : targets[0].Clips.Count > 0 ? Loc.T("transport.rec.replace", targets[0].Name) : Loc.T("transport.rec.one", targets[0].Name), "record");
        if (recordTip != recTip) { recTip = recordTip; ToolTip.SetTip(RecButton, recordTip); }
    }
}
