using GuitarCam.App.Dialogs;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// "¿Hay una versión nueva?": a few seconds after the window opens it asks GitHub for the latest release
/// (<see cref="UpdateCheck"/>, in the background) and "Acerca de" shows the answer. A newer version is announced
/// with a toast only the first time it is seen (remembered in config.json, "updateNotified"); later launches just
/// leave it in the bell, so it never nags. Turning the check off or on is the box in "Acerca de".
/// </summary>
public partial class MainWindow
{
    /// <summary>Lets startup (project, driver, camera, FFmpeg probe) settle before touching the network.</summary>
    static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(5);

    bool updateChecking;

    void InitUpdates()
    {
        About.UpdateToggled += on =>
        {
            if (on) _ = CheckUpdatesAsync(announce: false);
            else About.SetUpdate(AboutDialog.UpdateState.Off);
        };
        Opened += async (_, _) =>
        {
            if (!UpdateCheck.Enabled) return;
            await Task.Delay(UpdateCheckDelay);
            await CheckUpdatesAsync(announce: true);
        };
    }

    async Task CheckUpdatesAsync(bool announce)
    {
        if (updateChecking) return;
        updateChecking = true;
        try
        {
            About.SetUpdate(AboutDialog.UpdateState.Looking);
            var latest = await UpdateCheck.LatestAsync();
            if (!UpdateCheck.Enabled) { About.SetUpdate(AboutDialog.UpdateState.Off); return; }
            if (latest == null) { About.SetUpdate(AboutDialog.UpdateState.Unknown); return; }
            if (!UpdateCheck.IsNewer(latest)) { About.SetUpdate(AboutDialog.UpdateState.Latest, latest); return; }
            About.SetUpdate(AboutDialog.UpdateState.New, latest);
            if (!announce) return;
            string v = latest.Version.ToString(3);
            if (AppConfig.Get("updateNotified") == latest.Tag)
            {
                Status(Loc.T("update.bell", v));
                return;
            }
            Toast(Loc.T("update.toast", v, UpdateCheck.Current.ToString(3)), Loc.T("update.toast.title"), sticky: true,
                action: (Loc.T("about.update.download"), () => Explorer.Browse(latest.Url)));
            try { AppConfig.Set("updateNotified", latest.Tag); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Shown again next time, that's all. */ }
        }
        finally { updateChecking = false; }
    }
}
