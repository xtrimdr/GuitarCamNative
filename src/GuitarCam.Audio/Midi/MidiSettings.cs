using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitarCam.Audio.Midi;

/// <summary>
/// One MIDI input as the user set it up (Configuración → MIDI): kept by the device's name, because Windows numbers them
/// again whenever one is plugged in (two of the same model are "Name #2"). It is the machine's, not a project's.
/// </summary>
public sealed class MidiInputSettings
{
    /// <summary>The device's key (<see cref="MidiInputs.Devices"/>): its Windows name, "#2" for a second of the same.</summary>
    public string Name { get; set; } = "";
    /// <summary>The name the user gave it ("Teclado", "Pedalera"); null = its own.</summary>
    public string? Alias { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>The channel it listens to, 1..16; 0 = all (omni).</summary>
    public int Channel { get; set; }
    /// <summary>How the keys' velocity is taken (<see cref="VelocityCurves"/>).</summary>
    public string Velocity { get; set; } = VelocityCurves.Normal;
    /// <summary>The velocity every note gets with the "fixed" curve.</summary>
    public int FixedVelocity { get; set; } = 100;
    /// <summary>What it is used for (the user's call: boxes, not one type per device): notes, knobs, transport.</summary>
    public bool Notes { get; set; } = true;
    public bool Knobs { get; set; } = true;
    public bool Transport { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>What to call it: the user's name, or the device's.</summary>
    [JsonIgnore] public string Shown => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;

    internal void Normalize()
    {
        Name = Name?.Trim() ?? "";
        Alias = string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim();
        Channel = Math.Clamp(Channel, 0, 16);
        Velocity = VelocityCurves.Normalize(Velocity);
        FixedVelocity = Math.Clamp(FixedVelocity, 1, 127);
    }
}

/// <summary>
/// The MIDI side of the program's settings ("midi" in config.json): the inputs the user has seen, plugged in or not, and
/// whether a recording's notes go to the piano roll's magnet on their own. Read and written as JSON text, so the
/// window and gclab share it through <c>AppConfig</c> without this assembly knowing about it.
/// </summary>
public sealed class MidiSettings
{
    public List<MidiInputSettings> Inputs { get; set; } = [];
    /// <summary>Recorded notes are quantized to the piano roll's magnet when the take ends (off: by hand, later).</summary>
    public bool QuantizeOnRecord { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>From config.json's "midi" (null or broken = nothing set up yet).</summary>
    public static MidiSettings Parse(string? json)
    {
        MidiSettings s;
        try
        {
            s = string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<MidiSettings>(json, Json) ?? new();
        }
        catch (JsonException)
        {
            s = new();
        }
        s.Normalize();
        return s;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    void Normalize()
    {
        Inputs ??= [];
        Inputs.RemoveAll(i => i == null || string.IsNullOrWhiteSpace(i.Name));
        foreach (var i in Inputs) i.Normalize();
        // one entry per device
        Inputs = [.. Inputs.GroupBy(i => i.Name).Select(g => g.First())];
    }

    /// <summary>The device's settings, made (with the defaults) the first time it is seen.</summary>
    public MidiInputSettings For(string name)
    {
        if (Inputs.Find(i => i.Name == name) is { } found) return found;
        var made = new MidiInputSettings { Name = name };
        Inputs.Add(made);
        return made;
    }

    /// <summary>A device's settings if it has any, without adding it.</summary>
    public MidiInputSettings? Find(string? name) => name == null ? null : Inputs.Find(i => i.Name == name);
}

/// <summary>
/// How a key's velocity becomes the note's (asked for in the design: keyboards respond very differently). Soft lifts
/// light playing, hard needs more hand, fixed ignores it (pads, drum parts played on a keyboard).
/// </summary>
public static class VelocityCurves
{
    public const string Soft = "soft", Normal = "normal", Hard = "hard", Fixed = "fixed";
    public static readonly string[] All = [Soft, Normal, Hard, Fixed];

    public static string Normalize(string? id) => Array.IndexOf(All, id) >= 0 ? id! : Normal;

    /// <summary>The note's velocity (1..127) for a key's (1..127). Soft and hard are powers: 0.6 and 1.6.</summary>
    public static int Apply(string curve, int velocity, int fixedVelocity)
    {
        velocity = Math.Clamp(velocity, 1, 127);
        return curve switch
        {
            Soft => Math.Clamp((int)Math.Round(127 * Math.Pow(velocity / 127.0, 0.6)), 1, 127),
            Hard => Math.Clamp((int)Math.Round(127 * Math.Pow(velocity / 127.0, 1.6)), 1, 127),
            Fixed => Math.Clamp(fixedVelocity, 1, 127),
            _ => velocity,
        };
    }
}
