using GuitarCam.Audio;

namespace GuitarCam.Projects;

/// <summary>
/// Track folders (native only, asked by the user). The project keeps the tracks in one list and each folder right
/// before its children (<see cref="Folder.Index"/>); edits work on the "sequence" the timeline shows, folders and
/// tracks in order, and write it back (<see cref="Rebuild"/>), so every move lands in a consistent order. Where a
/// folder's sound goes is <see cref="ProjectBuses"/>' business. A separator (<see cref="Folder.Separator"/>) lives in the
/// same order as an empty folder that never takes tracks, so the timeline and the mixer move it like one.
/// </summary>
public static class TrackFolders
{
    /// <summary>Folders and tracks in the order the timeline shows them: each folder, then its children.</summary>
    public static List<object> Sequence(IReadOnlyList<Track> tracks, IReadOnlyList<Folder> folders)
    {
        var seq = new List<object>(tracks.Count + folders.Count);
        int fi = 0;
        for (int k = 0; k <= tracks.Count; k++)
        {
            while (fi < folders.Count && folders[fi].Index <= k) seq.Add(folders[fi++]);
            if (k < tracks.Count) seq.Add(tracks[k]);
        }
        while (fi < folders.Count) seq.Add(folders[fi++]);
        return seq;
    }

    public static List<object> Sequence(Project p) => Sequence(p.Tracks, p.Folders);

    /// <summary>Writes a sequence back: the tracks' order, the folders' places and order.</summary>
    static void Rebuild(Project p, List<object> seq)
    {
        var tracks = new List<Track>();
        var folders = new List<Folder>();
        foreach (var item in seq)
        {
            if (item is Track t) tracks.Add(t);
            else if (item is Folder f) { f.Index = tracks.Count; folders.Add(f); }
        }
        p.Tracks = tracks;
        p.Folders = folders;
        Normalize(p);
    }

    /// <summary>
    /// Keeps the rules: children of an unknown folder go to the root, each folder's children sit together right
    /// after it (where its first child was), an empty folder never lands inside another one, folders are in order.
    /// </summary>
    public static void Normalize(Project p)
    {
        p.Folders ??= [];
        p.Folders.RemoveAll(f => f == null);
        var ids = new HashSet<string>();
        foreach (var f in p.Folders)
        {
            if (string.IsNullOrEmpty(f.Id) || !ids.Add(f.Id)) f.Id = Ids.New("fld");
            ids.Add(f.Id);
            f.Name ??= "";
            f.Color = Track.NormalizeColor(f.Color);
            f.Height = Track.NormalizeHeight(f.Height, Folder.MinHeight, Folder.MaxHeight);
            f.HeightScale = Track.NormalizeHeight(f.HeightScale, 0.1, 8);
            f.Normalize();
        }
        var separators = p.Folders.Where(f => f.Separator).Select(f => f.Id).ToHashSet();
        foreach (var t in p.Tracks) if (t.FolderId != null && (!ids.Contains(t.FolderId) || separators.Contains(t.FolderId))) t.FolderId = null;

        var byId = p.Folders.ToDictionary(f => f.Id);
        var order = new List<Track>(p.Tracks.Count);
        var placed = new HashSet<string>();
        foreach (var t in p.Tracks)
        {
            if (t.FolderId == null) order.Add(t);
            else if (placed.Add(t.FolderId))
            {
                byId[t.FolderId].Index = order.Count;
                order.AddRange(p.Tracks.Where(x => x.FolderId == t.FolderId));
            }
        }
        p.Tracks = order;

        var blocks = p.Folders.Where(f => placed.Contains(f.Id)).Select(f => (f.Index, End: f.Index + order.Count(t => t.FolderId == f.Id))).ToList();
        foreach (var f in p.Folders.Where(f => !placed.Contains(f.Id)))
        {
            f.Index = Math.Clamp(f.Index, 0, order.Count);
            foreach (var (start, end) in blocks)
                if (f.Index > start && f.Index < end) f.Index = end;
        }
        // at the same place an empty folder comes first: the one with children is followed by them
        p.Folders = p.Folders.Select((f, i) => (f, i)).OrderBy(x => x.f.Index).ThenBy(x => placed.Contains(x.f.Id) ? 1 : 0).ThenBy(x => x.i)
            .Select(x => x.f).ToList();
    }

    public static Folder? Of(Project p, Track t) => t.FolderId == null ? null : p.Folders.Find(f => f.Id == t.FolderId);

    public static IEnumerable<Track> Children(Project p, Folder f) => p.Tracks.Where(t => t.FolderId == f.Id);

    /// <summary>A free "Carpeta n" name, in Spanish like "Pista n" (names the user creates are data).</summary>
    static string FreeName(Project p)
    {
        var used = p.Folders.Select(f => f.Name).ToHashSet();
        int n = p.Folders.Count(f => !f.Separator) + 1;
        while (used.Contains($"Carpeta {n}")) n++;
        return $"Carpeta {n}";
    }

    /// <summary>
    /// A new separator at <paramref name="at"/> in the sequence (the end when null; inside a folder's tracks it moves past
    /// them, like an empty folder), named <paramref name="name"/> or "Separador". It starts hidden in the timeline (asked by
    /// the user: separators are mostly for the mixer) unless <paramref name="hidden"/> is false. Returns its id.
    /// </summary>
    public static string AddSeparator(Project p, string? name = null, int? at = null, bool hidden = true)
    {
        var f = new Folder { Name = string.IsNullOrWhiteSpace(name) ? "Separador" : name.Trim(), Separator = true, HiddenInTimeline = hidden };
        var seq = Sequence(p);
        int pos = Math.Clamp(at ?? seq.Count, 0, seq.Count);
        // never inside a folder: past its last child
        while (pos > 0 && pos < seq.Count && seq[pos] is Track next && next.FolderId != null) pos++;
        seq.Insert(pos, f);
        Rebuild(p, seq);
        return f.Id;
    }

    /// <summary>Whether <paramref name="folderId"/> is a folder that takes tracks (not a separator).</summary>
    static bool Holds(Project p, string folderId) => p.Folders.Any(f => f.Id == folderId && !f.Separator);

    /// <summary>
    /// A new folder. With tracks it takes them (in their order) at the place of the first one; without, it goes at the
    /// end, empty. Returns its id.
    /// </summary>
    public static string Add(Project p, string? name = null, IReadOnlyCollection<string>? trackIds = null)
    {
        var f = new Folder { Name = string.IsNullOrWhiteSpace(name) ? FreeName(p) : name.Trim() };
        var seq = Sequence(p);
        var take = trackIds == null ? [] : p.Tracks.Where(t => trackIds.Contains(t.Id)).ToList();
        if (take.Count == 0)
        {
            seq.Add(f);
        }
        else
        {
            // inside another folder's children the new block splits nothing: Normalize puts that folder's tracks back together
            int at = seq.IndexOf(take[0]);
            seq.RemoveAll(x => x is Track t && take.Contains(t));
            at = Math.Min(at, seq.Count);
            foreach (var t in take) t.FolderId = f.Id;
            seq.InsertRange(at, [f, .. take]);
        }
        Rebuild(p, seq);
        return f.Id;
    }

    /// <summary>
    /// Moves a track to <paramref name="at"/> in the sequence without it, into <paramref name="folderId"/> (null = the
    /// root). The timeline picks a place that matches the folder; anything else is put right by <see cref="Normalize"/>.
    /// </summary>
    public static void Move(Project p, string trackId, int at, string? folderId) => Move(p, [trackId], at, folderId);

    /// <summary>
    /// Moves several tracks together, in the order they are now, to <paramref name="at"/> in the sequence without
    /// them, into <paramref name="folderId"/> (null = the root): they land one after the other.
    /// </summary>
    public static void Move(Project p, IReadOnlyCollection<string> trackIds, int at, string? folderId)
    {
        var block = p.Tracks.Where(t => trackIds.Contains(t.Id)).ToList();
        if (block.Count == 0) throw new ArgumentException(Texts.T("err.folder.noTrack", "No encontré esa pista."));
        if (folderId != null && !Holds(p, folderId)) throw new ArgumentException(Texts.T("err.folder.none", "No encontré esa carpeta."));
        var seq = Sequence(p);
        seq.RemoveAll(x => x is Track t && block.Contains(t));
        foreach (var t in block) t.FolderId = folderId;
        seq.InsertRange(Math.Clamp(at, 0, seq.Count), block);
        Rebuild(p, seq);
    }

    /// <summary>Moves a track to the end of a folder.</summary>
    public static void MoveInto(Project p, string trackId, string folderId)
    {
        var seq = Sequence(p);
        seq.RemoveAll(x => x is Track t && t.Id == trackId);
        int at = seq.FindLastIndex(x => x is Track c && c.FolderId == folderId || x is Folder f && f.Id == folderId) + 1;
        Move(p, trackId, at, folderId);
    }

    /// <summary>Moves a folder with its children to <paramref name="at"/> in the sequence without them (always at the root).</summary>
    public static void MoveFolder(Project p, string folderId, int at)
    {
        var f = p.Folders.Find(x => x.Id == folderId) ?? throw new ArgumentException(Texts.T("err.folder.none", "No encontré esa carpeta."));
        var seq = Sequence(p);
        var block = seq.Where(x => x == f || x is Track t && t.FolderId == f.Id).ToList();
        seq.RemoveAll(block.Contains);
        at = Math.Clamp(at, 0, seq.Count);
        // never inside another folder: past its last child
        while (at > 0 && at < seq.Count && seq[at] is Track next && next.FolderId != null) at++;
        seq.InsertRange(at, block);
        Rebuild(p, seq);
    }

    /// <summary>Takes a track out of its folder, right after the folder's last child.</summary>
    public static void TakeOut(Project p, string trackId)
    {
        var t = p.Tracks.Find(x => x.Id == trackId);
        if (t?.FolderId == null) return;
        var seq = Sequence(p);
        string folder = t.FolderId;
        seq.Remove(t);
        int at = seq.FindLastIndex(x => x is Track c && c.FolderId == folder || x is Folder f && f.Id == folder) + 1;
        t.FolderId = null;
        seq.Insert(at, t);
        Rebuild(p, seq);
    }

    /// <summary>Removes a folder; its tracks stay where they are, at the root, or go with it (their takes stay in Media).</summary>
    public static void Remove(Project p, string folderId, bool withTracks = false)
    {
        if (withTracks)
            foreach (var id in p.Tracks.Where(t => t.FolderId == folderId).Select(t => t.Id).ToList()) ClipEditing.RemoveTrack(p, id);
        var seq = Sequence(p);
        foreach (var t in p.Tracks) if (t.FolderId == folderId) t.FolderId = null;
        seq.RemoveAll(x => x is Folder f && f.Id == folderId);
        Rebuild(p, seq);
    }

    /// <summary>Called after a track left the list: an empty folder after it keeps its place.</summary>
    internal static void TrackRemoved(Project p, int index)
    {
        foreach (var f in p.Folders) if (f.Index > index && !p.Tracks.Any(t => t.FolderId == f.Id)) f.Index--;
        Normalize(p);
    }
}
