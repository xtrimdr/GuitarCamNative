using System.Globalization;
using System.Text.Json.Nodes;
using WildTrack.Audio;
using Part = WildTrack.Projects.ProjectHistory.Part;

namespace WildTrack.Projects;

/// <summary>
/// Names an undo step for Historial → Pasos by comparing the editable state before and after it (the JSON that
/// <see cref="UndoManager"/> keeps), so every edit gets its line without each of the dozens of places that edit having
/// to say what it did. Tracks, clips, folders, buses, slots and markers are matched by id. A step that changed several
/// things of one kind says it once ("Moviste 3 clips"); otherwise it says the first and how many more. A recording,
/// which the diff can't tell from a paste, brings its own label (<see cref="Recorded"/>).
/// </summary>
public static class EditSteps
{
    /// <summary>
    /// What a step says. Icon: plus, trash, move, cut, mic, volume, fx, folder, marker, color, video, lock, edit.
    /// </summary>
    public sealed record Label(string Icon, IReadOnlyList<Part> Parts)
    {
        public string Text => string.Concat(Parts.Select(p => p.Text));
    }

    /// <summary>One thing that changed: <see cref="Group"/> lets several of a kind say it once, <see cref="Subject"/> is
    /// the track, clip, bus… it is about.</summary>
    sealed record Change(string Icon, List<Part> Parts, string Group, string? Subject);

    static string T(string key, string es, params object?[] args) => Texts.T(key, es, args);
    static Part Q(string s) => new($"«{s}»", true);
    static Part P(string s) => new(s);

    /// <summary>The label of a take (or takes, or notes) just recorded: the diff would only see clips added.</summary>
    public static Label Recorded(IReadOnlyList<string> takes, IReadOnlyList<string> midiTracks)
    {
        if (takes.Count == 1 && midiTracks.Count == 0) return new("mic", [P(T("step.recorded", "Grabaste ")), Q(takes[0])]);
        if (takes.Count == 0 && midiTracks.Count == 1) return new("mic", [P(T("step.recordedNotes", "Grabaste notas en ")), Q(midiTracks[0])]);
        return new("mic", [P(T("step.recordedMany", "Grabaste {0}", Channels(takes.Count + midiTracks.Count)))]);
    }

    static string Channels(int n) => n == 1 ? T("act.oneChannel", "un canal") : T("act.nChannels", "{0} canales", n);

    /// <summary>The label of the step that took the editable state from <paramref name="before"/> to <paramref name="after"/>.</summary>
    /// <param name="mediaName">A take's name by its media id (the media are not part of the undo state).</param>
    public static Label Describe(string before, string after, Func<string, string?> mediaName)
    {
        JsonObject? a, b;
        try
        {
            a = JsonNode.Parse(before) as JsonObject;
            b = JsonNode.Parse(after) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            a = b = null;
        }
        if (a == null || b == null) return new("edit", [P(T("step.unknown", "Un cambio"))]);
        var changes = new List<Change>();
        var busNames = ById(b["buses"] as JsonArray).Concat(ById(a["buses"] as JsonArray))
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => Name(g.First().Value));
        Tracks(a, b, changes, mediaName, busNames);
        Folders(a["folders"] as JsonArray, b["folders"] as JsonArray, changes, busNames);
        Buses(a["buses"] as JsonArray, b["buses"] as JsonArray, changes, busNames);
        Markers(a["markers"] as JsonArray, b["markers"] as JsonArray, changes);
        Settings(a, b, changes);
        return Combine(changes);
    }

    static Label Combine(List<Change> changes)
    {
        if (changes.Count == 0) return new("edit", [P(T("step.view", "Cambiaste cómo se ve"))]);
        var first = changes[0];
        if (changes.Count == 1) return new(first.Icon, first.Parts);
        int n = changes.Count;
        if (changes.All(c => c.Group == first.Group) && Plural(first.Group, n) is { } many) return new(first.Icon, [P(many)]);
        if (first.Subject != null && changes.All(c => c.Subject == first.Subject))
            return new("edit", [P(T("step.manyOf", "Cambiaste varias cosas de ")), Q(first.Subject)]);
        return new(first.Icon, [.. first.Parts, P(n == 2 ? T("step.oneMore", " y un cambio más") : T("step.more", " y {0} cambios más", n - 1))]);
    }

    /// <summary>The same change on several tracks or clips, said once; null = no such sentence for that kind.</summary>
    static string? Plural(string group, int n) => group switch
    {
        "track-add" => T("step.n.trackAdd", "Agregaste {0} pistas", n),
        "track-remove" => T("step.n.trackRemove", "Borraste {0} pistas", n),
        "track-mute-on" => T("step.n.muteOn", "Silenciaste {0} pistas", n),
        "track-mute-off" => T("step.n.muteOff", "Le sacaste el mute a {0} pistas", n),
        "track-solo-on" => T("step.n.soloOn", "Solo en {0} pistas", n),
        "track-solo-off" => T("step.n.soloOff", "Le sacaste el solo a {0} pistas", n),
        "track-volume" => T("step.n.volume", "Volumen de {0} pistas", n),
        "track-look" => T("step.n.look", "Color de {0} pistas", n),
        "track-folder" => T("step.n.folder", "Cambiaste de carpeta {0} pistas", n),
        "track-lock" => T("step.n.lock", "Bloqueo de {0} pistas", n),
        "clip-add" => T("step.n.clipAdd", "Agregaste {0} clips", n),
        "clip-remove" => T("step.n.clipRemove", "Borraste {0} clips", n),
        "clip-move" => T("step.n.clipMove", "Moviste {0} clips", n),
        "clip-trim" => T("step.n.clipTrim", "Recortaste {0} clips", n),
        "clip-split" => T("step.n.clipSplit", "Cortaste {0} clips", n),
        "clip-fade" => T("step.n.clipFade", "Fundidos de {0} clips", n),
        "clip-gain" => T("step.n.clipGain", "Ganancia de {0} clips", n),
        "clip-lock" => T("step.n.clipLock", "Bloqueo de {0} clips", n),
        "midi-add" => T("step.n.midiAdd", "Agregaste {0} clips MIDI", n),
        "midi-remove" => T("step.n.midiRemove", "Borraste {0} clips MIDI", n),
        "midi-move" => T("step.n.midiMove", "Moviste {0} clips MIDI", n),
        "marker-add" => T("step.n.markerAdd", "Agregaste {0} marcadores", n),
        "marker-remove" => T("step.n.markerRemove", "Borraste {0} marcadores", n),
        "folder-fold" => T("step.n.fold", "Plegaste o desplegaste {0} carpetas", n),
        _ => null,
    };

    // ---------- tracks and clips ----------

    static void Tracks(JsonObject a, JsonObject b, List<Change> changes, Func<string, string?> mediaName, Dictionary<string, string> busNames)
    {
        var before = ById(a["tracks"] as JsonArray);
        var after = ById(b["tracks"] as JsonArray);
        var folderNames = ById(b["folders"] as JsonArray).Concat(ById(a["folders"] as JsonArray))
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => Name(g.First().Value));

        foreach (var (id, t) in after)
            if (!before.ContainsKey(id)) changes.Add(new("plus", [P(T("step.trackAdd", "Agregaste la pista ")), Q(Name(t))], "track-add", null));
        foreach (var (id, t) in before)
            if (!after.ContainsKey(id)) changes.Add(new("trash", [P(T("step.trackRemove", "Borraste la pista ")), Q(Name(t))], "track-remove", null));

        if (Moved(Ids(a["tracks"] as JsonArray, after), Ids(b["tracks"] as JsonArray, before)) is { } moved)
            changes.Add(moved.Length > 0 && after.TryGetValue(moved, out var mt)
                ? new("move", [P(T("step.trackMove", "Moviste la pista ")), Q(Name(mt))], "track-order", null)
                : new("move", [P(T("step.trackOrder", "Reordenaste las pistas"))], "track-order", null));

        Clips(before, after, changes, mediaName);

        foreach (var (id, t) in after)
        {
            if (!before.TryGetValue(id, out var old)) continue;
            string name = Name(t);
            void Add(string icon, string group, params Part[] parts) => changes.Add(new(icon, [.. parts], group, name));
            if (Str(old, "name") != Str(t, "name"))
                Add("edit", "track-name", P(T("step.rename", "Renombraste ")), Q(Name(old)), P(" → "), Q(name));
            if (Str(old, "folderId") != Str(t, "folderId"))
            {
                if (Str(t, "folderId") is { } f) Add("folder", "track-folder", P(T("step.toFolder", "Metiste ")), Q(name), P(T("step.inFolder", " en ")), Q(folderNames.GetValueOrDefault(f, "")));
                else Add("folder", "track-folder", P(T("step.outOfFolder", "Sacaste ")), Q(name), P(T("step.outOfFolder.end", " de su carpeta")));
            }
            Channel(old["channel"] as JsonObject, t["channel"] as JsonObject, name, changes, busNames);
            if (!Same(old, t, "color")) Add("color", "track-look", P(T("step.trackColor", "Color de ")), Q(name));
            if (!Same(old, t, "icon")) Add("color", "track-look", P(T("step.trackIcon", "Ícono de ")), Q(name));
            if (Num(old, "transpose") != Num(t, "transpose"))
                Add("fx", "track-transpose", P(T("step.transpose", "Transponer ")), Q(name), P($": {Signed(Num(t, "transpose"))}"));
            if (Bool(old, "video", true) != Bool(t, "video", true))
                Add("video", "track-video", Bool(t, "video", true)
                    ? P(T("step.videoOn", "Pusiste en el video a ")) : P(T("step.videoOff", "Sacaste del video a ")), Q(name));
            if (Bool(old, "locked") != Bool(t, "locked"))
                Add("lock", "track-lock", P(Bool(t, "locked") ? T("step.lockTrack", "Bloqueaste ") : T("step.unlockTrack", "Desbloqueaste ")), Q(name));
            if (!Same(old, t, "input") || !Same(old, t, "midiInput") || !Same(old, t, "midiChannel"))
                Add("edit", "track-input", P(T("step.input", "Entrada de ")), Q(name));
            if (!Same(old, t, "instrument")) Add("fx", "track-instrument", P(T("step.instrument", "Instrumento de ")), Q(name));
            if (!Same(old, t, "heightScale")) Add("edit", "track-height", P(T("step.height", "Altura de ")), Q(name));
            if (!Same(old, t, "mixerSide")) Add("move", "track-side", P(T("step.side", "Cambiaste de lado en el mixer a ")), Q(name));
            if (!Same(old, t, "kind")) Add("edit", "track-kind", P(T("step.changed", "Cambiaste ")), Q(name));
        }
    }

    /// <summary>Every clip by its id, wherever it is: a clip dragged to another track is the same clip.</summary>
    static Dictionary<string, (string Track, JsonObject Clip)> AllClips(Dictionary<string, JsonObject> tracks, string list)
    {
        var all = new Dictionary<string, (string, JsonObject)>();
        foreach (var (tid, t) in tracks)
            foreach (var (cid, c) in ById(t[list] as JsonArray)) all[cid] = (tid, c);
        return all;
    }

    static void Clips(Dictionary<string, JsonObject> beforeTracks, Dictionary<string, JsonObject> afterTracks, List<Change> changes, Func<string, string?> mediaName)
    {
        var before = AllClips(beforeTracks, "clips");
        var after = AllClips(afterTracks, "clips");
        string TrackName(string id) => Name(afterTracks.GetValueOrDefault(id) ?? beforeTracks[id]);
        string ClipName(JsonObject c) => mediaName(Str(c, "mediaId") ?? "") ?? T("act.aChannel", "un canal");

        // a split leaves the clip shorter and adds one of the same take right after it on that track
        var split = new HashSet<string>();
        foreach (var (id, (tid, c)) in after)
        {
            if (before.ContainsKey(id) || !beforeTracks.ContainsKey(tid)) continue;
            var twin = after.FirstOrDefault(x => x.Key != id && x.Value.Track == tid && before.ContainsKey(x.Key)
                && Str(x.Value.Clip, "mediaId") == Str(c, "mediaId") && !Same(before[x.Key].Clip, x.Value.Clip, "trimEndSec"));
            if (twin.Key == null) continue;
            split.Add(id);
            split.Add(twin.Key);
            changes.Add(new("cut", [P(T("step.split", "Cortaste ")), Q(ClipName(c)), P(T("step.on", " en ")), Q(TrackName(tid))], "clip-split", null));
        }
        foreach (var (id, (tid, c)) in after)
            if (!before.ContainsKey(id) && !split.Contains(id) && beforeTracks.ContainsKey(tid))
                changes.Add(new("plus", [P(T("step.clipAdd", "Agregaste ")), Q(ClipName(c)), P(T("step.to", " a ")), Q(TrackName(tid))], "clip-add", null));
        foreach (var (id, (tid, c)) in before)
            if (!after.ContainsKey(id) && afterTracks.ContainsKey(tid))
                changes.Add(new("trash", [P(T("step.clipRemove", "Borraste ")), Q(ClipName(c)), P(T("step.from", " de ")), Q(TrackName(tid))], "clip-remove", null));

        foreach (var (id, (tid, c)) in after)
        {
            if (split.Contains(id) || !before.TryGetValue(id, out var was)) continue;
            var old = was.Clip;
            string name = ClipName(c);
            Change? change =
                !Same(old, c, "syncMs") || !Same(old, c, "videoOffsetMs") ? new("edit", [P(T("step.sync", "Sincronía de ")), Q(name)], "clip-sync", name)
                : was.Track != tid ? new("move", [P(T("step.clipToTrack", "Moviste ")), Q(name), P(T("step.to", " a ")), Q(TrackName(tid))], "clip-move", null)
                : !Same(old, c, "trimStartSec") || !Same(old, c, "trimEndSec") ? new("cut", [P(T("step.trim", "Recortaste ")), Q(name)], "clip-trim", null)
                : !Same(old, c, "atSec") ? new("move", [P(T("step.clipMove", "Moviste ")), Q(name)], "clip-move", null)
                : !Same(old, c, "fadeInSec") || !Same(old, c, "fadeOutSec") || !Same(old, c, "videoFadeInSec") || !Same(old, c, "videoFadeOutSec")
                    || !Same(old, c, "linkVideoFades") ? new("volume", [P(T("step.fade", "Fundidos de ")), Q(name)], "clip-fade", null)
                : !Same(old, c, "gainDb") ? new("volume", [P(T("step.gain", "Ganancia de ")), Q(name), P($": {Db(Num(c, "gainDb"))}")], "clip-gain", null)
                : !Same(old, c, "gainCurve") ? new("volume", [P(T("step.curve", "Curva de volumen de ")), Q(name)], "clip-curve", name)
                : !Same(old, c, "framing") ? new("video", [P(T("step.framing", "Encuadre de ")), Q(name)], "clip-framing", name)
                : !Same(old, c, "transition") || !Same(old, c, "transitionSec") ? new("video", [P(T("step.transition", "Transición de ")), Q(name)], "clip-transition", name)
                : !Same(old, c, "locked") ? new("lock", [P(Bool(c, "locked") ? T("step.lockClip", "Bloqueaste ") : T("step.unlockClip", "Desbloqueaste ")), Q(name)], "clip-lock", null)
                : JsonNode.DeepEquals(old, c) ? null
                : new("edit", [P(T("step.changed", "Cambiaste ")), Q(name)], "clip-other", name);
            if (change != null) changes.Add(change);
        }

        var midiBefore = AllClips(beforeTracks, "midiClips");
        var midiAfter = AllClips(afterTracks, "midiClips");
        foreach (var (id, (tid, _)) in midiAfter)
            if (!midiBefore.ContainsKey(id) && beforeTracks.ContainsKey(tid))
                changes.Add(new("plus", [P(T("step.midiAdd", "Agregaste un clip MIDI a ")), Q(TrackName(tid))], "midi-add", null));
        foreach (var (id, (tid, _)) in midiBefore)
            if (!midiAfter.ContainsKey(id) && afterTracks.ContainsKey(tid))
                changes.Add(new("trash", [P(T("step.midiRemove", "Borraste un clip MIDI de ")), Q(TrackName(tid))], "midi-remove", null));
        foreach (var (id, (tid, c)) in midiAfter)
        {
            if (!midiBefore.TryGetValue(id, out var was) || JsonNode.DeepEquals(was.Clip, c)) continue;
            string track = TrackName(tid);
            changes.Add(!Same(was.Clip, c, "notes")
                ? new("edit", [P(T("step.notes", "Editaste las notas de ")), Q(track)], "midi-notes", track)
                : was.Track != tid || !Same(was.Clip, c, "atSec")
                    ? new("move", [P(T("step.midiMove", "Moviste un clip MIDI de ")), Q(track)], "midi-move", null)
                    : new("edit", [P(T("step.midiLength", "Largo de un clip MIDI de ")), Q(track)], "midi-length", track));
        }
    }

    /// <summary>A track's (or bus's) fader, pan, mute, solo, slots and routing.</summary>
    static void Channel(JsonObject? old, JsonObject? now, string name, List<Change> changes, Dictionary<string, string> busNames)
    {
        if (old == null || now == null || JsonNode.DeepEquals(old, now)) return;
        void Add(string icon, string group, params Part[] parts) => changes.Add(new(icon, [.. parts], group, name));
        if (Num(old, "volume", 1) != Num(now, "volume", 1))
            Add("volume", "track-volume", P(T("step.volume", "Volumen de ")), Q(name), P($": {Gain(Num(now, "volume", 1))}"));
        if (Num(old, "pan") != Num(now, "pan"))
            Add("volume", "track-pan", P(T("step.pan", "Paneo de ")), Q(name), P($": {Pan(Num(now, "pan"))}"));
        if (Bool(old, "muted") != Bool(now, "muted"))
            Add("volume", Bool(now, "muted") ? "track-mute-on" : "track-mute-off",
                P(Bool(now, "muted") ? T("step.muteOn", "Silenciaste ") : T("step.muteOff", "Le sacaste el mute a ")), Q(name));
        if (Bool(old, "solo") != Bool(now, "solo"))
            Add("volume", Bool(now, "solo") ? "track-solo-on" : "track-solo-off",
                P(Bool(now, "solo") ? T("step.soloOn", "Solo en ") : T("step.soloOff", "Le sacaste el solo a ")), Q(name));
        Slots(old["inserts"] as JsonArray, now["inserts"] as JsonArray, name, changes);
        if (!Same(old, now, "output"))
            Add("move", "route", P(T("step.output", "Salida de ")), Q(name), P(" → "),
                Q(Str(now, "output") is { } bus ? busNames.GetValueOrDefault(bus, "") : "Master"));
        Sends(old["sends"] as JsonArray, now["sends"] as JsonArray, name, changes, busNames);
    }

    static void Sends(JsonArray? old, JsonArray? now, string name, List<Change> changes, Dictionary<string, string> busNames)
    {
        if (JsonNode.DeepEquals(old, now)) return;
        var before = (old ?? []).OfType<JsonObject>().Where(s => Str(s, "busId") != null).GroupBy(s => Str(s, "busId")!).ToDictionary(g => g.Key, g => g.First());
        var after = (now ?? []).OfType<JsonObject>().Where(s => Str(s, "busId") != null).GroupBy(s => Str(s, "busId")!).ToDictionary(g => g.Key, g => g.First());
        var changed = after.Keys.Union(before.Keys).Where(k => !JsonNode.DeepEquals(before.GetValueOrDefault(k), after.GetValueOrDefault(k))).ToList();
        if (changed.Count == 1)
            changes.Add(new("move", [P(T("step.send", "Envío de ")), Q(name), P(T("step.to", " a ")), Q(busNames.GetValueOrDefault(changed[0], ""))], "route", name));
        else changes.Add(new("move", [P(T("step.sends", "Envíos de ")), Q(name)], "route", name));
    }

    /// <summary>A slot as a step names it: the musician's word for a built-in effect, the plugin's name for a VST3.</summary>
    static string SlotName(JsonObject s) => Str(s, "type") switch
    {
        InsertSlot.VstKind when Str((s["vst"] as JsonObject)?["plugin"] as JsonObject, "name") is { Length: > 0 } plugin => plugin,
        InsertSlot.SatKind => T("step.fx.sat", "Vida"),
        InsertSlot.CompKind => T("step.fx.comp", "el compresor"),
        InsertSlot.EqKind => T("step.fx.eq", "el EQ"),
        InsertSlot.ReverbKind => T("step.fx.reverb", "la reverb"),
        InsertSlot.DelayKind => T("step.fx.delay", "el delay"),
        InsertSlot.GateKind => T("step.fx.gate", "el gate"),
        _ => T("step.fx.other", "un efecto"),
    };

    /// <summary>Whether a slot sounds, by its kind's own switch (see <see cref="InsertSlot.On"/>).</summary>
    static bool SlotOn(JsonObject s) => Str(s, "type") switch
    {
        InsertSlot.ReverbKind => !Bool(s["reverb"] as JsonObject, "muted"),
        InsertSlot.DelayKind => !Bool(s["delay"] as JsonObject, "muted"),
        { } kind => Bool(s[kind] as JsonObject, "on"),
        _ => false,
    };

    static void Slots(JsonArray? old, JsonArray? now, string name, List<Change> changes)
    {
        if (JsonNode.DeepEquals(old, now)) return;
        var before = ById(old);
        var after = ById(now);
        void Add(string group, params Part[] parts) => changes.Add(new("fx", [.. parts], group, name));
        foreach (var (id, s) in after)
            if (!before.ContainsKey(id)) Add("fx-add", P(T("step.fxAdd", "Agregaste {0} a ", SlotName(s))), Q(name));
        foreach (var (id, s) in before)
            if (!after.ContainsKey(id)) Add("fx-remove", P(T("step.fxRemove", "Sacaste {0} de ", SlotName(s))), Q(name));
        if (Moved(Ids(old, after), Ids(now, before)) != null) Add("fx-order", P(T("step.fxOrder", "Reordenaste los efectos de ")), Q(name));
        foreach (var (id, s) in after)
        {
            if (!before.TryGetValue(id, out var was) || JsonNode.DeepEquals(was, s)) continue;
            string kind = SlotName(s);
            if (SlotOn(was) != SlotOn(s))
                Add("fx-switch", P(SlotOn(s) ? T("step.fxOn", "Prendiste {0} de ", kind) : T("step.fxOff", "Apagaste {0} de ", kind)), Q(name));
            else if (Without(was, "collapsed") is var w && Without(s, "collapsed") is var x && JsonNode.DeepEquals(w, x))
                Add("fx-fold", P(T("step.fxFold", "Plegaste o desplegaste {0} de ", kind)), Q(name));
            else Add("fx-edit", P(T("step.fxEdit", "Ajustaste {0} de ", kind)), Q(name));
        }
    }

    // ---------- folders, buses, markers, project ----------

    static void Folders(JsonArray? old, JsonArray? now, List<Change> changes, Dictionary<string, string> busNames)
    {
        if (JsonNode.DeepEquals(old, now)) return;
        var before = ById(old);
        var after = ById(now);
        foreach (var (id, f) in after)
            if (!before.ContainsKey(id))
                changes.Add(new("folder", [P(Bool(f, "separator") ? T("step.sepAdd", "Agregaste el separador ") : T("step.folderAdd", "Creaste la carpeta ")), Q(Name(f))], "folder-add", null));
        foreach (var (id, f) in before)
            if (!after.ContainsKey(id))
                changes.Add(new("trash", [P(Bool(f, "separator") ? T("step.sepRemove", "Borraste el separador ") : T("step.folderRemove", "Borraste la carpeta ")), Q(Name(f))], "folder-remove", null));
        foreach (var (id, f) in after)
        {
            if (!before.TryGetValue(id, out var was) || JsonNode.DeepEquals(was, f)) continue;
            string name = Name(f);
            void Add(string icon, string group, params Part[] parts) => changes.Add(new(icon, [.. parts], group, name));
            if (Str(was, "name") != Str(f, "name")) Add("edit", "folder-name", P(T("step.rename", "Renombraste ")), Q(Name(was)), P(" → "), Q(name));
            if (Bool(was, "collapsed") != Bool(f, "collapsed") || Bool(was, "mixerCollapsed") != Bool(f, "mixerCollapsed"))
                Add("folder", "folder-fold", P(Bool(f, "collapsed") || Bool(f, "mixerCollapsed") ? T("step.fold", "Plegaste ") : T("step.unfold", "Desplegaste ")), Q(name));
            if (Bool(was, "hiddenInTimeline") != Bool(f, "hiddenInTimeline"))
                Add("folder", "folder-hide", P(Bool(f, "hiddenInTimeline") ? T("step.sepHide", "Ocultaste del timeline ") : T("step.sepShow", "Mostraste en el timeline ")), Q(name));
            if (!Same(was, f, "color")) Add("color", "folder-color", P(T("step.trackColor", "Color de ")), Q(name));
            if (!Same(was, f, "output"))
                Add("move", "route", P(T("step.output", "Salida de ")), Q(name), P(" → "), Q(Str(f, "output") is { } bus ? busNames.GetValueOrDefault(bus, "") : "Master"));
            Sends(was["sends"] as JsonArray, f["sends"] as JsonArray, name, changes, busNames);
            if (!Same(was, f, "mixerSide")) Add("move", "folder-side", P(T("step.side", "Cambiaste de lado en el mixer a ")), Q(name));
            if (!Same(was, f, "height") || !Same(was, f, "heightScale")) Add("edit", "folder-height", P(T("step.height", "Altura de ")), Q(name));
        }
        // a folder dragged on its own: only its place changed (tracks moving with it say so themselves)
        if (changes.Count == 0 && after.Any(kv => before.TryGetValue(kv.Key, out var was) && !Same(was, kv.Value, "index")))
            changes.Add(new("move", [P(T("step.folderMove", "Moviste la carpeta ")), Q(Name(after.First(kv => before.TryGetValue(kv.Key, out var w) && !Same(w, kv.Value, "index")).Value))], "folder-move", null));
    }

    static void Buses(JsonArray? old, JsonArray? now, List<Change> changes, Dictionary<string, string> busNames)
    {
        if (JsonNode.DeepEquals(old, now)) return;
        var before = ById(old);
        var after = ById(now);
        foreach (var (id, x) in after)
            if (!before.ContainsKey(id)) changes.Add(new("plus", [P(T("step.busAdd", "Creaste el bus ")), Q(Name(x))], "bus-add", null));
        foreach (var (id, x) in before)
            if (!after.ContainsKey(id)) changes.Add(new("trash", [P(T("step.busRemove", "Borraste el bus ")), Q(Name(x))], "bus-remove", null));
        if (Moved(Ids(old, after), Ids(now, before)) != null) changes.Add(new("move", [P(T("step.busOrder", "Reordenaste los buses"))], "bus-order", null));
        foreach (var (id, x) in after)
        {
            if (!before.TryGetValue(id, out var was) || JsonNode.DeepEquals(was, x)) continue;
            string name = Name(x);
            if (Str(was, "name") != Str(x, "name"))
                changes.Add(new("edit", [P(T("step.rename", "Renombraste ")), Q(Name(was)), P(" → "), Q(name)], "bus-name", name));
            // a bus is a channel without solo: the same sentences
            Channel(was, x, name, changes, busNames);
            if (!Same(was, x, "mixerSide")) changes.Add(new("move", [P(T("step.side", "Cambiaste de lado en el mixer a ")), Q(name)], "bus-side", name));
        }
    }

    static void Markers(JsonArray? old, JsonArray? now, List<Change> changes)
    {
        if (JsonNode.DeepEquals(old, now)) return;
        var before = ById(old);
        var after = ById(now);
        foreach (var (id, m) in after)
            if (!before.ContainsKey(id)) changes.Add(new("marker", [P(T("step.markerAdd", "Agregaste el marcador ")), Q(Name(m))], "marker-add", null));
        foreach (var (id, m) in before)
            if (!after.ContainsKey(id)) changes.Add(new("trash", [P(T("step.markerRemove", "Borraste el marcador ")), Q(Name(m))], "marker-remove", null));
        foreach (var (id, m) in after)
        {
            if (!before.TryGetValue(id, out var was) || JsonNode.DeepEquals(was, m)) continue;
            changes.Add(Str(was, "name") != Str(m, "name")
                ? new("marker", [P(T("step.rename", "Renombraste ")), Q(Name(was)), P(" → "), Q(Name(m))], "marker-name", null)
                : new("marker", [P(T("step.markerMove", "Moviste el marcador ")), Q(Name(m))], "marker-move", null));
        }
    }

    static void Settings(JsonObject a, JsonObject b, List<Change> changes)
    {
        if (!Same(a, b, "mode"))
            changes.Add(new("video", [P(Str(b, "mode") == "audio" ? T("step.modeAudio", "Pasaste a solo audio") : T("step.modeVideo", "Pasaste a video"))], "mode", null));
        if (!Same(a, b, "aspect"))
            changes.Add(new("video", [P(T("step.aspect", "Formato del video: ")), Q(Str(b, "aspect") ?? "")], "format", null));
        else if (!Same(a, b, "layout"))
            changes.Add(new("video", [P(T("step.layout", "Disposición del video"))], "format", null));
        if (!Same(a, b, "color")) changes.Add(new("color", [P(T("step.videoColor", "Color del video"))], "video-color", null));
        if (!Same(a, b, "videoFx")) changes.Add(new("color", [P(T("step.videoFx", "Efectos del video"))], "video-fx", null));
        if (!Same(a, b, "compare")) changes.Add(new("volume", [P(T("step.compare", "Comparación A/B de la mezcla"))], "compare", null));
    }

    // ---------- JSON helpers ----------

    static Dictionary<string, JsonObject> ById(JsonArray? list)
    {
        var map = new Dictionary<string, JsonObject>();
        if (list == null) return map;
        foreach (var n in list)
            if (n is JsonObject o && Str(o, "id") is { } id) map.TryAdd(id, o);
        return map;
    }

    /// <summary>The ids of <paramref name="list"/> that are also in <paramref name="other"/>, in order.</summary>
    static List<string> Ids(JsonArray? list, Dictionary<string, JsonObject> other) =>
        (list ?? []).OfType<JsonObject>().Select(o => Str(o, "id")).Where(id => id != null && other.ContainsKey(id)).Select(id => id!).ToList();

    /// <summary>
    /// Null when both orders match. Otherwise the id whose move explains it (taking it out of both leaves them equal),
    /// or "" when it took more than one.
    /// </summary>
    static string? Moved(List<string> before, List<string> after)
    {
        if (before.SequenceEqual(after)) return null;
        foreach (var id in after)
            if (before.Where(x => x != id).SequenceEqual(after.Where(x => x != id))) return id;
        return "";
    }

    static bool Same(JsonNode? a, JsonNode? b, string key) => JsonNode.DeepEquals(a?[key], b?[key]);

    static JsonObject Without(JsonObject o, string key)
    {
        var copy = (JsonObject)o.DeepClone();
        copy.Remove(key);
        return copy;
    }

    static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static double Num(JsonObject? o, string key, double fallback = 0) => o?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    static bool Bool(JsonObject? o, string key, bool fallback = false) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : fallback;

    static string Name(JsonObject o) => Str(o, "name") is { Length: > 0 } n ? n : T("step.unnamed", "sin nombre");

    static string Db(double db) => db.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB";

    /// <summary>A fader's level (0..1.5) in dB, like the mixer.</summary>
    static string Gain(double v) => v <= 0 ? "-∞ dB" : Db(20 * Math.Log10(v));

    static string Pan(double v) => Math.Abs(v) < 0.01 ? "C" : v < 0 ? $"L{Math.Round(-v * 100):0}" : $"R{Math.Round(v * 100):0}";

    static string Signed(double v) => v.ToString("+0.##;-0.##;0", CultureInfo.CurrentCulture);
}
