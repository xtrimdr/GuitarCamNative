namespace WildTrack.Audio.Dsp;

/// <summary>
/// The gate's settings as the engine takes them. <see cref="Mode"/> picks how deep and how fast it closes (soft |
/// normal | hard): a musician chooses "how much" instead of a range and a release, so it can't be set to chop a
/// sustain by accident.
/// </summary>
public sealed record GateParams(bool On, string Mode, double ThresholdDb)
{
    public const string Soft = "soft", Normal = "normal", Hard = "hard";
    public const double MinThresholdDb = -80, MaxThresholdDb = -20, DefaultThresholdDb = -50;

    public static readonly GateParams Defaults = new(false, Normal, DefaultThresholdDb);

    /// <summary>How far it lowers the signal while closed (dB, positive) and how long it takes to get there (s).</summary>
    public (double DepthDb, double ReleaseSec) Shape => Mode switch
    {
        Soft => (12, 0.30),
        Hard => (80, 0.10),
        _ => (24, 0.18),
    };

    public static string ParseMode(string? m) => m is Soft or Hard ? m : Normal;
}

/// <summary>
/// Noise gate / downward expander for the hum and hiss between phrases (an electric guitar with gain). Stereo-linked
/// peak detector; it opens at the threshold and closes only 6 dB under it after a hold, so a note that dies slowly
/// isn't cut on and off around the threshold (the hysteresis), and it closes over the mode's release, never at once.
/// Opening takes about a millisecond: no look-ahead, so it adds no latency. Off it is bypassed and its gain rests
/// at 1, so turning it on glides down instead of clicking. Audio thread: no allocations.
/// </summary>
public sealed class NoiseGate
{
    /// <summary>How much under the threshold the signal has to fall before it closes (dB).</summary>
    const double HysteresisDb = 6;
    /// <summary>How long it stays open after the signal falls under the closing level: the gaps between picked notes.</summary>
    const double HoldSec = 0.06;
    /// <summary>The detector's fall: fast enough to follow the end of a note, slow enough not to ride a low E's cycles.</summary>
    const double DetectorFallSec = 0.012;
    /// <summary>The opening's time constant: short enough to keep the pick's attack.</summary>
    const double OpenSec = 0.0003;

    /// <summary>Everything the audio thread reads, swapped as one reference.</summary>
    sealed record Coeffs(bool On, float OpenLin, float CloseLin, float Floor, float Fall, float Rise, float Release, int Hold);

    readonly double fs;
    volatile Coeffs c;
    float env, gain = 1, minGain = 1;
    bool open = true;
    int hold;

    public NoiseGate(double sampleRate)
    {
        fs = sampleRate;
        c = Make(GateParams.Defaults);
    }

    public void Set(GateParams p) => c = Make(p);

    Coeffs Make(GateParams p)
    {
        var (depth, release) = p.Shape;
        double thr = Math.Clamp(p.ThresholdDb, GateParams.MinThresholdDb, GateParams.MaxThresholdDb);
        // release is how long it takes to close: about three time constants
        return new Coeffs(p.On, (float)Db(thr), (float)Db(thr - HysteresisDb), (float)Db(-depth),
            (float)Math.Exp(-1 / (DetectorFallSec * fs)), (float)(1 - Math.Exp(-1 / (OpenSec * fs))),
            (float)(1 - Math.Exp(-3 / (release * fs))), (int)(HoldSec * fs));
    }

    static double Db(double db) => Math.Pow(10, db / 20);

    public void Process(Span<float> left, Span<float> right)
    {
        var k = c;
        if (!k.On)
        {
            gain = 1;
            open = true;
            return;
        }
        float e = env, g = gain, low = minGain;
        bool o = open;
        int h = hold;
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            float x = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
            e = x > e ? x : e * k.Fall;
            if (e >= k.OpenLin)
            {
                o = true;
                h = k.Hold;
            }
            else if (o)
            {
                if (e >= k.CloseLin) h = k.Hold;
                else if (h > 0) h--;
                else o = false;
            }
            float target = o ? 1 : k.Floor;
            g += (target - g) * (target > g ? k.Rise : k.Release);
            left[i] *= g;
            right[i] *= g;
            if (g < low) low = g;
        }
        env = e;
        gain = g;
        minGain = low;
        open = o;
        hold = h;
    }

    public void Reset()
    {
        env = 0;
        gain = 1;
        open = true;
        hold = 0;
    }

    /// <summary>The deepest it lowered since the last call (dB, &lt;= 0), for the strip's meter.</summary>
    public double TakeGrDb()
    {
        float low = minGain;
        minGain = 1;
        return low >= 1 ? 0 : 20 * Math.Log10(Math.Max(low, 1e-6));
    }
}
