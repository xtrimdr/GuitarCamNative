namespace WildTrack.Audio;

/// <summary>Where an export is going: the frame it needs. The window shows <c>Label</c> through <c>Loc</c>
/// ("export.target.{id}"); here it is what wtlab prints.</summary>
public sealed record ExportTarget(string Id, string Label, string? Aspect);

/// <summary>
/// The social presets of the export (native only). "project" is the default and changes nothing: the project's own
/// frame. The others swap the frame for that video only (the project keeps its own). The loudness is not here: it is
/// the master's Destino (<see cref="Dsp.MasterLoudness"/>), so it is heard while mixing and the export just follows it.
/// </summary>
public static class ExportTargets
{
    public const string Project = "project";
    /// <summary>The loudness streaming platforms normalise to (wtlab's --lufs without a value).</summary>
    public const double StreamingLufs = -14;

    public static readonly IReadOnlyList<ExportTarget> All =
    [
        new(Project, "Proyecto", null),
        new("reels", "Reels · Shorts · TikTok", "9:16"),
        new("youtube", "YouTube", "16:9"),
        new("ig-square", "Instagram 1:1", "1:1"),
        new("ig-portrait", "Instagram 4:5", "4:5"),
    ];

    public static ExportTarget Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    /// <summary>The frame the export uses: the preset's, or the project's when the preset keeps it.</summary>
    public static string AspectFor(string? id, string projectAspect) => Get(id).Aspect ?? projectAspect;
}
