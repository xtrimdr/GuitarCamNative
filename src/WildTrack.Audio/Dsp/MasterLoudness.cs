namespace WildTrack.Audio.Dsp;

/// <summary>Where the mix is going and the integrated loudness that platform plays it at (null = as it is).</summary>
public sealed record LoudnessTarget(string Id, string Label, double? Lufs);

/// <summary>
/// The master's Destino and what the gain into the limiter does, the same in playback (<see cref="MixGraphBus"/>) and in
/// the export (<see cref="MixMaster"/>). With a target the gain is not a knob: <see cref="MixMaster.Render"/> measures
/// the whole mix (in the background while you mix, again when exporting) and finds the gain that lands it on the
/// target; the master keeps it (<see cref="MasterParams.TargetGainDb"/>) and playback applies it like the Empuje, so
/// what you hear mixing is what the export gives. The export starts from that gain and only moves it if the mix
/// changed, so with nothing changed it lands on the same gain. With a high gain the soft clip shares the peaks with
/// the limiter on its own (<see cref="SoftClipper"/>'s Auto).
/// </summary>
public static class MasterLoudness
{
    public const string AsIs = "";

    /// <summary>The Destinos. The window shows <c>Label</c> through <c>Loc</c> ("master.target.{id}").</summary>
    public static readonly IReadOnlyList<LoudnessTarget> All =
    [
        new(AsIs, "Como está", null),
        new("youtube", "YouTube", -14),
        new("instagram", "Instagram", -14),
        new("tiktok", "TikTok", -14),
        new("spotify", "Spotify", -14),
        new("apple", "Apple Music", -16),
    ];

    public static LoudnessTarget Get(string? id) => All.FirstOrDefault(t => t.Id == (id ?? AsIs)) ?? All[0];

    /// <summary>The gain into the clip and the limiter (dB): the target's measured one, else the Empuje (none with the limiter off).</summary>
    public static double GainDb(MasterParams p)
    {
        if (p.TargetLufs != null) return Math.Clamp(double.IsFinite(p.TargetGainDb) ? p.TargetGainDb : 0, -MixMaster.MaxGainDb, MixMaster.MaxGainDb);
        return p.LimOn ? Math.Clamp(double.IsFinite(p.LimDriveDb) ? p.LimDriveDb : 0, 0, MasterParams.DriveMaxDb) : 0;
    }
}
