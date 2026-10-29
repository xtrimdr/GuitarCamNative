using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using GuitarCam.App.Controls;
using GuitarCam.App.Model;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The timeline's tool strip (native only, asked by the user): Seleccionar, Rango | Dividir, Borrar | Escuchar, Zoom,
/// Mano, in the project's bar between the edit buttons and Exportar. It is centered on the window like the transport,
/// and only moves aside when the window is too narrow to keep it there. The tools themselves live in the timeline
/// (<see cref="TimelineView.Tool"/>); here are their buttons, keys and what their edits do to the project. Escuchar
/// solos the track for the engine only (<see cref="LiveTrack.ListenSolo"/>), so the mix that is saved never changes.
/// </summary>
public partial class MainWindow
{
    static readonly (TimelineTool Tool, string Shortcut, string Tip)[] ToolButtons =
    [
        (TimelineTool.Select, "toolSelect", "tool.select.tip"),
        (TimelineTool.Range, "toolRange", "tool.range.tip"),
        (TimelineTool.Split, "toolSplit", "tool.split.tip"),
        (TimelineTool.Erase, "toolErase", "tool.erase.tip"),
        (TimelineTool.Listen, "toolListen", "tool.listen.tip"),
        (TimelineTool.Zoom, "toolZoom", "tool.zoom.tip"),
        (TimelineTool.Hand, "toolHand", "tool.hand.tip"),
    ];

    /// <summary>The tools after which the strip draws a line: the editing ones apart from the selecting and the viewing ones.</summary>
    static readonly TimelineTool[] ToolGroupEnds = [TimelineTool.Range, TimelineTool.Erase];

    /// <summary>Px kept between the strip and its neighbors when the window is too narrow to center it.</summary>
    const double ToolGap = 12;

    readonly Dictionary<TimelineTool, ToggleButton> toolButtons = [];
    /// <summary>The track Escuchar is playing (while held), the one still alone until its fade ends, where the playhead
    /// was before, and which listen a late unsolo belongs to.</summary>
    LiveTrack? listening, listenSolo;
    double listenBack;
    int listenTurn;

    void InitTools()
    {
        AutomationProperties.SetName(ToolBox, Loc.T("tools.name"));
        foreach (var (tool, shortcut, tip) in ToolButtons)
        {
            Control icon = tool == TimelineTool.Hand ? FxIcon.Create(FxIcon.Hand, 13)
                : new TextBlock { Classes = { "icon" }, Text = ToolCursors.Glyph(tool), FontSize = 14 };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var b = new ToggleButton
            {
                Classes = { "mode" }, Content = icon, Width = 32, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, IsChecked = tool == TimelineTool.Select,
            };
            ToolTip.SetTip(b, Shortcuts.Tip(Loc.T(tip), shortcut));
            AutomationProperties.SetName(b, Loc.T(tip));
            b.Click += (_, _) => SetTool(tool);
            toolButtons[tool] = b;
            ToolStrip.Children.Add(b);
            if (ToolGroupEnds.Contains(tool)) ToolStrip.Children.Add(new Border { Classes = { "tsep" }, Margin = new Thickness(3, 0) });
        }
        ProjectRow.SizeChanged += (_, _) => PlaceTools();
        SessionChip.SizeChanged += (_, _) => PlaceTools();
        ExportButton.SizeChanged += (_, _) => PlaceTools();
        ToolBox.SizeChanged += (_, _) => PlaceTools();

        Timeline.SplitAtRequested += async (track, clip, at) => await SplitAt(track, clip, at);
        Timeline.ClipsErased += async erased =>
        {
            var ids = WithoutLocked(erased);
            if (ids.Count == 0) return;
            string? trackId = sel?.Track.Id;
            await EditTimeline(p =>
            {
                foreach (var id in ids) ClipEditing.RemoveClip(p, id);
                return trackId;
            }, ids.Count > 1 ? "clips-removed" : "clip-removed");
        };
        Timeline.ListenStarted += StartListening;
        Timeline.ListenEnded += StopListening;
    }

    void SetTool(TimelineTool tool)
    {
        Timeline.Tool = tool;
        foreach (var (t, b) in toolButtons) b.IsChecked = t == tool;
    }

    /// <summary>A tool's key: picks it (the strip follows).</summary>
    void ToolKey(string action)
    {
        foreach (var (tool, shortcut, _) in ToolButtons)
            if (shortcut == action && ToolBox.IsEnabled) SetTool(tool);
    }

    /// <summary>
    /// Centers the strip on the window (its row has the same margin on both sides), but never over the edit buttons or
    /// Exportar: with a narrow window, or a long project name, it slides toward the free side.
    /// </summary>
    void PlaceTools()
    {
        double w = ToolBox.Bounds.Width;
        if (w <= 0) return;
        double left = EditButtons.Bounds.Right, right = ExportButton.Bounds.Left;
        double x = Math.Clamp(ProjectRow.Bounds.Width / 2 - w / 2, left + ToolGap, Math.Max(left + ToolGap, right - ToolGap - w));
        var margin = new Thickness(Math.Round(x - left), 0, 0, 0);
        if (ToolBox.Margin != margin) ToolBox.Margin = margin;
    }

    /// <summary>Dividir: cuts the clip at the clicked time and selects the right piece.</summary>
    async Task SplitAt(LiveTrack track, LiveClip clip, double at)
    {
        if (RefuseLocked(track, clip)) return;
        if (at <= clip.ClipStartSec + ClipEnvelope.MinClipSec || at >= clip.ClipEndSec - ClipEnvelope.MinClipSec)
        {
            Status(Loc.T("split.edge"));
            return;
        }
        string? right = null;
        await EditTimeline(p => { right = ClipEditing.Split(p, clip.Clip.Id, at, clip.LengthSec); return track.Track.Id; }, "clip-split", clip.Clip.Id);
        if (right != null) Timeline.SelectClip(Timeline.Selected, right);
    }

    /// <summary>Escuchar: plays the track alone from the clicked time while the button is held.</summary>
    void StartListening(LiveTrack track, double at)
    {
        if (!engine.IsOpen) { Status(Loc.T("listen.noAudio")); return; }
        if (RecordingBusy) return;
        listenTurn++;
        // a track still alone from the last listen (its fade not over yet) joins the others again
        if (listenSolo != null && listenSolo != track) listenSolo.ListenSolo = false;
        if (listening == null) listenBack = engine.PositionSec;
        listening = listenSolo = track;
        track.ListenSolo = true;
        engine.Seek(at);
        engine.Play();
        Timeline.InvalidateVisual();
    }

    /// <summary>Released: fades out, puts the playhead back where it was, and lets the other tracks in once the fade is over.</summary>
    void StopListening()
    {
        if (listening == null) return;
        listening = null;
        engine.FadeOutAndPause();
        engine.Seek(listenBack);
        int turn = listenTurn;
        // unsoloed before the fade ends, the other tracks would come in for those milliseconds
        DispatcherTimer.RunOnce(() =>
        {
            if (turn != listenTurn || listenSolo == null) return;
            listenSolo.ListenSolo = false;
            listenSolo = null;
            Timeline.InvalidateVisual();
        }, TimeSpan.FromMilliseconds(engine.TransportFadeMs + ListenReleaseMs));
    }

    /// <summary>Margin after the transport fade before the listened track stops playing alone (a few audio blocks).</summary>
    const double ListenReleaseMs = 60;
}
