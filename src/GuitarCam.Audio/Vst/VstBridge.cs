using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace GuitarCam.Audio.Vst;

/// <summary>Where gcvsthost.exe is (the VST3 host process, src/GuitarCam.VstHost): next to the app or gclab.</summary>
public static class VstHost
{
    public const string ExeName = "gcvsthost.exe";
    /// <summary>Set it to use another one; null = the one next to the program.</summary>
    public static string? ExePath { get; set; }

    public static string? Find()
    {
        if (ExePath != null) return File.Exists(ExePath) ? ExePath : null;
        string here = Path.Combine(AppContext.BaseDirectory, ExeName);
        return File.Exists(here) ? here : null;
    }
}

/// <summary>A MIDI note for a plugin: where in the block, on or off, which key, how hard (0..1).</summary>
public struct VstNote
{
    public int Offset;
    public int Type; // 0 on, 1 off
    public int Pitch;
    public float Velocity;

    public const int On = 0, Off = 1;
}

/// <summary>
/// One VST3 plugin running in its own gcvsthost.exe (the user's call: a plugin that hangs or crashes never takes a
/// recording down). They talk through shared memory laid out as in <c>src/GuitarCam.VstHost/shared.h</c> (the offsets
/// below mirror it), two events to wake each other, and text lines on the process's stdin/stdout for what is not audio
/// (state, the plugin's window).
/// <para>
/// Live (<see cref="Exchange"/>), the audio thread never waits for the other process: each block it publishes the next
/// request and takes the output of the one before, so the plugin is heard one block late (the notes go that much early,
/// <see cref="InstrumentPlayer"/>). If the plugin did not finish in time, that block of it is silent and nothing else
/// stops. Offline (<see cref="Render"/>, the export) it waits for each block.
/// </para>
/// </summary>
public sealed unsafe class VstBridge : IDisposable
{
    // shared.h
    const int Magic = 0x53564347, ProtocolVersion = 1;
    public const int MaxBlock = 8192, MaxEvents = 512;
    const int OffMagic = 0, OffVersion = 4, OffSampleRate = 8, OffMaxBlock = 12, OffStatus = 16, OffLatency = 20, OffOutputs = 24,
        OffInstrument = 28, OffRequested = 32, OffDone = 40, OffError = 48, OffSlots = 560, SlotSize = 139304, TotalSize = 279168;
    const int SlotSeq = 0, SlotFrames = 8, SlotNumEvents = 12, SlotTime = 16, SlotTempo = 24, SlotFlags = 32, SlotEvents = 40,
        SlotIn = 8232, SlotOut = 73768, EventSize = 16;
    public const int FlagAllNotesOff = 1, FlagPlaying = 2;
    const int StatusReady = 1, StatusFailed = 2, StatusCrashed = 3;

    readonly MemoryMappedFile map;
    readonly MemoryMappedViewAccessor view;
    readonly byte* mem;
    readonly EventWaitHandle request, done;
    readonly Process process;
    readonly object inputGate = new();
    readonly ManualResetEventSlim readyEvent = new(false);
    TaskCompletionSource<string?>? stateReply, loadReply;
    bool disposed;
    /// <summary>The last block published (audio thread, or the export's thread).</summary>
    long seq;

    public string Name { get; }
    /// <summary>Its own latency in frames, as it reported when it loaded.</summary>
    public int Latency { get; private set; }
    public bool IsInstrument { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Blocks it did not finish in time (live), for the diagnostics.</summary>
    public int Misses;

    /// <summary>A line from the plugin's process that is not an answer: "dirty", "editor-open", "editor-closed", "error …". Raised on a background thread.</summary>
    public event Action<string>? Message;
    /// <summary>The process ended (it crashed, or was closed). Raised on a background thread.</summary>
    public event Action? Exited;

    public bool Ready => Volatile.Read(ref *(int*)(mem + OffStatus)) == StatusReady;
    /// <summary>It failed to load or crashed: it plays nothing.</summary>
    public bool Broken => Volatile.Read(ref *(int*)(mem + OffStatus)) is StatusFailed or StatusCrashed;

    VstBridge(string pluginPath, string cid, string name, int sampleRate, bool offline)
    {
        Name = name;
        string exe = VstHost.Find() ?? throw new FileNotFoundException(Texts.T("err.vst.noHost", "Falta gcvsthost.exe, el programa que abre los plugins VST3."));
        string id = $"GcVst-{Environment.ProcessId}-{Guid.NewGuid():N}";
        map = MemoryMappedFile.CreateNew(id, TotalSize);
        view = map.CreateViewAccessor(0, TotalSize);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        mem = p + view.PointerOffset;
        new Span<byte>(mem, TotalSize).Clear();
        *(int*)(mem + OffMagic) = Magic;
        *(int*)(mem + OffVersion) = ProtocolVersion;
        *(int*)(mem + OffSampleRate) = sampleRate;
        *(int*)(mem + OffMaxBlock) = MaxBlock;
        request = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".req");
        done = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".done");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in (string[])["--host", pluginPath, "--cid", cid, "--shm", id, "--sr", sampleRate.ToString(), "--parent", Environment.ProcessId.ToString()])
            psi.ArgumentList.Add(a);
        if (offline) psi.ArgumentList.Add("--offline");
        process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (!Ready && !Broken) Error ??= Texts.T("err.vst.exited", "El plugin se cerró al abrirse.");
            *(int*)(mem + OffStatus) = StatusCrashed;
            readyEvent.Set();
            stateReply?.TrySetResult(null);
            loadReply?.TrySetResult(null);
            if (!disposed) Exited?.Invoke();
        };
        process.Start();
        new Thread(ReadOutput) { IsBackground = true, Name = "vst " + name }.Start();
    }

    /// <summary>
    /// Starts <paramref name="name"/>'s process and waits until the plugin loaded (or failed: then <see cref="Error"/>
    /// says why and it throws). Slow plugins (big sample libraries) take seconds: never on the UI thread.
    /// </summary>
    public static VstBridge Start(string pluginPath, string cid, string name, int sampleRate, bool offline, int timeoutMs = 60000)
    {
        var b = new VstBridge(pluginPath, cid, name, sampleRate, offline);
        if (!b.readyEvent.Wait(timeoutMs)) b.Error ??= Texts.T("err.vst.timeout", "El plugin tardó demasiado en abrir.");
        if (b.Ready) return b;
        string error = b.Error ?? Texts.T("err.vst.failed", "No se pudo abrir.");
        b.Dispose();
        throw new InvalidOperationException(error);
    }

    void ReadOutput()
    {
        try
        {
            var r = process.StandardOutput;
            while (r.ReadLine() is { } line)
            {
                int space = line.IndexOf(' ');
                string cmd = space < 0 ? line : line[..space], arg = space < 0 ? "" : line[(space + 1)..];
                switch (cmd)
                {
                    case "ready":
                        var parts = arg.Split(' ');
                        Latency = parts.Length > 0 && int.TryParse(parts[0], out int lat) ? Math.Max(0, lat) : 0;
                        IsInstrument = parts.Length > 2 && parts[2] == "1";
                        readyEvent.Set();
                        break;
                    case "error":
                        Error = arg;
                        if (!Ready) readyEvent.Set();
                        loadReply?.TrySetResult(null);
                        Message?.Invoke(line);
                        break;
                    case "state":
                        stateReply?.TrySetResult(arg);
                        break;
                    case "loaded":
                        loadReply?.TrySetResult("ok");
                        break;
                    default:
                        Message?.Invoke(line);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    void Send(string line)
    {
        lock (inputGate)
        {
            if (disposed || process.HasExited) return;
            try
            {
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();
            }
            catch (IOException) { }
        }
    }

    /// <summary>The plugin's state (its sound as the user left it), as base64; null if it did not answer in time.</summary>
    public Task<string?> GetStateAsync(int timeoutMs = 3000)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        stateReply = tcs;
        Send("state");
        // (no await: the class is unsafe)
        return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ContinueWith(t => t.Result == tcs.Task ? tcs.Task.Result : null, TaskScheduler.Default);
    }

    /// <summary>Gives it back a saved state; false if it refused it or did not answer.</summary>
    public bool SetState(string base64, int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loadReply = tcs;
        Send("load-state " + base64);
        return tcs.Task.Wait(timeoutMs) && tcs.Task.Result == "ok";
    }

    /// <summary>Opens the plugin's own window (or brings it up), owned by <paramref name="owner"/> so it stays above it.</summary>
    public void OpenEditor(nint owner) => Send(owner != 0 ? "editor " + owner : "editor");

    public void CloseEditor() => Send("editor-close");

    Span<float> SlotSpan(int slot, int offset, int channel, int frames) =>
        new(mem + OffSlots + slot * SlotSize + offset + channel * MaxBlock * sizeof(float), frames);

    void WriteRequest(long s, ReadOnlySpan<VstNote> notes, int frames, long timeSamples, double tempo, int flags,
        ReadOnlySpan<float> inL, ReadOnlySpan<float> inR)
    {
        byte* slot = mem + OffSlots + (int)(s & 1) * SlotSize;
        int count = Math.Min(notes.Length, MaxEvents);
        *(long*)(slot + SlotSeq) = s;
        *(int*)(slot + SlotFrames) = frames;
        *(int*)(slot + SlotNumEvents) = count;
        *(long*)(slot + SlotTime) = timeSamples;
        *(double*)(slot + SlotTempo) = tempo;
        *(int*)(slot + SlotFlags) = flags;
        for (int i = 0; i < count; i++)
        {
            byte* e = slot + SlotEvents + i * EventSize;
            *(int*)e = notes[i].Offset;
            *(int*)(e + 4) = notes[i].Type;
            *(int*)(e + 8) = notes[i].Pitch;
            *(float*)(e + 12) = notes[i].Velocity;
        }
        if (!inL.IsEmpty)
        {
            inL[..frames].CopyTo(SlotSpan((int)(s & 1), SlotIn, 0, frames));
            (inR.IsEmpty ? inL : inR)[..frames].CopyTo(SlotSpan((int)(s & 1), SlotIn, 1, frames));
        }
        // the slot is complete before the host can see it
        Interlocked.Exchange(ref *(long*)(mem + OffRequested), s);
        request.Set();
    }

    /// <summary>
    /// Audio thread, once per block, never waits: adds the output of the block sent last time to <paramref name="outL"/>
    /// / <paramref name="outR"/> (if the plugin finished it) and sends this one. False when this one could not be sent
    /// (the plugin is two blocks behind): the caller keeps its notes for the next block.
    /// </summary>
    public bool Exchange(ReadOnlySpan<VstNote> notes, int frames, long timeSamples, double tempo, int flags,
        Span<float> outL, Span<float> outR, ReadOnlySpan<float> inL = default, ReadOnlySpan<float> inR = default)
    {
        if (!Ready) return true;
        frames = Math.Clamp(frames, 1, MaxBlock);
        long finished = Volatile.Read(ref *(long*)(mem + OffDone));
        if (seq > 0)
        {
            if (finished >= seq)
            {
                int slot = (int)(seq & 1);
                int n = Math.Min(Math.Min(*(int*)(mem + OffSlots + slot * SlotSize + SlotFrames), outL.Length), outR.Length);
                var l = SlotSpan(slot, SlotOut, 0, n);
                var r = SlotSpan(slot, SlotOut, 1, n);
                for (int i = 0; i < n; i++)
                {
                    outL[i] += l[i];
                    outR[i] += r[i];
                }
            }
            else Misses++;
        }
        // the slot this request takes was last used by the block before the previous one: it must be done
        if (finished < seq - 1) return false;
        seq++;
        WriteRequest(seq, notes, frames, timeSamples, tempo, flags, inL, inR);
        return true;
    }

    /// <summary>The export: sends a block and waits for it, writing its output to <paramref name="outL"/> / <paramref name="outR"/>.</summary>
    public void Render(ReadOnlySpan<VstNote> notes, int frames, long timeSamples, double tempo, int flags, Span<float> outL, Span<float> outR,
        ReadOnlySpan<float> inL = default, ReadOnlySpan<float> inR = default)
    {
        frames = Math.Clamp(frames, 1, MaxBlock);
        seq++;
        WriteRequest(seq, notes, frames, timeSamples, tempo, flags, inL, inR);
        while (Volatile.Read(ref *(long*)(mem + OffDone)) < seq)
        {
            if (Broken) throw new InvalidOperationException(Texts.T("err.vst.crashed", "El plugin {0} se cerró en medio de la exportación.", Name));
            done.WaitOne(1000);
        }
        int slot = (int)(seq & 1);
        SlotSpan(slot, SlotOut, 0, frames).CopyTo(outL);
        SlotSpan(slot, SlotOut, 1, frames).CopyTo(outR);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Send("quit");
        lock (inputGate)
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }
        try
        {
            if (!process.WaitForExit(3000)) process.Kill(true);
        }
        catch (InvalidOperationException) { }
        process.Dispose();
        request.Dispose();
        done.Dispose();
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        view.Dispose();
        map.Dispose();
        readyEvent.Dispose();
    }
}
