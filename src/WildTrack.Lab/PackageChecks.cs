using System.IO.Compression;
using WildTrack.Projects;

namespace WildTrack.Lab;

/// <summary>
/// wtlab proyecto verificar-paquete: a project in a temporary folder, with a take inside, one left outside (imported
/// without copying) and one no clip plays, packed both ways and unpacked into a separate projects folder. Checks what
/// each package carries, that the outside take comes in and the project opens pointing to it, and that a ZIP whose
/// entry climbs out of the folder is refused without writing anything.
/// </summary>
static class PackageChecks
{
    public static int Run()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {what}");
            if (!ok) failed++;
        }
        string tmp = Path.Combine(Path.GetTempPath(), $"wtlab-paquete-{Guid.NewGuid():N}");
        string src = Path.Combine(tmp, "Tema"), elsewhere = Path.Combine(tmp, "afuera"), root = Path.Combine(tmp, "proyectos");
        string? savedData = AppConfig.DirectoryOverride;
        AppConfig.DirectoryOverride = Path.Combine(tmp, "datos");
        try
        {
            Directory.CreateDirectory(Path.Combine(src, ProjectStore.MediaDir));
            Directory.CreateDirectory(elsewhere);
            File.WriteAllBytes(Path.Combine(src, "media", "toma1.wav"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(src, "media", "vieja.wav"), new byte[3000]);
            string outsideFile = Path.Combine(elsewhere, "base.wav");
            File.WriteAllBytes(outsideFile, new byte[4000]);
            var inside = new Media { Name = "toma1", Audio = "media/toma1.wav" };
            var outside = new Media { Name = "base", Kind = "import", Audio = outsideFile };
            var unused = new Media { Name = "vieja", Audio = "media/vieja.wav" };
            var p = new Project { Name = "Tema", Media = [inside, outside, unused] };
            p.Tracks.Add(new Track { Name = "Guitarra", Clips = [new Clip { MediaId = inside.Id }, new Clip { MediaId = outside.Id, AtSec = 10 }] });
            p.Exports.Add(new ExportRecord { File = "exports/video.mp4" });
            using (ProjectStore.Create(src, p)) { }
            Directory.CreateDirectory(Path.Combine(src, "exports"));
            File.WriteAllBytes(Path.Combine(src, "exports", "video.mp4"), new byte[2000]);
            Directory.CreateDirectory(Path.Combine(src, ProjectStore.CacheDir));
            File.WriteAllBytes(Path.Combine(src, ProjectStore.CacheDir, "transpuesta.wav"), new byte[1000]);

            string share = Path.Combine(tmp, "compartir.zip"), backup = Path.Combine(tmp, "respaldo.zip");
            var r1 = ProjectLibrary.Pack(src, share, PackageKind.Share);
            var r2 = ProjectLibrary.Pack(src, backup, PackageKind.Backup);
            List<string> Names(string zip)
            {
                using var z = ZipFile.OpenRead(zip);
                return [.. z.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
            }
            var sn = Names(share);
            var bn = Names(backup);
            Check("para compartir: el proyecto, la toma y la de afuera: " + string.Join(" ", sn),
                sn.SequenceEqual(["guitarcam.json", "media/de-afuera/base.wav", "media/toma1.wav"]) && r1.Missing == 0);
            Check("respaldo: además la toma sin usar y la exportación, sin cache ni lock",
                bn.Contains("media/vieja.wav") && bn.Contains("exports/video.mp4") && bn.Contains("media/de-afuera/base.wav")
                && !bn.Any(n => n.StartsWith("cache/") || n == ProjectStore.LockName) && r2.Files == bn.Count);
            Check("sin .part al terminar", !File.Exists(share + ".part") && !File.Exists(backup + ".part"));
            Check("el proyecto original no se tocó", File.ReadAllText(Path.Combine(src, ProjectStore.FileName)).Contains(outsideFile.Replace("\\", "\\\\")));

            string a = ProjectLibrary.Unpack(share, root), b = ProjectLibrary.Unpack(backup, root);
            Check("desempaquetar dos veces no pisa: " + Path.GetFileName(a) + " / " + Path.GetFileName(b),
                Path.GetFileName(a) == "Tema" && Path.GetFileName(b) == "Tema (2)");
            using (var s = ProjectStore.Open(a))
            {
                var m = s.Project.Media;
                Check("el desempaquetado abre con su toma de afuera adentro",
                    m.Count == 2 && m.All(x => !Path.IsPathRooted(x.Audio!) && File.Exists(s.Resolve(x.Audio))) && s.Project.Exports.Count == 0);
            }
            using (var s = ProjectStore.Open(b))
                Check("el respaldo trae las tres tomas y su exportación", s.Project.Media.Count == 3 && s.Project.Exports.Count == 1 && s.Project.Name == "Tema (2)");
            Check("sin carpetas a medio desempaquetar", !Directory.EnumerateDirectories(root, ".desempaquetando-*").Any());

            string evil = Path.Combine(tmp, "trampa.zip");
            using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry(ProjectStore.FileName).Open())) w.Write("""{"name":"Trampa"}""");
                using (var w = new StreamWriter(z.CreateEntry("../../fuera.txt").Open())) w.Write("no");
            }
            bool refused = false;
            try { ProjectLibrary.Unpack(evil, root); } catch (InvalidDataException) { refused = true; }
            Check("un ZIP que sale de la carpeta se rechaza sin escribir afuera",
                refused && !File.Exists(Path.Combine(tmp, "fuera.txt")) && !File.Exists(Path.Combine(Path.GetDirectoryName(tmp)!, "fuera.txt"))
                && !Directory.Exists(Path.Combine(root, "Trampa")) && !Directory.EnumerateDirectories(root, ".desempaquetando-*").Any());

            bool notProject = false;
            string plain = Path.Combine(tmp, "otra-cosa.zip");
            using (var z = ZipFile.Open(plain, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("hola.txt").Open())) w.Write("hola");
            try { ProjectLibrary.Unpack(plain, root); } catch (InvalidDataException) { notProject = true; }
            Check("un ZIP sin proyecto avisa", notProject);
        }
        finally
        {
            AppConfig.DirectoryOverride = savedData;
            try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(failed == 0 ? "Todo bien." : $"{failed} comprobaciones fallaron.");
        return failed == 0 ? 0 : 3;
    }
}
