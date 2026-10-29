using System.Text.Json;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>
/// gclab proyecto verificar-ab: the mix's A/B (<see cref="MixCompare"/>) on a project in memory. The first switch
/// copies, the two mixes go back and forth, mute and solo stay, a track or a bus made later is left alone, a bus that
/// is gone drops its sends, the slots keep their ids, and it is saved, undone and forgotten.
/// </summary>
static class MixCompareChecks
{
    public static int Run()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {what}");
            if (!ok) failed++;
        }

        var guitar = new Track { Name = "Guitarra" };
        var p = new Project { Tracks = [guitar] };
        string reverb = p.Settings.Buses[0].Id;
        guitar.Channel.Volume = 0.8;
        string slot = guitar.Channel.Inserts![0].Id;

        Check("la primera vez B arranca igual a A", MixCompare.Switch(p) == "b" && guitar.Channel.Volume == 0.8 && MixCompare.HasOther(p));
        guitar.Channel.Volume = 0.5;
        guitar.Channel.Pan = -0.4;
        guitar.Channel.Muted = true;
        Check("volver a A trae su mezcla", MixCompare.Switch(p) == "a" && guitar.Channel.Volume == 0.8 && guitar.Channel.Pan == 0);
        Check("el mute queda como estaba", guitar.Channel.Muted);
        Check("y B guardó la suya", MixCompare.Switch(p) == "b" && guitar.Channel.Volume == 0.5 && guitar.Channel.Pan == -0.4);
        Check("los efectos conservan su id (no pierden su estado)", guitar.Channel.Inserts![0].Id == slot);

        var bass = new Track { Name = "Bajo" };
        bass.Channel.Volume = 0.3;
        p.Tracks.Add(bass);
        MixCompare.Switch(p);
        Check("una pista nueva queda igual en las dos", bass.Channel.Volume == 0.3 && p.Tracks.Count == 2);

        // A has a send to the reverb; the reverb goes, and coming back to A drops that send instead of bringing the bus back
        ProjectBuses.SetSend(p, guitar.Channel, reverb, level: 0.4, on: true);
        MixCompare.Switch(p);
        p.Settings.Buses.RemoveAll(b => b.Id == reverb);
        MixCompare.Switch(p);
        Check("un bus borrado no vuelve y su envío se cae",
            p.Settings.Buses.All(b => b.Id != reverb) && guitar.Channel.Sends.All(s => s.BusId != reverb));

        string json = JsonSerializer.Serialize(p, ProjectStore.Json);
        var back = ProjectStore.Parse(json);
        Check("se guarda y se lee", back.Settings.Compare?.Active == p.Settings.Compare?.Active && MixCompare.HasOther(back));

        var undo = new UndoManager();
        string before = MixCompare.Active(p);
        double vol = guitar.Channel.Volume;
        undo.Checkpoint(p);
        MixCompare.Switch(p);
        undo.Undo(p);
        Check("deshacer vuelve a la mezcla de antes", MixCompare.Active(p) == before && p.Tracks[0].Channel.Volume == vol);

        MixCompare.Forget(p);
        Check("olvidar deja una sola mezcla, la A", MixCompare.Active(p) == "a" && !MixCompare.HasOther(p) && p.Tracks[0].Channel.Volume == vol);

        Console.WriteLine(failed == 0 ? "Todo bien." : $"{failed} comprobaciones fallaron.");
        return failed == 0 ? 0 : 3;
    }
}
