using System.Text.Json;
using WildTrack.Audio.Dsp;
using WildTrack.Projects;

namespace WildTrack.Lab;

/// <summary>
/// wtlab's side of the channels' insert slots: <c>proyecto insertos</c> lists and edits a track's slots (or, with
/// <c>--bus</c>, a bus's) like the mixer strip, and <see cref="Check"/> (part of <c>comprobar-dsp</c>) verifies that a
/// version 1 channel, turned into slots, sounds exactly like the fixed chain did, that repeating, reordering and swapping
/// slots behave, and that a bus sounds like its slots on the sum of what is routed to it.
/// </summary>
static class InsertCommands
{
    const int Block = 128;

    /// <summary>
    /// wtlab proyecto insertos carpeta (pista | --bus id|n) [agregar tipo [--en n] | quitar n | mover n m | duplicar n | prender n | apagar n | plegar n]
    /// Reverb and delay slots only go in a bus's chain, like the mixer's "+ Efecto" offers them; <c>agregar vst
    /// --plugin "nombre"</c> adds a VST3 effect from the scanned plugins (<c>vst escanear</c>).
    /// </summary>
    public static int Run(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        List<InsertSlot> slots;
        string name;
        int first;
        bool isReturn = false;
        if (a.Get("bus") is { } busId)
        {
            var bus = ProjectBuses.Find(p, busId) ?? throw new ArgumentException($"No encontré ese bus (hay {p.Settings.Buses.Count}).");
            slots = bus.Inserts;
            name = $"Bus «{bus.Name}»";
            first = 2;
            isReturn = true;
        }
        else
        {
            string trackId = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("insertos: falta la pista o --bus (wtlab proyecto clips carpeta).");
            var track = p.Tracks.Find(t => t.Id == trackId)
                ?? (int.TryParse(trackId, out int n) && n >= 1 && n <= p.Tracks.Count ? p.Tracks[n - 1] : null)
                ?? throw new ArgumentException("No encontré esa pista.");
            slots = track.Channel.Slots;
            name = track.Name;
            first = 3;
        }
        string op = a.Positional.ElementAtOrDefault(first) ?? "";
        string v1 = a.Positional.ElementAtOrDefault(first + 1) ?? "";
        string v2 = a.Positional.ElementAtOrDefault(first + 2) ?? "";
        string Id(string number) => int.TryParse(number, out int i) && i >= 1 && i <= slots.Count ? slots[i - 1].Id
            : throw new ArgumentException($"insertos: no hay un slot {number} (son {slots.Count}).");
        bool changed = op switch
        {
            "" => false,
            "agregar" when v1.Equals("vst", StringComparison.OrdinalIgnoreCase) =>
                ChannelInserts.AddVst(slots, Plugin(a), a.Get("en") is { } at ? int.Parse(at) - 1 : null) != null,
            "agregar" => ChannelInserts.Add(slots, KindFor(v1, isReturn), a.Get("en") is { } at ? int.Parse(at) - 1 : null) != null,
            "quitar" => ChannelInserts.Remove(slots, Id(v1)),
            "mover" => ChannelInserts.Move(slots, Id(v1), int.Parse(v2) - int.Parse(v1)),
            "duplicar" => ChannelInserts.Duplicate(slots, Id(v1)) != null,
            "prender" or "apagar" => Toggle(slots.Find(s => s.Id == Id(v1))!, op == "prender"),
            "plegar" => Fold(slots.Find(s => s.Id == Id(v1))!),
            _ => throw new ArgumentException("insertos: agregar vida|comp|eq|puerta (y reverb|delay en un bus) | agregar vst --plugin \"nombre\" | quitar n | mover n m | duplicar n | prender n | apagar n | plegar n"),
        };
        if (changed)
        {
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            store.Save();
        }
        Console.WriteLine($"{name}: {slots.Count} slot{(slots.Count == 1 ? "" : "s")}");
        for (int i = 0; i < slots.Count; i++) Console.WriteLine($"  {i + 1}. {Describe(slots[i])}");
        return 0;
    }

    /// <summary>The VST3 effect <c>--plugin</c> names, from the scanned plugins.</summary>
    static PluginRef Plugin(Args a)
    {
        string name = a.Get("plugin") ?? throw new ArgumentException("insertos: agregar vst --plugin \"nombre\"");
        var found = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con wtlab vst escanear.");
        if (found.IsInstrument) Console.WriteLine($"Ojo: {found.Name} figura como instrumento; lo pongo como efecto igual.");
        return PluginCatalog.Ref(found);
    }

    static bool Toggle(InsertSlot s, bool on)
    {
        if (s.On == on) return false;
        s.On = on;
        return true;
    }

    /// <summary>The kind from wtlab's Spanish names (or the saved ones).</summary>
    public static string Kind(string name) => name.ToLowerInvariant() switch
    {
        "sat" or "vida" or "valvula" or "saturacion" => TrackInserts.Sat,
        "comp" or "compresor" => TrackInserts.Comp,
        "eq" => TrackInserts.Eq,
        "puerta" or "gate" => TrackInserts.Gate,
        _ => throw new ArgumentException($"No conozco el efecto «{name}»: vida, comp, eq o puerta."),
    };

    /// <summary>The kind to add to a chain: a return's also takes reverb and delay.</summary>
    static string KindFor(string name, bool isReturn) => name.ToLowerInvariant() switch
    {
        "reverb" when isReturn => TrackInserts.Reverb,
        "delay" when isReturn => TrackInserts.Delay,
        "reverb" or "delay" => throw new ArgumentException("La reverb y el delay van en un retorno (--retorno): las pistas les mandan con sus envíos."),
        _ => Kind(name),
    };

    static string ReverbPreset(ReverbSettings r) => ReverbBus.PresetOf(ChannelDsp.Reverb(r)) is { Length: > 0 } p ? p + " · " : "";

    /// <summary>Folds or unfolds a slot in the mixer (only how it is shown).</summary>
    static bool Fold(InsertSlot s)
    {
        s.Collapsed = !s.Collapsed;
        return true;
    }

    public static string Describe(InsertSlot s) => DescribeKind(s) + (s.Collapsed ? " (plegado)" : "");

    static string DescribeKind(InsertSlot s)
    {
        string on = s.On ? "ON " : "off";
        return s.Type switch
        {
            InsertSlot.SatKind => $"{on} Vida {(s.Sat!.Mode == "tape" ? "cinta" : "válvula")} {s.Sat.Amount * 100:0}%",
            InsertSlot.CompKind => $"{on} Compresor {s.Comp!.Preset} {s.Comp.Amount * 100:0}%{(s.Comp.Limit ? " + lim" : "")}",
            InsertSlot.EqKind => $"{on} EQ {s.Eq!.Bands.Count(b => b.On)} bandas activas",
            InsertSlot.ReverbKind => $"{on} Reverb {ReverbPreset(s.Reverb!)}{s.Reverb!.Decay:0.0} s",
            InsertSlot.GateKind => $"{on} Puerta {(s.Gate!.Mode switch { "soft" => "suave", "hard" => "firme", _ => "normal" })} {s.Gate.Threshold:0.#} dB",
            InsertSlot.DelayKind => $"{on} Delay {(s.Delay!.Sync ? s.Delay.Division : $"{s.Delay.TimeMs:0} ms")} · repite {s.Delay.Feedback * 100:0}%",
            InsertSlot.VstKind when s.Known => $"{on} VST3 {s.Vst!.Plugin.Name}{(s.Vst.Plugin.Vendor.Length > 0 ? $" · {s.Vst.Plugin.Vendor}" : "")}{(s.Vst.Plugin.State != null ? " (con su ajuste guardado)" : "")}",
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
            TrackInserts.Gate => new InsertSpec($"s{i}", TrackInserts.Gate) { Gate = GateParams.Defaults with { On = true } },
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

        // 0. folding a slot in the mixer is kept with the project (and a slot from before reads unfolded)
        var folded = InsertSlot.New(InsertSlot.EqKind);
        folded.Collapsed = true;
        var back = JsonSerializer.Deserialize<InsertSlot>(JsonSerializer.Serialize(folded, ProjectStore.Json), ProjectStore.Json)!;
        var old = JsonSerializer.Deserialize<InsertSlot>("""{"type":"eq"}""", ProjectStore.Json)!;
        Line("slot plegado se guarda", back.Collapsed && !old.Collapsed ? 0 : 1, 0, "plegado");

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
            live.ProcessFilledBuffer(Block, false, sink, outL, out _, out _, out _);
            foreach (float v in outL) { jump = Math.Max(jump, Math.Abs(v - last)); last = v; }
            end = outL[^1];
        }
        Line("Slots: cambio de lista sin saltos", Math.Abs(end - 0.5f) < 1e-6 ? jump : 1, 0.5 / TrackDspChain.SwapFade + 1e-6, "rampa");

        // 4. the model: a kind with no slot gives a spare; turning it on puts it back where it went; unknown slots stay
        // a template's chain (a new channel has none)
        var m = new Channel { Inserts = Channel.DefaultInserts() };
        bool bare = new Channel().Slots.Count == 0;
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
        Line("Slots: repuesto, desconocidos, editar, canal nuevo vacío", bare && spare && adopted && future && edits ? 0 : 1, 0, "bien");

        // the mixer's FX switch: every slot goes to the engine off, and the channel keeps them on
        var fx = new Channel { Inserts = Channel.DefaultInserts() };
        foreach (var sl in fx.Slots) sl.On = true;
        var all = ChannelDsp.Specs(fx, rms, bypass: true);
        bool bypassed = all.Length == fx.Slots.Count && fx.Slots.All(sl => sl.On)
            && all.Where(sp => sp.Kind == TrackInserts.Sat).All(sp => sp.Sat.Bypass) && all.Where(sp => sp.Kind == TrackInserts.Comp).All(sp => !sp.Comp.On)
            && all.Where(sp => sp.Kind == TrackInserts.Eq).All(sp => !sp.EqOn);
        Line("Slots: el bypass de FX los apaga sin tocar el canal", bypassed ? 0 : 1, 0, "bien");

        // 5. buses: with an EQ, the mix is that EQ on the sum of the tracks routed there; a stem goes through it alone;
        // the live bus swaps its slots without jumps
        var y = new float[x.Length];
        for (int i = 0; i < y.Length; i++) y[i] = 0.6f * x[(i * 7 + 1000) % x.Length];
        var eqSpec = new InsertSpec("fe", TrackInserts.Eq) { EqOn = true, EqBands = bands };
        MixTrackSpec[] Dry(int output) =>
        [
            new() { Clips = [clip], Inserts = specs, Volume = 0.8, Pan = -0.3, Output = output },
            new() { Clips = [new MixClipSpec { Left = y, Right = y, DurationSec = 3 }], Inserts = [], Pan = 0.4, Output = output },
        ];
        // dry (no sends, no limiter): the bus's EQ over the sum of its tracks, block by block like the tracks
        var sum = MixRenderer.Render(Dry(-1), new MixBusSpec(), 3, sr, limit: false);
        var eqBus = MixRenderer.Render(Dry(0), new MixBusSpec { Nodes = [new MixNodeSpec { Inserts = [eqSpec] }] }, 3, sr, limit: false);
        var eqRef = new EqInsert(sr);
        eqRef.Set(true, bands);
        var sumL = (float[])sum.Left.Clone();
        var sumR = (float[])sum.Right.Clone();
        for (int pos = 0; pos < sumL.Length; pos += Block)
        {
            int n = Math.Min(Block, sumL.Length - pos);
            eqRef.Process(sumL.AsSpan(pos, n), sumR.AsSpan(pos, n));
        }
        bool eqHeard = MaxDiff(sum.Left, eqBus.Left) > 1e-3;
        Line("Buses: EQ del bus = EQ de la suma", eqHeard ? Math.Max(MaxDiff(sumL, eqBus.Left), MaxDiff(sumR, eqBus.Right)) : 1, 1e-6, "~0");

        // live: adding a slot to a bus and taking it out fades over, no jumps
        var bus = new BusDspChain(sr);
        float busLast = 0.5f, busJump = 0, busEnd = 0;
        var mixL = new float[Block];
        for (int b = 0; b < 16; b++)
        {
            if (b == 2) bus.SetInserts([new("e", TrackInserts.Eq)]);
            if (b == 9) bus.SetInserts([]);
            bus.Begin(Block);
            Array.Clear(mixL);
            dc.CopyTo(bus.BufferL(Block));
            dc.CopyTo(bus.BufferR(Block));
            bus.End(Block, sink, mixL);
            foreach (float v in mixL) { busJump = Math.Max(busJump, Math.Abs(v - busLast)); busLast = v; }
            busEnd = mixL[^1];
        }
        Line("Buses: slots en vivo sin saltos", Math.Abs(busEnd - 0.5f) < 1e-6 ? busJump : 1, 0.5 / TrackDspChain.SwapFade + 1e-6, "rampa");

        // the model: a bus keeps unknown slots, its repeated ids are fixed, and the slot edits work on it
        var pr = ProjectStore.Parse("""{"settings":{"buses":[{"id":"bus-1","inserts":[{"id":"fx-1","type":"vst3"},{"id":"fx-1","type":"eq"}]}]}}""");
        var busModel = pr.Settings.Buses[0];
        bool model = busModel.Inserts.Count == 2 && busModel.Inserts[0].Id != busModel.Inserts[1].Id
            && ChannelDsp.BusSpecs(busModel, 120).Length == 1 && ChannelInserts.Add(busModel.Inserts, TrackInserts.Comp) is { On: true }
            && ChannelDsp.Bus(pr).Nodes.Count == 1;
        Line("Buses: modelo y slots", model ? 0 : 1, 0, "bien");
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
            chain.ProcessFilledBuffer(n, false, sink.AsSpan(0, n), y.AsSpan(pos, n), out _, out _, out _);
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
