using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using GuitarCam.App.Controls;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// UI themes, port of GuitarCam/src/theme.ts: always dark, an accent color plus a surface palette (the greys,
/// tinted). Applied live to the App.axaml brushes, to <see cref="Palette"/> and to the Fluent accent colors.
/// Same as the web except it adds nine accent presets and three dark surface palettes, and is remembered
/// in the program's config ("theme") instead of localStorage.
/// </summary>
static class UiTheme
{
    public sealed record Surface(string Id, string Name, Color App, Color Panel, Color Raised, Color Hover, Color Line, Color Fg, Color Dim, Color Faint);

    public sealed record Settings(string Accent, string Surface);

    static Surface S(string id, string name, params string[] c) => new(id, name,
        Color.Parse(c[0]), Color.Parse(c[1]), Color.Parse(c[2]), Color.Parse(c[3]), Color.Parse(c[4]), Color.Parse(c[5]), Color.Parse(c[6]), Color.Parse(c[7]));

    public static readonly Surface[] Surfaces =
    [
        S("neutral", "Grafito", "#0b0b0d", "#131316", "#1c1c20", "#26262b", "#27272c", "#ececef", "#a1a1aa", "#6b6b74"),
        S("slate", "Pizarra", "#0a0c10", "#11151b", "#191e26", "#222833", "#232a35", "#e8edf3", "#9aa6b5", "#64707f"),
        S("warm", "Madera", "#0d0b0a", "#161311", "#201c19", "#2a2521", "#2b2622", "#efebe6", "#ada49a", "#756c63"),
        S("forest", "Bosque", "#090c0b", "#111614", "#1a201d", "#232b27", "#242c28", "#e8efeb", "#9cada5", "#64736c"),
        S("plum", "Ciruela", "#0c0a0e", "#151218", "#1e1a22", "#28232d", "#29242f", "#eeeaf1", "#a69eae", "#6e6677"),
        S("black", "Negro", "#000000", "#0a0a0b", "#141416", "#1e1e21", "#222225", "#ededef", "#a0a0a6", "#68686f"),
        S("ocean", "Océano", "#080c12", "#101720", "#18222e", "#202d3c", "#263445", "#e7eef7", "#9cacc1", "#687b93"),
        S("coffee", "Café", "#100b08", "#1a130e", "#261c15", "#32271e", "#382c22", "#f3ece3", "#b8a797", "#847161"),
        S("smoke", "Humo", "#101112", "#191b1d", "#232629", "#2d3135", "#34393e", "#eceff1", "#a8b0b7", "#747e87"),
    ];

    /// <summary>Sorted by hue, warm to cool, so neighbours in the picker look alike.</summary>
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Fuego", "#ff5809"), ("Naranja", "#fb923c"), ("Ámbar", "#f5a524"), ("Oro", "#facc15"), ("Lima", "#a3e635"),
        ("Verde", "#4ade80"), ("Esmeralda", "#34d399"), ("Turquesa", "#2dd4bf"), ("Menta", "#99f6e4"), ("Cyan", "#22d3ee"),
        ("Celeste", "#7dd3fc"), ("Azul", "#60a5fa"), ("Índigo", "#818cf8"), ("Violeta", "#a78bfa"), ("Lavanda", "#c4b5fd"),
        ("Fucsia", "#e879f9"), ("Rosa", "#f472b6"),
    ];

    public static readonly Settings Default = new("#22d3ee", "neutral");

    const string ConfigKey = "theme";

    public static Settings Current { get; private set; } = Default;
    static Task saving = Task.CompletedTask;

    /// <summary>Fired after a live change, so the window refreshes what it painted from the old colors.</summary>
    public static event Action? Changed;

    static bool IsHex(string? s) => s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    public static Settings Read()
    {
        try
        {
            if (AppConfig.Get(ConfigKey) is { } json && JsonSerializer.Deserialize<Settings>(json, ProjectStore.Json) is { } t
                && IsHex(t.Accent) && Surfaces.Any(s => s.Id == t.Surface))
                return t with { Accent = t.Accent.ToLowerInvariant() };
        }
        catch (JsonException) { }
        return Default;
    }

    static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    static Color Mix(Color c, Color to, double k) => Color.FromRgb(
        (byte)Math.Round(c.R + (to.R - c.R) * k), (byte)Math.Round(c.G + (to.G - c.G) * k), (byte)Math.Round(c.B + (to.B - c.B) * k));

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Applies the theme everywhere; <paramref name="persist"/> also remembers it (in the background).</summary>
    public static void Apply(Settings theme, bool persist = true)
    {
        var surface = Surfaces.FirstOrDefault(s => s.Id == theme.Surface) ?? Surfaces[0];
        var accent = IsHex(theme.Accent) ? Color.Parse(theme.Accent) : Color.Parse(Default.Accent);
        var onAccent = Luminance(accent) > 0.3 ? Color.Parse("#0b0b0d") : Colors.White;
        Current = theme;
        Palette.Set(surface, accent, onAccent);

        if (Application.Current is { } app)
        {
            var r = app.Resources;
            void Brush(string key, Color c)
            {
                if (r.TryGetResource(key, null, out var v) && v is SolidColorBrush b) b.Color = c;
            }
            (string, Color)[] tokens =
            [
                ("App", surface.App), ("Panel", surface.Panel), ("Raised", surface.Raised), ("Hover", surface.Hover), ("Line", surface.Line),
                ("Fg", surface.Fg), ("Dim", surface.Dim), ("Faint", surface.Faint), ("Accent", accent),
            ];
            foreach (var (key, c) in tokens)
            {
                Brush(key, c);
                r[key + "Color"] = c;
            }
            Brush("AccentSoft", WithAlpha(accent, 0x24));
            Brush("AccentFg", onAccent);
            Brush("AccentHover", Mix(accent, Colors.White, 0.2));
            Brush("AccentTint", WithAlpha(accent, 0x0f));
            Brush("Accent25", WithAlpha(accent, 0x40));
            Brush("Accent40", WithAlpha(accent, 0x66));
            Brush("Accent50", WithAlpha(accent, 0x80));
            Brush("Accent70", WithAlpha(accent, 0xb3));
            if (r.TryGetResource("HoverGlow", null, out var g) && g is DropShadowEffect glow) glow.Color = accent;
            // the Fluent controls (sliders, switches) read these as dynamic resources
            r["SystemAccentColor"] = accent;
            r["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.1);
            r["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.22);
            r["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.35);
            r["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.2);
            r["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.4);
            r["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.6);
        }

        if (persist)
        {
            string json = JsonSerializer.Serialize(theme, ProjectStore.Json);
            // in order, so the last pick is the one that stays
            saving = saving.ContinueWith(_ =>
            {
                try { AppConfig.Set(ConfigKey, json); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the theme still applies for this session */ }
            }, TaskScheduler.Default);
        }
        Changed?.Invoke();
    }
}
