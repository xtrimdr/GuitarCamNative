# GuitarCam Studio Native

[Español](README.md) · **English**

**GuitarCam Studio Native** is a Windows desktop application for making **guitar videos**: you record takes
with camera and audio, mix them, and export a video with one panel per take.

It works both as a **cam studio** (record takes with a camera and build the video) and as a **DAW** (record,
edit and mix audio in a full timeline and mixer, with or without picture: **Audio only** mode is for making
music without video).

It is designed to be used with the guitar strapped on: one-key shortcuts, big buttons and a short path of
**record → mix → export**.

![The timeline, with a video panel per take](docs/screenshot-timeline.png)

![The mixer, with one strip per track, the reverb and delay returns and the master](docs/screenshot-mixer.png)

![Audio only mode: a DAW without video](docs/screenshot-audio.png)

## What it does

- **Records** audio over ASIO (input and output in the same callback, with monitoring) and video from a
  camera (DirectShow or DroidCam over IP). Each take is written to disk as it happens, so a crash does not lose it.
- **Edits on a timeline**: multiple tracks, clips you can move, trim, split and duplicate, fades, clip gain
  with its own volume curve, range delete, unlimited undo and redo, and import of outside audio (WAV, MP3, FLAC,
  OGG, M4A, AIFF). Recording starts at the playhead, so you can redo just one part. Tracks group into **folders**
  that fold, and drag in and out by their handle. A sound you like saves as a **track preset** and comes back from
  New track → Preset.
- **Mixes** with a per-track effects chain: saturation ("Vida", tube or tape), a compressor with presets
  (Strumming / Arpeggio / Lead), a 6-band EQ with live spectrum, and sends to reverb and delay, plus a master
  limiter, pan templates and a full mixer. Each track gets its own color and an instrument icon (guitars, bass,
  drums, synths, strings, winds, percussion…), shown in the timeline, the inspector and the mixer. Each folder gets
  its own mixer channel: its fader, M and S rule all its tracks, like a VCA.
- **Composes the video**: a panel per track with cover-fit, zoom, pan and mirror, 16:9 / 9:16 / 1:1 aspect
  ratios, side-by-side or stacked layout, and a color tab (filters, adjustments, effects and templates).
- **Exports** to MP4 (offline mix + FFmpeg, with progress and cancellation) or, in **Audio only** mode, to
  24-bit WAV or 320 kbps MP3; optionally each track in its own WAV too (stems). What you see in the preview
  is what gets exported.
- **Metronome** with time signature, tempo, tap tempo, count-in and six click sounds; latency calibration
  with a cable or by playing. A **tuner** (arrows that tell you which way to turn the peg, adjustable sensitivity) and a master **LUFS** meter in the transport bar.
- **Never loses anything**: atomic autosave 1.5 s after every edit, copies in `historial/` to go back,
  recovery of interrupted recordings and a project manager with a Recycle Bin.
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

It builds, runs `gclab comprobar-dsp`, copies the app to `%LOCALAPPDATA%\GuitarCam Pruebas\<date>-<commit>` (the last
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
| C / Shift+C / T | click / count-in / tap tempo |
| A | tuner |
| M / S | mute / solo the track |
| F | fit all |
| X | timeline / mixer |
| Alt+X / D | split the clip at the playhead / duplicate it |
| Del | remove the clip (or the empty track) |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+Shift+N | new track |
| Ctrl+click / Shift+click on a header | pick several tracks (one by one / a range) to move them together |
| Ctrl+S | save now |
| B / P | show or hide the take panel / the project panel |
| Wheel, Shift+wheel, Ctrl+wheel, Alt+wheel | scroll up and down, zoom, scroll sideways, track height (the modifiers can be changed in Settings → Keyboard shortcuts) |

On the selected clip, a click on its volume line adds a point; points drag, and a double-click removes one.

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
