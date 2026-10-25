using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace GuitarCam.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Loc.Load();
        UiTheme.Apply(UiTheme.Read(), persist: false);
        Shortcuts.Load();
        SaveMode.Load();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (dir, note) = ParseArgs(desktop.Args ?? []);
            desktop.MainWindow = new MainWindow(dir, note);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// <c>GuitarCam.exe [carpeta] [--probar "texto"]</c>: the project folder, and what to try in this build (the
    /// agent passes it after each change; it is shown as a toast and not saved anywhere).
    /// </summary>
    static (string? Dir, string? TestNote) ParseArgs(string[] args)
    {
        string? dir = null, note = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--probar") { if (i + 1 < args.Length) note = args[++i]; }
            else dir ??= args[i];
        }
        return (dir, string.IsNullOrWhiteSpace(note) ? null : note);
    }
}
