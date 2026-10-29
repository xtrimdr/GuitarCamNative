using System.Diagnostics;
using NAudio.Midi;

namespace WildTrack.Audio.Midi;

/// <summary>What a key or pedal did, as the engine plays it.</summary>
public enum MidiKind : byte { NoteOn, NoteOff, Sustain, AllOff }

/// <summary>
/// One message from a MIDI input, already filtered by its channel and with its velocity curve applied: which port
/// (<see cref="MidiInputs.Port.Id"/>), its channel (1..16), what it did and when it arrived (Stopwatch ticks).
/// For <see cref="MidiKind.Sustain"/>, <see cref="Velocity"/> is 127 down, 0 up.
/// </summary>
public struct MidiEvent
{
    public long Ticks;
    public int Port;
    public byte Channel;
    public MidiKind Kind;
    public byte Pitch;
    public byte Velocity;
}

/// <summary>A MIDI input Windows sees: its number now, its key (the name, "#2" for a second of the same) and its name.</summary>
public sealed record MidiDevice(int Index, string Name, string Product);

/// <summary>
/// The MIDI inputs (keyboards, controllers, pedals) through winmm (NAudio's <see cref="MidiIn"/>, no other dependency),
/// opened as Configuración → MIDI says. Their messages arrive on winmm's own thread, never the driver's: the ones for
/// notes are filtered there (the port's channel, its velocity curve, "Notas" ticked) and go into a ring the audio
/// thread reads at the start of each block (<see cref="TryRead"/>: no locks on that side; the producers, one thread per
/// device at worst, take a lock among themselves). Everything that arrives also lights its port
/// (<see cref="Port.LastMessage"/>), so the settings show that it works before anything is set up.
/// </summary>
public sealed class MidiInputs : IDisposable
{
    /// <summary>An opened (or failed) input: its settings (the same object the window edits, read live) and its light.</summary>
    public sealed class Port
    {
        /// <summary>Its number for the engine's listeners, never reused while the program runs.</summary>
        public int Id { get; init; }
        public required MidiDevice Device { get; init; }
        public required MidiInputSettings Settings { get => settings; set => settings = value; }
        volatile MidiInputSettings settings = null!;
        internal MidiIn? In;
        /// <summary>Why it could not be opened (another program has it…); null when it is open.</summary>
        public string? Error { get; internal set; }
        public bool Open => In != null;
        long lastTicks;
        int lastMessage;
        /// <summary>The last raw message that arrived (status, data1, data2 in its bytes), and when; 0 = none yet.</summary>
        public int LastMessage => Volatile.Read(ref lastMessage);
        public long LastTicks => Interlocked.Read(ref lastTicks);

        internal void Saw(int message, long ticks)
        {
            Volatile.Write(ref lastMessage, message);
            Interlocked.Exchange(ref lastTicks, ticks);
        }
    }

    /// <summary>Room for 4096 messages between two blocks: a block is a few ms, a keyboard sends hundreds a second at most.</summary>
    const int RingSize = 4096;
    readonly MidiEvent[] ring = new MidiEvent[RingSize];
    int head, tail;
    readonly object produce = new();
    int nextId = 1;
    volatile Port[] ports = [];

    public IReadOnlyList<Port> Ports => ports;

    /// <summary>Every raw message as it arrives, on winmm's thread (wtlab's midi escuchar). Keep it short.</summary>
    public event Action<Port, int>? Received;

    /// <summary>Messages lost because the ring was full (nobody read it: the audio is closed).</summary>
    public long Dropped => Interlocked.Read(ref dropped);
    long dropped;

    /// <summary>How many inputs Windows has now: cheap, polled to notice one plugged in or out.</summary>
    public static int Count => MidiIn.NumberOfDevices;

    /// <summary>The inputs Windows has, with their keys; one that can't be read is left out.</summary>
    public static List<MidiDevice> Devices()
    {
        var list = new List<MidiDevice>();
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            string product;
            try { product = MidiIn.DeviceInfo(i).ProductName?.Trim() ?? ""; }
            catch (Exception e) when (e is NAudio.MmException or InvalidOperationException) { continue; }
            if (product.Length == 0) product = $"MIDI {i + 1}";
            int n = seen.GetValueOrDefault(product) + 1;
            seen[product] = n;
            list.Add(new MidiDevice(i, n == 1 ? product : $"{product} #{n}", product));
        }
        return list;
    }

    /// <summary>
    /// Opens the enabled inputs that are plugged in and closes the rest, keeping open the ones that stay (their light
    /// and their messages go on). Settings for a device never seen are added, with the defaults. Returns the ports.
    /// </summary>
    public IReadOnlyList<Port> Apply(MidiSettings settings)
    {
        var devices = Devices();
        var old = ports;
        var next = new List<Port>();
        var toOpen = new List<Port>();
        foreach (var d in devices)
        {
            var s = settings.For(d.Name);
            var kept = Array.Find(old, p => p.Device.Name == d.Name && p.Device.Index == d.Index && p.Open && s.Enabled);
            if (kept != null)
            {
                kept.Settings = s;
                next.Add(kept);
                continue;
            }
            var port = new Port { Id = nextId++, Device = d, Settings = s };
            if (s.Enabled) toOpen.Add(port);
            next.Add(port);
        }
        // closed first: a device can't be opened twice
        foreach (var p in old)
            if (!next.Contains(p)) ClosePort(p);
        foreach (var p in toOpen) OpenPort(p);
        ports = [.. next];
        return ports;
    }

    void OpenPort(Port port)
    {
        try
        {
            var midi = new MidiIn(port.Device.Index);
            midi.MessageReceived += (_, e) => Receive(port, e.RawMessage);
            midi.Start();
            port.In = midi;
            port.Error = null;
        }
        catch (NAudio.MmException e)
        {
            port.Error = e.Result == NAudio.MmResult.AlreadyAllocated
                ? Texts.T("midi.err.busy", "En uso por otro programa.")
                : Texts.T("midi.err.open", "No se pudo abrir ({0}).", e.Result);
        }
    }

    static void ClosePort(Port p)
    {
        var midi = p.In;
        p.In = null;
        if (midi == null) return;
        try
        {
            midi.Stop();
            midi.Dispose();
        }
        catch (NAudio.MmException) { }
    }

    /// <summary>winmm's thread: lights the port and queues what the engine plays (notes, the sustain pedal, all notes off).</summary>
    void Receive(Port port, int raw)
    {
        long now = Stopwatch.GetTimestamp();
        int status = raw & 0xFF;
        Received?.Invoke(port, raw);
        // active sensing and the clock arrive many times a second: they would keep the light on
        if (status < 0xF0) port.Saw(raw, now);
        if (status >= 0xF0) return;
        var s = port.Settings;
        if (!s.Notes) return;
        int channel = (status & 0x0F) + 1;
        if (s.Channel != 0 && s.Channel != channel) return;
        int d1 = (raw >> 8) & 0x7F, d2 = (raw >> 16) & 0x7F;
        var e = new MidiEvent { Ticks = now, Port = port.Id, Channel = (byte)channel, Pitch = (byte)d1 };
        switch (status & 0xF0)
        {
            case 0x90 when d2 > 0:
                e.Kind = MidiKind.NoteOn;
                e.Velocity = (byte)VelocityCurves.Apply(s.Velocity, d2, s.FixedVelocity);
                break;
            case 0x80 or 0x90:
                e.Kind = MidiKind.NoteOff;
                break;
            case 0xB0 when d1 == 64:
                e.Kind = MidiKind.Sustain;
                e.Velocity = (byte)(d2 >= 64 ? 127 : 0);
                break;
            case 0xB0 when d1 is 120 or 123:
                e.Kind = MidiKind.AllOff;
                break;
            default:
                return;
        }
        Push(e);
    }

    /// <summary>Queues an event as if a device had sent it (wtlab's checks, the window's tests). Any thread.</summary>
    public void Push(MidiEvent e)
    {
        lock (produce)
        {
            int h = head, next = (h + 1) % RingSize;
            if (next == Volatile.Read(ref tail))
            {
                Interlocked.Increment(ref dropped);
                return;
            }
            ring[h] = e;
            Volatile.Write(ref head, next);
        }
    }

    /// <summary>The next queued event. Audio thread (one reader only): no locks, no allocations.</summary>
    public bool TryRead(out MidiEvent e)
    {
        int t = tail;
        if (t == Volatile.Read(ref head))
        {
            e = default;
            return false;
        }
        e = ring[t];
        Volatile.Write(ref tail, (t + 1) % RingSize);
        return true;
    }

    /// <summary>Drops what is queued (the reader was away: the audio closed). Only when the audio thread isn't reading.</summary>
    public void Clear()
    {
        while (TryRead(out _)) { }
    }

    public void Dispose()
    {
        var all = ports;
        ports = [];
        foreach (var p in all) ClosePort(p);
    }
}
