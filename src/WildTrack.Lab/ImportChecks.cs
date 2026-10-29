using WildTrack.Projects;
using NAudio.Wave;

namespace WildTrack.Lab;

/// <summary>Regression cases for importing outside audio into a project (<see cref="AudioImport"/>).</summary>
static class ImportChecks
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Importar audio: {text}");
            n++;
        }
        string tmp = Path.Combine(Path.GetTempPath(), "gc-import-" + Guid.NewGuid().ToString("N")[..8]);
        string proj = Path.Combine(tmp, "proyecto");
        Directory.CreateDirectory(proj);
        try
        {
            string src = Path.Combine(tmp, "Base - tema (v2).wav");
            using (var w = new WaveFileWriter(src, new WaveFormat(44100, 16, 1)))
                w.Write(new byte[44100 * 2 * 3], 0, 44100 * 2 * 3);

            var a = AudioImport.CopyIn(proj, src);
            Check(a.DurationSec is > 2.99 and < 3.01 && a.SampleRate == 44100 && a.Channels == 1, "lee la duración y el formato");
            Check(File.Exists(Path.Combine(proj, a.RelativePath)) && File.Exists(src), "copia adentro y deja el original");
            var b = AudioImport.CopyIn(proj, src);
            Check(a.RelativePath != b.RelativePath && File.Exists(Path.Combine(proj, b.RelativePath)), "el mismo archivo dos veces no se pisa");
            Check(!Directory.GetFiles(Path.Combine(proj, "media")).Any(f => f.EndsWith(".importando")), "no deja temporales");

            var p = new Project();
            var empty = new Track { Name = "Pista 1" };
            p.Tracks.Add(empty);
            var t = AudioImport.AddClip(p, a, 1.2344, empty.Id);
            Check(t == empty && t.Clips.Count == 1 && t.Clips[0].AtSec == 1.234, "cae en la pista vacía elegida, en su tiempo");
            Check(!t.Video && t.Name == a.Name && !t.Channel.Comp.On && !t.Channel.Sat.On, "pista de audio, sin compresor ni saturación");
            Check(p.FindMedia(t.Clips[0].MediaId) is { Kind: "import", SyncMs: 0 }, "el medio queda como importado y sin sincronía");

            var t2 = AudioImport.AddClip(p, b, -5, empty.Id);
            Check(t2 != empty && p.Tracks.Count == 2 && t2.Clips[0].AtSec == 0, "con la pista ocupada crea una nueva; nunca antes de 0");
            Check(p.Log.Last().Type == "audio-imported", "queda en la actividad");

            string bad = Path.Combine(tmp, "roto.mp3");
            File.WriteAllBytes(bad, [1, 2, 3, 4, 5, 6, 7, 8]);
            int before = Directory.GetFiles(Path.Combine(proj, "media")).Length;
            bool failed = false;
            try { AudioImport.CopyIn(proj, bad); } catch (InvalidDataException) { failed = true; }
            Check(failed && Directory.GetFiles(Path.Combine(proj, "media")).Length == before, "un archivo roto avisa y no deja nada");
            File.WriteAllText(Path.Combine(tmp, "nota.txt"), "x");
            failed = false;
            try { AudioImport.CopyIn(proj, Path.Combine(tmp, "nota.txt")); } catch (InvalidDataException) { failed = true; }
            Check(failed, "una extensión que no es de audio avisa");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
        Console.WriteLine($"Importar audio: {n} comprobaciones bien.");
        return 0;
    }
}
