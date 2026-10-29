using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WildTrack.App.Controls;
using WildTrack.App.Model;
using WildTrack.Audio;
using WildTrack.Audio.Dsp;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// The track's Transponer (native only, asked by the user): its takes play moved by <see cref="Track.Transpose"/>
/// semitones, the same length, so they stay in sync with the video. Moving a take takes seconds
/// (<see cref="WildTrack.Audio.Dsp.PitchShifter"/>), so it runs in the background and is kept in the project's cache
/// (<see cref="TransposeCache"/>); meanwhile the clip keeps playing what it played, and switches with a short
/// crossfade on the audio thread when the new one is ready (<see cref="LiveClip.Sound"/>). A value that is no longer
/// wanted (the slider moved on, the project closed) cancels its work. The export uses the same cache.
/// </summary>
public partial class MainWindow
{
    readonly record struct TransposeKey(string MediaId, double Semis);

    sealed class TransposeJob(string trackId)
    {
        public readonly CancellationTokenSource Cts = new();
        public readonly string TrackId = trackId;
        /// <summary>0 to 1, written by the worker.</summary>
        public volatile float Progress;
        /// <summary>Since it started: one that came from the cache in a blink needs no "ready" toast.</summary>
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    }

    /// <summary>A transpose that took at least this long says so with a toast when it is ready.</summary>
    static readonly TimeSpan TransposeToastAfter = TimeSpan.FromSeconds(1);
    /// <summary>The cache of the open project was cleaned of unused transposes (once per project: the ones tried
    /// meanwhile stay, so going back to a value is immediate).</summary>
    bool transposePruned;

    readonly Dictionary<TransposeKey, ClipSound> transposed = [];
    readonly Dictionary<TransposeKey, TransposeJob> transposing = [];
    readonly HashSet<TransposeKey> transposeFailed = [];
    ProjectStore? transposeStore;
    readonly TextBlock transposeNote = new() { FontSize = 11, Foreground = Palette.Faint, IsVisible = false };
    /// <summary>Refreshes the "Transponiendo… 40%" note while something is being moved.</summary>
    readonly DispatcherTimer transposeClock = new() { Interval = TimeSpan.FromMilliseconds(250) };

    void InitTranspose(StackPanel host)
    {
        host.Children.Add(transposeNote);
        transposeClock.Tick += (_, _) => RefreshTransposeNote();
        inspectorRefresh.Add(RefreshTransposeNote);
        Timeline.TransposeProgress = TransposeProgress;
    }

    /// <summary>The selected track's Transponer, from the inspector.</summary>
    void SetTranspose(double semis) { if (sel != null) SetTranspose(sel, semis); }

    /// <summary>Any track's Transponer (inspector or mixer strip), clamped to what <see cref="PitchShifter"/> does.</summary>
    void SetTranspose(LiveTrack track, double semis)
    {
        semis = Math.Clamp(Math.Round(semis), -PitchShifter.MaxSemitones, PitchShifter.MaxSemitones);
        if (semis == track.Track.Transpose) return;
        EditTrack(track, t => t.Track.Transpose = semis, "transpose");
        RefreshTranspose();
        RefreshMixer();
    }

    /// <summary>How far the track's transpose is (0 to 1), or null when it already plays what it should.</summary>
    float? TransposeProgress(LiveTrack track)
    {
        float sum = 0;
        int n = 0;
        foreach (var job in transposing.Values)
            if (job.TrackId == track.Track.Id) { sum += job.Progress; n++; }
        return n == 0 ? null : sum / n;
    }

    /// <summary>The short form the channel shows (timeline header, mixer strip): "+2", "-5".</summary>
    static string TransposeTag(double v) => $"{v:+0;-0}";

    /// <summary>The tooltip of the channel's tag: how far it is moved, and how far the transpose is while it is made.</summary>
    string TransposeTagTip(LiveTrack t) => TransposeProgress(t) is { } p
        ? Loc.T("insp.transpose.working", $"{p * 100:0}") : Loc.T("mix.transpose.tag.tip", TransposeText(t.Track.Transpose));

    /// <summary>The wide mixer strip's Transponer, beside the reverb and delay sends (asked by the user): − and + a
    /// semitone around the value, lit while the track is moved. There is no ON/OFF: 0 is the original.</summary>
    void TransposePanel(StackPanel fx, LiveTrack t)
    {
        var panel = FxPanel(fx, Loc.T("insp.section.transpose"), FxIcon.Transpose, () => t.Track.Transpose != 0, null, _ => "");
        Button Step(string glyph, int delta, string tip)
        {
            var b = new Button { Classes = { "chip" }, Width = 26, Height = 26, MinHeight = 26, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => SetTranspose(t, t.Track.Transpose + delta);
            mx.Refresh.Add(() => b.IsEnabled = Math.Abs(t.Track.Transpose + delta) <= PitchShifter.MaxSemitones);
            return b;
        }
        var value = new TextBlock { Classes = { "mono" }, FontSize = 14, FontWeight = FontWeight.SemiBold, MinWidth = 30, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        ToolTip.SetTip(value, Loc.T("mix.transpose.reset.tip"));
        value.DoubleTapped += (_, _) => SetTranspose(t, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(Step("", -1, Loc.T("mix.transpose.down.tip")));
        row.Children.Add(value);
        row.Children.Add(Step("", 1, Loc.T("mix.transpose.up.tip")));
        var info = new TextBlock { FontSize = 11, Foreground = Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        void Show()
        {
            double v = t.Track.Transpose;
            value.Text = v == 0 ? "0" : TransposeTag(v);
            info.Text = TransposeProgress(t) is { } p ? Loc.T("mix.transpose.working", $"{p * 100:0}") : TransposeText(v);
        }
        mx.Refresh.Add(Show);
        mx.Transpose.Add(Show);
        panel.Body.Children.Add(row);
        panel.Body.Children.Add(info);
    }

    /// <summary>The compact strip's mark of a transposed track ("+2" in the accent, under its name), hidden at 0.</summary>
    TextBlock TransposeTagText(LiveTrack t)
    {
        var tag = new TextBlock { Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        void Show()
        {
            double v = t.Track.Transpose;
            tag.IsVisible = v != 0;
            tag.Text = TransposeTag(v);
            tag.Foreground = TransposeProgress(t) != null ? Palette.Faint : Palette.Accent;
            ToolTip.SetTip(tag, TransposeTagTip(t));
        }
        mx.Refresh.Add(Show);
        mx.Transpose.Add(Show);
        return tag;
    }

    static string TransposeText(double v) =>
        v == 0 ? Loc.T("insp.transpose.none") : Loc.T(Math.Abs(v) == 1 ? "insp.transpose.one" : "insp.transpose.value", $"{v:+0.##;-0.##}");

    /// <summary>
    /// Points every clip at the sound its track wants: the recording at 0, a transpose already in memory, or keeps
    /// what it plays while the missing one is made. Call it after anything that may change tracks, clips or Transponer.
    /// </summary>
    void RefreshTranspose()
    {
        var st = store;
        if (st != transposeStore)
        {
            foreach (var job in transposing.Values) job.Cts.Cancel();
            transposing.Clear();
            transposed.Clear();
            transposeFailed.Clear();
            transposeStore = st;
            transposePruned = false;
        }
        if (st == null) return;

        var wanted = new HashSet<TransposeKey>();
        foreach (var lt in tracks)
        {
            double semis = lt.Track.Transpose;
            // a MIDI track's notes just play higher or lower: nothing to render
            if (lt.Track.IsMidi) lt.SetMidi(st.Project.Settings.Bpm);
            foreach (var lc in lt.Clips)
            {
                if (lc.Audio is not { } audio || lc.Media is not { } media) continue;
                if (semis == 0)
                {
                    lc.Sound = audio.Sound;
                    lc.SoundSemis = 0;
                    continue;
                }
                var key = new TransposeKey(media.Id, semis);
                wanted.Add(key);
                if (transposed.TryGetValue(key, out var sound))
                {
                    lc.Sound = sound;
                    lc.SoundSemis = semis;
                }
                else if (!transposing.ContainsKey(key) && !transposeFailed.Contains(key) && st.Resolve(media.Audio) is { } path)
                    StartTranspose(st, key, path, audio, lt.Track.Id, string.IsNullOrWhiteSpace(lt.Track.Name) ? media.Name : lt.Track.Name);
            }
        }
        foreach (var (key, job) in transposing.Where(x => !wanted.Contains(x.Key)).ToList())
        {
            job.Cts.Cancel();
            transposing.Remove(key);
        }
        foreach (var key in transposed.Keys.Where(k => !wanted.Contains(k)).ToList()) transposed.Remove(key);
        if (transposing.Count == 0 && !transposePruned)
        {
            transposePruned = true;
            PruneTransposeCache(st);
        }
        transposeClock.IsEnabled = transposing.Count > 0;
        RefreshTransposeNote();
    }

    void StartTranspose(ProjectStore st, TransposeKey key, string path, MediaAudio audio, string trackId, string name)
    {
        var job = new TransposeJob(trackId);
        transposing[key] = job;
        var ct = job.Cts.Token;
        Task.Run(() => TransposeCache.Get(st.CachePath, path, audio.L, audio.R, key.Semis, p => job.Progress = (float)p, ct), ct)
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (!transposing.TryGetValue(key, out var current) || current != job) return; // no longer wanted
                transposing.Remove(key);
                if (t.IsCompletedSuccessfully)
                {
                    transposed[key] = new ClipSound(t.Result.Left, t.Result.Right);
                    // the last of the track's takes is ready: say so, unless it came straight from the cache
                    if (job.Clock.Elapsed >= TransposeToastAfter && !transposing.Values.Any(j => j.TrackId == trackId)
                        && tracks.FirstOrDefault(x => x.Track.Id == trackId) is { } lt && lt.Track.Transpose == key.Semis)
                        Toast(Loc.T("transpose.done", name, TransposeText(key.Semis)));
                }
                else if (!t.IsCanceled)
                {
                    transposeFailed.Add(key);
                    Status(Loc.T("transpose.error", name, t.Exception?.GetBaseException().Message ?? ""));
                }
                RefreshTranspose();
            }), TaskScheduler.Default);
    }

    /// <summary>The selected track's note: how far its transpose is, hidden when it plays what it should. The
    /// channel's tags (timeline header, mixer strip) show the same progress.</summary>
    void RefreshTransposeNote()
    {
        var progress = sel == null ? null : TransposeProgress(sel);
        transposeNote.IsVisible = progress != null;
        if (progress is { } p) transposeNote.Text = Loc.T("insp.transpose.working", $"{p * 100:0}");
        if (mx != null && mixerView && !masteringView) foreach (var r in mx.Transpose) r();
        Timeline.InvalidateVisual();
    }

    /// <summary>Deletes the cached transposes no track uses any more (in the background; a read-only window leaves them).</summary>
    void PruneTransposeCache(ProjectStore st)
    {
        if (st.ReadOnly) return;
        var used = new List<(string Path, int Length, double Semis)>();
        foreach (var lt in tracks)
        {
            if (lt.Track.Transpose == 0) continue;
            foreach (var lc in lt.Clips)
                if (lc.Audio != null && st.Resolve(lc.Media?.Audio) is { } path) used.Add((path, lc.Audio.L.Length, lt.Track.Transpose));
        }
        _ = Task.Run(() => TransposeCache.Prune(st.CachePath, used.Select(u => TransposeCache.PathFor(st.CachePath, u.Path, u.Length, u.Semis)).ToList()));
    }
}
