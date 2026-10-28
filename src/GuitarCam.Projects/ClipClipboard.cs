using GuitarCam.Audio;

namespace GuitarCam.Projects;

/// <summary>
/// What Ctrl+C / Ctrl+X took from the timeline, for Ctrl+V: copies of the clips (later edits of the originals don't
/// reach them), each with its distance in tracks from the topmost one and its place inside the block. Pasting puts
/// the block's start on the playhead and its top track on the chosen one, keeping the layout of the rest; near the last
/// track the block climbs so every clip still has a track. Clips point at the project's media, so the block only pastes
/// into the project it came from. It lives in memory, not in the project: it is not saved and it has no undo of its own.
/// </summary>
public sealed class ClipClipboard
{
    public sealed record Item(Clip Clip, int Lane);

    public string ProjectId { get; }
    /// <summary>The track the top clips came from: where the block goes back when no track is chosen.</summary>
    public string TopTrackId { get; }
    public IReadOnlyList<Item> Items { get; }
    /// <summary>Where the block started when it was copied, counting the part a cut at 0 hides.</summary>
    public double StartSec { get; }
    public double LengthSec { get; }
    /// <summary>How many tracks the block spans, from the top one.</summary>
    public int Lanes => Items.Max(i => i.Lane) + 1;

    ClipClipboard(string projectId, string topTrackId, List<Item> items, double start, double length)
    {
        ProjectId = projectId;
        TopTrackId = topTrackId;
        Items = items;
        StartSec = start;
        LengthSec = length;
    }

    /// <summary>Copies these clips (in project order: by track, then as they are drawn). Null if none was found.</summary>
    public static ClipClipboard? Take(Project p, IReadOnlyCollection<string> ids, Func<Clip, double?>? lengthOf = null)
    {
        var wanted = ids.ToHashSet();
        var found = p.Tracks.SelectMany((t, i) => t.Clips.Where(c => wanted.Contains(c.Id)).Select(c => (Index: i, Clip: c))).ToList();
        if (found.Count == 0) return null;
        int top = found.Min(f => f.Index);
        var (start, end) = ClipEditing.Span(p, found.Select(f => f.Clip), lengthOf);
        return new(p.Id, p.Tracks[top].Id, [.. found.Select(f => new Item(ClipEditing.Copy(f.Clip), f.Index - top))], start, end - start);
    }

    /// <summary>
    /// Pastes the block with its start at <paramref name="atSec"/> and its top track on <paramref name="trackId"/>
    /// (null = the track it was copied from).
    /// Returns the new clips' ids. Throws if the block is from another project or there aren't tracks enough.
    /// </summary>
    public List<string> Paste(Project p, string? trackId, double atSec)
    {
        if (p.Id != ProjectId) throw new ArgumentException(Texts.T("err.clip.pasteOtherProject", "Esos clips son de otro proyecto: copialos de nuevo acá."));
        if (Items.Any(i => p.FindMedia(i.Clip.MediaId) == null))
            throw new ArgumentException(Texts.T("err.clip.pasteNoMedia", "Ya no está la toma de alguno de esos clips."));
        int at = p.Tracks.FindIndex(t => t.Id == (trackId ?? TopTrackId));
        if (at < 0) throw new ArgumentException(Texts.T("err.clip.trackNotFound", "No encontré esa pista."));
        at = Math.Min(at, p.Tracks.Count - Lanes);
        if (at < 0) throw new ArgumentException(Texts.T("err.clip.pasteTracks", "Esos clips ocupan {0} pistas y el proyecto tiene {1}.", Lanes, p.Tracks.Count));
        if (Items.Any(i => p.Tracks[at + i.Lane].IsMidi))
            throw new ArgumentException(Texts.T("err.midi.noAudio", "Las tomas no van en una pista MIDI: elegí una pista de audio."));
        double shift = atSec - StartSec;
        List<string> pasted = [];
        foreach (var item in Items)
        {
            var copy = ClipEditing.Copy(item.Clip);
            copy.Id = Ids.New("clp");
            copy.AtSec = ClipEditing.ToSample(copy.AtSec + shift);
            p.Tracks[at + item.Lane].Clips.Add(copy);
            pasted.Add(copy.Id);
        }
        return pasted;
    }
}
