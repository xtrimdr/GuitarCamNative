using Avalonia.Controls;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// The project chip's menu (asked by the user): the projects manager, and "Volver a una versión guardada" with the
/// snapshots of historial/ (<see cref="ProjectStore.Snapshots"/>), which before could only be restored from wtlab; the
/// same versions, with their names, are in Historial → Versiones (MainWindow.Steps.cs), which goes back through here.
/// Going back saves the current state as one more snapshot first, so it is undone from the same list; the undo
/// steps are dropped, since they belong to the state that was left.
/// </summary>
public partial class MainWindow
{
    /// <summary>Newest snapshots listed: older ones stay in historial/ and in wtlab.</summary>
    const int MaxVersionsShown = 20;

    async Task ShowSessionMenu()
    {
        // with no project open there is nothing to go back to: straight to the manager, as before
        if (store == null) { await ShowProjects(); return; }
        if (PressClosedMenuOf(SessionChip)) return;
        var st = store;
        var snapshots = ProjectStore.Snapshots(st.Dir).Take(MaxVersionsShown).ToList();
        bool canRestore = !st.ReadOnly && !restoring && !RecordingBusy && !Export.IsRunning;
        var versions = new MenuItem
        {
            Header = new TextBlock { Text = Loc.T("ver.title"), FontSize = 12 },
            IsEnabled = canRestore && snapshots.Count > 0,
            ItemsSource = snapshots.Select(s => MenuAction(s.Name is { } name ? $"{VersionTime(s.Time)} · {name}" : VersionTime(s.Time),
                () => RestoreVersion(st, s), hint: s.Time.ToString("dd/MM/yyyy HH:mm:ss"))).ToList(),
        };
        ToolTip.SetTip(versions, snapshots.Count == 0 ? Loc.T("ver.none") : st.ReadOnly ? st.ReadOnlyReason : Loc.T("ver.tip"));
        ToolTip.SetShowDelay(versions, (int)MenuTipDelay.TotalMilliseconds);
        OpenAnchoredMenu(Popup([MenuAction(Loc.T("ver.projects"), ShowProjects, icon: ""), new Separator(), versions], 260), SessionChip);
    }

    /// <summary>"recién", "hace 5 min", "hoy 14:30", "ayer 09:10", dd/MM HH:mm, like the rest of the window.</summary>
    static string VersionTime(DateTime time)
    {
        var now = DateTime.Now;
        double minutes = Math.Max(0, (now - time).TotalMinutes);
        if (minutes < 1) return Loc.T("hist.ago.now");
        if (minutes < 60) return Loc.T("hist.ago.min", (int)minutes);
        if (time.Date == now.Date) return Loc.T("ver.today", time.ToString("HH:mm"));
        if (time.Date == now.Date.AddDays(-1)) return Loc.T("ver.yesterday", time.ToString("HH:mm"));
        return time.ToString("dd/MM HH:mm");
    }

    async Task RestoreVersion(ProjectStore st, ProjectStore.Snapshot s)
    {
        if (st != store || st.ReadOnly || restoring || RecordingBusy || Export.IsRunning) return;
        try
        {
            await FlushProjectOperation();
            engine.Stop();
            st.Restore(s);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Toast(Loc.T("ver.fail", e.Message));
            return;
        }
        undo.Clear();
        ResetSteps(s.Name is { } named ? Loc.T("steps.origin.restoredNamed", named) : Loc.T("steps.origin.restored", VersionTime(s.Time)));
        await ShowProject(st);
        ResetMaster();
        UpdateUndoButtons();
        RefreshHistory();
        ReloadVersions();
        Toast(s.Name is { } name ? Loc.T("ver.restoredNamed", name) : Loc.T("ver.restored", VersionTime(s.Time)));
    }
}
