using WildTrack.Projects;

namespace WildTrack.Lab;

/// <summary>
/// Historial → Pasos and Versiones without the window: the names the steps get (<see cref="EditSteps"/>), jumping to a
/// step and back (<see cref="UndoManager.JumpTo"/>), and the saved versions on a throwaway project (named, of an export,
/// kept from pruning, unlinked, and going back keeping the takes and exports).
/// </summary>
static class StepChecks
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Pasos: {text}");
            n++;
        }
        var history = new UndoManager();
        var take = new Media { Name = "Toma 1", DurationSec = 10 };
        var p = new Project { Name = "Prueba", Media = [take], Tracks = [new Track { Name = "Guitarra" }, new Track { Name = "Bajo" }] };
        p.Tracks[0].Clips.Add(new Clip { MediaId = take.Id, AtSec = 1 });
        string? Name(string id) => p.FindMedia(id)?.Name;
        string Last() => history.Steps(p, Name)[^1].Label.Text;

        history.Checkpoint(p);
        p.Tracks[0].Channel.Volume = 0.5;
        Check(Last().StartsWith("Volumen de «Guitarra»: -6"), "volumen: " + Last());
        history.Checkpoint(p);
        p.Tracks[1].Channel.Muted = true;
        Check(Last() == "Silenciaste «Bajo»", "mute: " + Last());
        history.Checkpoint(p);
        p.Tracks[0].Clips[0].AtSec = 2;
        Check(Last() == "Moviste «Toma 1»", "mover clip: " + Last());
        history.Checkpoint(p);
        ClipEditing.Split(p, p.Tracks[0].Clips[0].Id, 5);
        Check(Last() == "Cortaste «Toma 1» en «Guitarra»", "cortar: " + Last());
        history.Checkpoint(p);
        p.Tracks[0].Channel.Solo = p.Tracks[1].Channel.Solo = true;
        Check(Last() == "Solo en 2 pistas", "solo en dos: " + Last());
        history.Checkpoint(p);
        p.Tracks[0].Channel.Slots.Add(InsertSlot.New(InsertSlot.GateKind));
        Check(Last() == "Agregaste el gate a «Guitarra»", "efecto nuevo: " + Last());
        history.Checkpoint(p);
        p.Tracks.Add(new Track { Name = "Voz" });
        Check(Last() == "Agregaste la pista «Voz»", "pista nueva: " + Last());
        history.Checkpoint(p);
        (p.Tracks[0], p.Tracks[1]) = (p.Tracks[1], p.Tracks[0]);
        Check(Last().StartsWith("Moviste la pista"), "orden: " + Last());
        history.Checkpoint(p);
        p.Tracks[0].Name = "Bajo eléctrico";
        p.Tracks[0].Channel.Pan = -0.3;
        p.Tracks[0].Color = "#fb923c";
        Check(Last() == "Cambiaste varias cosas de «Bajo eléctrico»", "varias en una pista: " + Last());
        history.Checkpoint(p, label: EditSteps.Recorded(["Toma 2"], []));
        p.Tracks[2].Name = "Voz 2";
        Check(Last() == "Grabaste «Toma 2»", "la grabación trae su nombre: " + Last());

        var steps = history.Steps(p, Name);
        Check(steps.Count == 10 && steps.All(s => s.Done) && history.Position == 10, "diez pasos hechos");
        Check(history.JumpTo(p, 2) && history.Position == 2, "saltar al paso 2");
        Check(p.Tracks.Count == 2 && p.Tracks[0].Name == "Guitarra" && p.Tracks[0].Channel.Volume == 0.5 && p.Tracks[1].Channel.Muted
            && p.Tracks[0].Clips.Count == 1 && p.Tracks[0].Clips[0].AtSec == 1, "el proyecto queda como después del paso 2");
        steps = history.Steps(p, Name);
        Check(steps.Count == 10 && steps.Count(s => s.Done) == 2 && steps[2].Label.Text == "Moviste «Toma 1»", "los pasos de después quedan para rehacer, con su nombre");
        Check(!history.JumpTo(p, 2) && !history.JumpTo(p, 11) && !history.JumpTo(p, -1), "saltar a donde ya está o afuera no hace nada");
        Check(history.JumpTo(p, 10) && p.Tracks.Count == 3 && p.Tracks[2].Name == "Voz 2" && p.Tracks[0].Name == "Bajo eléctrico", "saltar al final rehace todo");
        Check(history.JumpTo(p, 0) && p.Tracks[0].Channel.Volume == 1 && !p.Tracks[1].Channel.Muted && !history.CanUndo, "saltar al principio deshace todo");
        Check(history.Redo(p) && p.Tracks[0].Channel.Volume == 0.5 && history.Position == 1, "rehacer sigue andando después de saltar");
        history.Checkpoint(p);
        p.Markers.Add(new Marker { Name = "Solo", Sec = 30 });
        Check(!history.CanRedo && history.Steps(p, Name).Count == 2 && Last() == "Agregaste el marcador «Solo»", "una edición nueva borra lo que estaba para rehacer");
        Check(history.Undo(p) && p.Markers.Count == 0 && history.Steps(p, Name)[^1].Label.Text == "Agregaste el marcador «Solo»", "deshacer conserva el nombre del paso");

        n += Versions();
        Console.WriteLine($"Pasos y versiones: {n} comprobaciones bien (sin tocar proyectos de verdad).");
        return 0;
    }

    static int Versions()
    {
        int n = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Versiones: {text}");
            n++;
        }
        string dir = Path.Combine(Path.GetTempPath(), $"wtlab-versiones-{Guid.NewGuid():N}");
        try
        {
            var a = new Media { Name = "Toma 1", Audio = "media/a.wav", DurationSec = 3 };
            var p = new Project { Name = "Prueba", Media = [a], Tracks = [new Track { Name = "Guitarra", Clips = [new Clip { MediaId = a.Id }] }] };
            using var store = ProjectStore.Create(dir, p);
            int first = ProjectStore.Snapshots(dir).Count; // the first save leaves an automatic one
            var named = store.SaveVersion(store.Serialize(), "  antes de mezclar  ");
            Check(named.Name == "antes de mezclar" && File.Exists(named.Path), "una versión con nombre (sin espacios de más)");
            store.Project.Tracks[0].Channel.Volume = 0.25;
            store.Project.Exports.Insert(0, new ExportRecord { File = "exports/video.mp4" });
            var exported = store.SaveVersion(store.Serialize(), null, "exports/video.mp4");
            var list = ProjectStore.Snapshots(dir);
            Check(list.Count == first + 2 && list[0].Export == "exports/video.mp4" && list[1].Name == "antes de mezclar" && list.Take(2).All(s => s.Kept), "la lista trae nombre y exportación");

            // old automatic versions go, old named ones stay
            string h = Path.Combine(dir, ProjectStore.HistoryDir);
            string json = File.ReadAllText(named.Path);
            var old = DateTime.Now.AddDays(-200);
            File.WriteAllText(Path.Combine(h, old.ToString("yyyy-MM-dd_HH-mm-ss") + ".json"), json);
            File.WriteAllText(Path.Combine(h, old.AddMinutes(1).ToString("yyyy-MM-dd_HH-mm-ss") + ".json"), json);
            var oldOne = ProjectStore.Snapshots(dir).Last();
            store.RenameVersion(oldOne, "la primera");
            for (int i = 0; i < ProjectStore.KeepRecentSnapshots + 2; i++)
                File.WriteAllText(Path.Combine(h, DateTime.Now.AddSeconds(-100 - i).ToString("yyyy-MM-dd_HH-mm-ss") + ".json"), json);
            store.SaveVersion(store.Serialize(), "después de mezclar");
            list = ProjectStore.Snapshots(dir);
            Check(list.Any(s => s.Name == "la primera") && list.Count(s => s.Time.Date == old.Date) == 1, "las viejas sin nombre se borran, las con nombre no");
            Check(list.Any(s => s.Name == "antes de mezclar") && list.Any(s => s.Export == "exports/video.mp4"), "las recientes con nombre o exportación siguen");

            store.RenameVersion(list.First(s => s.Name == "la primera"), " ");
            Check(ProjectStore.Snapshots(dir).All(s => s.Name != "la primera"), "un nombre vacío se lo saca");
            store.UnlinkExport("exports/video.mp4");
            Check(ProjectStore.Snapshots(dir).All(s => s.Export == null), "borrar la exportación suelta su versión");

            // going back: the song goes back, the takes and exports stay
            var b = new Media { Name = "Toma 2", Audio = "media/b.wav", DurationSec = 4 };
            store.Project.Media.Add(b);
            store.Project.Tracks.Add(new Track { Name = "Voz", Clips = [new Clip { MediaId = b.Id }] });
            store.Project.Name = "Prueba renombrada";
            store.Restore(ProjectStore.Snapshots(dir).First(s => s.Name == "antes de mezclar"));
            var now = store.Project;
            Check(now.Tracks.Count == 1 && now.Tracks[0].Channel.Volume == 1, "vuelve la mezcla de esa versión");
            Check(now.FindMedia(b.Id) != null && now.Exports.Count == 1 && now.Name == "Prueba renombrada", "la toma nueva, la exportación y el nombre se quedan");
            var after = ProjectStore.Snapshots(dir);
            Check(after[0].Before == named.File, "lo de antes queda como otra versión, que dice de dónde se volvió");
            Check(ProjectStore.Parse(File.ReadAllText(after[0].Path)).Tracks.Count == 2, "esa versión tiene lo que había antes de volver");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        return n;
    }
}
