namespace GuitarCam.Audio.Dsp;

/// <summary>
/// One effect in a track's insert slots (<see cref="TrackDspChain"/>). The chain only knows this shape, so a built-in
/// effect and a future VST3 plugin are both just a kind of slot. Runs on the audio thread: <see cref="Process"/> never
/// allocates, locks or throws. Its settings live in the project (<c>InsertSlot</c>); <see cref="TrackInserts.Apply"/>
/// hands them over from the UI thread as whole immutable values, like every other effect.
/// </summary>
public interface ITrackInsert
{
    /// <summary>The slot kind (<see cref="TrackInserts.Sat"/>…), as saved in the project.</summary>
    string Kind { get; }
    /// <summary>How late its output comes out. Zero for every built-in effect; a plugin's delay gets compensated later.</summary>
    int LatencySamples { get; }
    void Process(Span<float> left, Span<float> right);
    void Reset();
    /// <summary>Its gain reduction since the last call (dB, &lt;= 0), for the strip's meter; 0 for effects that don't reduce.</summary>
    double TakeGrDb();
}

/// <summary>The Vida saturator as a slot.</summary>
public sealed class SatInsert(int sampleRate) : ITrackInsert
{
    public Saturator Sat { get; } = new(sampleRate);
    public string Kind => TrackInserts.Sat;
    public int LatencySamples => 0;

    /// <summary>The web ran its parameters as 32-bit floats: rounded the same way, the native sounds the same.</summary>
    public void Set(SatParams p) => Sat.Set(p with
    {
        Drive = (float)p.Drive,
        Bias = (float)p.Bias,
        Tone = (float)p.Tone,
        ToneMix = (float)p.ToneMix,
        Bump = (float)p.Bump,
    });

    public void Process(Span<float> left, Span<float> right) => Sat.Process(left, right);
    public void Reset() => Sat.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>The compressor with the small EQ its presets bring (before and after it) as one slot.</summary>
public sealed class CompInsert(int sampleRate) : ITrackInsert
{
    public CompEqFilter Eq { get; } = new(sampleRate);
    public Compressor Comp { get; } = new(sampleRate);
    public string Kind => TrackInserts.Comp;
    public int LatencySamples => 0;

    static double F(double v) => (float)v;

    public void Set(CompState c)
    {
        Eq.Set(c.On, c.Eq);
        Comp.Bypass = !c.On;
        Comp.Threshold = F(c.Threshold);
        Comp.Ratio = F(c.Ratio);
        Comp.Knee = F(c.Knee);
        Comp.Attack = F(c.Attack);
        Comp.Release = F(c.Release);
        Comp.Makeup = F(c.Makeup);
        Comp.Limit = c.Limit;
        Comp.Ceiling = F(CompPresets.TrackLimitCeilingDb);
    }

    public void Process(Span<float> left, Span<float> right) => Eq.ProcessWith(Comp, left, right);

    public void Reset()
    {
        Eq.Reset();
        Comp.Reset();
    }

    public double TakeGrDb() => Comp.TakeMeter();
}

/// <summary>The 6-band track EQ as a slot.</summary>
public sealed class EqInsert(int sampleRate) : ITrackInsert
{
    public TrackEq Eq { get; } = new(sampleRate);
    public string Kind => TrackInserts.Eq;
    public int LatencySamples => 0;

    public void Set(bool on, ReadOnlySpan<EqBandSpec> bands) => Eq.Set(on, bands);
    public void Process(Span<float> left, Span<float> right) => Eq.Process(left, right);
    public void Reset() => Eq.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>
/// One slot's settings as the engine takes them (the export builds a chain from these; the live engine updates its
/// inserts with them). <see cref="Id"/> ties it to its insert, so an edit or a reorder keeps the effect's state.
/// </summary>
public sealed record InsertSpec(string Id, string Kind)
{
    public SatParams Sat { get; init; } = SatParams.From(false, SatMode.Tube, 0.3);
    public CompState Comp { get; init; } = CompState.Defaults;
    public bool EqOn { get; init; }
    public EqBandSpec[] EqBands { get; init; } = TrackEq.DefaultBands;
}

/// <summary>
/// The kinds of insert slot and how a list of <see cref="InsertSpec"/> becomes the chain's inserts. A new channel gets
/// <see cref="DefaultOrder"/>, the order the chain was fixed in before slots existed, so an old project sounds the same.
/// </summary>
public static class TrackInserts
{
    public const string Sat = "sat";
    public const string Comp = "comp";
    public const string Eq = "eq";
    public static readonly string[] DefaultOrder = [Sat, Comp, Eq];

    public static bool IsKind(string? kind) => kind is Sat or Comp or Eq;

    public static ITrackInsert Create(string kind, int sampleRate) => kind switch
    {
        Sat => new SatInsert(sampleRate),
        Comp => new CompInsert(sampleRate),
        Eq => new EqInsert(sampleRate),
        _ => throw new ArgumentException($"unknown insert kind '{kind}'", nameof(kind)),
    };

    public static void Apply(ITrackInsert insert, InsertSpec spec)
    {
        switch (insert)
        {
            case SatInsert s: s.Set(spec.Sat); break;
            case CompInsert c: c.Set(spec.Comp); break;
            case EqInsert e: e.Set(spec.EqOn, spec.EqBands); break;
        }
    }

    /// <summary>
    /// The inserts for <paramref name="specs"/>, in their order: an insert of <paramref name="current"/> with the same id
    /// and kind is reused (its state carries on), the rest are new. Returns <paramref name="current"/> itself when nothing
    /// was added, removed or moved, so the caller knows the chain doesn't need swapping. UI thread.
    /// </summary>
    public static ITrackInsert[] Build(IReadOnlyList<InsertSpec> specs, ITrackInsert[] current, string[] currentIds,
        int sampleRate, out string[] ids)
    {
        bool same = specs.Count == current.Length;
        var next = new ITrackInsert[specs.Count];
        ids = new string[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            int k = Array.IndexOf(currentIds, s.Id);
            var insert = k >= 0 && current[k].Kind == s.Kind ? current[k] : Create(s.Kind, sampleRate);
            Apply(insert, s);
            next[i] = insert;
            ids[i] = s.Id;
            same &= k == i && insert == current[i];
        }
        if (same)
        {
            ids = currentIds;
            return current;
        }
        return next;
    }

    /// <summary>The fixed chain from before slots (Vida → compressor → EQ), for specs that bring no slots.</summary>
    public static InsertSpec[] Legacy(SatParams sat, CompState comp, bool eqOn, EqBandSpec[] eqBands) =>
    [
        new("sat", Sat) { Sat = sat },
        new("comp", Comp) { Comp = comp },
        new("eq", Eq) { EqOn = eqOn, EqBands = eqBands },
    ];
}
