using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using GuitarCam.Projects;

namespace GuitarCam.App.Dialogs;

/// <summary>
/// "Acerca de", opened from the logo in the title bar. Native only (the web app has none), so it borrows
/// SettingsDialog's frame: dimmed overlay, Escape or a click outside closes it. Shows the name, the version
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
        VersionText.Text = $"Versión {AppVersion()}";
        AudioText.Text = $"ASIO · NAudio {Short(typeof(NAudio.Wave.WaveFormat).Assembly)}";
        UiText.Text = $"Avalonia {Short(typeof(Avalonia.Application).Assembly)} · .NET {Environment.Version.ToString(3)}";
    }

    /// <summary>The FFmpeg check runs at startup (<see cref="MainWindow"/>); null = not on the PATH.</summary>
    public void SetFfmpeg(string? version) =>
        VideoText.Text = version == null ? "FFmpeg no encontrado" : version.Length > 0 ? $"FFmpeg {version} (del PATH)" : "FFmpeg (del PATH)";

    /// <summary>"1.0.0 · 9395e4a": the informational version, with the commit the SDK appends after '+' shortened.</summary>
    static string AppVersion()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        int plus = info.IndexOf('+');
        if (plus < 0) return info;
        string commit = info[(plus + 1)..];
        return $"{info[..plus]} · {commit[..Math.Min(7, commit.Length)]}";
    }

    static string Short(Assembly a) => a.GetName().Version?.ToString(3) ?? "?";

    public void Show()
    {
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
