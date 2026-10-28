namespace GuitarCam.Audio;

/// <summary>How long a video can be on a network. <c>Repeats</c>: past the limit the video is cut into several
/// (stories), so the mark comes back every <c>Sec</c>. The window shows <c>Label</c> through <c>Loc</c>
/// ("social.{id}"); here it is what gclab prints.</summary>
public sealed record SocialLength(string Id, string Label, double Sec, bool Repeats = false);

/// <summary>
/// The lengths the timeline can mark (native only, asked by the user): a line where each network's limit falls, so
/// while recording you see whether the song fits or will have to be cut into several stories. Counted from 0, like
/// the export. The networks change these from time to time: this list is the one place to update them (2026).
/// </summary>
public static class SocialLengths
{
    public static readonly IReadOnlyList<SocialLength> All =
    [
        new("story", "Historias de Instagram, Facebook y WhatsApp", 60, Repeats: true),
        new("x", "X (Twitter)", 140),
        new("reels", "Instagram Reels · YouTube Shorts", 180),
        new("tiktok", "TikTok", 600),
    ];

    public static SocialLength? Get(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The ids kept in config.json ("socialMarks", comma separated), unknown ones left out, in the list's order.</summary>
    public static IReadOnlyList<SocialLength> Parse(string? ids)
    {
        var set = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. All.Where(s => set.Contains(s.Id))];
    }

    public static string Join(IEnumerable<SocialLength> marks) => string.Join(",", marks.Select(s => s.Id));

    /// <summary>How many pieces a video this long takes there: 1 when it fits, 0 when it does not and cannot be cut.</summary>
    public static int Pieces(SocialLength s, double lengthSec) =>
        lengthSec <= s.Sec + 1e-6 ? 1 : s.Repeats ? (int)Math.Ceiling(lengthSec / s.Sec - 1e-6) : 0;

    /// <summary>mm:ss, the way a network writes its limit (2:20, 10:00).</summary>
    public static string Clock(double sec)
    {
        int s = (int)Math.Round(sec);
        return $"{s / 60}:{s % 60:00}";
    }
}
