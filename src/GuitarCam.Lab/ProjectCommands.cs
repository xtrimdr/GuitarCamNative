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
        string? dir = a.Positional.Skip(1).FirstOrDefault();
        if (dir == null) throw new ArgumentException("Falta la carpeta del proyecto.");
        dir = Path.GetFullPath(dir);
        return sub switch
        {
            "abrir" => Open(dir),
            "versiones" => Versions(dir),
            "volver" => Restore(dir, a.Positional.Skip(2).FirstOrDefault()),
            "clips" => Clips(dir),
            "importar-audio" => ImportAudio(dir, a),
            "editar" => EditClips(dir, a),
            "historial" => HistoryCommands.Run(dir, a),
            "encuadre" => Frame(dir, a),
            "sincronia" => Sync(dir, a),
            "formato" => Format(dir, a),
            "modo" => Mode(dir, a),
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
            case "fijar-corte": ClipEditing.FixCut(p, id); break;
            case "restaurar": ClipEditing.Reset(p, id); break;
            case "dividir": Console.WriteLine(ClipEditing.Split(p, id, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))); break;
            case "duplicar": Console.WriteLine(ClipEditing.Duplicate(p, id)); break;
            case "ganancia": ClipEditing.SetGain(p, id, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "curva":
                // "segundos:dB" separated by commas, seconds from the start of the recording; empty = flat
                ClipEditing.SetCurve(p, id, value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(pt => pt.Split(':'))
                    .Select(pt => new GainPoint { Sec = double.Parse(pt[0], System.Globalization.CultureInfo.InvariantCulture), Db = double.Parse(pt[1], System.Globalization.CultureInfo.InvariantCulture) }));
                break;
            case "borrar-rango":
                Console.WriteLine($"{ClipEditing.DeleteRange(p, id, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), a.Double("hasta", 0))} clip(s) cambiados");
                break;
            default:
                var mode = op switch
                {
                    "mover" => ClipEditMode.Move, "recortar-inicio" => ClipEditMode.TrimStart,
                    "recortar-final" => ClipEditMode.TrimEnd, "fade-in" => ClipEditMode.FadeIn, "fade-out" => ClipEditMode.FadeOut,
                    _ => throw new ArgumentException("editar: mover|recortar-inicio|recortar-final|fade-in|fade-out clip delta-segundos; dividir clip segundos; ganancia clip dB; curva clip seg:dB,seg:dB; duplicar|a-pista|inicio|fijar-corte|restaurar|quitar-clip|quitar-pista|nueva-pista|agregar-toma|reordenar"),
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
            case "plantillas-video":
                foreach (var t in VideoTemplates.List())
                    Console.WriteLine($"  {t.Id,-20} {t.Name,-30} filtro {t.Color.Look} {t.Color.Amount:0}% · viñeta {t.Fx.Vignette:0} · grano {t.Fx.Grain:0} · barras {t.Fx.Letterbox}");
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
