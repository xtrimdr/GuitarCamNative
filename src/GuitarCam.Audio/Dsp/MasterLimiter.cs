namespace GuitarCam.Audio.Dsp;

/// <summary>
/// Port of gc-limiter (fxWorklet.ts): look-ahead peak limiter for the master bus, the output never
/// exceeds the ceiling. Latency = <c>lookahead</c> samples.
/// </summary>
public sealed class MasterLimiter
{
    public const double LookaheadSec = 0.003;
    public const double CeilingDb = -0.3;

    public static int LookaheadSamples(int sampleRate) => (int)Math.Round(LookaheadSec * sampleRate);

    readonly int N;
    readonly double ceiling;
    readonly float[] bufL, bufR, req;
    readonly double aA, aR;
    int idx;
    double minReq = 1;
    int minAge;
    double g = 1;

    public int Latency => N;

    public MasterLimiter(double sampleRate, int lookahead, double ceilingDb = CeilingDb)
    {
        N = Math.Max(1, lookahead);
        ceiling = Math.Pow(10, ceilingDb / 20);
        bufL = new float[N];
        bufR = new float[N];
        req = new float[N];
        Array.Fill(req, 1f);
        aA = Math.Exp(-1 / (N / 4.0));
        aR = Math.Exp(-1 / (0.08 * sampleRate));
    }

    public void Process(Span<float> left, Span<float> right)
    {
        double c = ceiling;
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

            double target = minReq;
            g = target < g ? aA * g + (1 - aA) * target : aR * g + (1 - aR) * target;
            double gg = Math.Min(g, reqOut);
            left[i] = (float)(outL * gg);
            right[i] = (float)(outR * gg);
        }
    }
}
