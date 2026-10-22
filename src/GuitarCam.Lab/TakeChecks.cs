using GuitarCam.Projects;

namespace GuitarCam.Lab;

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
        var media = new Media { Name = "Toma 3 — Base Rítmica", DurationSec = 5 };
        var full = new Track { Name = "Toma 3 — Base Rítmica", Clips = [new Clip { MediaId = media.Id }] };
        var empty1 = new Track { Name = "Pista 2" };
        var empty2 = new Track { Name = "Voz" };
        var p = new Project { Tracks = [empty1, full, empty2] };
        p.Media.Add(media);

        Check(TakeRecording.PickTarget(p, empty2.Id) == empty2, "la pista vacía seleccionada");
        Check(TakeRecording.PickTarget(p, full.Id) == empty2, "con una toma seleccionada, la vacía siguiente");
        Check(TakeRecording.PickTarget(p, null) == empty1, "sin selección, la primera vacía");
        Check(TakeRecording.NextTakeNumber(p) == 4, "numera después de la toma más alta");
        Check(TakeRecording.TakeName(p, empty1) == "Toma 4 — Base Rítmica", "nombre por posición (base)");
        Check(TakeRecording.TakeName(p, empty2) == "Voz", "una pista con nombre propio lo conserva");
        Check(!TakeRecording.IsFirstTake(p), "con una toma no es la primera");

        var j = new ProjectStore.RecordingJournal { MediaId = "med_a", Name = "Toma 4 — Base Rítmica", Audio = "media/med_a.wav", TrackId = empty1.Id, AtSec = -0.012, SyncMs = -12 };
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
        Check(t.Name == "Toma 6 — Arreglo" && t.Channel.Comp.Preset == "rhythm", "la cuarta pista es un arreglo");

        var two = new Project { Tracks = [new Track { Clips = [new Clip()] }] };
        Check(TakeRecording.TakeName(two, null) == "Toma 1 — Solo / Punteo", "la segunda pista es el solo");
        j = new ProjectStore.RecordingJournal { MediaId = "med_d", Name = "x", Audio = "media/med_d.wav" };
        Check(TakeRecording.AddTake(two, j, 1, 48000, 2, 1).Channel.Comp.Preset == "lead", "el solo arranca con el preset de punteo");
        Check(TakeRecording.IsFirstTake(new Project { Tracks = [new Track()] }), "pistas vacías: es la primera toma");

        Console.WriteLine($"Tomas: {n} comprobaciones correctas (sin modificar archivos).");
        return 0;
    }
}
