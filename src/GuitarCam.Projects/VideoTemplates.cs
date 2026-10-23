using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitarCam.Projects;

/// <summary>A saved look: filter, adjustments and effects, without anything of the project it came from.</summary>
public sealed class VideoTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ColorGrade Color { get; set; } = new();
    public VideoFx Fx { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>True when the project's color and effects are exactly this template's (the web compares the JSON).</summary>
    public bool Matches(ColorGrade color, VideoFx fx) => VideoTemplates.Key(Color, Fx) == VideoTemplates.Key(color, fx);
}

/// <summary>
/// Video templates, program-wide like the theme. Port of TemplatesSection in GuitarCam/src/components/ColorPanel.tsx:
/// same name (any case) updates that template, the list keeps its order. Stored in
/// %APPDATA%\GuitarCam Studio\plantillas-video.json instead of localStorage (gc.videoTemplates).
/// </summary>
public static class VideoTemplates
{
    /// <summary>The web's input maxLength.</summary>
    public const int MaxName = 40;

    static string FilePath => Path.Combine(AppConfig.Dir, "plantillas-video.json");

    internal static string Key(ColorGrade c, VideoFx fx)
    {
        // Extra stays out: unknown fields don't make a template look different.
        var cc = ProjectLibrary.Clone(c); cc.Extra = null; cc.Normalize();
        var ff = ProjectLibrary.Clone(fx); ff.Extra = null; ff.Normalize();
        return JsonSerializer.Serialize(new { cc, ff }, ProjectStore.Json);
    }

    public static List<VideoTemplate> List()
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<VideoTemplate>>(File.ReadAllText(FilePath, Encoding.UTF8), ProjectStore.Json) ?? [];
            list.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Name));
            foreach (var t in list)
            {
                if (string.IsNullOrEmpty(t.Id)) t.Id = t.Name;
                (t.Color ??= new()).Normalize();
                (t.Fx ??= new()).Normalize();
            }
            return list;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static void Write(List<VideoTemplate> list) => AppConfig.WriteAtomic(FilePath, JsonSerializer.Serialize(list, ProjectStore.Json));

    /// <summary>Saves the current look under a name; an existing one with that name is replaced in place.</summary>
    public static List<VideoTemplate> Save(string name, ColorGrade color, VideoFx fx)
    {
        name = name.Trim();
        if (name.Length > MaxName) name = name[..MaxName];
        if (name.Length == 0) throw new ArgumentException("La plantilla necesita un nombre.");
        var list = List();
        int i = list.FindIndex(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        var tpl = new VideoTemplate { Id = i >= 0 ? list[i].Id : Ids.New("vtpl"), Name = name, Color = ProjectLibrary.Clone(color), Fx = ProjectLibrary.Clone(fx) };
        tpl.Color.Normalize(); tpl.Fx.Normalize();
        if (i >= 0) list[i] = tpl; else list.Add(tpl);
        Write(list);
        return list;
    }

    public static List<VideoTemplate> Delete(string id)
    {
        var list = List();
        list.RemoveAll(t => t.Id == id);
        Write(list);
        return list;
    }

    /// <summary>The template's look as fresh copies for a project (applyVideoTemplate).</summary>
    public static void ApplyTo(VideoTemplate t, ProjectSettings s)
    {
        s.Color = ProjectLibrary.Clone(t.Color); s.Color.Normalize();
        s.VideoFx = ProjectLibrary.Clone(t.Fx); s.VideoFx.Normalize();
    }
}
