using System.Globalization;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// When edits reach guitarcam.json, chosen in Configuración → "Guardado" and kept in config.json ("save.mode",
/// "save.minutes"): right after each edit (the old behavior, the default), every few minutes, or only by hand
/// (Ctrl+S or the disk in the title bar). Takes, exports, closing and switching projects always save, whatever
/// the mode: losing a take is the worst thing that can happen.
/// </summary>
public static class SaveMode
{
    public const string OnEdit = "edit", Interval = "interval", Manual = "manual";
    /// <summary>Range and default of <see cref="Minutes"/>.</summary>
    public const int MinMinutes = 1, MaxMinutes = 30, DefaultMinutes = 5;

    public static string Mode { get; private set; } = OnEdit;
    public static int Minutes { get; private set; } = DefaultMinutes;

    /// <summary>The mode or the minutes changed: the window re-arms its save timer.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            string? m = AppConfig.Get("save.mode");
            Mode = m is Interval or Manual ? m : OnEdit;
            if (int.TryParse(AppConfig.Get("save.minutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int min))
                Minutes = Math.Clamp(min, MinMinutes, MaxMinutes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* defaults */ }
    }

    public static void Set(string mode, int minutes)
    {
        mode = mode is Interval or Manual ? mode : OnEdit;
        minutes = Math.Clamp(minutes, MinMinutes, MaxMinutes);
        if (mode == Mode && minutes == Minutes) return;
        Mode = mode;
        Minutes = minutes;
        Changed?.Invoke();
        _ = Task.Run(() =>
        {
            try
            {
                AppConfig.Set("save.mode", mode);
                AppConfig.Set("save.minutes", minutes.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
        });
    }
}
