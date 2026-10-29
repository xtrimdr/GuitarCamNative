namespace WildTrack.Projects;

/// <summary>
/// The edits of the buses (<see cref="ProjectSettings.Buses"/>) and of where things send their sound (every
/// <see cref="IRouted"/>: a track's channel, a folder, a bus): outputs and sends. One place for the mixer, the
/// inspector, the timeline and wtlab. For now a bus goes to the master only (asked by the user, 2026-10-26: chains
/// between buses made the routing hard to follow): <see cref="CanFeed"/> refuses a bus as the source and
/// <see cref="Normalize"/> clears a bus's output and sends. The engine still runs buses fed by buses
/// (<see cref="Order"/>, <c>MixRenderer</c>), so the chains can come back later. Each edit returns false (or null)
/// when there was nothing to do, so the caller doesn't mark the project dirty for nothing.
/// </summary>
public static class ProjectBuses
{
    /// <summary>The ids of the "Reverb" and "Delay" buses a new project brings (and version 2's fixed returns became).</summary>
    public const string ReverbId = "bus-reverb", DelayId = "bus-delay";

    /// <summary>What a new project brings: a "Reverb" and a "Delay" bus (with these settings, or their defaults).</summary>
    public static List<Bus> Defaults(ReverbSettings? reverb = null, DelaySettings? delay = null) =>
        [New(InsertSlot.ReverbKind, "Reverb", ReverbId, reverb), New(InsertSlot.DelayKind, "Delay", DelayId, delay: delay)];

    /// <summary>Sets <paramref name="ch"/>'s sends to the default Reverb and Delay buses (a template's track).</summary>
    public static void SendToDefaults(Channel ch, double reverb, double delay = 0)
    {
        if (reverb > 0) ch.Sends.Add(new Send { BusId = ReverbId, Level = reverb });
        if (delay > 0) ch.Sends.Add(new Send { BusId = DelayId, Level = delay });
    }

    /// <summary>A bus with one slot of <paramref name="kind"/> at its defaults, on (none for an empty bus).</summary>
    static Bus New(string kind, string name, string? id = null, ReverbSettings? reverb = null, DelaySettings? delay = null)
    {
        var bus = new Bus { Name = name };
        if (id != null) bus.Id = id;
        if (kind is InsertSlot.ReverbKind or InsertSlot.DelayKind)
        {
            var slot = InsertSlot.New(kind);
            if (reverb != null) slot.Reverb = reverb;
            if (delay != null) slot.Delay = delay;
            // version 2's mute of a fixed return was its whole return: here it is the bus's
            bool muted = reverb?.Muted == true || delay?.Muted == true;
            if (slot.Reverb != null) slot.Reverb.Muted = false;
            if (slot.Delay != null) slot.Delay.Muted = false;
            slot.On = true;
            bus.Inserts = [slot];
            bus.Muted = muted;
        }
        return bus;
    }

    /// <summary>
    /// Version 2's fixed reverb and delay (as the buses <see cref="ReverbId"/> and <see cref="DelayId"/>, which the old
    /// channel sends point at) followed by its extra returns, which keep their ids.
    /// </summary>
    internal static List<Bus> FromVersion2(ReverbSettings? reverb, DelaySettings? delay, List<Bus>? returns)
    {
        var list = Defaults(reverb, delay);
        if (returns != null) list.AddRange(returns.Where(r => r != null));
        return list;
    }

    /// <summary>
    /// Adds a bus at the end: empty, or with one reverb or delay slot. Its name is the kind's ("Bus", "Reverb", "Delay")
    /// with the next free number when that one is taken, unless one is given.
    /// </summary>
    public static Bus? Add(Project p, string kind = "", string? name = null)
    {
        if (kind is not ("" or InsertSlot.ReverbKind or InsertSlot.DelayKind)) return null;
        string baseName = kind switch { InsertSlot.ReverbKind => "Reverb", InsertSlot.DelayKind => "Delay", _ => "Bus" };
        if (string.IsNullOrWhiteSpace(name))
        {
            name = baseName;
            for (int n = 2; p.Settings.Buses.Exists(b => b.Name == name); n++) name = $"{baseName} {n}";
        }
        var bus = New(kind, name.Trim());
        p.Settings.Buses.Add(bus);
        return bus;
    }

    /// <summary>Removes the bus; what went out or was sent to it goes to the master or stops being sent.</summary>
    public static bool Remove(Project p, string id)
    {
        if (p.Settings.Buses.RemoveAll(b => b.Id == id) == 0) return false;
        foreach (var r in All(p))
        {
            if (r.Output == id) r.Output = null;
            r.Sends.RemoveAll(s => s.BusId == id);
        }
        return true;
    }

    public static bool Rename(Project p, string id, string name)
    {
        if (Find(p, id) is not { } b || string.IsNullOrWhiteSpace(name) || b.Name == name.Trim()) return false;
        b.Name = name.Trim();
        return true;
    }

    /// <summary>A bus by id or by its number in the list (1 = the first), or null.</summary>
    public static Bus? Find(Project p, string idOrNumber)
    {
        var list = p.Settings.Buses;
        if (list.Find(b => b.Id == idOrNumber) is { } byId) return byId;
        return int.TryParse(idOrNumber, out int n) && n >= 1 && n <= list.Count ? list[n - 1] : null;
    }

    /// <summary>The first bus whose first slot is a <paramref name="kind"/> (reverb or delay).</summary>
    public static Bus? FirstWith(Project p, string kind)
    {
        // a loop and not Find: the timeline asks while it draws, and a lambda capturing kind would allocate every frame
        var list = p.Settings.Buses;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Inserts.Count > 0 && list[i].Inserts[0].Type == kind) return list[i];
        return null;
    }

    /// <summary>Everything that routes sound: the tracks' channels, the folders and the buses.</summary>
    public static IEnumerable<IRouted> All(Project p) =>
        p.Tracks.Select(t => (IRouted)t.Channel).Concat(p.Folders.Where(f => !f.Separator)).Concat(p.Settings.Buses);

    /// <summary>
    /// Whether <paramref name="from"/> can send or route its sound to the bus <paramref name="to"/>: the bus exists and
    /// <paramref name="from"/> is a track or a folder (a bus goes to the master only, so no loop can close).
    /// </summary>
    public static bool CanFeed(Project p, IRouted from, string to) => from is not Bus && p.Settings.Buses.Exists(b => b.Id == to);

    /// <summary>Where <paramref name="r"/>'s output goes: null = the master. False when nothing changed or it would close a loop.</summary>
    public static bool SetOutput(Project p, IRouted r, string? busId)
    {
        if (r.Output == busId || busId != null && !CanFeed(p, r, busId)) return false;
        r.Output = busId;
        return true;
    }

    /// <summary>
    /// Changes <paramref name="r"/>'s send to <paramref name="busId"/>, creating it if it isn't there (level 0, on,
    /// post-fader): only what is given changes. A folder's never goes pre-fader. Returns the send, or null when the bus
    /// doesn't exist or the send would close a loop.
    /// </summary>
    public static Send? SetSend(Project p, IRouted r, string busId, double? level = null, bool? on = null, bool? pre = null)
    {
        if (!CanFeed(p, r, busId)) return null;
        var s = r.Sends.Find(x => x.BusId == busId);
        if (s == null)
        {
            s = new Send { BusId = busId };
            r.Sends.Add(s);
        }
        if (level is { } v) s.Level = double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        if (on is { } o) s.On = o;
        if (pre is { } x) s.Pre = x && r is not Folder;
        return s;
    }

    /// <summary>The buses <paramref name="r"/> shows a send to, in the buses' order: every one it can feed but those taken out (<see cref="Send.Hidden"/>).</summary>
    public static List<Bus> Shown(Project p, IRouted r) =>
        [.. p.Settings.Buses.Where(b => CanFeed(p, r, b.Id) && r.Sends.Find(s => s.BusId == b.Id)?.Hidden != true)];

    /// <summary>
    /// Takes a bus out of <paramref name="r"/>'s list (<paramref name="shown"/> false: the send goes off and hidden, its
    /// level kept) or adds it back (on, with that level). False when nothing changed or it can't feed that bus.
    /// </summary>
    public static bool ShowSend(Project p, IRouted r, string busId, bool shown)
    {
        if (!CanFeed(p, r, busId)) return false;
        var s = r.Sends.Find(x => x.BusId == busId);
        if ((s?.Hidden ?? false) != shown) return false;
        s = SetSend(p, r, busId, on: shown)!;
        s.Hidden = !shown;
        return true;
    }

    /// <summary>A send's level (0 without one), ON and pre-fader.</summary>
    public static (double Level, bool On, bool Pre) SendOf(IRouted r, string busId)
    {
        // a loop for the same reason as FirstWith
        var list = r.Sends;
        for (int i = 0; i < list.Count; i++)
            if (list[i].BusId == busId) return (list[i].Level, list[i].On, list[i].Pre);
        return (0, true, false);
    }

    /// <summary>
    /// The buses in the order the engine runs them: each one after every bus that routes or sends to it (ties keep the
    /// list's order). The folders always come before all of them: nothing routes into a folder but its tracks.
    /// </summary>
    public static List<Bus> Order(Project p)
    {
        var buses = p.Settings.Buses;
        var index = new Dictionary<string, int>();
        for (int i = 0; i < buses.Count; i++) index[buses[i].Id] = i;
        var into = new int[buses.Count];
        var targets = buses.Select(b => Targets(b).Where(index.ContainsKey).Select(id => index[id]).Distinct().ToList()).ToList();
        foreach (var t in targets) foreach (int k in t) into[k]++;
        var order = new List<Bus>(buses.Count);
        var done = new bool[buses.Count];
        while (order.Count < buses.Count)
        {
            // the first bus in the list that nothing left feeds (with a loop, which Normalize never leaves, the first left)
            int next = -1;
            for (int i = 0; i < buses.Count && next < 0; i++) if (!done[i] && into[i] == 0) next = i;
            if (next < 0) next = Array.IndexOf(done, false);
            done[next] = true;
            order.Add(buses[next]);
            foreach (int k in targets[next]) into[k]--;
        }
        return order;
    }

    /// <summary>The buses <paramref name="r"/>'s sound goes to: its output and its sends.</summary>
    static IEnumerable<string> Targets(IRouted r)
    {
        if (r.Output != null) yield return r.Output;
        foreach (var s in r.Sends) yield return s.BusId;
    }

    /// <summary>
    /// Repairs what a hand-edited file or a removed bus leaves: repeated bus ids, outputs and sends to buses that no
    /// longer exist (output → the master), repeated sends (the first wins), levels out of range, a folder's pre-fader
    /// sends, and any loop between buses (the edge that closes it is dropped).
    /// </summary>
    internal static void Normalize(Project p)
    {
        var ids = new HashSet<string>();
        foreach (var b in p.Settings.Buses)
            if (!ids.Add(b.Id)) { b.Id = Ids.New("bus"); ids.Add(b.Id); }
        foreach (var r in All(p))
        {
            r.Sends ??= [];
            if (r.Output != null && !ids.Contains(r.Output)) r.Output = null;
            var seen = new HashSet<string>();
            r.Sends.RemoveAll(s => s == null || !ids.Contains(s.BusId) || !seen.Add(s.BusId));
            foreach (var s in r.Sends)
            {
                s.Level = double.IsFinite(s.Level) ? Math.Clamp(s.Level, 0, 1) : 0;
                if (r is Folder) s.Pre = false;
            }
        }
        // a bus goes to the master only (see the summary): what a file brings from before goes
        foreach (var b in p.Settings.Buses)
        {
            b.Output = null;
            b.Sends.Clear();
        }
    }
}
