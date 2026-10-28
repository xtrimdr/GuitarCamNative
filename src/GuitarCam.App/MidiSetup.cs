using Avalonia.Threading;
using GuitarCam.Audio.Midi;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The window's side of the MIDI inputs (<see cref="MidiInputs"/>): Configuración → MIDI's settings, loaded once and
/// saved in order after every change ("midi" in config.json), and the ports opened from them. Windows doesn't tell a
/// window that does no device notifications when a keyboard is plugged in, so the number of inputs is polled every
/// two seconds (a cheap call) and the ports are opened again when it changes. Everything here lives on the UI thread;
/// <see cref="Changed"/> says the ports changed, so the engine's listeners are made again.
/// </summary>
public static class MidiSetup
{
    public static MidiSettings Settings { get; private set; } = new();
    public static MidiInputs Inputs { get; } = new();
    /// <summary>Grows every time the ports are opened again: the window's listeners compare it.</summary>
    public static int Version { get; private set; }
    public static event Action? Changed;

    static DispatcherTimer? poll;
    static int lastCount = -1;
    static Task saving = Task.CompletedTask;
    /// <summary>How often the number of inputs is looked at, to notice one plugged in or out.</summary>
    static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    public static void Start()
    {
        Settings = MidiSettings.Parse(AppConfig.Get("midi"));
        Rescan();
        poll = new DispatcherTimer(PollEvery, DispatcherPriority.Background, (_, _) =>
        {
            int n;
            try { n = MidiInputs.Count; }
            catch (NAudio.MmException) { return; }
            if (n != lastCount) Rescan();
        });
        poll.Start();
    }

    /// <summary>Opens the inputs again from the settings (the ones that stay open keep going). "Buscar de nuevo".</summary>
    public static void Rescan()
    {
        try
        {
            lastCount = MidiInputs.Count;
            int known = Settings.Inputs.Count;
            Inputs.Apply(Settings);
            // a device seen for the first time got its settings: remembered from now on
            if (Settings.Inputs.Count != known) Save();
        }
        catch (Exception e) when (e is NAudio.MmException or InvalidOperationException)
        {
            SessionLog.Write($"MIDI: {e.Message}");
        }
        Version++;
        Changed?.Invoke();
    }

    /// <summary>Writes the settings in the background, one write after the other, so the last change is the one that stays.</summary>
    public static void Save()
    {
        string json = Settings.ToJson();
        saving = saving.ContinueWith(_ =>
        {
            try { AppConfig.Set("midi", json); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* this session keeps it */ }
        }, TaskScheduler.Default);
    }

    /// <summary>Forgets a device that isn't plugged in (its settings go).</summary>
    public static void Forget(string name)
    {
        Settings.Inputs.RemoveAll(i => i.Name == name);
        Save();
        Changed?.Invoke();
    }

    /// <summary>The port a track listens to, for the engine: 0 = all, -1 = one that isn't plugged in or is off.</summary>
    public static int PortOf(string? device) => device == null ? 0 : Inputs.Ports.FirstOrDefault(p => p.Device.Name == device && p.Open)?.Id ?? -1;

    /// <summary>What to call a device: the user's name for it, or its own.</summary>
    public static string Shown(string device) => Settings.Find(device)?.Shown ?? device;

    /// <summary>A raw message in a few words, for the input's light: "C4 · 96", "CC 7 · 100", "Pitch bend".</summary>
    public static string Describe(int raw)
    {
        int status = raw & 0xFF, d1 = (raw >> 8) & 0x7F, d2 = (raw >> 16) & 0x7F;
        string what = (status & 0xF0) switch
        {
            0x90 when d2 > 0 => $"{MidiNote.Name(d1)} · {d2}",
            0x80 or 0x90 => Loc.T("midi.msg.off", MidiNote.Name(d1)),
            0xB0 when d1 == 64 => Loc.T(d2 >= 64 ? "midi.msg.pedalDown" : "midi.msg.pedalUp"),
            0xB0 => $"CC {d1} · {d2}",
            0xE0 => Loc.T("midi.msg.bend"),
            0xC0 => Loc.T("midi.msg.program", d1 + 1),
            0xA0 or 0xD0 => Loc.T("midi.msg.aftertouch"),
            _ => "",
        };
        return Loc.T("midi.msg.channel", what, (status & 0x0F) + 1);
    }

    public static void Stop()
    {
        poll?.Stop();
        Inputs.Dispose();
    }
}
