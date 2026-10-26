namespace GuitarCam.Projects;

/// <summary>
/// The edits of the extra returns (<see cref="ProjectSettings.Returns"/>) and of a track's sends to them
/// (<see cref="Channel.Sends"/>), plus the pre/post choice of the fixed reverb and delay sends. One place for the mixer,
/// the Canal tab and gclab. Each returns false (or null) when there was nothing to do, so the caller doesn't mark the
/// project dirty for nothing.
/// </summary>
public static class ProjectReturns
{
    /// <summary>
    /// Adds a return at the end, with one slot of <paramref name="kind"/> (reverb or delay) at its default settings, on.
    /// Its name is the kind's with the next free number ("Reverb 2": the fixed one is the first) unless one is given.
    /// </summary>
    public static ReturnBus? Add(Project p, string kind = InsertSlot.ReverbKind, string? name = null)
    {
        if (kind is not (InsertSlot.ReverbKind or InsertSlot.DelayKind)) return null;
        string baseName = kind == InsertSlot.ReverbKind ? "Reverb" : "Delay";
        if (string.IsNullOrWhiteSpace(name))
        {
            int n = 2;
            while (p.Settings.Returns.Exists(r => r.Name == $"{baseName} {n}")) n++;
            name = $"{baseName} {n}";
        }
        var slot = InsertSlot.New(kind);
        slot.On = true;
        var ret = new ReturnBus { Name = name.Trim(), Inserts = [slot] };
        p.Settings.Returns.Add(ret);
        return ret;
    }

    /// <summary>Removes the return and every track's send to it.</summary>
    public static bool Remove(Project p, string id)
    {
        if (p.Settings.Returns.RemoveAll(r => r.Id == id) == 0) return false;
        foreach (var t in p.Tracks) t.Channel.Sends.RemoveAll(s => s.ReturnId == id);
        return true;
    }

    public static bool Rename(Project p, string id, string name)
    {
        if (Find(p, id) is not { } r || string.IsNullOrWhiteSpace(name) || r.Name == name.Trim()) return false;
        r.Name = name.Trim();
        return true;
    }

    /// <summary>A return by id or by its number in the list (1 = the first extra return), or null.</summary>
    public static ReturnBus? Find(Project p, string idOrNumber)
    {
        var list = p.Settings.Returns;
        if (list.Find(r => r.Id == idOrNumber) is { } byId) return byId;
        return int.TryParse(idOrNumber, out int n) && n >= 1 && n <= list.Count ? list[n - 1] : null;
    }

    /// <summary>
    /// Changes a track's send to <paramref name="returnId"/>, creating it if it isn't there (level 0, on, post-fader):
    /// only what is given changes. Returns the send, or null when the return doesn't exist.
    /// </summary>
    public static Send? SetSend(Project p, Channel ch, string returnId, double? level = null, bool? on = null, bool? pre = null)
    {
        if (!p.Settings.Returns.Exists(r => r.Id == returnId)) return null;
        var s = ch.SendTo(returnId);
        if (s == null)
        {
            s = new Send { ReturnId = returnId };
            ch.Sends.Add(s);
        }
        if (level is { } v) s.Level = double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        if (on is { } o) s.On = o;
        if (pre is { } x) s.Pre = x;
        return s;
    }

    /// <summary>A send's level (0 without one), ON and pre-fader, read the same way for the fixed and the extra returns.</summary>
    public static (double Level, bool On, bool Pre) SendOf(Channel ch, string returnId) =>
        ch.SendTo(returnId) is { } s ? (s.Level, s.On, s.Pre) : (0, true, false);

    /// <summary>Drops the sends to returns that no longer exist (a hand-edited file, a removed return).</summary>
    internal static void Normalize(Project p)
    {
        var ids = new HashSet<string>();
        foreach (var r in p.Settings.Returns)
            if (!ids.Add(r.Id)) { r.Id = Ids.New("ret"); ids.Add(r.Id); }
        foreach (var t in p.Tracks) t.Channel.Sends.RemoveAll(s => !ids.Contains(s.ReturnId));
    }
}
