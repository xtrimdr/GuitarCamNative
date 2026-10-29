namespace WildTrack.Audio.Dsp;

/// <summary>One band of the master's multiband: how much it compresses (0 = off).</summary>
public readonly record struct MasterBandParams(double Amount);

/// <summary>
/// The master's settings as the DSP reads them (<see cref="MasterChain"/>). The EQ gains are in dB, 0 = flat;
/// <see cref="LowCutHz"/> 0 = no low cut. <see cref="EqOn"/> turns the cleanup before the multiband on (low cut, mud),
/// <see cref="ColorOn"/> the colour after it (lows, presence, air, tilt); then Cinta (<see cref="TapeAmount"/>), Ancho
/// (<see cref="Width"/>, <see cref="MonoBassHz"/>) and the soft clip (<see cref="ClipDb"/>), each with its ON. The limiter fields are the
/// <see cref="MasterLimiter"/>'s, apart from the master's ON: its own ON (<see cref="LimOn"/>), Empuje (gain into it),
/// Techo and Release. <see cref="TargetLufs"/> is the Destino (null = as it is) and <see cref="TargetGainDb"/> the gain
/// measured to land on it (<see cref="MasterLoudness"/>), which then takes the Empuje's place. <see cref="PickAmount"/>
/// is the multiband's fourth band, Púa (<see cref="MasterPickTamer"/>, 0 = off).
/// </summary>
public sealed record MasterParams(
    bool On,
    bool EqOn,
    double LowCutHz,
    double MudDb,
    double LowsDb,
    double PresenceDb,
    double AirDb,
    double TiltDb,
    bool BandsOn,
    double LowXHz,
    double HighXHz,
    MasterBandParams Low,
    MasterBandParams Mid,
    MasterBandParams High,
    bool ColorOn = true,
    double LimDriveDb = 0,
    double LimCeilingDb = MasterLimiter.CeilingDb,
    double LimReleaseMs = MasterLimiter.ReleaseMs,
    bool LimOn = true,
    bool TapeOn = true,
    double TapeAmount = 0,
    bool WidthOn = true,
    double Width = 1,
    double MonoBassHz = 0,
    bool ClipOn = true,
    double ClipDb = 0,
    double? TargetLufs = null,
    double TargetGainDb = 0,
    double PickAmount = 0)
{
    /// <summary>The most the limiter's Empuje pushes the mix into it.</summary>
    public const double DriveMaxDb = 12;

    public static readonly MasterParams Off = new(false, true, 0, 0, 0, 0, 0, 0, true,
        MasterBands.DefaultLowXHz, MasterBands.DefaultHighXHz, default, default, default);

    public MasterBandParams Band(int i) => i switch { 0 => Low, 1 => Mid, _ => High };

    public MasterParams WithBand(int i, MasterBandParams b) => i switch
    {
        0 => this with { Low = b },
        1 => this with { Mid = b },
        _ => this with { High = b },
    };
}

/// <summary>
/// How each band of the master's multiband compresses for a given "Cuánto" (0 to 1), like the track compressor's
/// presets (<see cref="CompPresets"/>): the user picks how much, the band picks how. Lows are slow so they neither
/// pump nor distort the bass; highs are fast so they catch the pick. Nothing is measured beforehand: the threshold sits
/// <c>Headroom</c> dB over the band's own level as it plays (<see cref="MasterBandComp"/>), so it works the moment it
/// is turned on, on any mix and at any level; more "Cuánto" = less headroom and a higher ratio.
/// </summary>
public static class MasterBands
{
    public const int Count = 3;
    public const double DefaultLowXHz = 150, DefaultHighXHz = 5000;
    public const double LowXMin = 60, LowXMax = 400, HighXMin = 2000, HighXMax = 10000;
    /// <summary>"Cuánto" a band starts at when the master is turned on: a gentle glue.</summary>
    public const double DefaultAmount = 0.35;

    /// <summary><c>Headroom</c>: dB over the band's level where it starts compressing (the band's crest decides how much).</summary>
    public sealed record Def(string Id, (double Min, double Max) Headroom, (double Min, double Max) Ratio,
        (double Min, double Max) AttackMs, (double Min, double Max) ReleaseMs, (double Min, double Max) Knee);

    public static readonly Def[] All =
    [
        new("low", Headroom: (4, -6), Ratio: (1.5, 4), AttackMs: (40, 15), ReleaseMs: (300, 160), Knee: (8, 5)),
        new("mid", Headroom: (5, -5), Ratio: (1.5, 3), AttackMs: (25, 10), ReleaseMs: (200, 120), Knee: (8, 5)),
        new("high", Headroom: (6, -4), Ratio: (1.8, 4), AttackMs: (10, 3), ReleaseMs: (120, 60), Knee: (6, 4)),
    ];

    static double Lerp((double Min, double Max) r, double t) => r.Min + (r.Max - r.Min) * t;

    /// <summary>The band's curve for <paramref name="amount"/>: headroom over its level (dB), ratio, knee (dB), attack and release (s).</summary>
    public static (double Headroom, double Ratio, double Knee, double Attack, double Release) Shape(int band, double amount)
    {
        var d = All[Math.Clamp(band, 0, Count - 1)];
        double t = Math.Clamp(amount, 0, 1);
        return (Lerp(d.Headroom, t), Lerp(d.Ratio, t), Lerp(d.Knee, t), Lerp(d.AttackMs, t) / 1000, Lerp(d.ReleaseMs, t) / 1000);
    }

    /// <summary>The crossovers, kept apart and inside their ranges.</summary>
    public static (double Low, double High) Crossovers(MasterParams p)
    {
        double lo = Math.Clamp(double.IsFinite(p.LowXHz) ? p.LowXHz : DefaultLowXHz, LowXMin, LowXMax);
        double hi = Math.Clamp(double.IsFinite(p.HighXHz) ? p.HighXHz : DefaultHighXHz, HighXMin, HighXMax);
        return (lo, Math.Max(hi, lo * 4));
    }
}

/// <summary>
/// The master's EQ in its two halves: the corrective cuts (low cut, mud) go before the multiband and the colour
/// (lows, presence, air, tilt) after it, as mastering engineers do: a boost in front of a compressor makes it squash
/// what was boosted. On screen it is one EQ. Zero latency; coefficients swap as one immutable reference.
/// </summary>
public sealed class MasterEq
{
    public const double GainMax = 6, TiltMax = 3;
    public const double LowCutMin = 20, LowCutMax = 150;
    public const double MudHz = 300, LowsHz = 100, PresenceHz = 3000, AirHz = 10000, TiltHz = 1000;
    /// <summary>Butterworth Q, in dB as <see cref="Biquad"/> reads it for high pass.</summary>
    static readonly double ButterQDb = 20 * Math.Log10(Math.Sqrt(0.5));

    sealed record Config(Biquad.Coeffs Cut, Biquad.Coeffs Mud, Biquad.Coeffs Lows, Biquad.Coeffs Presence, Biquad.Coeffs Air,
        Biquad.Coeffs TiltLow, Biquad.Coeffs TiltHigh);

    readonly double fs;
    readonly Biquad cut, mud, lows, presence, air, tiltLow, tiltHigh;
    volatile Config config;

    public MasterEq(double sampleRate)
    {
        fs = sampleRate;
        cut = new(sampleRate); mud = new(sampleRate); lows = new(sampleRate); presence = new(sampleRate);
        air = new(sampleRate); tiltLow = new(sampleRate); tiltHigh = new(sampleRate);
        config = Build(MasterParams.Off);
    }

    static double G(double db, double max) => Math.Clamp(double.IsFinite(db) ? db : 0, -max, max);

    Config Build(MasterParams p)
    {
        Biquad.Coeffs C(BiquadSpec s) => Biquad.Compute(fs, s);
        double tilt = G(p.TiltDb, TiltMax);
        return new Config(
            p.LowCutHz >= LowCutMin ? C(new(BiquadType.Highpass, Math.Min(p.LowCutHz, LowCutMax), ButterQDb)) : Biquad.FlatCoeffs,
            C(new(BiquadType.Peaking, MudHz, 1, G(p.MudDb, GainMax))),
            C(new(BiquadType.Lowshelf, LowsHz, 0.7071, G(p.LowsDb, GainMax))),
            C(new(BiquadType.Peaking, PresenceHz, 0.8, G(p.PresenceDb, GainMax))),
            C(new(BiquadType.Highshelf, AirHz, 0.7071, G(p.AirDb, GainMax))),
            C(new(BiquadType.Lowshelf, TiltHz, 0.7071, -tilt / 2)),
            C(new(BiquadType.Highshelf, TiltHz, 0.7071, tilt / 2)));
    }

    public void Set(MasterParams p) => config = Build(p);

    public void Reset()
    {
        cut.Reset(); mud.Reset(); lows.Reset(); presence.Reset(); air.Reset(); tiltLow.Reset(); tiltHigh.Reset();
    }

    /// <summary>Before the multiband: low cut and mud.</summary>
    public void ProcessPre(Span<float> l, Span<float> r)
    {
        var c = config;
        cut.Process(l, r, c.Cut);
        mud.Process(l, r, c.Mud);
    }

    /// <summary>After the multiband: lows, presence, air and tilt.</summary>
    public void ProcessPost(Span<float> l, Span<float> r)
    {
        var c = config;
        lows.Process(l, r, c.Lows);
        presence.Process(l, r, c.Presence);
        air.Process(l, r, c.Air);
        tiltLow.Process(l, r, c.TiltLow);
        tiltHigh.Process(l, r, c.TiltHigh);
    }
}

/// <summary>
/// Three-band Linkwitz-Riley crossover (4th order: two Butterworth biquads per side). The low band also goes through
/// the high split's allpass, so the three bands add back to the input with a flat magnitude: with the compressors
/// idle the multiband changes nothing you can hear. Zero latency.
/// </summary>
public sealed class Crossover3
{
    static readonly double ButterQDb = 20 * Math.Log10(Math.Sqrt(0.5));

    sealed record Config(Biquad.Coeffs Lp1, Biquad.Coeffs Hp1, Biquad.Coeffs Lp2, Biquad.Coeffs Hp2, Biquad.Coeffs Ap2);

    readonly double fs;
    readonly Biquad lp1a, lp1b, hp1a, hp1b, lp2a, lp2b, hp2a, hp2b, ap2;
    volatile Config config;

    public Crossover3(double sampleRate)
    {
        fs = sampleRate;
        lp1a = new(sampleRate); lp1b = new(sampleRate); hp1a = new(sampleRate); hp1b = new(sampleRate);
        lp2a = new(sampleRate); lp2b = new(sampleRate); hp2a = new(sampleRate); hp2b = new(sampleRate); ap2 = new(sampleRate);
        config = Build(MasterBands.DefaultLowXHz, MasterBands.DefaultHighXHz);
    }

    Config Build(double lo, double hi) => new(
        Biquad.Compute(fs, new(BiquadType.Lowpass, lo, ButterQDb)),
        Biquad.Compute(fs, new(BiquadType.Highpass, lo, ButterQDb)),
        Biquad.Compute(fs, new(BiquadType.Lowpass, hi, ButterQDb)),
        Biquad.Compute(fs, new(BiquadType.Highpass, hi, ButterQDb)),
        Biquad.Compute(fs, new(BiquadType.Allpass, hi, Math.Sqrt(0.5))));

    public void Set(double lowHz, double highHz) => config = Build(lowHz, highHz);

    public void Reset()
    {
        lp1a.Reset(); lp1b.Reset(); hp1a.Reset(); hp1b.Reset();
        lp2a.Reset(); lp2b.Reset(); hp2a.Reset(); hp2b.Reset(); ap2.Reset();
    }

    /// <summary>Splits <paramref name="l"/>/<paramref name="r"/> into the three bands (same length as the input).</summary>
    public void Split(ReadOnlySpan<float> l, ReadOnlySpan<float> r,
        Span<float> lowL, Span<float> lowR, Span<float> midL, Span<float> midR, Span<float> highL, Span<float> highR)
    {
        var c = config;
        l.CopyTo(lowL); r.CopyTo(lowR);
        l.CopyTo(midL); r.CopyTo(midR);
        lp1a.Process(lowL, lowR, c.Lp1);
        lp1b.Process(lowL, lowR, c.Lp1);
        ap2.Process(lowL, lowR, c.Ap2);
        hp1a.Process(midL, midR, c.Hp1);
        hp1b.Process(midL, midR, c.Hp1);
        midL.CopyTo(highL); midR.CopyTo(highR);
        lp2a.Process(midL, midR, c.Lp2);
        lp2b.Process(midL, midR, c.Lp2);
        hp2a.Process(highL, highR, c.Hp2);
        hp2b.Process(highL, highR, c.Hp2);
    }
}

/// <summary>
/// One band's compressor in the master, which sets its own threshold: it follows the band's level (mean power, rising
/// in ~<see cref="LevelRiseSec"/>, falling in ~<see cref="LevelFallSec"/>, so it holds the loud level through a strum) and
/// compresses what goes <see cref="Headroom"/> dB over it. Its make-up follows the reduction it averages over
/// ~<see cref="MakeupSec"/>, so the band keeps its level and the balance between bands stays: it changes the dynamics.
/// Feed-forward, stereo-linked, soft knee, gain smoothed in dB; it detects the peak held over <see cref="HoldSec"/>, so
/// the gain does not ripple with each cycle of a bass note (that ripple is distortion on the lows).
/// </summary>
public sealed class MasterBandComp
{
    public const double LevelRiseSec = 0.05, LevelFallSec = 2, MakeupSec = 3, HoldSec = 0.002;
    /// <summary>Under this band level (dBFS) nothing is learnt: silence neither pulls the level down nor the make-up up.</summary>
    const double GateDb = -60;
    /// <summary>The make-up never adds more than this.</summary>
    const double MakeupMaxDb = 12;
    /// <summary>A band turned off lets its make-up go over this long.</summary>
    const double MakeupOffSec = 0.2;

    readonly double fs;
    readonly int hold;
    readonly double holdFall, aRise, aFall, aMakeup, aMakeupOff;
    double y, held, level, avgGr;
    int age;

    public double Headroom { get; set; } = 10;
    public double Ratio { get; set; } = 1;
    public double Knee { get; set; } = 6;
    public double Attack { get; set; } = 0.02;
    public double Release { get; set; } = 0.15;

    /// <summary>Deepest reduction (dB, ≤ 0) since the last <see cref="TakeMeter"/>.</summary>
    double minGr;

    public MasterBandComp(double sampleRate)
    {
        fs = sampleRate;
        hold = Math.Max(1, (int)Math.Round(sampleRate * HoldSec));
        holdFall = Math.Exp(-1 / (HoldSec * sampleRate));
        aRise = 1 - Math.Exp(-1 / (LevelRiseSec * sampleRate));
        aFall = 1 - Math.Exp(-1 / (LevelFallSec * sampleRate));
        aMakeup = 1 - Math.Exp(-1 / (MakeupSec * sampleRate));
        aMakeupOff = 1 - Math.Exp(-1 / (MakeupOffSec * sampleRate));
    }

    public void Reset()
    {
        y = held = level = avgGr = 0;
        age = 0;
        minGr = 0;
    }

    public double TakeMeter()
    {
        double m = minGr;
        minGr = 0;
        return m;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        double H = Headroom, W = Math.Max(0, Knee), slope = 1 / Math.Max(1, Ratio) - 1;
        bool active = Ratio > 1;
        double aA = Math.Exp(-1 / (Math.Max(0.0001, Attack) * fs));
        double aR = Math.Exp(-1 / (Math.Max(0.005, Release) * fs));
        double gate = Math.Pow(10, GateDb / 10);
        double g = y, pk = held, lev = level, avg = avgGr, low = minGr;
        int k = age;
        for (int i = 0; i < left.Length; i++)
        {
            double l = left[i], r = right[i];
            double p = 0.5 * (l * l + r * r);
            lev += (p - lev) * (p > lev ? aRise : aFall);
            double x = Math.Max(Math.Abs(l), Math.Abs(r));
            if (x >= pk) { pk = x; k = 0; }
            else if (++k > hold) pk = Math.Max(x, pk * holdFall);
            double target = 0;
            if (active && lev > gate)
            {
                double over = (pk > 1e-6 ? 20 * Math.Log10(pk) : -120) - (10 * Math.Log10(lev) + H);
                if (2 * over <= -W) target = 0;
                else if (W > 0 && 2 * Math.Abs(over) < W) target = slope * (over + W / 2) * (over + W / 2) / (2 * W);
                else target = slope * over;
            }
            g = target < g ? aA * g + (1 - aA) * target : aR * g + (1 - aR) * target;
            // the make-up learns only while the band sounds; idle, it lets go quickly
            if (!active) avg += (0 - avg) * aMakeupOff;
            else if (lev > gate) avg += (g - avg) * aMakeup;
            double lin = Math.Pow(10, (g + Math.Min(MakeupMaxDb, -avg)) / 20);
            left[i] = (float)(l * lin);
            right[i] = (float)(r * lin);
            if (g < low) low = g;
        }
        y = g;
        held = pk;
        level = lev;
        avgGr = avg;
        age = k;
        minGr = low;
    }
}

/// <summary>
/// The master's soft clip, between the gain into the limiter (Empuje or the Destino's) and the limiter: it rounds the
/// fastest peaks (the pick, the strum's attack) so the limiter has less to do and pumps less. The curve is straight up
/// to a knee <see cref="KneeDb"/> under its ceiling and bends smoothly (tanh) into it. Two ceilings, the lower wins:
/// <list type="bullet">
/// <item>"Recorte" (0 to <see cref="MaxDb"/>): a fixed ceiling <see cref="MaxDb"/> − Recorte over the Techo, so the most =
/// the clipper takes everything the limiter would; 0 = none.</item>
/// <item>Auto (asked by the user: with a high final volume, share the work instead of leaving it all to the limiter): it
/// follows the loudest recent peak (instant attack, <see cref="AutoReleaseSec"/> to let go) and, of what that peak goes
/// over the Techo past <see cref="AutoFreeDb"/>, takes <see cref="AutoShare"/>, never more than
/// <see cref="AutoMaxCutDb"/>; the limiter takes the rest. A peak only a little over is left to the limiter alone.
/// It depends on the last fraction of a second only, like a compressor, so playback and the export cut the same.</item>
/// </list>
/// Anti-aliasing: first-order ADAA on what the curve takes away (the curve minus the straight line), so the bend's
/// harmonics are tamed without oversampling while the signal itself passes with no delay and no filtering: under the
/// knee it comes out bit for bit.
/// </summary>
public sealed class SoftClipper
{
    public const double MaxDb = 3;
    /// <summary>Where the bend starts, under the ceiling.</summary>
    public const double KneeDb = 3;
    /// <summary>Auto: a peak this far over the Techo the limiter takes alone (it handles it clean).</summary>
    public const double AutoFreeDb = 2;
    /// <summary>Auto: the share of the rest the clip takes.</summary>
    public const double AutoShare = 0.5;
    /// <summary>Auto: the most it cuts a peak; more would be heard as distortion on a guitar.</summary>
    public const double AutoMaxCutDb = 4;
    /// <summary>Auto: how long the loudest peak is remembered (the ceiling stays put through a strum).</summary>
    public const double AutoReleaseSec = 0.3;
    static readonly double Ln2 = Math.Log(2);
    static readonly double KneeLin = Math.Pow(10, -KneeDb / 20);
    static readonly double FreeLin = Math.Pow(10, AutoFreeDb / 20);
    static readonly double MaxCutLin = Math.Pow(10, -AutoMaxCutDb / 20);

    /// <summary><c>Ceiling</c> = the Recorte's (linear, +∞ = none); <c>Techo</c> = the limiter's (linear).</summary>
    sealed record Config(double Ceiling, bool Auto, double Techo);

    readonly double release;
    volatile Config config = new(double.PositiveInfinity, false, 1);
    double pl, pr, env;
    float minGain = 1;

    public SoftClipper(double sampleRate) => release = Math.Exp(-1 / (AutoReleaseSec * sampleRate));

    /// <summary>The clip's Recorte (dB, 0 = none), the limiter's Techo it is measured from and whether Auto shares the peaks.</summary>
    public void Set(double recorteDb, double techoDb, bool auto)
    {
        double r = Math.Clamp(double.IsFinite(recorteDb) ? recorteDb : 0, 0, MaxDb);
        double techo = double.IsFinite(techoDb) ? techoDb : MasterLimiter.CeilingDb;
        config = new Config(r > 0.01 ? Math.Pow(10, (techo + MaxDb - r) / 20) : double.PositiveInfinity, auto, Math.Pow(10, techo / 20));
    }

    /// <summary>As the master has it: Recorte and Auto with the master's and the clip's ON, against the Techo.</summary>
    public void Set(MasterParams p)
    {
        bool on = p.On && p.ClipOn;
        Set(on ? p.ClipDb : 0, p.LimCeilingDb, on);
    }

    public void Reset() => pl = pr = env = 0;

    /// <summary>For the meter: the deepest the clip has cut since the last call, in dB (≤ 0).</summary>
    public double TakeReductionDb()
    {
        float m = Interlocked.Exchange(ref minGain, 1f);
        return m < 1 ? 20 * Math.Log10(Math.Max(1e-6, m)) : 0;
    }

    static double LogCosh(double u)
    {
        double a = Math.Abs(u);
        return a + double.LogP1(Math.Exp(-2 * a)) - Ln2;
    }

    /// <summary>What the curve takes away at <paramref name="x"/> (the curve minus x): 0 under the knee <paramref name="k"/>.</summary>
    static double Cut(double x, double k, double d)
    {
        double a = Math.Abs(x);
        return a <= k ? 0 : Math.Sign(x) * (k + d * Math.Tanh((a - k) / d) - a);
    }

    /// <summary>Its antiderivative (even, 0 under the knee), for ADAA.</summary>
    static double CutIntegral(double x, double k, double d)
    {
        double a = Math.Abs(x);
        if (a <= k) return 0;
        double u = a - k;
        return k * u + d * d * LogCosh(u / d) - 0.5 * (a * a - k * k);
    }

    /// <summary>
    /// One sample through the curve: x plus what it takes away, anti-aliased against the previous sample. On a steep
    /// edge the average over the step under-cuts the sample, so it is held at the ceiling <paramref name="c"/>.
    /// </summary>
    static double Clip(double x, double p, double k, double d, double c)
    {
        if (Math.Abs(x) <= k && Math.Abs(p) <= k) return x;
        double dx = x - p;
        double y = x + (Math.Abs(dx) < 1e-6 ? Cut(0.5 * (x + p), k, d) : (CutIntegral(x, k, d) - CutIntegral(p, k, d)) / dx);
        return Math.Clamp(y, -c, c);
    }

    public void Process(Span<float> left, Span<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        if (n == 0) return;
        var c = config;
        if (!c.Auto && double.IsPositiveInfinity(c.Ceiling))
        {
            pl = left[n - 1];
            pr = right[n - 1];
            env = 0;
            return;
        }
        double rel = release, e = env, p0 = pl, p1 = pr, low = 1;
        double autoFrom = c.Techo * FreeLin;
        for (int i = 0; i < n; i++)
        {
            double xl = left[i], xr = right[i];
            double pk = Math.Max(Math.Abs(xl), Math.Abs(xr));
            e = pk >= e ? pk : e * rel;
            double ceil = c.Ceiling;
            if (c.Auto && e > autoFrom)
            {
                // the loudest recent peak comes down by AutoShare of its dB over AutoFreeDb, at most AutoMaxCutDb
                double a = e * Math.Max(MaxCutLin, Math.Pow(e / autoFrom, -AutoShare));
                if (a < ceil) ceil = a;
            }
            if (double.IsPositiveInfinity(ceil))
            {
                p0 = xl;
                p1 = xr;
                continue;
            }
            double k = ceil * KneeLin, d = ceil - k;
            double yl = Clip(xl, p0, k, d, ceil), yr = Clip(xr, p1, k, d, ceil);
            if (pk > k)
            {
                double g = Math.Min(Math.Abs(xl) > k ? Math.Abs(yl) / Math.Abs(xl) : 1, Math.Abs(xr) > k ? Math.Abs(yr) / Math.Abs(xr) : 1);
                if (g < low) low = g;
            }
            left[i] = (float)yl;
            right[i] = (float)yr;
            p0 = xl;
            p1 = xr;
        }
        env = e;
        pl = p0;
        pr = p1;
        if (low < minGain) minGain = (float)low;
    }
}

/// <summary>
/// The master's stereo width: mid/side, the side times <see cref="MasterParams.Width"/> (0 = mono, 1 = as it is, 2 =
/// twice as wide), and under <see cref="MasterParams.MonoBassHz"/> the side goes away (a high pass on it), so the lows
/// stay in the middle: a phone speaker or a club sums them and they do not cancel. Width glides per block.
/// Widening adds side energy and narrowing takes it away, so louder would pass for better: the output is scaled to keep
/// the energy the width alone changed (the mono lows are not made up for). The mix's mid and side energies are slow
/// averages (<see cref="EnergySec"/>, a plain mean right after a reset so it is right from the first second); the
/// width in the formula is the current one, so turning the knob is made up for at once, not a second later.
/// </summary>
public sealed class StereoWidth
{
    public const double MaxWidth = 2, MonoBassMaxHz = 250;
    /// <summary>How slowly the mid and side energies follow the mix: long enough not to pump with the music.</summary>
    const double EnergySec = 1.5;
    /// <summary>The make-up's limit either way (±6 dB): a near-pure side signal narrowed to mono has nothing to keep.</summary>
    const double MaxGain = 2;
    /// <summary>Below this mean energy (about -100 dBFS) the gain holds: silence says nothing about the mix.</summary>
    const double QuietEnergy = 1e-10;
    static readonly double ButterQDb = 20 * Math.Log10(Math.Sqrt(0.5));

    readonly double fs;
    readonly Biquad sideHp;
    volatile Biquad.Coeffs hp = Biquad.FlatCoeffs;
    volatile float target = 1;
    float width = 1;
    double midE, sideE, seenSec, gain = 1;

    public StereoWidth(double sampleRate)
    {
        fs = sampleRate;
        sideHp = new Biquad(sampleRate);
    }

    /// <summary>The level make-up for this width with the mix's current mid/side balance.</summary>
    double MakeUp(double w)
    {
        double e = midE + sideE;
        if (e < QuietEnergy) return gain;
        double g = Math.Sqrt(e / Math.Max(1e-30, midE + w * w * sideE));
        return Math.Clamp(g, 1 / MaxGain, MaxGain);
    }

    public void Set(double width, double monoBassHz)
    {
        target = (float)Math.Clamp(double.IsFinite(width) ? width : 1, 0, MaxWidth);
        hp = monoBassHz >= 20 ? Biquad.Compute(fs, new(BiquadType.Highpass, Math.Min(monoBassHz, MonoBassMaxHz), ButterQDb)) : Biquad.FlatCoeffs;
    }

    public void Reset()
    {
        sideHp.Reset();
        width = target;
        midE = sideE = seenSec = 0;
        gain = 1;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var c = hp;
        float w0 = width, w1 = target;
        int n = left.Length;
        if (n == 0) return;
        double g0 = gain, mSum = 0, sSum = 0;
        for (int i = 0; i < n; i++)
        {
            double l = left[i], r = right[i];
            double m = 0.5 * (l + r), s = sideHp.ProcessSample(0.5 * (l - r), 0, c);
            mSum += m * m;
            sSum += s * s;
            s *=w0 + (w1 - w0) * (i + 1) / n;
            left[i] = (float)(m + s);
            right[i] = (float)(m - s);
        }

        // energies: a plain mean until EnergySec has gone by, then an exponential one; silent blocks leave them be, so
        // a pause in the song neither forgets the mix nor slows the start
        bool first = seenSec == 0;
        if ((mSum + sSum) / n >= QuietEnergy)
        {
            double sec = n / fs;
            seenSec += sec;
            double a = Math.Max(sec / seenSec, 1 - Math.Exp(-sec / EnergySec));
            midE += a * (mSum / n - midE);
            sideE += a * (sSum / n - sideE);
        }
        double g1 = MakeUp(w1);
        if (first) g0 = g1; // nothing known before: no ramp from a guess
        gain = g1;
        width = w1;
        if (g0 == 1 && g1 == 1) return;
        for (int i = 0; i < n; i++)
        {
            float g = (float)(g0 + (g1 - g0) * (i + 1) / n);
            left[i] *= g;
            right[i] *= g;
        }
    }
}

/// <summary>
/// The master chain, between the returns and the limiter (<see cref="MixGraphBus"/>), the same in playback and export:
/// low cut + mud → three-band compressor (<see cref="Crossover3"/>, one <see cref="MasterBandComp"/> per band, each with its
/// self-set threshold and level-keeping make-up) and the Púa (<see cref="MasterPickTamer"/>) → lows, presence, air and tilt → Cinta (the tracks' tape
/// <see cref="Saturator"/>, gentler) → Ancho (<see cref="StereoWidth"/>). The soft clip and the limiter come after, in
/// <see cref="MixGraphBus"/>. Turning it on or off and each stage on or off crossfade over <see cref="FadeSec"/>, so nothing clicks. Off and faded out, it costs nothing. Buffers preallocated (<see cref="TrackDspChain.MaxBlockSize"/>):
/// <see cref="Process"/> never allocates.
/// </summary>
public sealed class MasterChain
{
    /// <summary>Crossfade of on/off and of each stage's ON.</summary>
    public const double FadeSec = 0.03;
    /// <summary>The master's Cinta at its most is the tracks' tape at this much: a mix wants less than a guitar.</summary>
    const double TapeScale = 0.5;
    /// <summary>The level the tape's drive is set for: where a mix usually sits (the tracks' tape reads its take's).</summary>
    const double MixLevelDb = -18;

    const int Max = TrackDspChain.MaxBlockSize;

    sealed record Config(bool On, bool EqOn, bool ColorOn, bool TapeOn, bool WidthOn, MasterBandShape[] Bands, double LowX, double HighX);
    readonly record struct MasterBandShape(double Headroom, double Ratio, double Knee, double Attack, double Release);

    readonly double fs;
    readonly MasterEq eq;
    readonly Crossover3 xo;
    readonly Saturator tape;
    readonly StereoWidth width;
    readonly MasterPickTamer pick;
    readonly MasterBandComp[] comps = new MasterBandComp[MasterBands.Count];
    readonly float[] dryL = new float[Max], dryR = new float[Max], tmpL = new float[Max], tmpR = new float[Max];
    readonly float[][] bandL = new float[MasterBands.Count][], bandR = new float[MasterBands.Count][];
    readonly float[] grMin = new float[MasterBands.Count];

    volatile Config config;
    Config? applied;
    bool running;
    double wet, eqWet, colorWet, tapeWet, widthWet;

    public MasterChain(double sampleRate)
    {
        fs = sampleRate;
        eq = new MasterEq(sampleRate);
        xo = new Crossover3(sampleRate);
        tape = new Saturator(sampleRate);
        width = new StereoWidth(sampleRate);
        pick = new MasterPickTamer(sampleRate);
        for (int i = 0; i < MasterBands.Count; i++)
        {
            comps[i] = new MasterBandComp(sampleRate);
            bandL[i] = new float[Max];
            bandR[i] = new float[Max];
        }
        config = Build(MasterParams.Off);
    }

    static Config Build(MasterParams p)
    {
        var bands = new MasterBandShape[MasterBands.Count];
        for (int i = 0; i < MasterBands.Count; i++)
        {
            var b = p.Band(i);
            var (headroom, ratio, knee, attack, release) = MasterBands.Shape(i, b.Amount);
            bool active = p.BandsOn && b.Amount > 0;
            bands[i] = new MasterBandShape(headroom, active ? ratio : 1, knee, attack, release);
        }
        var (lo, hi) = MasterBands.Crossovers(p);
        return new Config(p.On, p.EqOn, p.ColorOn, p.TapeOn, p.WidthOn, bands, lo, hi);
    }

    public void Set(MasterParams p)
    {
        eq.Set(p);
        double t = Math.Clamp(double.IsFinite(p.TapeAmount) ? p.TapeAmount : 0, 0, 1);
        tape.Set(SatParams.From(t > 0, SatMode.Tape, t * TapeScale, MixLevelDb));
        width.Set(p.Width, p.MonoBassHz);
        pick.Set(p.BandsOn ? p.PickAmount : 0);
        var c = Build(p);
        xo.Set(c.LowX, c.HighX);
        config = c;
    }

    public void Reset()
    {
        eq.Reset();
        xo.Reset();
        foreach (var c in comps) c.Reset();
        tape.Reset();
        width.Reset();
        pick.Reset();
        var cfg = config;
        wet = cfg.On ? 1 : 0;
        eqWet = cfg.EqOn ? 1 : 0;
        colorWet = cfg.ColorOn ? 1 : 0;
        tapeWet = cfg.TapeOn ? 1 : 0;
        widthWet = cfg.WidthOn ? 1 : 0;
        running = cfg.On;
    }

    /// <summary>For the band's meter: the deepest reduction since the last call, in dB (≤ 0).</summary>
    public double TakeReductionDb(int band) => Interlocked.Exchange(ref grMin[band], 0f);

    /// <summary>For the Púa's meter: its deepest cut since the last call, in dB (≤ 0).</summary>
    public double TakePickReductionDb() => pick.TakeReductionDb();

    static double Step(double from, double to, double step) => from < to ? Math.Min(to, from + step) : Math.Max(to, from - step);

    public void Process(Span<float> l, Span<float> r)
    {
        int n = Math.Min(l.Length, Max);
        if (n == 0) return;
        var c = config;
        if (!c.On && wet <= 0)
        {
            running = false;
            return;
        }
        if (!running)
        {
            // starting from off: old filter states would be a burst of the past; the fade-in hides the restart
            eq.Reset();
            xo.Reset();
            foreach (var comp in comps) comp.Reset();
            tape.Reset();
            width.Reset();
            pick.Reset();
            eqWet = c.EqOn ? 1 : 0;
            colorWet = c.ColorOn ? 1 : 0;
            tapeWet = c.TapeOn ? 1 : 0;
            widthWet = c.WidthOn ? 1 : 0;
            running = true;
        }
        if (!ReferenceEquals(c, applied))
        {
            for (int i = 0; i < MasterBands.Count; i++)
            {
                var b = c.Bands[i];
                var comp = comps[i];
                comp.Headroom = b.Headroom;
                comp.Ratio = b.Ratio;
                comp.Knee = b.Knee;
                comp.Attack = b.Attack;
                comp.Release = b.Release;
            }
            applied = c;
        }

        var x = l[..n];
        var y = r[..n];
        var dl = dryL.AsSpan(0, n);
        var dr = dryR.AsSpan(0, n);
        x.CopyTo(dl);
        y.CopyTo(dr);

        double fadeStep = 1.0 / Math.Max(1, FadeSec * fs);
        double eq0 = eqWet, eq1 = Step(eqWet, c.EqOn ? 1 : 0, fadeStep * n);
        eqWet = eq1;
        double co0 = colorWet, co1 = Step(colorWet, c.ColorOn ? 1 : 0, fadeStep * n);
        colorWet = co1;

        if (eq0 > 0 || eq1 > 0) Stage(x, y, eq0, eq1, StagePre);

        // three bands, each compressed (with its own make-up), added back
        var lowL = bandL[0].AsSpan(0, n); var lowR = bandR[0].AsSpan(0, n);
        var midL = bandL[1].AsSpan(0, n); var midR = bandR[1].AsSpan(0, n);
        var highL = bandL[2].AsSpan(0, n); var highR = bandR[2].AsSpan(0, n);
        xo.Split(x, y, lowL, lowR, midL, midR, highL, highR);
        x.Clear();
        y.Clear();
        for (int b = 0; b < MasterBands.Count; b++)
        {
            var bl = bandL[b].AsSpan(0, n);
            var br = bandR[b].AsSpan(0, n);
            comps[b].Process(bl, br);
            float gr = (float)comps[b].TakeMeter();
            if (gr < grMin[b]) grMin[b] = gr;
            for (int i = 0; i < n; i++)
            {
                x[i] += bl[i];
                y[i] += br[i];
            }
        }

        // the fourth band, Púa: part of the multiband, before the colour
        pick.Process(x, y);

        if (co0 > 0 || co1 > 0) Stage(x, y, co0, co1, StagePost);
        double ta0 = tapeWet, ta1 = Step(tapeWet, c.TapeOn ? 1 : 0, fadeStep * n);
        tapeWet = ta1;
        if (ta0 > 0 || ta1 > 0) Stage(x, y, ta0, ta1, StageTape);
        double wi0 = widthWet, wi1 = Step(widthWet, c.WidthOn ? 1 : 0, fadeStep * n);
        widthWet = wi1;
        if (wi0 > 0 || wi1 > 0) Stage(x, y, wi0, wi1, StageWidth);

        double w0 = wet, w1 = Step(wet, c.On ? 1 : 0, fadeStep * n);
        wet = w1;
        if (w0 >= 1 && w1 >= 1) return;
        for (int i = 0; i < n; i++)
        {
            double t = (i + 1) / (double)n;
            double w = w0 + (w1 - w0) * t;
            x[i] = (float)(x[i] * w + dl[i] * (1 - w));
            y[i] = (float)(y[i] * w + dr[i] * (1 - w));
        }
    }

    const int StagePre = 0, StagePost = 1, StageTape = 2, StageWidth = 3;

    /// <summary>One stage (by number: no delegates on the audio thread).</summary>
    void Run(Span<float> x, Span<float> y, int stage)
    {
        switch (stage)
        {
            case StagePre: eq.ProcessPre(x, y); break;
            case StagePost: eq.ProcessPost(x, y); break;
            case StageTape: tape.Process(x, y); break;
            default: width.Process(x, y); break;
        }
    }

    /// <summary>One stage, crossfaded with what came in while it turns on or off.</summary>
    void Stage(Span<float> x, Span<float> y, double w0, double w1, int stage)
    {
        int n = x.Length;
        if (w0 >= 1 && w1 >= 1)
        {
            Run(x, y, stage);
            return;
        }
        var tl = tmpL.AsSpan(0, n);
        var tr = tmpR.AsSpan(0, n);
        x.CopyTo(tl);
        y.CopyTo(tr);
        Run(x, y, stage);
        for (int i = 0; i < n; i++)
        {
            double w = w0 + (w1 - w0) * (i + 1) / n;
            x[i] = (float)(x[i] * w + tl[i] * (1 - w));
            y[i] = (float)(y[i] * w + tr[i] * (1 - w));
        }
    }
}
