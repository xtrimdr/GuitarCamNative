using GuitarCam.Audio.Dsp;

namespace GuitarCam.Projects;

/// <summary>
/// Turns a <see cref="Channel"/>'s insert slots into what the engine takes (<see cref="InsertSpec"/>). One place for
/// the live engine, the export and gclab, so the three hear the same chain. Slots of a kind this version doesn't know
/// are left out. Also a folder's bus (<see cref="FolderSpecs"/>, <see cref="Folders"/>), the extra returns
/// (<see cref="Returns"/>) with a track's sends to them (<see cref="Sends"/>), and the whole bus side of the export
/// (<see cref="Bus"/>).
/// </summary>
public static class ChannelDsp
{
    /// <summary>
    /// The level a folder bus's Vida is measured against: a bus has no take of its own, so it is the same as a track
    /// with no audio. Fixed, so the live engine, the export and gclab drive it the same.
    /// </summary>
    public const double BusRmsDb = -24;

    /// <param name="rmsDb">The take's level, which the Vida drive is measured against (-24 without audio).</param>
    public static InsertSpec[] Specs(Channel ch, double rmsDb) => Specs(ch.Slots, rmsDb);

    /// <summary>A folder bus's slots (empty: no bus).</summary>
    public static InsertSpec[] FolderSpecs(Folder f) => Specs(f.Inserts, BusRmsDb);

    /// <summary>Every folder as the export's bus, in <see cref="Project.Folders"/>' order (what <see cref="FolderIndex"/> points at).</summary>
    public static MixFolderSpec[] Folders(Project p) =>
        [.. p.Folders.Select(f => new MixFolderSpec { Name = f.Name, Inserts = FolderSpecs(f) })];

    /// <summary>The track's folder as an index into <see cref="Folders"/>, -1 = none (<see cref="MixTrackSpec.Folder"/>).</summary>
    public static int FolderIndex(Project p, Track t) => t.FolderId == null ? -1 : p.Folders.FindIndex(f => f.Id == t.FolderId);

    /// <summary>An extra return's slots; its delays follow <paramref name="bpm"/>.</summary>
    public static InsertSpec[] ReturnSpecs(ReturnBus r, double bpm) => Specs(r.Inserts, BusRmsDb, bpm);

    /// <summary>Every extra return as the export takes it, in <see cref="ProjectSettings.Returns"/>' order (what <see cref="Sends"/> point at).</summary>
    public static MixReturnSpec[] Returns(Project p) =>
        [.. p.Settings.Returns.Select(r => new MixReturnSpec { Name = r.Name, Volume = r.Volume, Muted = r.Muted, Inserts = ReturnSpecs(r, p.Settings.Bpm) })];

    /// <summary>A track's sends to the extra returns, as indexes into <see cref="Returns"/> (sends to a missing return are left out).</summary>
    public static MixSendSpec[] Sends(Project p, Channel ch)
    {
        var list = new List<MixSendSpec>(ch.Sends.Count);
        foreach (var s in ch.Sends)
        {
            int i = p.Settings.Returns.FindIndex(r => r.Id == s.ReturnId);
            if (i >= 0) list.Add(new MixSendSpec { Return = i, Level = s.Level, On = s.On, Pre = s.Pre });
        }
        return [.. list];
    }

    public static ReverbParams Reverb(ReverbSettings r) => new(r.Decay, r.PreDelayMs, r.Tone, r.LowCut, r.Width, r.ReturnGain, r.Muted);

    public static DelayParams Delay(DelaySettings d) =>
        new(DelayParams.ParseMode(d.Mode), d.Sync, d.Division, d.TimeMs, d.Feedback, d.Tone, d.LowCut, d.Width, d.Duck, d.ReturnGain, d.Muted);

    /// <summary>The bus side of the mix (the fixed returns, the extra ones, the folders and the master), for the export and gclab.</summary>
    public static MixBusSpec Bus(Project p) => new()
    {
        Bpm = p.Settings.Bpm,
        Reverb = Reverb(p.Settings.Reverb),
        Delay = Delay(p.Settings.Delay),
        Master = p.Settings.Master.ToParams(),
        Folders = Folders(p),
        Returns = Returns(p),
    };

    public static InsertSpec[] Specs(IReadOnlyList<InsertSlot> slots, double rmsDb, double bpm = 120)
    {
        var list = new List<InsertSpec>(slots.Count);
        foreach (var s in slots)
        {
            if (!s.Known) continue;
            s.Normalize();
            list.Add(s.Type switch
            {
                InsertSlot.SatKind => new InsertSpec(s.Id, s.Type) { Sat = Sat(s.Sat!, rmsDb) },
                InsertSlot.CompKind => new InsertSpec(s.Id, s.Type) { Comp = Comp(s.Comp!) },
                InsertSlot.ReverbKind => new InsertSpec(s.Id, s.Type) { Reverb = Reverb(s.Reverb!) },
                InsertSlot.DelayKind => new InsertSpec(s.Id, s.Type) { Delay = Delay(s.Delay!), Bpm = bpm },
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
