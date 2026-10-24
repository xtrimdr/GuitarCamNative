using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GuitarCam.App.Controls;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The tuner and the loudness readout of the transport bar (native only, the web has neither). The tuner listens
/// to the first armed input, or 1-2 (<see cref="Audio.PlaybackEngine.Pitch"/>): its cell shows the note and a panel
/// over it the needle, the turn arrows and the sensitivity (the level gate, saved in config.json as
/// <see cref="TunerGateKey"/>); it runs YIN only while it is on. A click outside the panel closes it (light dismiss),
/// like A and the cell. The LUFS cell shows the master's integrated loudness of the
/// current pass (it starts over with every play and with a click), with momentary and short-term in its tooltip.
/// Texts change only when the shown value does, so the tick allocates nothing while they hold.
/// </summary>
public partial class MainWindow
{
    /// <summary>Integrated, short-term and momentary in tenths of LU, as last shown; int.MinValue = "—".</summary>
    (int I, int S, int M) lufsShown = (0, 0, 0);
    bool lufsWasPlaying;
    string tunerShown = "";
    bool tunerInTuneShown;
    const string TunerGateKey = "tuner.gateDb";

    void InitMeters()
    {
        double gate = double.TryParse(AppConfig.Get(TunerGateKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double g) ? g : PitchTap.DefaultGateDb;
        engine.Pitch.GateDb = gate;
        // the slider runs on −gate, so right is more sensitive; it reads as the level it listens down to
        InspectorSlider? sensitivity = null;
        sensitivity = new InspectorSlider(Loc.T("tuner.sensitivity"), -PitchTap.MaxGateDb, -PitchTap.MinGateDb, 1, -PitchTap.DefaultGateDb,
            v => $"{-v:0} dB", v =>
            {
                engine.Pitch.GateDb = -v;
                ShowTunerSensitivity(sensitivity!);
            }, Loc.T("tuner.sensitivity.tip"));
        TunerBody.Children.Add(sensitivity);
        ShowTunerSensitivity(sensitivity);

        TunerToggle.IsCheckedChanged += (_, _) =>
        {
            bool on = TunerToggle.IsChecked == true;
            if (on) Tuner.Restart();
            TunerPopup.IsOpen = on;
            if (!on) SetTunerNote("♪", false);
        };
        TunerPopup.Closed += (_, _) =>
        {
            TunerToggle.IsChecked = false;
            try { AppConfig.Set(TunerGateKey, engine.Pitch.GateDb.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        };
        // with the slider focused the keys land in the popup: shortcuts (A to close, Space…) still work
        TunerPanel.AddHandler(KeyDownEvent, (s, e) => { if (!e.Handled) OnKey(s, e); }, RoutingStrategies.Bubble);
        TunerPanel.AddHandler(KeyUpEvent, (_, e) => transportKeys.Remove(e.Key), RoutingStrategies.Tunnel);
        LufsCell.Click += (_, _) =>
        {
            engine.Loudness.Reset();
            ShowLufs(force: true);
        };
        ShowLufs(force: true);
    }

    void TickMeters()
    {
        if (TunerToggle.IsChecked == true)
        {
            var r = engine.IsOpen ? engine.Pitch.Read() : null;
            Tuner.Push(r);
            SetTunerNote(r is { } v ? TunerView.NoteName(v.PitchClass) : "—", r != null && Tuner.InTune);
        }
        bool playing = engine.Playing || RecordingTake;
        if (playing && !lufsWasPlaying) engine.Loudness.Reset(); // a new pass of the song
        lufsWasPlaying = playing;
        if (engine.Loudness.Poll()) ShowLufs();
    }

    void SetTunerNote(string note, bool inTune)
    {
        if (note == tunerShown && inTune == tunerInTuneShown) return;
        tunerShown = note;
        tunerInTuneShown = inTune;
        TunerNote.Text = note;
        if (inTune) TunerNote.Foreground = Palette.InTune;
        else TunerNote.ClearValue(TextBlock.ForegroundProperty);
    }

    void ShowTunerSensitivity(InspectorSlider s) => s.Set(-engine.Pitch.GateDb);

    static int Tenths(double lufs) => double.IsFinite(lufs) ? (int)Math.Round(lufs * 10) : int.MinValue;

    void ShowLufs(bool force = false)
    {
        var l = engine.Loudness;
        var now = (Tenths(l.Integrated), Tenths(l.ShortTerm), Tenths(l.Momentary));
        if (!force && now == lufsShown) return;
        bool integratedChanged = force || now.Item1 != lufsShown.I;
        lufsShown = now;
        if (integratedChanged) LufsText.Text = Value(l.Integrated);
        ToolTip.SetTip(LufsCell, Loc.T("transport.lufs.tip", Value(l.Integrated), Value(l.ShortTerm), Value(l.Momentary)));

        static string Value(double v) => double.IsFinite(v) ? $"{v:0.0}" : "—";
    }
}
