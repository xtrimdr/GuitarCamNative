using System.Globalization;
using GuitarCam.Audio.Dsp;
using GuitarCam.Audio.Midi;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>
/// MIDI tracks, clips and notes without the window (stage 1 of the MIDI editor), and the checks of the notes' sound:
/// the test tone is the same in any block size and from any position, and the export plays what the live engine does.
/// <c>gclab mezclar carpeta</c> renders a project with its MIDI tracks.
/// </summary>
static class MidiCommands
{
    public static int Run(Args a)
    {
        if (a.Get("datos") is { } data) AppConfig.DirectoryOverride = Path.GetFullPath(data);
        string sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (sub == "comprobar") return Check() ? 0 : 3;
        if (sub == "entradas") return Inputs();
        if (sub == "escuchar") return Listen(a.Get("entrada"), a.Double("segundos", 10));
        string? dir = a.Positional.Skip(1).FirstOrDefault();
        if (dir == null) throw new ArgumentException("midi ver|pista|clip|nota|acordes|cuantizar|velocidad|humanizar|quitar|instrumento <carpeta> (o midi comprobar, entradas, escuchar)");
        using var store = ProjectStore.Open(Path.GetFullPath(dir));
        var p = store.Project;
        if (sub != "ver" && store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        switch (sub)
        {
            case "ver": break;
            case "pista":
                Console.WriteLine(MidiEditing.AddTrack(p));
                break;
            case "clip":
            {
                var t = TrackOf(p, a.Get("pista")) ?? throw new ArgumentException(a.Get("pista") == null
                    ? "No hay pistas MIDI: agregá una con midi pista."
                    : $"No encontré la pista {a.Get("pista")} (--pista: su número, contando todas, o su id).");
                var c = MidiEditing.AddClip(p, t.Id, a.Double("en", 0), a.Double("compases", MidiClip.DefaultBeats / 4) * 4);
                Console.WriteLine(c.Id);
                break;
            }
            case "nota":
            {
                string clip = a.Get("clip") ?? throw new ArgumentException("Falta --clip.");
                int pitch = MidiNote.Parse(a.Get("altura", "60")) ?? throw new ArgumentException("--altura: un número de 0 a 127 o una nota como C4 o F#2.");
                MidiEditing.AddNote(p, clip, pitch, a.Double("pulso", 0), a.Double("largo", 1), a.Int("vel", MidiNote.DefaultVelocity));
                break;
            }
            case "acordes":
            {
                // C, G, Am, F: one per bar, as a quick backing track to hear the tone and the tempo
                string clip = a.Get("clip") ?? throw new ArgumentException("Falta --clip.");
                int[][] chords = [[48, 60, 64, 67], [43, 59, 62, 67], [45, 60, 64, 69], [41, 60, 65, 69]];
                var notes = new List<MidiNote>();
                for (int bar = 0; bar < chords.Length; bar++)
                    foreach (int pitch in chords[bar]) notes.Add(new MidiNote { Pitch = pitch, Beat = bar * 4, Beats = 3.75, Velocity = 90 });
                MidiEditing.SetNotes(p, clip, notes);
                break;
            }
            case "quitar":
                MidiEditing.RemoveClip(p, a.Get("clip") ?? throw new ArgumentException("Falta --clip."));
                break;
            case "cuantizar":
            {
                string clip = a.Get("clip") ?? throw new ArgumentException("Falta --clip.");
                double grid = Grid(a.Get("grilla", "1/16")) ?? throw new ArgumentException("--grilla: 1/4, 1/8, 1/16, 1/32 o 1/8T.");
                Console.WriteLine($"{MidiEditing.Quantize(p, clip, grid)} notas movidas a {a.Get("grilla", "1/16")}.");
                break;
            }
            case "velocidad":
            {
                string clip = a.Get("clip") ?? throw new ArgumentException("Falta --clip.");
                var (_, c) = MidiEditing.Find(p, clip);
                Console.WriteLine($"{MidiEditing.ScaleVelocity(c.Notes, a.Double("rango", 100))} notas cambiadas.");
                break;
            }
            case "humanizar":
            {
                string clip = a.Get("clip") ?? throw new ArgumentException("Falta --clip.");
                var (_, c) = MidiEditing.Find(p, clip);
                var random = a.Get("semilla") != null ? new Random(a.Int("semilla", 0)) : new Random();
                Console.WriteLine($"{MidiEditing.HumanizeVelocity(c.Notes, a.Int("cantidad", 10), random)} notas cambiadas.");
                break;
            }
            case "instrumento":
            {
                // the plugin that plays the track (from vst escanear's list); --quitar goes back to the test tone
                var t = TrackOf(p, a.Get("pista")) ?? throw new ArgumentException("Falta --pista (número o id de una pista MIDI).");
                if (!t.IsMidi) throw new ArgumentException("Esa pista no es MIDI.");
                if (a.Flag("quitar")) t.Instrument = null;
                else
                {
                    string name = a.Get("plugin") ?? throw new ArgumentException("Falta --plugin \"nombre\" (o --quitar).");
                    var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()).Where(x => x.IsInstrument), name)
                        ?? throw new ArgumentException($"No encontré el instrumento \"{name}\": probá con vst escanear.");
                    t.Instrument = PluginCatalog.Ref(plugin);
                }
                break;
            }
            default:
                throw new ArgumentException("midi ver|pista|clip|nota|acordes|quitar <carpeta>");
        }
        if (sub != "ver")
        {
            p.AddLog("midi-edited", sub);
            store.Save();
        }
        Show(p);
        return 0;
    }

    /// <summary>The piano roll's magnet steps in beats: "1/16" = 0.25, "1/8T" = a third.</summary>
    static double? Grid(string s) => s.ToLowerInvariant() switch
    {
        "1/4" => 1, "1/8" => 0.5, "1/16" => 0.25, "1/32" => 0.125, "1/8t" => 1.0 / 3, _ => null,
    };

    /// <summary>The MIDI inputs Windows has, with what Configuración → MIDI says of each one.</summary>
    static int Inputs()
    {
        var settings = MidiSettings.Parse(AppConfig.Get("midi"));
        var devices = MidiInputs.Devices();
        if (devices.Count == 0) Console.WriteLine("Windows no ve ninguna entrada MIDI: enchufá el teclado (o su interfaz) y probá de nuevo.");
        foreach (var d in devices)
        {
            var s = settings.Find(d.Name);
            string uses = s == null ? "" : string.Join("+", new[] { s.Notes ? "Notas" : null, s.Knobs ? "Perillas" : null, s.Transport ? "Transporte" : null }.Where(x => x != null));
            string how = s == null ? "sin configurar (prendida, todos los canales, Notas y Perillas)"
                : $"{(s.Enabled ? "prendida" : "apagada")} · {(s.Channel == 0 ? "todos los canales" : $"canal {s.Channel}")} · velocidad {s.Velocity} · {uses}"
                  + (s.Alias != null ? $" · «{s.Alias}»" : "");
            Console.WriteLine($"{d.Index + 1}. {d.Name} · {how}");
        }
        foreach (var gone in settings.Inputs.Where(i => devices.All(d => d.Name != i.Name)))
            Console.WriteLine($"-. {gone.Name} · desconectada (sus ajustes quedan guardados)");
        Console.WriteLine($"Cuantizar al grabar: {(settings.QuantizeOnRecord ? "sí" : "no")}");
        return 0;
    }

    /// <summary>
    /// Opens the inputs as the window does and prints everything that arrives, raw and as the engine takes it
    /// (after the channel and the velocity curve), for <paramref name="seconds"/>.
    /// </summary>
    static int Listen(string? only, double seconds)
    {
        var settings = MidiSettings.Parse(AppConfig.Get("midi"));
        using var inputs = new MidiInputs();
        var ports = inputs.Apply(settings);
        if (only != null)
            ports = [.. ports.Where(p => p.Device.Name.Contains(only, StringComparison.OrdinalIgnoreCase) || p.Settings.Shown.Contains(only, StringComparison.OrdinalIgnoreCase))];
        if (ports.Count == 0)
        {
            Console.WriteLine(only == null ? "No hay entradas MIDI." : $"No encontré la entrada «{only}» (midi entradas las lista).");
            return 1;
        }
        foreach (var p in ports)
            Console.WriteLine($"{p.Device.Name}: {(p.Open ? "abierta" : p.Error ?? "apagada en Configuración")}");
        Console.WriteLine($"Escuchando {seconds:0.#} s: tocá algo.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        inputs.Received += (port, raw) =>
        {
            if ((raw & 0xFF) >= 0xF0 || !ports.Contains(port)) return;
            Console.WriteLine($"{sw.Elapsed.TotalSeconds,8:0.000}  {port.Settings.Shown,-24} {Describe(raw)}");
        };
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(20);
            while (inputs.TryRead(out var e))
            {
                string note = e.Kind is MidiKind.NoteOn or MidiKind.NoteOff ? MidiNote.Name(e.Pitch) + " " : "";
                Console.WriteLine($"{"",8}  {"→ al motor:",-24} {e.Kind} {note}vel {e.Velocity} canal {e.Channel}");
            }
        }
        return 0;
    }

    /// <summary>A raw message in words: "nota C4 vel 96 · canal 1".</summary>
    static string Describe(int raw)
    {
        int status = raw & 0xFF, d1 = (raw >> 8) & 0x7F, d2 = (raw >> 16) & 0x7F, ch = (status & 0x0F) + 1;
        string what = (status & 0xF0) switch
        {
            0x90 when d2 > 0 => $"nota {MidiNote.Name(d1)} vel {d2}",
            0x80 or 0x90 => $"suelta {MidiNote.Name(d1)}",
            0xB0 => $"CC {d1} = {d2}",
            0xE0 => $"pitch bend {((d2 << 7) | d1) - 8192}",
            0xC0 => $"programa {d1 + 1}",
            0xD0 => $"aftertouch {d1}",
            0xA0 => $"aftertouch {MidiNote.Name(d1)} {d2}",
            _ => $"0x{raw:X6}",
        };
        return $"{what} · canal {ch}";
    }

    /// <summary>A MIDI track by its number among all tracks (1 = the first) or its id.</summary>
    static Track? TrackOf(Project p, string? key)
    {
        if (key == null) return p.Tracks.FirstOrDefault(t => t.IsMidi);
        if (int.TryParse(key, out int n)) return n >= 1 && n <= p.Tracks.Count ? p.Tracks[n - 1] : null;
        return p.Tracks.Find(t => t.Id == key);
    }

    static void Show(Project p)
    {
        double bpm = p.Settings.Bpm;
        var midi = p.Tracks.Where(t => t.IsMidi).ToList();
        if (midi.Count == 0) { Console.WriteLine("Sin pistas MIDI."); return; }
        foreach (var t in midi)
        {
            Console.WriteLine($"{p.Tracks.IndexOf(t) + 1}. {t.Id} · {t.Name} · {(t.Instrument is { } i ? i.Name + (i.State != null ? " (con su estado)" : "") : "tono de prueba")}");
            foreach (var c in t.MidiClips ?? [])
            {
                Console.WriteLine($"   {c.Id} · {c.AtSec:0.000}–{c.EndSec(bpm):0.000} s · {c.LengthBeats / 4:0.##} compases · {c.Notes.Count} notas");
                foreach (var n in c.Notes)
                    Console.WriteLine($"      {MidiNote.Name(n.Pitch),-4} pulso {n.Beat,6:0.###} · largo {n.Beats:0.###} · vel {n.Velocity}");
            }
        }
    }

    /// <summary>The notes' sound: block size and position don't change it, the release ends, and the export matches.</summary>
    public static bool Check()
    {
        const int sr = 48000;
        bool ok = true;
        var seq = new NoteSequence(
        [
            new NoteEvent(0, sr / 2, NoteSynth.Hz(60), NoteSynth.Gain(100)),
            new NoteEvent(sr / 4, sr, NoteSynth.Hz(64), NoteSynth.Gain(80)),
            new NoteEvent(sr, sr / 3, NoteSynth.Hz(67), NoteSynth.Gain(127)),
        ], sr);
        int len = (int)seq.End + 1000;

        float[] Whole(int block)
        {
            var l = new float[len];
            var r = new float[len];
            for (int at = 0; at < len; at += block)
            {
                int n = Math.Min(block, len - at);
                NoteSynth.Render(seq, at, n, sr, l.AsSpan(at, n), r.AsSpan(at, n));
            }
            return l;
        }
        var a = Whole(37);
        var b = Whole(4096);
        double diff = 0, peak = 0;
        for (int i = 0; i < len; i++)
        {
            diff = Math.Max(diff, Math.Abs(a[i] - b[i]));
            peak = Math.Max(peak, Math.Abs(a[i]));
        }
        bool sameOk = diff == 0;
        ok &= sameOk;
        Console.WriteLine($"{"MIDI: igual en bloques de 37 y 4096",-36} {diff,12:0.0e0} {"0",10}  {(sameOk ? "igual" : "DISTINTO")}");

        // silent before the first note, past every release, and never above 0 dBFS with a chord of three
        bool tailOk = a[(int)seq.End + 10] == 0 && peak is > 0.05 and < 1;
        ok &= tailOk;
        Console.WriteLine($"{"MIDI: termina después del release",-36} {peak,12:0.000} {"< 1",10}  {(tailOk ? "igual" : "DISTINTO")}");

        // the export (MixRenderer) plays the notes through the same chain as live playback
        var mix = MixRenderer.Render([new MixTrackSpec { Notes = seq, Inserts = [] }], new MixBusSpec(), len / (double)sr, sr, limit: false);
        double mixDiff = 0;
        for (int i = 0; i < len; i++) mixDiff = Math.Max(mixDiff, Math.Abs(mix.Left[i] - a[i]));
        bool mixOk = mixDiff < 1e-6;
        ok &= mixOk;
        Console.WriteLine($"{"MIDI: la exportación suena igual",-36} {mixDiff,12:0.0e0} {"< 1e-6",10}  {(mixOk ? "igual" : "DISTINTO")}");

        // the project's clips become notes in frames: tempo, clip start and the clip's end cutting a long note
        var p = new Project();
        p.Settings.Bpm = 120;
        string tid = MidiEditing.AddTrack(p);
        var clip = MidiEditing.AddClip(p, tid, 1, 4);
        MidiEditing.AddNote(p, clip.Id, 69, 1, 8);
        MidiEditing.AddNote(p, clip.Id, 72, 5);
        var built = ChannelDsp.Notes(p.Tracks[0], 120, sr);
        bool convOk = built is { Notes: [var only] } && only.Start == (long)(1.5 * sr) && only.Length == (long)(1.5 * sr)
            && Math.Abs(only.Hz - 440) < 1e-9;
        ok &= convOk;
        Console.WriteLine($"{"MIDI: clip a notas (tempo y cortes)",-36} {built?.Notes.Length ?? 0,12} {"1",10}  {(convOk ? "igual" : "DISTINTO")}");

        // copy and paste: the notes go with the copy, MIDI clips only onto MIDI tracks, and to another project too
        var cp = new Project();
        cp.Settings.Bpm = 120;
        string m1 = MidiEditing.AddTrack(cp), m2 = MidiEditing.AddTrack(cp), audio = ClipEditing.AddEmpty(cp, false);
        var source = MidiEditing.AddClip(cp, m1, 1, 4);
        MidiEditing.AddNote(cp, source.Id, 60, 0);
        MidiEditing.AddNote(cp, source.Id, 64, 2);
        var board = ClipClipboard.Take(cp, [source.Id])!;
        var (pastedTrack, pastedClip) = MidiEditing.Find(cp, board.Paste(cp, m2, 3)[0]);
        bool refused;
        try { board.Paste(cp, audio, 0); refused = false; }
        catch (ArgumentException) { refused = true; }
        var other = new Project();
        MidiEditing.AddTrack(other);
        bool crossOk = MidiEditing.TryFind(other, board.Paste(other, null, 0)[0]) is { Notes.Count: 2 };
        bool pasteOk = pastedTrack.Id == m2 && pastedClip.AtSec == 3 && pastedClip.Id != source.Id && pastedClip.Notes.Count == 2
            && pastedClip.Notes[0] != source.Notes[0] && refused && crossOk;
        ok &= pasteOk;
        Console.WriteLine($"{"MIDI: copiar y pegar clips",-36} {pastedClip.Notes.Count,12} {"2",10}  {(pasteOk ? "igual" : "DISTINTO")}");

        ok &= CheckLive(sr);

        bool parseOk =MidiNote.Parse("C4") == 60 && MidiNote.Parse("f#2") == 42 && MidiNote.Parse("Eb3") == 51 && MidiNote.Name(61) == "C#4"
            && MidiNote.Parse("H2") == null;
        ok &= parseOk;
        Console.WriteLine($"{"MIDI: nombres de notas",-36} {"C4 = 60",12} {"",10}  {(parseOk ? "igual" : "DISTINTO")}");
        return ok;
    }

    /// <summary>
    /// The keyboard: a key played live sounds as the same note written in a clip, the sustain pedal holds what is let
    /// go, a take pairs its notes, quantizing goes to the song's grid and a recorded clip starts on the bar.
    /// </summary>
    static bool CheckLive(int sr)
    {
        bool ok = true;
        void Show(string what, string value, string want, bool good)
        {
            ok &= good;
            Console.WriteLine($"{what,-36} {value,12} {want,10}  {(good ? "igual" : "DISTINTO")}");
        }

        // held for 84 blocks of 192 (a third of a second), then let go: the same samples as a clip's note of that length
        const int block = 192;
        long held = 84 * block;
        int len = (int)(held + NoteSynth.ReleaseFrames(sr) + 2000);
        var live = new float[len];
        var liveR = new float[len];
        var keys = new LiveNotes(sr);
        keys.Press(64, 90);
        for (int at = 0; at < len; at += block)
        {
            int n = Math.Min(block, len - at);
            keys.BeginBlock();
            if (at == held) keys.Release(64);
            keys.Render(n, live.AsSpan(at, n), liveR.AsSpan(at, n));
        }
        var clip = new NoteSequence([new NoteEvent(0, held, NoteSynth.Hz(64), NoteSynth.Gain(90))], sr);
        var written = new float[len];
        NoteSynth.Render(clip, 0, len, sr, written, new float[len]);
        double diff = 0;
        for (int i = 0; i < len; i++) diff = Math.Max(diff, Math.Abs(live[i] - written[i]));
        Show("Teclado: suena como la nota escrita", $"{diff:0.0e0}", "< 1e-6", diff < 1e-6 && held % block == 0 && !keys.AnySounding);

        // the pedal: a key let go while it is down keeps sounding until it comes up; the same key again starts over
        var p = new LiveNotes(sr);
        p.BeginBlock();
        p.Pedal(true);
        p.Press(60, 100);
        p.Release(60);
        bool stillOn = p.AnySounding && p.EventCount == 1;
        p.BeginBlock();
        p.Press(60, 80);
        bool retrig = p.EventCount == 2 && !p.Events[0].On && p.Events[1].On;
        p.BeginBlock();
        p.Release(60);
        p.Pedal(false);
        bool released = !p.AnySounding && p.EventCount == 1 && !p.Events[0].On;
        Show("Teclado: pedal de sustain", stillOn && retrig && released ? "sostiene" : "no", "sostiene", stillOn && retrig && released);
        // the Transponer at the moment of the key: its end goes to the same note even if it changed meanwhile
        p.Transpose = 2;
        p.BeginBlock();
        p.Press(60, 100);
        p.Transpose = 0;
        p.Release(60);
        bool shifted = p.Events[0].Sounds == 62 && p.Events[1].Sounds == 62 && p.Events[1].Pitch == 60;
        Show("Teclado: Transponer al tocar", "C4 → D4", "", shifted);

        // a take: start and end paired, a key held at the stop ends there, the count-in's notes start with the take
        var take = new MidiTake();
        take.Add(-500, new LiveNote { On = true, Pitch = 60, Velocity = 70 });
        take.Add(1000, new LiveNote { On = false, Pitch = 60 });
        take.Add(2000, new LiveNote { On = true, Pitch = 67, Velocity = 90 });
        var notes = take.Notes(0, 5000);
        bool takeOk = notes is [(0, 1000, 60, 70), (2000, 3000, 67, 90)];
        Show("Teclado: toma (pares y final)", $"{notes.Count} notas", "2", takeOk);

        // quantize: to the song's grid, not the clip's (a clip an eighth late), and the length stays
        var q = new List<MidiNote> { new() { Pitch = 60, Beat = 0.1, Beats = 0.4 }, new() { Pitch = 62, Beat = 0.62, Beats = 1 } };
        int moved = MidiEditing.Quantize(q, 0.25, 120, 0.25);
        bool quantOk = moved == 2 && Math.Abs(q[0].Beat - 0) < 1e-9 && Math.Abs(q[1].Beat - 0.5) < 1e-9 && q[0].Beats == 0.4
            && MidiEditing.Quantize(q, 0, 120, 0) == 0;
        Show("Teclado: cuantizar a la grilla", $"{moved} movidas", "2", quantOk);

        // velocity: scaled around the average (50% halves the distance, 0% evens it), humanized inside the range
        var vel = new List<MidiNote> { new() { Velocity = 80 }, new() { Velocity = 100 }, new() { Velocity = 120 } };
        MidiEditing.ScaleVelocity(vel, 50);
        bool scaleOk = vel[0].Velocity == 90 && vel[1].Velocity == 100 && vel[2].Velocity == 110;
        MidiEditing.ScaleVelocity(vel, 0, [vel[0], vel[1]]);
        scaleOk &= vel[0].Velocity == 95 && vel[1].Velocity == 95 && vel[2].Velocity == 110;
        var loud = Enumerable.Range(0, 200).Select(_ => new MidiNote { Velocity = 125 }).ToList();
        MidiEditing.HumanizeVelocity(loud, 10, new Random(1));
        bool humOk = loud.All(n => n.Velocity is >= 115 and <= 127) && loud.Select(n => n.Velocity).Distinct().Count() > 5
            && MidiEditing.HumanizeVelocity(loud, 0, new Random(1)) == 0;
        Show("Velocidad: escalar y humanizar", $"{vel[0].Velocity}/{vel[1].Velocity}/{vel[2].Velocity}", "95/95/110", scaleOk && humOk);

        // a recorded clip: from the bar where the take began to the bar after it ended, quantized when asked
        var proj = new Project();
        proj.Settings.Bpm = 120;
        string tid = MidiEditing.AddTrack(proj);
        var rec = MidiEditing.AddRecorded(proj, tid, 2.7, 5.1, [(2.76, 0.5, 64, 100), (3.49, 0.25, 67, 80)], 0.25);
        bool recOk = rec is { AtSec: 2, LengthBeats: 8 } && rec.Notes.Count == 2 && Math.Abs(rec.Notes[0].Beat - 1.5) < 1e-9
            && Math.Abs(rec.Notes[1].Beat - 3) < 1e-9 && MidiEditing.AddRecorded(proj, tid, 0, 1, []) == null;
        Show("Teclado: clip grabado (compases)", rec == null ? "nada" : $"{rec.AtSec:0.##} s", "2 s", recOk);

        bool curves = VelocityCurves.Apply(VelocityCurves.Soft, 40, 100) > 40 && VelocityCurves.Apply(VelocityCurves.Hard, 40, 100) < 40
            && VelocityCurves.Apply(VelocityCurves.Fixed, 12, 100) == 100 && VelocityCurves.Apply(VelocityCurves.Normal, 127, 100) == 127;
        var parsed = MidiSettings.Parse("{\"inputs\":[{\"name\":\"Teclado\",\"channel\":40,\"velocity\":\"rara\"}],\"quantizeOnRecord\":true}");
        bool settingsOk = parsed.QuantizeOnRecord && parsed.Inputs[0] is { Channel: 16, Velocity: VelocityCurves.Normal }
            && MidiSettings.Parse("no es json").Inputs.Count == 0;
        Show("Teclado: curvas y ajustes", curves && settingsOk ? "ok" : "mal", "ok", curves && settingsOk);
        return ok;
    }
}
