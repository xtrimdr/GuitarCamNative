using GuitarCam.App.Controls;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// Which effect knobs the timeline's track headers show (asked by the user: all by default, and you turn off the ones
/// you know you won't use). Chosen in Configuración → "Vista" and kept in config.json ("timeline.hiddenKnobs") as the
/// hidden ones, so a knob added in a later version shows up without touching anyone's settings.
/// </summary>
public static class TrackKnobs
{
    /// <summary>Every knob, in the order the header shows them (<see cref="FxIcon"/> keys).</summary>
    public static readonly string[] All = [FxIcon.Sat, FxIcon.Comp, FxIcon.Reverb, FxIcon.Delay, FxIcon.Transpose];

    static readonly HashSet<string> hidden = [];

    /// <summary>The knobs shown, in header order.</summary>
    public static string[] Visible { get; private set; } = All;

    /// <summary>A knob was shown or hidden: the timeline relays its headers.</summary>
    public static event Action? Changed;

    public static bool Shows(string key) => !hidden.Contains(key);

    public static void Load()
    {
        try
        {
            foreach (string key in (AppConfig.Get("timeline.hiddenKnobs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (All.Contains(key)) hidden.Add(key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* all of them */ }
        Visible = [.. All.Where(Shows)];
    }

    public static void Set(string key, bool show)
    {
        if (!All.Contains(key) || show == Shows(key)) return;
        if (show) hidden.Remove(key); else hidden.Add(key);
        Visible = [.. All.Where(Shows)];
        Changed?.Invoke();
        string value = string.Join(",", All.Where(hidden.Contains));
        _ = Task.Run(() =>
        {
            try { AppConfig.Set("timeline.hiddenKnobs", value); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
        });
    }
}
