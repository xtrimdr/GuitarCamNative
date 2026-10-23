# GuitarCam Native (pruebas)

Laboratorio para ver si GuitarCam Studio se puede pasar a .NET nativo. Corre al lado de la app web
(`..\GuitarCam`), no la reemplaza.

- `src/GuitarCam.Audio`: motor de audio portado de la app web
  - `Dsp/Saturator.cs`, `Dsp/Compressor.cs`, `Dsp/MasterLimiter.cs`: ports de los worklets de `fxWorklet.ts`
  - `Dsp/LevelAnalysis.cs`: `analyze()` de `compPresets.ts` (nivel típico de la toma, para la "Vida")
  - `Dsp/CompPresets.cs`: presets del compresor (Rasgueo / Arpegio / Punteo + cantidad) y `CompEqFilter`
    (la EQ del preset alrededor del compresor), de `compPresets.ts` y `mixGraph.ts`
  - `Dsp/Biquad.cs`, `Dsp/TrackEq.cs`: filtros como el `BiquadFilterNode` y la EQ de pista de 6 bandas
    con sus presets, de `eq.ts`
  - `Dsp/ReverbBus.cs`, `Dsp/DelayBus.cs`: los buses de reverb (impulso sintético + convolución sin
    latencia) y de delay (con el ducker de `fxWorklet.ts`), con los presets de `Mixer.tsx`
  - `Dsp/PanTemplates.cs`, `Dsp/ClipEnvelope.cs`: plantillas de paneo y ventana/fades de clip
  - `Dsp/MixRenderer.cs`: la cadena por pista y los buses (los mismos para escuchar y exportar) y el
    render offline de la mezcla, como `renderMix` de `mixGraph.ts`
  - `AudioLoader.cs`: carga los medios y los remuestrea al sample rate del motor (como `decodeAudioData`)
  - `RecordingSession.cs`: `InputProcessor` (ganancia, modos estéreo/mono y limitador suave de la
    captura, de `audioEngine.ts`) y `TakeRecorder` (graba desde el callback sin bloquear, sobre `RecordingWriter`)
  - `LatencyCalibration.cs`: calibración de latencia con cable o tocando, de `latencyCalibration.ts`
  - `Mp4Exporter.cs`: la exportación a MP4 (el `filter_complex` de FFmpeg de `server/index.js`, con el
    color de `colorGrade.js` y los efectos de `videoFx.js`, progreso, cancelación y `.part`)
  - `ClickSynth.cs`: los 6 sonidos de click de `clickSounds.ts`
  - `CameraCapture.cs`: la cámara en vivo (DirectShow o DroidCam por IP) leída con FFmpeg, para el
    monitor "En vivo" y la grabación; `VideoTakeWriter.cs`: el video de la toma (H.264 en MP4, 30 fps,
    keyframe cada 1 s, MP4 fragmentado mientras graba para que un corte no lo pierda)
  - `AsioDuplex.cs`: entrada y salida ASIO en un mismo callback (NAudio)
- `src/GuitarCam.Projects`: el proyecto nativo (`guitarcam.json`: medios, pistas, clips), guardado atómico,
  historial, recuperación de grabaciones e importador de proyectos de la web
- `src/GuitarCam.Lab`: `gclab.exe`, la consola de pruebas
- `src/GuitarCam.App`: la ventana (Avalonia). Arranca en el gestor "Proyectos" (como `SessionPicker`
  de la web: lista, buscador, nuevo con plantilla, renombrar, duplicar, Papelera, abrir carpeta,
  importar de la web), salvo que reciba una carpeta por línea de comandos; el chip del proyecto lo
  vuelve a abrir. Timeline, preview con el monitor "En vivo" al lado (la cámara cruda y la entrada),
  inspector, barra derecha con la pestaña Color (filtro, ajustes, efectos y plantillas de video),
  mixer en lugar del timeline (X: tiras amplias o compactas, retornos de reverb y delay, master,
  plantillas de paneo y mini timeline), reproducción por ASIO, autoguardado. Las tomas se graban con el video de la cámara. El ⚙
  abre Configuración: driver ASIO y salida (se recuerdan), canales, ganancia, monitoreo, cámara
  (dispositivo, resolución, IP DroidCam, espejo), calibración de latencia, ajuste fino de sincronía y
  latencia de cámara.
  R = grabar (grabando: detener y guardar; en la cuenta: cancelar), Esc = cancelar la cuenta,
  Espacio = play/pausa (grabando: detener y guardar), Inicio = al principio, C = click,
  Shift+C = cuenta, T = tap tempo, M/S = mute/solo, F = ver todo, X = timeline / mixer,
  Shift+rueda = zoom, Alt+rueda = alto de pista, Ctrl+Z = deshacer, Ctrl+Y o Ctrl+Shift+Z = rehacer,
  Supr = quitar clip (otra vez: quitar la pista vacía), Esc = cancelar el arrastre.
  En el gestor: F2 = renombrar la fila, Esc = cierra el formulario y después el gestor (si hay un
  proyecto abierto)
- `tools/worklet-ref.mjs`: corre los worklets originales en Node para comparar muestra a muestra

Lo que queda por hacer está en [PENDIENTES.md](PENDIENTES.md).

## Uso

```
dotnet build
src\GuitarCam.App\bin\Debug\net10.0-windows\GuitarCam.exe
src\GuitarCam.Lab\bin\Debug\net10.0-windows\gclab.exe
```

| Comando | Qué hace |
|---|---|
| `dispositivos` | drivers ASIO y dispositivos WASAPI |
| `panel` | panel del driver ASIO (tamaño de buffer) |
| `latencia [--modo cable\|tocando] [--ver] [--borrar] [--simular ms] [--datos X]` | mide la latencia ida y vuelta (con cable o tocando 4 + 12 clicks) o muestra / borra la guardada |
| `grabar --monitor --sat valvula` | graba sobre el click; con `--monitor` te escuchás con el efecto, vía ASIO (también `--ganancia`, `--descartar`, `--latencia-ms`, `--ajuste-sincro`, `--simular`) |
| `mezclar proyecto\|toma.wav [--salida mezcla.wav] [--segundos N]` | renderiza la mezcla offline, con el mismo grafo que la reproducción |
| `procesar toma.wav --sat cinta --comp-estilo punteo --eq ritmica --reverb cuarto --delay punteada` | aplica efectos a un WAV (también `--comp-cant`, `--reverb-envio`, `--delay-envio`) |
| `verificar [toma.wav]` | compara el DSP portado con el de la app web |
| `click` | escribe todos los clicks a un WAV |
| `camara [listar]` | las cámaras que ve FFmpeg (DirectShow) y sus formatos; `*` = la que elige la app |
| `camara grabar [--dispositivo X \| --ip 192.168.1.50:4747] [--resolucion native\|1080p\|720p\|480p] [--segundos 5] [--salida camara.mp4]` | graba el video como una toma y lo revisa con ffprobe (duración, keyframes) |
| `camara verificar-corte [--dispositivo X]` | corta una grabación a la mitad, matando solo el programa y también FFmpeg, y comprueba que el video quede |
| `proyecto importar carpeta-web` | pasa un proyecto de la web al formato nativo, en una carpeta nueva |
| `proyecto abrir carpeta` | abre un proyecto nativo y muestra pistas, avisos e historial |
| `proyecto toma carpeta [--segundos 3] [--pista id] [--camara]` | graba un tono como la ventana: a qué pista va, nombre, canal, guardado y diario; con `--camara`, también el video |
| `proyecto cortar carpeta [--camara]` | graba un tono (y el video) y corta sin cerrar, para probar la recuperación |
| `proyecto versiones carpeta` / `proyecto volver carpeta n` | las versiones guardadas en `historial/` (1 = la más nueva) / vuelve a una (lo de antes queda en el historial) |
| `proyecto verificar-deshacer` / `proyecto verificar-clips` / `proyecto verificar-tomas` | pruebas de deshacer/rehacer, de edición de clips y de dónde cae una toma nueva, sin tocar archivos |
| `proyecto clips carpeta` | lista pistas y clips |
| `proyecto editar carpeta mover\|recortar-inicio\|recortar-final\|fade-in\|fade-out clip delta [--iman 1/4] [--libre]` | edita un clip como en el timeline (imán, anclaje de sincronía) |
| `proyecto editar carpeta a-pista clip pista [--intercambiar clip]`, `reordenar desde hasta`, `inicio\|fijar-corte\|restaurar\|quitar-clip clip`, `quitar-pista pista`, `nueva-pista`, `agregar-toma medio [--pista p]` | el resto de las operaciones del timeline |
| `proyecto encuadre carpeta clip [--zoom 1.5] [--panx 0.2] [--pany -0.3] [--espejar] [--restaurar]` | encuadre del clip en su panel |
| `proyecto sincronia carpeta clip [--audio-ms -30] [--camara-ms 80] [--heredar]` | ajuste de sincronía y de latencia de cámara de un clip (`--heredar` vuelve a los de la grabación) |
| `exportar [proyecto] [--salida nombre] [--carpeta destino] [--calidad alta\|estandar\|borrador] [--reemplazar] [--prueba]` | exporta el MP4 (mezcla offline + FFmpeg); `--prueba` arma un proyecto de prueba y prueba colisión y cancelación |
| `proyectos lista` / `proyectos plantillas` | la lista del gestor (carpeta de proyectos + recientes) / las plantillas |
| `proyectos nuevo "Nombre" [--plantilla id] [--carpeta vacía]` | crea un proyecto (nombre limpio para Windows y "(2)" si ya existe) |
| `proyectos previsualizar "Nombre"` | el nombre y la carpeta que tendría un proyecto nuevo |
| `proyectos renombrar carpeta "Nombre"` | renombra (y mueve la carpeta si está en la de proyectos) |
| `proyectos duplicar carpeta` | copia tomas y ajustes, sin exportaciones |
| `proyectos borrar carpeta` / `proyectos olvidar carpeta` | a la Papelera / lo saca de la lista |
| `proyectos carpeta ruta` | cambia la carpeta de proyectos |
| `proyectos guardar-plantilla carpeta "Nombre"` / `proyectos borrar-plantilla id` | plantillas del usuario |
| `proyectos plantillas-video` / `guardar-plantilla-video carpeta "Nombre"` | plantillas de video (filtro, ajustes y efectos) de la pestaña Color |
| `proyectos aplicar-plantilla-video carpeta id\|"Nombre"` / `borrar-plantilla-video id` | aplica una plantilla de video a un proyecto / la borra |

En `proyecto` y `proyectos`, `--datos X` usa otra carpeta para la configuración, los recientes y las
plantillas (para probar sin tocar los tuyos), y `--raiz X` otra carpeta de proyectos.
