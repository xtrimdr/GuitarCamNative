using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GuitarCam.App;

/// <summary>
/// "Mostrar en el Explorador": opens a folder (or shows a file selected) and brings that Explorer window
/// in front, like openInExplorer + ForegroundWindow.cs of the web server. The request goes straight to the
/// running shell (ShellExecute / SHOpenFolderAndSelectItems, about 0.3 s) instead of starting explorer.exe,
/// which only forwards it and takes 1 s or more. The shell isn't the foreground process, so the window can
/// open behind the app: being the foreground process, we first let any process take the focus, then look
/// for the new window for a moment and raise it with the same thread-input trick as the web.
/// All of it runs on a background thread, so the window never waits for the shell.
/// </summary>
public static class Explorer
{
    /// <summary>How long to wait for Explorer's window to show up.</summary>
    static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(3);

    public static void Open(string folder) => Launch(() =>
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" })?.Dispose(),
        $"\"{folder}\"");

    /// <summary>Opens a web page in the default browser; a failure (no browser set) is ignored.</summary>
    public static void Browse(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public static void Select(string file) => Launch(() =>
    {
        // with no child items, the shell opens the item's folder with the item selected
        Marshal.ThrowExceptionForHR(SHParseDisplayName(file, IntPtr.Zero, out var pidl, 0, out _));
        try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, null, 0)); }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }, $"/select,\"{file}\"");

    static void Launch(Action open, string fallbackArgs)
    {
        AllowSetForegroundWindow(AsfwAny);
        Task.Run(async () =>
        {
            var before = ExplorerWindows();
            try { open(); }
            catch
            {
                try { Process.Start("explorer.exe", fallbackArgs)?.Dispose(); }
                catch { return; }
            }
            var deadline = DateTime.UtcNow + WindowWait;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                // EnumWindows goes top to bottom, so the first new one is the one just opened
                var fresh = ExplorerWindows().FirstOrDefault(h => !before.Contains(h));
                if (fresh != IntPtr.Zero)
                {
                    Bring(fresh);
                    return;
                }
            }
        });
    }

    /// <summary>Top-level File Explorer windows, in z-order.</summary>
    static List<IntPtr> ExplorerWindows()
    {
        var list = new List<IntPtr>();
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            cls.Clear();
            if (IsWindowVisible(h) && GetClassName(h, cls, cls.Capacity) > 0 && cls.ToString() == "CabinetWClass") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Port of GuitarCamForeground.Bring (GuitarCam/server/ForegroundWindow.cs).</summary>
    static void Bring(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
        var fg = GetForegroundWindow();
        if (fg == hWnd) return;
        // sharing the input state of the foreground window's thread lifts the focus lock
        uint me = GetCurrentThreadId(), fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    const int AsfwAny = -1, SwRestore = 9;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint flags);
}
