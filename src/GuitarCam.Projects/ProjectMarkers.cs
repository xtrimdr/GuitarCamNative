using GuitarCam.Audio;

namespace GuitarCam.Projects;

/// <summary>
/// The song's named markers and the A–B loop range (native only, asked by the user), shared by the window and
/// gclab. Markers live in <see cref="Project.Markers"/>, always in time order (every edit sorts them again), and go
/// back with undo like the tracks; the loop range is transport state in <see cref="ProjectSettings"/>, like the tempo.
/// </summary>
public static class ProjectMarkers
{
    /// <summary>Shortest A–B range: anything shorter would loop a click.</summary>
    public const double MinLoopSec = 0.05;

    /// <summary>
    /// "Previous" from just after a marker goes to the one before it, like a CD player's ◀◀: while it plays, the
    /// playhead is already past the marker it jumped to when the next press comes.
    /// </summary>
    const double PreviousSlackSec = 0.4;

    public static void Normalize(Project p)
    {
        p.Markers ??= [];
        p.Markers.RemoveAll(m => m == null);
        var ids = new HashSet<string>();
        foreach (var m in p.Markers)
        {
            if (string.IsNullOrEmpty(m.Id) || !ids.Add(m.Id)) m.Id = Ids.New("mrk");
            ids.Add(m.Id);
            m.Name ??= "";
            m.Sec = double.IsFinite(m.Sec) ? Math.Max(0, m.Sec) : 0;
        }
        Sort(p);
    }

    static void Sort(Project p)
    {
        // stable, so two markers at the same time keep their order
        var sorted = p.Markers.OrderBy(m => m.Sec).ToList();
        p.Markers.Clear();
        p.Markers.AddRange(sorted);
    }

    public static Marker Find(Project p, string id) =>
        p.Markers.Find(m => m.Id == id) ?? throw new ArgumentException(Texts.T("err.marker.notFound", "No encontré ese marcador."));

    /// <summary>A marker at <paramref name="sec"/>; without a name, "Marcador n" with the first n not taken.</summary>
    public static Marker Add(Project p, double sec, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            int n = 1;
            while (p.Markers.Any(m => m.Name == $"Marcador {n}")) n++;
            name = $"Marcador {n}";
        }
        var marker = new Marker { Name = name.Trim(), Sec = Math.Max(0, double.IsFinite(sec) ? sec : 0) };
        p.Markers.Add(marker);
        Sort(p);
        return marker;
    }

    public static void Move(Project p, string id, double sec)
    {
        Find(p, id).Sec = Math.Max(0, double.IsFinite(sec) ? sec : 0);
        Sort(p);
    }

    public static void Rename(Project p, string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(Texts.T("err.marker.noName", "El marcador necesita un nombre."));
        Find(p, id).Name = name.Trim();
    }

    public static void Remove(Project p, string id) => p.Markers.Remove(Find(p, id));

    /// <summary>The first marker after <paramref name="sec"/> (a hair after, so one right at the playhead is skipped), or null.</summary>
    public static Marker? Next(Project p, double sec) => p.Markers.Find(m => m.Sec > sec + 1e-3);

    /// <summary>The last marker before <paramref name="sec"/>, skipping one the playhead just passed (see <see cref="PreviousSlackSec"/>), or null.</summary>
    public static Marker? Previous(Project p, double sec, bool playing) =>
        p.Markers.FindLast(m => m.Sec < sec - (playing ? PreviousSlackSec : 1e-3));

    /// <summary>Sets the A–B range in either order; a range shorter than <see cref="MinLoopSec"/> is refused.</summary>
    public static void SetLoop(ProjectSettings s, double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b)) throw new ArgumentException(Texts.T("err.loop.short", "El rango para repetir es demasiado corto."));
        double start = Math.Max(0, Math.Min(a, b)), end = Math.Max(a, b);
        if (end - start < MinLoopSec) throw new ArgumentException(Texts.T("err.loop.short", "El rango para repetir es demasiado corto."));
        s.LoopStartSec = start;
        s.LoopEndSec = end;
    }

    public static void ClearLoop(ProjectSettings s) => s.LoopStartSec = s.LoopEndSec = null;

    /// <summary>The A–B range, or null when the loop repeats the whole song.</summary>
    public static (double Start, double End)? Loop(ProjectSettings s) =>
        s.LoopStartSec is { } a && s.LoopEndSec is { } b ? (a, b) : null;
}
