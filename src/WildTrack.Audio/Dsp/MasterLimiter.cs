namespace WildTrack.Audio.Dsp;

/// <summary>
/// Port of gc-limiter (fxWorklet.ts): look-ahead peak limiter for the master bus, the output never
/// exceeds the ceiling. Latency = <c>lookahead</c> samples. Native only: the ceiling is in true peak (dBTP): besides
/// each sample, a <see cref="TruePeak"/> detector asks for the gain the peaks between samples need, which the AAC of
/// the MP4 would otherwise push over 0 dB; its answer is <see cref="TruePeak.Delay"/> samples late, so it lands on the
/// slot of the sample it belongs to while that one is still in the look-ahead. Native only: the ceiling and the release can change while
/// it plays (<see cref="Set"/>, the master card's Techo and Release), and <see cref="Bypass"/> turns it off without
/// moving anything in time: the look-ahead delay stays, so the click and the export stay aligned.
/// </summary>
public sealed class MasterLimiter
{
    public const double LookaheadSec = 0.003;
    /// <summary>dBTP: what the platforms ask for, with room for the AAC encoder's own overshoot.</summary>
    public const double CeilingDb = -1;
    public const double CeilingMinDb = -6;
    public const double ReleaseMs = 80, ReleaseMinMs = 20, ReleaseMaxMs = 500;

    public static int LookaheadSamples(int sampleRate) => (int)Math.Round(LookaheadSec * sampleRate);

    readonly int N;
    double ceiling;
    readonly float[] bufL, bufR, req;
    readonly double fs, aA;
    double aR;
    int idx;
    double minReq = 1;
    int minAge;
    double g = 1;
    readonly TruePeak tp = new();

    public int Latency => N;

    /// <summary>Off: the delay line still runs, the gain glides back to 1 (the master card's Limitador ON).</summary>
    public bool Bypass { get; set; }

    /// <summary>Lowest gain applied since the UI last read it (1 = not limiting); written once per block.</summary>
    float minGain = 1;

    /// <summary>For the master's LIM light: the deepest reduction since the last call, in dB (≤ 0).</summary>
    public double TakeReductionDb()
    {
        float m = Interlocked.Exchange(ref minGain, 1f);
        return m < 1 ? 20 * Math.Log10(Math.Max(1e-6, m)) : 0;
    }

    public MasterLimiter(double sampleRate, int lookahead, double ceilingDb = CeilingDb)
    {
        N = Math.Max(1, lookahead);
        fs = sampleRate;
        ceiling = Math.Pow(10, ceilingDb / 20);
        bufL = new float[N];
        bufR = new float[N];
        req = new float[N];
        Array.Fill(req, 1f);
        aA = Math.Exp(-1 / (N / 4.0));
        aR = Math.Exp(-1 / (ReleaseMs / 1000 * sampleRate));
    }

    /// <summary>New ceiling (dB, <see cref="CeilingMinDb"/> to 0) and release (ms); a double write each, safe while it plays.</summary>
    public void Set(double ceilingDb, double releaseMs)
    {
        ceiling = Math.Pow(10, Math.Clamp(double.IsFinite(ceilingDb) ? ceilingDb : CeilingDb, CeilingMinDb, 0) / 20);
        aR = Math.Exp(-1 / (Math.Clamp(double.IsFinite(releaseMs) ? releaseMs : ReleaseMs, ReleaseMinMs, ReleaseMaxMs) / 1000 * fs));
    }

    public void Reset()
    {
        Array.Clear(bufL);
        Array.Clear(bufR);
        Array.Fill(req, 1f);
        idx = 0;
        minReq = 1;
        minAge = 0;
        g = 1;
        tp.Reset();
    }

    public void Process(Span<float> left, Span<float> right)
    {
        double c = ceiling, rel = aR;
        bool off = Bypass;
        double lowest = 1;
        for (int i = 0; i < left.Length; i++)
        {
            double l = left[i];
            double r = right[i];
            double peak = Math.Max(Math.Abs(l), Math.Abs(r));
            double reqIn = peak > c ? c / peak : 1;

            // oldest sample leaves the delay line, the new one takes its slot
            int k = idx;
            double outL = bufL[k];
            double outR = bufR[k];
            double reqOut = req[k];
            bufL[k] = (float)l;
            bufR[k] = (float)r;
            req[k] = (float)reqIn;
            idx = k + 1 == N ? 0 : k + 1;

            // the peak between samples, for the sample Delay back (still in the line when the look-ahead is longer)
            double t = tp.Next((float)l, (float)r);
            if (t > c && N > TruePeak.Delay)
            {
                double reqTp = c / t;
                int s = k - TruePeak.Delay;
                if (s < 0) s += N;
                if (reqTp < req[s]) req[s] = (float)reqTp;
                if (reqTp <= minReq)
                {
                    minReq = reqTp;
                    minAge = TruePeak.Delay;
                }
            }

            // minimum required gain over the look-ahead window
            if (reqIn <= minReq)
            {
                minReq = reqIn;
                minAge = 0;
            }
            else if (++minAge >= N)
            {
                double m = 1;
                for (int j = 0; j < N; j++) if (req[j] < m) m = req[j];
                minReq = m;
                minAge = 0;
            }

            double target = off ? 1 : minReq;
            g = target < g ? aA * g + (1 - aA) * target : rel * g + (1 - rel) * target;
            double gg = off ? g : Math.Min(g, reqOut);
            if (gg < lowest) lowest = gg;
            left[i] = (float)(outL * gg);
            right[i] = (float)(outR * gg);
        }
        if (lowest < minGain) minGain = (float)lowest;
    }
}
