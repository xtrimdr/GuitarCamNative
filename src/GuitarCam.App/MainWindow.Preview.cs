using Avalonia.Controls.Primitives;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The stage's format bar: "Lado a lado" / "Apilado" and 16:9 / 9:16 / 1:1, like the two Segmented
/// controls above SplitScreenCanvas in App.tsx (changeLayout / changeAspectRatio: each change is its own
/// undo step, and picking 9:16 stacks the panels while 16:9 puts them side by side).
/// </summary>
public partial class MainWindow
{
    void InitPreview()
    {
        Preview.SelectedTrack = () => sel;
        Timeline.SelectionChanged += _ => Preview.InvalidateVisual();
        LayoutSideButton.Click += (_, _) => ChangeFormat(s => s.SetLayout("side-by-side"));
        LayoutStackButton.Click += (_, _) => ChangeFormat(s => s.SetLayout("top-to-bottom"));
        Aspect169Button.Click += (_, _) => ChangeFormat(s => s.SetAspect("16:9"));
        Aspect916Button.Click += (_, _) => ChangeFormat(s => s.SetAspect("9:16"));
        Aspect11Button.Click += (_, _) => ChangeFormat(s => s.SetAspect("1:1"));
        RefreshFormat();
    }

    void ChangeFormat(Action<ProjectSettings> change)
    {
        if (store != null && !store.ReadOnly && !restoring && !Projects.IsVisible)
        {
            var s = store.Project.Settings;
            var probe = new ProjectSettings { Layout = s.Layout, Aspect = s.Aspect };
            change(probe);
            if (probe.Layout != s.Layout || probe.Aspect != s.Aspect)
            {
                undo.Checkpoint(store.Project);
                change(s);
                MarkDirty();
                UpdateUndoButtons();
                Preview.SetProject(tracks, s);
            }
        }
        // a click on the checked option would uncheck it: always show the project's format again
        RefreshFormat();
    }

    void RefreshFormat()
    {
        var s = store?.Project.Settings;
        bool stacked = s?.Layout == "top-to-bottom";
        Set(LayoutSideButton, s != null && !stacked);
        Set(LayoutStackButton, stacked);
        Set(Aspect169Button, s?.Aspect == "16:9");
        Set(Aspect916Button, s?.Aspect == "9:16");
        Set(Aspect11Button, s?.Aspect == "1:1");
        bool editable = store?.ReadOnly == false;
        LayoutSideButton.IsEnabled = LayoutStackButton.IsEnabled = editable;
        Aspect169Button.IsEnabled = Aspect916Button.IsEnabled = Aspect11Button.IsEnabled = editable;
        static void Set(ToggleButton b, bool on) => b.IsChecked = on;
    }
}
