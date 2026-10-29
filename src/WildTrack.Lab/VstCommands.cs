using WildTrack.Audio;
using WildTrack.Audio.Dsp;
using WildTrack.Audio.Vst;
using WildTrack.Projects;

namespace WildTrack.Lab;

/// <summary>
/// VST3 plugins without the window: scan them (the same catalog the app uses), list them, manage what Configuración →
/// Plugins manages (the folders to look in, the plugins hidden from the lists), and play one offline through
/// wtvsthost.exe to a WAV, the way the export does, to hear that the bridge works. An effect (<c>vst efecto</c>) goes
/// through a slot both ways, the export's and live (paced like the driver), to check that live comes exactly one block
/// later than the export, which is what the plugin delay compensation counts on.
/// </summary>
static class VstCommands
{
    public static int Run(Args a)
    {
        if (a.Get("datos") is { } data) AppConfig.DirectoryOverride = Path.GetFullPath(data);
        string sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "";
        switch (sub)
        {
            case "escanear":
            {
                if (a.Get("archivo") is { } file)
                {
                    Console.WriteLine($"  abriendo {Path.GetFileNameWithoutExtension(file)}…");
                    Show(PluginCatalog.Rescan(Path.GetFullPath(file)));
                    return 0;
                }
                string[]? folders = a.Get("carpeta") is { } dir ? [Path.GetFullPath(dir)] : null;
                var entries = PluginCatalog.Scan((name, n, total) => Console.WriteLine($"  abriendo {name}… ({n} de {total})"), folders, a.Flag("todo"));
                Show(entries);
                return 0;
            }
            case "lista":
                Show(PluginCatalog.Load());
                return 0;
            case "carpetas":
            {
                if (a.Get("agregar") is { } add)
                    Console.WriteLine(PluginCatalog.AddFolder(add) ? $"Agregada: {Path.GetFullPath(add)}" : "Esa carpeta ya estaba.");
                if (a.Get("sacar") is { } remove)
                    Console.WriteLine(PluginCatalog.RemoveFolder(Path.GetFullPath(remove)) ? "Sacada (sus plugins se olvidan en el próximo escaneo)." : "Esa carpeta no estaba entre las agregadas.");
                var extra = PluginCatalog.ExtraFolders();
                foreach (var f in PluginCatalog.Folders)
                    Console.WriteLine($"{(extra.Contains(f) ? "agregada " : "windows  ")} {f}{(Directory.Exists(f) ? "" : "  (no existe)")}");
                return 0;
            }
            case "ocultar" or "mostrar":
            {
                string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException($"vst {sub} \"nombre del plugin\"");
                var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
                    ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
                PluginCatalog.SetHidden(plugin, sub == "ocultar");
                Console.WriteLine(sub == "ocultar" ? $"{plugin.Name} ya no sale en las listas." : $"{plugin.Name} vuelve a salir en las listas.");
                return 0;
            }
            case "probar":
                return Try(a);
            case "efecto":
                return Effect(a);
            default:
                throw new ArgumentException("vst escanear [--todo] [--carpeta X] [--archivo X] | lista | carpetas [--agregar X] [--sacar X] | ocultar \"nombre\" | mostrar \"nombre\" | probar \"nombre\" [--segundos 4] [--salida x.wav] [--ventana] | efecto \"nombre\" [--entrada x.wav] [--segundos 4] [--bloque 256] [--salida x.wav] [--apagado]");
        }
    }

    static void Show(List<PluginCatalog.Entry> entries)
    {
        var plugins = PluginCatalog.Plugins(entries);
        var hidden = PluginCatalog.Hidden();
        if (plugins.Count == 0) Console.WriteLine("Sin plugins (probá con vst escanear).");
        foreach (var p in plugins)
            Console.WriteLine($"{(p.IsInstrument ? "instrumento" : "efecto     ")}  {p.Name} · {p.Vendor} · {p.Version} · {p.Categories}{(p.KnowsBuses ? $" · entradas: {p.AudioIns} de audio, {p.EventIns} de notas" : "")}{(hidden.Contains(PluginCatalog.Key(p)) ? "  (oculto)" : "")}");
        foreach (var e in entries.Where(e => e.Error != null))
            Console.WriteLine($"no abrió     {Path.GetFileName(e.Path)}: {e.Error}");
    }

    /// <summary>Plays a C major chord (and a bass C), or a beat on a drum kit, through the plugin, offline, into a WAV; --ventana opens its window live first.</summary>
    static int Try(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst probar \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        const int sr = 48000;
        double seconds = a.Double("segundos", 4);
        string? state = null;
        if (a.Flag("ventana"))
        {
            using var live = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, sr, offline: false);
            live.OpenEditor(0);
            Console.WriteLine($"Abrí la ventana de {plugin.Name}: cambiá lo que quieras y apretá Enter.");
            Console.ReadLine();
            state = live.GetStateAsync().Result;
            Console.WriteLine(state == null ? "No me dio su estado." : $"Estado: {state.Length} caracteres.");
        }
        NoteSequence notes;
        if (plugin.Categories.Contains("Drum", StringComparison.OrdinalIgnoreCase))
        {
            // a drum kit: kick and snare (General MIDI 36 and 38) on the beats and the hi-hat (42) in eighths, at 120
            var hits = new List<NoteEvent>();
            for (int e = 0; e < (int)((seconds - 1) * 4); e++)
            {
                long at = sr / 10 + e * sr / 4;
                hits.Add(new NoteEvent(at, sr / 8, 0, 0, 42, 90));
                if (e % 2 == 0) hits.Add(new NoteEvent(at, sr / 8, 0, 0, e % 4 == 0 ? 36 : 38, 110));
            }
            notes = new NoteSequence([.. hits], sr);
        }
        else
        {
            int[] chord = [48, 60, 64, 67];
            long held = (long)((seconds - 1) * sr);
            notes = new NoteSequence([.. chord.Select(p => new NoteEvent(sr / 10, held, NoteSynth.Hz(p), 0, p, 100))], sr);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var render = new InstrumentRender(new VstInstance(plugin.Path, plugin.ClassId, plugin.Name, state), notes, 120, sr, offline: !a.Flag("tiempo-real"));
        double opened = sw.Elapsed.TotalSeconds;
        // some instruments (big sample libraries) keep loading in the background after they open
        if (a.Double("espera", 0) is var wait and > 0) render.Warm(wait);
        int frames = (int)(seconds * sr);
        var l = new float[frames];
        var r = new float[frames];
        sw.Restart();
        render.Render(0, frames, l, r);
        double took = sw.Elapsed.TotalSeconds;
        float peak = 0;
        for (int i = 0; i < frames; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        string output = Path.GetFullPath(a.Get("salida", $"{plugin.Name}.wav"));
        Wav.Write(output, l, r, sr);
        Console.WriteLine($"{plugin.Name}: abrió en {opened:0.0} s, {seconds:0.#} s de audio en {took:0.00} s, pico {Palette(peak)}");
        Console.WriteLine($"Escrito: {output}");
        return peak > 1e-4 ? 0 : 3;
    }

    /// <summary>
    /// An effect in a slot (<see cref="VstInsert"/>), offline as the export runs it and live in driver blocks of
    /// <c>--bloque</c> at the speed of real time: writes both and checks that the live one is the same sound one block
    /// later (a plugin that changes with time, like a modulated reverb, only lines up roughly).
    /// </summary>
    static int Effect(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst efecto \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        if (plugin.IsInstrument) Console.WriteLine($"Ojo: {plugin.Name} figura como instrumento; lo pruebo como efecto igual.");
        const int sr = 48000, quantum = 128;
        float[] inL, inR;
        if (a.Get("entrada") is { } input)
        {
            var (ch, wsr) = Wav.Read(Path.GetFullPath(input));
            if (wsr != sr) Console.WriteLine($"Aviso: el WAV es de {wsr} Hz; lo leo como si fuera de {sr} Hz.");
            inL = ch[0];
            inR = ch.Length > 1 ? ch[1] : ch[0];
        }
        else
        {
            // plucked chords every half second, as a guitar DI would come in
            int len = (int)(a.Double("segundos", 4) * sr);
            inL = new float[len];
            double[] hz = [82.4, 123.5, 164.8, 207.7];
            for (int i = 0; i < len; i++)
            {
                double t = i % (sr / 2) / (double)sr;
                double v = 0;
                foreach (double f in hz) v += ((t * f * (1 + i / (sr / 2) % 3 * 0.25)) % 1 - 0.5) * Math.Exp(-t * 6);
                inL[i] = (float)(v * 0.25);
            }
            inR = inL;
        }
        int n = inL.Length;
        int block = Math.Clamp(a.Int("bloque", 256), 32, VstBridge.MaxBlock);
        var instance = new VstInstance(plugin.Path, plugin.ClassId, plugin.Name, null);
        bool on = !a.Flag("apagado");

        // the export's way: its own process, offline, waiting for every block
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var offline = new VstInsert(sr);
        offline.Set(new VstSlotSpec(instance, on) { Offline = true }, 120);
        var expL = (float[])inL.Clone();
        var expR = (float[])inR.Clone();
        for (int at = 0; at < n; at += quantum)
        {
            int len = Math.Min(quantum, n - at);
            offline.Process(expL.AsSpan(at, len), expR.AsSpan(at, len));
        }
        int pluginLatency = offline.LatencySamples;
        offline.Dispose();
        Console.WriteLine($"{plugin.Name} como en la exportación: {n / (double)sr:0.0} s en {sw.Elapsed.TotalSeconds:0.00} s, latencia propia {pluginLatency} muestras, pico {Palette(Peak(expL, expR))}");

        // live: the window's process, blocks of the driver's size at the speed of real time, never waiting for it
        using var bridge = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, sr, offline: false);
        VstInsert.LiveBlock = block;
        var live = new VstInsert(sr);
        live.Set(new VstSlotSpec(instance, on) { Live = bridge }, 120);
        int expected = live.LatencySamples - pluginLatency;
        var liveL = (float[])inL.Clone();
        var liveR = (float[])inR.Clone();
        sw.Restart();
        for (int at = 0, k = 0; at < n; at += block, k++)
        {
            int len = Math.Min(block, n - at);
            live.Process(liveL.AsSpan(at, len), liveR.AsSpan(at, len));
            // the driver calls back once per block, evenly: wait for the next one spinning (a Sleep(1) lasts up to
            // 15 ms on Windows, and the blocks it put off would then come back to back, with no time for the plugin)
            double due = (k + 1) * block / (double)sr;
            while (sw.Elapsed.TotalSeconds < due) Thread.Yield();
        }
        Console.WriteLine($"En vivo, bloques de {block}: {live.Misses} bloques que no llegó a tiempo; se oye {live.LatencySamples} muestras tarde ({live.LatencySamples * 1000.0 / sr:0.0} ms)");

        // where the live sound lines up with the export's: one block later
        int best = 0;
        double bestErr = double.MaxValue;
        int from = Math.Min(n / 4, sr), to = n - 2 * block - 64;
        for (int lag = 0; lag <= 2 * block + 64; lag++)
        {
            double e = 0;
            for (int i = from; i < to; i += 7) e += Math.Abs(liveL[i + lag] - expL[i]);
            if (e < bestErr) { bestErr = e; best = lag; }
        }
        double diff = 0, level = 0;
        for (int i = from; i < to; i++)
        {
            diff = Math.Max(diff, Math.Abs(liveL[i + best] - expL[i]));
            level = Math.Max(level, Math.Abs(expL[i]));
        }
        string rel = level > 0 && diff > 0 ? $"{20 * Math.Log10(diff / level):0.0} dB" : "exacto";
        Console.WriteLine($"En vivo llega {best} muestras después de la exportación (esperado {expected}), diferencia {rel}");
        string output = Path.GetFullPath(a.Get("salida", $"{plugin.Name} efecto.wav"));
        Wav.Write(output, expL, expR, sr);
        string liveOut = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + " en vivo.wav");
        Wav.Write(liveOut, liveL, liveR, sr);
        Console.WriteLine($"Escritos: {output} y {liveOut}");
        return best == expected ? 0 : 3;
    }

    static float Peak(float[] l, float[] r)
    {
        float p = 0;
        for (int i = 0; i < l.Length; i++) p = Math.Max(p, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        return p;
    }

    static string Palette(float peak) => peak > 1e-6 ? $"{20 * Math.Log10(peak):0.0} dB" : "silencio";
}
