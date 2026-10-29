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

    /// <summary>
    /// After a range is deleted closing the gap on every track: markers after it slide left by its length, and the ones
    /// inside it go to where it started instead of being lost (deleting one is always the user's call).
    /// Returns how many moved.
    /// </summary>
    public static int CloseGap(Project p, double startSec, double endSec)
    {
        if (!(endSec > startSec)) return 0;
        int moved = 0;
        foreach (var m in p.Markers)
        {
            if (m.Sec <= startSec) continue;
            m.Sec = m.Sec >= endSec ? m.Sec - (endSec - startSec) : startSec;
            moved++;
        }
        Sort(p);
        return moved;
    }

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

    /// <summary>YouTube's rules for chapters in a description: the first at 0:00, at least three, each this long.</summary>
    public const int MinChapters = 3;
    public const double MinChapterSec = 10;

    /// <summary>
    /// The markers as YouTube chapters, one "m:ss Name" a line, to paste in the video's description (the export starts at
    /// the timeline's 0, so the times are the same). The first chapter must be at 0:00: a song whose first marker comes
    /// later gets an "Inicio" before it, and one within the first second counts as 0:00. <c>Problem</c> says, in words,
    /// why YouTube would ignore them (too few, one too short); the text is made anyway.
    /// </summary>
    public static (string Text, string? Problem) Chapters(Project p, double lengthSec)
    {
        var list = p.Markers.Where(m => m.Sec < lengthSec || lengthSec <= 0).Select(m => (Sec: m.Sec < 1 ? 0 : Math.Floor(m.Sec), m.Name)).ToList();
        if (list.Count == 0 || list[0].Sec > 0) list.Insert(0, (0, Texts.T("markers.start", "Inicio")));
        // two markers in the same second: the later one wins, YouTube would take only one anyway
        for (int i = list.Count - 2; i >= 0; i--) if (list[i].Sec == list[i + 1].Sec) list.RemoveAt(i);
        bool hours = lengthSec >= 3600 || list[^1].Sec >= 3600;
        string Time(double sec)
        {
            var t = TimeSpan.FromSeconds(sec);
            return hours ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }
        string text = string.Join(Environment.NewLine, list.Select(c => $"{Time(c.Sec)} {c.Name}"));
        string? problem = null;
        if (list.Count < MinChapters) problem = Texts.T("markers.chapters.few", "YouTube pide al menos {0} capítulos", MinChapters);
        else
            for (int i = 0; i < list.Count; i++)
            {
                double end = i + 1 < list.Count ? list[i + 1].Sec : lengthSec;
                if (end > 0 && end - list[i].Sec < MinChapterSec)
                {
                    problem = Texts.T("markers.chapters.short", "«{0}» dura menos de {1} s y YouTube no lo tomaría", list[i].Name, MinChapterSec);
                    break;
                }
            }
        return (text, problem);
    }

    /// <summary>The A–B range, or null when the loop repeats the whole song.</summary>
    public static (double Start, double End)? Loop(ProjectSettings s) =>
        s.LoopStartSec is { } a && s.LoopEndSec is { } b ? (a, b) : null;
}
