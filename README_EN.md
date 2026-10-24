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

## What it does

- **Records** audio over ASIO (input and output in the same callback, with monitoring) and video from a
  camera (DirectShow or DroidCam over IP). Each take is written to disk as it happens, so a crash does not lose it.
- **Edits on a timeline**: multiple tracks, clips you can move, trim, split and duplicate, fades, range
  delete, unlimited undo and redo, and import of outside audio (WAV, MP3, FLAC, OGG, M4A, AIFF).
- **Mixes** with a per-track effects chain: saturation ("Vida", tube or tape), a compressor with presets
  (Strumming / Arpeggio / Lead), a 6-band EQ with live spectrum, and sends to reverb and delay, plus a master
  limiter, pan templates and a full mixer.
- **Composes the video**: a panel per track with cover-fit, zoom, pan and mirror, 16:9 / 9:16 / 1:1 aspect
  ratios, side-by-side or stacked layout, and a color tab (filters, adjustments, effects and templates).
- **Exports** to MP4 (offline mix + FFmpeg, with progress and cancellation) or, in **Audio only** mode, to
  24-bit WAV or 320 kbps MP3. What you see in the preview is what gets exported.
- **Metronome** with time signature, tempo, tap tempo, count-in and six click sounds; latency calibration
  with a cable or by playing.
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
dotnet build
src\GuitarCam.App\bin\Debug\net10.0-windows\GuitarCam.exe
```

The app starts in the **Projects** manager (list, search, templates, rename, duplicate, Recycle Bin). With a
project folder as an argument, it opens it directly. `tools\make-shortcut.ps1` creates a desktop shortcut.
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
| M / S | mute / solo the track |
| F | fit all |
| X | timeline / mixer |
| Alt+X / D | split the clip at the playhead / duplicate it |
| Del | remove the clip (or the empty track) |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+Shift+N | new track |
| Ctrl+S | save now |
| B / P | show or hide the take panel / the project panel |
| Wheel, Shift+wheel, Alt+wheel | scroll, zoom, track height |

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

If you are going to contribute or use an AI agent, read [AGENTS.md](AGENTS.md) first.

## gclab

`gclab.exe` (`src\GuitarCam.Lab\bin\Debug\net10.0-windows\gclab.exe`) tests the engine and projects from the
console without opening the window. Its commands and options are in Spanish (for example `grabar`, `mezclar`,
`exportar`, `proyecto`, `proyectos`); run it without arguments to see the help, or see the command table in
the [Spanish README](README.md#gclab).
