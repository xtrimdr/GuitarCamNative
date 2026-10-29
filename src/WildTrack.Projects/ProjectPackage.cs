using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WildTrack.Audio;

namespace WildTrack.Projects;

/// <summary>What goes in a project's ZIP (<see cref="ProjectLibrary.Pack"/>).</summary>
public enum PackageKind
{
    /// <summary>To send or move: the project and the takes its clips play, nothing else.</summary>
    Share,
    /// <summary>A whole backup: every take (also the ones no clip plays), the saved versions and the exports.</summary>
    Backup,
}

/// <summary>What <see cref="ProjectLibrary.Pack"/> wrote: how many files, how big, and the takes it couldn't find.</summary>
public sealed record PackResult(string File, int Files, long Bytes, int Missing);

/// <summary>
/// The project as one ZIP to move it to another computer or keep a backup, and back (the manager's "Paquete").
/// Takes left outside the folder (imported without copying) go inside, in media/, and the packed guitarcam.json
/// points to them there, so the package opens anywhere. The lock and cache/ never go: one is this computer's, the
/// other is remade from media/. Both ways write somewhere temporary first and publish at the end (the ZIP's
/// <c>.part</c> renamed, the unpacked folder moved), so a crash halfway never leaves a package or a project that
/// looks whole and isn't. The project being packed is read from disk: the window saves it first.
/// </summary>
public static partial class ProjectLibrary
{
    /// <summary>Where the takes that lived outside the project go inside the package.</summary>
    const string OutsideDir = "media/de-afuera";
    const string PartExt = ".part";

    /// <summary>The file name a package of <paramref name="name"/> gets (the manager suggests it).</summary>
    public static string PackageFileName(string name, PackageKind kind) =>
        FolderNameFor(name) + (kind == PackageKind.Backup ? $" (respaldo {DateTime.Now:yyyy-MM-dd})" : "") + ".zip";

    public static PackResult Pack(string dir, string zipPath, PackageKind kind, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        dir = Path.GetFullPath(dir);
        var project = ProjectStore.Parse(File.ReadAllText(Path.Combine(dir, ProjectStore.FileName), Encoding.UTF8));
        // entry name -> file on disk; the folder's own files keep their place
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outside = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int missing = 0;

        string? Inside(string full)
        {
            string rel = Path.GetRelativePath(dir, full);
            return rel.StartsWith("..") || Path.IsPathRooted(rel) ? null : rel.Replace('\\', '/');
        }
        string? Take(string? path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string full = Path.GetFullPath(Resolve(dir, path));
            if (!File.Exists(full))
            {
                missing++;
                return path;
            }
            if (Inside(full) is { } rel)
            {
                entries[rel] = full;
                return rel;
            }
            if (outside.TryGetValue(full, out var known)) return known;
            string stem = Path.GetFileNameWithoutExtension(full), ext = Path.GetExtension(full), name = $"{OutsideDir}/{stem}{ext}";
            for (int i = 2; taken.Contains(name) || File.Exists(Path.Combine(dir, name)); i++) name = $"{OutsideDir}/{stem} ({i}){ext}";
            taken.Add(name);
            outside[full] = name;
            entries[name] = full;
            return name;
        }

        if (kind == PackageKind.Share)
        {
            var used = project.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaId).ToHashSet();
            project.Media.RemoveAll(m => !used.Contains(m.Id));
            project.Exports = [];
            project.Settings.Export.Dir = null;
        }
        foreach (var m in project.Media)
        {
            m.Audio = Take(m.Audio);
            m.Video = Take(m.Video);
        }
        if (kind == PackageKind.Backup)
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Inside(f)!;
                if (rel.StartsWith(ProjectStore.CacheDir + "/", StringComparison.OrdinalIgnoreCase) || rel.Equals(ProjectStore.LockName, StringComparison.OrdinalIgnoreCase)
                    || rel.Equals(ProjectStore.FileName, StringComparison.OrdinalIgnoreCase) || rel.EndsWith(PartExt, StringComparison.OrdinalIgnoreCase)) continue;
                entries.TryAdd(rel, f);
            }
        entries.Remove(ProjectStore.FileName);
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(project, ProjectStore.Json));

        long total = json.Length + entries.Values.Sum(f => new FileInfo(f).Length), done = 0;
        string part = zipPath + PartExt;
        try
        {
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var s = zip.CreateEntry(ProjectStore.FileName, CompressionLevel.Optimal).Open()) s.Write(json);
                    done += json.Length;
                    var buffer = new byte[1 << 20];
                    foreach (var (name, file) in entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry(name, LevelFor(name));
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                        // takes may be open for playing: read them shared
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = entry.Open();
                        int n;
                        while ((n = src.Read(buffer)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            dst.Write(buffer, 0, n);
                            done += n;
                            progress?.Report(total > 0 ? (double)done / total : 1);
                        }
                    }
                }
                fs.Flush(true);
            }
            File.Move(part, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        progress?.Report(1);
        return new PackResult(zipPath, entries.Count + 1, new FileInfo(zipPath).Length, missing);
    }

    /// <summary>Audio and video that is already compressed is stored as it is: deflating it only costs time.</summary>
    static CompressionLevel LevelFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".flac" or ".mp4" or ".mov" or ".webm" or ".mkv" or ".jpg" or ".jpeg" or ".png" or ".zip" => CompressionLevel.NoCompression,
        ".json" or ".txt" or ".log" => CompressionLevel.Optimal,
        _ => CompressionLevel.Fastest,
    };

    /// <summary>
    /// Unpacks a package into a new project of the projects folder (with " (2)" if the name is taken) and returns its
    /// folder. Accepts the project at the ZIP's root or in a single folder (a ZIP made by hand from the folder). Entries
    /// that would land outside the new folder are refused, never written.
    /// </summary>
    public static string Unpack(string zipPath, string? root = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var main = files.Where(e => e.Name.Equals(ProjectStore.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault()
            ?? throw new InvalidDataException(Texts.T("store.pack.notProject", "Ese ZIP no tiene un proyecto de WildTrack adentro"));
        string prefix = main.FullName[..^main.Name.Length];
        Project project;
        using (var r = new StreamReader(main.Open(), Encoding.UTF8)) project = ProjectStore.Parse(r.ReadToEnd());

        root ??= Root;
        Directory.CreateDirectory(root);
        var (dir, name) = UniqueProject(root, CleanName(project.Name) is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(zipPath));
        string staging = Path.GetFullPath(Path.Combine(root, $".desempaquetando-{Guid.NewGuid():N}"));
        try
        {
            Directory.CreateDirectory(staging);
            var mine = files.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            long total = Math.Max(1, mine.Sum(e => e.Length)), done = 0;
            foreach (var e in mine)
            {
                ct.ThrowIfCancellationRequested();
                string rel = e.FullName[prefix.Length..];
                if (rel.Equals(ProjectStore.LockName, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(ProjectStore.CacheDir + "/", StringComparison.OrdinalIgnoreCase)) continue;
                string to = Path.GetFullPath(Path.Combine(staging, rel));
                if (!to.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Texts.T("store.pack.badEntry", "El ZIP tiene un archivo que saldría de la carpeta del proyecto: {0}", e.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                e.ExtractToFile(to);
                done += e.Length;
                progress?.Report((double)done / total);
            }
            // the new copy is its own project: its name may have changed, and its history says where it came from
            var store = ProjectStore.Open(staging);
            using (store)
            {
                if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
                store.Project.Name = name;
                store.Project.AddLog("session-unpacked", Path.GetFileName(zipPath));
                store.Save();
            }
            Directory.CreateDirectory(Path.Combine(staging, "exports"));
            Directory.Move(staging, dir);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        RecentProjects.Touch(dir, name);
        progress?.Report(1);
        return dir;
    }
}
