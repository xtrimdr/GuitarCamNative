using System.Globalization;

namespace GuitarCam.App;

/// <summary>
/// Which build this is when it runs from a copy made by <c>tools\probar.ps1</c>: that script leaves a
/// <c>prueba.txt</c> next to the exe (commit, "dirty" or "clean", build time) so the title bar can say exactly what
/// is being tried. Builds without the file (bin\, the installer) show nothing.
/// </summary>
static class TestBuild
{
    /// <summary>Short text for the title bar chip ("a1b2c3d + cambios"), or null outside a test copy.</summary>
    public static readonly string? Label;

    /// <summary>Longer text for the chip's tooltip: when it was built and whether it has uncommitted changes.</summary>
    public static readonly string? Detail;

    static TestBuild()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "prueba.txt");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            if (lines.Length < 3) return;
            bool dirty = lines[1].Trim() == "dirty";
            string commit = lines[0].Trim();
            string when = DateTime.TryParse(lines[2].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t.ToString("g", CultureInfo.CurrentCulture) : lines[2].Trim();
            Label = dirty ? Loc.T("title.test.dirty", commit) : commit;
            Detail = Loc.T(dirty ? "title.test.tip.dirty" : "title.test.tip", commit, when);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
