using Avalonia.Threading;
using GuitarCam.Audio.Vst;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The window's side of <see cref="PluginCatalog"/>: what was found, and the one scan that may be running, shared by
/// Configuración → Plugins and the MIDI track's "Instrumento" menu so both see the same list and never scan twice at
/// once. Everything here lives on the UI thread; the scan itself runs in the background and reports back with
/// <see cref="Changed"/>.
/// </summary>
public static class PluginScan
{
    static List<PluginCatalog.Entry>? entries;
    static CancellationTokenSource? cancel;

    /// <summary>Something changed: progress of a scan, a scan or retry that ended, a plugin hidden or shown.</summary>
    public static event Action? Changed;

    public static List<PluginCatalog.Entry> Entries => entries ??= PluginCatalog.Load();
    public static bool Running { get; private set; }
    /// <summary>The file being opened now, its number and how many will be opened (while <see cref="Running"/>).</summary>
    public static (string Name, int N, int Total)? Progress { get; private set; }

    /// <summary>Some file was opened by an older scan, that didn't ask what each plugin takes: the next scan opens it
    /// again to tell its instruments from its effects (<see cref="PluginCatalog.CurrentFormat"/>).</summary>
    public static bool Outdated => Entries.Any(e => e.Error == null && e.Format < PluginCatalog.CurrentFormat);

    /// <summary>The plugins the menus offer (not the hidden ones).</summary>
    public static List<VstPluginInfo> Visible => PluginCatalog.Visible(Entries);

    /// <summary>
    /// Looks for plugins in the background (<paramref name="all"/>: opens every file again, not only new or changed
    /// ones) or, with <paramref name="file"/>, opens that one again. Null if another scan is running or it failed
    /// (<paramref name="error"/> gets why).
    /// </summary>
    public static async Task<List<PluginCatalog.Entry>?> Run(bool all = false, string? file = null, Action<string>? error = null)
    {
        if (Running) return null;
        Running = true;
        Progress = null;
        var cts = cancel = new CancellationTokenSource();
        Changed?.Invoke();
        try
        {
            var result = await Task.Run(() => file != null ? PluginCatalog.Rescan(file)
                : PluginCatalog.Scan((name, n, total) => Dispatcher.UIThread.Post(() =>
                {
                    if (cancel != cts) return;
                    Progress = (name, n, total);
                    Changed?.Invoke();
                }), all: all, cancel: cts.Token));
            entries = result;
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error?.Invoke(e.Message);
            return null;
        }
        finally
        {
            Running = false;
            Progress = null;
            cancel = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Stops the scan after the file it is opening (what it found so far is kept).</summary>
    public static void Cancel() => cancel?.Cancel();

    public static void SetHidden(VstPluginInfo p, bool hidden)
    {
        try { PluginCatalog.SetHidden(p, hidden); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Changed?.Invoke();
    }

    /// <summary>The folders changed: nothing to reload until the next scan, but the page shows them again.</summary>
    public static void FoldersChanged() => Changed?.Invoke();
}
