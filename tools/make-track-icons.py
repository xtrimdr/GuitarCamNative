"""Writes src/WildTrack.App/Controls/TrackIcons.Data.cs: the track icons, all filled shapes on one 32x32 grid.

Usage: python tools/make-track-icons.py folder
The folder holds the icons.json of each set below, named <set>.json, from the npm packages @iconify-json/<set>
(https://cdn.jsdelivr.net/npm/@iconify-json/<set>/icons.json). Fluent Emoji High Contrast is the base (the user's
pick); what it lacks comes from other free sets, filled too where there was one. Every path is moved onto the 32 grid
and written again with explicit separators, so Avalonia's path parser never meets packed arc flags, and gets
Avalonia's fill-rule prefix (F1 nonzero, SVG's default; F0 evenodd).
"""
import json, re, sys, os

# set -> (version, license, grid size, margin on the 32 grid): game-icons draw up to the edge, the rest bring their own
SOURCES = {
    "fluent-emoji-high-contrast": ("1.2.6", "MIT, Microsoft Corporation", 32, 0),
    "mdi": ("1.2.3", "Apache-2.0, Pictogrammers", 24, 0),
    "game-icons": ("1.2.4", "CC BY 3.0, Lorc, Delapouite and contributors", 512, 2),
    "qlementine-icons": ("1.2.16", "MIT, Olivier Cléro", 24, 0),
}

F, MDI, GAME, QL = "fluent-emoji-high-contrast", "mdi", "game-icons", "qlementine-icons"

# (category, [(icon id, set, name)]) in the order the picker shows them. The ids are ours and stay stable: a project
# stores them, and ids from the previous set (IconOven) that are gone map to these in TrackIcons.Legacy.
CATALOG = [
    ("strings", [("guitar", MDI, "guitar-acoustic"), ("electric-guitar", F, "guitar"), ("bass-guitar", GAME, "guitar-bass-head"),
                 ("banjo", F, "banjo"), ("violin", F, "violin"), ("harp", GAME, "harp"), ("guitar-pick", MDI, "guitar-pick"),
                 ("amplifier", MDI, "amplifier")]),
    ("drums", [("drum-kit", F, "drum"), ("drum", F, "long-drum"), ("drumsticks", QL, "drumsticks-24"), ("cymbal", QL, "cymbal-24"),
               ("bongos", QL, "bongos-24"), ("tambourine", GAME, "tambourine"), ("maracas", F, "maracas"),
               ("xylophone", GAME, "xylophone"), ("metronome", MDI, "metronome")]),
    ("keys", [("piano", F, "musical-keyboard"), ("grand-piano", GAME, "grand-piano"), ("synthesizer", F, "control-knobs"),
              ("pipe-organ", GAME, "pipe-organ"), ("mixer", F, "level-slider")]),
    ("winds", [("saxophone", F, "saxophone"), ("trumpet", F, "trumpet"), ("trombone", GAME, "trombone"), ("tuba", GAME, "tuba"),
               ("french-horn", F, "postal-horn"), ("flute", F, "flute"), ("clarinet", GAME, "clarinet"),
               ("accordion", F, "accordion"), ("bagpipe", GAME, "bagpipes")]),
    ("more", [("microphone-stand", F, "studio-microphone"), ("microphone", F, "microphone"), ("headphones", F, "headphone"),
              ("speaker", F, "speaker-high-volume"), ("turntable", MDI, "record-player"), ("vinyl-record", MDI, "album"),
              ("music-note", F, "musical-note"), ("music", F, "musical-notes"), ("sheet-music", F, "musical-score"),
              ("video-camera", F, "video-camera"), ("fire", F, "fire"), ("star", F, "glowing-star"), ("sparkles", F, "sparkles"),
              ("rock", F, "sign-of-the-horns")]),
]

ARGS = {"m": 2, "l": 2, "h": 1, "v": 1, "c": 6, "s": 4, "q": 4, "t": 2, "a": 7, "z": 0}
NUM = re.compile(r"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")


def fmt(x):
    s = f"{x:.3f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def clean_path(d, k=1.0, t=0.0):
    """Re-emits SVG path data command by command with spaces between every number, scaled by k and moved by t
    (absolute coordinates only; arc rotation and flags stay as they are)."""
    out, i, cmd = [], 0, None
    while i < len(d):
        ch = d[i]
        if ch in " ,\t\n":
            i += 1
            continue
        if ch.isalpha():
            cmd = ch
            i += 1
            if cmd in "zZ":
                out.append(cmd)
                continue
        n = ARGS[cmd.lower()]
        vals = []
        while len(vals) < n:
            while i < len(d) and d[i] in " ,\t\n":
                i += 1
            if cmd.lower() == "a" and len(vals) in (3, 4):  # arc flags: a single 0 or 1, maybe packed
                vals.append(float(d[i]))
                i += 1
                continue
            m = NUM.match(d, i)
            if not m:
                raise ValueError(f"bad path near {d[i:i+20]!r}")
            vals.append(float(m.group()))
            i = m.end()
        out.append(cmd + " ".join(fmt(v) for v in place(cmd, vals, k, t)))
        # an implicit repeat after M/m is a line
        if cmd == "M": cmd = "L"
        elif cmd == "m": cmd = "l"
    # a path's first moveto is absolute even when written "m"; joined after another shape it would not be
    if out and out[0].startswith("m"): out[0] = "M" + out[0][1:]
    return " ".join(out)


def place(cmd, vals, k, t):
    """Moves one command's numbers onto the 32 grid."""
    shift = t if cmd.isupper() else 0
    if cmd.lower() == "a":
        rx, ry, rot, large, sweep, x, y = vals
        return [rx * k, ry * k, rot, large, sweep, x * k + shift, y * k + shift]
    return [v * k + shift for v in vals]


def attr(tag, name, default=None):
    m = re.search(rf'\s{name}="([^"]*)"', tag)
    return m.group(1) if m else default


def fills(body, k, t):
    """One Avalonia path string per SVG path, each filled on its own as SVG does (joined, a nonzero fill could punch
    holes where two paths overlap); the clip paths in <defs> are left out."""
    body = re.sub(r"<defs>.*?</defs>", "", body)
    out = []
    for tag in re.findall(r"<path[^>]*>", body):
        if attr(tag, "fill") not in (None, "currentColor"):
            raise ValueError(f"unexpected paint in {tag}")
        rule = "F0" if attr(tag, "fill-rule") == "evenodd" else "F1"
        out.append(f"{rule} " + clean_path(attr(tag, "d"), k, t))
    return out


def main():
    folder = sys.argv[1]
    sets = {name: json.load(open(os.path.join(folder, name + ".json"), encoding="utf8")) for name in SOURCES}
    lines = [
        "// <auto-generated> by tools/make-track-icons.py from Fluent Emoji High Contrast (MIT), Material Design Icons",
        "// (Apache-2.0), game-icons.net (CC BY 3.0) and Qlementine Icons (MIT); see",
        "// Assets/Icons/LICENSES.txt. Run it again instead of editing this file. </auto-generated>",
        "namespace WildTrack.App.Controls;",
        "",
        "public static partial class TrackIcons",
        "{",
        "    /// <summary>(category, icon id, filled paths on a 32×32 grid) in picker order.</summary>",
        "    static readonly (string Category, string Id, string[] Paths)[] Data =",
        "    [",
    ]
    for cat, ids in CATALOG:
        for id, src, name in ids:
            _, _, grid, margin = SOURCES[src]
            k = (32 - 2 * margin) / grid
            parts = ", ".join(f'"{p}"' for p in fills(sets[src]["icons"][name]["body"], k, margin))
            lines.append(f'        ("{cat}", "{id}", [{parts}]),')
    lines += ["    ];", "}", ""]
    out = os.path.join(os.path.dirname(__file__), "..", "src", "WildTrack.App", "Controls", "TrackIcons.Data.cs")
    with open(out, "w", encoding="utf8", newline="\r\n") as f:
        f.write("\n".join(lines))
    print("wrote", os.path.normpath(out))


main()
