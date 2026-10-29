using WildTrack.Audio;
using WildTrack.Audio.Dsp;
using WildTrack.Audio.Vst;
using WildTrack.Projects;

namespace WildTrack.Lab;

/// <summary>
/// VST3 plugins without the window: scan them (the same catalog the app uses), list them, manage what Configuración →
/// Plugins manages (the folders to look in, the plugins hidden from the lists), and play one offline through
/// wtvsthost.exe to a WAV, the way the export does, to hear that the bridge works.
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
            default:
                throw new ArgumentException("vst escanear [--todo] [--carpeta X] [--archivo X] | lista | carpetas [--agregar X] [--sacar X] | ocultar \"nombre\" | mostrar \"nombre\" | probar \"nombre\" [--segundos 4] [--salida x.wav] [--ventana]");
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

    static string Palette(float peak) => peak > 1e-6 ? $"{20 * Math.Log10(peak):0.0} dB" : "silencio";
}
