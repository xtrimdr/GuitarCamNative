using System.Globalization;
using System.Text.Json;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>
/// gclab's side of the extra returns and the sends: <c>proyecto retornos</c> lists and edits the returns the user added
/// (like the mixer's return strips), <c>proyecto envios</c> a track's sends, the fixed reverb's and delay's included (like
/// the send panels and their PRE chip), and <see cref="Check"/> (part of <c>comprobar-dsp</c>) verifies that the extra
/// plumbing changes nothing until it is used, that a pre-fader send ignores the fader, that a return with one reverb or
/// delay slot sounds exactly like the fixed return, and the model's normalizing.
/// </summary>
static class ReturnCommands
{
    const int Block = 128;

    /// <summary>gclab proyecto retornos carpeta [agregar [nombre] [--tipo reverb|delay] | quitar n | renombrar n nombre | volumen n x | mute n]</summary>
    public static int Returns(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        string op = a.Positional.ElementAtOrDefault(2) ?? "";
        string v1 = a.Positional.ElementAtOrDefault(3) ?? "";
        string v2 = a.Positional.ElementAtOrDefault(4) ?? "";
        ReturnBus Find(string n) => ProjectReturns.Find(p, n) ?? throw new ArgumentException($"retornos: no hay un retorno «{n}» (son {p.Settings.Returns.Count}).");
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
                    var k => throw new ArgumentException($"retornos: --tipo reverb o delay, no «{k}»."),
                };
                var added = ProjectReturns.Add(p, kind, v1 == "" ? null : v1)!;
                Console.WriteLine($"Agregado: {added.Name} [{added.Id}]");
                changed = true;
                break;
            case "quitar":
                changed = ProjectReturns.Remove(p, Find(v1).Id);
                break;
            case "renombrar":
                changed = ProjectReturns.Rename(p, Find(v1).Id, v2);
                break;
            case "volumen":
                var rv = Find(v1);
                double vol = double.Parse(v2.Replace(',', '.'), CultureInfo.InvariantCulture);
                changed = rv.Volume != Math.Clamp(vol, 0, 1.5);
                rv.Volume = Math.Clamp(vol, 0, 1.5);
                break;
            case "mute":
                var rm = Find(v1);
                rm.Muted = !rm.Muted;
                changed = true;
                break;
            default:
                throw new ArgumentException("retornos: agregar [nombre] [--tipo reverb|delay] | quitar n | renombrar n nombre | volumen n x | mute n");
        }
        if (changed)
        {
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            store.Save();
        }
        var list = p.Settings.Returns;
        Console.WriteLine($"Retornos fijos: Reverb ({(p.Settings.Reverb.Muted ? "mute" : $"{p.Settings.Reverb.ReturnGain * 100:0}%")}) · Delay ({(p.Settings.Delay.Muted ? "mute" : $"{p.Settings.Delay.ReturnGain * 100:0}%")})");
        Console.WriteLine($"Retornos agregados: {list.Count}");
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            int senders = p.Tracks.Count(t => t.Channel.SendTo(r.Id) is { On: true, Level: > 0 });
            Console.WriteLine($"  {i + 1}. {r.Name}  {Fmt(r.Volume)}{(r.Muted ? " · mute" : "")} · {(senders == 1 ? "1 pista le manda" : $"{senders} pistas le mandan")}  [{r.Id}]");
            for (int k = 0; k < r.Inserts.Count; k++) Console.WriteLine($"       {k + 1}. {InsertCommands.Describe(r.Inserts[k])}");
        }
        return 0;
    }

    /// <summary>gclab proyecto envios carpeta pista [reverb|delay|retorno [nivel] [--pre|--post] [--apagar|--prender]]</summary>
    public static int Sends(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        string trackId = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("envios: falta la pista (gclab proyecto clips carpeta).");
        var track = p.Tracks.Find(t => t.Id == trackId)
            ?? (int.TryParse(trackId, out int tn) && tn >= 1 && tn <= p.Tracks.Count ? p.Tracks[tn - 1] : null)
            ?? throw new ArgumentException("No encontré esa pista.");
        var ch = track.Channel;
        string target = a.Positional.ElementAtOrDefault(3) ?? "";
        double? level = a.Positional.ElementAtOrDefault(4) is { } lv ? double.Parse(lv.Replace(',', '.'), CultureInfo.InvariantCulture) : null;
        bool? pre = a.Flag("pre") ? true : a.Flag("post") ? false : null;
        bool? on = a.Flag("apagar") ? false : a.Flag("prender") ? true : null;
        // like the knob: turning the level up turns the send on
        if (level is > 0 && on == null) on = true;
        if (target != "")
        {
            switch (target.ToLowerInvariant())
            {
                case "reverb":
                    if (level is { } r) ch.ReverbSend = Math.Clamp(r, 0, 1);
                    if (on is { } ro) ch.ReverbOn = ro;
                    if (pre is { } rp) ch.ReverbPre = rp;
                    break;
                case "delay":
                    if (level is { } d) ch.DelaySend = Math.Clamp(d, 0, 1);
                    if (on is { } dOn) ch.DelayOn = dOn;
                    if (pre is { } dp) ch.DelayPre = dp;
                    break;
                default:
                    var ret = ProjectReturns.Find(p, target) ?? throw new ArgumentException($"envios: no hay un retorno «{target}»: reverb, delay o uno de «proyecto retornos».");
                    ProjectReturns.SetSend(p, ch, ret.Id, level, on, pre);
                    break;
            }
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            store.Save();
        }
        Console.WriteLine($"{track.Name}: envíos (PRE = antes del fader)");
        Console.WriteLine($"  Reverb  {Send(ch.ReverbSend, ch.ReverbOn, ch.ReverbPre)}");
        Console.WriteLine($"  Delay   {Send(ch.DelaySend, ch.DelayOn, ch.DelayPre)}");
        for (int i = 0; i < p.Settings.Returns.Count; i++)
        {
            var r = p.Settings.Returns[i];
            var (l, o, x) = ProjectReturns.SendOf(ch, r.Id);
            Console.WriteLine($"  {i + 1}. {r.Name}  {Send(l, o, x)}");
        }
        return 0;
    }

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
        MixBusSpec Bus(params MixReturnSpec[] returns) => new() { Reverb = rev, Delay = dly, Bpm = bpm, Returns = returns };
        var revSlot = new InsertSpec("r", TrackInserts.Reverb) { Reverb = rev };
        var dlySlot = new InsertSpec("d", TrackInserts.Delay) { Delay = dly, Bpm = bpm };
        double Diff((float[] Left, float[] Right) a, (float[] Left, float[] Right) b) => Math.Max(MaxDiff(a.Left, b.Left), MaxDiff(a.Right, b.Right));

        // (a) the plumbing changes nothing until it is used: no extra returns and every send post = a return nobody
        // sends to, sends that are off or at 0, and the default (post) flags written out
        MixTrackSpec[] Plain() =>
        [
            new() { Clips = [cx], Inserts = [], Volume = 0.8, Pan = -0.3, ReverbSend = 0.35, DelaySend = 0.1 },
            new() { Clips = [cy], Inserts = [], Volume = 0.6, Pan = 0.5, ReverbSend = 0.2, DelaySend = 0.3 },
        ];
        var plain = MixRenderer.Render(Plain(), Bus(), 4, sr);
        MixTrackSpec[] Unused() =>
        [
            new() { Clips = [cx], Inserts = [], Volume = 0.8, Pan = -0.3, ReverbSend = 0.35, DelaySend = 0.1, ReverbPre = false, DelayPre = false,
                Sends = [new() { Return = 0, Level = 0.5, On = false }, new() { Return = 1, Level = 0 }] },
            new() { Clips = [cy], Inserts = [], Volume = 0.6, Pan = 0.5, ReverbSend = 0.2, DelaySend = 0.3, Sends = [new() { Return = 7, Level = 1 }] },
        ];
        var unused = MixRenderer.Render(Unused(), Bus(new MixReturnSpec { Inserts = [revSlot] }, new MixReturnSpec { Inserts = [dlySlot] }), 4, sr);
        Line("Retornos: sin usar = como antes", Diff(plain, unused), 0, "exacto");

        // (b) pre-fader: the send ignores the fader (and the folder's VCA, which is part of it); post follows it; mute
        // silences both. Measured at the return's output, through the live classes (TrackDspChain + ReturnDspChain).
        float[] ReturnOut(double volume, bool pre, bool muted, bool fixedReverb)
        {
            var chain = new TrackDspChain(sr) { Volume = volume, Pan = 0.2, Muted = muted, ReverbPre = pre, ReverbSend = fixedReverb ? 0.5 : 0 };
            chain.SetInserts([], fade: false);
            var ret = new ReturnDspChain(sr);
            ret.SetInserts([revSlot], fade: false);
            chain.Sends = fixedReverb ? [] : [new TrackSend(ret, -1, 0.5f, pre)];
            var outL = new float[x.Length];
            var sink = new float[Block];
            var revL = new float[Block];
            for (int pos = 0; pos < x.Length; pos += Block)
            {
                int n = Math.Min(Block, x.Length - pos);
                x.AsSpan(pos, n).CopyTo(chain.BufferL(n));
                y.AsSpan(pos, n).CopyTo(chain.BufferR(n));
                Array.Clear(revL);
                ret.Begin(n);
                chain.ProcessFilledBuffer(n, false, sink.AsSpan(0, n), sink.AsSpan(0, n), revL.AsSpan(0, n), sink.AsSpan(0, n), sink.AsSpan(0, n), sink.AsSpan(0, n), out _, out _, out _);
                if (fixedReverb) revL.AsSpan(0, n).CopyTo(outL.AsSpan(pos, n));
                else
                {
                    foreach (var s in chain.BlockSends) chain.AddSend(n, s.Level, s.Pre, ret.BufferL(n), ret.BufferR(n));
                    ret.End(n, outL.AsSpan(pos, n), sink.AsSpan(0, n));
                }
            }
            return outL;
        }
        static double Peak(float[] a) => a.Max(v => Math.Abs(v));
        var preLoud = ReturnOut(1, true, false, false);
        var preLow = ReturnOut(0.3, true, false, false);
        var postLoud = ReturnOut(1, false, false, false);
        var postLow = ReturnOut(0.3, false, false, false);
        bool postFollows = Math.Abs(Peak(postLow) / Peak(postLoud) - 0.3) < 0.01;
        bool mutedSilent = Peak(ReturnOut(1, true, true, false)) == 0 && Peak(ReturnOut(1, false, true, false)) == 0;
        Line("Retornos: PRE no sigue al fader", postFollows && mutedSilent ? MaxDiff(preLoud, preLow) : 1, 0, "exacto");
        // the fixed reverb's PRE too (its send buffer), and post at full volume = pre
        var fixedPreLoud = ReturnOut(1, true, false, true);
        var fixedPreLow = ReturnOut(0.3, true, false, true);
        bool fixedPostFollows = Math.Abs(Peak(ReturnOut(0.3, false, false, true)) / Peak(fixedPreLoud) - 0.3) < 0.01;
        Line("Retornos: PRE de la Reverb fija", fixedPostFollows ? Math.Max(MaxDiff(fixedPreLoud, fixedPreLow), MaxDiff(preLoud, postLoud)) : 1, 0, "exacto");

        // (c) a return whose chain is one reverb (or delay) slot with the fixed one's settings = the fixed return fed
        // the same send, through the whole export, stems included
        MixTrackSpec[] ToFixed(bool reverb) =>
        [
            new() { Clips = [cx], Inserts = [], Volume = 0.8, Pan = -0.3, ReverbSend = reverb ? 0.4 : 0, DelaySend = reverb ? 0 : 0.4 },
            new() { Clips = [cy], Inserts = [], Volume = 0.6, Pan = 0.5, ReverbSend = reverb ? 0.25 : 0, DelaySend = reverb ? 0 : 0.25, DelayPre = true, ReverbPre = true },
        ];
        MixTrackSpec[] ToExtra() =>
        [
            new() { Clips = [cx], Inserts = [], Volume = 0.8, Pan = -0.3, Sends = [new() { Return = 0, Level = 0.4 }] },
            new() { Clips = [cy], Inserts = [], Volume = 0.6, Pan = 0.5, Sends = [new() { Return = 0, Level = 0.25, Pre = true }] },
        ];
        // the same tracks without any send
        (float[] Left, float[] Right) dryMixOf(MixTrackSpec[] tracks) =>
            MixRenderer.Render(tracks.Select(t => new MixTrackSpec { Clips = t.Clips, Inserts = [], Volume = t.Volume, Pan = t.Pan }).ToArray(), Bus(), 6, sr);
        var fixedRev = MixRenderer.Render(ToFixed(true), Bus(), 6, sr);
        var extraRev = MixRenderer.Render(ToExtra(), Bus(new MixReturnSpec { Name = "Reverb 2", Inserts = [revSlot] }), 6, sr);
        bool revHeard = Diff(fixedRev, dryMixOf(ToFixed(true))) > 1e-3;
        Line("Retornos: Reverb en slot = Reverb fija", revHeard ? Diff(fixedRev, extraRev) : 1, 0, "exacto");
        var fixedDly = MixRenderer.Render(ToFixed(false), Bus(), 6, sr);
        var extraDly = MixRenderer.Render(ToExtra(), Bus(new MixReturnSpec { Name = "Delay 2", Inserts = [dlySlot] }), 6, sr);
        bool dlyHeard = Diff(fixedDly, dryMixOf(ToFixed(false))) > 1e-3;
        Line("Retornos: Delay en slot = Delay fijo", dlyHeard ? Diff(fixedDly, extraDly) : 1, 0, "exacto");
        var stemFixed = MixRenderer.Render(ToFixed(true), Bus(), 6, sr, only: 1);
        var stemExtra = MixRenderer.Render(ToExtra(), Bus(new MixReturnSpec { Inserts = [revSlot] }), 6, sr, only: 1);
        Line("Retornos: stem con su retorno", Diff(stemFixed, stemExtra), 0, "exacto");
        // the return's fader and mute: at 0 (or muted) it is as if nobody sent to it
        var dryMix = dryMixOf(ToFixed(true));
        var mutedRet = MixRenderer.Render(ToExtra(), Bus(new MixReturnSpec { Inserts = [revSlot], Muted = true }), 6, sr);
        Line("Retornos: retorno en mute = sin él", Diff(dryMix, mutedRet), 1e-7, "~0");

        // (d) the model: a send to a removed return is dropped, one send per return, defaults, names and the specs
        const string json = """
            {"settings":{"returns":[{"id":"ret-a","name":"Plate","inserts":[{"id":"fx-r","type":"reverb","reverb":{"decay":3}}]},
                                    {"id":"ret-a","name":"Copia"}]},
             "tracks":[{"id":"trk-1","channel":{"reverbPre":true,
                "sends":[{"returnId":"ret-a","level":0.5,"pre":true},{"returnId":"ret-a","level":0.9},{"returnId":"ret-x","level":0.3},{"returnId":"","level":1},{"returnId":"ret-a","level":4}]}}]}
            """;
        var p = ProjectStore.Parse(json);
        var ch = p.Tracks[0].Channel;
        bool normalized = p.Settings.Returns.Count == 2 && p.Settings.Returns[0].Id != p.Settings.Returns[1].Id
            && ch.Sends.Count == 1 && ch.Sends[0] is { ReturnId: "ret-a", Level: 0.5, Pre: true, On: true } && ch.ReverbPre && !ch.DelayPre
            && p.Settings.Returns[0].Inserts[0] is { Type: InsertSlot.ReverbKind, On: true, Reverb.Decay: 3 };
        var added = ProjectReturns.Add(p);
        var added2 = ProjectReturns.Add(p, InsertSlot.DelayKind);
        var added3 = ProjectReturns.Add(p);
        bool adds = added is { Name: "Reverb 2", Inserts: [{ Type: InsertSlot.ReverbKind, On: true }] } && added2 is { Name: "Delay 2", Inserts: [{ Type: InsertSlot.DelayKind }] }
            && added3?.Name == "Reverb 3" && ProjectReturns.Add(p, InsertSlot.EqKind) == null;
        var send = ProjectReturns.SetSend(p, ch, added!.Id, level: 0.7);
        var specsBefore = ChannelDsp.Sends(p, ch);
        bool removed = send is { Level: 0.7, On: true, Pre: false } && specsBefore.Length == 2 && specsBefore[1].Return == 2
            && ProjectReturns.Remove(p, added.Id) && ch.SendTo(added.Id) == null && ChannelDsp.Sends(p, ch).Length == 1
            && ProjectReturns.SetSend(p, ch, "ret-nada", 1) == null && !ProjectReturns.Remove(p, added.Id);
        // a return's slots: the delay follows the tempo, an off reverb is bypassed
        p.Settings.Bpm = 140;
        var specs = ChannelDsp.Returns(p);
        bool returnSpecs = specs.Length == 4 && specs[2].Inserts[0] is { Kind: TrackInserts.Delay, Bpm: 140 };
        var off = new InsertSpec("o", TrackInserts.Reverb) { Reverb = rev with { Muted = true } };
        var passed = new ReturnDspChain(sr);
        passed.SetInserts([off], fade: false);
        var probe = new float[Block];
        var probeR = new float[Block];
        Array.Fill(probe, 0.25f);
        passed.Process(probe, probeR);
        bool bypass = probe.All(v => v == 0.25f);
        // saved and read back, returns and sends stay the same
        string saved = JsonSerializer.Serialize(p, ProjectStore.Json);
        var back = ProjectStore.Parse(saved);
        bool roundTrip = back.Settings.Returns.Select(r => r.Id + r.Name).SequenceEqual(p.Settings.Returns.Select(r => r.Id + r.Name))
            && back.Tracks[0].Channel.Sends.Count == ch.Sends.Count && back.Tracks[0].Channel.ReverbPre;
        // one undo step takes a new return and its sends away, and redo brings both back
        var undo = new UndoManager();
        int count = p.Settings.Returns.Count, sendCount = ch.Sends.Count;
        undo.Checkpoint(p);
        var temp = ProjectReturns.Add(p)!;
        ProjectReturns.SetSend(p, p.Tracks[0].Channel, temp.Id, 0.4);
        bool undone = undo.Undo(p) && p.Settings.Returns.Count == count && p.Tracks[0].Channel.Sends.Count == sendCount
            && undo.Redo(p) && p.Settings.Returns.Count == count + 1 && p.Tracks[0].Channel.SendTo(temp.Id) is { Level: 0.4 };
        Line("Retornos: modelo y envíos", normalized && adds && removed && returnSpecs && bypass && roundTrip && undone ? 0 : 1, 0, "bien");
        return allOk;
    }

    static double MaxDiff(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 1;
        double d = 0;
        for (int i = 0; i < a.Length; i++) d = Math.Max(d, Math.Abs(a[i] - b[i]));
        return d;
    }
}
