# Pendientes — GuitarCam Studio Native

Ideas y cosas para implementar en la versión nativa. Cada ítem: qué es, por qué, y notas si hay.
Al terminar uno, pasarlo a **Hecho** con la fecha.

## Ideas nuevas (solo nativa)

_Cosas que no están en la app web o que la versión nativa debería hacer distinto._

> **Foco:** la app sigue siendo para hacer videos de guitarra. Lo de DAW tiene que servir para eso
> y no puede complicar el camino de "grabo, mezclo y exporto el video".

### Pedidas

#### 1. Usarla también como DAW, solo audio
- [x] Proyectos o pistas sin video (hecho, ver abajo)
- [x] Edición de clips: dividir, duplicar, mover a otra pista y a otro tiempo, borrar un pedazo (rango) (hechos). Falta
  borrar el rango "cerrando el hueco" (que lo de después se corra) y rangos que abarquen varias pistas
- [ ] Ganancia por clip, aparte del fader de la pista (con su curva de volumen dentro del clip)
- [ ] Varios clips por pista: ya se reproducen, se dibujan y se exportan (comparten el panel); falta grabar
  desde el cabezal (hoy toda toma arranca en 0) y elegir qué clip se ve cuando se pisan
- [x] Importar audio externo (hecho, ver abajo). Falta: que el clip importado siga el tempo del proyecto (hoy se
  usa tal cual) y importar el audio de un video
- [ ] Mejorar el mixer: inserts reordenables, más sends/buses, grupos, medidores más completos
- [ ] Exportar las pistas por separado (stems). La mezcla en WAV / MP3 ya está
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

Nada: al 2026-10-23 está todo portado. El relevamiento (qué hace cada cosa en la web, cómo funciona y
cómo quedó acá) sigue en [docs/MIGRACION.md](docs/MIGRACION.md); si la web suma algo, se anota ahí y se
porta igual ([AGENTS.md → Migrar algo de la web](AGENTS.md#migrar-algo-de-la-web)). Lo que falta probar a
mano está en [docs/PRUEBAS.md](docs/PRUEBAS.md).

## Notas

- Puntitos en la preview (resuelto): con la ventana permitiendo transparencia, algunos píxeles oscuros
  del video dejaban ver la ventana de atrás. `TransparencyLevelHint="None"` lo corta, y con el video
  decodificado quedan solo unos pocos píxeles negros sobre casi negro, invisibles. Ojo al revisar
  capturas: las de GDI (CopyFromScreen/PrintWindow) dan alfa 0 en los píxeles negros y los visores los
  pintan blancos, aunque en pantalla se vean bien. Mirar el RGB, no el alfa.
- "Volver a una versión guardada" (solo nativa: la web no tiene historial de versiones) no tiene dónde
  tocarse en la ventana: el menú provisorio del chip se fue con el gestor y la pestaña Historial de la web
  no lo trae. Hoy solo con `gclab proyecto versiones` / `volver`. Falta decidir dónde va.
- `gclab verificar` corre los `.ts` reales de la web, pero los nodos nativos de Web Audio (Biquad,
  Delay, Convolver) y el ruteo de los buses son una emulación en JS (`tools/worklet-ref.mjs`). Falta
  una pasada contra un `OfflineAudioContext` de verdad en Chromium para confirmarlos.
- Lo que falta probar a mano (saltos del video mientras reproduce, fps con muchas tomas, calibración,
  medidores con sonido…) está todo en [docs/PRUEBAS.md](docs/PRUEBAS.md).
- Cámara: mientras la app está abierta, la cámara queda tomada (como en la web), así que otro programa no
  la puede usar. Si se desconecta, el monitor dice "Sin señal de cámara" y no se reconecta sola: hay que
  tocar "Refrescar dispositivos".

## Hecho

- 2026-10-24: **borrar un pedazo (rango)** (ítem 1): Shift+arrastrar sobre una pista marca un rango (con el imán, Alt
  = libre; se ve como una banda de acento con su largo) y Supr, o "Borrar el rango" del clic derecho, saca ese tramo
  de todos los clips de la pista: los que quedan adentro se van, los que sobresalen por un lado se recortan y los que
  sobresalen por los dos se parten en dos, con el fade in a la izquierda y el fade out a la derecha. Queda el hueco y
  nada se corre; es un paso de deshacer y no toca los archivos (`ClipEditing.DeleteRange`, `TimelineView.Range`,
  `MainWindow.DeleteRangeSelection`). Un clic, Esc o cualquier edición sacan la selección. `gclab proyecto editar
  carpeta borrar-rango pista desde --hasta fin` y 9 comprobaciones nuevas en `verificar-clips`. Probado con `gclab`
  (borrar y mezclar) y la ventana abre; sin ver el arrastre ni la banda con el mouse.

- 2026-10-24: **mover un clip a otra pista y a otro tiempo a la vez** (ítem 1, pedido del usuario): al arrastrar un
  clip sobre otra pista, el clip original queda atenuado donde estaba y un "fantasmita" (contorno punteado de acento
  con el nombre de la pista y el segundo de inicio) muestra dónde va a caer, con el imán de siempre (Alt = libre).
  Ctrl mantiene lo de la web: intercambiar con el clip de abajo, sin cambiar el tiempo (`TimelineView.DrawGhost`).
  Probado con `gclab` (mover y pasar de pista en el mismo proyecto; `verificar-clips` y `verificar-deshacer`
  siguen bien). Sin ver en la ventana: el dibujo del fantasma y el arrastre con el mouse.

- 2026-10-24: **importar audio externo** (ítem 1): "Importar audio…" en el menú del "+" de las pistas o soltar
  archivos WAV / MP3 / FLAC / OGG / M4A / AIFF en la ventana (`MainWindow.Import.cs`). Cada archivo se copia a
  `media/` en segundo plano (`GuitarCam.Projects/AudioImport.cs`, con nombre único y sin dejar medio archivo si
  falla) y queda en una pista nueva de solo audio, sin compresor ni saturación, en el cabezal (la pista vacía
  seleccionada se usa para el primero); es un paso de deshacer. `gclab proyecto importar-audio` y
  `proyecto verificar-importar`. De paso se arregló `AudioLoader`: con FLAC (y otros formatos de Media Foundation)
  el audio se cortaba porque el largo que informa el lector es solo una pista; ahora se lee hasta el final y la
  duración del medio se mide decodificando (`AudioLoader.Measure`). Probado con `gclab`: MP3, FLAC y M4A importados y
  mezclados (largos revisados con ffprobe), y la ventana abre el proyecto. Sin probar a mano: el diálogo de archivos
  y soltar archivos sobre la ventana.

- 2026-10-23: **R arma la pista seleccionada** (pedido del usuario): R ahora arma / desarma la pista seleccionada
  (acción "arm" en `Shortcuts.cs`) y grabar pasó a **Shift+R** o **Num \***; los tooltips del ● lo dicen.

- 2026-10-23: **atajos personalizables** (pedido del usuario): pestaña "Atajos de teclado" en Configuración, con una
  fila por acción agrupada en Transporte / Pistas / Edición / Vista; clic en la tecla y se toca la nueva (Esc cancela),
  si era de otra acción esa queda sin atajo y se avisa, ↺ por fila y "Restablecer todo". Se guardan en `config.json`
  ("shortcuts", solo lo que cambió). `OnKey` y las teclas de grabación buscan la acción en la tabla (`Shortcuts.cs`)
  y todos los tooltips leen la tecla de ahí. Atajos nuevos: **B** muestra / oculta el panel de la toma (izquierda) y
  **P** el del proyecto (derecha). Compila y arranca; la pestaña no la pude probar con el mouse en esta sesión.

- 2026-10-23: barras de estado más prolijas (pedido del usuario): botón **Monitor** al lado del medidor de entrada
  (prende el monitoreo por software, el mismo de Configuración); el formato ("48 kHz · 192 muestras") chiquito debajo
  del medidor; la carga DSP arriba con barrita, junto a CPU / GPU / RAM, que ahora se leen "CPU 19% ▬" (antes el
  número quedaba pegado a la etiqueta siguiente) con líneas finas entre los grupos (`MainWindow.axaml`,
  `MainWindow.TopBar.cs`, `Controls/SystemMeter.cs`). El medidor de entrada tiene el ancho de ese texto y una escala
  en dB debajo (`StereoMeter.ShowScale`). Visto en la ventana; el botón Monitor no lo escuché.

- 2026-10-23: **Quitar todos los mute / solo** (pedido del usuario, como Cubase): una M y una S al final de la barra
  de arriba del timeline (la columna de encabezados pasa a medir 324 como mínimo para que entren) y en la barra del
  mixer; se prenden si alguna pista tiene mute o solo y un clic los saca todos en un solo paso de deshacer
  (`MainWindow.ClearAllToggles`). Las plantillas de paneo del mixer pasaron a un desplegable "Paneo" con la apertura
  adentro (30 % por defecto). Visto en la ventana con la copia de prueba de "Camino a San Francisco".

- 2026-10-23: **Video | Solo audio** (ítem 1, pedido del usuario): interruptor en la barra de título y en "Nuevo
  proyecto" (`ProjectSettings.Mode`, `MainWindow.Mode.cs`, `gclab proyecto modo`). En solo audio el timeline ocupa el
  lugar de la composición, la cámara queda libre, se ocultan Color, Encuadre y lo de video del inspector, y
  "Exportar audio" saca la mezcla en WAV de 24 bits o MP3 de 320 kbps (`AudioExporter`, `gclab exportar --audio`).
  Se cambia en cualquier momento, es un paso de deshacer y no borra nada. Además, "En el video" por pista
  (inspector y clic derecho) para una pista que suena sin panel. Probado en la ventana con una copia de PruebasNEW:
  pasar a solo audio y volver, exportar un MP3 y abrirlo desde Historial, Ctrl+Z del cambio; y con `gclab` (WAV y
  MP3 revisados con ffprobe, `verificar-deshacer`). Sin probar: grabar una toma en solo audio y el formulario de
  "Nuevo proyecto" en la ventana. No quedó en el mixer el interruptor "En el video" (no entra en las tiras
  angostas): está en el inspector y en el clic derecho del timeline.

- 2026-10-23: dividir y duplicar clips (ítem 1, primer tramo): Alt+X divide el clip en el cabezal y D lo duplica
  justo después, también desde el clic derecho (`ClipEditing.Split` / `Duplicate`, `gclab proyecto editar …
  dividir|duplicar`, comprobados en `verificar-clips`). La exportación pone todos los clips de una pista en su
  panel, cada uno en su tramo (`ExportTakeSpec.Panel` en `Mp4Exporter`); probado con `gclab exportar` sobre una
  copia de PruebasNEW con clips divididos, movidos y duplicados (cuadros revisados a mano).
- 2026-10-23: **grabar varias pistas a la vez** (pedido del usuario): se arman varias pistas y R graba una toma
  por pista desde el mismo sample, cada una con su entrada (chip "1-2 ▾" en el encabezado armado y en la tira del
  mixer; `Track.Input`, `InputRoute`/`InputTap` en `GuitarCam.Audio`, el motor abre todas las entradas). Un journal
  por toma, así un corte las recupera todas; el video va a la primera armada que sale en el video. El ● ahora se
  puede desarmar también cuando lo eligió la app, y las pistas vacías tienen volumen, paneo, M, S y medidor.
  Probado: `gclab grabar --simular --entradas 1,2,3-4,1-2,4` (cada WAV comparado muestra a muestra),
  `proyecto verificar-tomas`, y la ventana con un proyecto de tres pistas (chip de entrada visible). Sin probar:
  grabar de verdad con la placa, el menú de entradas y armar varias en la ventana, y el monitoreo de varias entradas.
- 2026-10-23: armar pistas para grabar (pedido del usuario): ● en cada encabezado del timeline y en cada tira
  del mixer; la pista donde cae la toma se tiñe de rojo; armada con una toma, la nueva la reemplaza (la vieja
  queda en Historial) y la pista no suena mientras grabás. El botón Grabar del transporte quedó como un ● chico.
  Probado en la ventana: armar, ver el tinte en timeline y mixer, y grabar unos segundos sobre una pista con
  dos clips. Sin probar: grabar tocando de verdad (la toma salió muda).

- 2026-10-23: editar el timeline ya no recarga el proyecto: mover, recortar, fades, cambiar de pista, quitar,
  reordenar, deshacer y sumar una toma grabada se aplican en el lugar (`ApplyTracks` en `MainWindow.Timeline.cs`):
  pistas y clips se emparejan por id y siguen con su audio, su video, el estado de los efectos y el cabezal,
  sin parar la reproducción. Solo se lee del disco la toma nueva. Antes cada gesto paraba el audio, reabría
  todos los videos y el clip soltado volvía un instante a su lugar viejo.
- 2026-10-23: las tomas grababan con cortes en el lado derecho: en estéreo, el "mono automático" (la entrada 1
  a los dos lados si la 2 está muda) se decidía cada bloque y el soplido de la entrada 2 de la Focusrite
  (~0,0001) caía justo en el umbral de la web (0,00005), así que el lado derecho se prendía y apagaba ~60
  veces por segundo. Ahora la decisión queda trabada hasta que la entrada 2 trae señal de verdad y la 2
  cuenta como muda hasta -66 dBFS (`InputProcessor`, `RecordingSession.cs`). La web tiene el mismo problema.
  Se prueba con `gclab grabar --simular --canal estereo --ruido-2 0.00005`.
- 2026-10-23: la app se llama **GuitarCam Studio Native**: título de la ventana, badge "NATIVE" en la barra
  de título (`MainWindow.axaml`), nombre y descripción del exe (`GuitarCam.App.csproj`), acceso directo del
  escritorio (`tools/make-shortcut.ps1`, borra el viejo "(nativa)") y docs. El logo de la barra de título
  es el ícono de la app (`Assets/GuitarCam-64.png`, de `tools/make-icon.ps1`) y abre "Acerca de"
  (`Dialogs/AboutDialog`: versión con el commit, en qué está hecha y la carpeta de ajustes).
- 2026-10-23: lo último que faltaba de MIGRACION (§1.4 y §11): la cuenta grande 1-2-3-4 sobre el escenario
  (`PreviewView.CountdownBeat`, como el overlay de `SplitScreenCanvas`), "Mostrar en el Explorador" al frente
  (`Explorer.cs`, port de `openInExplorer` + `ForegroundWindow.cs`, en el gestor, el Historial y la
  exportación) y `tools/make-shortcut.ps1` para el acceso directo. El selector de carpetas de Avalonia ya era el
  moderno de Windows. Probado: la cuenta en la ventana (se ve y se va al cancelar, sin dejar archivos) y el
  Explorador (el usuario: abría, pero tardaba). Ahora le pide la carpeta directo al shell (ShellExecute /
  `SHOpenFolderAndSelectItems`, ~0,3 s contra 0,8–1,5 s de arrancar explorer.exe) en segundo plano. Sin probar
  en la ventana esa última versión (la app estaba abierta; compilada aparte).

- 2026-10-23: miniaturas del video en los carriles (§3 y §11 de MIGRACION): `Video/VideoThumbnails.cs`, port de
  `thumbnails.ts` (16 cuadros de 160 px por toma, en memoria, FFmpeg en segundo plano de a una toma) y la tira de
  `Filmstrip` en `TimelineView` (celdas 16:9 a la altura del carril, cover, espejo del clip, la onda blanca abajo
  sobre el degradé negro), desde 52 px de alto. Comparado con la web (TESTINGS). Revisado lo que habían dejado los
  agentes: A5 (el tempo con el mouse: arrastre del entero y del decimal, rueda y doble clic para escribir, probado
  en la ventana) y B6 (el diálogo de exportación lado a lado con la web: igual salvo la calidad, que ahora usa el
  `Segmented` gris de la web en vez de una pastilla de acento). Falta escuchar el menú del click y la calibración
  con cable o tocando (necesitan el cable o la guitarra).

- 2026-10-23: Configuración → Entradas y salidas (§7 de MIGRACION): "Buffer" con el tamaño actual y el botón
  "Panel del driver" (`PlaybackEngine.ShowDriverPanel`); cuando el driver pide reiniciarse (nuevo buffer) el
  audio se reabre solo, esperando si está grabando o calibrando (`AsioDuplex.ResetRequested`). Papelera (§11): lo
  que se borraba ya iba a la Papelera; ahora también la toma de "Parar sin guardar" (WAV y video), con un aviso en
  la barra de estado. F2 en el gestor ya estaba. Probado: abrir y cerrar el panel de la Scarlett y descartar una
  toma en la copia de prueba. Sin probar: cambiar el buffer desde el panel (no toqué la configuración de la interfaz).

- 2026-10-23: timeline (§3 y §1.7 de MIGRACION): la esquina de la regla quedó como la de la web (modo de regla
  en un recuadro con el activo resaltado, imán con su ícono, "Ver todo" deshabilitado si ya está ajustado,
  altura de pistas de a 2 px con el slider chico de la web) y la altura de la timeline se recuerda
  (`timelineHeight`; mínimo 110, doble clic en el separador vuelve a la de por defecto). La regla en minutos,
  el reloj que la alterna, el botón del medio y deseleccionar con un clic en blanco ya estaban. De paso:
  abrir un proyecto ya no deja un paso de deshacer vacío "track:…:name" (`InitInspector`: Avalonia avisa el
  `TextChanged` después, fuera de `syncing`). Probado en la ventana: arrastrar y doble clic en el separador,
  cerrar y abrir.

- 2026-10-23: pestaña Historial (§2.3 de MIGRACION): `MainWindow.History.cs`, port de `HistoryPanel.tsx`, con la
  lógica en `GuitarCam.Projects/ProjectHistory.cs` (`gclab proyecto historial` y `verificar-historial`). Tomas con
  filtro, buscador y días, escuchar el WAV crudo (`GuitarCam.Audio/TakePreview.cs`, salida de Windows), quitar /
  usar, "Borrar no usadas" a la Papelera (limpia el deshacer, como la web); Exportaciones (ver, mostrar, Papelera
  con confirmación en la fila; ahora guardan sus tomas); Actividad en frases por día con "Ver más". Las tomas que
  salen o vuelven al timeline se registran al guardar, como el `saveState` de la web. Es la pestaña por defecto.
  Probado en la ventana con una copia de "Camino a San Francisco": quitar, usar, escuchar, borrar no usadas,
  exportar, mandar el MP4 a la Papelera y el buscador; comparado con la web (TESTINGS). Sin probar: escuchar con
  los parlantes (solo vi el cambio de ícono al empezar y al terminar).

- 2026-10-23: más colores en el selector de tema (`UiTheme.cs`): 8 acentos nuevos (Naranja, Oro,
  Esmeralda, Menta, Celeste, Índigo, Lavanda y Fucsia) y 3 fondos oscuros (Océano, Café y Humo).
  El catálogo queda en 16 acentos y 9 fondos, con el mismo selector y tema predeterminado.

- 2026-10-22: barra superior (§1.1 de MIGRACION): `MainWindow.TopBar.cs`. Indicador "Guardando / Guardado /
  Error al guardar" en el chip del proyecto; medidor CPU / GPU / RAM (`GuitarCam.Audio/SystemStats.cs`, contadores
  de Windows por PDH; `gclab sistema`); resolución de la cámara, "48 kHz" y FFmpeg con su punto verde o "FFmpeg no
  encontrado"; selector de tema (`UiTheme.cs`, `Controls/ThemePicker.cs`) con los acentos, "Color a elección" y
  los seis fondos de la web, en vivo y recordado. Los tokens de color ahora son pinceles que cambian con el tema
  (`Palette` y App.axaml). Clic en el medidor de entrada abre Configuración → Entradas. De paso: los diálogos
  (Proyectos, Exportar, Configuración) no tapaban la barra derecha. Probado en la ventana con la Focusrite: el
  indicador, FFmpeg, abrir el tema, Madera + Violeta en vivo y recordado. Sin probar: "Color a elección"
  (diálogo de Windows) y el medidor del sistema en la ventana (necesita 1600 px; con `gclab sistema` sí).
  Lo que se arma una sola vez (el número de toma del inspector) conserva el acento anterior hasta reabrir.

- 2026-10-22: lo que faltaba del inspector (§1.2 de MIGRACION): el panel "Toma" se pliega a una columna
  de íconos ("Ocultar panel" / "Mostrar la toma") y se recuerda (`sidebarCollapsed`); estados vacíos
  como la web ("Seleccioná una toma" + "Todavía no hay tomas grabadas." / "Hacé click en una pista o en su
  panel del video.", y "Pista vacía" con el número gris, el texto de grabar y "Quitar pista" con ícono);
  el ícono de "Mostrar / Colapsar todos los efectos" cambia según el estado; y el **espectro en vivo** del
  EQ (`GuitarCam.Audio/Dsp/SpectrumTap.cs`: port del AnalyserNode de la web, FFT de 8192 con Blackman y
  suavizado 0,75; el hilo de audio solo copia a un anillo preasignado y la FFT corre en el `Tick`; el
  gráfico dibuja igual que `EqGraph` de la web). `gclab espectro` lo comprueba contra la fórmula del
  AnalyserNode (bien a 0,05 dB). Probado en la ventana: plegar y desplegar, proyecto sin tomas, pista
  vacía y el espectro de una toma sonando. Las plantillas de paneo y el canal de la pista vacía no van en
  el inspector: en la web están en el mixer, que ya los tiene.
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
  Todo con deshacer. (El espectro en vivo del EQ se sumó después, ver arriba.)

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
