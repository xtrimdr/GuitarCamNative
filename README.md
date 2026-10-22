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
  - `ClickSynth.cs`: los 6 sonidos de click de `clickSounds.ts`
  - `AsioDuplex.cs`: entrada y salida ASIO en un mismo callback (NAudio)
- `src/GuitarCam.Projects`: el proyecto nativo (`guitarcam.json`: medios, pistas, clips), guardado atómico,
  historial, recuperación de grabaciones e importador de proyectos de la web
- `src/GuitarCam.Lab`: `gclab.exe`, la consola de pruebas
- `src/GuitarCam.App`: la ventana (Avalonia). Arranca en el gestor "Proyectos" (como `SessionPicker`
  de la web: lista, buscador, nuevo con plantilla, renombrar, duplicar, Papelera, abrir carpeta,
  importar de la web), salvo que reciba una carpeta por línea de comandos; el chip del proyecto lo
  vuelve a abrir. Timeline, preview e inspector, reproducción por ASIO, autoguardado.
  Espacio = play/stop, Inicio = al principio, C = click, M/S = mute/solo, F = ver todo,
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
| `latencia [--modo cable\|tocando] [--ver] [--borrar] [--simular ms]` | mide la latencia ida y vuelta (con cable o tocando 4 + 12 clicks) o muestra / borra la guardada |
| `grabar --monitor --sat valvula` | graba sobre el click; con `--monitor` te escuchás con el efecto, vía ASIO (también `--ganancia`, `--descartar`, `--latencia-ms`, `--ajuste-sincro`, `--simular`) |
| `mezclar proyecto\|toma.wav [--salida mezcla.wav] [--segundos N]` | renderiza la mezcla offline, con el mismo grafo que la reproducción |
| `procesar toma.wav --sat cinta --comp-estilo punteo --eq ritmica --reverb cuarto --delay punteada` | aplica efectos a un WAV (también `--comp-cant`, `--reverb-envio`, `--delay-envio`) |
| `verificar [toma.wav]` | compara el DSP portado con el de la app web |
| `click` | escribe todos los clicks a un WAV |
| `proyecto importar carpeta-web` | pasa un proyecto de la web al formato nativo, en una carpeta nueva |
| `proyecto abrir carpeta` | abre un proyecto nativo y muestra pistas, avisos e historial |
| `proyecto cortar carpeta` | graba un tono y corta sin cerrar, para probar la recuperación |
| `proyecto versiones carpeta` / `proyecto volver carpeta n` | las versiones guardadas en `historial/` (1 = la más nueva) / vuelve a una (lo de antes queda en el historial) |
| `proyecto verificar-deshacer` / `proyecto verificar-clips` | pruebas de deshacer/rehacer y de edición de clips, sin tocar archivos |
| `proyecto clips carpeta` | lista pistas y clips |
| `proyecto editar carpeta mover\|recortar-inicio\|recortar-final\|fade-in\|fade-out clip delta [--iman 1/4] [--libre]` | edita un clip como en el timeline (imán, anclaje de sincronía) |
| `proyecto editar carpeta a-pista clip pista [--intercambiar clip]`, `reordenar desde hasta`, `inicio\|fijar-corte\|restaurar\|quitar-clip clip`, `quitar-pista pista`, `nueva-pista`, `agregar-toma medio [--pista p]` | el resto de las operaciones del timeline |
| `proyecto encuadre carpeta clip [--zoom 1.5] [--panx 0.2] [--pany -0.3] [--espejar] [--restaurar]` | encuadre del clip en su panel |
| `proyectos lista` / `proyectos plantillas` | la lista del gestor (carpeta de proyectos + recientes) / las plantillas |
| `proyectos nuevo "Nombre" [--plantilla id] [--carpeta vacía]` | crea un proyecto (nombre limpio para Windows y "(2)" si ya existe) |
| `proyectos previsualizar "Nombre"` | el nombre y la carpeta que tendría un proyecto nuevo |
| `proyectos renombrar carpeta "Nombre"` | renombra (y mueve la carpeta si está en la de proyectos) |
| `proyectos duplicar carpeta` | copia tomas y ajustes, sin exportaciones |
| `proyectos borrar carpeta` / `proyectos olvidar carpeta` | a la Papelera / lo saca de la lista |
| `proyectos carpeta ruta` | cambia la carpeta de proyectos |
| `proyectos guardar-plantilla carpeta "Nombre"` / `proyectos borrar-plantilla id` | plantillas del usuario |

En `proyecto` y `proyectos`, `--datos X` usa otra carpeta para la configuración, los recientes y las
plantillas (para probar sin tocar los tuyos), y `--raiz X` otra carpeta de proyectos.
