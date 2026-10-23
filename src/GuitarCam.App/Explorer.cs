using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GuitarCam.App;

/// <summary>
/// "Mostrar en el Explorador": opens a folder (or shows a file selected) and brings that Explorer window
/// in front, like openInExplorer + ForegroundWindow.cs of the web server. explorer.exe hands the request
/// to the running shell, which isn't the foreground process, so the window can open behind the app.
/// Being the foreground process, we first let any process take the focus; then we look for the new
/// window for a moment (on a background thread) and raise it with the same thread-input trick as the web.
/// </summary>
public static class Explorer
{
    /// <summary>How long to wait for Explorer's window to show up.</summary>
    static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(3);

    public static void Open(string folder) => Launch($"\"{folder}\"");

    public static void Select(string file) => Launch($"/select,\"{file}\"");

    static void Launch(string args)
    {
        var before = ExplorerWindows();
        AllowSetForegroundWindow(AsfwAny);
        Process.Start("explorer.exe", args);
        Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + WindowWait;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
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
}
