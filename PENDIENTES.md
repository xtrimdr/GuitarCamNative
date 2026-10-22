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

- A5 · transporte y tempo (`MainWindow.Transport.cs`, `Controls/TempoControl.cs`): implementado, con la
  escucha del click (`PreviewClick`); falta la prueba a mano en la ventana: Detener, play/pausa, loop,
  reloj, tempo (arrastre, rueda, doble clic, TAP), Cuenta y el menú del click (que se escuche)
- B6 · el diálogo de exportación ya se abre con "Exportar MP4"; falta la captura lado a lado con la web

## Notas

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

## Hecho

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
