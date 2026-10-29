using System.Text.Json;
using System.Text.Json.Serialization;

namespace WildTrack.Projects;

/// <summary>
/// A mix kept aside for A/B (<see cref="MixCompare"/>): every track's channel, every folder's routing and sends and the
/// buses' settings, matched by id. Only settings: no media, no clips, so it costs a few kilobytes. The master is not
/// part of it: mastering is its own stage, with its own view.
/// </summary>
public sealed class MixSnapshot
{
    public List<TrackMix> Tracks { get; set; } = [];
    public List<FolderMix> Folders { get; set; } = [];
    public List<Bus> Buses { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A track's channel in a <see cref="MixSnapshot"/>.</summary>
public sealed class TrackMix
{
    public string TrackId { get; set; } = "";
    public Channel Channel { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A folder's output and sends in a <see cref="MixSnapshot"/> (a folder has nothing else that sounds).</summary>
public sealed class FolderMix
{
    public string FolderId { get; set; } = "";
    public string? Output { get; set; }
    public List<Send> Sends { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The A/B of the mix as the project keeps it: which one is sounding, and the other one (null = not made yet).</summary>
public sealed class MixCompareState
{
    public const string A = "a", B = "b";
    public string Active { get; set; } = A;
    public MixSnapshot? Other { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public string Inactive => Active == A ? B : A;

    internal void Normalize()
    {
        if (Active is not (A or B)) Active = A;
        if (Other != null)
        {
            Other.Tracks = [.. (Other.Tracks ?? []).Where(t => t?.Channel != null && !string.IsNullOrEmpty(t.TrackId))];
            Other.Folders = [.. (Other.Folders ?? []).Where(f => f != null && !string.IsNullOrEmpty(f.FolderId))];
            foreach (var f in Other.Folders) f.Sends ??= [];
            Other.Buses = [.. (Other.Buses ?? []).Where(b => b != null)];
        }
    }
}

/// <summary>
/// Comparing two mixes (native only), like a plugin's A/B but for the whole mix: the project's mix is the one sounding
/// (<see cref="MixCompareState.Active"/>) and the other waits in <see cref="ProjectSettings.Compare"/>. Switching swaps
/// them; the first time, the other starts as a copy of this one, so you change one and compare. It never adds or
/// removes tracks, folders or buses: what came after the other was kept keeps its settings in both. Mute and solo stay
/// as they are (they are for listening, not part of a mix), and so do names, sides, the bus list itself and the
/// master. Applied as one edit, and undo keeps <see cref="ProjectSettings.Compare"/>, so it brings the switch back.
/// </summary>
public static class MixCompare
{
    /// <summary>The project's mix now, as a copy.</summary>
    public static MixSnapshot Capture(Project p) => new()
    {
        Tracks = [.. p.Tracks.Select(t => new TrackMix { TrackId = t.Id, Channel = ProjectLibrary.Clone(t.Channel) })],
        Folders = [.. p.Folders.Where(f => !f.Separator).Select(f => new FolderMix { FolderId = f.Id, Output = f.Output, Sends = ProjectLibrary.Clone(f.Sends) })],
        Buses = ProjectLibrary.Clone(p.Settings.Buses),
    };

    /// <summary>Puts <paramref name="s"/> on the project, by id (see the class); then normalizes, so a send to a bus that is gone drops.</summary>
    public static void Apply(Project p, MixSnapshot s)
    {
        foreach (var tm in s.Tracks)
        {
            if (p.Tracks.Find(t => t.Id == tm.TrackId) is not { } t) continue;
            var ch = ProjectLibrary.Clone(tm.Channel);
            ch.Muted = t.Channel.Muted;
            ch.Solo = t.Channel.Solo;
            t.Channel = ch;
        }
        foreach (var fm in s.Folders)
        {
            if (p.Folders.Find(f => f.Id == fm.FolderId && !f.Separator) is not { } f) continue;
            f.Output = fm.Output;
            f.Sends = ProjectLibrary.Clone(fm.Sends);
        }
        foreach (var sb in s.Buses)
        {
            if (p.Settings.Buses.Find(b => b.Id == sb.Id) is not { } b) continue;
            b.Volume = sb.Volume;
            b.Pan = sb.Pan;
            b.Inserts = ProjectLibrary.Clone(sb.Inserts);
            b.Output = sb.Output;
            b.Sends = ProjectLibrary.Clone(sb.Sends);
        }
        p.Normalize();
    }

    /// <summary>Switches to the other mix (made from this one the first time). Returns the one sounding now, "a" or "b".</summary>
    public static string Switch(Project p)
    {
        var state = p.Settings.Compare ??= new();
        var now = Capture(p);
        if (state.Other != null) Apply(p, state.Other);
        state.Other = now;
        state.Active = state.Inactive;
        return state.Active;
    }

    /// <summary>Copies the mix sounding now over the other one ("Copiar A en B").</summary>
    public static void CopyToOther(Project p) => (p.Settings.Compare ??= new()).Other = Capture(p);

    /// <summary>Forgets the other mix: the project goes back to having one, called A.</summary>
    public static void Forget(Project p) => p.Settings.Compare = null;

    /// <summary>Which one is sounding ("a" when there is no comparison yet).</summary>
    public static string Active(Project p) => p.Settings.Compare?.Active ?? MixCompareState.A;

    /// <summary>Whether there is another mix to switch to (else switching makes it).</summary>
    public static bool HasOther(Project p) => p.Settings.Compare?.Other != null;
}
