namespace GuitarCam.Audio;

/// <summary>Where an export is going: the frame it needs and the loudness the platform plays it at. The window shows
/// <c>Label</c> through <c>Loc</c> ("export.target.{id}"); here it is what gclab prints.</summary>
public sealed record ExportTarget(string Id, string Label, string? Aspect, double? Lufs);

/// <summary>
/// The social presets of the export (native only). "project" is the default and changes nothing: the project's own
/// frame and the mix as it is. The others swap the frame for that video only (the project keeps its own) and master
/// the mix to the integrated loudness the platform normalises to, so it is not turned down or sounds weaker than
/// the rest of the feed. All of them ask for about −14 LUFS today; each one keeps its own value in case one moves.
/// </summary>
public static class ExportTargets
{
    public const string Project = "project";
    /// <summary>The loudness streaming platforms normalise to, also used by "Exportar audio".</summary>
    public const double StreamingLufs = -14;

    public static readonly IReadOnlyList<ExportTarget> All =
    [
        new(Project, "Proyecto", null, null),
        new("reels", "Reels · Shorts · TikTok", "9:16", -14),
        new("youtube", "YouTube", "16:9", -14),
        new("ig-square", "Instagram 1:1", "1:1", -14),
        new("ig-portrait", "Instagram 4:5", "4:5", -14),
    ];

    public static ExportTarget Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    /// <summary>The frame the export uses: the preset's, or the project's when the preset keeps it.</summary>
    public static string AspectFor(string? id, string projectAspect) => Get(id).Aspect ?? projectAspect;
}
