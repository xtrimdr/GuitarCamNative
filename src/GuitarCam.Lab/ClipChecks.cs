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
        Clip Drag(Clip c, ClipEditMode mode, double dx, bool free = false) => ClipEditing.Drag(c, media, mode, dx, 0.5, free);
        // with the grid off nothing pulls unless the snap to other clips is on (then their edges pull within 8 px)
        Clip Magnet(double dx, double grid, params ClipEnvelope.ClipWindow[]? others) =>
            ClipEditing.Drag(original, media, ClipEditMode.Move, dx, grid, false, null, others, SnapEdges.Both, 100);
        var moved = Drag(original, ClipEditMode.Move, 0.41);
        Check(Near(moved.AtSec, 1.42) && Near(ClipEditing.Window(moved, media).Anchor, 1.5), "el anclaje cae en grilla y conserva 80 ms de sincronía");
        Check(Near(original.AtSec, 0.92) && media.SyncMs == -80, "calcular un gesto no muta el clip ni la grabación");
        var offGrid = Drag(new Clip { MediaId = media.Id, AtSec = 0.72 }, ClipEditMode.Move, 0.41);
        Check(Near(offGrid.AtSec, 1.22) && Near(ClipEditing.Window(offGrid, media).Anchor, 1.3), "fuera de grilla, se mueve un paso y conserva su desfasaje");
        Check(Near(Drag(original, ClipEditMode.Move, 0.413, true).AtSec, 1.333), "Alt evita todos los imanes");
        Check(Math.Abs(Drag(original, ClipEditMode.Move, 0.0002, true).AtSec - 0.9202) < 1.0 / 48000, "Alt mueve más fino que un milisegundo (de a muestra)");
        var loose = new Project { Media = [media], Tracks = [new Track { Clips = [new Clip { MediaId = media.Id, AtSec = 0.72 }] }] };
        string looseId = loose.Tracks[0].Clips[0].Id;
        ClipEditing.AlignToGrid(loose, looseId, 0.25);
        Check(Near(ClipEditing.Window(loose.Tracks[0].Clips[0], media).Anchor, 0.75), "alinear pone el anclaje en la línea del imán más cercana");
        var other = ClipEnvelope.Window(3, 4.05);
        Check(Near(Magnet(0.03, 0, null).AtSec, 0.95), "sin imán no se pega a nada: ni al cero, ni al cabezal");
        Check(Near(Magnet(3.1, 0, other).AtSec, 4.05), "con imán a otros clips, el borde visible se pega al borde de otro clip");
        ClipEditing.Drag(original, media, ClipEditMode.Move, 3.1, 0, false, out var stuck, null, [other], SnapEdges.Both, 100);
        ClipEditing.Drag(original, media, ClipEditMode.Move, 1, 0, false, out var loose2, null, [other], SnapEdges.Both, 100);
        var gridNearer = ClipEditing.Drag(original, media, ClipEditMode.Move, 3.06, 0.5, false, out var gridStuck, null, [other], SnapEdges.Both, 100);
        Check(Near(gridNearer.AtSec, 3.92) && gridStuck == null, "con grilla, el borde no tira si el clip ya ubicado queda lejos");
        // the video's case: a bar's step, the pointer next to an edge but the clip a quarter of a step away: no leap
        var barStep = ClipEditing.Drag(original, media, ClipEditMode.Move, 2.6, 2, false, out var barStuck, null, [ClipEnvelope.Window(3, 3.47)], SnapEdges.Both, 100);
        Check(Near(barStep.AtSec, 2.92) && barStuck == null, "con pasos grandes, un borde cerca del mouse no hace saltar al clip");
        Check(Near(ClipEditing.Drag(original, media, ClipEditMode.Move, 3.1, 0, false, null, [other], SnapEdges.End, 100).AtSec, 4.02),
            "con imán solo al final, el comienzo no se pega");
        var trimmedOther = ClipEnvelope.Window(3, 3.55, 0.5);
        Check(Near(Magnet(3.1, 0, trimmedOther).AtSec, 4.05), "se pega al borde visible de un clip recortado, no al comienzo escondido de la toma");
        Check(stuck is { } s && Near(s, 4.05) && loose2 == null, "informa contra qué borde se pegó (para la guía), y nada si no se pegó");
        Check(Near(Magnet(3.12, 0.5, ClipEnvelope.Window(3, 3.97)).AtSec, 3.97) && Near(Magnet(3.12, 0.5, null).AtSec, 3.92),
            "con grilla, el borde corrige el paso cuando el clip ubicado queda a menos de 8 px");
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
        var snapped = ClipEditing.Drag(overrideClip, media, ClipEditMode.Move, .41, .5, false);
        Check(Near(ClipEditing.Window(snapped, media).Anchor, 1.5), "el imán usa el override del clip");
        var zero = new Clip { SyncMs = 0, VideoOffsetMs = 0 };
        Check(zero.EffectiveSyncMs(media) == 0 && zero.EffectiveVideoOffsetMs(media) == 0,
            "cero es override explícito, distinto de null");
        var incomplete = new Media { DurationSec = 0 };
        var loaded = new Clip();
        var loadedWindow = ClipEditing.Window(loaded, incomplete, 10.000020833333);
        Check(Near(loadedWindow.End, 10.000020833333), "la edición usa el largo decodificado aun con metadatos sin duración");
        var trimmed = ClipEditing.Drag(loaded, incomplete, ClipEditMode.TrimEnd, -100, 0, true, 10);
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
        // several clips on two tracks: one block after the block's end, and Ctrl+C / Ctrl+V keep the layout
        var bp = new Project { Media = [media], Tracks = [new Track { Name = "A" }, new Track { Name = "B" }, new Track { Name = "C" }] };
        var ba = new Clip { MediaId = media.Id, AtSec = 2, TrimEndSec = 6 };
        var bb = new Clip { MediaId = media.Id, AtSec = 3, TrimEndSec = 4 };
        bp.Tracks[0].Clips.Add(ba);
        bp.Tracks[1].Clips.Add(bb);
        var many = ClipEditing.DuplicateMany(bp, [ba.Id, bb.Id]);
        var wa = ClipEditing.Window(ClipEditing.Find(bp, many[0]).Clip, media); var wb = ClipEditing.Window(ClipEditing.Find(bp, many[1]).Clip, media);
        Check(bp.Tracks[0].Clips.Count == 2 && bp.Tracks[1].Clips.Count == 2 && Near(wa.Start, 9) && Near(wb.Start, 10),
            "duplicar varios clips los copia en bloque, cada uno en su pista y a la misma distancia");
        var board = ClipClipboard.Take(bp, [ba.Id, bb.Id])!;
        var pasted = board.Paste(bp, bp.Tracks[2].Id, 20);
        var (ta, pa) = ClipEditing.Find(bp, pasted[0]); var (tb, pb) = ClipEditing.Find(bp, pasted[1]);
        Check(ta == bp.Tracks[1] && tb == bp.Tracks[2] && Near(ClipEditing.Window(pa, media).Start, 20) && Near(ClipEditing.Window(pb, media).Start, 21),
            "pegar en la última pista sube el bloque para que entre y lo pone en el cabezal");
        ba.GainDb = -6;
        Check(board.Items[0].Clip.GainDb == 0 && board.Paste(bp, null, 0).Count == 2 && bp.Tracks[0].Clips.Count == 3,
            "lo copiado no cambia si se edita el original, y sin pista vuelve a la suya");
        bool foreign = false;
        try { board.Paste(new Project { Media = [media], Tracks = [new Track(), new Track()] }, null, 0); } catch (ArgumentException) { foreign = true; }
        Check(foreign, "no pega en otro proyecto");
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
        // delete a range closing the gap, over two of three tracks: what follows slides left on those tracks only
        var cp = new Project { Media = [media], Tracks = [new Track { Name = "A" }, new Track { Name = "B" }, new Track { Name = "C" }] };
        cp.Tracks[0].Clips.Add(new Clip { MediaId = media.Id, AtSec = 0 });
        cp.Tracks[0].Clips.Add(new Clip { MediaId = media.Id, AtSec = 12 });
        cp.Tracks[1].Clips.Add(new Clip { MediaId = media.Id, AtSec = 5 });
        cp.Tracks[2].Clips.Add(new Clip { MediaId = media.Id, AtSec = 12 });
        ClipEditing.DeleteRangeClosing(cp, [cp.Tracks[0].Id, cp.Tracks[1].Id], 4, 6);
        var ca = cp.Tracks[0].Clips.Select(c => ClipEditing.Window(c, media)).OrderBy(w => w.Start).ToList();
        var cb = cp.Tracks[1].Clips.Select(c => ClipEditing.Window(c, media)).ToList();
        Check(ca.Count == 3 && Near(ca[0].End, 4) && Near(ca[1].Start, 4) && Near(ca[1].End, 8) && Near(ca[2].Start, 10) && Near(ca[2].End, 20),
            "cerrar el hueco corre todo lo de después el largo del rango");
        Check(cb.Count == 1 && Near(cb[0].Start, 4) && Near(cb[0].End, 13), "un clip que empieza adentro del rango pierde ese pedazo y se corre al inicio del rango…");
        Check(Near(ClipEditing.Window(cp.Tracks[2].Clips[0], media).Start, 12), "…y las pistas fuera del rango no se mueven");
        // markers follow only when the gap closes on every track
        var mBefore = ProjectMarkers.Add(cp, 3, "Antes"); var mInside = ProjectMarkers.Add(cp, 5, "Adentro"); var mAfter = ProjectMarkers.Add(cp, 9, "Después");
        ClipEditing.DeleteRangeClosing(cp, [cp.Tracks[0].Id, cp.Tracks[1].Id], 4, 6);
        Check(mBefore.Sec == 3 && mInside.Sec == 5 && mAfter.Sec == 9, "cerrar el hueco en algunas pistas no mueve los marcadores");
        ClipEditing.DeleteRangeClosing(cp, cp.Tracks.Select(t => t.Id), 4, 6);
        Check(mBefore.Sec == 3 && mInside.Sec == 4 && Near(mAfter.Sec, 7) && cp.Markers.SequenceEqual<Marker>([mBefore, mInside, mAfter]),
            "cerrándolo en todas, los de después se corren y los de adentro quedan al inicio del rango");
        var front = cp.Tracks[0].Clips[0];
        Check(ClipEditing.BringToFront(cp, front.Id) && cp.Tracks[0].Clips[^1] == front && !ClipEditing.BringToFront(cp, front.Id),
            "traer al frente pasa el clip al final de la pista (el que se ve)");
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

        // video transitions: stored on the clip that comes in, found where another ends, never touching the sound
        var tp = new Project { Media = [media], Tracks = [new Track { Clips = [new Clip { MediaId = media.Id }] }] };
        string first = tp.Tracks[0].Clips[0].Id;
        ClipEditing.SetTransition(tp, first, "crossfade", 9);
        string joinId = ClipEditing.Split(tp, first, 4);
        var a = tp.Tracks[0].Clips[0];
        var b = tp.Tracks[0].Clips[1];
        Check(a.Transition == "crossfade" && b.Transition == "cut" && a.TransitionSec == GuitarCam.Audio.ClipTransitions.MaxSec,
            "dividir deja la transición en la pieza de la izquierda y la nueva entra con corte; el largo se limita");
        Check(ClipEditing.Joins(tp, tp.Tracks[0]) is [{ Join: null } cutJoin] && cutJoin.In == b, "dos piezas de un corte se tocan: una unión, con corte");
        var fadesBefore = (a.FadeInSec, a.FadeOutSec, b.FadeInSec, b.FadeOutSec);
        ClipEditing.SetTransition(tp, joinId, "dip", 1);
        var dip = ClipEditing.Joins(tp, tp.Tracks[0]) is [{ Join: { } dj }] ? dj : default;
        Check(dip.Kind == GuitarCam.Audio.TransitionKind.Dip && Near(dip.At, ClipEditing.Window(b, media).Start)
            && Near(dip.Sec, 1) && dip.Overhang == 0, "pasar por negro va centrado en la unión y no muestra nada fuera de los clips");
        Check((a.FadeInSec, a.FadeOutSec, b.FadeInSec, b.FadeOutSec) == fadesBefore, "la transición no toca los fundidos de audio");
        var dw = GuitarCam.Audio.ClipTransitions.Weights(dip, dip.At - 0.25);
        Check(Near(dw.Out, 0.5) && dw.In == 0 && GuitarCam.Audio.ClipTransitions.Weights(dip, dip.At).Out == 0, "pasar por negro: baja el que sale hasta la unión, después sube el otro");
        ClipEditing.SetTransition(tp, joinId, "crossfade", 1);
        var xf = ClipEditing.Joins(tp, tp.Tracks[0])[0].Join!.Value;
        var xw = GuitarCam.Audio.ClipTransitions.Weights(xf, xf.At);
        Check(xf.Overhang == 0.5 && xw.Out == 1 && Near(xw.In, 0.5), "fundido cruzado: los dos se ven medio segundo de cada lado, a mitad de camino en la unión");
        // the sound crosses over the same stretch, with the audio the trims hide
        var xa = ClipEditing.Window(a, media);
        var xb = ClipEditing.Window(b, media);
        var aj = GuitarCam.Audio.ClipTransitions.Audio(xf, xa, xb, true);
        Check(aj.Overlap && aj.Linear && Near(aj.Start, xf.Start) && Near(aj.End, xf.End)
            && Near(aj.OutGain(xf.At) + aj.InGain(xf.At), 1), "dos pedazos de una toma se funden en línea recta: suman exactamente la toma");
        var ep = GuitarCam.Audio.ClipTransitions.Audio(xf, xa, xb, false);
        Check(!ep.Linear && Near(ep.OutGain(xf.At) * ep.OutGain(xf.At) + ep.InGain(xf.At) * ep.InGain(xf.At), 1), "tomas distintas se funden a potencia constante");
        var bare = GuitarCam.Audio.ClipTransitions.Audio(xf, ClipEnvelope.Window(4, 0), ClipEnvelope.Window(6, 4), false);
        Check(!bare.Overlap && bare.OutGain(4) == 0 && bare.InGain(4) == 0 && bare.OutGain(3.99) == 1 && bare.InGain(4.01) == 1,
            "sin audio escondido de ningún lado, cruza con un fundido de 10 ms en la unión (sin clic)");
        // rendered: a steady take split in two with a crossfade stays steady across the join
        const int xr = 1000;
        var steady = Enumerable.Repeat(1f, 10 * xr).ToArray();
        MixClipSpec[] pieces =
        [
            new() { Left = steady, Right = steady, DurationSec = 10, TrimEndSec = 6, TakeId = "t" },
            new() { Left = steady, Right = steady, DurationSec = 10, TrimStartSec = 4, Transition = "crossfade", TransitionSec = 1, TakeId = "t" },
        ];
        var xl = new float[10 * xr];
        var xrr = new float[10 * xr];
        TrackDspChain.SampleClips(pieces, 0, xl.Length, xr, xl, xrr, TrackDspChain.AudioJoins(pieces, xr));
        Check(xl.All(v => Math.Abs(v - 1) < 1e-4), "una toma dividida con fundido cruzado suena igual que entera");
        var cutLeft = new float[10 * xr];
        TrackDspChain.SampleClips(pieces, 0, cutLeft.Length, xr, cutLeft, new float[10 * xr], TrackDspChain.AudioJoins(
            [pieces[0], new() { Left = steady, Right = steady, DurationSec = 10, TrimStartSec = 4, Transition = "crossfade", TransitionSec = 1, TakeId = "u" }], xr));
        Check(cutLeft[3500] > 0.99 && cutLeft[3999] < 1.42 && cutLeft[4000] > 1.3, "tomas distintas: a potencia constante (sube hasta √2 con la misma señal)");
        b.AtSec += 1;
        Check(ClipEditing.Joins(tp, tp.Tracks[0]).Count == 0, "si el clip se separa, la unión desaparece (y la transición queda guardada sin efecto)");
        Console.WriteLine($"Edición de clips: {n} comprobaciones correctas (sin modificar archivos).");
        return 0;
    }
}
