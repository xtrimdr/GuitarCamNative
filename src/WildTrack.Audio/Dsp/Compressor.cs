namespace WildTrack.Audio.Dsp;

/// <summary>
/// Port of gc-compressor (fxWorklet.ts): feed-forward, stereo-linked, zero-latency compressor with a soft
/// knee, gain smoothed in dB, plus the optional zero-latency per-track peak limiter after the make-up.
/// </summary>
public sealed class Compressor
{
    readonly double fs;
    double gr; // current gain reduction, dB (<= 0)
    double lg = 1; // peak limiter gain (linear)

    public bool Bypass { get; set; } = true;
    public double Threshold { get; set; } = -18;
    public double Ratio { get; set; } = 4;
    public double Knee { get; set; } = 6;
    public double Attack { get; set; } = 0.01; // s
    public double Release { get; set; } = 0.15; // s
    public double Makeup { get; set; }
    public bool Limit { get; set; }
    public double Ceiling { get; set; } = -6;

    /// <summary>Deepest reduction (dB, &lt;= 0) since the last <see cref="TakeMeter"/>.</summary>
    public double MinGr { get; private set; }

    public Compressor(double sampleRate) => fs = sampleRate;

    public void Reset()
    {
        gr = 0;
        lg = 1;
        MinGr = 0;
    }

    static double DbToLin(double db) => Math.Pow(10, db / 20);

    public double TakeMeter()
    {
        double m = MinGr;
        MinGr = 0;
        return m;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        if (Bypass)
        {
            gr = 0;
            return;
        }
        double T = Threshold;
        double R = Math.Max(1, Ratio);
        double W = Math.Max(0, Knee);
        double slope = 1 / R - 1;
        double aA = Math.Exp(-1 / (Math.Max(0.0001, Attack) * fs));
        double aR = Math.Exp(-1 / (Math.Max(0.005, Release) * fs));
        double makeup = Makeup;
        // Zero-latency peak limiter after the make-up: gain drops on the very sample that would pass
        // the ceiling (no overshoot, no delay, so the track stays on the timeline) and recovers in ~60 ms.
        bool limit = Limit;
        double ceil = DbToLin(Ceiling);
        double aL = Math.Exp(-1 / (0.06 * fs));
        double g1 = lg;
        double y = gr;
        double minGr = MinGr;
        for (int i = 0; i < left.Length; i++)
        {
            double l = left[i];
            double r = right[i];
            double x = Math.Max(Math.Abs(l), Math.Abs(r));
            double xdb = x > 1e-6 ? 20 * Math.Log10(x) : -120;
            double over = xdb - T;
            double target;
            if (2 * over <= -W) target = 0;
            else if (W > 0 && 2 * Math.Abs(over) < W) target = slope * (over + W / 2) * (over + W / 2) / (2 * W);
            else target = slope * over;
            y = target < y ? aA * y + (1 - aA) * target : aR * y + (1 - aR) * target;
            double g = DbToLin(y + makeup);
            double red = y;
            if (limit)
            {
                double pk = x * g;
                double req = pk > ceil ? ceil / pk : 1;
                g1 = req < g1 ? req : Math.Min(req, aL * g1 + (1 - aL));
                g *= g1;
                red += 20 * Math.Log10(g1);
            }
            left[i] = (float)(l * g);
            right[i] = (float)(r * g);
            if (red < minGr) minGr = red;
        }
        gr = y;
        lg = limit ? g1 : 1;
        MinGr = minGr;
    }
}
