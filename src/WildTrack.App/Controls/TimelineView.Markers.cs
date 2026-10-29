using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using WildTrack.Projects;

namespace WildTrack.App.Controls;

/// <summary>
/// The strip over the ruler (native only, asked by the user): the A–B range the loop repeats and the song's markers.
/// A drag on the empty strip draws the range, its edges and its body drag, and a click anywhere jumps there; a marker
/// drags in time, a click jumps to it, a double click renames it, and a double click on the empty strip adds one.
/// Everything snaps like the clips (Alt = free). The drag is a local preview; the window gets it on release, like a
/// clip edit, and decides what to save. Off, the range stays drawn in grey so you still see where it is.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>Height of the strip; <see cref="RulerH"/> includes it, the ruler's numbers sit under it.</summary>
    public const double MarkerH = 18;
    /// <summary>Px from a range edge that grab it.</summary>
    const double LoopEdgeGrab = 5;

    IReadOnlyList<Marker> markers = [];
    /// <summary>The project's markers, in time order (the window sets them after every edit).</summary>
    public IReadOnlyList<Marker> Markers { get => markers; set { markers = value; InvalidateVisual(); } }
    (double Start, double End)? loopRange;
    public (double Start, double End)? LoopRange { get => loopRange; set { loopRange = value; InvalidateVisual(); } }
    bool loopOn;
    /// <summary>The loop is on: the range is drawn in the accent (grey when off).</summary>
    public bool LoopOn { get => loopOn; set { if (loopOn == value) return; loopOn = value; InvalidateVisual(); } }

    /// <summary>A range drawn or moved on the strip (start, end).</summary>
    public event Action<double, double>? LoopRangeRequested;
    public event Action<string, double>? MarkerMoveRequested;
    public event Action<double>? MarkerAddRequested;
    /// <summary>A double click on a marker: the window edits its name in place over that rect.</summary>
    public event Action<string, Rect>? MarkerRenameRequested;
    /// <summary>A right click on the strip: the marker under the pointer (or null), the time there and the point.</summary>
    public event Action<Marker?, double, Point>? StripContextRequested;

    // the strip's gesture: "marker" or "loop" (loopMode says which part: draw, start, end, move)
    string? markerDragId;
    double markerDragSec;
    string loopMode = "";
    double loopAnchor, loopA, loopB, loopDownSec;

    bool InStrip(Point p) => p.Y < MarkerH && p.X > HeaderW;

    double SnapAt(double x) => Math.Max(0, ClipEditing.Snap(Sec(x), Grid, (modifiers & KeyModifiers.Alt) != 0));

    TextLayout MarkerLabel(Marker m, bool hot) => Palette.Text(m.Name, 11, hot ? Palette.Fg : Palette.Dim, Palette.Ui);

    Rect MarkerRect(Marker m)
    {
        double sec = m.Id == markerDragId ? markerDragSec : m.Sec;
        return new Rect(X(sec), 1, MarkerLabel(m, false).Width + 12, MarkerH - 2);
    }

    /// <summary>Where a marker's flag is drawn, to rename it in place; null if it is out of sight.</summary>
    public Rect? MarkerRectOf(string id) =>
        markers.FirstOrDefault(m => m.Id == id) is { } m && MarkerRect(m) is var r && r.Right > HeaderW && r.X < Bounds.Width ? r : null;

    /// <summary>The marker whose flag is under the point, the last drawn (on top) first.</summary>
    Marker? MarkerAt(Point p)
    {
        if (!InStrip(p)) return null;
        for (int k = markers.Count - 1; k >= 0; k--)
            if (MarkerRect(markers[k]).Contains(p)) return markers[k];
        return null;
    }

    (double Start, double End)? ShownLoop => gesture == "loop" ? (loopA, loopB) : loopRange;

    /// <summary>Which part of the range is under x: "start", "end", "move" or "" (none).</summary>
    string LoopPartAt(double x)
    {
        if (loopRange is not { } r) return "";
        double x0 = X(r.Start), x1 = X(r.End);
        if (Math.Abs(x - x0) <= LoopEdgeGrab) return "start";
        if (Math.Abs(x - x1) <= LoopEdgeGrab) return "end";
        return x > x0 && x < x1 ? "move" : "";
    }

    /// <summary>A press on the strip; true when it took it.</summary>
    bool StripPressed(Point p, PointerPressedEventArgs e, bool right)
    {
        if (!InStrip(p)) return false;
        var m = MarkerAt(p);
        if (right)
        {
            StripContextRequested?.Invoke(m, SnapAt(p.X), p);
            e.Handled = true;
            return true;
        }
        if (m != null)
        {
            if (e.ClickCount == 2) { MarkerRenameRequested?.Invoke(m.Id, MarkerRect(m)); e.Handled = true; return true; }
            markerDragId = m.Id;
            markerDragSec = m.Sec;
            StartGesture("marker", e);
            return true;
        }
        if (e.ClickCount == 2)
        {
            MarkerAddRequested?.Invoke(SnapAt(p.X));
            e.Handled = true;
            return true;
        }
        loopMode = LoopPartAt(p.X) is { Length: > 0 } part ? part : "draw";
        loopDownSec = SnapAt(p.X);
        (loopA, loopB) = loopRange ?? (loopDownSec, loopDownSec);
        if (loopMode == "draw") loopAnchor = loopA = loopB = loopDownSec;
        StartGesture("loop", e);
        return true;
    }

    void StripMoved()
    {
        double at = SnapAt(lastPointer.X);
        if (!moved && Math.Abs(lastPointer.X - down.X) < 3) return;
        moved = true;
        if (gesture == "marker") { markerDragSec = at; return; }
        var r = loopRange ?? (loopDownSec, loopDownSec);
        switch (loopMode)
        {
            case "draw": (loopA, loopB) = (Math.Min(loopAnchor, at), Math.Max(loopAnchor, at)); break;
            // an edge dragged past the other one swaps them, like drawing it again
            case "start": (loopA, loopB) = (Math.Min(at, r.End), Math.Max(at, r.End)); break;
            case "end": (loopA, loopB) = (Math.Min(r.Start, at), Math.Max(r.Start, at)); break;
            case "move":
                double d = Math.Max(-r.Start, ClipEditing.Snap(r.Start + Sec(lastPointer.X) - Sec(down.X), Grid, (modifiers & KeyModifiers.Alt) != 0) - r.Start);
                (loopA, loopB) = (r.Start + d, r.End + d);
                break;
        }
    }

    /// <summary>The strip's gesture ended: a drag is committed, a click jumps the playhead there.</summary>
    void StripReleased(string kind, bool changed)
    {
        if (kind == "marker" && markerDragId is { } id)
        {
            if (changed) MarkerMoveRequested?.Invoke(id, markerDragSec);
            else if (markers.FirstOrDefault(m => m.Id == id) is { } m) SeekRequested?.Invoke(m.Sec);
        }
        else if (kind == "loop")
        {
            if (changed && loopB - loopA >= ProjectMarkers.MinLoopSec) LoopRangeRequested?.Invoke(loopA, loopB);
            else if (!changed) SeekRequested?.Invoke(loopDownSec);
        }
        markerDragId = null;
        loopMode = "";
    }

    StandardCursorType StripCursor(Point p) =>
        MarkerAt(p) != null ? StandardCursorType.Hand
        : LoopPartAt(p.X) switch { "start" => StandardCursorType.LeftSide, "end" => StandardCursorType.RightSide, "move" => StandardCursorType.SizeWestEast, _ => StandardCursorType.Arrow };

    /// <summary>The strip: the range (accent while the loop is on, grey off) and the markers' flags on top.</summary>
    void DrawStrip(DrawingContext ctx, double W)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, 0, W - HeaderW, MarkerH));
        ctx.DrawLine(Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7)), new Point(HeaderW, MarkerH - 0.5), new Point(W, MarkerH - 0.5));
        using var clip = ctx.PushClip(new Rect(HeaderW, 0, Math.Max(0, W - HeaderW), MarkerH));
        if (ShownLoop is { } r)
        {
            double x0 = X(r.Start), x1 = X(r.End);
            var color = loopOn ? Palette.AccentColor : Palette.FaintColor;
            ctx.DrawRectangle(Palette.Alpha(color, loopOn ? 0.30 : 0.16), null, new Rect(x0, 2, Math.Max(1, x1 - x0), MarkerH - 4), 4, 4);
            var edge = Palette.Pen(Palette.Alpha(color, 0.9), 2);
            ctx.DrawLine(edge, new Point(x0 + 1, 2), new Point(x0 + 1, MarkerH - 2));
            ctx.DrawLine(edge, new Point(x1 - 1, 2), new Point(x1 - 1, MarkerH - 2));
        }
        foreach (var m in markers)
        {
            var box = MarkerRect(m);
            if (box.X > W || box.Right < HeaderW || m.Id == editingNameId) continue;
            bool hot = m.Id == markerDragId || gesture == null && box.Contains(lastPointer);
            ctx.DrawRectangle(hot ? Palette.Hover : Palette.Raised, Palette.Pen(Palette.Line), box, 4, 4);
            ctx.DrawLine(Palette.Pen(Palette.Fg, 1.5), new Point(box.X + 0.75, 1), new Point(box.X + 0.75, MarkerH - 1));
            var label = MarkerLabel(m, hot);
            ctx.DrawText(label, new Point(box.X + 7, (MarkerH - label.Height) / 2));
        }
    }

    /// <summary>Over the lanes: a faint line under each marker and the range's edges while the loop is on.</summary>
    void DrawStripGuides(DrawingContext ctx, double W, double H)
    {
        var pen = Palette.Pen(Palette.Alpha(Palette.FgColor, 0.14));
        foreach (var m in markers)
        {
            double x = X(m.Id == markerDragId ? markerDragSec : m.Sec);
            if (x >= HeaderW && x <= W) ctx.DrawLine(pen, new Point(x + 0.5, RulerH), new Point(x + 0.5, H));
        }
        if (!loopOn || ShownLoop is not { } r) return;
        var edge = Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.35));
        Guide(X(r.Start));
        Guide(X(r.End));

        void Guide(double x)
        {
            if (x >= HeaderW && x <= W) ctx.DrawLine(edge, new Point(x + 0.5, RulerH), new Point(x + 0.5, H));
        }
    }
}
