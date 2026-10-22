namespace GuitarCam.Audio.Dsp;

/// <summary>
/// Port of <c>gc-ducker</c> (<c>GuitarCam/src/audio/fxWorklet.ts</c>): side-chained ducker for the delay
/// return. Drops the repeats up to <c>24 * Amount</c> dB while the dry signal plays and brings them back
/// up in the gaps. Zero latency.
/// </summary>
public sealed class Ducker
{
    readonly double eA, eR, gA, gR;
    double env;
    double g = 1;

    public double Amount { get; set; }

    public Ducker(double sampleRate)
    {
        eA = Math.Exp(-1.0 / (0.005 * sampleRate));
        eR = Math.Exp(-1.0 / (0.06 * sampleRate));
        gA = Math.Exp(-1.0 / (0.02 * sampleRate));
        gR = Math.Exp(-1.0 / (0.25 * sampleRate));
    }

    static double DbToLin(double db) => Math.Pow(10.0, db / 20.0);

    public void Reset()
    {
        env = 0;
        g = 1;
    }

    public void Process(
        Span<float> wetLeft,
        Span<float> wetRight,
        ReadOnlySpan<float> scLeft,
        ReadOnlySpan<float> scRight)
    {
        double maxDb = 24.0 * (float)Amount;
        double curEnv = env;
        double curG = g;
        for (int i = 0; i < wetLeft.Length; i++)
        {
            double x = i < scLeft.Length ? Math.Max(Math.Abs((double)scLeft[i]), Math.Abs((double)scRight[i])) : 0;
            curEnv = x > curEnv ? eA * curEnv + (1.0 - eA) * x : eR * curEnv + (1.0 - eR) * x;
            double target = 1.0;
            if (maxDb > 0 && curEnv > 0.0056)
            {
                double k = Math.Min(1.0, (20.0 * Math.Log10(curEnv) + 45.0) / 30.0);
                target = DbToLin(-maxDb * k);
            }
            curG = target < curG ? gA * curG + (1.0 - gA) * target : gR * curG + (1.0 - gR) * target;
            wetLeft[i] = (float)(wetLeft[i] * curG);
            wetRight[i] = (float)(wetRight[i] * curG);
        }
        env = curEnv;
        g = curG;
    }
}

public enum DelayMode { Stereo, PingPong }

/// <summary>
/// Port of <c>DelaySettings</c> in <c>GuitarCam/src/types.ts</c>.
/// </summary>
public readonly record struct DelayParams(
    DelayMode Mode,
    bool Sync,
    string Division,
    double TimeMs,
    double Feedback,
    double Tone,
    double LowCut,
    double Width,
    double Duck,
    double ReturnGain,
    bool Muted = false)
{
    public static readonly DelayParams Defaults = new(
        Mode: DelayMode.Stereo,
        Sync: true,
        Division: "1/8.",
        TimeMs: 375,
        Feedback: 0.35,
        Tone: 5000,
        LowCut: 250,
        Width: 1,
        Duck: 0.35,
        ReturnGain: 0.7,
        Muted: false);

    public static DelayMode ParseMode(string? mode) =>
        string.Equals(mode, "pingpong", StringComparison.OrdinalIgnoreCase) ? DelayMode.PingPong : DelayMode.Stereo;

    public static string ModeToString(DelayMode mode) =>
        mode == DelayMode.PingPong ? "pingpong" : "stereo";
}

public sealed record DelayDivisionDef(string Id, string Label, double Beats);

public sealed record DelayPresetDef(
    string Label,
    string Hint,
    DelayMode Mode,
    bool Sync,
    string Division,
    double TimeMs,
    double Feedback,
    double Tone,
    double LowCut,
    double Width,
    double Duck);

/// <summary>
/// Port of the delay bus in <c>GuitarCam/src/audio/mixGraph.ts</c> and presets in <c>Mixer.tsx</c>:
/// two delay lines (L/R) with high-pass + low-pass inside the feedback loop (so every repeat gets darker),
/// stereo or ping-pong routing, width matrix, side-chained <see cref="Ducker"/> and return gain.
/// </summary>
public sealed class DelayBus
{
    public const double MaxDelaySec = 2.5;

    public static readonly DelayDivisionDef[] Divisions =
    [
        new("1/2", "1/2", 2.0),
        new("1/4.", "1/4 punt.", 1.5),
        new("1/4", "1/4", 1.0),
        new("1/4t", "1/4 tres.", 2.0 / 3.0),
        new("1/8.", "1/8 punt.", 0.75),
        new("1/8", "1/8", 0.5),
        new("1/8t", "1/8 tres.", 1.0 / 3.0),
        new("1/16", "1/16", 0.25),
    ];

    public static readonly DelayPresetDef[] Presets =
    [
        new("Slapback", "Un solo rebote corto, rockabilly / country",
            DelayMode.Stereo, false, "1/8.", 110, 0.05, 3500, 150, 0.5, 0),
        new("Corchea", "Repeticiones a tempo, discretas",
            DelayMode.Stereo, true, "1/8", 375, 0.25, 4500, 200, 1, 0.3),
        new("Punteada", "Corchea con puntillo: el clásico para arpegios y punteos",
            DelayMode.Stereo, true, "1/8.", 375, 0.35, 5000, 250, 1, 0.35),
        new("Ping-pong", "Negras que rebotan entre izquierda y derecha",
            DelayMode.PingPong, true, "1/4", 375, 0.35, 4000, 200, 1, 0.3),
        new("Ambiente", "Largo y oscuro, de fondo para solos lentos",
            DelayMode.PingPong, true, "1/4.", 375, 0.5, 2500, 300, 1, 0.5),
    ];

    /// <summary>Port of <c>delayTimeSec</c> in <c>GuitarCam/src/types.ts</c>.</summary>
    public static double DelayTimeSec(DelayParams d, double bpm)
    {
        var div = Divisions[4]; // "1/8."
        foreach (var x in Divisions)
            if (x.Id == d.Division) { div = x; break; }
        double validBpm = double.IsFinite(bpm) && bpm > 0 ? bpm : 120;
        double sec = d.Sync ? (60.0 / Math.Max(20.0, validBpm)) * div.Beats : d.TimeMs / 1000.0;
        return Math.Clamp(sec, 0.01, MaxDelaySec);
    }

    public static string TimeLabel(DelayParams d)
    {
        if (d.Sync)
        {
            foreach (var x in Divisions)
                if (x.Id == d.Division) return x.Label;
            return d.Division;
        }
        return $"{Math.Round(d.TimeMs):0} ms";
    }

    public static string PresetOf(DelayParams d)
    {
        foreach (var p in Presets)
        {
            if (d.Mode != p.Mode || d.Sync != p.Sync) continue;
            if (d.Sync && d.Division != p.Division) continue;
            if (!d.Sync && Math.Abs(d.TimeMs - p.TimeMs) >= 0.01) continue;
            if (Math.Abs(d.Feedback - p.Feedback) < 0.01 &&
                Math.Abs(d.Tone - p.Tone) < 0.01 &&
                Math.Abs(d.LowCut - p.LowCut) < 0.01 &&
                Math.Abs(d.Width - p.Width) < 0.01 &&
                Math.Abs(d.Duck - p.Duck) < 0.01)
                return p.Label;
        }
        return "";
    }

    public static DelayParams ApplyPreset(DelayParams current, string label)
    {
        foreach (var p in Presets)
        {
            if (p.Label == label)
            {
                return current with
                {
                    Mode = p.Mode,
                    Sync = p.Sync,
                    Division = p.Sync ? p.Division : current.Division,
                    TimeMs = p.Sync ? current.TimeMs : p.TimeMs,
                    Feedback = p.Feedback,
                    Tone = p.Tone,
                    LowCut = p.LowCut,
                    Width = p.Width,
                    Duck = p.Duck,
                };
            }
        }
        return current;
    }

    sealed record DelayConfig(
        double TargetDelaySamplesL,
        double TargetDelaySamplesR,
        float RouteLL,
        float RouteLPing,
        float RouteRPing,
        float RouteRR,
        float FbSelf,
        float FbCross,
        float DirectGain,
        float BlendGain,
        float ReturnGain,
        double DuckAmount,
        Biquad.Coeffs HpCoeffs,
        Biquad.Coeffs LpCoeffs,
        bool Glide);

    readonly int fs;
    readonly int bufSize;
    readonly float[] bufL;
    readonly float[] bufR;
    readonly float[] scL = new float[8192];
    readonly float[] scR = new float[8192];
    readonly Biquad hp;
    readonly Biquad lp;
    readonly Ducker ducker;
    readonly double timeGlideCoeff;  // tau = 0.05 s (setTargetAtTime on delayTime)
    readonly float routeGlideCoeff;  // tau = 0.01 s (setTargetAtTime on delayRoute gains)
    readonly float gainGlideCoeff;   // tau = 0.03 s (setTargetAtTime on feedback/width/return gains)

    volatile DelayConfig config;

    int writeIdx;
    double curDelaySamplesL;
    double curDelaySamplesR;
    float curRouteLL = 1, curRouteLPing, curRouteRPing, curRouteRR = 1;
    float curFbSelf = 0.35f, curFbCross;
    float curDirectGain = 1, curBlendGain;
    float curReturnGain = 0.7f;
    bool initialized;

    public DelayBus(int sampleRate)
    {
        fs = sampleRate;
        bufSize = (int)Math.Ceiling((MaxDelaySec + 0.15) * sampleRate) + 4;
        bufL = new float[bufSize];
        bufR = new float[bufSize];
        hp = new Biquad(sampleRate);
        lp = new Biquad(sampleRate);
        ducker = new Ducker(sampleRate);
        timeGlideCoeff = 1.0 - Math.Exp(-1.0 / (0.05 * sampleRate));
        routeGlideCoeff = (float)(1.0 - Math.Exp(-1.0 / (0.01 * sampleRate)));
        gainGlideCoeff = (float)(1.0 - Math.Exp(-1.0 / (0.03 * sampleRate)));
        config = BuildConfig(DelayParams.Defaults, 120, glide: false);
        SnapTo(config);
        initialized = true;
    }

    static double F(double v) => (float)v;

    DelayConfig BuildConfig(DelayParams d, double bpm, bool glide)
    {
        double t = DelayTimeSec(d, bpm);
        double w = Math.Clamp(d.Width, 0, 1);
        bool ping = d.Mode == DelayMode.PingPong;
        float fb = (float)Math.Clamp(d.Feedback, 0, 0.9);

        double tL = F(t);
        double tR = F(ping ? t : Math.Min(MaxDelaySec, t + 0.012 * w));
        double targetL = Math.Clamp(tL * fs, 1, bufSize - 3);
        double targetR = Math.Clamp(tR * fs, 1, bufSize - 3);

        var hpCoeffs = Biquad.Compute(fs, new BiquadSpec(BiquadType.Highpass, F(Math.Clamp(d.LowCut, 20, 2000)), F(0.707), 0));
        var lpCoeffs = Biquad.Compute(fs, new BiquadSpec(BiquadType.Lowpass, F(Math.Clamp(d.Tone, 500, 20000)), F(0.707), 0));

        return new DelayConfig(
            TargetDelaySamplesL: targetL,
            TargetDelaySamplesR: targetR,
            RouteLL: ping ? 0f : 1f,
            RouteLPing: ping ? 0.5f : 0f,
            RouteRPing: ping ? 0.5f : 0f,
            RouteRR: ping ? 0f : 1f,
            FbSelf: ping ? 0f : fb,
            FbCross: ping ? fb : 0f,
            DirectGain: (float)((1.0 + w) / 2.0),
            BlendGain: (float)((1.0 - w) / 2.0),
            ReturnGain: d.Muted ? 0f : (float)Math.Clamp(d.ReturnGain, 0, 2),
            DuckAmount: Math.Clamp(d.Duck, 0, 1),
            HpCoeffs: hpCoeffs,
            LpCoeffs: lpCoeffs,
            Glide: glide);
    }

    void SnapTo(DelayConfig cfg)
    {
        curDelaySamplesL = cfg.TargetDelaySamplesL;
        curDelaySamplesR = cfg.TargetDelaySamplesR;
        curRouteLL = cfg.RouteLL;
        curRouteLPing = cfg.RouteLPing;
        curRouteRPing = cfg.RouteRPing;
        curRouteRR = cfg.RouteRR;
        curFbSelf = cfg.FbSelf;
        curFbCross = cfg.FbCross;
        curDirectGain = cfg.DirectGain;
        curBlendGain = cfg.BlendGain;
        curReturnGain = cfg.ReturnGain;
    }

    public void Set(DelayParams d, double bpm, bool glide = false)
    {
        config = BuildConfig(d, bpm, glide);
    }

    public void Reset()
    {
        Array.Clear(bufL);
        Array.Clear(bufR);
        writeIdx = 0;
        hp.Reset();
        lp.Reset();
        ducker.Reset();
        SnapTo(config);
    }

    static float ReadDelay(float[] buf, int bufSize, int writePos, double delaySamples)
    {
        int dInt = (int)delaySamples;
        double frac = delaySamples - dInt;
        int r0 = writePos - dInt;
        if (r0 < 0) r0 += bufSize;
        if (frac <= 1e-7) return buf[r0];
        int r1 = r0 - 1;
        if (r1 < 0) r1 += bufSize;
        return (float)(buf[r0] * (1.0 - frac) + buf[r1] * frac);
    }

    static float StepSmooth(float cur, float target, float coeff) =>
        Math.Abs(cur - target) > 1e-6f ? cur + coeff * (target - cur) : target;

    /// <summary>
    /// Processes the summed delay send <paramref name="left"/> / <paramref name="right"/> in place
    /// into the stereo delay return (ready to add into the master bus).
    /// </summary>
    public void Process(Span<float> left, Span<float> right)
    {
        int total = left.Length;
        int offset = 0;
        while (offset < total)
        {
            int n = Math.Min(scL.Length, total - offset);
            ProcessChunk(left.Slice(offset, n), right.Slice(offset, n));
            offset += n;
        }
    }

    void ProcessChunk(Span<float> left, Span<float> right)
    {
        var cfg = config;
        if (!cfg.Glide || !initialized)
        {
            SnapTo(cfg);
            initialized = true;
        }

        int n = left.Length;
        left.CopyTo(scL.AsSpan(0, n));
        right.CopyTo(scR.AsSpan(0, n));

        var hpC = cfg.HpCoeffs;
        var lpC = cfg.LpCoeffs;
        bool glide = cfg.Glide;

        for (int i = 0; i < n; i++)
        {
            if (glide)
            {
                if (Math.Abs(curDelaySamplesL - cfg.TargetDelaySamplesL) > 1e-6)
                    curDelaySamplesL += timeGlideCoeff * (cfg.TargetDelaySamplesL - curDelaySamplesL);
                else
                    curDelaySamplesL = cfg.TargetDelaySamplesL;

                if (Math.Abs(curDelaySamplesR - cfg.TargetDelaySamplesR) > 1e-6)
                    curDelaySamplesR += timeGlideCoeff * (cfg.TargetDelaySamplesR - curDelaySamplesR);
                else
                    curDelaySamplesR = cfg.TargetDelaySamplesR;

                curRouteLL = StepSmooth(curRouteLL, cfg.RouteLL, routeGlideCoeff);
                curRouteLPing = StepSmooth(curRouteLPing, cfg.RouteLPing, routeGlideCoeff);
                curRouteRPing = StepSmooth(curRouteRPing, cfg.RouteRPing, routeGlideCoeff);
                curRouteRR = StepSmooth(curRouteRR, cfg.RouteRR, routeGlideCoeff);

                curFbSelf = StepSmooth(curFbSelf, cfg.FbSelf, gainGlideCoeff);
                curFbCross = StepSmooth(curFbCross, cfg.FbCross, gainGlideCoeff);
                curDirectGain = StepSmooth(curDirectGain, cfg.DirectGain, gainGlideCoeff);
                curBlendGain = StepSmooth(curBlendGain, cfg.BlendGain, gainGlideCoeff);
            }

            float inL = scL[i];
            float inR = scR[i];

            float dL = ReadDelay(bufL, bufSize, writeIdx, curDelaySamplesL);
            float dR = ReadDelay(bufR, bufSize, writeIdx, curDelaySamplesR);

            float sumL = inL * curRouteLL + inL * curRouteLPing + inR * curRouteRPing + dL * curFbSelf + dR * curFbCross;
            float sumR = inR * curRouteRR + dR * curFbSelf + dL * curFbCross;

            float filtL = (float)lp.ProcessSample((float)hp.ProcessSample(sumL, 0, hpC), 0, lpC);
            float filtR = (float)lp.ProcessSample((float)hp.ProcessSample(sumR, 1, hpC), 1, lpC);

            bufL[writeIdx] = filtL;
            bufR[writeIdx] = filtR;
            if (++writeIdx == bufSize) writeIdx = 0;

            left[i] = dL * curDirectGain + dR * curBlendGain;
            right[i] = dR * curDirectGain + dL * curBlendGain;
        }

        ducker.Amount = cfg.DuckAmount;
        ducker.Process(left, right, scL.AsSpan(0, n), scR.AsSpan(0, n));

        for (int i = 0; i < n; i++)
        {
            if (glide) curReturnGain = StepSmooth(curReturnGain, cfg.ReturnGain, gainGlideCoeff);
            left[i] *= curReturnGain;
            right[i] *= curReturnGain;
        }
    }
}
