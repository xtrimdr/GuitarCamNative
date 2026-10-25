using System.Text.Json;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>
/// gclab's side of the channels' insert slots: <c>proyecto insertos</c> lists and edits a track's slots like the mixer
/// strip, and <see cref="Check"/> (part of <c>comprobar-dsp</c>) verifies that a version 1 channel, turned into slots,
/// sounds exactly like the fixed chain did, and that repeating, reordering and swapping slots behave.
/// </summary>
static class InsertCommands
{
    const int Block = 128;

    /// <summary>gclab proyecto insertos carpeta pista [agregar tipo [--en n] | quitar n | mover n m | duplicar n | prender n | apagar n]</summary>
    public static int Run(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        string trackId = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("insertos: falta la pista (gclab proyecto clips carpeta).");
        var track = p.Tracks.Find(t => t.Id == trackId)
            ?? (int.TryParse(trackId, out int n) && n >= 1 && n <= p.Tracks.Count ? p.Tracks[n - 1] : null)
            ?? throw new ArgumentException("No encontré esa pista.");
        var ch = track.Channel;
        string op = a.Positional.ElementAtOrDefault(3) ?? "";
        string v1 = a.Positional.ElementAtOrDefault(4) ?? "";
        string v2 = a.Positional.ElementAtOrDefault(5) ?? "";
        string Id(string number) => int.TryParse(number, out int i) && i >= 1 && i <= ch.Slots.Count ? ch.Slots[i - 1].Id
            : throw new ArgumentException($"insertos: no hay un slot {number} (son {ch.Slots.Count}).");
        bool changed = op switch
        {
            "" => false,
            "agregar" => ChannelInserts.Add(ch, Kind(v1), a.Get("en") is { } at ? int.Parse(at) - 1 : null) != null,
            "quitar" => ChannelInserts.Remove(ch, Id(v1)),
            "mover" => ChannelInserts.Move(ch, Id(v1), int.Parse(v2) - int.Parse(v1)),
            "duplicar" => ChannelInserts.Duplicate(ch, Id(v1)) != null,
            "prender" or "apagar" => Toggle(ch.Slot(Id(v1))!, op == "prender"),
            _ => throw new ArgumentException("insertos: agregar valvula|comp|eq | quitar n | mover n m | duplicar n | prender n | apagar n"),
        };
        if (changed)
        {
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            store.Save();
        }
        Console.WriteLine($"{track.Name}: {ch.Slots.Count} slot{(ch.Slots.Count == 1 ? "" : "s")}");
        for (int i = 0; i < ch.Slots.Count; i++) Console.WriteLine($"  {i + 1}. {Describe(ch.Slots[i])}");
        return 0;
    }

    static bool Toggle(InsertSlot s, bool on)
    {
        if (s.On == on) return false;
        s.On = on;
        return true;
    }

    /// <summary>The kind from gclab's Spanish names (or the saved ones).</summary>
    public static string Kind(string name) => name.ToLowerInvariant() switch
    {
        "sat" or "vida" or "valvula" or "saturacion" => TrackInserts.Sat,
        "comp" or "compresor" => TrackInserts.Comp,
        "eq" => TrackInserts.Eq,
        _ => throw new ArgumentException($"No conozco el efecto «{name}»: vida, comp o eq."),
    };

    static string Describe(InsertSlot s)
    {
        string on = s.On ? "ON " : "off";
        return s.Type switch
        {
            InsertSlot.SatKind => $"{on} Vida {(s.Sat!.Mode == "tape" ? "cinta" : "válvula")} {s.Sat.Amount * 100:0}%",
            InsertSlot.CompKind => $"{on} Compresor {s.Comp!.Preset} {s.Comp.Amount * 100:0}%{(s.Comp.Limit ? " + lim" : "")}",
            InsertSlot.EqKind => $"{on} EQ {s.Eq!.Bands.Count(b => b.On)} bandas activas",
            _ => $"--- {s.Type} (desconocido: se guarda tal cual y no suena)",
        } + $"  [{s.Id}]";
    }

    /// <summary>
    /// Builds a chain for <c>procesar --cadena</c>: the kinds in that order (repeats allowed), each with the settings
    /// the other options gave.
    /// </summary>
    public static InsertSpec[] Chain(string list, SatParams sat, CompState comp, bool eqOn, EqBandSpec[] eqBands)
    {
        var kinds = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return kinds.Select((k, i) => Kind(k) switch
        {
            TrackInserts.Sat => new InsertSpec($"s{i}", TrackInserts.Sat) { Sat = sat },
            TrackInserts.Comp => new InsertSpec($"s{i}", TrackInserts.Comp) { Comp = comp },
            _ => new InsertSpec($"s{i}", TrackInserts.Eq) { EqOn = eqOn, EqBands = eqBands },
        }).ToArray();
    }

    public static bool Check()
    {
        int sr = 48000;
        bool allOk = true;
        void Line(string name, double got, double tol, string want)
        {
            bool ok = got <= tol;
            allOk &= ok;
            string g = got == 0 ? "exacto" : $"{got:0.0e0}";
            Console.WriteLine($"{name,-36} {g,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        // a guitar-ish signal: harmonics with a slow swell, so the compressor and the saturator both work
        var x = new float[sr * 3];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / (double)sr, env = 0.2 + 0.6 * Math.Abs(Math.Sin(Math.PI * t / 1.5));
            x[i] = (float)(env * (0.5 * Math.Sin(2 * Math.PI * 147 * t) + 0.25 * Math.Sin(2 * Math.PI * 294 * t) + 0.1 * Math.Sin(2 * Math.PI * 2205 * t)));
        }

        // 1. a version 1 channel (fixed sat/comp/eq) read as slots sounds exactly like the fixed chain, pre-fader
        const string v1Json = """
            {"volume":1,"pan":0,"sat":{"on":true,"mode":"tape","amount":0.55},
             "comp":{"on":true,"preset":"lead","amount":0.7,"limit":true,"threshold":-22,"ratio":5,"attack":0.005,"release":0.12,"knee":4,"makeup":3,
                     "eq":{"hpfHz":90,"mudDb":-2,"presenceDb":2.5,"presenceHz":3000,"airDb":1.5}},
             "eq":{"on":true,"bands":[{"on":true,"type":"highpass","freq":100,"q":0.7},{"on":true,"type":"peaking","freq":250,"gain":-3,"q":1},
                   {"type":"peaking","freq":800},{"on":true,"type":"peaking","freq":2000,"gain":2,"q":1.2},{"type":"peaking","freq":4500},{"type":"lowpass","freq":12000,"q":0.7}]}}
            """;
        var ch = JsonSerializer.Deserialize<Channel>(v1Json, ProjectStore.Json)!;
        double rms = -18;
        var specs = ChannelDsp.Specs(ch, rms);
        bool shape = specs.Length == 3 && specs[0].Kind == TrackInserts.Sat && specs[1].Kind == TrackInserts.Comp && specs[2].Kind == TrackInserts.Eq;
        var sat = SatParams.From(true, SatMode.Tape, 0.55, rms);
        var comp = ChannelDsp.Comp(new CompSettings
        {
            On = true, Preset = "lead", Amount = 0.7, Limit = true, Threshold = -22, Ratio = 5, Attack = 0.005, Release = 0.12, Knee = 4, Makeup = 3,
            Eq = new CompEq { HpfHz = 90, MudDb = -2, PresenceDb = 2.5, PresenceHz = 3000, AirDb = 1.5 },
        });
        var bands = ChannelDsp.EqBands(new EqSettings { On = true, Bands =
        [
            new() { On = true, Type = "highpass", Freq = 100, Q = 0.7 }, new() { On = true, Type = "peaking", Freq = 250, Gain = -3, Q = 1 },
            new() { Type = "peaking", Freq = 800 }, new() { On = true, Type = "peaking", Freq = 2000, Gain = 2, Q = 1.2 },
            new() { Type = "peaking", Freq = 4500 }, new() { Type = "lowpass", Freq = 12000, Q = 0.7 },
        ] });
        var fixedOut = Reference(x, sr, sat, comp, bands);
        var migrated = Run(x, sr, specs);
        Line("Slots: canal v1 migrado = cadena fija", shape ? MaxDiff(fixedOut, migrated) : 1, 0, "exacto");

        // the same through the whole export: MixTrackSpec with the old fields vs. with the slots
        var clip = new MixClipSpec { Left = x, Right = x, DurationSec = 3 };
        var oldMix = MixRenderer.Render([new MixTrackSpec { Clips = [clip], Sat = sat, Comp = comp, EqOn = true, EqBands = bands }], new MixBusSpec(), 3, sr);
        var newMix = MixRenderer.Render([new MixTrackSpec { Clips = [clip], Inserts = specs }], new MixBusSpec(), 3, sr);
        Line("Slots: exportación v1 = slots", Math.Max(MaxDiff(oldMix.Left, newMix.Left), MaxDiff(oldMix.Right, newMix.Right)), 0, "exacto");

        // saved again, a channel has "inserts" and none of the old fields; read back it is the same chain
        string saved = JsonSerializer.Serialize(ch, ProjectStore.Json);
        var again = JsonSerializer.Deserialize<Channel>(saved, ProjectStore.Json)!;
        var root = JsonDocument.Parse(saved).RootElement;
        bool format = root.TryGetProperty("inserts", out _) && !root.TryGetProperty("sat", out _) && !root.TryGetProperty("comp", out _) && !root.TryGetProperty("eq", out _)
            && again.Slots.Select(s => s.Id).SequenceEqual(ch.Slots.Select(s => s.Id));
        Line("Slots: guardado sin sat/comp/eq viejos", format ? MaxDiff(migrated, Run(x, sr, ChannelDsp.Specs(again, rms))) : 1, 0, "exacto");

        // 2. repeated and reordered: two EQs = the EQ twice; EQ before Vida = those two in that order; no slots = dry
        var eqTwice = Run(x, sr, [new("a", TrackInserts.Eq) { EqOn = true, EqBands = bands }, new("b", TrackInserts.Eq) { EqOn = true, EqBands = bands }]);
        var eqOnce = Run(x, sr, [new("a", TrackInserts.Eq) { EqOn = true, EqBands = bands }]);
        Line("Slots: EQ repetido = EQ dos veces", MaxDiff(eqTwice, Run(eqOnce, sr, [new("a", TrackInserts.Eq) { EqOn = true, EqBands = bands }])), 0, "exacto");
        var eqSat = Run(x, sr, [new("e", TrackInserts.Eq) { EqOn = true, EqBands = bands }, new("s", TrackInserts.Sat) { Sat = sat }]);
        var satOfEq = Run(eqOnce, sr, [new("s", TrackInserts.Sat) { Sat = sat }]);
        var satEq = Run(x, sr, [new("s", TrackInserts.Sat) { Sat = sat }, new("e", TrackInserts.Eq) { EqOn = true, EqBands = bands }]);
        bool orderMatters = MaxDiff(eqSat, satEq) > 1e-3;
        Line("Slots: EQ → Vida = en ese orden", orderMatters ? MaxDiff(eqSat, satOfEq) : 1, 0, "exacto");
        Line("Slots: sin slots = seco", MaxDiff(x, Run(x, sr, [])), 1e-9, "~0");

        // 3. live: changing a slot's settings keeps its effect (no swap); a new list fades over in 2 × 5 ms, no jumps
        var chain = new TrackDspChain(sr);
        chain.SetInserts(specs, fade: false);
        var before = chain.Inserts.ToArray();
        chain.SetInserts(ChannelDsp.Specs(ch, rms - 6));
        bool kept = before.SequenceEqual(chain.Inserts);
        Line("Slots: editar no cambia los efectos", kept ? 0 : 1, 0, "mismos");
        var dc = new float[Block];
        Array.Fill(dc, 0.5f);
        var live = new TrackDspChain(sr);
        live.SetInserts([new("e", TrackInserts.Eq)], fade: false);
        var outL = new float[Block];
        var sink = new float[Block];
        float last = 0.5f, jump = 0, end = 0;
        for (int b = 0; b < 12; b++)
        {
            if (b == 2) live.SetInserts([]);
            dc.CopyTo(live.BufferL(Block));
            dc.CopyTo(live.BufferR(Block));
            Array.Clear(outL);
            live.ProcessFilledBuffer(Block, false, sink, outL, sink, sink, sink, sink, out _, out _, out _);
            foreach (float v in outL) { jump = Math.Max(jump, Math.Abs(v - last)); last = v; }
            end = outL[^1];
        }
        Line("Slots: cambio de lista sin saltos", Math.Abs(end - 0.5f) < 1e-6 ? jump : 1, 0.5 / TrackDspChain.SwapFade + 1e-6, "rampa");

        // 4. the model: a kind with no slot gives a spare; turning it on puts it back where it went; unknown slots stay
        var m = new Channel();
        ChannelInserts.Remove(m, m.Slots[1].Id);
        bool spare = m.Slots.Count == 2 && !m.Comp.On;
        m.Comp.On = true;
        bool adopted = m.AdoptSpares() && m.Slots.Count == 3 && m.Slots[1].Type == InsertSlot.CompKind && m.Slots[1].Comp == m.Comp;
        var withFuture = JsonSerializer.Deserialize<Channel>("""{"inserts":[{"id":"fx-1","type":"vst3","plugin":"Amp"},{"id":"fx-2","type":"eq"}]}""", ProjectStore.Json)!;
        string futureSaved = JsonSerializer.Serialize(withFuture, ProjectStore.Json);
        bool future = ChannelDsp.Specs(withFuture, rms).Length == 1 && futureSaved.Contains("\"plugin\": \"Amp\"") && futureSaved.Contains("\"vst3\"");
        var dup = ChannelInserts.Duplicate(m, m.Slots[0].Id);
        bool edits = dup != null && m.Slots[1] == dup && dup.Id != m.Slots[0].Id && ChannelInserts.Move(m, dup.Id, 2) && m.Slots[3] == dup
            && !ChannelInserts.Move(m, dup.Id, 1);
        // a drag lands next to another slot: after the last, before the first, after the second
        string firstId = m.Slots[0].Id, lastId = m.Slots[^1].Id;
        edits &= ChannelInserts.MoveNextTo(m, firstId, lastId, true) && m.Slots[^1].Id == firstId
            && ChannelInserts.MoveNextTo(m, firstId, m.Slots[0].Id, false) && m.Slots[0].Id == firstId
            && ChannelInserts.MoveNextTo(m, firstId, m.Slots[2].Id, true) && m.Slots[2].Id == firstId
            && !ChannelInserts.MoveNextTo(m, firstId, firstId, true);
        Line("Slots: repuesto, desconocidos, editar", spare && adopted && future && edits ? 0 : 1, 0, "bien");
        return allOk;
    }

    /// <summary>The chain as it was fixed before slots, built by hand from the effects themselves.</summary>
    static float[] Reference(float[] x, int sr, SatParams sat, CompState comp, EqBandSpec[] bands)
    {
        var s = new SatInsert(sr);
        s.Set(sat);
        var c = new CompInsert(sr);
        c.Set(comp);
        var e = new EqInsert(sr);
        e.Set(true, bands);
        var l = (float[])x.Clone();
        var r = (float[])x.Clone();
        for (int pos = 0; pos < l.Length; pos += Block)
        {
            int n = Math.Min(Block, l.Length - pos);
            var bl = l.AsSpan(pos, n);
            var br = r.AsSpan(pos, n);
            s.Process(bl, br);
            c.Process(bl, br);
            e.Process(bl, br);
        }
        return l;
    }

    /// <summary><paramref name="x"/> (on both channels) through a <see cref="TrackDspChain"/> with these slots, before the fader.</summary>
    static float[] Run(float[] x, int sr, IReadOnlyList<InsertSpec> specs)
    {
        var chain = new TrackDspChain(sr);
        chain.SetInserts(specs, fade: false);
        var y = new float[x.Length];
        var sink = new float[Block];
        // centred at full volume, the right output is the right channel as it left the slots times exactly 1
        for (int pos = 0; pos < x.Length; pos += Block)
        {
            int n = Math.Min(Block, x.Length - pos);
            x.AsSpan(pos, n).CopyTo(chain.BufferL(n));
            x.AsSpan(pos, n).CopyTo(chain.BufferR(n));
            chain.ProcessFilledBuffer(n, false, sink.AsSpan(0, n), y.AsSpan(pos, n), sink.AsSpan(0, n), sink.AsSpan(0, n), sink.AsSpan(0, n), sink.AsSpan(0, n),
                out _, out _, out _);
        }
        return y;
    }

    static double MaxDiff(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 1;
        double d = 0;
        for (int i = 0; i < a.Length; i++) d = Math.Max(d, Math.Abs(a[i] - b[i]));
        return d;
    }
}
