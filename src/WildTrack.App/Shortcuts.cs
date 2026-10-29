using System.Text.Json;
using Avalonia.Input;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// The keyboard shortcuts (native only, the web's are fixed in App.tsx onKey): one table of actions with their
/// default keys, the user's changes on top, remembered in the program's config ("shortcuts", only what differs
/// from the default). Tooltips read their key from here (<see cref="Tip"/>), so a change shows up everywhere.
/// Esc is not in the table: it always cancels (count-in, gesture, dialogs). The timeline's wheel gestures (zoom,
/// sideways, track height) are a second, smaller table (<see cref="Wheel"/>): their "key" is only the modifiers held
/// while turning the wheel, remembered apart ("wheel") because a modifier alone is not a <see cref="KeyGesture"/>.
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
        new("loop", Loc.T("sc.group.transport"), Loc.T("sc.loop"), [G(Key.L)]),
        new("loopIn", Loc.T("sc.group.transport"), Loc.T("sc.loopIn"), [G(Key.I)]),
        new("loopOut", Loc.T("sc.group.transport"), Loc.T("sc.loopOut"), [G(Key.O)]),
        new("marker", Loc.T("sc.group.transport"), Loc.T("sc.marker"), [G(Key.K)]),
        new("prevMarker", Loc.T("sc.group.transport"), Loc.T("sc.prevMarker"), [G(Key.OemComma)]),
        new("nextMarker", Loc.T("sc.group.transport"), Loc.T("sc.nextMarker"), [G(Key.OemPeriod)]),
        new("mute", Loc.T("sc.group.tracks"), Loc.T("sc.mute"), [G(Key.M)]),
        new("solo", Loc.T("sc.group.tracks"), Loc.T("sc.solo"), [G(Key.S)]),
        new("arm", Loc.T("sc.group.tracks"), Loc.T("sc.arm"), [G(Key.R)]),
        new("newTrack", Loc.T("sc.group.tracks"), Loc.T("sc.newTrack"), [G(Key.N, KeyModifiers.Control | KeyModifiers.Shift)]),
        new("delete", Loc.T("sc.group.editing"), Loc.T("sc.delete"), [G(Key.Delete)]),
        new("split", Loc.T("sc.group.editing"), Loc.T("sc.split"), [G(Key.X, KeyModifiers.Alt)]),
        new("duplicate", Loc.T("sc.group.editing"), Loc.T("sc.duplicate"), [G(Key.D)]),
        new("quantize", Loc.T("sc.group.editing"), Loc.T("sc.quantize"), [G(Key.Q)]),
        new("copy", Loc.T("sc.group.editing"), Loc.T("sc.copy"), [G(Key.C, KeyModifiers.Control)]),
        new("cut", Loc.T("sc.group.editing"), Loc.T("sc.cut"), [G(Key.X, KeyModifiers.Control)]),
        new("paste", Loc.T("sc.group.editing"), Loc.T("sc.paste"), [G(Key.V, KeyModifiers.Control)]),
        new("deleteClose", Loc.T("sc.group.editing"), Loc.T("sc.deleteClose"), [G(Key.Delete, KeyModifiers.Shift)]),
        new("undo", Loc.T("sc.group.editing"), Loc.T("sc.undo"), [G(Key.Z, KeyModifiers.Control)]),
        new("redo", Loc.T("sc.group.editing"), Loc.T("sc.redo"), [G(Key.Y, KeyModifiers.Control), G(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)]),
        new("save", Loc.T("sc.group.editing"), Loc.T("sc.save"), [G(Key.S, KeyModifiers.Control)]),
        new("fit", Loc.T("sc.group.view"), Loc.T("sc.fit"), [G(Key.F)]),
        new("zoom1", Loc.T("sc.group.view"), Loc.T("sc.zoom1"), []),
        new("zoom2", Loc.T("sc.group.view"), Loc.T("sc.zoom2"), []),
        new("zoom3", Loc.T("sc.group.view"), Loc.T("sc.zoom3"), []),
        new("zoom4", Loc.T("sc.group.view"), Loc.T("sc.zoom4"), []),
        new("zoom5", Loc.T("sc.group.view"), Loc.T("sc.zoom5"), []),
        new("mixer", Loc.T("sc.group.view"), Loc.T("sc.mixer"), [G(Key.X)]),
        new("leftPanel", Loc.T("sc.group.view"), Loc.T("sc.leftPanel"), [G(Key.B)]),
        new("rightPanel", Loc.T("sc.group.view"), Loc.T("sc.rightPanel"), [G(Key.P)]),
        new("fullScreen", Loc.T("sc.group.view"), Loc.T("sc.fullScreen"), [G(Key.F11)]),
        new("toolSelect", Loc.T("sc.group.tools"), Loc.T("sc.toolSelect"), [G(Key.D1), G(Key.NumPad1)]),
        new("toolRange", Loc.T("sc.group.tools"), Loc.T("sc.toolRange"), [G(Key.D2), G(Key.NumPad2)]),
        new("toolSplit", Loc.T("sc.group.tools"), Loc.T("sc.toolSplit"), [G(Key.D3), G(Key.NumPad3)]),
        new("toolErase", Loc.T("sc.group.tools"), Loc.T("sc.toolErase"), [G(Key.D4), G(Key.NumPad4)]),
        new("toolListen", Loc.T("sc.group.tools"), Loc.T("sc.toolListen"), [G(Key.D5), G(Key.NumPad5)]),
        new("toolZoom", Loc.T("sc.group.tools"), Loc.T("sc.toolZoom"), [G(Key.D6), G(Key.NumPad6)]),
        new("toolHand", Loc.T("sc.group.tools"), Loc.T("sc.toolHand"), [G(Key.D7), G(Key.NumPad7)]),
    ];

    public sealed record WheelAction(string Id, string Name, KeyModifiers Default);

    /// <summary>The wheel's modifiers in the timeline; the plain wheel always scrolls up and down.</summary>
    public static readonly WheelAction[] Wheel =
    [
        new("wheelZoom", Loc.T("sc.wheelZoom"), KeyModifiers.Shift),
        new("wheelSideways", Loc.T("sc.wheelSideways"), KeyModifiers.Control),
        new("wheelHeight", Loc.T("sc.wheelHeight"), KeyModifiers.Alt),
    ];

    const KeyModifiers Mods = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

    /// <summary>The keys the user changed; an empty array means "sin atajo".</summary>
    static readonly Dictionary<string, KeyGesture[]> custom = [];

    /// <summary>The wheel modifiers the user changed; None means "sin atajo".</summary>
    static readonly Dictionary<string, KeyModifiers> customWheel = [];

    /// <summary>The plain wheel (up and down) has no modifiers to change, but it can be reversed like the others.</summary>
    public const string WheelScroll = "wheelScroll";

    /// <summary>The wheel gestures (and <see cref="WheelScroll"/>) the user turned around, each on its own
    /// ("wheelReverse").</summary>
    static readonly HashSet<string> reversed = [];

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
        customWheel.Clear();
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(AppConfig.Get("wheel") ?? "{}") ?? [];
            foreach (var (id, mods) in saved)
                if (Wheel.Any(a => a.Id == id) && Enum.TryParse<KeyModifiers>(mods, out var m))
                    customWheel[id] = m & Mods;
        }
        catch (JsonException) { }
        reversed.Clear();
        try
        {
            foreach (var id in JsonSerializer.Deserialize<string[]>(AppConfig.Get("wheelReverse") ?? "[]") ?? [])
                if (id == WheelScroll || Wheel.Any(a => a.Id == id)) reversed.Add(id);
        }
        catch (JsonException) { }
    }

    public static KeyGesture[] Keys(string id) =>
        custom.TryGetValue(id, out var k) ? k : All.First(a => a.Id == id).Defaults;

    public static bool IsDefault(string id) => !custom.ContainsKey(id) && !customWheel.ContainsKey(id);

    public static bool AllDefault => custom.Count == 0 && customWheel.Count == 0 && reversed.Count == 0;

    /// <summary>-1 if the user reversed this wheel gesture, else 1. Runs on every wheel step, so it doesn't allocate.</summary>
    public static int WheelSign(string id) => reversed.Contains(id) ? -1 : 1;

    public static bool WheelReversed(string id) => reversed.Contains(id);

    public static void SetWheelReversed(string id, bool on)
    {
        if (on) reversed.Add(id);
        else reversed.Remove(id);
        Save();
    }

    public static KeyModifiers WheelMods(string id) =>
        customWheel.TryGetValue(id, out var m) ? m : Wheel.First(a => a.Id == id).Default;

    /// <summary>The wheel action for the held modifiers (exact match), or null for the plain scroll. Runs on every
    /// wheel step, so it doesn't allocate.</summary>
    public static string? FindWheel(KeyModifiers mods)
    {
        mods &= Mods;
        if (mods == KeyModifiers.None) return null;
        foreach (var a in Wheel)
            if (WheelMods(a.Id) == mods) return a.Id;
        return null;
    }

    /// <summary>Like <see cref="Assign"/> for a wheel gesture: another wheel action with the same modifiers loses them.</summary>
    public static string? AssignWheel(string id, KeyModifiers mods)
    {
        mods &= Mods;
        string? taken = null;
        if (mods != KeyModifiers.None)
            foreach (var a in Wheel)
                if (a.Id != id && WheelMods(a.Id) == mods)
                {
                    SetWheel(a.Id, KeyModifiers.None);
                    taken = a.Name;
                }
        SetWheel(id, mods);
        Save();
        return taken;
    }

    public static void ResetWheel(string id)
    {
        var def = Wheel.First(a => a.Id == id).Default;
        if (FindWheel(def) is { } other && other != id) SetWheel(other, KeyModifiers.None);
        customWheel.Remove(id);
        Save();
    }

    static void SetWheel(string id, KeyModifiers mods)
    {
        if (mods == Wheel.First(a => a.Id == id).Default) customWheel.Remove(id);
        else customWheel[id] = mods;
    }

    /// <summary>The action for a key press, or null. Modifiers have to match exactly (C is click, Shift+C the count-in).</summary>
    public static string? Find(KeyEventArgs e) => Find(e.Key, e.KeyModifiers);

    static string? Find(Key key, KeyModifiers mods)
    {
        mods &= Mods;
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
        customWheel.Clear();
        reversed.Clear();
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
        string wheel = JsonSerializer.Serialize(customWheel.ToDictionary(p => p.Key, p => p.Value.ToString()));
        string reverse = JsonSerializer.Serialize(reversed.Order().ToArray());
        _ = Task.Run(() =>
        {
            try { AppConfig.Set("shortcuts", json); AppConfig.Set("wheel", wheel); AppConfig.Set("wheelReverse", reverse); }
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

    public static string Label(KeyGesture g) => Label(g.KeyModifiers, KeyName(g.Key));

    /// <summary>"Shift+rueda"; empty when the wheel action has no modifiers.</summary>
    public static string WheelLabel(string id) => WheelMods(id) is var m && m != KeyModifiers.None ? Label(m, Loc.T("sc.key.wheel")) : "";

    /// <summary>"Zoom horizontal (Shift+rueda)", or just the text if the wheel action has no modifiers.</summary>
    public static string WheelTip(string text, string id) => WheelLabel(id) is { Length: > 0 } k ? $"{text} ({k})" : text;

    static string Label(KeyModifiers mods, string key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(key);
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
