# GuitarCam Studio Native

[Español](README.md) · **English**

**GuitarCam Studio Native** is a Windows desktop application for making **guitar videos**: you record takes
with camera and audio, mix them, and export a video with one panel per take.

It works both as a **cam studio** (record takes with a camera and build the video) and as a **DAW** (record,
edit and mix audio in a full timeline and mixer, with or without picture: **Audio only** mode is for making
music without video).

It is designed to be used with the guitar strapped on: one-key shortcuts, big buttons and a short path of
**record → mix → export**.

![The timeline in Video mode: one panel per take, colored track folders, the channel inspector and the color tab](docs/screenshots/timeline.png)

| | |
|---|---|
| ![The mixer while playing: folders as VCAs, reverb and delay returns and the master](docs/screenshots/mixer.png) | ![Vertical 9:16 stacked layout, ready for Reels, Shorts and TikTok](docs/screenshots/vertical.png) |
| **The mixer**: one strip per track, folders as VCAs, the reverb and delay returns and the master. | **Vertical 9:16** for Reels, Shorts and TikTok, with a color look applied. |

![Audio only mode: tracks with their effect knobs and the EQ with its live spectrum](docs/screenshots/audio.png)

*The screenshots show the demo project; the panel images are photos from [Unsplash](https://unsplash.com).*

## What it does

### Record

- Audio over **ASIO** (input and output in the same callback, with monitoring) and video from a camera (DirectShow
  or DroidCam over IP). Each take is written to disk as it happens, so a crash does not lose it.
- Recording starts at the playhead, so you can redo just one part, with a **metronome** (time signature, tempo, tap
  tempo, count-in and six click sounds) and **latency calibration** with a cable or by playing.
- A **tuner** with arrows that tell you which way to turn the peg and adjustable sensitivity.

### Edit

- A multitrack **timeline**: clips you can move, trim, split and duplicate, fades, clip gain with its own **volume
  curve**, range delete, unlimited undo and redo, and import of outside audio (WAV, MP3, FLAC, OGG, M4A, AIFF).
- **Folders** that fold and group tracks; Ctrl+click / Shift+click marks several tracks to move them together. Each
  track gets its own **color** (24, in rainbow order), an **instrument icon** (guitars, bass, drums, synths, strings,
  winds, percussion…), its own height and Vida, Comp, Reverb and Delay knobs in its header.
- A sound you like saves as a **track preset** and comes back from New track → Preset.
- Above the ruler, an **A–B range** loops a stretch (to practice the solo or mix a part) and named **markers**
  (Intro, Chorus, Solo…) jump from one part to another. Zoom from one beat to the whole song with shortcuts you
  assign in Settings → Keyboard shortcuts.

### Mix

- A per-track effects chain: saturation (**Vida**, tube or tape), a **compressor** with presets (Strumming /
  Arpeggio / Lead), a **6-band EQ** with live spectrum, and sends to **reverb** and **delay**, plus a master limiter
  and pan templates.
- A full **mixer**; each folder gets its own channel, and its fader, M and S rule all its tracks, like a VCA.
- Professional-grade audio: starting, stopping, jumping and dragging the playhead all fade, with no clicks or dropouts.

### Build the video

- A panel per track with cover-fit, zoom, pan and mirror; **16:9 / 9:16 / 1:1** aspect ratios, side-by-side or
  stacked layout.
- A **color** tab with 26 looks (Brujo, Cine, Moody, Fresco, Noche azul, Hielo…), adjustments (exposure, contrast,
  whites, blacks, light…), effects and templates. What you see in the preview is what gets exported.

### Export

- **MP4** (offline mix + FFmpeg, with progress and cancellation), with presets for **Reels · TikTok**, **YouTube** and
  **Instagram** (1:1 and 4:5) that fit the frame and bring the mix to −14 LUFS.
- Easy or custom: pick the destination and quality, and if you want, the resolution (720p to 4K) and bitrate under **Advanced**.
- The video is encoded on the **video card** (NVIDIA, AMD or Intel) when there is one, the takes are processed in
  parallel and the mix uses every core; if the card fails, it carries on with the processor.
- In **Audio only** mode, 24-bit WAV or 320 kbps MP3; optionally each track in its own WAV too (stems). A master
  **LUFS** meter sits in the transport bar.

### And also

- **Never loses anything**: atomic saving (after every edit, every X minutes or by hand with Ctrl+S or the disk
  button, chosen in Settings → Saving and export; takes are always saved), copies in `historial/` to go back, recovery of
  interrupted recordings and a project manager with a Recycle Bin.
- Configurable language, theme and keyboard shortcuts.

## Where it is going

GuitarCam Studio Native is **under constant development**. The goal is to take it to a level that works both
for people just starting out who want to make music quickly and without complications, and for professionals
who want to make music seriously.

Next up is support for **MIDI**, **VST plugins** and more studio tools. What is planned and what is missing is
in [PENDIENTES.md](PENDIENTES.md) (in Spanish).

## Requirements

- Windows 10/11, 64-bit.
- An audio interface with an **ASIO** driver.
- **FFmpeg** and **ffprobe** on the `PATH` (camera, video and export).
- [.NET 10 SDK](https://dotnet.microsoft.com/) to build.

## Build and run

```
tools\probar.ps1 "what to try"
```

It builds, runs `gclab comprobar-dsp`, copies the app to `pruebas\<date>-<commit>` (ignored by git; the last
5 are kept) and opens it from there, marked "TEST <commit>" in the title bar. Since it never runs from `bin\`, an open
app does not block the next build. `-Abrir` opens the newest copy without building, `-NoAbrir` only builds and copies,
`-SinComprobar` skips `gclab`, `-Proyecto folder` opens that project. If a GuitarCam is already open, a dialog asks first whether to close it
(it saves the project before closing), so the new copy does not open the same project read-only. Plain `dotnet build` still works for building.

The app starts in the **Projects** manager (list, search, templates, rename, duplicate, Recycle Bin). With a
project folder as an argument, it opens it directly. `tools\make-shortcut.ps1` creates a desktop shortcut that opens the newest test copy (`probar.ps1 -Abrir`).
Settings are stored in `%APPDATA%\GuitarCam Studio`.

## Main shortcuts

All of them can be changed in ⚙ Settings → Keyboard shortcuts (except Esc). The interface itself is in
Spanish by default; the language is chosen in Settings.

| Key | Action |
|---|---|
| Space | play / stop |
| Shift+R or Num * | record (while recording: stop and save) |
| R | arm / disarm the selected track |
| Home | back to the start |
| L | loop (the A–B range, or the whole song) |
| I / O | range start (A) / end (B) at the playhead |
| K / , / . | marker at the playhead / go to the previous / the next one |
| C / Shift+C / T | click / count-in / tap tempo |
| A | tuner |
| M / S | mute / solo the track |
| F | fit all |
| X | timeline / mixer |
| Alt+X / D | split the clip at the playhead / duplicate it |
| Del | remove the clip (or the empty track); with a range, delete it |
| Shift+Del | delete the range and close the gap (what follows slides left) |
| 1 to 7 | tools: Select / Range / Split / Erase / Listen / Zoom / Hand |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+Shift+N | new track |
| Ctrl+click / Shift+click on a header | pick several tracks (one by one / a range) to move them together |
| Ctrl+S | save now |
| B / P | show or hide the take panel / the project panel |
| Wheel, Shift+wheel, Ctrl+wheel, Alt+wheel | scroll up and down, zoom, scroll sideways, track height (the modifiers can be changed in Settings → Keyboard shortcuts) |

On the selected clip, a click on its volume line adds a point; points drag, and a double-click removes one.

On the strip above the ruler, a drag sets the A–B range (and turns the loop on); its edges and the whole range drag.
A double-click adds a marker; markers drag, a click jumps there and a double-click renames them. Right-click the strip
to add a marker, start or end the range there or remove it, and on a marker to pick a name from the list or loop to
the next one.

The timeline tools sit in the middle of the project bar: **Select** (as always), **Range** (marks a stretch of one or
several tracks; right-click: delete it, delete it and close the gap, loop it), **Split** (a click cuts the clip
there, on the grid), **Erase** (removes the clips it touches), **Listen** (hold the button on a clip to hear that
track alone from there), **Zoom** (click zooms in, Alt+click out, a drag shows that stretch) and **Hand** (drag to
move the view). Where two clips of a track overlap, right-click → **Bring to front** picks which one shows.

## How it is built

```
src/
  GuitarCam.Audio      audio engine: DSP, duplex ASIO, click, recording to disk, export
  GuitarCam.Projects   project model, atomic saving, history, project manager
  GuitarCam.App        the window (Avalonia 11.3)
  GuitarCam.Lab        gclab.exe: console to test audio, DSP and projects without the window
tools/                 icon, shortcut and verification utilities
```

Dependencies point downward (App → Projects → Audio): the engine and the project model know nothing about
the interface, and everything they do can be exercised from `gclab`. A project is saved in a folder:

```
guitarcam.json        the project
guitarcam.json.bak    the previous save
historial/            copies to go back to
media/                recordings and imports (never rewritten)
exports/              exported videos and audio
```

## Contributing and license

GuitarCam Studio Native is **free and non-profit**: it is made for the community, so anyone can record and
make music without paying a cent. Contributions are welcome and **all of them go through the maintainer's
approval**: open an issue or a Pull Request following [CONTRIBUTING.md](CONTRIBUTING.md). Before touching code,
read [AGENTS.md](AGENTS.md) (the project guide, in Spanish) and see [PENDIENTES.md](PENDIENTES.md) for what is
missing.

It is released under the [GPL-3.0](LICENSE) license: you can use, study, modify and share it freely, and
distributed modified versions must stay free under the same license.

## gclab

`gclab.exe` (`src\GuitarCam.Lab\bin\Debug\net10.0-windows\gclab.exe`) tests the engine and projects from the
console without opening the window. Its commands and options are in Spanish (for example `grabar`, `mezclar`,
`exportar`, `proyecto`, `proyectos`); run it without arguments to see the help, or see the command table in
the [Spanish README](README.md#gclab).
