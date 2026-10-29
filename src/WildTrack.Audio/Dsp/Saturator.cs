namespace WildTrack.Audio.Dsp;

/// <summary>
/// Port of gc-saturator (GuitarCam/src/audio/fxWorklet.ts). tanh waveshaper with first-order ADAA on the
/// residual only (curve minus its straight line, then a DC blocker), so the dry signal passes untouched.
///   tube: biased curve -> even harmonics. tape: symmetric, soft high cut + head bump at 90 Hz.
/// </summary>
public sealed class Saturator
{
    static readonly double Ln2 = Math.Log(2);

    readonly double fs;
    readonly double dcR;
    readonly double[] u1 = new double[2]; // previous shaper input
    readonly double[] lp = new double[2]; // high-cut state
    readonly double[] dcx = new double[2]; // DC blocker
    readonly double[] dcy = new double[2];
    readonly double[][] bq = [new double[4], new double[4]]; // head bump biquad state (x1 x2 y1 y2)
    double bumpDb = -1;
    double cb0 = 1, cb1, cb2, ca1, ca2;

    public bool Bypass { get; set; } = true;
    public double Drive { get; set; } = 1;
    public double Bias { get; set; }
    public double Tone { get; set; } = 20000;
    public double ToneMix { get; set; }
    public double Bump { get; set; }

    public Saturator(double sampleRate)
    {
        fs = sampleRate;
        dcR = Math.Exp(-2 * Math.PI * 8 / fs);
    }

    public void Set(SatParams p)
    {
        Bypass = p.Bypass;
        Drive = p.Drive;
        Bias = p.Bias;
        Tone = p.Tone;
        ToneMix = p.ToneMix;
        Bump = p.Bump;
    }

    public void Reset()
    {
        u1[0] = u1[1] = 0;
        lp[0] = lp[1] = 0;
        dcx[0] = dcx[1] = 0;
        dcy[0] = dcy[1] = 0;
        Array.Clear(bq[0]);
        Array.Clear(bq[1]);
    }

    static double LogCosh(double u)
    {
        double a = Math.Abs(u);
        return a + double.LogP1(Math.Exp(-2 * a)) - Ln2;
    }

    // RBJ peaking EQ at 90 Hz
    void SetBump(double db)
    {
        bumpDb = db;
        double A = Math.Pow(10, db / 40);
        double w = 2 * Math.PI * 90 / fs;
        double al = Math.Sin(w) / (2 * 0.8);
        double cs = Math.Cos(w);
        double a0 = 1 + al / A;
        cb0 = (1 + al * A) / a0;
        cb1 = -2 * cs / a0;
        cb2 = (1 - al * A) / a0;
        ca1 = -2 * cs / a0;
        ca2 = (1 - al / A) / a0;
    }

    /// <summary>Processes a stereo block in place.</summary>
    public void Process(Span<float> left, Span<float> right)
    {
        if (Bypass)
        {
            u1[0] = u1[1] = 0;
            return;
        }
        double k = Math.Max(0.0001, Drive);
        double b = Bias;
        double tb = Math.Tanh(b);
        double norm = 1 / (k * (1 - tb * tb)); // small-signal gain back to 1
        double aLp = 1 - Math.Exp(-2 * Math.PI * Math.Min(Tone, fs * 0.45) / fs);
        double wLp = ToneMix;
        double slope = 1 - tb * tb; // the curve's straight line around the bias point
        if (Math.Abs(Bump - bumpDb) > 0.01) SetBump(Bump);
        bool useBump = Bump > 0.01;
        double R = dcR;
        for (int c = 0; c < 2; c++)
        {
            Span<float> buf = c == 0 ? left : right;
            double pu = u1[c], l = lp[c], dx = dcx[c], dy = dcy[c];
            double[] q = bq[c];
            for (int i = 0; i < buf.Length; i++)
            {
                double x = buf[i];
                double u = k * x + b;
                double d = u - pu;
                double s = Math.Abs(d) < 1e-5 ? Math.Tanh(0.5 * (u + pu)) : (LogCosh(u) - LogCosh(pu)) / d;
                // residual: averaged curve minus the averaged straight line (both over the same two samples)
                double res = (s - tb - slope * (0.5 * (u + pu) - b)) * norm;
                pu = u;
                // DC blocker on the residual only (the bias leaves a signal-dependent offset there)
                double rd = res - dx + R * dy;
                dx = res;
                dy = rd;
                double y = x + rd;
                l += aLp * (y - l);
                y += (l - y) * wLp;
                if (useBump)
                {
                    double z = cb0 * y + cb1 * q[0] + cb2 * q[1] - ca1 * q[2] - ca2 * q[3];
                    q[1] = q[0];
                    q[0] = y;
                    q[3] = q[2];
                    q[2] = z;
                    y = z;
                }
                buf[i] = (float)y;
            }
            u1[c] = pu;
            lp[c] = l;
            dcx[c] = dx;
            dcy[c] = dy;
        }
    }
}

public enum SatMode { Tube, Tape }

/// <summary>What the "Vida" knob does (port of satParams in saturation.ts).</summary>
public readonly record struct SatParams(bool Bypass, double Drive, double Bias, double Tone, double ToneMix, double Bump)
{
    /// <param name="rmsDb">The take's typical level (<see cref="LevelAnalysis"/>); -24 if unknown.</param>
    public static SatParams From(bool on, SatMode mode, double amount, double rmsDb = -24)
    {
        double a = Math.Clamp(amount, 0, 1);
        double level = double.IsFinite(rmsDb) && rmsDb > -80 ? rmsDb : -24;
        double target = -60 + 58 * Math.Pow(a, 0.35);
        double drive = Math.Clamp(Math.Pow(10, (target - level) / 20), 0.0001, 300);
        bool tape = mode == SatMode.Tape;
        return new SatParams(
            Bypass: !on,
            Drive: drive,
            Bias: tape ? 0 : 0.4 * a,
            Tone: tape ? 18000 * (1 - 0.5 * a) : 18000,
            ToneMix: tape ? Math.Min(1, a * 4) : 0,
            Bump: tape ? 1.5 * a : 0);
    }
}
