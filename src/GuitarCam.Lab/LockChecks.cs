using System.Text.Json;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>
/// gclab proyecto verificar-bloqueo: the lock rule (<see cref="ClipEditing.IsLocked"/>) on a project in memory. A
/// locked clip, or one of a locked track, refuses a new place, a trim, a split, being deleted or carried off; its
/// gain still changes; the edits over a range skip it (closing the gap leaves its track alone); a take recorded
/// over it goes to a new track; and the lock is saved and read back.
/// </summary>
static class LockChecks
{
    public static int Run()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {what}");
            if (!ok) failed++;
        }
        bool Refuses(Action edit)
        {
            try { edit(); return false; }
            catch (ArgumentException) { return true; }
        }

        var media = new Media { Name = "toma", Audio = "media/toma.wav", DurationSec = 4 };
        var locked = new Clip { MediaId = media.Id, AtSec = 0, Locked = true };
        var free = new Clip { MediaId = media.Id, AtSec = 5 };
        var a = new Track { Name = "Guitarra", Clips = [locked, free] };
        var b = new Track { Name = "Bajo", Clips = [new Clip { MediaId = media.Id, AtSec = 6 }] };
        var p = new Project { Media = [media], Tracks = [a, b] };

        var moved = ClipEditing.Copy(locked);
        moved.AtSec = 1;
        Check("un clip bloqueado no se mueve", Refuses(() => ClipEditing.Replace(p, moved)) && locked.AtSec == 0);
        var louder = ClipEditing.Copy(locked);
        louder.GainDb = 3;
        ClipEditing.Replace(p, louder);
        Check("pero su volumen sí cambia", a.Clips[0].GainDb == 3);
        Check("no se divide, no se borra, no va al inicio ni se restaura",
            Refuses(() => ClipEditing.Split(p, locked.Id, 2)) && Refuses(() => ClipEditing.RemoveClip(p, locked.Id))
            && Refuses(() => ClipEditing.ToStart(p, locked.Id)) && Refuses(() => ClipEditing.Reset(p, locked.Id)) && a.Clips.Count == 2);
        Check("no se va a otra pista ni se saca su pista",
            Refuses(() => ClipEditing.MoveToTrack(p, locked.Id, b.Id)) && Refuses(() => ClipEditing.RemoveTrack(p, a.Id)) && p.Tracks.Count == 2);

        int cut = ClipEditing.DeleteRange(p, a.Id, 2, 7);
        var after = a.Clips.Find(c => c.Id == free.Id)!;
        Check($"borrar un tramo lo saltea y recorta el otro ({cut} cambio)",
            cut == 1 && a.Clips[0].Locked && a.Clips[0].TrimEndSec == 0 && after.TrimStartSec > 0);

        ClipEditing.DeleteRangeClosing(p, [a.Id, b.Id], 0.5, 1.5);
        Check("cerrar el hueco deja quieta su pista y corre la otra",
            a.Clips[0].AtSec == 0 && a.Clips.Find(c => c.Id == free.Id)!.AtSec == 5 && b.Clips[0].AtSec == 5);

        b.Locked = true;
        Check("con la pista bloqueada, sus clips tampoco se mueven", ClipEditing.IsLocked(p, b.Clips[0].Id) && Refuses(() => ClipEditing.ToStart(p, b.Clips[0].Id)));
        Check("y no recibe clips de otra pista", Refuses(() => ClipEditing.MoveToTrack(p, a.Clips[^1].Id, b.Id)));

        var j = new ProjectStore.RecordingJournal { MediaId = Ids.New("med"), Name = "Toma 9", Audio = "media/toma9.wav", TrackId = a.Id, AtSec = 0, StartSec = 0 };
        var got = TakeRecording.AddTake(p, j, 3, 48000, 2, 9);
        Check("una toma grabada sobre ella va a una pista nueva", got != a && p.Tracks.Count == 3 && a.Clips.Any(c => c.Id == locked.Id));
        var empty = new Track { Name = "Vacía", Locked = true };
        p.Tracks.Add(empty);
        Check("grabar no elige una pista vacía bloqueada", TakeRecording.PickTarget(p, empty.Id) != empty);

        string json = JsonSerializer.Serialize(p, ProjectStore.Json);
        var back = JsonSerializer.Deserialize<Project>(json, ProjectStore.Json)!;
        Check("se guarda y se lee", back.Tracks[0].Clips[0].Locked && back.Tracks[1].Locked && !back.Tracks[0].Clips[1].Locked);
        Check("lo desbloqueado no escribe nada", !JsonSerializer.Serialize(new Clip(), ProjectStore.Json).Contains("locked"));

        Console.WriteLine(failed == 0 ? "Todo bien." : $"{failed} comprobaciones fallaron.");
        return failed == 0 ? 0 : 3;
    }
}
