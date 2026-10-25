using System.Diagnostics;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// Diagnostic for windows that close on their own: appends to <c>%APPDATA%\GuitarCam Studio\sesiones.log</c> when the
/// program starts, why the window closes (the user, Windows, or the code, with where from) and every unhandled
/// error, so a close without a crash report can be explained afterwards. Never throws: logging must not be what fails.
/// </summary>
static class SessionLog
{
    /// <summary>Past this size the log starts over, so it never grows without bound.</summary>
    const long MaxBytes = 512 * 1024;

    static readonly string LogPath = Path.Combine(AppConfig.Dir, "sesiones.log");
    static readonly object gate = new();

    public static void Start(string[] args)
    {
        Write($"inicio  pid {Environment.ProcessId}  exe {Environment.ProcessPath}  args [{string.Join(" ", args)}]");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"error sin manejar (cierra: {e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Write($"error en tarea: {e.Exception}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write($"fin  código {Environment.ExitCode}");
    }

    public static void Write(string text)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(AppConfig.Dir);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes) info.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Diagnostic only. */ }
    }

    /// <summary>The stack of whoever asked to close, so a programmatic close says where it came from.</summary>
    public static string Caller() => new StackTrace(2, false).ToString().Trim();
}
