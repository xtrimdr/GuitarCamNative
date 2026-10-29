namespace WildTrack.App;

/// <summary>
/// The live camera in the window: feeds the "En vivo" monitor every tick (LiveMonitor.tsx draws on each
/// animation frame) and shows why a camera didn't open in the status bar, where the web shows its error banner.
/// A camera that was sending and stops (unplugged, DroidCam closed) pops up a toast: it doesn't come back by itself.
/// </summary>
public partial class MainWindow
{
    string? cameraErrorShown;
    // the capture that last had a picture, so losing it toasts once until it (or a new one) sends again
    WildTrack.Audio.CameraCapture? cameraWithSignal;

    void TickCamera()
    {
        var cap = camera.Capture;
        Live.UpdateCamera(cap, camera.Mirror);
        bool recording = RecordingTake;
        int? beat = recording ? engine.CountdownBeat : null;
        Live.SetState(recording && beat == null, beat, Math.Max(0, engine.PositionSec - engine.RecordStartSec));
        Live.SetAudio(engine.DriverName, engine.InputGainDb);
        if (cap is { HasSignal: true }) cameraWithSignal = cap;
        else if (cap != null && cap == cameraWithSignal)
        {
            cameraWithSignal = null;
            if (!AudioOnly) Toast(Loc.T("toast.camera.lost"));
        }
        string? err = cap?.Error;
        if (err == cameraErrorShown) return;
        cameraErrorShown = err;
        if (err != null) Status(err);
    }
}
