namespace GuitarCam.Audio;

/// <summary>
/// A stretch of the timeline exported instead of the whole song (native only: the loop A–B). The mix still renders
/// from 0, so what rings from before A (a reverb, a delay) sounds as it does in the song; the clips and notes go quiet
/// at B (<see cref="StopFadeSec"/>) while the effects ring out, like the end of a full export, and the result starts
/// at A with a short fade (<see cref="StartFadeSec"/>) so the cut never clicks. The video gets only the takes that
/// show inside it, trimmed to it and moved so A is 0:00; a panel with no take there goes away.
/// </summary>
public readonly record struct ExportRange(double StartSec, double EndSec)
{
    /// <summary>The fade the export comes in with at A: short enough to keep the attack, long enough not to click.</summary>
    public const double StartFadeSec = 0.01;
    /// <summary>The fade the clips and notes go out with at B, before the effect tails.</summary>
    public const double StopFadeSec = 0.02;
    /// <summary>A shorter stretch is not worth a video (and a loop that short is surely a slip).</summary>
    public const double MinSec = 0.5;

    public double LengthSec => EndSec - StartSec;

    /// <summary>The range inside the song (0..<paramref name="songSec"/>); null when there is none or what's left is under <see cref="MinSec"/>.</summary>
    public static ExportRange? Fit(ExportRange? range, double songSec)
    {
        if (range is not { } r || !double.IsFinite(r.StartSec) || !double.IsFinite(r.EndSec)) return null;
        double a = Math.Max(0, Math.Min(r.StartSec, r.EndSec)), b = Math.Min(Math.Max(r.StartSec, r.EndSec), songSec);
        return b - a >= MinSec ? new ExportRange(a, b) : null;
    }

    /// <summary>
    /// The takes that show between A and B, trimmed to it and moved back by A. A take cut at A or B loses its fade
    /// and its transition on that side (the cut is the range's, not the clip's). The panels left are renumbered in
    /// order, so the layout has exactly as many as the stretch shows.
    /// </summary>
    public static IReadOnlyList<ExportTakeSpec> Takes(IReadOnlyList<ExportTakeSpec> takes, ExportRange r)
    {
        var kept = new List<ExportTakeSpec>();
        var panels = new Dictionary<int, int>();
        for (int i = 0; i < takes.Count; i++)
        {
            var t = takes[i];
            var w = t.Window;
            double start = Math.Max(w.Start, r.StartSec), end = Math.Min(w.End, r.EndSec);
            if (end - start < 1 / 30.0) continue;
            int panel = t.Panel >= 0 ? t.Panel : i;
            if (!panels.TryGetValue(panel, out int p)) panels[panel] = p = panels.Count;
            bool cutIn = start > w.Start + 1e-6, cutOut = end < w.End - 1e-6;
            kept.Add(t with
            {
                AtSec = t.AtSec - r.StartSec,
                TrimStartSec = cutIn ? start - t.AtSec : t.TrimStartSec,
                TrimEndSec = cutOut ? t.AtSec + w.Dur - end : t.TrimEndSec,
                FadeInSec = cutIn ? 0 : t.FadeInSec,
                VideoFadeInSec = cutIn ? 0 : t.VideoFadeInSec,
                FadeOutSec = cutOut ? 0 : t.FadeOutSec,
                VideoFadeOutSec = cutOut ? 0 : t.VideoFadeOutSec,
                Transition = cutIn ? ClipTransitions.CutId : t.Transition,
                Panel = p,
            });
        }
        return kept;
    }

    /// <summary>The mix from <paramref name="fromSec"/> on, coming in with <see cref="StartFadeSec"/> (a copy: the render keeps its arrays).</summary>
    public static (float[] Left, float[] Right) From(float[] left, float[] right, double fromSec, int sampleRate)
    {
        int n = Math.Min(left.Length, right.Length);
        int from = Math.Clamp((int)Math.Round(fromSec * sampleRate), 0, Math.Max(0, n - 1));
        if (from == 0) return (left, right);
        float[] l = left[from..n], r = right[from..n];
        int fade = Math.Min(l.Length, (int)(StartFadeSec * sampleRate));
        for (int i = 0; i < fade; i++)
        {
            float g = i / (float)fade;
            l[i] *= g;
            r[i] *= g;
        }
        return (l, r);
    }

    /// <summary>
    /// Fades a track's sound (clips and notes, before its chain) to silence at <paramref name="stopFrame"/>, over
    /// <see cref="StopFadeSec"/> before it; <paramref name="pos"/> is the frame of the block's first sample.
    /// </summary>
    public static void Stop(Span<float> l, Span<float> r, long pos, long stopFrame, int fadeFrames)
    {
        long fadeStart = stopFrame - fadeFrames;
        for (int i = 0; i < l.Length; i++)
        {
            long s = pos + i;
            if (s < fadeStart) continue;
            float g = s >= stopFrame ? 0 : (stopFrame - s) / (float)fadeFrames;
            l[i] *= g;
            r[i] *= g;
        }
    }
}
