using WildTrack.Projects;

namespace WildTrack.Lab;

/// <summary>Regression cases for where a new take lands (App.tsx handleStartRecordingSequence / handleStopRecording).</summary>
static class TakeChecks
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Tomas: {text}");
            n++;
        }
        var media = new Media { Name = "Canal 3 — Base Rítmica", DurationSec = 5 };
        var full = new Track { Name = "Canal 3 — Base Rítmica", Clips = [new Clip { MediaId = media.Id }] };
        var empty1 = new Track { Name = "Pista 2" };
        var empty2 = new Track { Name = "Voz" };
        var p = new Project { Tracks = [empty1, full, empty2] };
        p.Media.Add(media);

        Check(TakeRecording.PickTarget(p, empty2.Id) == empty2, "la pista vacía seleccionada");
        Check(TakeRecording.PickTarget(p, full.Id) == empty2, "con una toma seleccionada, la vacía siguiente");
        Check(TakeRecording.PickTarget(p, null) == empty1, "sin selección, la primera vacía");
        Check(TakeRecording.NextTakeNumber(p) == 4, "numera después dla toma más alta");
        Check(TakeRecording.TakeName(p, empty1) == "Canal 4 — Base Rítmica", "nombre por posición (base)");
        Check(TakeRecording.TakeName(p, empty2) == "Voz", "una pista con nombre propio lo conserva");
        Check(!TakeRecording.IsFirstTake(p), "con una toma no es la primera");

        var j = new ProjectStore.RecordingJournal { MediaId = "med_a", Name = "Canal 4 — Base Rítmica", Audio = "media/med_a.wav", TrackId = empty1.Id, AtSec = -0.012, SyncMs = -12 };
        var t = TakeRecording.AddTake(p, j, 2.5, 48000, 2, 4);
        Check(t == empty1 && t.Clips.Count == 1 && t.Clips[0].AtSec == -0.012, "la toma cae en la pista elegida, en su corrección");
        Check(t.Channel.Comp.On && t.Channel.Comp.Preset == "rhythm", "canal sin tocar: compresor prendido, rasgueo en la primera");
        Check(p.FindMedia("med_a") is { Kind: "recording", DurationSec: 2.5 }, "agrega el medio");
        Check(p.Log.Last().Type == "take-recorded" && p.Log.Last().Name == j.Name, "queda en la actividad");

        empty2.Channel.Volume = 0.5;
        empty2.Channel.Comp.Preset = "custom";
        j = new ProjectStore.RecordingJournal { MediaId = "med_b", Name = "Voz", Audio = "media/med_b.wav", TrackId = empty2.Id };
        t = TakeRecording.AddTake(p, j, 1, 48000, 2, 5);
        Check(t == empty2 && t.Channel.Volume == 0.5 && !t.Channel.Comp.On, "una pista ya ajustada conserva su canal");

        j = new ProjectStore.RecordingJournal { MediaId = "med_c", Name = TakeRecording.TakeName(p, null), Audio = "media/med_c.wav" };
        int before = p.Tracks.Count;
        t = TakeRecording.AddTake(p, j, 1, 48000, 2, 6);
        Check(p.Tracks.Count == before + 1 && p.Tracks[^1] == t && !t.Video, "sin pista vacía: una nueva al final, solo audio");
        Check(t.Name == "Canal 6 — Arreglo" && t.Channel.Comp.Preset == "rhythm", "la cuarta pista es un arreglo");

        j = new ProjectStore.RecordingJournal { MediaId = "med_v", Name = "x", Audio = "media/med_v.wav", Video = "media/med_v.mp4", VideoOffsetMs = 120, Mirror = true };
        t = TakeRecording.AddTake(p, j, 1, 48000, 2, 7);
        Check(t.Video && t.Clips[0].Framing.Mirror && p.FindMedia("med_v") is { Video: "media/med_v.mp4", VideoOffsetMs: 120 },
            "con cámara: pista con panel, el video y su latencia en el medio, espejada como el monitor");

        var two = new Project { Tracks = [new Track { Clips = [new Clip()] }] };
        Check(TakeRecording.TakeName(two, null) == "Canal 1 — Solo / Punteo", "la segunda pista es el solo");
        j = new ProjectStore.RecordingJournal { MediaId = "med_d", Name = "x", Audio = "media/med_d.wav" };
        Check(TakeRecording.AddTake(two, j, 1, 48000, 2, 1).Channel.Comp.Preset == "lead", "el solo arranca con el preset de punteo");
        Check(TakeRecording.IsFirstTake(new Project { Tracks = [new Track()] }), "pistas vacías: es la primera toma");

        full.Channel.Pan = -0.4;
        var old = full.Clips[0];
        j = new ProjectStore.RecordingJournal { MediaId = "med_e", Name = "Canal 8 — Solo / Punteo", Audio = "media/med_e.wav", TrackId = full.Id };
        t = TakeRecording.AddTake(p, j, 3, 48000, 2, 8);
        Check(t == full && t.Clips.Count == 1 && t.Clips[0].MediaId == "med_e" && t.Channel.Pan == -0.4,
            "armada con una toma: la nueva la reemplaza y el canal queda");
        Check(p.FindMedia(old.MediaId) is { } kept && kept.Extra?.ContainsKey("lastTimelineClip") == true,
            "la toma reemplazada queda en el historial con su clip");

        // from the playhead (punch-in): only the stretch the take covers goes
        var longMedia = new Media { Id = "med_long", Name = "larga", DurationSec = 20 };
        var punched = new Track { Name = "Solo", Clips = [new Clip { MediaId = longMedia.Id }] };
        var pp = new Project { Tracks = [punched] };
        pp.Media.Add(longMedia);
        j = new ProjectStore.RecordingJournal { MediaId = "med_p", Name = "Solo", Audio = "media/med_p.wav", TrackId = punched.Id, StartSec = 8, AtSec = 8 - 0.012, SyncMs = -12 };
        TakeRecording.AddTake(pp, j, 4, 48000, 2, 9);
        var parts = punched.Clips.Where(c => c.MediaId == longMedia.Id).Select(c => ClipEditing.Window(c, longMedia)).OrderBy(w => w.Start).ToList();
        Check(parts.Count == 2 && Math.Abs(parts[0].End - 7.988) < 1e-6 && Math.Abs(parts[1].Start - 11.988) < 1e-6,
            "desde el cabezal: la toma vieja queda antes y después del tramo grabado");
        Check(punched.Clips[^1].MediaId == "med_p" && Math.Abs(punched.Clips[^1].AtSec - 7.988) < 1e-9,
            "desde el cabezal: la toma nueva cae donde arrancó, arriba de todo");

        // several armed tracks at once: consecutive numbers, each take on its own track, each input saved
        var a = new Track { Name = "Pista 1", Input = "1" };
        var b = new Track { Name = "Pista 2", Input = "3-4" };
        var multi = new Project { Tracks = [a, b] };
        int num = TakeRecording.NextTakeNumber(multi);
        Check(TakeRecording.TakeName(multi, a, num) == "Canal 1 — Base Rítmica" && TakeRecording.TakeName(multi, b, num + 1) == "Canal 2 — Solo / Punteo",
            "dos pistas a la vez: números seguidos y cada una su rol");
        TakeRecording.AddTake(multi, new ProjectStore.RecordingJournal { MediaId = "med_m1", Name = "Canal 1 — Base Rítmica", Audio = "media/med_m1.wav", TrackId = a.Id }, 2, 48000, 2, num);
        TakeRecording.AddTake(multi, new ProjectStore.RecordingJournal { MediaId = "med_m2", Name = "Canal 2 — Solo / Punteo", Audio = "media/med_m2.wav", TrackId = b.Id }, 2, 48000, 2, num + 1);
        Check(a.Clips.Single().MediaId == "med_m1" && b.Clips.Single().MediaId == "med_m2" && TakeRecording.NextTakeNumber(multi) == 3,
            "cada toma en su pista");
        Check(a.Input == "1" && b.Input == "3-4", "la pista conserva su entrada");
        var json = System.Text.Json.JsonSerializer.Serialize(multi, ProjectStore.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<Project>(json, ProjectStore.Json)!;
        Check(back.Tracks[1].Input == "3-4" && !json.Contains("\"input\":null"), "la entrada se guarda (y no se escribe si es la de Configuración)");

        Check(Audio.InputRoute.Parse("3") == new Audio.InputRoute(2, false) && Audio.InputRoute.Parse("3-4") == new Audio.InputRoute(2, true),
            "entradas: \"3\" mono, \"3-4\" par");
        Check(Audio.InputRoute.Parse("3-5") == null && Audio.InputRoute.Parse("0") == null && Audio.InputRoute.Parse("x") == null && Audio.InputRoute.Parse(null) == null,
            "entradas: lo que no es una entrada o un par seguido es la de Configuración");
        Check(string.Join(",", Audio.InputRoute.All(5).Select(r => r.Key)) == "1,2,3,4,5,1-2,3-4", "entradas: cada una y después los pares");

        Console.WriteLine($"Tomas: {n} comprobaciones correctas (sin modificar archivos).");
        return 0;
    }
}
