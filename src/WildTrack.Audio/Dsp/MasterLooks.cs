namespace WildTrack.Audio.Dsp;

/// <summary>
/// What a look sets on the master: the EQ (dB, <see cref="LowCutHz"/> 0 = none), how much each band compresses, the
/// Cinta, the Ancho (<see cref="MonoBassHz"/> 0 = none) and the Púa (<see cref="MasterPickTamer"/>). The limiter,
/// the clip and the crossovers are not a look's: they belong to the Salida and to the mix.
/// </summary>
public sealed record MasterLook(string Id, string Label, double LowCutHz, double MudDb, double LowsDb, double PresenceDb,
    double AirDb, double TiltDb, double Low, double Mid, double High, double Tape, double Width, double MonoBassHz, double Pick);

/// <summary>
/// The master's looks (asked by the user, with how each one sounds): a starting point picked by ear, with a single
/// "Cuánto" that moves everything. Every look is Natural plus what makes it itself, and Cuánto goes from Natural (0)
/// to the whole look (1); Natural's own Cuánto goes from nothing at all to Natural. Picking one writes its values into
/// the knobs (<see cref="Apply"/>), so it can be finished by hand.
/// </summary>
public static class MasterLooks
{
    /// <summary>No look: the knobs as the user left them.</summary>
    public const string Custom = "";
    public const double DefaultAmount = 1;

    /// <summary>Everything off: what Natural's Cuánto starts from.</summary>
    static readonly MasterLook Flat = new("flat", "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0);

    public static readonly MasterLook Natural = new("natural", "Natural", 30, -1, 0, 0, 0.5, 0, 0.3, 0.3, 0.3, 0, 1, 0, 0);

    /// <summary>The looks, in the order the window lists them. It shows <c>Label</c> through <c>Loc</c> ("master.look.{id}").</summary>
    public static readonly IReadOnlyList<MasterLook> All =
    [
        Natural,
        // glue: the bands work harder and the tape rounds it, everything closer together
        Natural with { Id = "glue", Label = "Pegadito", Low = 0.6, Mid = 0.55, High = 0.45, Tape = 0.3 },
        // air and presence up, the highs compressed a little more so the brightness does not sting
        Natural with { Id = "bright", Label = "Brillante", PresenceDb = 1.5, AirDb = 3, TiltDb = 1, High = 0.4 },
        // more lows and tape, the top tilted down and its band softer
        Natural with { Id = "warm", Label = "Cálido", LowsDb = 2, AirDb = -1, TiltDb = -1.5, High = 0.5, Tape = 0.5 },
        // the mids forward: presence, less mud, the mid band holding them, narrower with the lows in the middle
        Natural with { Id = "front", Label = "Al frente", MudDb = -2, PresenceDb = 2.5, Mid = 0.6, Width = 0.9, MonoBassHz = 120 },
        // the pick's attack (2–5 kHz) held down only when it hits; nothing else changes
        Natural with { Id = "pick", Label = "Domar la púa", Pick = 0.7 },
    ];

    public static MasterLook? Get(string? id) => All.FirstOrDefault(l => l.Id == id);

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>A cut-off frequency at <paramref name="t"/> of the way: under <paramref name="min"/> it is off (0).</summary>
    static double Hz(double a, double b, double t, double min)
    {
        double f = Lerp(a, b, t);
        return f < min ? 0 : Math.Round(f);
    }

    /// <summary>What <paramref name="look"/> at <paramref name="amount"/> (0 to 1) sets: from Natural (or nothing, for Natural itself).</summary>
    public static MasterLook At(MasterLook look, double amount)
    {
        double t = Math.Clamp(double.IsFinite(amount) ? amount : DefaultAmount, 0, 1);
        var from = look.Id == Natural.Id ? Flat : Natural;
        static double R(double v, double step) => Math.Round(v / step) * step;
        return look with
        {
            LowCutHz = Hz(from.LowCutHz, look.LowCutHz, t, MasterEq.LowCutMin),
            MudDb = R(Lerp(from.MudDb, look.MudDb, t), 0.1),
            LowsDb = R(Lerp(from.LowsDb, look.LowsDb, t), 0.1),
            PresenceDb = R(Lerp(from.PresenceDb, look.PresenceDb, t), 0.1),
            AirDb = R(Lerp(from.AirDb, look.AirDb, t), 0.1),
            TiltDb = R(Lerp(from.TiltDb, look.TiltDb, t), 0.1),
            Low = R(Lerp(from.Low, look.Low, t), 0.01),
            Mid = R(Lerp(from.Mid, look.Mid, t), 0.01),
            High = R(Lerp(from.High, look.High, t), 0.01),
            Tape = R(Lerp(from.Tape, look.Tape, t), 0.01),
            Width = R(Lerp(from.Width, look.Width, t), 0.01),
            MonoBassHz = Hz(from.MonoBassHz, look.MonoBassHz, t, 40),
            Pick = R(Lerp(from.Pick, look.Pick, t), 0.01),
        };
    }

    /// <summary>The master's settings with the look at that amount in them (the rest as it was).</summary>
    public static MasterParams Apply(MasterParams p, MasterLook look, double amount)
    {
        var v = At(look, amount);
        return p with
        {
            LowCutHz = v.LowCutHz, MudDb = v.MudDb, LowsDb = v.LowsDb, PresenceDb = v.PresenceDb, AirDb = v.AirDb, TiltDb = v.TiltDb,
            Low = new(v.Low), Mid = new(v.Mid), High = new(v.High), TapeAmount = v.Tape, Width = v.Width, MonoBassHz = v.MonoBassHz,
            PickAmount = v.Pick,
        };
    }
}

/// <summary>
/// The master's fourth band, Púa (asked by the user for "Domar la púa"): a dynamic cut at 2–5 kHz that only acts when
/// the pick hits. A band pass (<see cref="CenterHz"/>, <see cref="Q"/>, 0 dB at its centre) is both the detector and
/// what gets taken away: out = in − k·band, so k = 0 is the input bit for bit and k = 1 a notch; k follows the
/// reduction. Like the multiband's bands it sets its own threshold, <c>Headroom</c> dB over that band's own level as
/// it plays, so it catches what jumps out of it (the attack) and leaves the sustained tone alone. Fast (attack
/// <see cref="AttackSec"/>), no make-up: it is there to take the sting off. Zero latency, nothing allocated.
/// </summary>
public sealed class MasterPickTamer
{
    public const double CenterHz = 3200, Q = 1.05;
    /// <summary><c>LevelSec</c>: the band's level is its mean power over about this long, both ways, so the attacks
    /// (a few ms each) barely move it and stand out of it; from silence it takes the first sound in <c>LevelStartSec</c>.</summary>
    public const double AttackSec = 0.0005, HoldSec = 0.001, LevelSec = 0.4, LevelStartSec = 0.01;
    /// <summary>The deepest it cuts (dB).</summary>
    public const double MaxCutDb = 8;
    const double KneeDb = 4, GateDb = -60;

    readonly double fs;
    readonly Biquad.Coeffs bp;
    readonly Biquad filter;
    readonly int hold;
    readonly double holdFall, aLevel, aStart, aAttack;
    double g, held, level;
    int age;
    float minGr;
    bool idle = true;

    /// <summary>The band's curve: headroom over its level (dB), ratio, release (s), from "Cuánto"; ratio 1 = off.</summary>
    volatile Shape shape = new(10, 1, 0.08);
    sealed record Shape(double Headroom, double Ratio, double Release);

    public MasterPickTamer(double sampleRate)
    {
        fs = sampleRate;
        filter = new Biquad(sampleRate);
        double w0 = 2 * Math.PI * CenterHz / sampleRate, alpha = Math.Sin(w0) / (2 * Q), a0 = 1 + alpha;
        // RBJ band pass with 0 dB at the centre, so in − band is the RBJ notch
        bp = new Biquad.Coeffs(new BiquadSpec(BiquadType.Peaking, CenterHz, Q), alpha / a0, 0, -alpha / a0, -2 * Math.Cos(w0) / a0, (1 - alpha) / a0);
        hold = Math.Max(1, (int)Math.Round(sampleRate * HoldSec));
        holdFall = Math.Exp(-1 / (HoldSec * sampleRate));
        aLevel = 1 - Math.Exp(-1 / (LevelSec * sampleRate));
        aStart = 1 - Math.Exp(-1 / (LevelStartSec * sampleRate));
        aAttack = Math.Exp(-1 / (AttackSec * sampleRate));
    }

    /// <summary>"Cuánto" (0 = off): less headroom, a higher ratio and a quicker release the more.</summary>
    public void Set(double amount)
    {
        double t = Math.Clamp(double.IsFinite(amount) ? amount : 0, 0, 1);
        shape = t <= 0 ? new Shape(10, 1, 0.08) : new Shape(8 - 5 * t, 2 + 2 * t, 0.08 - 0.03 * t);
    }

    public bool Active => shape.Ratio > 1;

    public void Reset()
    {
        filter.Reset();
        g = held = level = 0;
        age = 0;
    }

    /// <summary>For the meter: the deepest cut since the last call, in dB (≤ 0).</summary>
    public double TakeReductionDb() => Interlocked.Exchange(ref minGr, 0f);

    public void Process(Span<float> left, Span<float> right)
    {
        var s = shape;
        double slope = 1 / Math.Max(1, s.Ratio) - 1, H = s.Headroom, W = KneeDb;
        bool on = s.Ratio > 1;
        if (!on && g >= -1e-4)
        {
            // off and let go: nothing to do; the filter starts clean when it is turned on again
            g = 0;
            idle = true;
            return;
        }
        if (idle)
        {
            filter.Reset();
            held = level = 0;
            idle = false;
        }
        double aR = Math.Exp(-1 / (Math.Max(0.005, s.Release) * fs));
        double gate = Math.Pow(10, GateDb / 10);
        double gg = g, pk = held, lev = level, low = minGr;
        int k = age;
        var c = bp;
        for (int i = 0; i < left.Length; i++)
        {
            double l = left[i], r = right[i];
            double bl = filter.ProcessSample(l, 0, c), br = filter.ProcessSample(r, 1, c);
            double p = 0.5 * (bl * bl + br * br);
            lev += (p - lev) * (lev < gate ? aStart : aLevel);
            double x = Math.Max(Math.Abs(bl), Math.Abs(br));
            if (x >= pk) { pk = x; k = 0; }
            else if (++k > hold) pk = Math.Max(x, pk * holdFall);
            double target = 0;
            if (on && lev > gate)
            {
                double over = (pk > 1e-6 ? 20 * Math.Log10(pk) : -120) - (10 * Math.Log10(lev) + H);
                if (2 * over <= -W) target = 0;
                else if (2 * Math.Abs(over) < W) target = slope * (over + W / 2) * (over + W / 2) / (2 * W);
                else target = slope * over;
                if (target < -MaxCutDb) target = -MaxCutDb;
            }
            gg = target < gg ? aAttack * gg + (1 - aAttack) * target : aR * gg + (1 - aR) * target;
            if (gg < -1e-4)
            {
                double cut = 1 - Math.Pow(10, gg / 20);
                left[i] = (float)(l - cut * bl);
                right[i] = (float)(r - cut * br);
            }
            if (gg < low) low = gg;
        }
        g = gg;
        held = pk;
        level = lev;
        age = k;
        minGr = (float)low;
    }
}
