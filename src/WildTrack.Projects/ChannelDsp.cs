using WildTrack.Audio.Dsp;
using WildTrack.Audio.Vst;

namespace WildTrack.Projects;

/// <summary>
/// Turns a <see cref="Channel"/>'s insert slots into what the engine takes (<see cref="InsertSpec"/>). One place for
/// the live engine, the export and wtlab, so the three hear the same chain. Slots of a kind this version doesn't know
/// are left out. Also the mix's nodes (<see cref="Nodes"/>: the folders, then the buses in <see cref="ProjectBuses.Order"/>),
/// where each track goes (<see cref="TrackOutput"/>) and what it sends (<see cref="Sends"/>), and the whole bus side of
/// the export (<see cref="Bus"/>).
/// </summary>
public static class ChannelDsp
{
    /// <summary>
    /// The level a bus's Vida is measured against: a bus has no take of its own, so it is the same as a track with no
    /// audio. Fixed, so the live engine, the export and wtlab drive it the same.
    /// </summary>
    public const double BusRmsDb = -24;

    /// <param name="rmsDb">The take's level, which the Vida drive is measured against (-24 without audio).</param>
    /// <param name="live">Live: the running process of each VST3 slot (null while it loads); without it (the export,
    /// wtlab), each VST3 slot opens its own.</param>
    public static InsertSpec[] Specs(Channel ch, double rmsDb, double bpm = 120, Func<InsertSlot, VstBridge?>? live = null, bool bypass = false) =>
        Specs(ch.Slots, rmsDb, bpm, live, bypass);

    /// <summary>A bus's slots; its delays follow <paramref name="bpm"/>.</summary>
    public static InsertSpec[] BusSpecs(Bus b, double bpm, Func<InsertSlot, VstBridge?>? live = null, bool bypass = false) => Specs(b.Inserts, BusRmsDb, bpm, live, bypass);

    /// <summary>
    /// The ids of the mix's nodes in the order they run: every folder (nothing feeds a folder but its tracks), then the
    /// buses, each after everything that feeds it. What <see cref="MixBusSpec.Nodes"/> and the live engine follow.
    /// </summary>
    public static List<string> Nodes(Project p) => [.. p.Folders.Where(f => !f.Separator).Select(f => f.Id), .. ProjectBuses.Order(p).Select(b => b.Id)];

    /// <summary>Where a track's output goes, by node id: its folder, or else its channel's bus; null = the master.</summary>
    public static string? TrackOutput(Track t) => t.FolderId ?? t.Channel.Output;

    /// <summary>What <paramref name="r"/> sends, as indexes into <paramref name="nodes"/> (sends to a missing bus are left out).</summary>
    public static MixSendSpec[] Sends(IReadOnlyList<string> nodes, IRouted r)
    {
        var list = new List<MixSendSpec>(r.Sends.Count);
        foreach (var s in r.Sends)
        {
            int i = IndexOf(nodes, s.BusId);
            if (i >= 0) list.Add(new MixSendSpec { To = i, Level = s.Level, On = s.On, Pre = s.Pre });
        }
        return [.. list];
    }

    static int IndexOf(IReadOnlyList<string> nodes, string? id)
    {
        if (id == null) return -1;
        for (int i = 0; i < nodes.Count; i++) if (nodes[i] == id) return i;
        return -1;
    }

    /// <summary>A track as the export renders it, with its clips and the level its Vida is measured against.</summary>
    public static MixTrackSpec Track(Project p, IReadOnlyList<string> nodes, Track t, IReadOnlyList<MixClipSpec> clips, double rmsDb)
    {
        var ch = t.Channel;
        return new MixTrackSpec
        {
            Name = t.Name,
            Clips = clips,
            Notes = Notes(t, p.Settings.Bpm, MidiSampleRate),
            Instrument = t.Instrument is { } i ? Instance(i) : null,
            Tempo = p.Settings.Bpm,
            Volume = ch.Volume,
            Pan = ch.Pan,
            Muted = ch.Muted,
            Solo = ch.Solo,
            Inserts = Specs(ch, rmsDb, p.Settings.Bpm),
            Output = IndexOf(nodes, TrackOutput(t)),
            Sends = Sends(nodes, ch),
        };
    }

    /// <summary>The rate the export renders notes at: the engine's (<c>PlaybackEngine.SampleRate</c>).</summary>
    const int MidiSampleRate = 48000;

    /// <summary>
    /// A MIDI track's notes as the engine plays them (<see cref="NoteSynth"/>), in frames at <paramref name="sampleRate"/>
    /// with the tempo turning beats into time; null for an audio track or one without notes. A note that runs past its
    /// clip's end is cut there and one that starts past it is left out.
    /// </summary>
    public static NoteSequence? Notes(Track t, double bpm, int sampleRate)
    {
        if (!t.IsMidi || t.MidiClips is not { Count: > 0 } clips) return null;
        double beatSec = 60 / Math.Max(1, bpm);
        var list = new List<NoteEvent>();
        foreach (var c in clips)
            foreach (var n in c.Notes)
            {
                if (n.Beat >= c.LengthBeats) continue;
                double start = c.AtSec + n.Beat * beatSec;
                double end = c.AtSec + Math.Min(n.EndBeat, c.LengthBeats) * beatSec;
                long from = (long)Math.Round(start * sampleRate);
                long len = Math.Max(1, (long)Math.Round(end * sampleRate) - from);
                // a plugin takes whole semitones: the Transponer's cents only reach the test tone
                int pitch = Math.Clamp(n.Pitch + (int)Math.Round(t.Transpose), MidiNote.MinPitch, MidiNote.MaxPitch);
                list.Add(new NoteEvent(from, len, NoteSynth.Hz(n.Pitch + t.Transpose), NoteSynth.Gain(n.Velocity), pitch, n.Velocity));
            }
        return list.Count == 0 ? null : new NoteSequence([.. list], sampleRate);
    }

    /// <summary>The bus side of the mix (the folders and buses as nodes, and the master), for the export and wtlab.</summary>
    public static MixBusSpec Bus(Project p)
    {
        var nodes = Nodes(p);
        var list = new List<MixNodeSpec>(nodes.Count);
        foreach (var id in nodes)
        {
            if (p.Folders.Find(f => f.Id == id) is { } f)
                list.Add(new MixNodeSpec { Name = f.Name, Output = IndexOf(nodes, f.Output), Sends = Sends(nodes, f) });
            else if (p.Settings.Buses.Find(b => b.Id == id) is { } b)
                list.Add(new MixNodeSpec
                {
                    Name = b.Name,
                    Volume = b.Volume,
                    Pan = b.Pan,
                    Muted = b.Muted,
                    Inserts = BusSpecs(b, p.Settings.Bpm),
                    Output = IndexOf(nodes, b.Output),
                    Sends = Sends(nodes, b),
                });
        }
        return new MixBusSpec { Bpm = p.Settings.Bpm, Master = p.Settings.Master.ToParams(), Nodes = list };
    }

    public static ReverbParams Reverb(ReverbSettings r, bool bypass = false) => new(r.Decay, r.PreDelayMs, r.Tone, r.LowCut, r.Width, r.ReturnGain, r.Muted || bypass);

    public static DelayParams Delay(DelaySettings d, bool bypass = false) =>
        new(DelayParams.ParseMode(d.Mode), d.Sync, d.Division, d.TimeMs, d.Feedback, d.Tone, d.LowCut, d.Width, d.Duck, d.ReturnGain, d.Muted || bypass);

    /// <summary>
    /// The slots as the engine takes them. <paramref name="bypass"/> hands every one over off, as its own power switch
    /// would (the mixer's FX switch: same path, same fades, nothing reset), without touching the project.
    /// </summary>
    public static InsertSpec[] Specs(IReadOnlyList<InsertSlot> slots, double rmsDb, double bpm = 120, Func<InsertSlot, VstBridge?>? live = null, bool bypass = false)
    {
        var list = new List<InsertSpec>(slots.Count);
        foreach (var s in slots)
        {
            if (!s.Known) continue;
            s.Normalize();
            list.Add(s.Type switch
            {
                InsertSlot.SatKind => new InsertSpec(s.Id, s.Type) { Sat = Sat(s.Sat!, rmsDb, bypass) },
                InsertSlot.CompKind => new InsertSpec(s.Id, s.Type) { Comp = Comp(s.Comp!, bypass) },
                InsertSlot.ReverbKind => new InsertSpec(s.Id, s.Type) { Reverb = Reverb(s.Reverb!, bypass) },
                InsertSlot.DelayKind => new InsertSpec(s.Id, s.Type) { Delay = Delay(s.Delay!, bypass), Bpm = bpm },
                InsertSlot.GateKind => new InsertSpec(s.Id, s.Type) { Gate = Gate(s.Gate!, bypass) },
                InsertSlot.VstKind => new InsertSpec(s.Id, s.Type) { Vst = Vst(s.Vst!, live == null ? null : live(s), live == null, bypass), Bpm = bpm },
                _ => new InsertSpec(s.Id, s.Type) { EqOn = s.Eq!.On && !bypass, EqBands = EqBands(s.Eq) },
            });
        }
        return [.. list];
    }

    /// <summary>A VST3 slot as the engine takes it: live with the window's process, or offline (it opens its own).</summary>
    public static VstSlotSpec Vst(VstSlotSettings v, VstBridge? live, bool offline, bool bypass = false) =>
        new(Instance(v.Plugin), v.On && !bypass) { Live = live, Offline = offline };

    public static VstInstance Instance(PluginRef p) => new(p.Path, p.ClassId, p.Name, p.State);

    public static GateParams Gate(GateSettings g, bool bypass = false) => new(g.On && !bypass, GateParams.ParseMode(g.Mode), g.Threshold);

    public static SatParams Sat(SatSettings s, double rmsDb, bool bypass = false) =>
        SatParams.From(s.On && !bypass, s.Mode == "tape" ? SatMode.Tape : SatMode.Tube, s.Amount, rmsDb);

    public static CompState Comp(CompSettings c, bool bypass = false) => new(
        c.On && !bypass,
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
