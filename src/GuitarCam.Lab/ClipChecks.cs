using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>Regression cases for native clip gestures, recording anchors and non-destructive lane operations.</summary>
static class ClipChecks
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Clips: {text}");
            n++;
        }
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        var media = new Media { DurationSec = 10, SyncMs = -80, Audio = "media/original.wav" };
        var original = new Clip { MediaId = media.Id, AtSec = 0.92 };
        Clip Drag(Clip c, ClipEditMode mode, double dx, bool free = false, double head = 0, params ClipEnvelope.ClipWindow[] others) =>
            ClipEditing.Drag(c, media, mode, dx, 100, head, 0.5, free, others);
        // with a grid a move goes in grid steps and nothing else pulls it; the magnets work with the grid off
        Clip Magnet(double dx, double head, params ClipEnvelope.ClipWindow[] others) =>
            ClipEditing.Drag(original, media, ClipEditMode.Move, dx, 100, head, 0, false, others);
        var moved = Drag(original, ClipEditMode.Move, 0.41);
        Check(Near(moved.AtSec, 1.42) && Near(ClipEditing.Window(moved, media).Anchor, 1.5), "el anclaje cae en grilla y conserva 80 ms de sincronía");
        Check(Near(original.AtSec, 0.92) && media.SyncMs == -80, "calcular un gesto no muta el clip ni la grabación");
        var offGrid = Drag(new Clip { MediaId = media.Id, AtSec = 0.72 }, ClipEditMode.Move, 0.41);
        Check(Near(offGrid.AtSec, 1.22) && Near(ClipEditing.Window(offGrid, media).Anchor, 1.3), "fuera de grilla, se mueve un paso y conserva su desfasaje");
        Check(Near(Drag(original, ClipEditMode.Move, 0.413, true).AtSec, 1.333), "Alt evita todos los imanes");
        Check(Near(Magnet(0.41, 1.44).AtSec, 1.36), "sin grilla, el cabezal imanta dentro de 8 píxeles");
        Check(Near(Drag(original, ClipEditMode.Move, 0.41, false, 1.44).AtSec, 1.42), "con grilla, el cabezal no tira");
        var other = ClipEnvelope.Window(3, 4.05);
        Check(Near(Magnet(3, 0, other).AtSec, 3.97), "sin grilla, el anclaje se imanta al borde de otro clip");
        Check(Near(ClipEditing.Window(Drag(original, ClipEditMode.Move, -100, true), media).End, 0.1), "siempre quedan 0,1 segundos visibles al cruzar cero");
        var cut = new Clip { MediaId = media.Id, AtSec = -2, TrimStartSec = 0.5 };
        Check(Near(Drag(cut, ClipEditMode.TrimStart, 0.2, true).TrimStartSec, 2.2), "recorte positivo incorpora la parte cortada por cero");
        Check(Near(Drag(cut, ClipEditMode.TrimStart, -0.2, true).TrimStartSec, 0.3), "recorte negativo recupera el original");
        Check(Near(Drag(original, ClipEditMode.TrimEnd, -100, true).TrimEndSec, 9.9), "recortar mantiene la duración mínima");
        var faded = new Clip { MediaId = media.Id, FadeInSec = 2, FadeOutSec = 3 };
        Check(Near(Drag(faded, ClipEditMode.FadeIn, 100, true).FadeInSec, 7), "fade in no pisa el fade out");
        Check(Near(Drag(faded, ClipEditMode.FadeOut, -100, true).FadeOutSec, 8), "fade out no pisa el fade in");
        var p = new Project { Media = [media], Tracks = [new Track { Name = "Base" }, new Track { Name = "Punteo" }] };
        var left = p.Tracks[0]; var right = p.Tracks[1];
        left.Channel.Pan = -0.8; right.Channel.Pan = 0.8;
        left.Clips.Add(original);
        double offset = original.AtSec;
        ClipEditing.MoveToTrack(p, original.Id, right.Id);
        Check(left.Clips.Count == 0 && ReferenceEquals(right.Clips[0], original) && Near(original.AtSec, offset), "cambiar de pista conserva el tiempo");
        Check(left.Channel.Pan == -0.8 && right.Channel.Pan == 0.8, "las tomas se quedan en sus pistas");
        var second = new Clip { MediaId = media.Id, AtSec = 8 };
        left.Clips.Add(second);
        ClipEditing.MoveToTrack(p, original.Id, left.Id, second.Id);
        Check(left.Clips[0].Id == original.Id && right.Clips[0].Id == second.Id && second.AtSec == 8, "intercambio sin mover los tiempos");
        left.Clips.Add(new Clip { MediaId = media.Id, AtSec = 12 });
        ClipEditing.RemoveClip(p, original.Id);
        Check(left.Clips.Count == 1 && left.Name == "Base" && p.Media.Count == 1, "quitar un clip no quita otros ni borra medios");
        ClipEditing.RemoveTrack(p, left.Id);
        ClipEditing.RemoveTrack(p, right.Id);
        string empty = ClipEditing.AddEmpty(p);
        p.Tracks[0].Channel.Volume = 0.4;
        ClipEditing.AddMedia(p, media.Id, empty);
        Check(p.Tracks.Count == 1 && p.Tracks[0].Channel.Volume == 0.4 && p.Tracks[0].Clips.Count == 1, "historial llena la pista seleccionada y hereda su canal");
        string duplicateName = ClipEditing.AddEmpty(p);
        string third = ClipEditing.AddEmpty(p);
        Check(p.Tracks.Select(t => t.Name).Distinct().Count() == 3, "pistas vacías con nombres únicos");
        ClipEditing.Reorder(p, 0, 2);
        Check(p.Tracks[0].Id == duplicateName && p.Tracks[1].Id == third && p.Tracks[2].Id == empty, "reordenar mueve el elemento y desplaza los demás");
        var clip = p.Tracks[2].Clips[0];
        clip.AtSec = -2; clip.TrimStartSec = 0.5;
        ClipEditing.FixCut(p, clip.Id);
        Check(clip.TrimStartSec == 2 && ClipEditing.Window(clip, media).Cut == 0, "fijar el corte en cero");
        ClipEditing.ToStart(p, clip.Id);
        Check(Near(ClipEditing.Window(clip, media).Anchor, 0), "llevar al inicio mueve el anclaje");
        clip.FadeInSec = 2; clip.VideoFadeOutSec = 4;
        ClipEditing.Reset(p, clip.Id);
        Check(clip.TrimStartSec == 0 && clip.FadeInSec == 0 && clip.VideoFadeOutSec == 0, "restaurar incluye los fades de video");
        var undo = new UndoManager();
        undo.Checkpoint(p);
        string track = p.Tracks[2].Id;
        ClipEditing.RemoveClip(p, clip.Id);
        Check(undo.Undo(p) && p.Tracks.Find(t => t.Id == track)!.Clips[0].Id == clip.Id && p.Media[0].Audio == "media/original.wav",
            "deshacer recupera el clip y deja intacta la ruta de grabación");
        var pan = InspectorEditing.Pan(0, 0, 80, 40);
        Check(pan.X == -0.5 && pan.Y == -0.25, "encuadre arrastra con la dirección y escala de la web");
        pan = InspectorEditing.Pan(0.8, -0.8, -1600, 1600);
        Check(pan.X == 1 && pan.Y == -1, "encuadre no sale del rango del panel");
        Check(InspectorEditing.Zoom(1, false) == 1 && InspectorEditing.Zoom(2.48, true) == 2.5,
            "rueda respeta los límites de zoom");
        Check(InspectorEditing.Zoom(1.2, true) == 1.28, "rueda aumenta el zoom en 0,08");
        InspectorEditing.Frame(clip, 2, 0.5, -0.25, true);
        InspectorEditing.ResetFrame(clip);
        Check(clip.Framing.Zoom == 1 && clip.Framing.PanX == 0 && clip.Framing.PanY == 0 && clip.Framing.Mirror,
            "resetear encuadre conserva el espejo como en la web");
        var overrideClip = new Clip { MediaId = media.Id, AtSec = .92 };
        Check(overrideClip.EffectiveSyncMs(media) == -80 && overrideClip.EffectiveVideoOffsetMs(media) == 80,
            "clips anteriores heredan ambas correcciones del medio");
        double anchor = ClipEditing.Window(overrideClip, media).Anchor;
        InspectorEditing.SetSync(overrideClip, media, -30);
        Check(Near(overrideClip.AtSec, .97) && Near(ClipEditing.Window(overrideClip, media).Anchor, anchor),
            "cambiar sincronía por clip mueve el audio y conserva el anclaje");
        overrideClip.VideoOffsetMs = -25;
        Check(overrideClip.EffectiveSyncMs(media) == -30 && overrideClip.EffectiveVideoOffsetMs(media) == -25
            && media.SyncMs == -80 && media.VideoOffsetMs == 80 && original.SyncMs == null,
            "los overrides no cambian el medio ni sus otros clips");
        var snapped = ClipEditing.Drag(overrideClip, media, ClipEditMode.Move, .41, 100, 0, .5, false, []);
        Check(Near(ClipEditing.Window(snapped, media).Anchor, 1.5), "el imán usa el override del clip");
        var zero = new Clip { SyncMs = 0, VideoOffsetMs = 0 };
        Check(zero.EffectiveSyncMs(media) == 0 && zero.EffectiveVideoOffsetMs(media) == 0,
            "cero es override explícito, distinto de null");
        var incomplete = new Media { DurationSec = 0 };
        var loaded = new Clip();
        var loadedWindow = ClipEditing.Window(loaded, incomplete, 10.000020833333);
        Check(Near(loadedWindow.End, 10.000020833333), "la edición usa el largo decodificado aun con metadatos sin duración");
        var trimmed = ClipEditing.Drag(loaded, incomplete, ClipEditMode.TrimEnd, -100, 100, 0, 0, true, [], 10);
        Check(Near(trimmed.TrimEndSec, 9.9) && Near(ClipEditing.Window(trimmed, incomplete, 10).End, .1),
            "el recorte usa la misma duración cargada que reproducción");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<Clip>(System.Text.Json.JsonSerializer.Serialize(overrideClip, ProjectStore.Json), ProjectStore.Json)!;
        Check(roundTrip.SyncMs == -30 && roundTrip.VideoOffsetMs == -25 && roundTrip.AtSec == overrideClip.AtSec,
            "los overrides y el anclaje persisten en JSON sin cambiar la versión");
        var inheritedJson = System.Text.Json.JsonSerializer.Serialize(new Clip(), ProjectStore.Json);
        using var inheritedDoc = System.Text.Json.JsonDocument.Parse(inheritedJson);
        Check(!inheritedDoc.RootElement.TryGetProperty("syncMs", out _) && !inheritedDoc.RootElement.TryGetProperty("videoOffsetMs", out _),
            "sin override se conserva el JSON anterior, sin campos de sincronía nuevos");
        InspectorEditing.SetSync(overrideClip, media, -25);
        Check(Near(overrideClip.AtSec, .975), "nudge de cinco ms conserva la compensación anterior");
        InspectorEditing.SetSync(overrideClip, media, 0);
        Check(overrideClip.SyncMs == 0 && Near(overrideClip.AtSec, 1), "reset de sincronía conserva la colocación dla toma");
        var roleClip = new Clip { Extra = new() { ["role"] = System.Text.Json.JsonSerializer.SerializeToElement("solo") } };
        Check(InspectorEditing.DefaultCompPreset(roleClip) == "lead" && InspectorEditing.DefaultCompPreset(new Clip(), "rhythm") == "rhythm",
            "el reset del compresor usa el rol original y admite proyectos nativos anteriores");
        var sp = new Project { Media = [media], Tracks = [new Track { Name = "Base" }] };
        var whole = new Clip { MediaId = media.Id, AtSec = 1.08, TrimStartSec = 0.5, TrimEndSec = 1, FadeInSec = 0.3, FadeOutSec = 0.4, GainDb = -2 };
        sp.Tracks[0].Clips.Add(whole);
        var before = ClipEditing.Window(whole, media);
        string rightId = ClipEditing.Split(sp, whole.Id, 4.2);
        var piece = ClipEditing.Find(sp, rightId).Clip;
        var wl = ClipEditing.Window(whole, media); var wr = ClipEditing.Window(piece, media);
        Check(sp.Tracks[0].Clips.Count == 2 && sp.Tracks[0].Clips[1] == piece && Near(wl.End, wr.Start) && Near(wr.Start, 4.2),
            "dividir deja dos clips pegados en el cabezal");
        Check(Near(wl.Start, before.Start) && Near(wr.End, before.End) && piece.AtSec == whole.AtSec && piece.MediaId == media.Id,
            "dividir no mueve el audio ni cambia el medio");
        Check(whole.FadeInSec == 0.3 && whole.FadeOutSec == 0 && piece.FadeInSec == 0 && piece.FadeOutSec == 0.4 && piece.GainDb == -2,
            "la izquierda conserva el fade in y la derecha el fade out");
        bool refused = false;
        try { ClipEditing.Split(sp, whole.Id, wl.Start + 0.05); } catch (ArgumentException) { refused = true; }
        Check(refused, "no divide pegado al borde");
        string copyId = ClipEditing.Duplicate(sp, piece.Id);
        var wc = ClipEditing.Window(ClipEditing.Find(sp, copyId).Clip, media);
        Check(sp.Tracks[0].Clips.Count == 3 && Near(wc.Start, wr.End) && Near(wc.End - wc.Start, wr.End - wr.Start),
            "duplicar pone la copia justo después del original");
        var cutAtZero = new Clip { MediaId = media.Id, AtSec = -1 };
        sp.Tracks[0].Clips.Add(cutAtZero);
        var wz = ClipEditing.Window(ClipEditing.Find(sp, ClipEditing.Duplicate(sp, cutAtZero.Id)).Clip, media);
        Check(Near(wz.Start, 9) && Near(wz.End, 19), "duplicar un clip cortado en 0 copia la toma entera");
        // delete a range: a clip of the media from 20 s to 30 s (AtSec 20, 10 s long), fades 0.5 / 0.7
        var rp = new Project { Media = [media], Tracks = [new Track { Name = "R" }, new Track { Name = "Otra" }] };
        var rc = new Clip { MediaId = media.Id, AtSec = 20, FadeInSec = 0.5, FadeOutSec = 0.7 };
        rp.Tracks[0].Clips.Add(rc);
        var other2 = new Clip { MediaId = media.Id, AtSec = 20 };
        rp.Tracks[1].Clips.Add(other2);
        Check(ClipEditing.DeleteRange(rp, rp.Tracks[0].Id, 40, 45) == 0 && ClipEditing.DeleteRange(rp, rp.Tracks[0].Id, 25, 25) == 0,
            "un rango que no toca el clip, o vacío, no cambia nada");
        Check(ClipEditing.DeleteRange(rp, rp.Tracks[0].Id, 23, 26) == 1 && rp.Tracks[0].Clips.Count == 2, "un rango adentro parte el clip en dos");
        var rl = ClipEditing.Window(rp.Tracks[0].Clips[0], media); var rr = ClipEditing.Window(rp.Tracks[0].Clips[1], media);
        Check(Near(rl.Start, 20) && Near(rl.End, 23) && Near(rr.Start, 26) && Near(rr.End, 30), "queda lo de antes y lo de después, con el hueco en el medio");
        Check(rp.Tracks[0].Clips[0].FadeInSec == 0.5 && rp.Tracks[0].Clips[0].FadeOutSec == 0
            && rp.Tracks[0].Clips[1].FadeInSec == 0 && rp.Tracks[0].Clips[1].FadeOutSec == 0.7, "los bordes del corte no tienen fade; los de afuera sí");
        Check(rp.Tracks[0].Clips.All(c => c.AtSec == 20 && c.MediaId == media.Id) && Near(ClipEditing.Window(other2, media).End, 30),
            "el audio no se mueve, el medio es el mismo y otras pistas no cambian");
        Check(ClipEditing.DeleteRange(rp, rp.Tracks[0].Id, 24, 27) == 1 && rp.Tracks[0].Clips.Count == 2
            && Near(ClipEditing.Window(rp.Tracks[0].Clips[1], media).Start, 27) && Near(ClipEditing.Window(rp.Tracks[0].Clips[1], media).End, 30),
            "un rango que pisa el principio de un clip lo deja sin ese pedazo");
        Check(ClipEditing.DeleteRange(rp, rp.Tracks[0].Id, 21, 29.95) == 2 && rp.Tracks[0].Clips.Count == 1
            && Near(ClipEditing.Window(rp.Tracks[0].Clips[0], media).End, 21), "lo que sobra con menos de 0,1 s se va, y el final se recorta");
        Check(ClipEditing.DeleteRange(rp, rp.Tracks[0].Id, 0, 100) == 1 && rp.Tracks[0].Clips.Count == 0, "un rango que cubre todo quita los clips");
        var rq = new Project { Media = [media], Tracks = [new Track { Name = "Q" }] };
        rq.Tracks[0].Clips.Add(new Clip { MediaId = media.Id, AtSec = 0, TrimStartSec = 2, FadeInSec = 1 });
        ClipEditing.DeleteRange(rq, rq.Tracks[0].Id, 3, 6);
        var qr = ClipEditing.Window(rq.Tracks[0].Clips[1], media);
        Check(rq.Tracks[0].Clips.Count == 2 && Near(qr.Start, 6) && Near(qr.End, 10), "con un recorte previo también se corta en el lugar justo");
        // clip gain and volume curve (native only)
        ClipGainCurve.Point[] pts = [new(1, 0), new(3, -12)];
        Check(ClipGainCurve.DbAt(pts, 0) == 0 && ClipGainCurve.DbAt(pts, 2) == -6 && ClipGainCurve.DbAt(pts, 9) == -12 && ClipGainCurve.DbAt([], 5) == 0,
            "curva: se mantiene antes y después, recta en dB entre puntos, plana sin puntos");
        Check(ClipGainCurve.Gain(ClipGainCurve.MinDb) == 0 && Near(ClipGainCurve.Gain(-6), Math.Pow(10, -0.3)), "curva: abajo de todo es silencio");
        var curved = new Clip { MediaId = media.Id, GainDb = 40, GainCurve = [new() { Sec = 4, Db = -99 }, new() { Sec = 2, Db = 3 }, new() { Sec = double.NaN, Db = 0 }] };
        var raw = new Project { Tracks = [new Track { Clips = [curved] }] };
        raw.Media.Add(media);
        var curvedProject = ProjectStore.Parse(System.Text.Json.JsonSerializer.Serialize(raw, ProjectStore.Json));
        curved = curvedProject.Tracks[0].Clips[0];
        Check(curved.GainDb == Clip.MaxGainDb && curved.GainCurve.Count == 2 && curved.GainCurve[0].Sec == 2 && curved.GainCurve[1].Db == ClipGainCurve.MinDb,
            "curva: al abrir se ordena, se descarta lo roto y se acota");
        string curvedRight = ClipEditing.Split(curvedProject, curved.Id, 3);
        var rightClip = ClipEditing.Find(curvedProject, curvedRight).Clip;
        Check(rightClip.GainCurve.Count == 2 && rightClip.GainCurve[0].Sec == 2 && !ReferenceEquals(rightClip.GainCurve, curved.GainCurve),
            "curva: al dividir, cada pedazo se queda con la curva entera (sigue pegada a la toma)");
        ClipEditing.Reset(curvedProject, curved.Id);
        Check(curved.GainDb == 0 && curved.GainCurve.Count == 0, "restaurar el clip saca la ganancia y la curva");

        // the render follows the curve: full level before the dip, silence at the bottom, half way in between
        const int sr = 1000;
        var ones = Enumerable.Repeat(1f, 4 * sr).ToArray();
        var spec = new MixClipSpec { Left = ones, Right = ones, GainCurve = [new(1, 0), new(2, ClipGainCurve.MinDb), new(3, ClipGainCurve.MinDb)] };
        var bl = new float[4 * sr];
        var br = new float[4 * sr];
        TrackDspChain.SampleClips([spec], 0, bl.Length, sr, bl, br);
        Check(Math.Abs(bl[500] - 1) < 1e-4 && bl[2500] == 0 && Math.Abs(bl[1500] - (float)ClipGainCurve.Gain(ClipGainCurve.MinDb / 2)) < 0.01,
            "el render sigue la curva (antes, en el fondo y a mitad de la bajada)");
        Console.WriteLine($"Edición de clips: {n} comprobaciones correctas (sin modificar archivos).");
        return 0;
    }
}
