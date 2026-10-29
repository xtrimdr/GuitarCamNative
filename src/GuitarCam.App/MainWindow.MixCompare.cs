using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using GuitarCam.App.Controls;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The mixer's A | B (native only, <see cref="MixCompare"/>): two whole mixes of the same takes, switched while it
/// plays, like a plugin's A/B. The first click on the other letter makes it a copy of this one, so you change one
/// and listen back and forth. Applied the way undo applies a channel edit (<see cref="LiveTrack.ApplyChannel"/> and
/// the buses; the master stays): every slot keeps its id in both mixes, so effects keep their state and nothing is
/// reloaded. One undo step per switch. Its menu (right click) copies this mix over the other or forgets it.
/// </summary>
public partial class MainWindow
{
    Control BuildMixCompare()
    {
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (string id in new[] { MixCompareState.A, MixCompareState.B })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = id.ToUpperInvariant(), FontSize = 12, MinWidth = 28,
                HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) =>
            {
                if (store != null && MixCompare.Active(store.Project) != id) SwitchMix();
                else RefreshMixCompare();
            };
            headerRefresh.Add(() =>
            {
                b.IsChecked = store != null && MixCompare.Active(store.Project) == id;
                b.IsEnabled = store is { ReadOnly: false } && !restoring;
                ToolTip.SetTip(b, store != null && MixCompare.Active(store.Project) == id ? Loc.T("mix.ab.on", id.ToUpperInvariant())
                    : store != null && MixCompare.HasOther(store.Project) ? Loc.T("mix.ab.go", id.ToUpperInvariant())
                    : Loc.T("mix.ab.first", id.ToUpperInvariant(), MixCompare.Active(store?.Project ?? new Project()).ToUpperInvariant()));
            });
            seg.Children.Add(b);
        }
        var box = new Border { Classes = { "seg" }, Child = seg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        box.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (store == null) return;
            string on = MixCompare.Active(store.Project).ToUpperInvariant(), off = on == "A" ? "B" : "A";
            bool other = MixCompare.HasOther(store.Project), can = !store.ReadOnly && !restoring;
            var menu = Popup(new List<object>
            {
                MenuAction(Loc.T("mix.ab.copy", on, off), () => { EditMix(MixCompare.CopyToOther, Loc.T("mix.ab.copied", on, off)); return Task.CompletedTask; },
                    enabled: can, icon: ""),
                MenuAction(Loc.T("mix.ab.forget", off), () => { EditMix(MixCompare.Forget, Loc.T("mix.ab.forgot", off)); return Task.CompletedTask; },
                    enabled: can && other, icon: "", tip: Loc.T("mix.ab.forget.tip", on)),
            }, 240);
            menu.Open(box);
        };
        return box;
    }

    void RefreshMixCompare()
    {
        foreach (var r in headerRefresh) r();
    }

    /// <summary>Switches to the other mix and hears it right away.</summary>
    void SwitchMix()
    {
        if (store == null || store.ReadOnly || restoring) return;
        undo.Checkpoint(store.Project);
        string now = MixCompare.Switch(store.Project);
        ApplyMix();
        Status(Loc.T("mix.ab.now", now.ToUpperInvariant()));
    }

    /// <summary>An A/B edit that doesn't change what sounds (copy, forget): one undo step and the header.</summary>
    void EditMix(Action<Project> edit, string done)
    {
        if (store == null || store.ReadOnly || restoring) return;
        undo.Checkpoint(store.Project);
        edit(store.Project);
        MarkDirty();
        UpdateUndoButtons();
        RefreshMixCompare();
        Status(done);
    }

    /// <summary>Hands the engine the project's mix as it is now: every channel and the buses.</summary>
    void ApplyMix()
    {
        if (store == null) return;
        foreach (var t in tracks) t.ApplyChannel();
        ApplyRouting(store.Project, tracks);
        RefreshMixer();
        if (sel != null)
        {
            RebuildInspectorChain(sel);
            RebuildInspectorSends(sel);
        }
        RefreshInspectorText();
        Timeline.InvalidateVisual();
        MarkDirty();
        UpdateUndoButtons();
        RefreshMixCompare();
    }
}
