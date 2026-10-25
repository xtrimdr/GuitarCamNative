using GuitarCam.Audio.Dsp;

namespace GuitarCam.Projects;

/// <summary>
/// Turns a <see cref="Channel"/>'s insert slots into what the engine takes (<see cref="InsertSpec"/>). One place for
/// the live engine, the export and gclab, so the three hear the same chain. Slots of a kind this version doesn't know
/// are left out.
/// </summary>
public static class ChannelDsp
{
    /// <param name="rmsDb">The take's level, which the Vida drive is measured against (-24 without audio).</param>
    public static InsertSpec[] Specs(Channel ch, double rmsDb)
    {
        var slots = ch.Slots;
        var list = new List<InsertSpec>(slots.Count);
        foreach (var s in slots)
        {
            if (!s.Known) continue;
            s.Normalize();
            list.Add(s.Type switch
            {
                InsertSlot.SatKind => new InsertSpec(s.Id, s.Type) { Sat = Sat(s.Sat!, rmsDb) },
                InsertSlot.CompKind => new InsertSpec(s.Id, s.Type) { Comp = Comp(s.Comp!) },
                _ => new InsertSpec(s.Id, s.Type) { EqOn = s.Eq!.On, EqBands = EqBands(s.Eq) },
            });
        }
        return [.. list];
    }

    public static SatParams Sat(SatSettings s, double rmsDb) =>
        SatParams.From(s.On, s.Mode == "tape" ? SatMode.Tape : SatMode.Tube, s.Amount, rmsDb);

    public static CompState Comp(CompSettings c) => new(
        c.On,
        c.Preset,
        c.Amount,
        c.Limit,
        c.Threshold,
        c.Ratio,
        c.Attack,
        c.Release,
        c.Knee,
        c.Makeup,
        new CompEqParams(c.Eq.HpfHz, c.Eq.MudDb, c.Eq.PresenceDb, c.Eq.PresenceHz, c.Eq.AirDb));

    public static EqBandSpec[] EqBands(EqSettings eq)
    {
        var bands = new EqBandSpec[TrackEq.BandCount];
        for (int i = 0; i < TrackEq.BandCount; i++)
        {
            if (i < eq.Bands.Count)
            {
                var b = eq.Bands[i];
                bands[i] = EqBandSpec.FromStrings(b.On, b.Type, b.Freq, b.Gain, b.Q, b.Slope, TrackEq.DefaultBands[i].Type);
            }
            else bands[i] = TrackEq.DefaultBands[i];
        }
        return bands;
    }

    /// <summary>
    /// Writes a compressor preset's values (<see cref="CompPresets.UpdateComp"/>) back into <paramref name="c"/>, measured
    /// against the take's <paramref name="env"/>. Returns the estimated reduction (dB).
    /// </summary>
    public static double RecalculateComp(CompSettings c, CompPresets.TakeEnvelope env)
    {
        var state = CompPresets.UpdateComp(Comp(c), env);
        c.Threshold = state.Threshold;
        c.Ratio = state.Ratio;
        c.Attack = state.Attack;
        c.Release = state.Release;
        c.Knee = state.Knee;
        c.Makeup = state.Makeup;
        c.Eq.HpfHz = state.Eq.HpfHz;
        c.Eq.MudDb = state.Eq.MudDb;
        c.Eq.PresenceDb = state.Eq.PresenceDb;
        c.Eq.PresenceHz = state.Eq.PresenceHz;
        c.Eq.AirDb = state.Eq.AirDb;
        return state.EstGrDb ?? 0;
    }
}
