using Avalonia.Controls;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App;

/// <summary>
/// The tuner and the loudness readout of the transport bar (native only, the web has neither). The tuner listens
/// to the first armed input, or 1-2 (<see cref="Audio.PlaybackEngine.Pitch"/>): its cell shows the note and a panel
/// over it the needle; it runs YIN only while it is on. The LUFS cell shows the master's integrated loudness of the
/// current pass (it starts over with every play and with a click), with momentary and short-term in its tooltip.
/// Texts change only when the shown value does, so the tick allocates nothing while they hold.
/// </summary>
public partial class MainWindow
{
    /// <summary>Integrated, short-term and momentary in tenths of LU, as last shown; int.MinValue = "—".</summary>
    (int I, int S, int M) lufsShown = (0, 0, 0);
    bool lufsWasPlaying;
    string tunerShown = "";

    void InitMeters()
    {
        TunerToggle.IsCheckedChanged += (_, _) =>
        {
            bool on = TunerToggle.IsChecked == true;
            TunerPopup.IsOpen = on;
            if (!on) SetTunerNote("♪");
        };
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
            SetTunerNote(r is { } v ? v.Note : "—");
        }
        bool playing = engine.Playing || RecordingTake;
        if (playing && !lufsWasPlaying) engine.Loudness.Reset(); // a new pass of the song
        lufsWasPlaying = playing;
        if (engine.Loudness.Poll()) ShowLufs();
    }

    void SetTunerNote(string note)
    {
        if (note == tunerShown) return;
        tunerShown = note;
        TunerNote.Text = note;
    }

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
