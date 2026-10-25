using GuitarCam.Audio;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitarCam.Projects;

/// <summary>
/// Project templates: the tracks a new project starts with (empty, with their channel strip) plus project
/// settings. Port of GuitarCam/src/session/projectTemplates.ts: same factory templates; the user's are
/// program-wide (%APPDATA%\GuitarCam Studio\plantillas.json instead of localStorage).
/// </summary>
public sealed class ProjectTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Factory templates only (user templates get a summary instead).</summary>
    public string? Hint { get; set; }
    public bool Builtin { get; set; }
    public List<TemplateTrack> Tracks { get; set; } = [];
    public TemplateSettings Settings { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The tracks as empty lanes and the settings that are set; the rest stays at its default.</summary>
    public void ApplyTo(Project p)
    {
        var s = Settings;
        if (s.Layout != null) p.Settings.Layout = s.Layout;
        if (s.Aspect != null) p.Settings.Aspect = s.Aspect;
        if (s.Bpm is { } bpm) p.Settings.Bpm = bpm;
        if (s.Reverb != null) p.Settings.Reverb = ProjectLibrary.Clone(s.Reverb);
        if (s.Delay != null) p.Settings.Delay = ProjectLibrary.Clone(s.Delay);
        if (s.Color != null) p.Settings.Color = ProjectLibrary.Clone(s.Color);
        if (s.VideoFx != null) p.Settings.VideoFx = ProjectLibrary.Clone(s.VideoFx);
        foreach (var t in Tracks) p.Tracks.Add(new Track { Name = t.Name, Color = t.Color, Icon = t.Icon, Channel = ProjectLibrary.Clone(t.Channel) });
        p.Normalize();
    }

    static readonly Dictionary<string, string> AspectLabel = new() { ["16:9"] = "16:9", ["9:16"] = Texts.T("tpl.aspect.vertical", "9:16 vertical"), ["1:1"] = "1:1" };

    /// <summary>One line: tracks, then format and tempo.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Tracks.Count > 0 ? string.Join(", ", Tracks.Select(t => t.Name)) : Texts.T("tpl.noTracks", "Sin pistas") };
            if (Settings.Aspect is { } a) parts.Add(AspectLabel.GetValueOrDefault(a, a));
            if (Settings.Bpm is { } bpm) parts.Add($"{Math.Round(bpm * 100) / 100:0.##} BPM");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>An empty lane's name and channel, independent of the project it was saved from.</summary>
public sealed class TemplateTrack
{
    public string Name { get; set; } = "";
    /// <summary>The track's color and icon (<see cref="Track.Color"/>, <see cref="Track.Icon"/>).</summary>
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public Channel Channel { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Only what's set is applied to the new project.</summary>
public sealed class TemplateSettings
{
    public string? Layout { get; set; }
    public string? Aspect { get; set; }
    public double? Bpm { get; set; }
    public ReverbSettings? Reverb { get; set; }
    public DelaySettings? Delay { get; set; }
    public ColorGrade? Color { get; set; }
    public VideoFx? VideoFx { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Factory and user template storage, with the web's replacement and last-selection rules.</summary>
public static class ProjectTemplates
{
    /// <summary>"Vacío": no template.</summary>
    public const string None = "";

    static string FilePath => Path.Combine(AppConfig.Dir, "plantillas.json");

    /// <summary>Built-in track names by their Spanish name, to look up the translation.</summary>
    static readonly Dictionary<string, string> TrackIds = new() { ["Rítmica"] = "rhythm", ["Rítmica L"] = "rhythmL", ["Rítmica R"] = "rhythmR", ["Punteo"] = "lead", ["Arpegio"] = "arpeggio", ["Guitarra"] = "guitar" };

    /// <summary>The compressor's raw values get computed from the first take that fills the track.</summary>
    static TemplateTrack Track(string name, string preset, Action<Channel>? set = null, double amount = 0.5)
    {
        var ch = new Channel { Comp = new CompSettings { On = true, Preset = preset, Amount = amount } };
        set?.Invoke(ch);
        string[] known = ["Rítmica", "Rítmica L", "Rítmica R", "Punteo", "Arpegio", "Guitarra"];
        return new TemplateTrack { Name = Array.IndexOf(known, name) >= 0 ? Texts.T("tpl.track." + TrackIds[name], name) : name, Channel = ch };
    }

    static ReverbSettings Sala() => new() { Decay = 1.3, PreDelayMs = 15, Tone = 6000, LowCut = 150, Width = 1 };
    static ReverbSettings Placa() => new() { Decay = 1.8, PreDelayMs = 10, Tone = 9500, LowCut = 250, Width = 1 };
    static DelaySettings Punteada() => new() { Mode = "stereo", Sync = true, Division = "1/8.", Feedback = 0.35, Tone = 5000, LowCut = 250, Width = 1, Duck = 0.35 };

    public static readonly IReadOnlyList<ProjectTemplate> Builtin =
    [
        new()
        {
            Id = "builtin:rhythm-lead",
            Name = Texts.T("tpl.rhythmLead.name", "Rítmica + Punteo"),
            Hint = Texts.T("tpl.rhythmLead.hint", "Base al centro y punteo con reverb y delay"),
            Builtin = true,
            Tracks =
            [
                Track("Rítmica", "rhythm", c => c.ReverbSend = 0.12),
                Track("Punteo", "lead", c => { c.ReverbSend = 0.25; c.DelaySend = 0.2; }),
            ],
            Settings = new() { Layout = "side-by-side", Aspect = "16:9", Reverb = Sala(), Delay = Punteada() },
        },
        new()
        {
            Id = "builtin:double-rhythm-lead",
            Name = Texts.T("tpl.doubleRhythmLead.name", "Rítmica L/R + Punteo"),
            Hint = Texts.T("tpl.doubleRhythmLead.hint", "Dos rítmicas abiertas a los costados, punteo al centro"),
            Builtin = true,
            Tracks =
            [
                Track("Rítmica L", "rhythm", c => { c.Pan = -0.8; c.Volume = 0.85; c.ReverbSend = 0.1; }),
                Track("Rítmica R", "rhythm", c => { c.Pan = 0.8; c.Volume = 0.85; c.ReverbSend = 0.1; }),
                Track("Punteo", "lead", c => { c.ReverbSend = 0.25; c.DelaySend = 0.2; }),
            ],
            Settings = new() { Layout = "side-by-side", Aspect = "16:9", Reverb = Sala(), Delay = Punteada() },
        },
        new()
        {
            Id = "builtin:full",
            Name = Texts.T("tpl.full.name", "Rítmica L/R + Arpegio + Punteo"),
            Hint = Texts.T("tpl.full.hint", "Cuatro guitarras en grilla 2×2"),
            Builtin = true,
            Tracks =
            [
                Track("Rítmica L", "rhythm", c => { c.Pan = -0.8; c.Volume = 0.8; c.ReverbSend = 0.1; }),
                Track("Rítmica R", "rhythm", c => { c.Pan = 0.8; c.Volume = 0.8; c.ReverbSend = 0.1; }),
                Track("Arpegio", "arpeggio", c => { c.Pan = -0.3; c.Volume = 0.8; c.ReverbSend = 0.2; c.DelaySend = 0.1; }),
                Track("Punteo", "lead", c => { c.Pan = 0.15; c.ReverbSend = 0.25; c.DelaySend = 0.2; }),
            ],
            Settings = new() { Layout = "side-by-side", Aspect = "16:9", Reverb = Sala(), Delay = Punteada() },
        },
        new()
        {
            Id = "builtin:vertical",
            Name = Texts.T("tpl.vertical.name", "Rítmica + Punteo vertical"),
            Hint = Texts.T("tpl.vertical.hint", "9:16 una arriba de la otra, para Reels / Shorts / TikTok"),
            Builtin = true,
            Tracks =
            [
                Track("Rítmica", "rhythm", c => c.ReverbSend = 0.12),
                Track("Punteo", "lead", c => { c.ReverbSend = 0.25; c.DelaySend = 0.2; }),
            ],
            Settings = new() { Layout = "top-to-bottom", Aspect = "9:16", Reverb = Sala(), Delay = Punteada() },
        },
        new()
        {
            Id = "builtin:single",
            Name = Texts.T("tpl.single.name", "Canal único"),
            Hint = Texts.T("tpl.single.hint", "Una guitarra sola con un poco de placa"),
            Builtin = true,
            Tracks = [Track("Guitarra", "lead", c => c.ReverbSend = 0.2, amount: 0.4)],
            Settings = new() { Layout = "side-by-side", Aspect = "16:9", Reverb = Placa() },
        },
    ];

    public static List<ProjectTemplate> User()
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<ProjectTemplate>>(File.ReadAllText(FilePath, Encoding.UTF8), ProjectStore.Json) ?? [];
            list.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Name));
            foreach (var t in list)
            {
                t.Builtin = false;
                t.Hint = null;
                if (string.IsNullOrEmpty(t.Id)) t.Id = t.Name;
                t.Tracks ??= [];
                t.Tracks.RemoveAll(x => x == null);
                foreach (var x in t.Tracks) (x.Channel ??= new()).Normalize();
                t.Settings ??= new();
            }
            return list;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static void Write(List<ProjectTemplate> list)
    {
        AppConfig.WriteAtomic(FilePath, JsonSerializer.Serialize(list, ProjectStore.Json));
    }

    public static IEnumerable<ProjectTemplate> All() => Builtin.Concat(User());

    /// <summary>Saves a user template; one with the same name (any case) is replaced. Returns the new list.</summary>
    public static List<ProjectTemplate> SaveUser(ProjectTemplate t)
    {
        t.Name = ProjectLibrary.CleanName(t.Name);
        var list = User();
        var existing = list.Find(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
        t.Id = existing?.Id ?? Ids.New("tpl");
        t.Builtin = false;
        if (existing != null) list[list.IndexOf(existing)] = t;
        else list.Add(t);
        Write(list);
        return list;
    }

    public static List<ProjectTemplate> DeleteUser(string id)
    {
        var list = User();
        list.RemoveAll(t => t.Id == id);
        Write(list);
        return list;
    }

    public static string LastId
    {
        get => AppConfig.Get("lastProjectTemplate") ?? None;
        set => AppConfig.Set("lastProjectTemplate", value);
    }
}
