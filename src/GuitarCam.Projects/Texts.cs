using System.Globalization;

namespace GuitarCam.Projects;

/// <summary>
/// Hook for the few texts this layer builds itself (the Actividad lines, day names, "hace 5 min"): it has no UI,
/// so it cannot reach the app's language files. The window sets <see cref="Translate"/> and <see cref="Culture"/>;
/// without them (gclab, tests) the Spanish text passed along is used, so the output stays what it always was.
/// </summary>
public static class Texts
{
    /// <summary>Returns the text for a key in the current language with its arguments applied, or null if there is none.</summary>
    public static Func<string, object?[], string?>? Translate { get; set; }

    /// <summary>Culture for day and month names.</summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("es-AR");

    /// <param name="es">The Spanish text, a composite format when there are arguments.</param>
    public static string T(string key, string es, params object?[] args) =>
        Translate?.Invoke(key, args) ?? (args.Length == 0 ? es : string.Format(CultureInfo.CurrentCulture, es, args));
}
