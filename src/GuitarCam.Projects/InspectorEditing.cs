namespace GuitarCam.Projects;

/// <summary>
/// Framing operations from GuitarCam/src/components/SplitScreenCanvas.tsx and TakeInspector.tsx.
/// Native edits belong to the selected clip, so separate uses of a recording can keep their own framing.
/// </summary>
public static class InspectorEditing
{
    public static (double X, double Y) Pan(double x, double y, double dx, double dy) =>
        (Fixed2(Math.Clamp(x - dx / 160, -1, 1)), Fixed2(Math.Clamp(y - dy / 160, -1, 1)));

    public static double Zoom(double zoom, bool up) => Fixed2(Math.Clamp(zoom + (up ? 0.08 : -0.08), 1, 2.5));

    static double Fixed2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static void Frame(Clip clip, double? zoom = null, double? x = null, double? y = null, bool? mirror = null)
    {
        var f = clip.Framing;
        if (zoom is { } z) f.Zoom = Math.Clamp(z, 1, 2.5);
        if (x is { } px) f.PanX = Math.Clamp(px, -1, 1);
        if (y is { } py) f.PanY = Math.Clamp(py, -1, 1);
        if (mirror is { } m) f.Mirror = m;
    }

    // Like the web reset, the mirror switch keeps its state.
    public static void ResetFrame(Clip clip) => Frame(clip, 1, 0, 0);
}
