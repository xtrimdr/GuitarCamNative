namespace WildTrack.Projects;

/// <summary>
/// The edits of a list of insert slots (a channel's <see cref="Channel.Inserts"/> or a bus's
/// <see cref="Bus.Inserts"/>): add, remove, repeat and move. One place for the mixer strip, the inspector and wtlab.
/// Each returns false (or null) when there was nothing to do, so the caller doesn't mark the project dirty for nothing.
/// </summary>
public static class ChannelInserts
{
    /// <summary>Adds a slot of <paramref name="kind"/> at <paramref name="at"/> (the end when null), already on: whoever adds an effect wants to hear it.</summary>
    public static InsertSlot? Add(Channel ch, string kind, int? at = null) => Add(ch.Slots, kind, at);

    public static InsertSlot? Add(List<InsertSlot> slots, string kind, int? at = null)
    {
        if (!Audio.Dsp.TrackInserts.IsKind(kind)) return null;
        var slot = InsertSlot.New(kind);
        slot.On = true;
        slots.Insert(Math.Clamp(at ?? slots.Count, 0, slots.Count), slot);
        return slot;
    }

    public static bool Remove(Channel ch, string id) => Remove(ch.Slots, id);

    public static bool Remove(List<InsertSlot> slots, string id) => slots.RemoveAll(s => s.Id == id) > 0;

    /// <summary>Moves the slot <paramref name="delta"/> places (−1 = earlier in the chain).</summary>
    public static bool Move(Channel ch, string id, int delta) => Move(ch.Slots, id, delta);

    public static bool Move(List<InsertSlot> slots, string id, int delta)
    {
        int i = slots.FindIndex(s => s.Id == id);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= slots.Count || j == i) return false;
        var s = slots[i];
        slots.RemoveAt(i);
        slots.Insert(j, s);
        return true;
    }

    /// <summary>
    /// Moves the slot right before or after <paramref name="anchorId"/>: what a drag hands over (the card it landed on),
    /// by id, so slots the window doesn't show (a kind this version doesn't know) never shift the count.
    /// </summary>
    public static bool MoveNextTo(Channel ch, string id, string anchorId, bool after) => MoveNextTo(ch.Slots, id, anchorId, after);

    public static bool MoveNextTo(List<InsertSlot> slots, string id, string anchorId, bool after)
    {
        int i = slots.FindIndex(s => s.Id == id);
        if (i < 0 || id == anchorId || !slots.Exists(s => s.Id == anchorId)) return false;
        var slot = slots[i];
        slots.RemoveAt(i);
        int j = slots.FindIndex(s => s.Id == anchorId);
        slots.Insert(after ? j + 1 : j, slot);
        return true;
    }

    /// <summary>A copy of the slot, with its settings and a new id, right after it.</summary>
    public static InsertSlot? Duplicate(Channel ch, string id) => Duplicate(ch.Slots, id);

    public static InsertSlot? Duplicate(List<InsertSlot> slots, string id)
    {
        int i = slots.FindIndex(s => s.Id == id);
        if (i < 0) return null;
        var copy = ProjectLibrary.Clone(slots[i]);
        copy.Id = Ids.New("fx");
        slots.Insert(i + 1, copy);
        return copy;
    }
}
