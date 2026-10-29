using WildTrack.Audio;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WildTrack.Projects;

/// <summary>
/// A track saved to start new ones from (native only, asked by the user): a sound found for a guitar, with its whole
/// channel strip, color, icon and input. Unlike a <see cref="ProjectTemplate"/> it is one track, added to the open
/// project from the "nueva pista" menu.
/// </summary>
public sealed class TrackPreset
{
    public string Id { get; set; } = Ids.New("tps");
    public string Name { get; set; } = "";
    /// <summary>Whether the new track gets a panel in the video.</summary>
    public bool Video { get; set; } = true;
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public string? Input { get; set; }
    public Channel Channel { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>One line for the menu: what the strip has on.</summary>
    public string Summary
    {
        get
        {
            var c = Channel;
            var parts = new List<string>();
            if (c.Sat.On) parts.Add(Texts.T("tps.life", "Vida") + $" {c.Sat.Amount * 100:0}%");
            if (c.Comp.On) parts.Add(Texts.T("tps.comp", "Comp"));
            if (c.Eq.On) parts.Add("EQ");
            int sends = c.Sends.Count(s => s.On && s.Level > 0);
            if (sends > 0) parts.Add(Texts.T("tps.sends", "Envíos: {0}", sends));
            return parts.Count > 0 ? string.Join(" · ", parts) : Texts.T("tps.flat", "Sin efectos");
        }
    }
}

/// <summary>The user's track presets, program-wide in %APPDATA%\WildTrack\pistas.json, written atomically.</summary>
public static class TrackPresets
{
    static string FilePath => Path.Combine(AppConfig.Dir, "pistas.json");

    public static List<TrackPreset> All()
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<TrackPreset>>(File.ReadAllText(FilePath, Encoding.UTF8), ProjectStore.Json) ?? [];
            list.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Name));
            foreach (var t in list)
            {
                if (string.IsNullOrEmpty(t.Id)) t.Id = Ids.New("tps");
                (t.Channel ??= new()).Normalize();
                t.Color = Track.NormalizeColor(t.Color);
                t.Input = InputRoute.Parse(t.Input)?.Key;
            }
            return list;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The track as a preset: its strip with mute and solo off, its look and input, without clips.</summary>
    public static TrackPreset From(Track t, string name)
    {
        var ch = ProjectLibrary.Clone(t.Channel);
        ch.Muted = ch.Solo = false;
        return new TrackPreset { Name = name, Video = t.Video, Color = t.Color, Icon = t.Icon, Input = t.Input, Channel = ch };
    }

    /// <summary>Saves a preset; one with the same name (any case) is replaced. Returns the new list.</summary>
    public static List<TrackPreset> Save(TrackPreset preset)
    {
        preset.Name = ProjectLibrary.CleanName(preset.Name);
        if (preset.Name.Length == 0) throw new ArgumentException(Texts.T("err.preset.name", "Falta el nombre."));
        var list = All();
        var existing = list.Find(x => string.Equals(x.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            preset.Id = existing.Id;
            list[list.IndexOf(existing)] = preset;
        }
        else list.Add(preset);
        AppConfig.WriteAtomic(FilePath, JsonSerializer.Serialize(list, ProjectStore.Json));
        return list;
    }

    public static List<TrackPreset> Delete(string id)
    {
        var list = All();
        list.RemoveAll(t => t.Id == id);
        AppConfig.WriteAtomic(FilePath, JsonSerializer.Serialize(list, ProjectStore.Json));
        return list;
    }

    /// <summary>
    /// Puts the preset's sound on a track that already exists: its effect slots (with new ids, so the engine starts
    /// them fresh instead of mixing them up with the old ones), fader, pan and its send levels to the buses this project
    /// also has (a bus it doesn't have is skipped; a send the preset has none for stays as it was). What belongs to the
    /// track and not to the sound stays: takes, name, color, icon, input, output, mute, solo and Transponer. The
    /// caller measures the compressors again against the track's takes. Returns how many sends it skipped.
    /// </summary>
    public static int ApplyTo(Project p, Track t, TrackPreset preset)
    {
        var src = ProjectLibrary.Clone(preset.Channel);
        src.Normalize();
        var ch = t.Channel;
        var slots = src.Slots;
        foreach (var s in slots) s.Id = Ids.New("fx");
        ch.Inserts = slots;
        ch.Volume = src.Volume;
        ch.Pan = src.Pan;
        int skipped = 0;
        foreach (var s in src.Sends)
        {
            if (ProjectBuses.SetSend(p, ch, s.BusId, s.Level, s.On, s.Pre) is { } mine) mine.Hidden = s.Hidden;
            else skipped++;
        }
        t.Normalize();
        return skipped;
    }

    /// <summary>
    /// Adds an empty track from the preset at the end (in solo audio projects without a panel); its compressor's raw
    /// values get computed again from the first take, like a template's. Returns its id.
    /// </summary>
    public static string AddTo(Project p, TrackPreset preset, bool audioOnly = false)
    {
        var used = p.Tracks.Select(t => t.Name).ToHashSet();
        string name = preset.Name;
        for (int n = 2; used.Contains(name); n++) name = $"{preset.Name} {n}";
        var t = new Track
        {
            Name = name, Video = preset.Video && !audioOnly, Color = preset.Color, Icon = preset.Icon, Input = preset.Input,
            Channel = ProjectLibrary.Clone(preset.Channel),
        };
        t.Normalize();
        p.Tracks.Add(t);
        TrackFolders.Normalize(p);
        return t.Id;
    }
}
