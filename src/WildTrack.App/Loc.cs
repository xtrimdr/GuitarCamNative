using System.Globalization;
using System.Text.Json;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using WildTrack.Audio;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// Interface texts by key. Each language is a flat JSON in Assets/Lang (embedded); the current one is read once at
/// startup, so changing it in Configuración asks for a restart instead of repainting every control, including the
/// hand-drawn ones. A key missing from a language falls back to Spanish (the reference), and a key missing from
/// both shows the key itself, so a forgotten string is visible but never blank. The choice is stored in
/// config.json ("language"); the first run follows the system language. Values saved in projects are not texts
/// and never go through here.
/// </summary>
public static class Loc
{
    /// <summary>Language codes with their own name, in the order Configuración lists them.</summary>
    public static readonly (string Code, string Name)[] Languages = [("es", "Español"), ("en", "English")];

    const string DefaultLanguage = "es";
    const string ConfigKey = "language";

    static Dictionary<string, string> current = [];
    static Dictionary<string, string> fallback = [];

    public static string Language { get; private set; } = DefaultLanguage;

    /// <summary>Loads the stored language (or the system's) before any window is built.</summary>
    public static void Load()
    {
        string? saved = null;
        try { saved = AppConfig.Get(ConfigKey); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* use the system language */ }
        saved ??= CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Language = Languages.Any(l => l.Code == saved) ? saved! : DefaultLanguage;
        fallback = Read(DefaultLanguage);
        current = Language == DefaultLanguage ? fallback : Read(Language);
        Culture = CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "es-AR");
        Texts.Culture = Culture;
        Texts.Translate = (key, args) => Has(key) ? (args.Length == 0 ? T(key) : T(key, args)) : null;
    }

    /// <summary>Stores the language for the next start; the running window keeps the one it loaded.</summary>
    public static void Choose(string code)
    {
        if (Languages.All(l => l.Code != code)) return;
        try { AppConfig.Set(ConfigKey, code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
    }

    /// <summary>Culture of the language, for day and month names.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("es-AR");

    public static bool Has(string key) => current.ContainsKey(key) || fallback.ContainsKey(key);

    /// <summary>The text for the key, or <paramref name="fallback"/> when no language has it (names that come from the engine).</summary>
    public static string Or(string key, string fallbackText) => Has(key) ? T(key) : fallbackText;

    public static string T(string key) => current.TryGetValue(key, out var s) || fallback.TryGetValue(key, out s) ? s : key;

    public static string T(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    static Dictionary<string, string> Read(string code)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://WildTrack/Assets/Lang/{code}.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or FileNotFoundException)
        {
            return [];
        }
    }
}

/// <summary><c>Text="{loc:T settings.title}"</c> in XAML: the same as <see cref="Loc.T(string)"/>.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(key);
}
