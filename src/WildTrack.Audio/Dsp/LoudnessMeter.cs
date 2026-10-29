namespace WildTrack.Audio.Dsp;

/// <summary>
/// Loudness in LUFS (ITU-R BS.1770 / EBU R128), native only: K-weighting (the standard's high shelf and high pass)
/// and the mean square in 100 ms steps. The audio thread only filters and adds (<see cref="Process"/>); the reader
/// turns the steps into momentary (400 ms), short-term (3 s) and integrated (gated at −70 LUFS and 10 LU under the
/// ungated level) with <see cref="Poll"/>. The integrated gate keeps a histogram in 0,1 LU bins, so it never grows
/// with the length of the song. <see cref="Measure"/> does the same offline for a rendered mix.
/// </summary>
public sealed class LoudnessMeter
{
    const int StepRing = 64;
    /// <summary>Histogram of the 400 ms blocks from −70 to +10 LUFS in 0,1 LU bins.</summary>
    const double HistMin = -70, HistMax = 10, HistStep = 0.1;
    static readonly int HistBins = (int)((HistMax - HistMin) / HistStep);

    readonly int stepLen;
    // K-weighting, one state per channel and stage
    readonly double sb0, sb1, sb2, sa1, sa2, hb0, hb1, hb2, ha1, ha2;
    double s1L, s2L, h1L, h2L, s1R, s2R, h1R, h2R;
    double acc;
    int accN;
    readonly double[] steps = new double[StepRing];
    long stepCount;

    // reader side
    long read;
    readonly double[] recent = new double[30];
    int recentN;
    readonly long[] histCount = new long[HistBins];
    readonly double[] histEnergy = new double[HistBins];

    public LoudnessMeter(int sampleRate)
    {
        stepLen = sampleRate / 10;
        // pre-filter: high shelf (+4 dB around 1,7 kHz), from the standard's 48 kHz design for any rate
        double k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate), q = 0.7071752369554196;
        double vh = Math.Pow(10, 3.999843853973347 / 20), vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k / q + k * k;
        (sb0, sb1, sb2) = ((vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0);
        (sa1, sa2) = (2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        // RLB: high pass at 38 Hz
        k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
        q = 0.5003270373238773;
        a0 = 1 + k / q + k * k;
        (hb0, hb1, hb2) = (1, -2, 1);
        (ha1, ha2) = (2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
    }

    /// <summary>Audio thread: K-weights a block and adds it to the current 100 ms step. No allocation, no locks.</summary>
    public void Process(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        for (int i = 0; i < l.Length; i++)
        {
            double yl = Filter(l[i], ref s1L, ref s2L, ref h1L, ref h2L);
            double yr = Filter(r[i], ref s1R, ref s2R, ref h1R, ref h2R);
            acc += yl * yl + yr * yr;
            if (++accN < stepLen) continue;
            steps[(int)(stepCount % StepRing)] = acc / stepLen;
            Interlocked.Increment(ref stepCount);
            acc = 0;
            accN = 0;
        }
    }

    double Filter(double v, ref double s1, ref double s2, ref double h1, ref double h2)
    {
        // transposed direct form II, both stages
        double y = sb0 * v + s1;
        s1 = sb1 * v - sa1 * y + s2;
        s2 = sb2 * v - sa2 * y;
        double z = hb0 * y + h1;
        h1 = hb1 * y - ha1 * z + h2;
        h2 = hb2 * y - ha2 * z;
        return double.IsFinite(z) ? z : 0;
    }

    public double Momentary { get; private set; } = double.NegativeInfinity;
    public double ShortTerm { get; private set; } = double.NegativeInfinity;
    public double Integrated { get; private set; } = double.NegativeInfinity;

    /// <summary>Reader: takes the new steps and updates the three readings. True when something new came in.</summary>
    public bool Poll()
    {
        long n = Interlocked.Read(ref stepCount);
        if (n == read) return false;
        if (n - read > StepRing) read = n - StepRing; // the reader fell behind: those steps are gone
        for (; read < n; read++)
        {
            Array.Copy(recent, 1, recent, 0, recent.Length - 1);
            recent[^1] = steps[(int)(read % StepRing)];
            recentN = Math.Min(recent.Length, recentN + 1);
            if (recentN < 4) continue;
            double block = Mean(4);
            double lufs = Lufs(block);
            if (lufs < HistMin) continue;
            int bin = Math.Min(HistBins - 1, (int)((lufs - HistMin) / HistStep));
            histCount[bin]++;
            histEnergy[bin] += block;
        }
        Momentary = recentN >= 4 ? Lufs(Mean(4)) : double.NegativeInfinity;
        ShortTerm = recentN >= 30 ? Lufs(Mean(30)) : double.NegativeInfinity;
        Integrated = Gated(histCount, histEnergy);
        return true;
    }

    double Mean(int count)
    {
        double s = 0;
        for (int i = recent.Length - count; i < recent.Length; i++) s += recent[i];
        return s / count;
    }

    /// <summary>Starts the integrated reading over (a new pass of the song).</summary>
    public void Reset()
    {
        read = Interlocked.Read(ref stepCount);
        recentN = 0;
        Array.Clear(recent);
        Array.Clear(histCount);
        Array.Clear(histEnergy);
        Momentary = ShortTerm = Integrated = double.NegativeInfinity;
    }

    static double Lufs(double meanSquare) => meanSquare > 0 ? -0.691 + 10 * Math.Log10(meanSquare) : double.NegativeInfinity;

    static double Gated(long[] count, double[] energy)
    {
        long n = 0;
        double e = 0;
        for (int i = 0; i < count.Length; i++) { n += count[i]; e += energy[i]; }
        if (n == 0) return double.NegativeInfinity;
        double relative = Lufs(e / n) - 10;
        n = 0;
        e = 0;
        for (int i = 0; i < count.Length; i++)
        {
            if (HistMin + (i + 1) * HistStep <= relative) continue;
            n += count[i];
            e += energy[i];
        }
        return n == 0 ? double.NegativeInfinity : Lufs(e / n);
    }

    /// <summary>Integrated loudness of a whole stereo signal (an export, a WAV), with the same steps and gates.</summary>
    public static double Measure(float[] left, float[] right, int sampleRate)
    {
        var m = new LoudnessMeter(sampleRate);
        const int Block = 4096;
        int len = Math.Min(left.Length, right.Length);
        for (int i = 0; i < len; i += Block)
        {
            int c = Math.Min(Block, len - i);
            m.Process(left.AsSpan(i, c), right.AsSpan(i, c));
            m.Poll();
        }
        return m.Integrated;
    }
}
