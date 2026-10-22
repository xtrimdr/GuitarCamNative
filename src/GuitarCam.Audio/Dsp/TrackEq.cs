using System.Globalization;

namespace GuitarCam.Audio.Dsp;

/// <summary>
/// Port of <c>EqBand</c> in <c>GuitarCam/src/audio/eq.ts</c>.
/// </summary>
public readonly record struct EqBandSpec(
    bool On,
    BiquadType Type,
    double Freq,
    double Gain = 0,
    double Q = 1,
    int Slope = 12)
{
    public static EqBandSpec FromStrings(bool on, string? type, double freq, double gain, double q, int slope, BiquadType fallbackType = BiquadType.Peaking) =>
        new(
            On: on,
            Type: BiquadSpec.ParseType(type, fallbackType),
            Freq: Math.Clamp(double.IsFinite(freq) ? freq : 1000, TrackEq.MinHz, TrackEq.MaxHz),
            Gain: Math.Clamp(double.IsFinite(gain) ? gain : 0, -TrackEq.GainMax, TrackEq.GainMax),
            Q: Math.Clamp(double.IsFinite(q) ? q : 1, TrackEq.QMin, TrackEq.QMax),
            Slope: slope == 24 ? 24 : 12);
}

public sealed record EqPresetDef(string Id, string Label, string Hint, EqBandSpec[] Bands);

/// <summary>
/// Port of <c>GuitarCam/src/audio/eq.ts</c>: per-track 6-band parametric EQ (low cut, 4 bells, high cut),
/// 2 biquad stages per band (12 Biquads in series, zero latency, zero allocations in <see cref="Process"/>).
/// </summary>
public sealed class TrackEq
{
    public const double MinHz = 20;
    public const double MaxHz = 20000;
    public const double GainMax = 18;
    public const double QMin = 0.3;
    public const double QMax = 12;
    public const int BandCount = 6;
    public const int StagesPerBand = 2;
    public const int TotalStages = BandCount * StagesPerBand;

    static readonly double Butter12 = 20.0 * Math.Log10(Math.Sqrt(0.5));
    static readonly double Butter24A = 20.0 * Math.Log10(0.5412);
    static readonly double Butter24B = 20.0 * Math.Log10(1.3066);

    public static readonly EqBandSpec[] DefaultBands =
    [
        new(false, BiquadType.Highpass, 80, 0, 0.7, 12),
        new(false, BiquadType.Peaking, 250, 0, 1, 12),
        new(false, BiquadType.Peaking, 800, 0, 1, 12),
        new(false, BiquadType.Peaking, 2000, 0, 1, 12),
        new(false, BiquadType.Peaking, 4500, 0, 1, 12),
        new(false, BiquadType.Lowpass, 12000, 0, 0.7, 12),
    ];

    public static readonly EqPresetDef[] Presets =
    [
        new("flat", "Plano", "Todas las bandas apagadas", [.. DefaultBands]),
        new("rhythm", "Rítmica", "Saca graves que embarran y un poco de barro en 250 Hz",
        [
            DefaultBands[0] with { On = true, Freq = 100, Slope = 24 },
            DefaultBands[1] with { On = true, Freq = 250, Gain = -2.5, Q = 1.2 },
            DefaultBands[2],
            DefaultBands[3],
            DefaultBands[4],
            DefaultBands[5],
        ]),
        new("lead", "Punteo", "Doma lo chirriante (3–4 kHz) y corta el fizz de arriba",
        [
            DefaultBands[0] with { On = true, Freq = 120 },
            DefaultBands[1],
            DefaultBands[2],
            DefaultBands[3],
            DefaultBands[4] with { On = true, Freq = 3500, Gain = -3, Q = 1.4 },
            DefaultBands[5] with { On = true, Freq = 9000 },
        ]),
        new("acoustic", "Acústica", "Menos retumbe de caja, un poco de brillo",
        [
            DefaultBands[0] with { On = true, Freq = 80, Slope = 24 },
            DefaultBands[1] with { On = true, Freq = 200, Gain = -3, Q = 1 },
            DefaultBands[2],
            DefaultBands[3],
            DefaultBands[4] with { On = true, Type = BiquadType.Highshelf, Freq = 8000, Gain = 2 },
            DefaultBands[5],
        ]),
        new("front", "Al frente", "Más presencia para que la guitarra se escuche adelante",
        [
            DefaultBands[0] with { On = true, Freq = 90 },
            DefaultBands[1],
            DefaultBands[2],
            DefaultBands[3] with { On = true, Freq = 2200, Gain = 2.5, Q = 0.9 },
            DefaultBands[4],
            DefaultBands[5],
        ]),
    ];

    sealed record EqConfig(bool Active, Biquad.Coeffs[] StageCoeffs);

    readonly double fs;
    readonly Biquad[] stages = new Biquad[TotalStages];
    volatile EqConfig config;

    public TrackEq(double sampleRate)
    {
        fs = sampleRate;
        var flat = new Biquad.Coeffs[TotalStages];
        for (int i = 0; i < TotalStages; i++)
        {
            stages[i] = new Biquad(sampleRate);
            flat[i] = Biquad.FlatCoeffs;
        }
        config = new EqConfig(false, flat);
    }

    public static bool IsCut(BiquadType t) => t is BiquadType.Highpass or BiquadType.Lowpass;

    public static bool HasGain(BiquadType t) => !IsCut(t);

    public static string TypeLabel(BiquadType t) => t switch
    {
        BiquadType.Highpass => "Corte de graves",
        BiquadType.Lowshelf => "Estante grave",
        BiquadType.Peaking => "Campana",
        BiquadType.Highshelf => "Estante agudo",
        BiquadType.Lowpass => "Corte de agudos",
        _ => "Campana",
    };

    static double F(double v) => (float)v;

    /// <summary>Port of <c>bandSpecs</c> in <c>eq.ts</c>.</summary>
    public static (BiquadSpec Stage0, BiquadSpec Stage1) BandSpecs(EqBandSpec b, bool eqOn = true)
    {
        if (!eqOn || !b.On) return (BiquadSpec.Flat, BiquadSpec.Flat);
        double freq = F(Math.Clamp(b.Freq, MinHz, MaxHz));
        if (IsCut(b.Type))
        {
            if (b.Slope == 24)
            {
                return (
                    new BiquadSpec(b.Type, freq, F(Butter24A), 0),
                    new BiquadSpec(b.Type, freq, F(Butter24B), 0));
            }
            return (
                new BiquadSpec(b.Type, freq, F(Butter12), 0),
                BiquadSpec.Flat);
        }
        return (
            new BiquadSpec(b.Type, freq, F(Math.Clamp(b.Q, QMin, QMax)), F(Math.Clamp(b.Gain, -GainMax, GainMax))),
            BiquadSpec.Flat);
    }

    /// <summary>Port of <c>eqSpecs</c> in <c>eq.ts</c>.</summary>
    public static void GetSpecs(bool eqOn, ReadOnlySpan<EqBandSpec> bands, Span<BiquadSpec> dst)
    {
        for (int i = 0; i < BandCount; i++)
        {
            var b = i < bands.Length ? bands[i] : DefaultBands[i];
            var (s0, s1) = BandSpecs(b, eqOn);
            dst[i * 2] = s0;
            dst[i * 2 + 1] = s1;
        }
    }

    /// <summary>Port of <c>eqActive</c> in <c>eq.ts</c>.</summary>
    public static bool IsActive(bool eqOn, ReadOnlySpan<EqBandSpec> bands)
    {
        if (!eqOn) return false;
        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            if (b.On && (IsCut(b.Type) || Math.Abs(b.Gain) >= 0.05)) return true;
        }
        return false;
    }

    public static EqBandSpec[] ApplyPreset(string id)
    {
        foreach (var p in Presets)
            if (p.Id == id) return [.. p.Bands];
        return [.. Presets[0].Bands];
    }

    /// <summary>Port of <c>eqPresetOf</c> in <c>eq.ts</c>.</summary>
    public static string PresetOf(ReadOnlySpan<EqBandSpec> bands)
    {
        if (bands.Length < BandCount) return "";
        foreach (var p in Presets)
        {
            bool match = true;
            for (int i = 0; i < BandCount; i++)
            {
                if (!SameBand(p.Bands[i], bands[i])) { match = false; break; }
            }
            if (match) return p.Id;
        }
        return "";
    }

    static bool SameBand(EqBandSpec a, EqBandSpec b) =>
        a.On == b.On &&
        (!a.On ||
            (a.Type == b.Type &&
             Math.Abs(a.Freq - b.Freq) < 0.5 &&
             Math.Abs(a.Gain - b.Gain) < 0.05 &&
             Math.Abs(a.Q - b.Q) < 0.01 &&
             a.Slope == b.Slope));

    public static string FmtHz(double v) =>
        v >= 1000
            ? $"{(v / 1000.0).ToString(v >= 10000 ? "0" : "0.0", CultureInfo.InvariantCulture)}k"
            : $"{Math.Round(v).ToString("0", CultureInfo.InvariantCulture)}";

    public void Set(bool eqOn, ReadOnlySpan<EqBandSpec> bands)
    {
        Span<BiquadSpec> specs = stackalloc BiquadSpec[TotalStages];
        GetSpecs(eqOn, bands, specs);
        var stageCoeffs = new Biquad.Coeffs[TotalStages];
        bool any = false;
        for (int i = 0; i < TotalStages; i++)
        {
            stageCoeffs[i] = Biquad.Compute(fs, specs[i]);
            if (!specs[i].IsFlat) any = true;
        }
        config = new EqConfig(any, stageCoeffs);
    }

    public void Reset()
    {
        for (int i = 0; i < TotalStages; i++) stages[i].Reset();
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var cfg = config;
        if (!cfg.Active) return;
        for (int i = 0; i < TotalStages; i++)
            stages[i].Process(left, right, cfg.StageCoeffs[i]);
    }
}
