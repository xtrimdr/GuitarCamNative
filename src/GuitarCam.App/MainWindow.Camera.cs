namespace GuitarCam.App;

/// <summary>
/// The live camera in the window: feeds the "En vivo" monitor every tick (LiveMonitor.tsx draws on each
/// animation frame) and shows why a camera didn't open in the status bar, where the web shows its error banner.
/// </summary>
public partial class MainWindow
{
    string? cameraErrorShown;

    void TickCamera()
    {
        var cap = camera.Capture;
        Live.UpdateCamera(cap, camera.Mirror);
        bool recording = RecordingTake;
        int? beat = recording ? engine.CountdownBeat : null;
        Live.SetState(recording && beat == null, beat, Math.Max(0, engine.PositionSec - engine.RecordStartSec));
        Live.SetAudio(engine.DriverName, engine.InputGainDb);
        string? err = cap?.Error;
        if (err == cameraErrorShown) return;
        cameraErrorShown = err;
        if (err != null) Status(err);
    }
}
