# Pendientes — GuitarCam Native

Ideas y cosas para implementar en la versión nativa. Cada ítem: qué es, por qué, y notas si hay.
Al terminar uno, pasarlo a **Hecho** con la fecha.

## Ideas nuevas (solo nativa)

_Cosas que no están en la app web o que la versión nativa debería hacer distinto._

> **Foco:** la app sigue siendo para hacer videos de guitarra. Lo de DAW tiene que servir para eso
> y no puede complicar el camino de "grabo, mezclo y exporto el video".

### Pedidas

#### 1. Usarla también como DAW, solo audio
- [ ] Proyectos o pistas sin video: grabar solo audio (la pista no ocupa panel en el video)
- [ ] Edición de clips: dividir (cortar en el cursor), recortar, mover, duplicar, borrar un pedazo
- [ ] Ganancia por clip, aparte del fader de la pista (con su curva de volumen dentro del clip)
- [ ] Varios clips por pista (hoy es una toma por carril)
- [ ] Importar audio externo (WAV, MP3, FLAC…): pistas de acompañamiento, loops, tomas de otro lado
- [ ] Mejorar el mixer: inserts reordenables, más sends/buses, grupos, medidores más completos
- [ ] Exportar solo el audio (WAV/MP3), la mezcla o las pistas por separado (stems)
- …y más: ir sumando acá

#### 2. Soporte de plugins VST
- [ ] Cargar VST3 en las pistas y en los buses (amps, pedales, reverbs de terceros)
- Notas: en .NET no hay un host VST3 listo; probablemente haga falta un puente en C++.
  Conviene correr los plugins en otro proceso, así si uno se cuelga no tira abajo la grabación.
  La exportación tiene que pasar por los mismos plugins, igual que hoy el mix offline usa el mismo grafo.

#### 3. MIDI
- [ ] Pedalera o controlador MIDI: grabar/parar, play, marcar tomas, con los pies y sin soltar la guitarra
- [ ] Mapear perillas y faders de un controlador al mixer (MIDI learn)
- [ ] Pistas MIDI con instrumentos VST (batería, bajo) para armar la base
- [ ] MIDI clock / tempo sincronizado con otros equipos

#### 4. Edición de video más completa
- [ ] Color por toma, además del color global
- [ ] Más filtros y efectos (nitidez, desenfoque, glow, estabilización, chroma key/pantalla verde)
- [ ] Textos y títulos: nombre del tema, créditos, carteles
- [ ] Transiciones entre clips y entre layouts
- [ ] Recorte y encuadre animados (zoom/paneo que cambian con el tiempo)

#### 5. Atajos personalizables
- [ ] Un apartado (probablemente en Configuración) para cambiar las teclas de cada acción. Los atajos ya
  se muestran solo en los tooltips; cuando exista, los tooltips tienen que leer la tecla configurada.

### Sugerencias (para decidir)

_Ideas que surgieron charlando. Todas apuntan al video._

- [ ] **Layout que cambia en el tiempo ("director")**: en el solo, el panel del punteo crece o pasa a
  pantalla completa, y después vuelve al split. Se marca en el timeline como automatización.
- [ ] **Automatización** de volumen, paneo y efectos en el timeline (base para lo anterior)
- [ ] **Varias cámaras por toma**: plano general + primer plano de la mano, y elegir cuál se ve en cada momento
- [ ] **Importar video del celular** y sincronizarlo solo por el audio de la cámara (correlación,
  como la calibración de latencia)
- [ ] **Amp sim + cargador de IRs de gabinete**: grabar la guitarra directa (DI) y que suene bien sin VSTs
- [ ] **Afinador** integrado (en la barra de transporte o en el monitor de entrada)
- [ ] **Grabación en loop / tomas múltiples (comping)**: grabar varias pasadas del solo y elegir
  los mejores pedazos
- [ ] **Exportar con presets para redes**: Reels/Shorts/TikTok (9:16), YouTube (16:9), Instagram
  (1:1, 4:5), con el volumen normalizado al que pide cada una (ej. -14 LUFS)
- [ ] **Medidor de LUFS** en el master
- [ ] **Visualizadores de audio** como capa del video (onda o espectro animado)
- [ ] **Acordes / tablatura / letra en pantalla** mientras grabás (tipo teleprompter) y opcionalmente en el video
- [ ] **Exportar con la GPU** (NVENC) para que el render sea mucho más rápido

## Falta portar de la app web

El relevamiento completo (qué hace cada cosa en la web, cómo funciona y en qué estado está acá) está en
[docs/MIGRACION.md](docs/MIGRACION.md), con el orden sugerido al final. Todo se porta igual que en la web
([AGENTS.md → Migrar algo de la web](AGENTS.md#migrar-algo-de-la-web)).

## Entregado, falta revisar (2026-10-22)

Lo hicieron los agentes y compila con 0/0 y pasa sus pruebas de `gclab`, pero el coordinador todavía no
lo revisó. El detalle y el orden de revisión están en [docs/COORDINACION.md](docs/COORDINACION.md)
("Para retomar"). Al aprobarlo, pasa a **Hecho** y se marca en MIGRACION.

- A5 · transporte y tempo (`MainWindow.Transport.cs`, `Controls/TempoControl.cs`): probado a mano el
  2026-10-22 (sin mouse, por UI Automation): play/pausa, Detener (vuelve a donde arrancó), loop prendido y
  apagado, reloj (compases ↔ minutos), TAP, la cuenta y Esc. Se arregló que cambiar el tempo dejaba un
  paso de deshacer vacío. Falta: el tempo con el mouse (arrastre, rueda, doble clic) y escuchar el menú
  del click; la calibración desde Configuración con cable o tocando (necesita el cable o la guitarra)
- B6 · el diálogo de exportación ya se abre con "Exportar MP4"; falta la captura lado a lado con la web

## Notas

- Al abrir un proyecto queda un paso de deshacer vacío "track:…:name": el `TextChanged` del nombre en el
  inspector (`MainWindow.Inspector.cs`, `InitInspector`) se dispara al llenarlo fuera de `syncing`. El
  primer Ctrl+Z "deshace" sin cambiar nada.
- Puntitos en la preview (resuelto): con la ventana permitiendo transparencia, algunos píxeles oscuros
  del video dejaban ver la ventana de atrás. `TransparencyLevelHint="None"` lo corta, y con el video
  decodificado quedan solo unos pocos píxeles negros sobre casi negro, invisibles. Ojo al revisar
  capturas: las de GDI (CopyFromScreen/PrintWindow) dan alfa 0 en los píxeles negros y los visores los
  pintan blancos, aunque en pantalla se vean bien. Mirar el RGB, no el alfa.
- "Volver a una versión guardada" no tiene dónde tocarse en la ventana: el menú provisorio del chip se
  fue con el gestor y su lugar es la pestaña Historial (§2.3 de MIGRACION). Mientras tanto,
  `gclab proyecto versiones` / `volver`.
- `gclab verificar` corre los `.ts` reales de la web, pero los nodos nativos de Web Audio (Biquad,
  Delay, Convolver) y el ruteo de los buses son una emulación en JS (`tools/worklet-ref.mjs`). Falta
  una pasada contra un `OfflineAudioContext` de verdad en Chromium para confirmarlos.
- Sin probar todavía: saltos para atrás en el video mientras reproduce (reinicia FFmpeg en ese punto)
  y cuántos cuadros por segundo llegan de verdad con muchas tomas.
- Cámara: mientras la app está abierta, la cámara queda tomada (como en la web), así que otro programa no
  la puede usar. Si se desconecta, el monitor dice "Sin señal de cámara" y no se reconecta sola: hay que
  tocar "Refrescar dispositivos".

## Hecho

- 2026-10-22: preview (§1.4 de MIGRACION): elegir el formato desde la ventana, arriba del escenario como en
  la web ("Lado a lado" / "Apilado" y 16:9 · 9:16 · 1:1, `MainWindow.Preview.cs`); 9:16 pasa a apilado y
  16:9 a lado a lado, cada cambio es un paso de deshacer y se guarda solo (`ProjectSettings.SetAspect` /
  `SetLayout`, también `gclab proyecto formato`). En `PreviewView`: borde de acento en el panel de la pista
  elegida, la separación entre paneles de la exportación (4 px de salida), los textos de estado de la web
  ("La composición aparece…", "Mientras grabás…", "Cargando video…") y los fades de video solo mientras
  suena o graba (como la web). La preview ya no le da panel a una pista cuyo primer clip no tiene video,
  igual que la exportación. Arrastre, rueda y clic ya estaban. Probado en la ventana: los dos selectores,
  9:16 → apilado, 1:1 y deshacer; comparado con un MP4 de `gclab exportar` (misma grilla y encuadre). Sin
  probar a mano: el arrastre y la rueda en un panel y ver el fade mientras reproduce.

- 2026-10-22: mixer (§4 y §1.7 de MIGRACION): `MainWindow.Mixer.cs`, port de `Mixer.tsx` y
  `MiniTimeline.tsx`. Se alterna con el timeline (X o el botón de abajo) y tiene su propia altura
  (recordada). Tiras amplias (paneo y un panel por efecto: Sat, Comp con LIM y "Comprime", EQ con el
  gráfico chico y su plantilla, envíos de reverb y delay) o compactas (Pan / Comp / Rev / Delay, M S C),
  "Auto / Compacto / Amplio" recordado; pistas vacías con "vacía". Retornos de reverb y delay con sus
  presets, nota o "Libre (ms)", estéreo / ping-pong, mute y fader; master con la luz LIM. Plantillas de
  paneo con "Apertura". Controles nuevos: `Knob`, `Fader`, `MixerMeter`, `MiniTimeline`, `SelectButton`;
  el limitador del master ahora informa su reducción. Todo con deshacer (mismas claves que el inspector) y
  en vivo. Probado en la ventana: cambiar de vista, los tres anchos, paneo con la rueda, "Alternado L/R"
  y deshacer, reproducir con la mini timeline. Sin probar: los medidores con una toma con sonido (el
  proyecto de prueba tiene tomas en silencio), los presets de los buses de oído, y la captura lado a lado
  con la web.

- 2026-10-22: barra derecha y pestaña Color (§1.3 y §6 de MIGRACION; A6): `MainWindow.Color.cs`, port de
  `ColorPanel.tsx` con Plantillas (guardar actual, aplicar, borrar; mismo nombre = actualizar), Filtro con
  las muestras de cada look e Intensidad, Ajustes con Restablecer, Efectos (viñeta y tamaño, grano, barras
  de cine) y los ON/OFF "Ver sin color" / "Ver sin efectos". Cada cambio es un paso de deshacer (misma
  clave que la web), se guarda solo y se ve en vivo en la preview. La barra se pliega a una columna de
  íconos y recuerda pestaña y secciones. Plantillas en `VideoTemplates.cs` (`plantillas-video.json`),
  `gclab proyectos plantillas-video | guardar-plantilla-video | aplicar-plantilla-video |
  borrar-plantilla-video`. `ColorGrade` y `VideoFx` ahora se normalizan al abrir (mismos rangos que la
  web). Probado en la ventana: elegir un look, deshacer, guardar y borrar una plantilla, plegar y
  desplegar. La pestaña Historial está pero vacía (§2.3). Falta la captura lado a lado con la web.

- 2026-10-22: cámara (§11 de MIGRACION) y monitor "En vivo" (§1.5): `CameraCapture.cs` (DirectShow por
  FFmpeg, la elección de cámara de la web, resolución con sus alternativas, DroidCam por IP) y
  `VideoTakeWriter.cs` (H.264 8 Mbps, keyframe cada 1 s, 30 fps contados desde la primera muestra de
  audio, MP4 fragmentado mientras graba y con el índice adelante al terminar). La toma guarda
  `media/{id}.mp4` con la latencia de cámara y el espejo; el diario lo recupera. Pestaña Cámara y
  "Latencia de cámara" de Configuración prendidas (`Video/LiveCamera.cs`), monitor en
  `Controls/LiveMonitor.cs`. `gclab camara listar|grabar|verificar-corte`, `proyecto toma|cortar --camara`.
  Probado con la Smart Connect Camera (1280×720) sin el celular conectado (imagen de espera): toma de
  3,4 s con video en la ventana, cuenta cancelada con Esc sin archivos, y los cortes. Sin probar:
  DroidCam por IP (no hay celular en la red) y una cámara que ofrezca 1080p.

- 2026-10-22: Configuración (§7 de MIGRACION): `Dialogs/SettingsDialog.axaml(.cs)` desde el ⚙, con
  Entradas y salidas (driver ASIO, medidor, canales, ganancia, par de salida, monitoreo), Sincronización
  (latencia calibrada/estimada con sus avisos, calibrar con cable o tocando, "Usar la estimación",
  ajuste fino que usa la grabación) y Cámara con la forma de la web pero apagada. El driver y la salida se
  recuerdan. El motor ahora aplica la calibración guardada aunque esté vieja (avisa) y vuelve a la
  estimación en un par sin medir. Probado abrirlo con la Focusrite; la calibración desde el diálogo
  todavía no se probó con cable ni tocando.

- 2026-10-22: grabar desde la ventana (§5.8 de MIGRACION): R / botón Grabar, cuenta con "Cancelar n/4"
  (Esc), Detener (Espacio / R) y Descartar, reloj y cabezal rojos, "Grabando…" en la pista destino o en
  una fila nueva. La toma va a la pista vacía como en la web (`GuitarCam.Projects/TakeRecording.cs`), es
  un paso de deshacer, recalcula el compresor con su nivel y se guarda antes de borrar el diario; cerrar
  la ventana grabando la guarda (`MainWindow.Recording.cs`). `gclab proyecto toma` y
  `proyecto verificar-tomas`. Probado con la Focusrite: dos tomas guardadas y la recuperación de un corte.
  Solo audio hasta que esté la cámara.

- 2026-10-22: color y efectos de video en la preview (B7): LUT 3D trilineal, grano, viñeta y barras de
  cine, los mismos de la exportación (`VideoColorProcessor`, `Video/PreviewVideoFx.cs`). El color de cada
  cuadro corre en paralelo (1,2 ms por cuadro de 640 px en vez de 5,3). Falta la pestaña Color para
  editarlo (A6); hoy se ve el que trae el proyecto.

- 2026-10-22: escucha del click desde su menú y al mover el volumen (`PlaybackEngine.PreviewClick`).

- 2026-10-22: inspector completo (§1.2; tarea A4, ChatGPT): mezcla, saturación, compresor con estilos,
  cantidad, ajuste fino y EQ del preset, EQ de 6 bandas con editor gráfico y plantillas, envíos de reverb
  y delay, sincronización por clip, encuadre y clip (recortes y fades de audio y video), secciones
  plegables recordadas (`MainWindow.Inspector.cs`, `Controls/EqEditor.cs`, `Controls/Inspector*.cs`).
  Todo con deshacer. Falta el espectro en vivo del EQ.

- 2026-10-22: render offline de la mezcla (parte de audio de §8; tarea B2, Antigravity):
  `Dsp/MixRenderer.cs` (la cadena por pista y los buses, los mismos que usa la reproducción, sin la
  demora del limitador) y `gclab mezclar`, verificado contra la web. `AudioLoader.cs` carga los medios y
  los remuestrea a 48 kHz para escuchar y exportar (no es el remuestreador de Chromium: misma duración y
  tono, no las mismas muestras).

- 2026-10-22: calibración de latencia (§5.9, motor; tarea B5, Antigravity): `LatencyCalibration.cs`,
  port de `latencyCalibration.ts` (con cable o tocando), verificada con `gclab verificar`; se guarda por
  driver y canales en `%APPDATA%\GuitarCam Studio\latencia.json`. `gclab latencia --modo cable|tocando`,
  `--ver`, `--borrar`, `--simular`. Falta la pantalla (Configuración → Sincronización).

- 2026-10-22: tooltip "Rehacer (Ctrl+Y)", igual que la web (R5, ChatGPT).

- 2026-10-22: ports del DSP que faltaba (§5.3 a §5.6 de MIGRACION; tarea B1, Antigravity), verificados
  muestra a muestra con `gclab verificar` (también con bloques de 100 y 441): presets y EQ del
  compresor, EQ de pista, reverb, delay con ducker, plantillas de paneo y fades de clip, en
  `src/GuitarCam.Audio/Dsp/`. Todavía no suenan en la ventana (tarea B3).

- 2026-10-22: deshacer / rehacer (§9 de MIGRACION; tarea A2, ChatGPT): `GuitarCam.Projects/UndoManager.cs`,
  port de las fotos de `App.tsx` (pistas, clips, canales, formato, aspecto, reverb, delay, color, efectos;
  200 pasos; misma clave en menos de 800 ms = un paso). Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z y los botones de la
  barra; se vacía al cambiar de proyecto. `gclab proyecto verificar-deshacer`. Además, `gclab proyecto
  versiones` / `volver` para las versiones guardadas.

- 2026-10-22: gestor "Proyectos" y plantillas, igual que `SessionPicker` de la web (§2.1 y §2.2 de
  MIGRACION; tarea A1, hecha por Claude y ChatGPT):
  - `GuitarCam.Projects/ProjectLibrary.cs`: lista de la carpeta de proyectos + recientes, nombres
    limpios para Windows y "(2)", renombrar (con la carpeta), duplicar (con hard links, sin exports,
    en una carpeta temporal que se publica al terminar), Papelera, carpeta de proyectos cambiable
  - `ProjectTemplates.cs`: las 5 de fábrica + las del usuario en `%APPDATA%`, "Guardar como plantilla"
  - `Controls/ProjectsDialog.axaml(.cs)`: la app arranca ahí y el chip lo abre; los atajos de la
    ventana se apagan mientras está abierto
  - `gclab proyectos …` para todo lo anterior

- 2026-10-22: formato de proyecto propio (`src/GuitarCam.Projects`), sin compatibilidad con la web:
  - `guitarcam.json` con medios / pistas / clips (varios clips por pista, pistas solo audio)
  - escritura atómica (tmp + `File.Replace` con `.bak`), autoguardado a los 1,5 s, Ctrl+S
  - historial en `historial/` (cada 10 min, 30 recientes + una por día por 90 días) y "Volver a una versión guardada"
  - si el archivo está dañado abre la copia válida más nueva; bloqueo para no abrirlo en dos ventanas
  - versión del formato con migraciones; una versión más nueva abre solo para ver
  - grabaciones recuperables: `RecordingWriter` + diario `*.grabando.json`; archivos sueltos en `media/` vuelven como medios
  - importador de la web (`WebImporter`, menú del proyecto o `gclab proyecto importar`): carpeta nueva
    "Nombre (nativa)" con enlaces duros a los medios, el original no se toca

- 2026-10-22: video decodificado de verdad en la preview (`Video/VideoDecoder.cs`): FFmpeg por pipe,
  BGRA a 30 fps y hasta 640 px de ancho, un hilo por toma que lee un poco por delante del playhead

- 2026-10-22: primera ventana (`src/GuitarCam.App`, Avalonia 11.3): abre el proyecto más reciente de
  la web, timeline con ondas, preview con la grilla de paneles portada, inspector de toma (volumen,
  paneo, saturación, compresor), reproducción por ASIO con click, medidores de entrada y de pista
