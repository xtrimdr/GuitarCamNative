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
        if (sub == "verificar-tomas") return TakeChecks.Run();
        if (sub == "verificar-importar") return ImportChecks.Run();
        if (sub == "verificar-historial") return HistoryCommands.Check();
        if (sub == "verificar-carpetas") return FolderChecks();
        if (sub == "verificar-marcadores") return MarkerChecks();
        string? dir = a.Positional.Skip(1).FirstOrDefault();
        if (dir == null) throw new ArgumentException("Falta la carpeta del proyecto.");
        dir = Path.GetFullPath(dir);
        return sub switch
        {
            "abrir" => Open(dir),
            "versiones" => Versions(dir),
            "volver" => Restore(dir, a.Positional.Skip(2).FirstOrDefault()),
            "clips" => Clips(dir),
            "transiciones" => Transitions(dir),
            "importar-audio" => ImportAudio(dir, a),
            "editar" => EditClips(dir, a),
            "historial" => HistoryCommands.Run(dir, a),
            "insertos" => InsertCommands.Run(dir, a),
            "encuadre" => Frame(dir, a),
            "sincronia" => Sync(dir, a),
            "formato" => Format(dir, a),
            "modo" => Mode(dir, a),
            "marcadores" => Markers(dir, a),
            "repetir" => Loop(dir, a),
            "plantillas-de-pista" => ListPresets(),
            "cortar" => Crash(dir, a.Double("segundos", 3), a.Flag("camara")),
            "toma" => RecordTake(dir, a.Double("segundos", 3), a.Get("pista"), a.Flag("camara")),
            _ => throw new ArgumentException("proyecto importar-audio|abrir|versiones|volver|cortar <carpeta>"),
        };
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

    /// <summary>The joins of every track and how the picture goes through each (only the video: the sound always cuts there).</summary>
    static int Transitions(string dir)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        int n = 0;
        foreach (var t in p.Tracks)
            foreach (var (from, to, join) in ClipEditing.Joins(p, t))
            {
                n++;
                var w = ClipEditing.Window(to, p.FindMedia(to.MediaId));
                string what = join is { } j
                    ? $"{(j.Kind == TransitionKind.Crossfade ? "fundido cruzado" : "pasar por negro")} de {j.Sec:0.00} s ({j.Start:0.000}–{j.End:0.000} s)"
                    : "corte";
                Console.WriteLine($"{t.Name} · {from.Id} → {to.Id} en {w.Start:0.000} s · {what}");
            }
        if (n == 0) Console.WriteLine("No hay clips que se toquen: cada uno empieza y termina solo.");
        return 0;
    }

    static int ImportAudio(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        string file = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("Falta el archivo de audio.");
        var imported = AudioImport.CopyIn(dir, Path.GetFullPath(file));
        var track = AudioImport.AddClip(store.Project, imported, a.Double("en", 0), a.Get("pista"));
        store.Save();
        Console.WriteLine($"{track.Id} · {imported.Name} · {imported.DurationSec:0.00} s · {imported.SampleRate} Hz · {imported.Channels} canal(es) · {imported.RelativePath}");
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
            case "alinear": ClipEditing.AlignToGrid(p, id, ClipEditing.GridSeconds(a.Get("iman", "1/4"), p.Settings.Bpm)); break;
            case "fijar-corte": ClipEditing.FixCut(p, id); break;
            case "restaurar": ClipEditing.Reset(p, id); break;
            case "dividir": Console.WriteLine(ClipEditing.Split(p, id, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))); break;
            // several ids (comma-separated) go as one block, like D on a selection box
            case "duplicar": Console.WriteLine(string.Join(",", ClipEditing.DuplicateMany(p, id.Split(',', StringSplitOptions.RemoveEmptyEntries)))); break;
            case "pegar":
                // copies the clips and pastes them with their start at "value" seconds, the top one on --pista (or where they were)
                Console.WriteLine(string.Join(",", (ClipClipboard.Take(p, id.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    ?? throw new ArgumentException("pegar: no encontré esos clips")).Paste(p, a.Get("pista"), double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))));
                break;
            case "ganancia": ClipEditing.SetGain(p, id, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "curva":
                // "segundos:dB" separated by commas, seconds from the start of the recording; empty = flat
                ClipEditing.SetCurve(p, id, value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(pt => pt.Split(':'))
                    .Select(pt => new GainPoint { Sec = double.Parse(pt[0], System.Globalization.CultureInfo.InvariantCulture), Db = double.Parse(pt[1], System.Globalization.CultureInfo.InvariantCulture) }));
                break;
            case "nueva-carpeta":
                Console.WriteLine(TrackFolders.Add(p, id.Length > 0 ? id : null, a.Get("pistas")?.Split(',', StringSplitOptions.RemoveEmptyEntries)));
                break;
            case "a-carpeta":
                // pista[,pista…] carpeta|raiz --en n: n = place among folders and tracks without them (default: the folder's end)
                string? folder = value is "" or "raiz" ? null : value;
                var moving = id.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var rest = TrackFolders.Sequence(p).Where(x => x is not Track mt || !moving.Contains(mt.Id)).ToList();
                int at = a.Get("en") is { } en ? int.Parse(en) : folder == null ? rest.Count
                    : rest.FindLastIndex(x => x is Track t && t.FolderId == folder || x is Folder f && f.Id == folder) + 1;
                TrackFolders.Move(p, moving, at, folder);
                break;
            case "sacar-de-carpeta": TrackFolders.TakeOut(p, id); break;
            case "mover-carpeta": TrackFolders.MoveFolder(p, id, int.Parse(value)); break;
            case "quitar-carpeta": TrackFolders.Remove(p, id, a.Flag("con-pistas")); break;
            case "transponer":
                // like the inspector's Transponer: the track's semitones, and its takes rendered into the cache now
                var tt = p.Tracks.Find(t => t.Id == id) ?? throw new ArgumentException("No encontré esa pista.");
                double semis = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (Math.Abs(semis) > GuitarCam.Audio.Dsp.PitchShifter.MaxSemitones) throw new ArgumentException("transponer: de -12 a +12 semitonos.");
                tt.Transpose = Math.Round(semis, 2);
                foreach (var c in tt.Clips)
                {
                    if (store.Resolve(p.FindMedia(c.MediaId)?.Audio) is not { } ap || !File.Exists(ap)) continue;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var loaded = AudioLoader.Load(ap, 48000);
                    store.TrackAudio(tt, ap, loaded.Left, loaded.Right);
                    Console.WriteLine($"  {c.Id}: {loaded.DurationSec:0.0} s listos en {sw.Elapsed.TotalSeconds:0.0} s");
                }
                break;
            case "carpeta":
                var fd = p.Folders.Find(f => f.Id == id) ?? throw new ArgumentException("No encontré esa carpeta.");
                if (a.Get("nombre") is { } name) fd.Name = name;
                if (a.Get("volumen") is { } vol) fd.Volume = Math.Clamp(double.Parse(vol, System.Globalization.CultureInfo.InvariantCulture), 0, 1.5);
                if (a.Flag("mute")) fd.Muted = !fd.Muted;
                if (a.Flag("solo")) fd.Solo = !fd.Solo;
                if (a.Flag("plegar")) fd.Collapsed = !fd.Collapsed;
                break;
            case "guardar-plantilla":
                var src = p.Tracks.Find(t => t.Id == id) ?? throw new ArgumentException("No encontré esa pista.");
                TrackPresets.Save(TrackPresets.From(src, value.Length > 0 ? value : src.Name));
                Console.WriteLine($"Plantilla de pista guardada: {(value.Length > 0 ? value : src.Name)}");
                break;
            case "usar-plantilla":
                var preset = TrackPresets.All().Find(t => t.Id == id || string.Equals(t.Name, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("No encontré esa plantilla de pista (gclab proyecto plantillas-de-pista).");
                Console.WriteLine(TrackPresets.AddTo(p, preset, p.Settings.AudioOnly));
                break;
            case "borrar-rango":
                // pista[,pista…] desde --hasta fin [--cerrar]: with --cerrar what follows slides left over the gap
                double rs = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), re = a.Double("hasta", 0);
                var rangeTracks = id.Split(',', StringSplitOptions.RemoveEmptyEntries);
                int cut = a.Flag("cerrar") ? ClipEditing.DeleteRangeClosing(p, rangeTracks, rs, re) : rangeTracks.Sum(t => ClipEditing.DeleteRange(p, t, rs, re));
                Console.WriteLine($"{cut} clip(s) cambiados");
                break;
            case "transicion":
                // how this clip comes in from the one before it: corte|fundido|negro [--segundos 0.5]
                string? kind = value switch
                {
                    "" => null, "corte" => ClipTransitions.CutId, "fundido" => ClipTransitions.CrossfadeId, "negro" => ClipTransitions.DipId,
                    _ => throw new ArgumentException($"transicion: \"{value}\" no existe. Opciones: corte, fundido, negro."),
                };
                ClipEditing.SetTransition(p, id, kind, a.Get("segundos") != null ? a.Double("segundos", ClipTransitions.DefaultSec) : null);
                break;
            case "al-frente":
                Console.WriteLine(ClipEditing.BringToFront(p, id) ? "El clip quedó al frente." : "Ya estaba al frente.");
                break;
            default:
                var mode = op switch
                {
                    "mover" => ClipEditMode.Move, "recortar-inicio" => ClipEditMode.TrimStart,
                    "recortar-final" => ClipEditMode.TrimEnd, "fade-in" => ClipEditMode.FadeIn, "fade-out" => ClipEditMode.FadeOut,
                    _ => throw new ArgumentException("editar: mover|recortar-inicio|recortar-final|fade-in|fade-out clip delta-segundos; dividir clip segundos; ganancia clip dB; curva clip seg:dB,seg:dB; duplicar clip[,clip…]; pegar clip[,clip…] segundos [--pista id]; a-pista|inicio|alinear|fijar-corte|restaurar|quitar-clip|quitar-pista|nueva-pista|agregar-toma|reordenar; nueva-carpeta [nombre] --pistas id,id; a-carpeta pista[,pista…] carpeta|raiz [--en n]; sacar-de-carpeta pista; mover-carpeta carpeta n; quitar-carpeta carpeta [--con-pistas]; carpeta id [--nombre --volumen --mute --solo --plegar]; transponer pista semitonos; guardar-plantilla pista [nombre]; usar-plantilla id|nombre; borrar-rango pista[,pista…] desde --hasta fin [--cerrar]; al-frente clip; transicion clip corte|fundido|negro [--segundos 0.5]"),
                };
                var (_, clip) = ClipEditing.Find(p, id);
                double dx = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                var sticky = a.Get("a-clips", "no") switch
                {
                    "inicio" => SnapEdges.Start, "final" => SnapEdges.End, "ambos" => SnapEdges.Both, "no" => SnapEdges.None,
                    var x => throw new ArgumentException($"--a-clips {x}: inicio|final|ambos|no"),
                };
                var others = p.Tracks.SelectMany(t => t.Clips).Where(c => c.Id != id).Select(c => ClipEditing.Window(c, p.FindMedia(c.MediaId)));
                ClipEditing.Replace(p, ClipEditing.Drag(clip, p.FindMedia(clip.MediaId), mode, dx,
                    ClipEditing.GridSeconds(a.Get("iman", "1/4"), p.Settings.Bpm), a.Flag("libre"), null, others, sticky, a.Double("pps", 100)));
                break;
        }
        p.AddLog("timeline-edited", op);
        store.Save();
        Console.WriteLine("Timeline actualizado; las grabaciones quedaron intactas.");
        return 0;
    }

    static int Sync(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        string id = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("Falta el ID del clip.");
        var clip = ClipEditing.Find(store.Project, id).Clip;
        var media = store.Project.FindMedia(clip.MediaId);
        if (a.Flag("heredar"))
        {
            InspectorEditing.SetSync(clip, media, media?.SyncMs ?? 0);
            clip.SyncMs = clip.VideoOffsetMs = null;
        }
        if (a.Get("audio-ms") != null) InspectorEditing.SetSync(clip, media, a.Double("audio-ms", 0));
        if (a.Get("camara-ms") != null) clip.VideoOffsetMs = a.Double("camara-ms", 80);
        store.Project.AddLog("clip-sync");
        store.Save();
        Console.WriteLine($"Sincronía del clip: audio {clip.EffectiveSyncMs(media):0.###} ms · cámara {clip.EffectiveVideoOffsetMs(media):0.###} ms (anclaje conservado).");
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

    static int Format(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        var s = store.Project.Settings;
        // like the window: the aspect first (it may change the layout), then an explicit layout wins
        if (a.Get("relacion") is { } aspect) s.SetAspect(aspect);
        if (a.Get("disposicion") is { } layout)
            s.SetLayout(layout switch { "lado-a-lado" => "side-by-side", "apilado" => "top-to-bottom", _ => layout });
        store.Project.AddLog("format-changed");
        store.Save();
        Console.WriteLine($"Formato: {s.Aspect} · {(s.Layout == "top-to-bottom" ? "Apilado" : "Lado a lado")}");
        return 0;
    }

    /// <summary>The project's mode (video / solo audio) and, with --pista, whether a track gets a panel in the video.</summary>
    static int Mode(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        var p = store.Project;
        if (a.Positional.ElementAtOrDefault(2) is { } mode) p.Settings.SetMode(mode);
        if (a.Get("pista") is { } id)
        {
            var t = p.Tracks.Find(x => x.Id == id) ?? throw new ArgumentException("No encontré esa pista.");
            t.Video = a.Get("en-video", "si") != "no";
        }
        p.AddLog("mode-changed");
        store.Save();
        Console.WriteLine($"Modo: {(p.Settings.AudioOnly ? "solo audio" : "video")}");
        foreach (var t in p.Tracks) Console.WriteLine($"  {t.Name,-24} {(t.Video ? "en el video" : "fuera del video")} · {t.Id}");
        return 0;
    }

    static int ListPresets()
    {
        var list = TrackPresets.All();
        if (list.Count == 0) Console.WriteLine("Todavía no hay plantillas de pista.");
        foreach (var t in list) Console.WriteLine($"{t.Id} · {t.Name} · {(t.Video ? "video" : "audio")} · {t.Summary}");
        return 0;
    }

    /// <summary>Folder moves on a project in memory: order, membership, empty folders, removal and undo.</summary>
    static int FolderChecks()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {what}");
            if (!ok) failed++;
        }
        string Order(Project p) => string.Join(" ", TrackFolders.Sequence(p).Select(x => x is Folder f ? $"[{f.Name}]" : ((Track)x).FolderId != null ? "·" + ((Track)x).Name : ((Track)x).Name));
        var p = new Project();
        foreach (var n in new[] { "A", "B", "C", "D" }) p.Tracks.Add(new Track { Name = n });
        string f1 = TrackFolders.Add(p, "F", [p.Tracks[1].Id, p.Tracks[2].Id]);
        Check("una carpeta con B y C: " + Order(p), Order(p) == "A [F] ·B ·C D");
        TrackFolders.Move(p, p.Tracks[0].Id, 3, f1);
        Check("A al final de F: " + Order(p), Order(p) == "[F] ·B ·C ·A D");
        TrackFolders.Move(p, p.Tracks.Find(t => t.Name == "B")!.Id, 0, null);
        Check("B afuera, arriba: " + Order(p), Order(p) == "B [F] ·C ·A D");
        string f2 = TrackFolders.Add(p, "G");
        Check("carpeta vacía al final: " + Order(p), Order(p) == "B [F] ·C ·A D [G]");
        TrackFolders.MoveFolder(p, f2, 1);
        Check("G antes de F: " + Order(p), Order(p) == "B [G] [F] ·C ·A D");
        TrackFolders.MoveFolder(p, f2, 3);
        Check("G no queda dentro de F: " + Order(p), Order(p) == "B [F] ·C ·A [G] D");
        TrackFolders.Move(p, p.Tracks.Find(t => t.Name == "D")!.Id, 5, f2);
        Check("D a la carpeta vacía: " + Order(p), Order(p) == "B [F] ·C ·A [G] ·D");
        var undo = new UndoManager();
        undo.Checkpoint(p);
        TrackFolders.Remove(p, f1);
        Check("quitar F deja sus pistas: " + Order(p), Order(p) == "B C A [G] ·D");
        undo.Undo(p);
        Check("deshacer la vuelve: " + Order(p), Order(p) == "B [F] ·C ·A [G] ·D");
        p.Folders.Find(f => f.Id == f1)!.Volume = 0.5;
        Check("el fader de la carpeta multiplica", TrackFolders.Mix(p, p.Tracks.Find(t => t.Name == "C")!).Gain == 0.5);
        ClipEditing.RemoveTrack(p, p.Tracks.Find(t => t.Name == "B")!.Id);
        TrackFolders.Add(p, "H");
        TrackFolders.MoveFolder(p, p.Folders.Find(f => f.Name == "H")!.Id, 0);
        ClipEditing.RemoveTrack(p, p.Tracks.Find(t => t.Name == "D")!.Id);
        Check("una carpeta vacía conserva su lugar: " + Order(p), Order(p) == "[H] [F] ·C ·A [G]");
        string[] ca = [p.Tracks.Find(t => t.Name == "C")!.Id, p.Tracks.Find(t => t.Name == "A")!.Id];
        TrackFolders.Move(p, ca, 3, null);
        Check("C y A juntas fuera de F, al final: " + Order(p), Order(p) == "[H] [F] [G] C A");
        TrackFolders.Move(p, ca, 3, p.Folders.Find(f => f.Name == "G")!.Id);
        Check("C y A juntas dentro de G: " + Order(p), Order(p) == "[H] [F] [G] ·C ·A");
        TrackFolders.Remove(p, p.Folders.Find(f => f.Name == "G")!.Id, withTracks: true);
        Check("quitar G con sus pistas: " + Order(p), Order(p) == "[H] [F]" && p.Tracks.Count == 0);
        Console.WriteLine(failed == 0 ? "Todo bien." : $"{failed} comprobación(es) fallaron.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Markers and the A–B range on a project in memory: order, names, jumps, undo and the saved file.</summary>
    static int MarkerChecks()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {what}");
            if (!ok) failed++;
        }
        string Names(Project p) => string.Join(" ", p.Markers.Select(m => $"{m.Name}@{m.Sec:0.#}"));
        var p = new Project();
        var undo = new UndoManager();
        var solo = ProjectMarkers.Add(p, 30, "Solo");
        ProjectMarkers.Add(p, 0, "Intro");
        var first = ProjectMarkers.Add(p, 12);
        Check("en orden y con nombre por defecto: " + Names(p), Names(p) == "Intro@0 Marcador 1@12 Solo@30");
        Check("el siguiente nombre libre", ProjectMarkers.Add(p, 40).Name == "Marcador 2");
        undo.Checkpoint(p);
        ProjectMarkers.Move(p, solo.Id, 5);
        Check("mover reordena: " + Names(p), Names(p) == "Intro@0 Solo@5 Marcador 1@12 Marcador 2@40");
        undo.Undo(p);
        Check("deshacer lo vuelve: " + Names(p), Names(p) == "Intro@0 Marcador 1@12 Solo@30 Marcador 2@40");
        Check("siguiente desde el cabezal en un marcador", ProjectMarkers.Next(p, 12)?.Sec == 30);
        Check("anterior parado", ProjectMarkers.Previous(p, 12.2, playing: false)?.Sec == 12);
        Check("anterior sonando se saltea el que acaba de pasar", ProjectMarkers.Previous(p, 12.2, playing: true)?.Sec == 0);
        ProjectMarkers.Rename(p, first.Id, "  Estribillo ");
        Check("renombrar recorta espacios", p.Markers.Any(m => m.Name == "Estribillo"));
        bool refused = false;
        try { ProjectMarkers.Rename(p, first.Id, " "); } catch (ArgumentException) { refused = true; }
        Check("sin nombre no", refused);
        ProjectMarkers.SetLoop(p.Settings, 20, 8);
        Check("el rango se ordena", ProjectMarkers.Loop(p.Settings) == (8, 20));
        refused = false;
        try { ProjectMarkers.SetLoop(p.Settings, 3, 3.01); } catch (ArgumentException) { refused = true; }
        Check("un rango de 10 ms no", refused && ProjectMarkers.Loop(p.Settings) == (8, 20));
        undo.Checkpoint(p);
        ProjectMarkers.ClearLoop(p.Settings);
        undo.Undo(p);
        Check("el rango queda afuera del deshacer (como el tempo)", ProjectMarkers.Loop(p.Settings) == null);
        ProjectMarkers.SetLoop(p.Settings, 8, 20);
        var back = ProjectStore.Parse(System.Text.Json.JsonSerializer.Serialize(p, ProjectStore.Json));
        Check("se guarda y se lee", Names(back) == Names(p) && back.Settings.LoopStartSec == 8 && back.Settings.LoopEndSec == 20);
        back.Settings.LoopStartSec = 30;
        back.Markers.Add(new Marker { Id = back.Markers[0].Id, Name = null!, Sec = -4 });
        back = ProjectStore.Parse(System.Text.Json.JsonSerializer.Serialize(back, ProjectStore.Json));
        Check("normalizar arregla lo roto", back.Settings.LoopEndSec == null && back.Markers.Any(m => m.Name == "" && m.Sec == 0)
            && back.Markers.Select(m => m.Id).Distinct().Count() == back.Markers.Count);
        Console.WriteLine(failed == 0 ? "Todo bien." : $"{failed} comprobación(es) fallaron.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The song's markers from the console: list, add, move, rename and remove (one save each).</summary>
    static int Markers(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        string op = a.Positional.ElementAtOrDefault(2) ?? "";
        string id = a.Positional.ElementAtOrDefault(3) ?? "";
        string value = a.Positional.ElementAtOrDefault(4) ?? "";
        double Sec(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (op.Length > 0)
        {
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            switch (op)
            {
                case "agregar": Console.WriteLine(ProjectMarkers.Add(p, Sec(id), value.Length > 0 ? value : null).Id); break;
                case "mover": ProjectMarkers.Move(p, id, Sec(value)); break;
                case "renombrar": ProjectMarkers.Rename(p, id, value); break;
                case "quitar": ProjectMarkers.Remove(p, id); break;
                default: throw new ArgumentException("marcadores carpeta [agregar segundos [nombre] | mover id segundos | renombrar id nombre | quitar id]");
            }
            p.AddLog("markers-edited", op);
            store.Save();
        }
        if (p.Markers.Count == 0) Console.WriteLine("Sin marcadores.");
        foreach (var m in p.Markers) Console.WriteLine($"{m.Id} · {m.Sec,8:0.000} s · {m.Name}");
        return 0;
    }

    /// <summary>The A–B range the loop repeats: shows it, sets it (either order) or removes it.</summary>
    static int Loop(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var s = store.Project.Settings;
        if (a.Flag("quitar") || a.Positional.Count > 3)
        {
            if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
            if (a.Flag("quitar")) ProjectMarkers.ClearLoop(s);
            else ProjectMarkers.SetLoop(s, double.Parse(a.Positional[2], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(a.Positional[3], System.Globalization.CultureInfo.InvariantCulture));
            store.Save();
        }
        Console.WriteLine(ProjectMarkers.Loop(s) is { } r ? $"Repite de {r.Start:0.000} s a {r.End:0.000} s ({r.End - r.Start:0.000} s)." : "Sin rango: el loop repite todo el tema.");
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
        foreach (var item in TrackFolders.Sequence(p))
        {
            if (item is Folder f)
            {
                Console.WriteLine($"  [{f.Name}] vol {f.Volume:0.00}{(f.Muted ? " mute" : "")}{(f.Solo ? " solo" : "")}{(f.Collapsed ? " plegada" : "")} · {f.Id}");
                continue;
            }
            var t = (Track)item;
            string clips = string.Join(", ", t.Clips.Select(c => $"{p.FindMedia(c.MediaId)?.Name ?? "?"} @ {c.AtSec:0.000} s"));
            Console.WriteLine($"  {(t.FolderId != null ? "  " : "")}{t.Name,-24} vol {t.Channel.Volume:0.00} pan {t.Channel.Pan:+0.00;-0.00;0} {(t.Video ? "video" : "audio")} · {(clips == "" ? "vacía" : clips)}");
        }
        if (store.ReadOnly) Console.WriteLine($"Solo lectura: {store.ReadOnlyReason}");
        foreach (var n in store.Notices) Console.WriteLine($"Aviso: {n}");
        var snaps = ProjectStore.Snapshots(store.Dir);
        Console.WriteLine($"Historial: {snaps.Count} copias{(snaps.Count > 0 ? $", la última {snaps[0].Time:g}" : "")}");
    }

    /// <summary>
    /// Records a test tone the way the window does (journal, WAV, take into the project, save, journal off),
    /// to try where a take lands without the window. --pista plays the selected track.
    /// </summary>
    /// <summary>Opens the app's camera and starts its video for a take (gclab --camara).</summary>
    static (CameraCapture Cam, VideoTakeWriter Writer) StartVideo(ProjectStore store, ProjectStore.RecordingJournal j, long start)
    {
        var cam = CameraCapture.Open(new CameraSource(CameraDevices.Pick(CameraDevices.List(), null)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((cam.Opening || !cam.HasSignal) && cam.Error == null && sw.Elapsed.TotalSeconds < 15) Thread.Sleep(50);
        if (!cam.HasSignal)
        {
            cam.Dispose();
            throw new InvalidOperationException(cam.Error ?? "La cámara no mandó imagen.");
        }
        var w = VideoTakeWriter.Start(store.Resolve(j.Video)!, cam.Width, cam.Height, () => start);
        cam.Attach(w);
        return (cam, w);
    }

    /// <summary>A 220 Hz tone, written in real time when a camera records along (so the video has its length).</summary>
    static long WriteTone(RecordingWriter w, double seconds, bool realTime, long start)
    {
        const int sr = 48000;
        long frames = (long)(seconds * sr);
        var l = new float[480];
        var r = new float[480];
        long f = 0;
        while (f < frames)
        {
            if (realTime)
                while (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds * sr < f) Thread.Sleep(2);
            for (int i = 0; i < l.Length; i++, f++) l[i] = r[i] = 0.25f * MathF.Sin(2 * MathF.PI * 220 * f / sr);
            w.Append(l, r);
        }
        return f;
    }

    static int RecordTake(string dir, double seconds, string? selected, bool camera)
    {
        using var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new InvalidOperationException(store.ReadOnlyReason);
        const int sr = 48000;
        var p = store.Project;
        var target = TakeRecording.PickTarget(p, selected);
        int number = TakeRecording.NextTakeNumber(p);
        var id = Ids.New("med");
        var j = new ProjectStore.RecordingJournal
        {
            MediaId = id,
            Name = TakeRecording.TakeName(p, target),
            Audio = $"{ProjectStore.MediaDir}/{id}.wav",
            Video = camera ? $"{ProjectStore.MediaDir}/{id}.mp4" : null,
            TrackId = target?.Id,
        };
        store.BeginRecording(j);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var video = camera ? StartVideo(store, j, start) : default;
        long frames;
        using (var w = new RecordingWriter(store.Resolve(j.Audio)!, sr))
            frames = WriteTone(w, seconds, camera, start);
        VideoTakeResult? vres = null;
        if (camera)
        {
            video.Writer.StopTaking();
            video.Cam.Dispose();
            if (seconds <= TakeRecording.MinTakeSec) video.Writer.Discard();
            else vres = video.Writer.Finish(frames / (double)sr);
            video.Writer.Dispose();
            if (vres == null) j.Video = null;
            else Console.WriteLine($"Video: {vres.Frames} cuadros · repetidos {vres.Duplicated} · descartados {vres.Dropped}");
        }
        if (seconds <= TakeRecording.MinTakeSec)
        {
            File.Delete(store.Resolve(j.Audio)!);
            store.EndRecording(id);
            Console.WriteLine($"Toma de {seconds:0.0} s: muy corta, la descarté como la app.");
            return 0;
        }
        var track = TakeRecording.AddTake(p, j, frames / (double)sr, sr, 2, number);
        store.Save();
        store.EndRecording(id);
        Console.WriteLine($"\"{j.Name}\" ({seconds:0.0} s) en la pista {p.Tracks.IndexOf(track) + 1}{(target == null ? " (nueva)" : "")} · compresor {(track.Channel.Comp.On ? track.Channel.Comp.Preset : "apagado")}{(j.Video != null ? " · con video" : "")}");
        return 0;
    }

    /// <summary>Records a test tone like the app will and dies before finishing, to try the recovery.</summary>
    static int Crash(string dir, double seconds, bool camera)
    {
        var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new InvalidOperationException(store.ReadOnlyReason);
        const int sr = 48000;
        var id = Ids.New("med");
        string rel = $"{ProjectStore.MediaDir}/{id}.wav";
        var j = new ProjectStore.RecordingJournal
        {
            MediaId = id,
            Name = "Prueba de corte",
            Audio = rel,
            Video = camera ? $"{ProjectStore.MediaDir}/{id}.mp4" : null,
            TrackId = store.Project.Tracks.FirstOrDefault(t => t.Clips.Count == 0)?.Id,
            AtSec = 0,
        };
        store.BeginRecording(j);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (camera) StartVideo(store, j, start);
        var w = new RecordingWriter(store.Resolve(rel)!, sr) { CheckpointSec = 0.5 };
        // a few blocks after the last checkpoint, then the "crash": no Dispose, no save (the camera's FFmpeg is left orphaned)
        long f = WriteTone(w, seconds + 0.2, camera, start);
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
        history.Checkpoint(p);
        p.Settings.SetMode("audio");
        p.Tracks[0].Video = false;
        Check(history.Undo(p) && p.Settings.Mode == "video" && p.Tracks[0].Video, "deshacer vuelve de solo audio a video y la pista al video");
        Check(history.Redo(p) && p.Settings.AudioOnly && !p.Tracks[0].Video, "rehacer vuelve a solo audio");
        p.Settings.SetMode("video");
        p.Tracks[0].Video = true;
        history.Clear();
        for (int i = 0; i < 205; i++) { history.Checkpoint(p); p.Tracks[0].Name = $"Pista {i}"; }
        int count = 0;
        while (history.Undo(p)) count++;
        Check(count == 200 && p.Tracks[0].Name == "Pista 4", "límite de 200 pasos");
        history.Clear();
        Check(!history.CanUndo && !history.CanRedo && !history.Undo(p) && !history.Redo(p), "limpiar reinicia ambas pilas");
        p.Tracks = [new Track { Clips = [new Clip { MediaId = media.Id, AtSec = 1 }] }];
        media.SyncMs = -80;
        history.Checkpoint(p);
        InspectorEditing.SetSync(p.Tracks[0].Clips[0], media, 25);
        p.Tracks[0].Clips[0].VideoOffsetMs = -40;
        Check(history.Undo(p) && p.Tracks[0].Clips[0].SyncMs == null && p.Tracks[0].Clips[0].VideoOffsetMs == null
            && p.Tracks[0].Clips[0].AtSec == 1 && ReferenceEquals(p.Media[0], media) && media.SyncMs == -80,
            "deshacer sincronía restaura herencia y posición sin tocar Media");
        Check(history.Redo(p) && p.Tracks[0].Clips[0].SyncMs == 25 && p.Tracks[0].Clips[0].VideoOffsetMs == -40
            && Math.Abs(p.Tracks[0].Clips[0].AtSec - 1.105) < 1e-9,
            "rehacer sincronía recupera ambos overrides y el ajuste de anclaje");
        history.Checkpoint(p, "track:color");
        p.Tracks[0].Color = "#fb923c";
        p.Tracks[0].Icon = "electric-guitar";
        Check(history.Undo(p) && p.Tracks[0].Color == null && p.Tracks[0].Icon == null, "deshacer saca el color y el ícono de la pista");
        Check(history.Redo(p) && p.Tracks[0].Color == "#fb923c" && p.Tracks[0].Icon == "electric-guitar", "rehacer los vuelve a poner");
        var looks = ProjectStore.Parse("""{"tracks":[{"color":"#FB923C","icon":" drum-kit "},{"color":"naranja","icon":""},{"color":"#12345z"}]}""");
        Check(looks.Tracks[0].Color == "#fb923c" && looks.Tracks[0].Icon == "drum-kit", "color en minúsculas e ícono sin espacios");
        Check(looks.Tracks[1].Color == null && looks.Tracks[1].Icon == null && looks.Tracks[2].Color == null, "un color o ícono roto queda sin color / sin ícono");
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
            case "buscar":
                var found = ProjectLibrary.Inspect(Need(arg1));
                Console.WriteLine($"{found.Kind}: {found.Dir}");
                foreach (var f in found.Found) Console.WriteLine($"  {f}");
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
            case "plantillas-video":
                foreach (var t in VideoTemplates.List())
                    Console.WriteLine($"  {t.Id,-20} {t.Name,-30} filtro {t.Color.Look} {t.Color.Amount:0}% · viñeta {t.Fx.Vignette:0} · grano {t.Fx.Grain:0} · barras {t.Fx.Letterbox}{(t.Fx.Letterbox == "custom" ? $" {t.Fx.LetterboxHeight:0}%" : "")}");
                return 0;
            case "guardar-plantilla-video":
                using (var store = ProjectStore.Open(Path.GetFullPath(Need(arg1))))
                    VideoTemplates.Save(Need(arg2), store.Project.Settings.Color, store.Project.Settings.VideoFx);
                Console.WriteLine("Plantilla de video guardada.");
                return 0;
            case "aplicar-plantilla-video":
                var vt = VideoTemplates.List().FirstOrDefault(t => t.Id == arg2 || string.Equals(t.Name, arg2, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No hay una plantilla de video \"{arg2}\".");
                using (var store = ProjectStore.Open(Path.GetFullPath(Need(arg1))))
                {
                    VideoTemplates.ApplyTo(vt, store.Project.Settings);
                    store.Save();
                }
                Console.WriteLine($"Apliqué \"{vt.Name}\".");
                return 0;
            case "borrar-plantilla-video":
                VideoTemplates.Delete(Need(arg1));
                Console.WriteLine("Plantilla de video borrada.");
                return 0;
            default:
                throw new ArgumentException("proyectos lista|nuevo|previsualizar|renombrar|duplicar|borrar|olvidar|carpeta|plantillas|guardar-plantilla|borrar-plantilla|plantillas-video|guardar-plantilla-video|aplicar-plantilla-video|borrar-plantilla-video");
        }
    }

    static string Need(string? s) => s ?? throw new ArgumentException("Faltan argumentos.");
}
