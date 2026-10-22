namespace GuitarCam.Audio.Dsp;

/// <summary>
/// Filter shapes matching Web Audio's <c>BiquadFilterType</c> (used by <c>CompEq</c>, <c>TrackEq</c>,
/// <c>ReverbBus</c> and <c>DelayBus</c>).
/// </summary>
public enum BiquadType
{
    Highpass,
    Lowshelf,
    Peaking,
    Highshelf,
    Lowpass,
}

/// <summary>
/// Port of <c>BiquadSpec</c> in <c>GuitarCam/src/audio/eq.ts</c>. Note that Web Audio's
/// <c>BiquadFilterNode</c> reads <see cref="Q"/> in dB for <see cref="BiquadType.Highpass"/> and
/// <see cref="BiquadType.Lowpass"/>, linearly for <see cref="BiquadType.Peaking"/>, and ignores it for shelves.
/// </summary>
public readonly record struct BiquadSpec(BiquadType Type, double Freq, double Q, double Gain = 0, bool IsFlat = false)
{
    public static readonly BiquadSpec Flat = new(BiquadType.Peaking, 1000, 1, 0, IsFlat: true);

    public static BiquadType ParseType(string? type, BiquadType fallback = BiquadType.Peaking) => type switch
    {
        "highpass" => BiquadType.Highpass,
        "lowshelf" => BiquadType.Lowshelf,
        "peaking" => BiquadType.Peaking,
        "highshelf" => BiquadType.Highshelf,
        "lowpass" => BiquadType.Lowpass,
        _ => fallback,
    };

    public static string TypeToString(BiquadType type) => type switch
    {
        BiquadType.Highpass => "highpass",
        BiquadType.Lowshelf => "lowshelf",
        BiquadType.Peaking => "peaking",
        BiquadType.Highshelf => "highshelf",
        BiquadType.Lowpass => "lowpass",
        _ => "peaking",
    };
}

/// <summary>
/// Stereo zero-latency biquad filter matching Web Audio's <c>BiquadFilterNode</c> formulas and
/// <c>getFrequencyResponse</c> (<c>GuitarCam/src/audio/eq.ts</c> and <c>mixGraph.ts</c>).
/// Note: in <c>gclab verificar</c> (<c>tools/worklet-ref.mjs</c>), Web Audio's native <c>BiquadFilterNode</c>,
/// <c>DelayNode</c> and <c>ConvolverNode</c> are emulated in Node following the W3C Web Audio specification
/// and Chromium's implementation (not executed inside a browser <c>OfflineAudioContext</c>).
/// Coefficient updates via <see cref="Set"/> replace a single immutable reference so live UI changes never
/// tear coefficients on the audio thread.
/// </summary>
public sealed class Biquad
{
    public sealed record Coeffs(BiquadSpec Spec, double B0, double B1, double B2, double A1, double A2);

    public static readonly Coeffs FlatCoeffs = new(BiquadSpec.Flat, 1, 0, 0, 0, 0);

    readonly double fs;
    readonly double[] x1 = new double[2];
    readonly double[] x2 = new double[2];
    readonly double[] y1 = new double[2];
    readonly double[] y2 = new double[2];

    volatile Coeffs current = FlatCoeffs;

    public BiquadSpec Spec => current.Spec;
    public Coeffs CurrentCoeffs => current;

    public Biquad(double sampleRate) => fs = sampleRate;

    public Biquad(double sampleRate, BiquadSpec spec) : this(sampleRate) => Set(spec);

    public void Reset()
    {
        x1[0] = x1[1] = 0;
        x2[0] = x2[1] = 0;
        y1[0] = y1[1] = 0;
        y2[0] = y2[1] = 0;
    }

    public static Coeffs Compute(double sampleRate, BiquadSpec s)
    {
        if (s.IsFlat) return FlatCoeffs;
        ComputeCoefficients(sampleRate, s, out double b0, out double b1, out double b2, out double a1, out double a2);
        return new Coeffs(s, b0, b1, b2, a1, a2);
    }

    public void Set(BiquadSpec s)
    {
        if (s == current.Spec) return;
        current = Compute(fs, s);
    }

    public void Set(Coeffs coeffs) => current = coeffs;

    public static void ComputeCoefficients(
        double sampleRate,
        BiquadSpec s,
        out double b0,
        out double b1,
        out double b2,
        out double a1,
        out double a2)
    {
        if (s.IsFlat)
        {
            b0 = 1;
            b1 = b2 = a1 = a2 = 0;
            return;
        }

        double nyquist = sampleRate * 0.5;
        double f = Math.Clamp(s.Freq / nyquist, 0.0, 1.0);
        double w0 = Math.PI * f;

        switch (s.Type)
        {
            case BiquadType.Lowpass:
            {
                if (f >= 1.0)
                {
                    b0 = 1;
                    b1 = b2 = a1 = a2 = 0;
                    return;
                }
                if (f <= 0.0)
                {
                    b0 = b1 = b2 = a1 = a2 = 0;
                    return;
                }
                double g = Math.Pow(10.0, s.Q / 20.0);
                double alpha = Math.Sin(w0) / (2.0 * g);
                double cosw = Math.Cos(w0);
                double a0 = 1.0 + alpha;
                double nb1 = 1.0 - cosw;
                b0 = (0.5 * nb1) / a0;
                b1 = nb1 / a0;
                b2 = b0;
                a1 = (-2.0 * cosw) / a0;
                a2 = (1.0 - alpha) / a0;
                return;
            }
            case BiquadType.Highpass:
            {
                if (f >= 1.0)
                {
                    b0 = b1 = b2 = a1 = a2 = 0;
                    return;
                }
                if (f <= 0.0)
                {
                    b0 = 1;
                    b1 = b2 = a1 = a2 = 0;
                    return;
                }
                double g = Math.Pow(10.0, s.Q / 20.0);
                double alpha = Math.Sin(w0) / (2.0 * g);
                double cosw = Math.Cos(w0);
                double a0 = 1.0 + alpha;
                double onePlusCos = 1.0 + cosw;
                b0 = (0.5 * onePlusCos) / a0;
                b1 = -onePlusCos / a0;
                b2 = b0;
                a1 = (-2.0 * cosw) / a0;
                a2 = (1.0 - alpha) / a0;
                return;
            }
            case BiquadType.Peaking:
            {
                if (Math.Abs(s.Gain) < 1e-9 || f <= 0.0 || f >= 1.0)
                {
                    b0 = 1;
                    b1 = b2 = a1 = a2 = 0;
                    return;
                }
                double A = Math.Pow(10.0, s.Gain / 40.0);
                double q = Math.Max(1e-6, s.Q);
                double alpha = Math.Sin(w0) / (2.0 * q);
                double cosw = Math.Cos(w0);
                double a0 = 1.0 + alpha / A;
                b0 = (1.0 + alpha * A) / a0;
                b1 = (-2.0 * cosw) / a0;
                b2 = (1.0 - alpha * A) / a0;
                a1 = (-2.0 * cosw) / a0;
                a2 = (1.0 - alpha / A) / a0;
                return;
            }
            case BiquadType.Lowshelf:
            {
                if (Math.Abs(s.Gain) < 1e-9 || f <= 0.0)
                {
                    b0 = 1;
                    b1 = b2 = a1 = a2 = 0;
                    return;
                }
                double A = Math.Pow(10.0, s.Gain / 40.0);
                if (f >= 1.0)
                {
                    b0 = A * A;
                    b1 = b2 = a1 = a2 = 0;
                    return;
                }
                double sinw = Math.Sin(w0);
                double cosw = Math.Cos(w0);
                double beta = Math.Sqrt(A) * Math.Sqrt(2.0) * sinw;
                double ap1 = A + 1.0;
                double am1 = A - 1.0;
                double a0 = ap1 + am1 * cosw + beta;
                b0 = (A * (ap1 - am1 * cosw + beta)) / a0;
                b1 = (2.0 * A * (am1 - ap1 * cosw)) / a0;
                b2 = (A * (ap1 - am1 * cosw - beta)) / a0;
                a1 = (-2.0 * (am1 + ap1 * cosw)) / a0;
                a2 = (ap1 + am1 * cosw - beta) / a0;
                return;
            }
            case BiquadType.Highshelf:
            {
                if (Math.Abs(s.Gain) < 1e-9 || f >= 1.0)
                {
                    b0 = 1;
                    b1 = b2 = a1 = a2 = 0;
                    return;
                }
                double A = Math.Pow(10.0, s.Gain / 40.0);
                if (f <= 0.0)
                {
                    b0 = A * A;
                    b1 = b2 = a1 = a2 = 0;
                    return;
                }
                double sinw = Math.Sin(w0);
                double cosw = Math.Cos(w0);
                double beta = Math.Sqrt(A) * Math.Sqrt(2.0) * sinw;
                double ap1 = A + 1.0;
                double am1 = A - 1.0;
                double a0 = ap1 - am1 * cosw + beta;
                b0 = (A * (ap1 + am1 * cosw + beta)) / a0;
                b1 = (-2.0 * A * (am1 + ap1 * cosw)) / a0;
                b2 = (A * (ap1 + am1 * cosw - beta)) / a0;
                a1 = (2.0 * (am1 - ap1 * cosw)) / a0;
                a2 = (ap1 - am1 * cosw - beta) / a0;
                return;
            }
            default:
                b0 = 1;
                b1 = b2 = a1 = a2 = 0;
                return;
        }
    }

    public double ProcessSample(double x, int ch) => ProcessSample(x, ch, current);

    public double ProcessSample(double x, int ch, Coeffs c)
    {
        if (c.Spec.IsFlat) return x;
        double y = c.B0 * x + c.B1 * x1[ch] + c.B2 * x2[ch] - c.A1 * y1[ch] - c.A2 * y2[ch];
        x2[ch] = x1[ch];
        x1[ch] = x;
        y2[ch] = y1[ch];
        y1[ch] = y;
        return y;
    }

    public void Process(Span<float> left, Span<float> right) => Process(left, right, current);

    public void Process(Span<float> left, Span<float> right, Coeffs co)
    {
        if (co.Spec.IsFlat) return;
        double cb0 = co.B0, cb1 = co.B1, cb2 = co.B2, ca1 = co.A1, ca2 = co.A2;
        for (int c = 0; c < 2; c++)
        {
            Span<float> buf = c == 0 ? left : right;
            double px1 = x1[c], px2 = x2[c], py1 = y1[c], py2 = y2[c];
            for (int i = 0; i < buf.Length; i++)
            {
                double x = buf[i];
                double y = cb0 * x + cb1 * px1 + cb2 * px2 - ca1 * py1 - ca2 * py2;
                px2 = px1;
                px1 = x;
                py2 = py1;
                py1 = y;
                buf[i] = (float)y;
            }
            x1[c] = px1;
            x2[c] = px2;
            y1[c] = py1;
            y2[c] = py2;
        }
    }

    /// <summary>
    /// Magnitude response in dB at <paramref name="freqHz"/> for one <see cref="BiquadSpec"/>,
    /// matching <c>BiquadFilterNode.getFrequencyResponse</c> in <c>eq.ts</c>.
    /// </summary>
    public static double ResponseDb(BiquadSpec spec, double sampleRate, double freqHz)
    {
        if (spec.IsFlat) return 0;
        ComputeCoefficients(sampleRate, spec, out double cb0, out double cb1, out double cb2, out double ca1, out double ca2);
        double w = 2.0 * Math.PI * freqHz / sampleRate;
        double cos1 = Math.Cos(w);
        double sin1 = Math.Sin(w);
        double cos2 = Math.Cos(2.0 * w);
        double sin2 = Math.Sin(2.0 * w);

        double nr = cb0 + cb1 * cos1 + cb2 * cos2;
        double ni = -(cb1 * sin1 + cb2 * sin2);
        double dr = 1.0 + ca1 * cos1 + ca2 * cos2;
        double di = -(ca1 * sin1 + ca2 * sin2);

        double numSq = nr * nr + ni * ni;
        double denSq = Math.Max(1e-24, dr * dr + di * di);
        double mag = Math.Sqrt(numSq / denSq);
        return 20.0 * Math.Log10(Math.Max(1e-6, mag));
    }

    /// <summary>Port of <c>responseDb</c> in <c>GuitarCam/src/audio/eq.ts</c>.</summary>
    public static void ResponseDb(ReadOnlySpan<BiquadSpec> specs, double sampleRate, ReadOnlySpan<float> freqs, Span<float> outDb)
    {
        outDb.Clear();
        for (int s = 0; s < specs.Length; s++)
        {
            var spec = specs[s];
            if (spec.IsFlat) continue;
            for (int k = 0; k < freqs.Length; k++)
                outDb[k] += (float)ResponseDb(spec, sampleRate, freqs[k]);
        }
    }
}
