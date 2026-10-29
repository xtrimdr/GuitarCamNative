namespace WildTrack.Audio.Dsp;

/// <summary>
/// Port of the AnalyserNode the web hangs after a track's EQ (mixGraph.ts buildTrack: fftSize 8192,
/// smoothingTimeConstant 0.75), read by EqEditor.tsx for its live spectrum. The audio thread only copies the
/// mono down-mix ((L+R)/2, like Web Audio) into a preallocated ring; the FFT runs on the reader's thread in
/// <see cref="Read"/>, following the Web Audio spec: last N samples → Blackman window → |X[k]|/N → smoothing
/// over time → dB. A read that races the writer can mix two blocks; it is only drawn, never heard.
/// </summary>
public sealed class SpectrumTap
{
    /// <summary>fftSize of the web's analyser: fine bins for the lows.</summary>
    public const int FftSize = 8192;
    public const int Bins = FftSize / 2;
    /// <summary>smoothingTimeConstant of the web's analyser.</summary>
    public const double Smoothing = 0.75;
    const int Mask = FftSize - 1;

    readonly float[] ring = new float[FftSize];
    long written;

    readonly double[] re = new double[FftSize];
    readonly double[] im = new double[FftSize];
    readonly double[] window = new double[FftSize];
    readonly double[] smoothed = new double[Bins];
    readonly double[] cos = new double[FftSize / 2];
    readonly double[] sin = new double[FftSize / 2];
    readonly int[] reverse = new int[FftSize];

    public SpectrumTap()
    {
        const double a0 = 0.42, a1 = 0.5, a2 = 0.08; // Blackman, alpha 0.16
        for (int n = 0; n < FftSize; n++)
            window[n] = a0 - a1 * Math.Cos(2 * Math.PI * n / FftSize) + a2 * Math.Cos(4 * Math.PI * n / FftSize);
        for (int k = 0; k < FftSize / 2; k++)
        {
            cos[k] = Math.Cos(-2 * Math.PI * k / FftSize);
            sin[k] = Math.Sin(-2 * Math.PI * k / FftSize);
        }
        int bits = (int)Math.Log2(FftSize);
        for (int i = 0; i < FftSize; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            reverse[i] = r;
        }
    }

    /// <summary>Total samples written since creation; the reader compares it to know whether audio is coming in.</summary>
    public long Written => Interlocked.Read(ref written);

    /// <summary>Audio thread: appends a block. No allocation, no locks.</summary>
    public void Write(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        long w = Interlocked.Read(ref written);
        int n = l.Length;
        for (int i = 0; i < n; i++) ring[(int)((w + i) & Mask)] = 0.5f * (l[i] + r[i]);
        Interlocked.Add(ref written, n);
    }

    /// <summary>Forgets the smoothing history (a different track starts feeding the tap).</summary>
    public void ResetSmoothing() => Array.Clear(smoothed);

    /// <summary>getFloatFrequencyData: fills <paramref name="outDb"/> (<see cref="Bins"/> values) with the smoothed
    /// spectrum of the latest <see cref="FftSize"/> samples, in dB (−∞ becomes a very low finite value).</summary>
    public void Read(float[] outDb)
    {
        long w = Interlocked.Read(ref written);
        for (int i = 0; i < FftSize; i++)
        {
            int src = (int)((w - FftSize + i) & Mask);
            int dst = reverse[i];
            re[dst] = ring[src] * window[i];
            im[dst] = 0;
        }
        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int half = size >> 1, step = FftSize / size;
            for (int start = 0; start < FftSize; start += size)
                for (int k = 0; k < half; k++)
                {
                    int a = start + k, b = a + half;
                    double c = cos[k * step], s = sin[k * step];
                    double tr = re[b] * c - im[b] * s, ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
        int len = Math.Min(outDb.Length, Bins);
        for (int k = 0; k < Bins; k++)
        {
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / FftSize;
            double v = Smoothing * smoothed[k] + (1 - Smoothing) * mag;
            smoothed[k] = double.IsFinite(v) ? v : 0;
            if (k < len) outDb[k] = (float)(20 * Math.Log10(Math.Max(1e-12, smoothed[k])));
        }
    }
}
