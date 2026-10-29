namespace WildTrack.Audio;

/// <summary>
/// The program's own folder, %APPDATA%\WildTrack (settings, recent projects, presets, plugin list, latency, logs).
/// Until 2026-10-29 the app was "GuitarCam Studio" and kept it in %APPDATA%\GuitarCam Studio: the first time the
/// folder is asked for, what the old one has and the new one doesn't is copied over, then a mark is left so it
/// happens once. It merges instead of waiting for an empty folder because something may write there first (the test
/// script's sesiones.log), and each file goes to a temporary name and is then moved into place, so a copy cut halfway
/// is redone next time instead of leaving half a settings file. The old folder stays, so an older version still finds
/// its own. It lives here, in the lowest layer, because the latency store needs it too.
/// </summary>
public static class AppFolder
{
    public const string Name = "WildTrack";

    /// <summary>The folder's name before the rename.</summary>
    const string LegacyName = "GuitarCam Studio";

    /// <summary>Left in the folder once the old one was copied.</summary>
    const string MigratedMark = "migrado-de-guitarcam.txt";

    static readonly Lazy<string> dir = new(Resolve);

    public static string Dir => dir.Value;

    static string Resolve()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string path = Path.Combine(root, Name);
        string legacy = Path.Combine(root, LegacyName);
        if (!Directory.Exists(legacy) || File.Exists(Path.Combine(path, MigratedMark))) return path;
        try
        {
            CopyMissing(legacy, path);
            File.WriteAllText(Path.Combine(path, MigratedMark), $"Copiado de {legacy} el {DateTime.Now:yyyy-MM-dd HH:mm}.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Starting with default settings beats not starting: the old folder is untouched, the next run retries
        }
        return path;
    }

    static void CopyMissing(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
        {
            string dest = Path.Combine(to, Path.GetFileName(f));
            if (File.Exists(dest)) continue;
            string temp = dest + ".migrando";
            File.Copy(f, temp, true);
            File.Move(temp, dest);
        }
        foreach (string d in Directory.GetDirectories(from)) CopyMissing(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
