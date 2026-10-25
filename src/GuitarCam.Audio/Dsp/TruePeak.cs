namespace GuitarCam.Audio.Dsp;

/// <summary>
/// True peak (ITU-R BS.1770-4, Annex 2), native only: the level between samples, where a DAC or an AAC encoder
/// overshoots even when no sample reaches the ceiling. Oversamples ×4 with the standard's 48-tap polyphase filter
/// (4 phases of 12 taps) and keeps the largest of the four values; the answer is <see cref="Delay"/> samples late.
/// The audio thread uses it two ways: <see cref="Next"/> inside the <see cref="MasterLimiter"/> as its detector, and
/// <see cref="Process"/> on the master out for the Volumen panel, read with <see cref="TakeMax"/>. No allocation, no locks.
/// </summary>
public sealed class TruePeak
{
    const int Taps = 12, Phases = 4;
    /// <summary>Samples between the newest input and the stretch the answer describes: the filter's centre tap.</summary>
    public const int Delay = 6;

    /// <summary>The standard's coefficients, phase by phase.</summary>
    static readonly double[] H =
    [
        0.0017089843750, 0.0109863281250, -0.0196533203125, 0.0332031250000, -0.0594482421875, 0.1373291015625,
        0.9721679687500, -0.1022949218750, 0.0476074218750, -0.0266113281250, 0.0148925781250, -0.0083007812500,
        -0.0291748046875, 0.0292968750000, -0.0517578125000, 0.0891113281250, -0.1665039062500, 0.4650878906250,
        0.7797851562500, -0.2003173828125, 0.1015625000000, -0.0582275390625, 0.0330810546875, -0.0189208984375,
        -0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625000000, -0.2003173828125, 0.7797851562500,
        0.4650878906250, -0.1665039062500, 0.0891113281250, -0.0517578125000, 0.0292968750000, -0.0291748046875,
        -0.0083007812500, 0.0148925781250, -0.0266113281250, 0.0476074218750, -0.1022949218750, 0.9721679687500,
        0.1373291015625, -0.0594482421875, 0.0332031250000, -0.0196533203125, 0.0109863281250, 0.0017089843750,
    ];

    // history written twice, so the newest Taps samples are always contiguous from pos
    readonly double[] hl = new double[Taps * 2], hr = new double[Taps * 2];
    int pos;
    /// <summary>Highest true peak since the UI last read it (linear); written once per block.</summary>
    float max;

    public static double ToDb(double linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;

    public void Reset()
    {
        Array.Clear(hl);
        Array.Clear(hr);
        pos = 0;
        max = 0;
    }

    /// <summary>Takes one stereo sample and returns the true peak (linear, both channels) <see cref="Delay"/> samples back.</summary>
    public double Next(float l, float r)
    {
        pos = pos == 0 ? Taps - 1 : pos - 1;
        hl[pos] = hl[pos + Taps] = l;
        hr[pos] = hr[pos + Taps] = r;
        double pk = 0;
        for (int p = 0; p < Phases; p++)
        {
            double yl = 0, yr = 0;
            int h = p * Taps;
            for (int j = 0; j < Taps; j++)
            {
                yl += H[h + j] * hl[pos + j];
                yr += H[h + j] * hr[pos + j];
            }
            if (yl < 0) yl = -yl;
            if (yr < 0) yr = -yr;
            if (yl > pk) pk = yl;
            if (yr > pk) pk = yr;
        }
        // the sample itself, so it never reads under the sample peak
        double sl = Math.Abs(hl[pos + Delay]), sr = Math.Abs(hr[pos + Delay]);
        return Math.Max(pk, Math.Max(sl, sr));
    }

    /// <summary>Audio thread: measures a block for <see cref="TakeMax"/>.</summary>
    public void Process(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        double pk = 0;
        for (int i = 0; i < l.Length; i++)
        {
            double v = Next(l[i], r[i]);
            if (v > pk) pk = v;
        }
        if (pk > max) max = (float)pk;
    }

    /// <summary>UI thread: the highest true peak since the last call (linear).</summary>
    public float TakeMax() => Interlocked.Exchange(ref max, 0f);

    /// <summary>Offline: the true peak of a whole rendered mix, in dBTP.</summary>
    public static double MeasureDb(float[] l, float[] r, int frames)
    {
        var tp = new TruePeak();
        double pk = 0;
        int n = Math.Min(frames, Math.Min(l.Length, r.Length));
        for (int i = 0; i < n + Delay; i++)
        {
            double v = i < n ? tp.Next(l[i], r[i]) : tp.Next(0, 0);
            if (v > pk) pk = v;
        }
        return ToDb(pk);
    }
}
