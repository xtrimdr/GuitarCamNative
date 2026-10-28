using GuitarCam.Audio.Dsp;
using GuitarCam.Audio.Midi;

namespace GuitarCam.Audio.Vst;

/// <summary>
/// A MIDI track's notes played by an instrument plugin (<see cref="VstBridge"/>), live. The plugin is heard one block
/// late plus its own latency (<see cref="VstBridge.Exchange"/>), so the notes go that much early: when the playhead is
/// at <c>pos</c>, the notes sent are the ones from <c>pos + block + latency</c>, and they sound right on the timeline
/// with nothing else delayed. A jump (seek, loop) or a stop turns every note off first; the notes between where it lands
/// and that lookahead (the first beat of the song, when it starts from 0) go right away, at the start of the first block,
/// late by the lookahead instead of lost; so do the ones that began less than a block before it (a loop lands that much
/// past its start). Notes already playing longer than that when the playhead lands in their middle are not retriggered.
/// <para>
/// While stopped (<see cref="Idle"/>) it keeps running, silent, so the piano roll's keys (<see cref="Audition"/>) sound
/// through the instrument: its output opens with the first one and closes at the next stop. Audio thread only, except
/// <see cref="Audition"/>, <see cref="Notes"/> and <see cref="Tempo"/>; no allocations, no locks.
/// </para>
/// <para>
/// The keys of a MIDI keyboard (<see cref="LiveNotes"/>) go at the start of the block they arrive in, playing or not;
/// after an "all notes off" (a jump, the song starting) the ones still held are sent again, so a chord held while the
/// song starts keeps sounding.
/// </para>
/// </summary>
public sealed class InstrumentPlayer(VstBridge bridge)
{
    public VstBridge Bridge { get; } = bridge;

    volatile NoteSequence? notes;
    /// <summary>The track's notes in frames, swapped whole by the UI.</summary>
    public NoteSequence? Notes { get => notes; set => notes = value; }
    /// <summary>The project's tempo, for the plugin's transport (arpeggiators, synced LFOs).</summary>
    public double Tempo { get; set; } = 120;

    readonly VstNote[] buf = new VstNote[VstBridge.MaxEvents];
    int count;
    /// <summary>The block that could not be sent: its notes go at the start of the next one.</summary>
    bool carried;
    /// <summary>Where the next block should start if nothing jumps; -1 = after a stop.</summary>
    long expected = -1;
    bool idle = true;
    /// <summary>The output while stopped: 0 (shut after a stop) to 1 (open since an audition).</summary>
    float gate;
    const float GateStep = 1f / 240; // 5 ms at 48 kHz

    // the piano roll's notes: a lock-free ring from the UI thread, and how long each key still has to sound
    readonly (int Pitch, int Velocity, int Frames)[] ring = new (int, int, int)[64];
    int ringHead, ringTail;
    readonly int[] offIn = new int[128];

    /// <summary>Plays a key on its own for <paramref name="frames"/> (the piano roll). UI thread.</summary>
    public void Audition(int pitch, int velocity, int frames)
    {
        int head = ringHead, next = (head + 1) % ring.Length;
        if (next == Volatile.Read(ref ringTail)) return; // full: dropped
        ring[head] = (Math.Clamp(pitch, 0, 127), Math.Clamp(velocity, 1, 127), Math.Max(1, frames));
        Volatile.Write(ref ringHead, next);
    }

    /// <summary>
    /// While playing: adds the instrument's sound for frames <c>[pos .. pos + n)</c> to <paramref name="l"/> /
    /// <paramref name="r"/> and sends the notes that start or end a block and a latency later.
    /// </summary>
    public void Play(long pos, int n, Span<float> l, Span<float> r, LiveNotes? keys = null)
    {
        int flags = VstBridge.FlagPlaying;
        if (pos != expected || idle)
        {
            flags |= VstBridge.FlagAllNotesOff;
            if (!carried) count = 0;
        }
        idle = false;
        expected = pos + n;
        BeginBlock();
        long from = pos + n + Bridge.Latency;
        if (notes is { } seq)
        {
            if ((flags & VstBridge.FlagAllNotesOff) != 0) CatchUp(seq, pos, from, n);
            AddNotes(seq, from, from + n);
        }
        Auditions(n);
        Keys(keys, flags);
        Send(n, from, flags, l, r);
    }

    readonly LiveNote[] held = new LiveNote[128];

    /// <summary>The keys played since the last block; after "all notes off", every key still sounding.</summary>
    bool Keys(LiveNotes? keys, int flags)
    {
        if (keys == null) return false;
        if ((flags & VstBridge.FlagAllNotesOff) != 0)
        {
            int k = keys.Sounding(held);
            for (int i = 0; i < k; i++) Add(0, VstNote.On, held[i].Sounds, held[i].Velocity);
        }
        else
        {
            var events = keys.Events;
            for (int i = 0; i < events.Length; i++)
                Add(0, events[i].On ? VstNote.On : VstNote.Off, events[i].Sounds, events[i].Velocity);
        }
        return keys.EventCount > 0 || keys.AnySounding;
    }

    /// <summary>
    /// After a jump: the notes that start between the playhead and the lookahead (they can't be early any more) go at the
    /// start of the block, with their own length when they also end before it.
    /// </summary>
    void CatchUp(NoteSequence seq, long pos, long from, int n)
    {
        var all = seq.Notes;
        int lo = 0, hi = all.Length;
        // a loop lands a little past its start (what the last block ran over the end): the notes right at the start
        // began up to a block before the playhead and are played too, or the first beat of every lap was lost
        long since = pos - n;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (all[mid].Start < since) lo = mid + 1; else hi = mid;
        }
        for (int k = lo; k < all.Length && all[k].Start < from; k++)
        {
            var e = all[k];
            Add(0, VstNote.On, e.Pitch, e.Velocity);
            if (e.Start + e.Length < from) Add((int)Math.Clamp(e.Length, 1, n - 1), VstNote.Off, e.Pitch, e.Velocity);
        }
    }

    /// <summary>While stopped: auditions and the keyboard only. Adds the output while the gate is open; false when there was nothing to add.</summary>
    public bool Idle(int n, Span<float> l, Span<float> r, LiveNotes? keys = null)
    {
        int flags = 0;
        if (!idle)
        {
            // just stopped: everything off, and the output shuts (the transport already faded it out)
            idle = true;
            gate = 0;
            flags |= VstBridge.FlagAllNotesOff;
            if (!carried) count = 0;
        }
        expected = -1;
        BeginBlock();
        bool auditioned = Auditions(n);
        auditioned |= Keys(keys, flags);
        if (!auditioned && gate <= 0)
        {
            // keep it running so its tails and envelopes stay current, but nothing is heard
            Send(n, 0, flags, default, default);
            return false;
        }
        l.Clear();
        r.Clear();
        Send(n, 0, flags, l, r);
        float g = gate;
        for (int i = 0; i < n; i++)
        {
            if (g < 1) g = Math.Min(1, g + GateStep);
            l[i] *= g;
            r[i] *= g;
        }
        gate = g;
        return true;
    }

    void BeginBlock()
    {
        // a block that could not go: its notes stay, at the start of this one
        if (!carried) count = 0;
        else for (int i = 0; i < count; i++) buf[i].Offset = 0;
    }

    void Send(int n, long time, int flags, Span<float> l, Span<float> r)
    {
        SortByOffset();
        Span<float> none = default;
        carried = !Bridge.Exchange(buf.AsSpan(0, count), n, time, Tempo, flags, l.IsEmpty ? none : l, r.IsEmpty ? none : r);
        if (!carried) count = 0;
    }

    void Add(int offset, int type, int pitch, int velocity)
    {
        if (count >= buf.Length) return;
        buf[count++] = new VstNote { Offset = offset, Type = type, Pitch = pitch, Velocity = velocity / 127f };
    }

    /// <summary>Notes that start or end in <c>[from .. to)</c>, by binary search over the sorted sequence.</summary>
    void AddNotes(NoteSequence seq, long from, long to)
    {
        var all = seq.Notes;
        int lo = 0, hi = all.Length;
        long earliest = from - seq.MaxSpan;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (all[mid].Start < earliest) lo = mid + 1; else hi = mid;
        }
        for (int k = lo; k < all.Length && all[k].Start < to; k++)
        {
            var e = all[k];
            if (e.Start >= from) Add((int)(e.Start - from), VstNote.On, e.Pitch, e.Velocity);
            long end = e.Start + e.Length;
            if (end >= from && end < to) Add((int)(end - from), VstNote.Off, e.Pitch, e.Velocity);
        }
    }

    /// <summary>The piano roll's keys: new ones start now, held ones end when their time runs out. True if any sounds.</summary>
    bool Auditions(int n)
    {
        bool any = false;
        for (int p = 0; p < 128; p++)
        {
            if (offIn[p] <= 0) continue;
            any = true;
            if (offIn[p] <= n) { Add(offIn[p] - 1, VstNote.Off, p, 0); offIn[p] = 0; }
            else offIn[p] -= n;
        }
        int tail = ringTail;
        while (tail != Volatile.Read(ref ringHead))
        {
            var (pitch, velocity, frames) = ring[tail];
            if (offIn[pitch] > 0) Add(0, VstNote.Off, pitch, 0);
            Add(0, VstNote.On, pitch, velocity);
            offIn[pitch] = frames;
            tail = (tail + 1) % ring.Length;
            any = true;
        }
        Volatile.Write(ref ringTail, tail);
        return any;
    }

    /// <summary>Plugins want events in time order; at one frame, offs before ons (a note retriggered right where it ends).</summary>
    void SortByOffset()
    {
        for (int i = 1; i < count; i++)
        {
            var x = buf[i];
            int j = i - 1;
            while (j >= 0 && (buf[j].Offset > x.Offset || buf[j].Offset == x.Offset && buf[j].Type < x.Type))
            {
                buf[j + 1] = buf[j];
                j--;
            }
            buf[j + 1] = x;
        }
    }
}

/// <summary>
/// The export's side of an instrument plugin: a fresh process with the track's saved state, rendering offline and
/// waiting for every block (<see cref="VstBridge.Render"/>), so it plays exactly the notes at their time. The plugin's
/// own latency is taken out by sending the notes that much early, as live.
/// </summary>
public sealed class InstrumentRender : IDisposable
{
    readonly VstBridge bridge;
    readonly NoteSequence? notes;
    readonly double tempo;
    readonly VstNote[] buf = new VstNote[VstBridge.MaxEvents];
    bool started;

    /// <param name="offline">Tell the plugin it renders offline (it may use its best quality); false = as live.</param>
    public InstrumentRender(VstInstance instrument, NoteSequence? notes, double tempo, int sampleRate, bool offline = true)
    {
        bridge = VstBridge.Start(instrument.Path, instrument.ClassId, instrument.Name, sampleRate, offline);
        if (instrument.State is { Length: > 0 } state && !bridge.SetState(state))
        {
            string name = instrument.Name;
            bridge.Dispose();
            throw new InvalidOperationException(Texts.T("err.vst.state", "{0} no aceptó su configuración guardada.", name));
        }
        this.notes = notes;
        this.tempo = tempo;
    }

    /// <summary>
    /// Runs it on silence at the speed of real time for up to <paramref name="seconds"/>: some instruments load their
    /// samples only while they are being processed.
    /// </summary>
    public void Warm(double seconds)
    {
        var l = new float[VstBridge.MaxBlock];
        var r = new float[VstBridge.MaxBlock];
        int block = 1024;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (long done = 0; sw.Elapsed.TotalSeconds < seconds; done += block)
        {
            bridge.Render([], block, -1_000_000 + done, tempo, 0, l.AsSpan(0, block), r.AsSpan(0, block));
            double ahead = done / 48000.0 - sw.Elapsed.TotalSeconds;
            if (ahead > 0) Thread.Sleep(TimeSpan.FromSeconds(ahead));
        }
    }

    /// <summary>Renders frames <c>[pos .. pos + n)</c> into <paramref name="l"/> / <paramref name="r"/>, in blocks of at most <see cref="VstBridge.MaxBlock"/>.</summary>
    public void Render(long pos, int n, Span<float> l, Span<float> r)
    {
        for (int off = 0; off < n; off += VstBridge.MaxBlock)
        {
            int len = Math.Min(VstBridge.MaxBlock, n - off);
            long from = pos + off + bridge.Latency;
            int count = 0;
            if (notes is { } seq)
            {
                var all = seq.Notes;
                // the first block also takes what starts before its plugin latency (the first beat, with a latent plugin)
                long first = started ? from : Math.Min(from, pos + off);
                for (int k = 0; k < all.Length && all[k].Start < from + len; k++)
                {
                    var e = all[k];
                    if (e.Start < from && e.Start >= first && count < buf.Length)
                        buf[count++] = new VstNote { Offset = 0, Type = VstNote.On, Pitch = e.Pitch, Velocity = e.Velocity / 127f };
                    if (e.Start >= from && count < buf.Length) buf[count++] = new VstNote { Offset = (int)(e.Start - from), Type = VstNote.On, Pitch = e.Pitch, Velocity = e.Velocity / 127f };
                    long end = e.Start + e.Length;
                    if (end >= from && end < from + len && count < buf.Length) buf[count++] = new VstNote { Offset = (int)(end - from), Type = VstNote.Off, Pitch = e.Pitch };
                }
                Array.Sort(buf, 0, count, Comparer<VstNote>.Create((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : b.Type.CompareTo(a.Type)));
            }
            int flags = VstBridge.FlagPlaying | (started ? 0 : VstBridge.FlagAllNotesOff);
            started = true;
            bridge.Render(buf.AsSpan(0, count), len, from, tempo, flags, l.Slice(off, len), r.Slice(off, len));
        }
    }

    public void Dispose() => bridge.Dispose();
}

/// <summary>An instrument as the export needs it: which plugin (file and class) and its saved state (base64).</summary>
public sealed record VstInstance(string Path, string ClassId, string Name, string? State);
