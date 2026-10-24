using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>The Historial tab without the window: takes in use, exports, activity, and its actions (ProjectHistory).</summary>
static class HistoryCommands
{
    public static int Run(string dir, Args a)
    {
        using var store = ProjectStore.Open(dir);
        var p = store.Project;
        string op = a.Positional.ElementAtOrDefault(2) ?? "";
        string id = a.Positional.ElementAtOrDefault(3) ?? "";
        if (op.Length > 0 && store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        switch (op)
        {
            case "":
                Show(store);
                return 0;
            case "quitar":
                ProjectHistory.RemoveFromTimeline(p, id);
                break;
            case "usar":
                ClipEditing.AddMedia(p, id, a.Get("pista"));
                break;
            case "borrar-no-usadas":
                var r = ProjectHistory.PurgeUnused(store);
                Console.WriteLine($"Mandé {r.Count} toma(s) a la Papelera ({ProjectHistory.FormatBytes(r.Bytes)}).");
                break;
            case "borrar-exportacion":
                var e = p.Exports.OrderByDescending(x => x.CreatedAt).ElementAtOrDefault(int.Parse(id) - 1)
                    ?? throw new ArgumentException("No hay una exportación con ese número.");
                ProjectHistory.DeleteExport(store, e);
                Console.WriteLine($"Mandé {Path.GetFileName(e.File)} a la Papelera.");
                break;
            default:
                throw new ArgumentException("historial carpeta [quitar|usar medio | borrar-no-usadas | borrar-exportacion n]");
        }
        store.Save();
        Show(store);
        return 0;
    }

    static void Show(ProjectStore store)
    {
        var p = store.Project;
        var used = ProjectHistory.UsedMediaIds(p);
        Console.WriteLine($"Tomas ({p.Media.Count}, {used.Count(id => p.FindMedia(id) != null)} en uso):");
        foreach (var m in p.Media.OrderByDescending(m => m.CreatedAt))
            Console.WriteLine($"  {m.Id} · {m.Name} · {m.CreatedAt.ToLocalTime():dd/MM HH:mm} · {ProjectHistory.FormatDuration(m.DurationSec)} · " +
                $"{ProjectHistory.FormatBytes(ProjectHistory.MediaBytes(store.Dir, m))}{(used.Contains(m.Id) ? "" : " · No usada")}");
        Console.WriteLine($"Exportaciones ({p.Exports.Count}):");
        int n = 1;
        foreach (var e in p.Exports.OrderByDescending(x => x.CreatedAt))
            Console.WriteLine($"  {n++}. {Path.GetFileName(e.File)} · {ProjectHistory.FormatAgo(e.CreatedAt)} · {e.Aspect} · {e.TakeIds.Count} toma(s)" +
                $"{(File.Exists(ProjectHistory.ExportPath(store.Dir, e)) ? "" : " · falta el archivo")}");
        Console.WriteLine("Actividad:");
        foreach (var l in ProjectHistory.BuildActivity(p).Take(ProjectHistory.ActivityPage))
            Console.WriteLine($"  {l.At.ToLocalTime():dd/MM HH:mm} [{l.Icon}] {l.Text}");
    }

    /// <summary>Regression cases on a throwaway project: in-use logging, purge to the Recycle Bin, export deletion, activity lines.</summary>
    public static int Check()
    {
        int n = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Historial: {text}");
            n++;
        }
        string dir = Path.Combine(Path.GetTempPath(), $"gclab-historial-{Guid.NewGuid():N}");
        try
        {
            var p = new Project { Name = "Prueba" };
            var a = new Media { Name = "Canal 1", Audio = "media/a.wav", DurationSec = 3 };
            var b = new Media { Name = "Canal 2", Audio = "media/b.wav", DurationSec = 75 };
            p.Media.AddRange([a, b]);
            p.Tracks.Add(new Track { Name = a.Name, Clips = [new Clip { MediaId = a.Id }] });
            p.Tracks.Add(new Track { Name = b.Name, Clips = [new Clip { MediaId = b.Id }] });
            p.AddLog("session-created");
            Directory.CreateDirectory(Path.Combine(dir, "media"));
            File.WriteAllBytes(Path.Combine(dir, "media", "a.wav"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(dir, "media", "b.wav"), new byte[4096]);
            string outside = Path.Combine(dir, "..", $"gclab-afuera-{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(outside, new byte[10]);
            p.Exports.Add(new ExportRecord { File = Path.GetFullPath(outside), Bytes = 10, TakeIds = [a.Id, b.Id] });
            using var store = ProjectStore.Create(dir, p);
            p = store.Project;

            Check(ProjectHistory.MediaBytes(store.Dir, b) == 4096, "tamaño en disco");
            ProjectHistory.RemoveFromTimeline(p, b.Id);
            Check(p.Tracks.Count == 1, "quitar del timeline saca su pista");
            store.Save();
            Check(p.Log[^1] is { Type: "take-removed", Name: "Canal 2" }, "registra que salió del timeline");
            store.Save();
            Check(p.Log.Count(e => e.Type == "take-removed") == 1, "guardar de nuevo no repite el registro");
            ClipEditing.AddMedia(p, b.Id, null);
            store.Save();
            Check(p.Log[^1].Type == "take-restored", "volver a usarla también queda");
            p.Media.Add(new Media { Name = "Nueva", Audio = "media/c.wav" });
            p.Tracks.Add(new Track { Clips = [new Clip { MediaId = p.Media[^1].Id }] });
            store.Save();
            Check(p.Log[^1].Type == "take-restored", "una toma nueva no se registra como vuelta");

            ProjectHistory.RemoveFromTimeline(p, b.Id);
            var r = ProjectHistory.PurgeUnused(store);
            Check(r.Count == 1 && r.Bytes == 4096 && p.FindMedia(b.Id) == null, "borrar no usadas saca la toma");
            Check(!File.Exists(Path.Combine(dir, "media", "b.wav")) && File.Exists(Path.Combine(dir, "media", "a.wav")), "solo su archivo va a la Papelera");
            Check(ProjectHistory.PurgeUnused(store).Count == 0, "sin no usadas no hace nada");

            ProjectHistory.DeleteExport(store, p.Exports[0]);
            Check(p.Exports.Count == 0 && !File.Exists(outside), "la exportación va a la Papelera aunque esté afuera");

            var lines = ProjectHistory.BuildActivity(p);
            Check(lines[0].Text == "Mandaste «gclab-afuera" + lines[0].Text["Mandaste «gclab-afuera".Length..] && lines[0].Rec, "borrar una exportación: rojo");
            Check(lines[1].Text == "Mandaste «Canal 2» a la Papelera (4 KB)", "borrar no usadas, con el tamaño");
            Check(lines[2].Text == "Sacaste «Canal 2» del timeline", "sacar del timeline");
            Check(lines[^1].Text == "Creaste el proyecto", "lo más viejo al final");

            var q = new Project();
            var t0 = DateTime.UtcNow;
            foreach (var (name, s) in new[] { ("A", 0), ("B", 30), ("C", 60), ("D", 400) })
                q.Log.Add(new LogEntry { Type = "take-removed", Name = name, At = t0.AddSeconds(s) });
            q.Log.Add(new LogEntry { Type = "clip-moved", At = t0.AddSeconds(401) });
            var burst = ProjectHistory.BuildActivity(q);
            Check(burst.Count == 2 && burst[1].Text == "Sacaste «A», «B» y «C» del timeline" && burst[0].Text == "Sacaste «D» del timeline",
                "las ráfagas se juntan y los movimientos de clips no aparecen");
            Check(ProjectHistory.FormatDuration(12.34) == "12.3 s" && ProjectHistory.FormatDuration(75) == "1:15", "duración como la web");
            var today = new DateTime(2026, 10, 23);
            Check(ProjectHistory.DayLabel(today.AddHours(5), today) == "Hoy" && ProjectHistory.DayLabel(today.AddDays(-1), today) == "Ayer"
                && ProjectHistory.DayLabel(new DateTime(2026, 10, 20), today) == "Martes, 20 de octubre", "días como la web");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        Console.WriteLine($"Historial: {n} comprobaciones bien.");
        return 0;
    }
}
