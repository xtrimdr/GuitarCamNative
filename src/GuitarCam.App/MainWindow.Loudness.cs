using Avalonia.Threading;
using GuitarCam.App.Audio;
using GuitarCam.App.Dialogs;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App;

/// <summary>
/// The master's Destino while mixing (asked by the user: the export's loudness, heard before exporting). With a target,
/// every edit (<see cref="MarkDirty"/>) waits <see cref="LoudnessDelay"/> and then measures the whole mix in the
/// background with the export's own <see cref="MixMaster.Render"/>: the gain it finds goes to the master
/// (<see cref="Projects.MasterSettings.TargetGainDb"/>) and playback glides to it, so the mix sounds at the platform's
/// level while you work and the export lands on that same gain. A new edit cancels a measurement under way. It waits
/// while recording and while some audio is still loading or being transposed, so it never reads from disk on the UI thread.
/// </summary>
public partial class MainWindow
{
    /// <summary>A burst of edits (a fader drag) measures once, after it.</summary>
    static readonly TimeSpan LoudnessDelay = TimeSpan.FromSeconds(1.5);

    readonly DispatcherTimer loudTimer = new() { Interval = LoudnessDelay };
    CancellationTokenSource? loudCts;
    /// <summary>Set while the measurement's own result is saved, so saving it does not ask for another one.</summary>
    bool loudApplying;
    bool loudMeasuring, loudFailed;
    /// <summary>What the last measurement landed on (LUFS), for the Volumen panel.</summary>
    double loudLufs = double.NaN;

    void InitLoudness()
    {
        loudTimer.Tick += (_, _) =>
        {
            loudTimer.Stop();
            _ = MeasureLoudness();
        };
    }

    /// <summary>The mix or the Destino changed (or another project opened): measure again in a moment.</summary>
    void LoudnessSoon()
    {
        if (loudApplying) return;
        loudCts?.Cancel();
        loudCts = null;
        loudTimer.Stop();
        loudFailed = false;
        loudMeasuring = store != null && MasterLoudness.Get(MasterSet.Target).Lufs != null;
        if (loudMeasuring) loudTimer.Start();
        else loudLufs = double.NaN;
        RefreshTargetStatus();
    }

    /// <summary>Every clip's audio is in memory as it plays (transpose included): the mix can be built without the disk.</summary>
    bool MixInMemory()
    {
        if (store == null) return false;
        foreach (var trk in store.Project.Tracks)
        {
            var lt = tracks.FirstOrDefault(t => t.Track.Id == trk.Id);
            if (lt == null)
            {
                if (trk.Clips.Count > 0) return false;
                continue;
            }
            foreach (var c in trk.Clips)
            {
                var lc = lt.Clips.FirstOrDefault(x => x.Clip.Id == c.Id);
                if (lc == null) return false;
                if (!lc.Offline && (lc.Sound == null || lc.SoundSemis != trk.Transpose)) return false;
            }
        }
        return true;
    }

    async Task MeasureLoudness()
    {
        var st = store;
        if (st == null) return;
        var target = MasterLoudness.Get(st.Project.Settings.Master.Target);
        if (target.Lufs == null) return;
        if (RecordingBusy || !MixInMemory())
        {
            loudTimer.Start(); // a take or a load under way: try again in a moment
            return;
        }
        var (mixTracks, bus) = ExportDialog.BuildMix(st, tracks);
        double content = Math.Max(1, MixRenderer.MixDurationSec(mixTracks, PlaybackEngine.SampleRate));
        var cts = loudCts = new CancellationTokenSource();
        loudMeasuring = true;
        RefreshTargetStatus();
        try
        {
            var mix = await Task.Run(() => MixMaster.Render(mixTracks, bus, content, PlaybackEngine.SampleRate, MixMaster.MaxTailSec,
                target.Lufs, null, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || store != st) return;
            loudLufs = mix.Lufs;
            var m = st.Project.Settings.Master;
            double gain = Math.Round(mix.GainDb, 2);
            if (m.Target == target.Id && Math.Abs(m.TargetGainDb - gain) >= 0.01)
            {
                m.TargetGainDb = gain;
                engine.ApplyMaster(m);
                loudApplying = true;
                try { MarkDirty(); }
                finally { loudApplying = false; }
                RefreshMasterPanel();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (loudCts == cts) loudFailed = true;
        }
        finally
        {
            if (loudCts == cts)
            {
                loudCts = null;
                loudMeasuring = false;
            }
            cts.Dispose();
            RefreshTargetStatus();
        }
    }
}
