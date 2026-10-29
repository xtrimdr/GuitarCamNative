namespace WildTrack.Audio.Dsp;

/// <summary>
/// The RMS of one mixer meter (a channel, a return, the master). The audio thread adds each block's sum of squares
/// with <see cref="Add"/> into running totals that only grow; the UI reads them on its tick with <see cref="Take"/>,
/// keeps what it saw last and smooths each side's mean square over <see cref="IntegrationSec"/> (a stereo meter shows
/// both, a mono one the louder). Nobody resets
/// the totals, so the UI never races the audio thread into losing or counting a block twice; a torn read between the
/// sums and the count is at most one block off and the smoothing hides it. Nothing here allocates or locks.
/// </summary>
public sealed class RmsTap
{
    /// <summary>How long the meter integrates (s): the classic VU-like ~300 ms to settle on a new level.</summary>
    public const double IntegrationSec = 0.3;
    /// <summary>The one-pole's time constant: three of them make the 300 ms (95 % of the way), so it falls ~43 dB/s.</summary>
    const double Tau = IntegrationSec / 3;

    // written by the audio thread only
    double sumL, sumR;
    long count;

    // the UI's side
    double seenL, seenR;
    long seenCount;
    double meanSqL, meanSqR;

    /// <summary>Audio thread: a block's sums of squares (left and right) over <paramref name="n"/> frames.</summary>
    public void Add(double sqL, double sqR, int n)
    {
        sumL += sqL;
        sumR += sqR;
        Volatile.Write(ref count, count + n); // after the sums, so a reader that sees the count sees them too
    }

    /// <summary>Audio thread: adds a stereo block (the first <paramref name="n"/> samples of each channel).</summary>
    public void Add(float[] l, float[] r, int n) => Add(SumSquares(l, n), SumSquares(r, n), n);

    /// <summary>UI: what the last <see cref="Take"/> returned (a folder's meter reads its tracks' without taking again).</summary>
    public double MeanSq => Math.Max(meanSqL, meanSqR);
    /// <summary>UI: each side's smoothed mean square as of the last <see cref="Take"/>.</summary>
    public double MeanSqL => meanSqL;
    public double MeanSqR => meanSqR;

    /// <summary>
    /// UI: the mean square (linear power, the louder side's; each side's in <see cref="MeanSqL"/> / <see cref="MeanSqR"/>) smoothed over <see cref="IntegrationSec"/>, after
    /// <paramref name="dtSec"/> since the last call. With no new audio in between it decays as silence would.
    /// </summary>
    public double Take(double dtSec)
    {
        long c = Volatile.Read(ref count);
        double l = sumL, r = sumR;
        long n = c - seenCount;
        double msL = n > 0 ? Math.Max(l - seenL, 0) / n : 0, msR = n > 0 ? Math.Max(r - seenR, 0) / n : 0;
        seenL = l; seenR = r; seenCount = c;
        meanSqL = Smooth(meanSqL, msL, dtSec);
        meanSqR = Smooth(meanSqR, msR, dtSec);
        return MeanSq;
    }

    /// <summary>One step of the one-pole smoothing of a mean square toward <paramref name="target"/>.</summary>
    public static double Smooth(double current, double target, double dtSec)
    {
        double a = 1 - Math.Exp(-Math.Max(dtSec, 0) / Tau);
        return current + (target - current) * a;
    }

    /// <summary>Sum of the squares of the first <paramref name="n"/> samples.</summary>
    public static double SumSquares(float[] x, int n)
    {
        float s = 0;
        for (int i = 0; i < n; i++) s += x[i] * x[i];
        return s;
    }

    /// <summary>A mean square in dBFS (a full-scale sine reads −3 dB); −∞ for silence.</summary>
    public static double Db(double meanSq) => meanSq > 1e-12 ? 10 * Math.Log10(meanSq) : double.NegativeInfinity;
}
