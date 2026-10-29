using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using WildTrack.App.Controls;
using WildTrack.App.Model;
using WildTrack.Audio;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// The transition of a join between two clips (native only, asked by the user: simple to use; later, that it also
/// fades the sound, as a bare cut was too abrupt). Opened from the join's chip in the timeline or the clip's
/// right-click menu: Corte / Fundido cruzado / Pasar por negro (only where both clips have a picture), quick lengths
/// and a slider, and a line on what the sound does. Every pick is an edit of the clip coming in
/// (<see cref="EditTrack"/>: undo, autosave); playback, the preview and the export read it from there.
/// </summary>
public partial class MainWindow
{
    Flyout? transitionFlyout;

    void InitTransitions() => Timeline.TransitionRequested += (t, c, at) => ShowTransitionMenu(t, c, at);

    void ShowTransitionMenu(LiveTrack track, LiveClip incoming, Point at)
    {
        if (store == null || store.ReadOnly || restoring) return;
        var refresh = new List<Action>();
        bool filling = false;
        var body = new StackPanel { Spacing = 8 };

        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("trans.title").ToUpperInvariant() });

        // a dip is about the picture: without one on both sides it would only crossfade the sound
        bool video = HasPictures(track, incoming);
        var kinds = new UniformGrid { Rows = 1 };
        foreach (var (id, key) in new[] { (ClipTransitions.CutId, "trans.cut"), (ClipTransitions.CrossfadeId, "trans.crossfade"), (ClipTransitions.DipId, "trans.dip") })
        {
            if (id == ClipTransitions.DipId && !video && incoming.Clip.Transition != id) continue;
            var b = new ToggleButton { Classes = { "seg" }, Padding = new Thickness(6, 0), MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Content = new TextBlock { Text = Loc.T(key), FontSize = 12 } };
            ToolTip.SetTip(b, Loc.T(key + ".tip"));
            b.Click += (_, _) => { SetTransition(track, incoming, id, null); foreach (var r in refresh) r(); };
            refresh.Add(() => b.IsChecked = incoming.Clip.Transition == id);
            kinds.Children.Add(b);
        }
        body.Children.Add(new Border { Classes = { "seg" }, Child = kinds });

        var length = new InspectorSlider(Loc.T("trans.length"), ClipTransitions.MinSec, ClipTransitions.MaxSec, 0.05, ClipTransitions.DefaultSec,
            v => $"{v:0.00} s", v => { if (filling) return; SetTransition(track, incoming, null, v); foreach (var r in refresh) r(); }, Loc.T("trans.length.tip"));
        var quick = new UniformGrid { Rows = 1, Margin = new Thickness(-2, 0) };
        foreach (double sec in ClipTransitions.QuickSec)
        {
            var b = new Button { Classes = { "chip" }, Content = $"{sec:0.##} s", FontSize = 11, MinHeight = 26, Margin = new Thickness(2, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => { SetTransition(track, incoming, null, sec); foreach (var r in refresh) r(); };
            refresh.Add(() => b.Classes.Set("on", Math.Abs(incoming.Clip.TransitionSec - sec) < 0.001));
            quick.Children.Add(b);
        }
        var lengthBox = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        lengthBox.Children.Add(length);
        lengthBox.Children.Add(quick);
        refresh.Add(() =>
        {
            filling = true;
            length.Set(incoming.Clip.TransitionSec);
            filling = false;
            // a cut has no length: the controls stay, dimmed, so the box doesn't jump
            lengthBox.IsEnabled = incoming.Clip.Transition != ClipTransitions.CutId;
            lengthBox.Opacity = lengthBox.IsEnabled ? 1 : 0.35;
        });
        body.Children.Add(lengthBox);

        var still = new TextBlock { FontSize = 11, Foreground = Palette.Faint, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        refresh.Add(() =>
        {
            still.Text = Notes(track, incoming, video);
            still.IsVisible = still.Text.Length > 0;
        });
        body.Children.Add(still);
        body.Children.Add(new TextBlock { Text = Loc.T(video ? "trans.audio" : "trans.audioOnly"), FontSize = 11, Foreground = Palette.Faint,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var r in refresh) r();

        var card = new Border
        {
            Width = 300, Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        transitionFlyout?.Hide();
        var flyout = transitionFlyout = new Flyout
        {
            Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft, PlacementGravity = PopupGravity.BottomRight, HorizontalOffset = at.X, VerticalOffset = at.Y,
        };
        flyout.ShowAt(Timeline);
    }

    /// <summary>Sets the transition into a clip (null keeps that part): one undo step per kind of change, like a slider drag.</summary>
    void SetTransition(LiveTrack track, LiveClip incoming, string? kind, double? sec)
    {
        if (store == null) return;
        string id = incoming.Clip.Id;
        EditTrack(track, _ => ClipEditing.SetTransition(store.Project, id, kind, sec), kind != null ? $"transition:{id}" : $"transitionSec:{id}");
    }

    /// <summary>Both clips of the join into <paramref name="incoming"/> have a picture on a video track.</summary>
    bool HasPictures(LiveTrack track, LiveClip incoming)
    {
        if (AudioOnly || !track.Track.Video || incoming.VideoPath == null) return false;
        var (w, k) = Windows(track, incoming);
        int o = k < 0 ? -1 : ClipTransitions.FindOut(w, k);
        return o >= 0 && track.Clips[o].VideoPath != null;
    }

    static (WildTrack.Audio.Dsp.ClipEnvelope.ClipWindow[] Windows, int Index) Windows(LiveTrack track, LiveClip clip)
    {
        var w = new WildTrack.Audio.Dsp.ClipEnvelope.ClipWindow[track.Clips.Count];
        int k = -1;
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = track.Clips[i].Window;
            if (track.Clips[i] == clip) k = i;
        }
        return (w, k);
    }

    /// <summary>
    /// What the transition can't do with what the trims hide: a crossfade whose clips have no hidden video on a side
    /// holds that side's frame; with no hidden audio on either side the sound crosses with a quick fade at the join.
    /// </summary>
    static string Notes(LiveTrack track, LiveClip incoming, bool video)
    {
        var (w, k) = Windows(track, incoming);
        if (k < 0 || ClipTransitions.Find(w, k, ClipTransitions.Parse(incoming.Clip.Transition), incoming.Clip.TransitionSec) is not { } j) return "";
        var o = w[j.Out];
        var notes = new List<string>();
        if (video && j.Overhang > 0)
        {
            bool outHolds = o.Origin + o.Dur - o.End < j.Overhang - 0.001, inHolds = w[k].Start - w[k].Origin < j.Overhang - 0.001;
            if (outHolds || inHolds) notes.Add(Loc.T(outHolds && inHolds ? "trans.still.both" : outHolds ? "trans.still.out" : "trans.still.in"));
        }
        if (!ClipTransitions.Audio(j, o, w[k], false).Overlap) notes.Add(Loc.T("trans.audioShort"));
        return string.Join(" ", notes);
    }

    /// <summary>The clip menu's entries for the transitions at its start and its end (native only, video tracks).</summary>
    IEnumerable<object> TransitionItems(LiveTrack track, LiveClip clip, Point point)
    {
        if (store == null || clip.Offline) yield break;
        var joins = ClipEditing.Joins(store.Project, track.Track, c => track.Clips.FirstOrDefault(x => x.Clip == c)?.LengthSec);
        foreach (var (from, to, join) in joins)
        {
            if (from != clip.Clip && to != clip.Clip) continue;
            var incoming = track.Clips.FirstOrDefault(x => x.Clip == to);
            var outgoing = track.Clips.FirstOrDefault(x => x.Clip == from);
            if (incoming == null || outgoing == null || incoming.Offline || outgoing.Offline) continue;
            string kind = join is { Kind: TransitionKind.Crossfade } ? Loc.T("trans.crossfade") : join != null ? Loc.T("trans.dip") : Loc.T("trans.cut");
            yield return MenuAction(Loc.T(to == clip.Clip ? "menu.transitionIn" : "menu.transitionOut") + "…",
                () => { ShowTransitionMenu(track, incoming, point); return Task.CompletedTask; }, kind, !store.ReadOnly);
        }
    }
}
