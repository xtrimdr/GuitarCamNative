using GuitarCam.Audio;

namespace GuitarCam.App.Video;

/// <summary>
/// The camera settings of Configuración → Cámara and the live capture they open: port of the camera state in
/// App.tsx (selectedVideoId, cameraResMode, useIpCamera, droidCamIpInput / activeDroidCamHost, liveMirror,
/// defaultCameraLatencyMs). Like the web it lives in memory: every start re-picks the camera by the web's
/// preference, the IP stream opens only after "Conectar", and switching modes closes the other stream.
/// Opening and closing run off the UI thread, one after the other (a camera opens in one process at a time).
/// Native only: <see cref="SetPaused"/> lets go of the camera while a solo audio project is open, so another
/// program can use it, and opens it again when the project goes back to video.
/// </summary>
public sealed class LiveCamera : IDisposable
{
    public const string DefaultIp = "192.168.1.50:4747";
    public const double DefaultLatencyMs = 80;

    Task work = Task.CompletedTask;
    bool disposed;

    public string[] Devices { get; private set; } = [];
    public string? Device { get; private set; }
    public CameraResMode Mode { get; private set; } = CameraResMode.Native;
    public bool UseIp { get; private set; }
    public string IpInput { get; set; } = DefaultIp;
    public string ActiveIpHost { get; private set; } = "";
    /// <summary>"Espejar imagen en vivo": the monitor shows it mirrored and new takes start mirrored.</summary>
    public bool Mirror { get; set; }
    /// <summary>"Latencia de cámara" for new takes (how late the image arrives compared to the audio).</summary>
    public double LatencyMs { get; set; } = DefaultLatencyMs;
    public CameraCapture? Capture { get; private set; }
    public bool Listing { get; private set; }
    public bool Paused { get; private set; }

    public Task SetPaused(bool paused) => paused == Paused ? Task.CompletedTask : Queue(() => Reopen(() => Paused = paused));

    public string Resolution => Capture?.Resolution ?? "";
    public bool HasSignal => Capture?.HasSignal == true;

    /// <summary>The list or the capture changed: whoever shows them refreshes.</summary>
    public event Action? Changed;

    /// <summary>"Refrescar dispositivos": lists the cameras, keeps the chosen one if it's still there, and opens it.</summary>
    public Task Refresh() => Queue(() =>
    {
        Listing = true;
        Changed?.Invoke();
        var list = CameraDevices.List();
        Devices = list;
        Listing = false;
        string? pick = CameraDevices.Pick(list, Device);
        if (pick != Device || Capture == null || Capture.Error != null) Reopen(() => Device = pick);
    });

    public Task SelectDevice(string device) => device == Device ? Task.CompletedTask : Queue(() => Reopen(() => Device = device));

    public Task SetMode(CameraResMode mode) => mode == Mode ? Task.CompletedTask : Queue(() => Reopen(() => Mode = mode));

    public Task SetUseIp(bool ip) => ip == UseIp ? Task.CompletedTask : Queue(() => Reopen(() => UseIp = ip));

    /// <summary>"Conectar": opens DroidCam at the typed address.</summary>
    public Task ConnectIp() => Queue(() => Reopen(() => ActiveIpHost = IpInput.Trim()));

    Task Queue(Action a)
    {
        work = work.ContinueWith(_ => { if (!disposed) a(); }, TaskScheduler.Default);
        return work;
    }

    void Reopen(Action change)
    {
        var old = Capture;
        Capture = null;
        old?.Dispose();
        change();
        if (Paused) { /* stays closed until SetPaused(false) */ }
        else if (UseIp)
        {
            if (ActiveIpHost.Length > 0) Capture = CameraCapture.Open(new CameraSource(null, CameraResMode.Native, ActiveIpHost));
        }
        else if (Device != null) Capture = CameraCapture.Open(new CameraSource(Device, Mode));
        Changed?.Invoke();
    }

    public void Dispose()
    {
        disposed = true;
        work.Wait(3000);
        Capture?.Dispose();
        Capture = null;
    }
}
