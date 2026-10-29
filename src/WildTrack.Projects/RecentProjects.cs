using System.Text;
using System.Text.Json;

namespace WildTrack.Projects;

/// <summary>Recently opened native projects, in %APPDATA%\WildTrack\recientes.json.</summary>
public static class RecentProjects
{
    public sealed record Entry(string Dir, string Name, DateTime OpenedAt);

    const int Max = 10;

    static string FilePath => Path.Combine(AppConfig.Dir, "recientes.json");

    public static List<Entry> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath, Encoding.UTF8), ProjectStore.Json) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string? MostRecent() => Load().FirstOrDefault(e => ProjectStore.IsProject(e.Dir))?.Dir;

    public static void Touch(string dir, string name)
    {
        var list = Load();
        list.RemoveAll(e => ProjectLibrary.SamePath(e.Dir, dir));
        list.Insert(0, new Entry(Path.GetFullPath(dir), name, DateTime.UtcNow));
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        Write(list);
    }

    public static void Forget(string dir)
    {
        var list = Load();
        if (list.RemoveAll(e => ProjectLibrary.SamePath(e.Dir, dir)) > 0) Write(list);
    }

    static void Write(List<Entry> list)
    {
        try
        {
            AppConfig.WriteAtomic(FilePath, JsonSerializer.Serialize(list, ProjectStore.Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the list is a convenience: never fail an open over it
        }
    }
}
