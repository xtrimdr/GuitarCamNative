using WildTrack.Audio.Dsp;

namespace WildTrack.Audio;

/// <summary>How one clip goes into the next where they touch on a track (native only).</summary>
public enum TransitionKind { Cut, Crossfade, Dip }

/// <summary>
/// Transitions between two clips of a track (native only, asked by the user). Stored on the clip that comes in and
/// centered on the join: half before it, half after. The picture: a crossfade shows the clip going out past its end
/// and the one coming in before its start, using the video the trims hide (where there is none, that side holds its
/// last or first frame); a dip goes to the background at the join and comes back, so it needs nothing hidden. The
/// sound (asked by the user later: a bare cut was too abrupt): both kinds crossfade the audio over the same stretch
/// with the audio the trims hide (<see cref="Audio"/>), on top of the clips' own fades, which they never change. The
/// preview and playback (<c>LiveTrack</c>), the mix (<see cref="MixRenderer"/>) and the video export
/// (<see cref="Mp4Exporter"/>) all find the joins and weigh the clips here, so they show and sound the same.
/// </summary>
public static class ClipTransitions
{
    /// <summary>The stored ids (<c>Clip.Transition</c>).</summary>
    public const string CutId = "cut", CrossfadeId = "crossfade", DipId = "dip";
    public const double DefaultSec = 0.5, MinSec = 0.1, MaxSec = 3;
    /// <summary>The menu's quick lengths; the slider gives anything from <see cref="MinSec"/> to <see cref="MaxSec"/>.</summary>
    public static readonly double[] QuickSec = [0.25, 0.5, 1, 2];
    /// <summary>
    /// Two clips whose edges are closer than this touch (two frames at 30 fps): a split or a snap leaves them
    /// exactly together, a hand placement within a frame or two still counts.
    /// </summary>
    public const double JoinSec = 2 / 30.0;

    public static TransitionKind Parse(string? id) => id switch
    {
        CrossfadeId => TransitionKind.Crossfade,
        DipId => TransitionKind.Dip,
        _ => TransitionKind.Cut,
    };

    public static string Id(TransitionKind kind) => kind switch
    {
        TransitionKind.Crossfade => CrossfadeId,
        TransitionKind.Dip => DipId,
        _ => CutId,
    };

    /// <summary>A stored length brought into range (a broken or missing one is the default).</summary>
    public static double ClampSec(double sec) => double.IsFinite(sec) ? Math.Clamp(sec, MinSec, MaxSec) : DefaultSec;

    /// <summary>
    /// A transition in place: clip <see cref="Out"/> ends and clip <see cref="In"/> starts at <see cref="At"/>
    /// (indexes into the windows it was found in), over <see cref="Sec"/> centered on it.
    /// </summary>
    public readonly record struct Join(int Out, int In, TransitionKind Kind, double At, double Sec)
    {
        public double Start => At - Sec / 2;
        public double End => At + Sec / 2;
        /// <summary>How far the picture of each clip shows past its edge: half the length on a crossfade, nothing on a dip.</summary>
        public double Overhang => Kind == TransitionKind.Crossfade ? Sec / 2 : 0;
    }

    /// <summary>The clip that ends where clip <paramref name="i"/> starts (the closest within <see cref="JoinSec"/>), or -1.</summary>
    public static int FindOut(ReadOnlySpan<ClipEnvelope.ClipWindow> w, int i)
    {
        int best = -1;
        double bestGap = JoinSec;
        var to = w[i];
        if (to.End - to.Start <= 0) return -1;
        for (int j = 0; j < w.Length; j++)
        {
            if (j == i) continue;
            var from = w[j];
            if (from.End - from.Start <= 0 || from.Start >= to.Start - JoinSec) continue;
            double gap = Math.Abs(from.End - to.Start);
            if (gap <= bestGap)
            {
                best = j;
                bestGap = gap;
            }
        }
        return best;
    }

    /// <summary>
    /// The transition into clip <paramref name="i"/>, or null when it is a cut or doesn't touch another clip. Its
    /// length is at most as long as each clip, so half of it always fits inside both.
    /// </summary>
    public static Join? Find(ReadOnlySpan<ClipEnvelope.ClipWindow> w, int i, TransitionKind kind, double sec)
    {
        if (kind == TransitionKind.Cut) return null;
        int o = FindOut(w, i);
        if (o < 0) return null;
        double len = Math.Min(ClampSec(sec), Math.Min(w[o].End - w[o].Start, w[i].End - w[i].Start));
        if (len < 1 / 30.0) return null;
        return new Join(o, i, kind, w[i].Start, len);
    }

    /// <summary>
    /// The opacity of the clip going out and of the one coming in at <paramref name="t"/>, on top of their own
    /// video fades. Crossfade: the one going out stays whole and the one coming in rises over it (the export may
    /// stack them the other way and fade the one going out instead: on an opaque picture it's the same mix). Dip:
    /// the one going out sinks to the background up to the join and the one coming in rises after it.
    /// </summary>
    public static (double Out, double In) Weights(Join j, double t)
    {
        double p = Math.Clamp((t - j.Start) / j.Sec, 0, 1);
        if (j.Kind == TransitionKind.Crossfade) return (1, p);
        return (Math.Clamp(1 - 2 * p, 0, 1), Math.Clamp(2 * p - 1, 0, 1));
    }

    /// <summary>
    /// Where neither clip has audio hidden past the join, the sound still doesn't click: the one going out fades in
    /// this long before it and the one coming in fades in this long after it (the engine's 10 ms of a jump).
    /// </summary>
    public const double ShortSec = 0.01;

    /// <summary>
    /// The sound of a transition: the stretch where both clips sound, inside the picture's, as far as the audio the
    /// trims hide goes on each side. Two pieces of the same take that were one (same media, same place) crossfade
    /// in straight lines, which add up to exactly the take; anything else at equal power, so the level doesn't sag.
    /// </summary>
    public static AudioJoin Audio(Join j, ClipEnvelope.ClipWindow from, ClipEnvelope.ClipWindow to, bool sameTake)
    {
        double half = j.Sec / 2;
        double after = Math.Max(0, from.Origin + from.Dur - from.End), before = Math.Max(0, to.Start - to.Origin);
        double s = j.At - Math.Min(half, before), e = j.At + Math.Min(half, after);
        if (e - s < 2 * ShortSec) return new AudioJoin(j.At - ShortSec, j.At, j.At + ShortSec, false, true);
        return new AudioJoin(s, j.At, e, true, sameTake && Math.Abs(from.Origin - to.Origin) < 1e-6);
    }

    /// <summary>
    /// The sound of every transition of a track's clips (<paramref name="w"/>, with each clip's stored kind and
    /// length and an id of its take, equal for clips of the same media): <paramref name="ins"/>[i] / <paramref name="outs"/>[i]
    /// get the join clip i comes in or goes out through (default = none). Allocates nothing.
    /// </summary>
    public static void AudioJoins(ReadOnlySpan<ClipEnvelope.ClipWindow> w, ReadOnlySpan<TransitionKind> kinds, ReadOnlySpan<double> secs,
        ReadOnlySpan<int> takes, Span<AudioJoin> ins, Span<AudioJoin> outs)
    {
        ins[..w.Length].Clear();
        outs[..w.Length].Clear();
        for (int i = 0; i < w.Length; i++)
        {
            if (Find(w, i, kinds[i], secs[i]) is not { } j) continue;
            var a = Audio(j, w[j.Out], w[i], takes[j.Out] == takes[i]);
            ins[i] = a;
            outs[j.Out] = a;
        }
    }
}

/// <summary>
/// One transition as the sound hears it (<see cref="ClipTransitions.Audio"/>): with <see cref="Overlap"/>, both clips
/// sound from <see cref="Start"/> to <see cref="End"/> and cross there; without it (no hidden audio on either side)
/// the one going out fades out up to <see cref="At"/> and the other fades in after it. Default = no transition.
/// </summary>
public readonly record struct AudioJoin(double Start, double At, double End, bool Overlap, bool Linear)
{
    public bool Exists => End > Start;

    /// <summary>The level of the clip going out at <paramref name="t"/> (1 before the transition, 0 after).</summary>
    public double OutGain(double t)
    {
        if (t <= Start) return 1;
        if (!Overlap) return t >= At ? 0 : (At - t) / (At - Start);
        if (t >= End) return 0;
        double p = (t - Start) / (End - Start);
        return Linear ? 1 - p : Math.Cos(p * Math.PI / 2);
    }

    /// <summary>The level of the clip coming in at <paramref name="t"/> (0 before the transition, 1 after).</summary>
    public double InGain(double t)
    {
        if (t >= End) return 1;
        if (!Overlap) return t <= At ? 0 : (t - At) / (End - At);
        if (t <= Start) return 0;
        double p = (t - Start) / (End - Start);
        return Linear ? p : Math.Sin(p * Math.PI / 2);
    }
}

/// <summary>
/// What the transitions do to one clip's sound: the join it comes in through and the one it goes out through
/// (<see cref="AudioJoin.Exists"/> false = none). Never changed once built, so the audio thread can hold it; the
/// clip sounds from <see cref="From"/> to <see cref="Until"/> (past its edges where the join overlaps) at <see cref="Gain"/>.
/// </summary>
public sealed class ClipAudioJoins(AudioJoin @in, AudioJoin @out)
{
    public readonly AudioJoin In = @in, Out = @out;

    public double From(double start) => In.Exists && In.Overlap ? Math.Min(start, In.Start) : start;
    public double Until(double end) => Out.Exists && Out.Overlap ? Math.Max(end, Out.End) : end;
    public double Gain(double t) => (In.Exists ? In.InGain(t) : 1) * (Out.Exists ? Out.OutGain(t) : 1);

    /// <summary>The joins of every clip (null where it has none): built once, for the mix render.</summary>
    public static ClipAudioJoins?[] For(ReadOnlySpan<ClipEnvelope.ClipWindow> w, ReadOnlySpan<TransitionKind> kinds, ReadOnlySpan<double> secs, ReadOnlySpan<int> takes)
    {
        var ins = new AudioJoin[w.Length];
        var outs = new AudioJoin[w.Length];
        ClipTransitions.AudioJoins(w, kinds, secs, takes, ins, outs);
        var list = new ClipAudioJoins?[w.Length];
        for (int i = 0; i < w.Length; i++)
            if (ins[i].Exists || outs[i].Exists) list[i] = new ClipAudioJoins(ins[i], outs[i]);
        return list;
    }
}
