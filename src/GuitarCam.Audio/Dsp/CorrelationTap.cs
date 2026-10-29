namespace GuitarCam.Audio.Dsp;

/// <summary>
/// The master's phase correlation (the mixer's COR, beside MONO): +1 = the same on both sides (mono), 0 = unrelated
/// (very wide), −1 = one side is the other upside down, and what cancels in mono. Built like <see cref="RmsTap"/>: the
/// audio thread adds each block's sums (L², R², L·R) to totals that only grow, the UI reads them on its tick and smooths
/// them over <see cref="IntegrationSec"/>, slower than the meters so the number can be read. Nothing allocates or locks.
/// </summary>
public sealed class CorrelationTap
{
    /// <summary>How long it integrates (s): long enough to see a trend, short enough to follow a part that changes.</summary>
    public const double IntegrationSec = 0.6;
    const double Tau = IntegrationSec / 3;
    /// <summary>Under this mean square (−70 dBFS) there is nothing to judge: <see cref="Value"/> reads NaN.</summary>
    const double Floor = 1e-7;

    // written by the audio thread only
    double sumLL, sumRR, sumLR;
    long count;

    // the UI's side
    double seenLL, seenRR, seenLR;
    long seenCount;
    double ll, rr, lr;

    /// <summary>Audio thread: the first <paramref name="n"/> samples of a stereo block.</summary>
    public void Add(ReadOnlySpan<float> l, ReadOnlySpan<float> r, int n)
    {
        float a = 0, b = 0, c = 0;
        for (int i = 0; i < n; i++)
        {
            a += l[i] * l[i];
            b += r[i] * r[i];
            c += l[i] * r[i];
        }
        sumLL += a;
        sumRR += b;
        sumLR += c;
        Volatile.Write(ref count, count + n); // after the sums, as in RmsTap
    }

    /// <summary>UI: the smoothed correlation after <paramref name="dtSec"/> since the last call (−1..+1; NaN in silence).</summary>
    public double Take(double dtSec)
    {
        long c = Volatile.Read(ref count);
        double a = sumLL, b = sumRR, x = sumLR;
        long n = c - seenCount;
        double mLL = n > 0 ? Math.Max(a - seenLL, 0) / n : 0, mRR = n > 0 ? Math.Max(b - seenRR, 0) / n : 0, mLR = n > 0 ? (x - seenLR) / n : 0;
        seenLL = a; seenRR = b; seenLR = x; seenCount = c;
        double k = 1 - Math.Exp(-Math.Max(dtSec, 0) / Tau);
        ll += (mLL - ll) * k;
        rr += (mRR - rr) * k;
        lr += (mLR - lr) * k;
        return Value;
    }

    /// <summary>UI: what the last <see cref="Take"/> returned.</summary>
    public double Value => ll < Floor || rr < Floor ? double.NaN : Math.Clamp(lr / Math.Sqrt(ll * rr), -1, 1);
}
