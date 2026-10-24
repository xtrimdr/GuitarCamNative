using System.Text.Json;
using Avalonia.Input;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// The keyboard shortcuts (native only, the web's are fixed in App.tsx onKey): one table of actions with their
/// default keys, the user's changes on top, remembered in the program's config ("shortcuts", only what differs
/// from the default). Tooltips read their key from here (<see cref="Tip"/>), so a change shows up everywhere.
/// Esc is not in the table: it always cancels (count-in, gesture, dialogs).
/// </summary>
static class Shortcuts
{
    public sealed record Action(string Id, string Group, string Name, KeyGesture[] Defaults);

    static KeyGesture G(Key k, KeyModifiers m = KeyModifiers.None) => new(k, m);

    public static readonly Action[] All =
    [
        new("play", Loc.T("sc.group.transport"), Loc.T("sc.play"), [G(Key.Space)]),
        new("toStart", Loc.T("sc.group.transport"), Loc.T("sc.toStart"), [G(Key.Home)]),
        new("record", Loc.T("sc.group.transport"), Loc.T("sc.record"), [G(Key.R, KeyModifiers.Shift), G(Key.Multiply)]),
        new("click", Loc.T("sc.group.transport"), Loc.T("sc.click"), [G(Key.C)]),
        new("countIn", Loc.T("sc.group.transport"), Loc.T("sc.countIn"), [G(Key.C, KeyModifiers.Shift)]),
        new("tap", Loc.T("sc.group.transport"), Loc.T("sc.tap"), [G(Key.T)]),
        new("tuner", Loc.T("sc.group.transport"), Loc.T("sc.tuner"), [G(Key.A)]),
        new("mute", Loc.T("sc.group.tracks"), Loc.T("sc.mute"), [G(Key.M)]),
        new("solo", Loc.T("sc.group.tracks"), Loc.T("sc.solo"), [G(Key.S)]),
        new("arm", Loc.T("sc.group.tracks"), Loc.T("sc.arm"), [G(Key.R)]),
        new("newTrack", Loc.T("sc.group.tracks"), Loc.T("sc.newTrack"), [G(Key.N, KeyModifiers.Control | KeyModifiers.Shift)]),
        new("delete", Loc.T("sc.group.editing"), Loc.T("sc.delete"), [G(Key.Delete)]),
        new("split", Loc.T("sc.group.editing"), Loc.T("sc.split"), [G(Key.X, KeyModifiers.Alt)]),
        new("duplicate", Loc.T("sc.group.editing"), Loc.T("sc.duplicate"), [G(Key.D)]),
        new("undo", Loc.T("sc.group.editing"), Loc.T("sc.undo"), [G(Key.Z, KeyModifiers.Control)]),
        new("redo", Loc.T("sc.group.editing"), Loc.T("sc.redo"), [G(Key.Y, KeyModifiers.Control), G(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)]),
        new("save", Loc.T("sc.group.editing"), Loc.T("sc.save"), [G(Key.S, KeyModifiers.Control)]),
        new("fit", Loc.T("sc.group.view"), Loc.T("sc.fit"), [G(Key.F)]),
        new("mixer", Loc.T("sc.group.view"), Loc.T("sc.mixer"), [G(Key.X)]),
        new("leftPanel", Loc.T("sc.group.view"), Loc.T("sc.leftPanel"), [G(Key.B)]),
        new("rightPanel", Loc.T("sc.group.view"), Loc.T("sc.rightPanel"), [G(Key.P)]),
    ];

    /// <summary>The keys the user changed; an empty array means "sin atajo".</summary>
    static readonly Dictionary<string, KeyGesture[]> custom = [];

    /// <summary>A shortcut changed: tooltips that show keys refresh.</summary>
    public static event System.Action? Changed;

    public static void Load()
    {
        custom.Clear();
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string[]>>(AppConfig.Get("shortcuts") ?? "{}") ?? [];
            foreach (var (id, keys) in saved)
                if (All.Any(a => a.Id == id))
                    custom[id] = keys.Select(Parse).OfType<KeyGesture>().ToArray();
        }
        catch (JsonException) { }
    }

    public static KeyGesture[] Keys(string id) =>
        custom.TryGetValue(id, out var k) ? k : All.First(a => a.Id == id).Defaults;

    public static bool IsDefault(string id) => !custom.ContainsKey(id);

    /// <summary>The action for a key press, or null. Modifiers have to match exactly (C is click, Shift+C the count-in).</summary>
    public static string? Find(KeyEventArgs e) => Find(e.Key, e.KeyModifiers);

    static string? Find(Key key, KeyModifiers mods)
    {
        mods &= KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;
        foreach (var a in All)
            if (Keys(a.Id).Any(g => g.Key == key && g.KeyModifiers == mods)) return a.Id;
        return null;
    }

    /// <summary>
    /// Gives an action one key; another action that had it loses it (its name comes back so the dialog can say so).
    /// Null leaves the action without a shortcut.
    /// </summary>
    public static string? Assign(string id, KeyGesture? gesture)
    {
        string? taken = null;
        if (gesture != null)
            foreach (var a in All)
            {
                if (a.Id == id) continue;
                var keys = Keys(a.Id);
                var left = keys.Where(g => !Same(g, gesture)).ToArray();
                if (left.Length == keys.Length) continue;
                SetKeys(a.Id, left);
                taken = a.Name;
            }
        SetKeys(id, gesture == null ? [] : [gesture]);
        Save();
        return taken;
    }

    public static void Reset(string id)
    {
        foreach (var g in All.First(a => a.Id == id).Defaults)
            if (Find(g.Key, g.KeyModifiers) is { } other && other != id)
                SetKeys(other, Keys(other).Where(k => !Same(k, g)).ToArray());
        custom.Remove(id);
        Save();
    }

    public static void ResetAll()
    {
        custom.Clear();
        Save();
    }

    static void SetKeys(string id, KeyGesture[] keys)
    {
        var defaults = All.First(a => a.Id == id).Defaults;
        if (keys.Length == defaults.Length && keys.Zip(defaults).All(p => Same(p.First, p.Second))) custom.Remove(id);
        else custom[id] = keys;
    }

    static bool Same(KeyGesture a, KeyGesture b) => a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;

    static void Save()
    {
        string json = JsonSerializer.Serialize(custom.ToDictionary(p => p.Key, p => p.Value.Select(g => g.ToString()).ToArray()));
        _ = Task.Run(() =>
        {
            try { AppConfig.Set("shortcuts", json); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { /* keep this session's keys */ }
        });
        Changed?.Invoke();
    }

    static KeyGesture? Parse(string s)
    {
        try { return KeyGesture.Parse(s); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>"Espacio", "Ctrl+Y o Ctrl+Shift+Z"; empty when the action has no key.</summary>
    public static string Label(string id) => string.Join(" o ", Keys(id).Select(Label));

    public static string Label(KeyGesture g)
    {
        var parts = new List<string>();
        if (g.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(g.Key));
        return string.Join("+", parts);
    }

    static string KeyName(Key k) => k switch
    {
        Key.Space => Loc.T("sc.key.space"), Key.Home => Loc.T("sc.key.home"), Key.End => Loc.T("sc.key.end"), Key.Delete => Loc.T("sc.key.del"), Key.Insert => "Insert",
        Key.Back => Loc.T("sc.key.back"), Key.Enter => "Enter", Key.Tab => "Tab", Key.PageUp => Loc.T("sc.key.pgup"), Key.PageDown => Loc.T("sc.key.pgdn"),
        Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => Loc.T("sc.key.num", (int)(k - Key.NumPad0)),
        Key.Multiply => "Num *", Key.Divide => "Num /", Key.Add => "Num +", Key.Subtract => "Num -",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "+",
        _ => k.ToString(),
    };

    /// <summary>"Reproducir (Espacio)": the tooltip text with the action's key, or just the text if it has none.</summary>
    public static string Tip(string text, string id) => text + Paren(id);

    /// <summary>" (R)" to put inside a sentence, or nothing if the action has no key.</summary>
    public static string Paren(string id) => Label(id) is { Length: > 0 } k ? $" ({k})" : "";

    /// <summary>" · Alt+X: dividir" for a list of gestures in a tooltip, or nothing if the action has no key.</summary>
    public static string Note(string id, string what) => Label(id) is { Length: > 0 } k ? $" · {k}: {what}" : "";

    /// <summary>Keys that can't be a shortcut: modifiers alone, Esc (always cancels) and system keys.</summary>
    public static bool Assignable(Key k) => k is not (Key.None or Key.Escape or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Apps or Key.System or Key.CapsLock or Key.NumLock or Key.Scroll
        or Key.PrintScreen or Key.ImeProcessed);
}
