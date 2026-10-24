namespace GuitarCam.Audio.Dsp;

/// <summary>
/// A clip's volume curve (native only, the web has none): points in dB on top of the clip gain, timed from the
/// media's sample 0 so they stay on the same notes when the clip is moved, trimmed or split. Between points the
/// level goes in a straight line in dB; before the first and after the last it holds. At <see cref="MinDb"/> it is
/// silence. Playback and export sample it every <see cref="Step"/> samples and ramp in between, so a block of any
/// size costs a few evaluations and never jumps.
/// </summary>
public static class ClipGainCurve
{
    public readonly record struct Point(double Sec, double Db);

    /// <summary>The bottom of the lane: silence.</summary>
    public const double MinDb = -36;
    /// <summary>The top of the lane, the most a point can add.</summary>
    public const double MaxDb = 12;
    /// <summary>Samples between evaluations (1,3 ms at 48 kHz).</summary>
    public const int Step = 64;

    /// <summary>The curve's level at a time in the media, in dB (0 with no points).</summary>
    public static double DbAt(ReadOnlySpan<Point> pts, double sec)
    {
        if (pts.Length == 0) return 0;
        if (sec <= pts[0].Sec) return pts[0].Db;
        for (int i = 1; i < pts.Length; i++)
        {
            if (sec > pts[i].Sec) continue;
            var (a, b) = (pts[i - 1], pts[i]);
            double span = b.Sec - a.Sec;
            return span <= 0 ? b.Db : a.Db + (b.Db - a.Db) * (sec - a.Sec) / span;
        }
        return pts[^1].Db;
    }

    /// <summary>Linear gain for a level in dB, 0 at <see cref="MinDb"/> or below.</summary>
    public static double Gain(double db) => db <= MinDb ? 0 : Math.Pow(10, db / 20);

    public static double GainAt(ReadOnlySpan<Point> pts, double sec) => pts.Length == 0 ? 1 : Gain(DbAt(pts, sec));
}
