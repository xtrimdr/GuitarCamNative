using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using GuitarCam.App.Controls;
using GuitarCam.Projects;

namespace GuitarCam.App.Dialogs;

/// <summary>
/// "Acerca de", opened from the logo in the title bar. Borrows SettingsDialog's frame (dimmed overlay, Escape or
/// a click outside closes it) with the splash's hero on top (<see cref="BrandArt"/>, asked by the user, in the
/// theme's accent). Shows the name, the version
/// (with the build's commit when the SDK embedded it) and what the app is built on, and opens the
/// program's settings folder.
/// </summary>
public sealed partial class AboutDialog : UserControl
{
    public event Action? Dismissed;

    public AboutDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => HideDialog();
        PointerPressed += (_, e) => { if (e.Source == this) HideDialog(); };
        SettingsFolderButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppConfig.Dir);
            Explorer.Open(AppConfig.Dir);
        };
        UpdateButton.Click += (_, _) => Explorer.Browse(updateUrl);
        UpdateCheckBox.IsChecked = UpdateCheck.Enabled;
        UpdateCheckBox.IsCheckedChanged += (_, _) =>
        {
            bool on = UpdateCheckBox.IsChecked == true;
            try { UpdateCheck.Enabled = on; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Preference only. */ }
            UpdateToggled?.Invoke(on);
        };
        SetUpdate(UpdateCheck.Enabled ? UpdateState.Looking : UpdateState.Off);
        VersionText.Text = Loc.T("about.version", AppVersion());
        Logo.Source = BrandArt.Logo;
        if (TestBuild.Label != null)
        {
            TestChip.IsVisible = true;
            TestText.Text = Loc.T("splash.test");
        }
        AudioText.Text = $"ASIO · NAudio {Short(typeof(NAudio.Wave.WaveFormat).Assembly)}";
        UiText.Text = $"Avalonia {Short(typeof(Avalonia.Application).Assembly)} · .NET {Environment.Version.ToString(3)}";
    }

    /// <summary>The FFmpeg check runs at startup (<see cref="MainWindow"/>); null = not on the PATH.</summary>
    public void SetFfmpeg(string? version) =>
        VideoText.Text = version == null ? Loc.T("title.ffmpeg.missing") : version.Length > 0 ? Loc.T("about.ffmpeg.version", version) : Loc.T("about.ffmpeg");

    /// <summary>"0.9.6 · 9395e4a": the informational version (Directory.Build.props), with the commit the SDK appends after '+' shortened.</summary>
    public static string AppVersion()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        int plus = info.IndexOf('+');
        if (plus < 0) return info;
        string commit = info[(plus + 1)..];
        return $"{info[..plus]} · {commit[..Math.Min(7, commit.Length)]}";
    }

    public enum UpdateState { Off, Looking, Unknown, Latest, New }

    /// <summary>The "Avisarme…" box changed; on → MainWindow checks now.</summary>
    public event Action<bool>? UpdateToggled;

    string updateUrl = UpdateCheck.ReleasesUrl;

    /// <summary>What the update row says; <paramref name="latest"/> is the release found (for New, its version and page).</summary>
    public void SetUpdate(UpdateState state, UpdateCheck.Release? latest = null)
    {
        if (latest != null) updateUrl = latest.Url;
        UpdateText.Text = state switch
        {
            UpdateState.Off => Loc.T("about.update.off"),
            UpdateState.Looking => Loc.T("about.update.looking"),
            UpdateState.Unknown => Loc.T("about.update.unknown"),
            UpdateState.New => Loc.T("about.update.new", latest?.Version.ToString(3) ?? "?"),
            _ => Loc.T("about.update.latest"),
        };
        UpdateText.Foreground = state == UpdateState.New ? Palette.Accent : Palette.Dim;
        UpdateButton.IsVisible = state == UpdateState.New;
    }

    static string Short(Assembly a) => a.GetName().Version?.ToString(3) ?? "?";

    public void Show()
    {
        // rebuilt each time: the theme may have changed the accent since the last time
        Glow.Fill = BrandArt.Glow();
        AccentLine.Background = BrandArt.Line();
        IsVisible = true;
        Focus();
    }

    public void HideDialog()
    {
        IsVisible = false;
        Dismissed?.Invoke();
    }

    /// <summary>Escape closes; every other key stays here while it is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) HideDialog();
        e.Handled = true;
        return true;
    }
}
