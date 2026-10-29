using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GuitarCam.App.Controls;
using GuitarCam.Audio;

namespace GuitarCam.App.Dialogs;

/// <summary>
/// "Falta FFmpeg" (asked by the user): the installer doesn't carry ffmpeg.exe, so on a PC without it this says what it
/// is for and offers to install it right here with winget (<see cref="FfmpegSetup"/>), to open the download page or to
/// look again after installing it by hand. Same frame as <see cref="UnsavedDialog"/>. Closing it while winget works
/// doesn't stop the install: <see cref="Found"/> still comes when FFmpeg answers, and <see cref="Failed"/> says why not.
/// </summary>
public sealed partial class FfmpegDialog : UserControl
{
    bool? winget;
    bool busy;

    /// <summary>FFmpeg answers now (installed here or found after a manual install); carries its version.</summary>
    public event Action<string>? Found;
    /// <summary>The install ended without FFmpeg; carries the message for the status bell.</summary>
    public event Action<string>? Failed;

    public FfmpegDialog()
    {
        InitializeComponent();
        CommandText.Text = FfmpegSetup.WingetCommand;
        InstallButton.Click += (_, _) => Install();
        PageButton.Click += (_, _) => Explorer.Browse(FfmpegSetup.DownloadUrl);
        RecheckButton.Click += (_, _) => Recheck();
        LaterButton.Click += (_, _) => IsVisible = false;
        CopyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip) await clip.SetTextAsync(FfmpegSetup.WingetCommand);
            Say(Loc.T("ffmpeg.copied"), false);
        };
        PointerPressed += (_, e) => { if (e.Source == this && !busy) IsVisible = false; };
    }

    /// <summary>Opens the dialog; the first time it looks for winget, which decides what it offers.</summary>
    public async void Show()
    {
        IsVisible = true;
        if (!busy) StateText.IsVisible = false;
        if (winget == null)
        {
            HowText.Text = Loc.T("ffmpeg.how.looking");
            InstallButton.IsEnabled = false;
            winget = await Task.Run(FfmpegSetup.HasWinget);
        }
        HowText.Text = Loc.T(winget == true ? "ffmpeg.how.winget" : "ffmpeg.how.page");
        InstallButton.IsVisible = winget == true;
        InstallButton.IsEnabled = !busy;
        (winget == true ? InstallButton : PageButton).Focus();
    }

    async void Install()
    {
        if (busy) return;
        busy = true;
        InstallButton.IsEnabled = RecheckButton.IsEnabled = false;
        InstallButton.Content = Loc.T("ffmpeg.installing");
        Say(Loc.T("ffmpeg.installing.body"), false);
        FfmpegInstallResult r;
        try { r = await FfmpegSetup.InstallAsync(); }
        finally
        {
            busy = false;
            InstallButton.IsEnabled = RecheckButton.IsEnabled = true;
            InstallButton.Content = Loc.T("ffmpeg.install");
        }
        if (r.Ready)
        {
            await Done();
            return;
        }
        // winget said yes but this process still can't see it: a new PATH entry Windows hasn't published yet
        string msg = r.ExitCode == 0 ? Loc.T("ffmpeg.fail.restart") : Loc.T("ffmpeg.fail", LastLine(r.Log));
        Say(msg, true);
        Failed?.Invoke(msg);
    }

    async void Recheck()
    {
        FfmpegSetup.RefreshPath();
        VideoEncoders.Forget();
        if (await Task.Run(FfmpegInfo.Probe) != null) await Done();
        else Say(Loc.T("ffmpeg.still"), true);
    }

    async Task Done()
    {
        string version = await Task.Run(FfmpegInfo.Probe) ?? "";
        IsVisible = false;
        Found?.Invoke(version);
    }

    void Say(string text, bool bad)
    {
        StateText.Text = text;
        StateText.Classes.Set("dim", !bad);
        StateText.Foreground = bad ? Palette.Rec : null;
        StateText.IsVisible = true;
    }

    static string LastLine(string log)
    {
        int nl = log.LastIndexOf('\n');
        string line = nl >= 0 ? log[(nl + 1)..] : log;
        return line.Length > 0 ? line : "winget";
    }

    /// <summary>Enter takes the focused button (install by default), Escape closes; every other key stays here.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) IsVisible = false;
        else if (e.Key is Key.Enter or Key.Space)
        {
            if (e.Source is Button { IsEnabled: true } b) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (e.Source is not Button && winget == true) Install();
        }
        e.Handled = true;
        return true;
    }
}
