using System.Globalization;
using System.Text.Json;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>
/// gclab's side of the buses and the routing: <c>proyecto buses</c> lists and edits the buses (like the mixer's bus
/// strips), <c>proyecto envios</c> the output and sends of a track, a folder or a bus (like the Salida selector, the
/// send panels and their PRE chip), and <see cref="Check"/> (part of <c>comprobar-dsp</c>) verifies that a folder is a
/// plain sum, that a pre-fader send ignores the fader, that the live classes block by block sound like the export
/// through a chain of folder and buses, that a stem goes through its buses, and the model: loops refused, version 2
/// projects turned into buses, normalizing and undo.
/// </summary>
static class BusCommands
{
    const int Block = 128;

    /// <summary>A new project's buses ("Reverb" first, then "Delay") as the export takes them, for mixes without a project.</summary>
    public static MixBusSpec DefaultBus(double bpm) => ChannelDsp.Bus(new Project { Settings = new ProjectSettings { Bpm = bpm } });

    /// <summary>gclab proyecto buses carpeta [agregar [nombre] [--tipo reverb|delay|vacio] | quitar n | renombrar n nombre | volumen n x | paneo n x | mute n]</summary>
    public static int Buses(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        string op = a.Positional.ElementAtOrDefault(2) ?? "";
        string v1 = a.Positional.ElementAtOrDefault(3) ?? "";
        string v2 = a.Positional.ElementAtOrDefault(4) ?? "";
        Bus Find(string n) => ProjectBuses.Find(p, n) ?? throw new ArgumentException($"buses: no hay un bus «{n}» (son {p.Settings.Buses.Count}).");
        double Num(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
        bool changed;
        switch (op)
        {
            case "":
                changed = false;
                break;
            case "agregar":
                string kind = (a.Get("tipo") ?? "reverb").ToLowerInvariant() switch
                {
                    "reverb" => InsertSlot.ReverbKind,
                    "delay" => InsertSlot.DelayKind,
                    "vacio" or "vacío" => "",
                    var k => throw new ArgumentException($"buses: --tipo reverb, delay o vacio, no «{k}»."),
                };
                var added = ProjectBuses.Add(p, kind, v1 == "" ? null : v1)!;
                Console.WriteLine($"Agregado: {added.Name} [{added.Id}]");
                changed = true;
                break;
            case "quitar":
                changed = ProjectBuses.Remove(p, Find(v1).Id);
                break;
            case "renombrar":
                changed = ProjectBuses.Rename(p, Find(v1).Id, v2);
                break;
            case "volumen":
                var bv = Find(v1);
                double vol = Math.Clamp(Num(v2), 0, 1.5);
                changed = bv.Volume != vol;
                bv.Volume = vol;
                break;
            case "paneo":
                var bp = Find(v1);
                double pan = Math.Clamp(Num(v2), -1, 1);
                changed = bp.Pan != pan;
                bp.Pan = pan;
                break;
            case "mute":
                var bm = Find(v1);
                bm.Muted = !bm.Muted;
                changed = true;
                break;
            default:
                throw new ArgumentException("buses: agregar [nombre] [--tipo reverb|delay|vacio] | quitar n | renombrar n nombre | volumen n x | paneo n x | mute n");
        }
        if (changed)
        {
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            store.Save();
        }
        var list = p.Settings.Buses;
        Console.WriteLine($"Buses: {list.Count} (en el orden en que suenan: {string.Join(" → ", ProjectBuses.Order(p).Select(b => b.Name))})");
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            int outs = ProjectBuses.All(p).Count(r => r.Output == b.Id);
            int sends = ProjectBuses.All(p).Count(r => r.Sends.Exists(s => s.BusId == b.Id && s.On && s.Level > 0));
            Console.WriteLine($"  {i + 1}. {b.Name}  {Fmt(b.Volume)}{(b.Pan != 0 ? $" · paneo {b.Pan:+0.00;-0.00}" : "")}{(b.Muted ? " · mute" : "")} · sale a {OutName(p, b.Output)}"
                + $" · le llegan {outs} salida(s) y {sends} envío(s)  [{b.Id}]");
            for (int k = 0; k < b.Inserts.Count; k++) Console.WriteLine($"       {k + 1}. {InsertCommands.Describe(b.Inserts[k])}");
            foreach (var s in b.Sends) Console.WriteLine($"       → {ProjectBuses.Find(p, s.BusId)?.Name ?? s.BusId}  {Send(s.Level, s.On, s.Pre)}");
        }
        return 0;
    }

    /// <summary>
    /// gclab proyecto envios carpeta quien [bus [nivel] [--pre|--post] [--apagar|--prender] [--quitar|--sumar]] [--salida bus|master]:
    /// quien es una pista (número o id), «carpeta:n» o «bus:n». --quitar saca el bus de su lista (apagado y escondido,
    /// con su nivel guardado) y --sumar lo vuelve a poner, como el + y "Quitar" del mixer.
    /// </summary>
    public static int Sends(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        string who = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("envios: falta quién (una pista, carpeta:n o bus:n).");
        var (name, r) = Who(p, who);
        string target = a.Positional.ElementAtOrDefault(3) ?? "";
        double? level = a.Positional.ElementAtOrDefault(4) is { } lv ? double.Parse(lv.Replace(',', '.'), CultureInfo.InvariantCulture) : null;
        bool? pre = a.Flag("pre") ? true : a.Flag("post") ? false : null;
        bool? on = a.Flag("apagar") ? false : a.Flag("prender") ? true : null;
        // like the knob: turning the level up turns the send on
        if (level is > 0 && on == null) on = true;
        bool changed = false;
        if (target != "")
        {
            var bus = ProjectBuses.Find(p, target) ?? throw new ArgumentException($"envios: no hay un bus «{target}» (ver «proyecto buses»).");
            if (!ProjectBuses.CanFeed(p, r, bus.Id))
                throw new ArgumentException($"envios: {name} no puede mandar a {bus.Name}: un bus sale solo al master.");
            if (a.Flag("quitar") || a.Flag("sumar")) ProjectBuses.ShowSend(p, r, bus.Id, a.Flag("sumar"));
            if (level != null || on != null || pre != null) ProjectBuses.SetSend(p, r, bus.Id, level, on, pre);
            changed = true;
        }
        if (a.Get("salida") is { } outTo)
        {
            string? id = outTo.Equals("master", StringComparison.OrdinalIgnoreCase) ? null
                : ProjectBuses.Find(p, outTo)?.Id ?? throw new ArgumentException($"envios: no hay un bus «{outTo}».");
            if (id != null && !ProjectBuses.CanFeed(p, r, id))
                throw new ArgumentException($"envios: {name} no puede salir a ese bus: un bus sale solo al master.");
            changed |= ProjectBuses.SetOutput(p, r, id);
        }
        if (changed)
        {
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            store.Save();
        }
        string through = r is Channel && p.Tracks.Find(t => t.Channel == r)?.FolderId is { } fid ? $" (va por la carpeta {p.Folders.Find(f => f.Id == fid)?.Name})" : "";
        Console.WriteLine($"{name}: sale a {OutName(p, r.Output)}{through} · envíos (PRE = antes del fader)");
        foreach (var b in p.Settings.Buses)
        {
            if (!ProjectBuses.CanFeed(p, r, b.Id)) continue;
            var (l, o, x) = ProjectBuses.SendOf(r, b.Id);
            bool hidden = r.Sends.Find(s => s.BusId == b.Id)?.Hidden == true;
            Console.WriteLine($"  {b.Name}  {Send(l, o, x)}{(hidden ? " (quitado de su lista)" : "")}");
        }
        return 0;
    }

    /// <summary>A track (number or id), «carpeta:n» or «bus:n», with its name.</summary>
    static (string Name, IRouted Routed) Who(Project p, string who)
    {
        if (who.StartsWith("carpeta:", StringComparison.OrdinalIgnoreCase))
        {
            string n = who[8..];
            var f = p.Folders.Find(x => x.Id == n) ?? (int.TryParse(n, out int k) && k >= 1 && k <= p.Folders.Count ? p.Folders[k - 1] : null)
                ?? throw new ArgumentException("No encontré esa carpeta.");
            return ($"Carpeta {f.Name}", f);
        }
        if (who.StartsWith("bus:", StringComparison.OrdinalIgnoreCase))
        {
            var b = ProjectBuses.Find(p, who[4..]) ?? throw new ArgumentException("No encontré ese bus.");
            return ($"Bus {b.Name}", b);
        }
        var t = p.Tracks.Find(x => x.Id == who) ?? (int.TryParse(who, out int tn) && tn >= 1 && tn <= p.Tracks.Count ? p.Tracks[tn - 1] : null)
            ?? throw new ArgumentException("No encontré esa pista.");
        return (t.Name, t.Channel);
    }

    static string OutName(Project p, string? id) => id == null ? "Master" : ProjectBuses.Find(p, id)?.Name ?? id;

    static string Send(double level, bool on, bool pre) => $"{level * 100:0}%{(on ? "" : " · apagado")}{(pre ? " · PRE" : "")}";

    static string Fmt(double volume) => volume <= 0 ? "-∞ dB" : $"{20 * Math.Log10(volume):+0.0;-0.0;0.0} dB";

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

        // two tracks of plucked-ish tones with gaps, so the reverb and the delay have tails to show
        var x = new float[sr * 3];
        var y = new float[sr * 3];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / (double)sr, ph = t % 0.75;
            x[i] = (float)(0.5 * Math.Exp(-ph * 6) * Math.Sin(2 * Math.PI * 196 * t));
            y[i] = (float)(0.4 * Math.Exp(-((t + 0.3) % 0.5) * 9) * Math.Sin(2 * Math.PI * 523 * t));
        }
        var cx = new MixClipSpec { Left = x, Right = x, DurationSec = 3 };
        var cy = new MixClipSpec { Left = y, Right = y, DurationSec = 3 };
        var rev = new ReverbParams(2.4, 35, 7000, 180, 0.8, 0.9);
        var dly = new DelayParams(DelayMode.PingPong, true, "1/4", 300, 0.45, 4500, 300, 0.7, 0.2, 0.75);
        const double bpm = 97;
        var revSlot = new InsertSpec("r", TrackInserts.Reverb) { Reverb = rev };
        var dlySlot = new InsertSpec("d", TrackInserts.Delay) { Delay = dly, Bpm = bpm };
        var eqSlot = new InsertSpec("e", TrackInserts.Eq) { EqOn = true, EqBands = [.. TrackEq.DefaultBands.Select((b, i) => i == 2 ? b with { Gain = 6 } : b)] };
        double Diff((float[] Left, float[] Right) a, (float[] Left, float[] Right) b) => Math.Max(MaxDiff(a.Left, b.Left), MaxDiff(a.Right, b.Right));
        (float[] Left, float[] Right) Mix(MixTrackSpec[] tracks, MixBusSpec bus, int only = -1) => MixRenderer.Render(tracks, bus, 5, sr, only: only, limit: false);

        // (a) a folder is a plain sum: its tracks routed through it sound exactly like without it
        MixTrackSpec[] Two(int output) =>
        [
            new() { Clips = [cx], Inserts = [], Volume = 0.8, Pan = -0.3, Output = output },
            new() { Clips = [cy], Inserts = [], Volume = 0.6, Pan = 0.5, Output = output },
        ];
        var noFolder = Mix(Two(-1), new MixBusSpec());
        var inFolder = Mix(Two(0), new MixBusSpec { Nodes = [new MixNodeSpec { Name = "Guitarras" }] });
        Line("Buses: carpeta = sin carpeta", Diff(noFolder, inFolder), 0, "exacto");

        // (b) pre-fader: the send ignores the fader; post follows it; mute silences both. Measured at the bus's output,
        // through the live classes (TrackDspChain + BusDspChain), block by block like the engine.
        float[] BusOut(double volume, bool pre, bool muted)
        {
            var chain = new TrackDspChain(sr) { Volume = volume, Pan = 0.2, Muted = muted };
            chain.SetInserts([], fade: false);
            var bus = new BusDspChain(sr);
            bus.SetInserts([revSlot], fade: false);
            chain.Sends = [new TrackSend(bus, -1, 0.5f, pre)];
            var outL = new float[x.Length];
            var sink = new float[Block];
            for (int pos = 0; pos < x.Length; pos += Block)
            {
                int n = Math.Min(Block, x.Length - pos);
                x.AsSpan(pos, n).CopyTo(chain.BufferL(n));
                y.AsSpan(pos, n).CopyTo(chain.BufferR(n));
                bus.Begin(n);
                chain.ProcessFilledBuffer(n, false, sink.AsSpan(0, n), sink.AsSpan(0, n), out _, out _, out _);
                foreach (var s in chain.BlockSends) chain.AddSend(n, s.Level, s.Pre, bus.BufferL(n), bus.BufferR(n));
                bus.End(n, outL.AsSpan(pos, n), sink.AsSpan(0, n));
            }
            return outL;
        }
        static double Peak(float[] a) => a.Max(v => Math.Abs(v));
        var preLoud = BusOut(1, true, false);
        var preLow = BusOut(0.3, true, false);
        var postLoud = BusOut(1, false, false);
        var postLow = BusOut(0.3, false, false);
        bool postFollows = Math.Abs(Peak(postLow) / Peak(postLoud) - 0.3) < 0.01;
        bool mutedSilent = Peak(BusOut(1, true, true)) == 0 && Peak(BusOut(1, false, true)) == 0;
        Line("Buses: PRE no sigue al fader", postFollows && mutedSilent ? Math.Max(MaxDiff(preLoud, preLow), MaxDiff(preLoud, postLoud)) : 1, 0, "exacto");

        // (c) live = export through a chain: track 1 → folder → "Grupo" (EQ, panned) → master, track 2 → master, both send
        // to "Reverb" (track 2 pre-fader) and the folder to "Delay", "Grupo" sends to "Reverb" and "Delay" goes into
        // "Reverb". Nodes in run order: folder, Grupo, Delay, Reverb.
        MixNodeSpec[] nodes =
        [
            new() { Name = "Carpeta", Output = 1, Sends = [new() { To = 2, Level = 0.3 }] },
            new() { Name = "Grupo", Inserts = [eqSlot], Pan = -0.4, Volume = 0.9, Sends = [new() { To = 3, Level = 0.25 }] },
            new() { Name = "Delay", Inserts = [dlySlot], Output = 3, Volume = 0.8 },
            new() { Name = "Reverb", Inserts = [revSlot], Volume = 0.7 },
        ];
        var graph = new MixBusSpec { Bpm = bpm, Nodes = nodes };
        MixTrackSpec[] Routed() =>
        [
            new() { Clips = [cx], Inserts = [], Volume = 0.8, Pan = -0.3, Output = 0, Sends = [new() { To = 3, Level = 0.35 }] },
            new() { Clips = [cy], Inserts = [], Volume = 0.6, Pan = 0.5, Sends = [new() { To = 3, Level = 0.4, Pre = true }] },
        ];
        var export = Mix(Routed(), graph);
        var live = Live(Routed(), nodes, 5, sr);
        bool heard = Diff(export, Mix([.. Routed().Select(t => new MixTrackSpec { Clips = t.Clips, Inserts = [], Volume = t.Volume, Pan = t.Pan })], new MixBusSpec())) > 1e-3;
        Line("Buses: vivo = exportación", heard ? Diff(export, live) : 1, 1e-6, "~0");
        // a stem goes through its own buses: the same as the whole mix with the other track silent
        var stem = Mix(Routed(), graph, only: 0);
        var alone = Routed();
        var muted = new MixTrackSpec[] { alone[0], new() { Clips = alone[1].Clips, Inserts = [], Muted = true, Sends = alone[1].Sends } };
        Line("Buses: stem por sus buses", Diff(stem, Mix(muted, graph)), 0, "exacto");
        // a muted bus is as if nothing went there (its sends included)
        MixNodeSpec[] mutedReverb = [nodes[0], nodes[1], nodes[2], new() { Name = "Reverb", Inserts = [revSlot], Volume = 0.7, Muted = true }];
        MixNodeSpec[] noReverb = [nodes[0], new() { Name = "Grupo", Inserts = [eqSlot], Pan = -0.4, Volume = 0.9 }, new() { Name = "Delay", Inserts = [dlySlot], Volume = 0 }, new() { Name = "Reverb" }];
        MixTrackSpec[] Unsent() => [.. Routed().Select(t => new MixTrackSpec { Clips = t.Clips, Inserts = [], Volume = t.Volume, Pan = t.Pan, Output = t.Output })];
        Line("Buses: bus en mute = sin él", Diff(Mix(Routed(), new MixBusSpec { Bpm = bpm, Nodes = mutedReverb }), Mix(Unsent(), new MixBusSpec { Bpm = bpm, Nodes = noReverb })), 1e-7, "~0");

        // (d) the model: names, removal, loops refused, the run order, normalizing, version 2, saving and undo
        var p = new Project();
        p.Tracks.Add(new Track { Name = "A" });
        p.Tracks.Add(new Track { Name = "B" });
        bool defaults = p.Settings.Buses is [{ Id: ProjectBuses.ReverbId, Name: "Reverb" }, { Id: ProjectBuses.DelayId, Name: "Delay" }]
            && ProjectBuses.FirstWith(p, InsertSlot.ReverbKind)?.Id == ProjectBuses.ReverbId;
        var g = ProjectBuses.Add(p, "")!;
        var r2 = ProjectBuses.Add(p, InsertSlot.ReverbKind)!;
        bool adds = g is { Name: "Bus", Inserts: [] } && r2 is { Name: "Reverb 2", Inserts: [{ Type: InsertSlot.ReverbKind, On: true }] } && ProjectBuses.Add(p, InsertSlot.EqKind) == null;
        var ch = p.Tracks[0].Channel;
        // a bus goes to the master only: it can't go to another bus nor send to one (or to itself); a track can
        bool loops = !ProjectBuses.SetOutput(p, g, r2.Id) && ProjectBuses.SetSend(p, r2, g.Id, 0.5) == null
            && ProjectBuses.SetSend(p, g, g.Id, 0.5) == null && ProjectBuses.SetOutput(p, ch, g.Id) && ProjectBuses.SetSend(p, ch, r2.Id, 0.3, pre: true) != null;
        var order = ProjectBuses.Order(p);
        bool ordered = order.IndexOf(g) < order.IndexOf(r2);
        var nodeIds = ChannelDsp.Nodes(p);
        var spec = ChannelDsp.Bus(p);
        bool specs = spec.Nodes.Count == 4 && spec.Nodes[nodeIds.IndexOf(g.Id)].Output == spec.Nodes[nodeIds.IndexOf(ProjectBuses.ReverbId)].Output
            && ChannelDsp.Track(p, nodeIds, p.Tracks[0], [], -24) is { Output: var o, Sends: [{ Pre: true }] } && o == nodeIds.IndexOf(g.Id);
        bool removed = ProjectBuses.Remove(p, r2.Id) && g.Output == null && ch.SendTo(r2.Id) == null && !ProjectBuses.Remove(p, r2.Id);
        // taking a bus out of a track's list keeps its level, off; adding it back turns it on with that level
        var hideTrack = new Track { Name = "C" };
        p.Tracks.Add(hideTrack);
        var hideCh = hideTrack.Channel;
        ProjectBuses.SetSend(p, hideCh, ProjectBuses.ReverbId, 0.35);
        bool hides = ProjectBuses.Shown(p, hideCh).Count == p.Settings.Buses.Count
            && ProjectBuses.ShowSend(p, hideCh, ProjectBuses.ReverbId, false) && !ProjectBuses.Shown(p, hideCh).Exists(b => b.Id == ProjectBuses.ReverbId)
            && hideCh.SendTo(ProjectBuses.ReverbId) is { On: false, Hidden: true, Level: 0.35 } && !ProjectBuses.ShowSend(p, hideCh, ProjectBuses.ReverbId, false)
            && ProjectBuses.ShowSend(p, hideCh, ProjectBuses.ReverbId, true) && hideCh.SendTo(ProjectBuses.ReverbId) is { On: true, Hidden: false, Level: 0.35 }
            && ProjectBuses.ShowSend(p, hideCh, ProjectBuses.DelayId, false) && hideCh.SendTo(ProjectBuses.DelayId) is { Hidden: true, Level: 0 }
            && ProjectBuses.Shown(p, g).Count == 0;
        ProjectBuses.ShowSend(p, hideCh, ProjectBuses.DelayId, true);
        // a folder never sends pre-fader
        var folder = new Folder { Name = "F" };
        p.Folders.Add(folder);
        bool folderPost = ProjectBuses.SetSend(p, folder, ProjectBuses.DelayId, 0.4, pre: true) is { Pre: false };

        // a file with buses fed by buses (they go: a bus goes to the master), a send to nowhere and repeated ids is put right
        const string loopJson = """
            {"version":3,"settings":{"buses":[{"id":"bus-a","name":"A","output":"bus-b"},{"id":"bus-b","name":"B","sends":[{"busId":"bus-a","level":0.5}]},
              {"id":"bus-a","name":"Copia","output":"bus-x"}]},
             "tracks":[{"id":"trk-1","channel":{"output":"bus-x","sends":[{"busId":"bus-b","level":2},{"busId":"bus-b","level":0.1},{"busId":"bus-y","level":1}]}}]}
            """;
        var lp = ProjectStore.Parse(loopJson);
        var lch = lp.Tracks[0].Channel;
        bool fixedUp = lp.Settings.Buses.Select(b => b.Id).Distinct().Count() == 3 && lp.Settings.Buses.All(b => b.Output == null && b.Sends.Count == 0)
            && lch.Output == null && lch.Sends is [{ BusId: "bus-b", Level: 1 }];

        // version 2: the fixed reverb and delay and the extra returns become buses, the sends follow them
        const string v2 = """
            {"version":2,"settings":{"reverb":{"decay":3.1,"muted":true},"delay":{"division":"1/4"},
              "returns":[{"id":"ret-a","name":"Plate","inserts":[{"id":"fx-r","type":"reverb","reverb":{"decay":2}}]}]},
             "folders":[{"id":"fld-1","name":"G","volume":0.5,"muted":true,"inserts":[{"id":"fx-x","type":"eq"}]}],
             "tracks":[{"id":"trk-1","folderId":"fld-1","channel":{"reverbSend":0.4,"reverbPre":true,"delayOn":false,"delaySend":0.2,
               "sends":[{"returnId":"ret-a","level":0.5}]}}]}
            """;
        var old = ProjectStore.Parse(v2);
        var och = old.Tracks[0].Channel;
        bool converted = old.Settings.Buses.Select(b => b.Id).SequenceEqual([ProjectBuses.ReverbId, ProjectBuses.DelayId, "ret-a"])
            && old.Settings.Buses[0] is { Muted: true, Inserts: [{ Reverb: { Decay: 3.1, Muted: false } }] } && old.Settings.Buses[1].Inserts[0].Delay?.Division == "1/4"
            && och.SendTo(ProjectBuses.ReverbId) is { Level: 0.4, Pre: true, On: true } && och.SendTo(ProjectBuses.DelayId) is { Level: 0.2, On: false }
            && och.SendTo("ret-a") is { Level: 0.5 } && old.Folders[0].Extra?.ContainsKey("volume") != true;
        string resaved = JsonSerializer.Serialize(old, ProjectStore.Json);
        bool clean = !resaved.Contains("\"reverbSend\"") && !resaved.Contains("\"returnId\"") && !resaved.Contains("\"returns\"") && !resaved.Contains("\"volume\": 0.5");

        // saved and read back the same, and one undo step takes a new bus and its sends away (redo brings them back)
        var back = ProjectStore.Parse(JsonSerializer.Serialize(p, ProjectStore.Json));
        bool roundTrip = back.Settings.Buses.Select(b => b.Id + b.Name + b.Output).SequenceEqual(p.Settings.Buses.Select(b => b.Id + b.Name + b.Output))
            && back.Tracks[0].Channel.Output == ch.Output && back.Folders[0].Sends.Count == 1;
        var undo = new UndoManager();
        int count = p.Settings.Buses.Count;
        undo.Checkpoint(p);
        var temp = ProjectBuses.Add(p)!;
        ProjectBuses.SetSend(p, p.Tracks[1].Channel, temp.Id, 0.4);
        bool undone = undo.Undo(p) && p.Settings.Buses.Count == count && p.Tracks[1].Channel.Sends.Count == 0
            && undo.Redo(p) && p.Settings.Buses.Count == count + 1 && p.Tracks[1].Channel.SendTo(temp.Id) is { Level: 0.4 };
        Line("Buses: modelo, solo al master y versión 2", defaults && adds && loops && hides && ordered && specs && removed && folderPost && fixedUp && converted && clean && roundTrip && undone ? 0 : 1, 0, "bien");
        return allOk;
    }

    /// <summary>
    /// The mix as the live engine plays it: the tracks' chains and the nodes (<see cref="BusDspChain"/>, routed by
    /// reference) block by block, without the master (as <see cref="MixRenderer.Render"/> with <c>limit: false</c> and an
    /// off master).
    /// </summary>
    static (float[] Left, float[] Right) Live(MixTrackSpec[] tracks, MixNodeSpec[] specs, double sec, int sr)
    {
        var nodes = specs.Select(s => { var n = new BusDspChain(sr); n.Configure(s); return n; }).ToArray();
        for (int j = 0; j < nodes.Length; j++)
        {
            nodes[j].Out = specs[j].Output >= 0 ? nodes[specs[j].Output] : null;
            nodes[j].Sends = [.. specs[j].Sends.Select(s => new TrackSend(nodes[s.To], -1, (float)s.Level, s.Pre))];
        }
        var chains = tracks.Select(t =>
        {
            var c = new TrackDspChain(sr);
            c.Configure(t);
            c.Sends = [.. t.Sends.Select(s => new TrackSend(nodes[s.To], -1, (float)s.Level, s.Pre))];
            return c;
        }).ToArray();
        int frames = (int)Math.Ceiling(sec * sr);
        var outL = new float[frames];
        var outR = new float[frames];
        for (int pos = 0; pos < frames; pos += Block)
        {
            int n = Math.Min(Block, frames - pos);
            var ml = outL.AsSpan(pos, n);
            var mr = outR.AsSpan(pos, n);
            foreach (var node in nodes) node.Begin(n);
            for (int k = 0; k < chains.Length; k++)
            {
                var c = chains[k];
                TrackDspChain.SampleClips([.. tracks[k].Clips], pos, n, sr, c.BufferL(n), c.BufferR(n));
                var to = tracks[k].Output >= 0 ? nodes[tracks[k].Output] : null;
                c.ProcessFilledBuffer(n, false, to != null ? to.BufferL(n) : ml, to != null ? to.BufferR(n) : mr, out _, out _, out _);
                foreach (var s in c.BlockSends) c.AddSend(n, s.Level, s.Pre, s.To!.BufferL(n), s.To.BufferR(n));
            }
            foreach (var node in nodes) node.End(n, ml, mr);
        }
        return (outL, outR);
    }

    static double MaxDiff(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 1;
        double d = 0;
        for (int i = 0; i < a.Length; i++) d = Math.Max(d, Math.Abs(a[i] - b[i]));
        return d;
    }
}
