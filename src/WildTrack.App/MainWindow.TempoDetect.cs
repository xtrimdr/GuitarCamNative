using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using WildTrack.App.Model;
using WildTrack.Audio;

namespace WildTrack.App;

/// <summary>
/// "Detectar tempo" on an audio clip (asked by the user): <see cref="TempoDetector"/> listens to the part of the take
/// the clip plays, on a worker thread, and a menu opens where the clip menu was with what it heard and the choice to
/// put the project at that tempo, its double or its half (the same pulse: the detector can't tell 87 from 174), or to
/// leave it. While it listens the clip shows how far it is, like a transpose (one clip at a time). The tempo has no undo step (<see cref="SetTempo"/>), so the toast that confirms it can put the old one back.
/// </summary>
public partial class MainWindow
{
    /// <summary>A detected tempo this close to a whole number is that number: loops and samples are made at whole BPM.</summary>
    const double WholeBpmSnap = 0.15;
    /// <summary>The tempos the transport takes (<see cref="Controls.TempoControl"/>).</summary>
    const double TempoMin = 40, TempoMax = 300;

    /// <summary>The clip being listened to and how far it is (0 to 1, written by the worker); null when idle.</summary>
    sealed class TempoJob(LiveClip clip)
    {
        public readonly LiveClip Clip = clip;
        public volatile float Progress;
    }
    TempoJob? tempoJob;
    /// <summary>Redraws the clip's bar while it listens.</summary>
    DispatcherTimer? tempoClock;

    MenuItem DetectTempoItem(LiveClip clip, Point point, bool enabled) =>
        MenuAction(Loc.T("tempo.detect") + "…", () => DetectTempo(clip, point), enabled: enabled && clip.Audio != null && !RecordingBusy && tempoJob == null,
            icon: Controls.TimelineView.TempoGlyph,
            tip: Loc.T(clip.Audio == null ? "tempo.detect.offline" : tempoJob != null ? "tempo.detect.busy" : "tempo.detect.tip"));

    async Task DetectTempo(LiveClip clip, Point point)
    {
        if (clip.Audio is not { } audio) return;
        var w = clip.Window;
        int sr = audio.SampleRate;
        int start = (int)Math.Max(0, (w.Start - w.Origin) * sr);
        int count = (int)Math.Max(0, (w.End - w.Start) * sr);
        string name = clip.Name;
        var st = store;
        var job = tempoJob = new TempoJob(clip);
        Timeline.TempoProgress ??= c => tempoJob is { } j && j.Clip == c ? j.Progress : null;
        if (tempoClock == null)
        {
            tempoClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            tempoClock.Tick += (_, _) => Timeline.InvalidateVisual();
        }
        tempoClock.Start();
        Timeline.InvalidateVisual();
        TempoDetector.Result? r;
        try
        {
            r = await Task.Run(() => TempoDetector.Detect(audio.L, audio.R, sr, start, count, p => job.Progress = (float)p));
        }
        finally
        {
            tempoJob = null;
            tempoClock.Stop();
            Timeline.InvalidateVisual();
        }
        // another project opened meanwhile: what it heard isn't about this one
        if (store == null || store != st || store.ReadOnly || restoring) return;
        if (r == null || r.Confidence < TempoDetector.MinConfidence)
        {
            Toast(Loc.T(count < TempoDetector.MinSec * sr ? "tempo.detect.short" : "tempo.detect.none", name, TempoDetector.MinSec));
            return;
        }
        ShowTempoMenu(r, name, point);
    }

    void ShowTempoMenu(TempoDetector.Result r, string name, Point point)
    {
        double bpm = TempoValue(r.Bpm), now = store!.Project.Settings.Bpm;
        string sure = Loc.T(r.Confidence >= 0.6 ? "tempo.detect.sure" : r.Confidence >= 0.35 ? "tempo.detect.likely" : "tempo.detect.doubt");
        var list = new List<object>
        {
            new MenuItem { Header = Loc.T("tempo.detect.found", name, Bpm(bpm)), IsEnabled = false, FontSize = 11 },
            new MenuItem { IsEnabled = false, Header = new TextBlock { Text = sure, FontSize = 10.5, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 232 } },
        };
        void Offer(double value, string key)
        {
            if (value < TempoMin || value > TempoMax) return;
            list.Add(MenuAction(Loc.T(key, Bpm(value)), () => { ApplyDetectedTempo(value, now); return Task.CompletedTask; },
                enabled: !RecordingBusy && value != now, icon: key == "tempo.detect.use" ? "" : null));
        }
        Offer(bpm, "tempo.detect.use");
        Offer(TempoValue(bpm * 2), "tempo.detect.double");
        Offer(TempoValue(bpm / 2), "tempo.detect.half");
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("tempo.detect.keep", Bpm(now)), () => Task.CompletedTask));
        OpenTimelineMenu(Popup(list, 260), point, MenuHeight(list));
    }

    void ApplyDetectedTempo(double value, double before)
    {
        var st = store;
        SetTempo(value);
        // the button only goes back in the project it changed
        Toast(Loc.T("tempo.detect.done", Bpm(value)), action: (Loc.T("tempo.detect.back", Bpm(before)), () => { if (store == st) SetTempo(before); }));
    }

    /// <summary>The tempo as the transport takes it: in tenths, or whole when it is that close.</summary>
    static double TempoValue(double bpm)
    {
        double whole = Math.Round(bpm);
        return Math.Abs(bpm - whole) <= WholeBpmSnap ? whole : Math.Round(bpm, 1, MidpointRounding.AwayFromZero);
    }

    static string Bpm(double bpm) => bpm.ToString("0.#", CultureInfo.CurrentCulture);
}
