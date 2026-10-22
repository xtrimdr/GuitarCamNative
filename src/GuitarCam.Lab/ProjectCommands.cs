using GuitarCam.Audio;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>Project import, recovery, saved versions and editable history without the window.</summary>
static class ProjectCommands
{
    public static int Run(Args a)
    {
        if (a.Get("datos") is { } data) AppConfig.DirectoryOverride = Path.GetFullPath(data);
        string sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (sub == "verificar-deshacer") return UndoChecks.Run();
        if (sub == "verificar-clips") return ClipChecks.Run();
        string? dir = a.Positional.Skip(1).FirstOrDefault();
        if (dir == null) throw new ArgumentException("Falta la carpeta del proyecto.");
        dir = Path.GetFullPath(dir);
        return sub switch
        {
            "importar" => Import(dir, a.Get("destino")),
            "abrir" => Open(dir),
            "versiones" => Versions(dir),
            "volver" => Restore(dir, a.Positional.Skip(2).FirstOrDefault()),
            "clips" => Clips(dir),
            "editar" => EditClips(dir, a),
            "encuadre" => Frame(dir, a),
            "cortar" => Crash(dir, a.Double("segundos", 3)),
            _ => throw new ArgumentException("proyecto importar|abrir|versiones|volver|cortar <carpeta>"),
        };
    }

    static int Import(string webDir, string? target)
    {
        target = Path.GetFullPath(target ?? WebImporter.SuggestTarget(webDir));
        Console.WriteLine($"Importando {webDir}\n       en {target}");
        var r = WebImporter.Import(webDir, target);
        using var store = r.Store;
        Console.WriteLine($"Listo: {r.Linked} archivos enlazados, {r.Copied} copiados.");
        foreach (var m in r.Missing) Console.WriteLine($"  falta: {m}");
        Summary(store);
        return 0;
    }

    static int Open(string dir)
    {
        using var store = ProjectStore.Open(dir);
        Summary(store);
        return 0;
    }

    static int Clips(string dir)
    {
        using var store = ProjectStore.Open(dir);
        foreach (var t in store.Project.Tracks)
        {
            Console.WriteLine($"{t.Id} · {t.Name}");
            foreach (var c in t.Clips)
            {
                var w = ClipEditing.Window(c, store.Project.FindMedia(c.MediaId));
                Console.WriteLine($"  {c.Id} · {c.MediaId} · {w.Start:0.000}–{w.End:0.000} s · anclaje {w.Anchor:0.000} s · fades {c.FadeInSec:0.000}/{c.FadeOutSec:0.000} s");
            }
        }
        return 0;
    }

    static int EditClips(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        var p = store.Project;
        string op = a.Positional.ElementAtOrDefault(2) ?? "";
        string id = a.Positional.ElementAtOrDefault(3) ?? "";
        string value = a.Positional.ElementAtOrDefault(4) ?? "";
        switch (op)
        {
            case "nueva-pista": Console.WriteLine(ClipEditing.AddEmpty(p)); break;
            case "agregar-toma": Console.WriteLine(ClipEditing.AddMedia(p, id, a.Get("pista"))); break;
            case "quitar-pista": ClipEditing.RemoveTrack(p, id); break;
            case "quitar-clip": ClipEditing.RemoveClip(p, id); break;
            case "reordenar": ClipEditing.Reorder(p, int.Parse(id) - 1, int.Parse(value) - 1); break;
            case "a-pista": ClipEditing.MoveToTrack(p, id, value, a.Get("intercambiar")); break;
            case "inicio": ClipEditing.ToStart(p, id); break;
            case "fijar-corte": ClipEditing.FixCut(p, id); break;
            case "restaurar": ClipEditing.Reset(p, id); break;
            default:
                var mode = op switch
                {
                    "mover" => ClipEditMode.Move, "recortar-inicio" => ClipEditMode.TrimStart,
                    "recortar-final" => ClipEditMode.TrimEnd, "fade-in" => ClipEditMode.FadeIn, "fade-out" => ClipEditMode.FadeOut,
                    _ => throw new ArgumentException("editar: mover|recortar-inicio|recortar-final|fade-in|fade-out clip delta-segundos; a-pista|inicio|fijar-corte|restaurar|quitar-clip|quitar-pista|nueva-pista|agregar-toma|reordenar"),
                };
                var (_, clip) = ClipEditing.Find(p, id);
                double dx = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                var others = p.Tracks.SelectMany(t => t.Clips).Where(c => c.Id != id)
                    .Select(c => ClipEditing.Window(c, p.FindMedia(c.MediaId)));
                ClipEditing.Replace(p, ClipEditing.Drag(clip, p.FindMedia(clip.MediaId), mode, dx, a.Double("pps", 100),
                    a.Double("cabezal", 0), ClipEditing.GridSeconds(a.Get("iman", "1/4"), p.Settings.Bpm), a.Flag("libre"), others));
                break;
        }
        p.AddLog("timeline-edited", op);
        store.Save();
        Console.WriteLine("Timeline actualizado; las grabaciones quedaron intactas.");
        return 0;
    }

    static int Frame(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        string id = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("Falta el ID del clip.");
        var clip = ClipEditing.Find(store.Project, id).Clip;
        if (a.Flag("restaurar")) InspectorEditing.ResetFrame(clip);
        double? Value(string key) => a.Get(key) is { } text ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : null;
        InspectorEditing.Frame(clip, Value("zoom"), Value("panx"), Value("pany"), a.Flag("espejar") ? !clip.Framing.Mirror : null);
        store.Project.AddLog("clip-framed");
        store.Save();
        Console.WriteLine("Encuadre actualizado; la grabación quedó intacta.");
        return 0;
    }

    static int Versions(string dir)
    {
        if (!ProjectStore.IsProject(dir)) throw new IOException("Esa carpeta no es un proyecto de GuitarCam");
        var list = ProjectStore.Snapshots(dir);
        if (list.Count == 0) Console.WriteLine("Todavía no hay versiones guardadas.");
        for (int i = 0; i < list.Count; i++)
            Console.WriteLine($"{i + 1,3} · {RelativeTime(list[i].Time)} · {list[i].Time:dd/MM/yyyy HH:mm:ss}");
        return 0;
    }

    static int Restore(string dir, string? value)
    {
        var list = ProjectStore.Snapshots(dir);
        if (!int.TryParse(value, out int n) || n < 1 || n > list.Count)
            throw new ArgumentException("Elegí un número de la lista de «gclab proyecto versiones carpeta».");
        var snapshot = list[n - 1];
        using var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        store.Restore(snapshot);
        Console.WriteLine($"Volví a la versión de {RelativeTime(snapshot.Time)} (lo que había antes también quedó en el historial)");
        Summary(store);
        return 0;
    }

    static string RelativeTime(DateTime time)
    {
        var now = DateTime.Now;
        double minutes = Math.Max(0, (now - time).TotalMinutes);
        if (minutes < 1) return "recién";
        if (minutes < 60) return $"hace {(int)minutes} min";
        if (time.Date == now.Date) return $"hoy {time:HH:mm}";
        if (time.Date == now.Date.AddDays(-1)) return $"ayer {time:HH:mm}";
        return time.ToString("dd/MM HH:mm");
    }

    static void Summary(ProjectStore store)
    {
        var p = store.Project;
        Console.WriteLine($"\"{p.Name}\" v{p.Version} · {p.Settings.Bpm:0.##} BPM · {p.Settings.Aspect} · {p.Media.Count} medios · {p.Tracks.Count} pistas");
        foreach (var t in p.Tracks)
        {
            string clips = string.Join(", ", t.Clips.Select(c => $"{p.FindMedia(c.MediaId)?.Name ?? "?"} @ {c.AtSec:0.000} s"));
            Console.WriteLine($"  {t.Name,-24} vol {t.Channel.Volume:0.00} pan {t.Channel.Pan:+0.00;-0.00;0} {(t.Video ? "video" : "audio")} · {(clips == "" ? "vacía" : clips)}");
        }
        if (store.ReadOnly) Console.WriteLine($"Solo lectura: {store.ReadOnlyReason}");
        foreach (var n in store.Notices) Console.WriteLine($"Aviso: {n}");
        var snaps = ProjectStore.Snapshots(store.Dir);
        Console.WriteLine($"Historial: {snaps.Count} copias{(snaps.Count > 0 ? $", la última {snaps[0].Time:g}" : "")}");
    }

    /// <summary>Records a test tone like the app will and dies before finishing, to try the recovery.</summary>
    static int Crash(string dir, double seconds)
    {
        var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new InvalidOperationException(store.ReadOnlyReason);
        const int sr = 48000;
        var id = Ids.New("med");
        string rel = $"{ProjectStore.MediaDir}/{id}.wav";
        store.BeginRecording(new ProjectStore.RecordingJournal
        {
            MediaId = id,
            Name = "Prueba de corte",
            Audio = rel,
            TrackId = store.Project.Tracks.FirstOrDefault(t => t.Clips.Count == 0)?.Id,
            AtSec = 0,
        });
        var w = new RecordingWriter(store.Resolve(rel)!, sr) { CheckpointSec = 0.5 };
        var l = new float[480];
        var r = new float[480];
        long f = 0;
        for (int b = 0; b < seconds * sr / l.Length; b++)
        {
            for (int i = 0; i < l.Length; i++, f++) l[i] = r[i] = 0.25f * MathF.Sin(2 * MathF.PI * 220 * f / sr);
            w.Append(l, r);
        }
        // a few blocks after the last checkpoint, then the "crash": no Dispose, no save
        for (int b = 0; b < 20; b++) w.Append(l, r);
        Console.WriteLine($"Grabé {f / (double)sr:0.0} s y corté sin cerrar el archivo. Abrilo con: gclab proyecto abrir \"{dir}\"");
        Environment.Exit(3);
        return 3;
    }
}

/// <summary>Deterministic regressions for snapshot isolation, coalescing, redo and retained project data.</summary>
static class UndoChecks
{
    /// <summary>A monotonic millisecond clock so the 800 ms boundary needs no sleeping.</summary>
    sealed class Clock : TimeProvider
    {
        long now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => now;
        public void Advance(int ms) => now += ms;
    }

    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException($"Deshacer: {label}");
            checks++;
        }
        var clock = new Clock();
        var history = new UndoManager(clock);
        var p = new Project { Name = "Prueba", Tracks = [new Track { Name = "Guitarra" }, new Track { Name = "Otra" }] };
        var media = new Media { Name = "Original", DurationSec = 10 };
        p.Media.Add(media);
        p.Tracks[0].Clips.Add(new Clip { MediaId = media.Id, GainDb = 3 });
        history.Checkpoint(p, "volume");
        p.Tracks[0].Channel.Volume = 0.8;
        clock.Advance(790);
        history.Checkpoint(p, "volume");
        p.Tracks[0].Channel.Volume = 0.5;
        clock.Advance(790);
        history.Checkpoint(p, "volume");
        p.Tracks[0].Channel.Volume = 0.2;
        p.Settings.Bpm = 140;
        p.Name = "Nombre actual";
        p.Exports.Add(new ExportRecord { File = "video.mp4" });
        p.AddLog("test");
        Check(history.Undo(p) && p.Tracks[0].Channel.Volume == 1 && !history.CanUndo, "el arrastre completo es un paso");
        Check(ReferenceEquals(p.Media[0], media) && p.Name == "Nombre actual" && p.Exports.Count == 1 && p.Log.Count == 1 && p.Settings.Bpm == 140,
            "medios, identidad, exports, log y tempo no se revierten");
        Check(history.Redo(p) && p.Tracks[0].Channel.Volume == 0.2, "rehacer recupera el final del arrastre");
        history.Clear();
        history.Checkpoint(p, "pan");
        p.Tracks[0].Channel.Pan = 0.2;
        clock.Advance(800);
        history.Checkpoint(p, "pan");
        p.Tracks[0].Channel.Pan = 0.5;
        Check(history.Undo(p) && p.Tracks[0].Channel.Pan == 0.2 && history.CanUndo, "800 ms abre otro paso");
        history.Checkpoint(p, "mute");
        p.Tracks[0].Channel.Muted = true;
        Check(!history.CanRedo, "una edición nueva elimina rehacer");
        history.Clear();
        history.Checkpoint(p, "volume");
        p.Tracks[0].Channel.Volume = 0.3;
        history.Checkpoint(p, "pan");
        p.Tracks[0].Channel.Pan = -0.5;
        Check(history.Undo(p) && p.Tracks[0].Channel.Volume == 0.3, "claves distintas generan pasos separados");
        history.Clear();
        history.Checkpoint(p);
        p.Tracks[0].Clips[0].GainDb = -12;
        p.Tracks[0].Channel.Comp.Eq.AirDb = 4;
        p.Tracks.Reverse();
        p.Settings.Aspect = "9:16";
        p.Settings.Reverb.Decay = 7;
        p.Settings.Delay.Feedback = 0.9;
        p.Settings.Color.Exposure = 50;
        p.Settings.VideoFx.Grain = 50;
        Check(history.Undo(p) && p.Tracks[0].Name == "Guitarra" && p.Tracks[0].Clips[0].GainDb == 3 && p.Tracks[0].Channel.Comp.Eq.AirDb == 0
            && p.Settings.Aspect == "16:9" && p.Settings.Reverb.Decay != 7
            && p.Settings.Delay.Feedback != 0.9 && p.Settings.Color.Exposure == 0 && p.Settings.VideoFx.Grain == 0,
            "fotos profundas de clips, canales y ajustes");
        Check(history.Redo(p) && p.Tracks[0].Name == "Otra" && p.Settings.Reverb.Decay == 7 && p.Tracks[1].Clips[0].GainDb == -12,
            "las fotos no comparten referencias mutables");
        history.Clear();
        for (int i = 0; i < 205; i++) { history.Checkpoint(p); p.Tracks[0].Name = $"Pista {i}"; }
        int count = 0;
        while (history.Undo(p)) count++;
        Check(count == 200 && p.Tracks[0].Name == "Pista 4", "límite de 200 pasos");
        history.Clear();
        Check(!history.CanUndo && !history.CanRedo && !history.Undo(p) && !history.Redo(p), "limpiar reinicia ambas pilas");
        Console.WriteLine($"Deshacer/rehacer: {checks} comprobaciones correctas (sin modificar archivos).");
        return 0;
    }
}

/// <summary>The project manager without the window: list, create, rename, duplicate, delete, templates.</summary>
static class LibraryCommands
{
    public static int Run(Args a)
    {
        if (a.Get("datos") is { } data) AppConfig.DirectoryOverride = Path.GetFullPath(data);
        if (a.Get("raiz") is { } r) ProjectLibrary.RootOverride = Path.GetFullPath(r);
        string sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "lista";
        string? arg1 = a.Positional.Skip(1).FirstOrDefault();
        string? arg2 = a.Positional.Skip(2).FirstOrDefault();
        switch (sub)
        {
            case "lista":
                var (root, list) = ProjectLibrary.List();
                Console.WriteLine($"Carpeta de proyectos: {root}");
                foreach (var p in list)
                    Console.WriteLine($"  {p.Name,-32} {(p.Exists ? $"{p.TrackCount} pistas · {p.TakeCount} tomas · {p.DurationSec:0} s · {p.Bpm:0.##} BPM · {p.Bytes / 1e6:0.0} MB" : "no se encontró la carpeta")}{(p.InRoot ? "" : $"  [{p.Dir}]")}");
                return 0;
            case "nuevo":
                var tpl = a.Get("plantilla") is { } id ? ProjectTemplates.All().FirstOrDefault(t => t.Id == id || t.Name == id) ?? throw new ArgumentException($"No hay una plantilla \"{id}\".") : null;
                Console.WriteLine($"Creado: {ProjectLibrary.Create(arg1 ?? "Proyecto", tpl, a.Get("carpeta"))}");
                return 0;
            case "previsualizar":
                var preview = ProjectLibrary.PreviewNew(Need(arg1));
                Console.WriteLine($"{preview.Name}\n{preview.Dir}");
                return 0;
            case "carpeta":
                ProjectLibrary.SetRoot(Path.GetFullPath(Need(arg1)));
                Console.WriteLine($"Carpeta de proyectos: {ProjectLibrary.Root}");
                return 0;
            case "olvidar":
                ProjectLibrary.Forget(Path.GetFullPath(Need(arg1)));
                Console.WriteLine("Quitado de la lista.");
                return 0;
            case "renombrar":
                var res = ProjectLibrary.Rename(Path.GetFullPath(Need(arg1)), Need(arg2), true);
                Console.WriteLine($"Renombrado: {res.Dir}{(res.FolderKept ? " (la carpeta no se pudo renombrar)" : "")}");
                return 0;
            case "duplicar":
                Console.WriteLine($"Copia: {ProjectLibrary.Duplicate(Path.GetFullPath(Need(arg1)))}");
                return 0;
            case "borrar":
                ProjectLibrary.Delete(Path.GetFullPath(Need(arg1)));
                Console.WriteLine("Mandado a la Papelera.");
                return 0;
            case "plantillas":
                foreach (var t in ProjectTemplates.All())
                    Console.WriteLine($"  {t.Id,-28} {t.Name,-34} {t.Summary}");
                return 0;
            case "guardar-plantilla":
                using (var store = ProjectStore.Open(Path.GetFullPath(Need(arg1))))
                    ProjectTemplates.SaveUser(ProjectLibrary.TemplateOf(store.Project, Need(arg2)));
                Console.WriteLine("Plantilla guardada.");
                return 0;
            case "borrar-plantilla":
                ProjectTemplates.DeleteUser(Need(arg1));
                Console.WriteLine("Plantilla borrada.");
                return 0;
            default:
                throw new ArgumentException("proyectos lista|nuevo|previsualizar|renombrar|duplicar|borrar|olvidar|carpeta|plantillas|guardar-plantilla|borrar-plantilla");
        }
    }

    static string Need(string? s) => s ?? throw new ArgumentException("Faltan argumentos.");
}
