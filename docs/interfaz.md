# Interfaz — cómo es cada parte de la ventana

Referencia para agentes: qué hay en cada parte de la ventana, cómo se comporta y en qué archivo vive. No hace falta
leerla entera: buscá el apartado de lo que vas a tocar. Las reglas generales de diseño (tokens, tipografía, clases,
atajos) están en [src/GuitarCam.App/AGENTS.md](../src/GuitarCam.App/AGENTS.md). Si cambiás cómo funciona algo de
acá, actualizá su apartado en el mismo cambio.

La captura de referencia es [captura.png](captura.png).

Todo lo que se describe acá lo decidió el usuario: no lo cambies sin preguntar, aunque otro DAW (o la web
`..\GuitarCam`, de donde nació la app) lo haga distinto.

## Splash (`SplashWindow`)

Al abrir la app, mientras se arma la ventana principal: ícono grande, nombre, frase, versión con el commit
("PRUEBA" en una copia de prueba) y una franja del acento que corre por el borde de abajo (animación de composición:
sigue moviéndose aunque el hilo de la UI esté ocupado). Toma los colores del tema, dura al menos 3 s (pedido del usuario), queda
encima de la ventana principal mientras esta carga detrás y se va con un fundido. Excepción a "sin gradientes": el brillo del acento detrás del ícono y la franja
(`BrandArt`, compartido con "Acerca de").

## Estructura

```
┌ barra de título propia (44) ─ logo · nombre · NATIVE · (PRUEBA) ··························· CPU │ GPU │ RAM │ DSP │ FFmpeg · 🔔 · tema · ⚙ · [_ □ ×] ┐
├ fila de herramientas (~40) ─ Video|Solo audio · chip del proyecto (guardado) ▾ │ guardar · deshacer/rehacer ···· [↖ ⌶│✂ ⌫│🎧 🔍 ✋] ···· [Exportar MP4] ┤
├ inspector (290↔) ┬─ preview (un panel por pista con video) ────────┬─ "En vivo" (256) ─┬─ proyecto (290↔, plegable a 44) ┤
│ TOMA + secciones │                                                 │ cámara + entrada  │ pestañas Color / Historial     │
├──────────────────┴─ separador arrastrable ─────────────────────────┴───────────────────┤                                │
│ timeline (280): regla (32) · encabezados fijos (324) · un carril por pista (64, 40–320) │                                │
├ transporte, siempre abajo (~52) ────────────────────────────────────────────────────────────────────────────────────────────┤
│ ·························· medidor de entrada (kHz · buffer debajo) · Monitor  [ reloj │ ⏮ ■ ▶ ● loop │ Compás Tempo TAP Cuenta Click ▾ ] ··· Timeline/Piano roll/Mixer/Mastering · Ver todo │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Ese es el diseño **Clásico**. Para el **Vertical**, ver el apartado que sigue.

## Diseño de la ventana (`MainWindow.Layout.cs`)

- Botón de la barra de título (ícono de mosaico, antes del tema) con un menú: **Clásico**, **Vertical, video a la
  izquierda** y **Vertical, video a la derecha**, cada uno con un dibujito y el elegido en acento. Es de la app, no del
  proyecto (`config.json`: `layout`), pedido del usuario.
- Vertical: la columna del video (formato, preview y "En vivo" debajo de la preview) va de arriba a abajo entre el
  inspector y la barra del proyecto, a la izquierda o a la derecha de la timeline; la timeline ocupa el resto y el
  mixer (o Mastering) se abre debajo, igual que en solo audio (`MoveBottomView`, `stackedViews`), sin mini timeline.
  El ancho de la columna se arrastra desde su borde interior (400–1000, 460 por defecto, doble clic vuelve;
  `stageWidth`) y se achica sola si la ventana no da, para que la timeline conserve 360.
- En solo audio no hay video: el diseño no cambia nada y se ve como siempre, y el botón queda desactivado con un
  tooltip que dice por qué (pedido del usuario).
- Al pasar el proyecto a 9:16 en Clásico, un toast ofrece el Vertical (una vez por sesión); nunca cambia solo.
- Los paneles (InspectorBar, StageArea, AudioArea con TimelineHost y MixerHost, ProjectBar) se mueven sin
  reconstruirse; la barra de título y el transporte no se mueven. `ApplyLayout` decide dónde va cada uno: todo
  diseño nuevo pasa por ahí. Falta sacarlos a otra ventana (etapa 3 de PENDIENTES).

## Barra de título

- Son dos filas en un mismo `TitleBar` (para que todo entre con la ventana sin maximizar, pedido del usuario): la
  primera, de 44, es la barra de título (`ExtendClientAreaTitleBarHeightHint="44"`) con la app, los medidores, avisos,
  tema y Configuración; la segunda es la del proyecto: modo, chip, guardar/deshacer y Exportar a la derecha (ahí no
  hay botones del sistema, así que va sin el margen de 150). Las dos arrastran la ventana.
- En el medio de la segunda fila, las **herramientas del timeline** (`MainWindow.Tools.cs`, pedido del usuario): una
  caja como la de Video | Solo audio (`Border.mode` con `ToggleButton.mode` de 32 px, solo ícono) con Seleccionar ·
  Rango │ Dividir · Borrar │ Escuchar · Zoom · Mano. Queda **centrada en la ventana**, como el transporte; solo se
  corre (`PlaceTools`) si no entra entre deshacer y Exportar. Con el mixer en lugar del timeline se apaga.
- Es la de la app (`ExtendClientAreaToDecorationsHint`): arrastrable, doble clic maximiza, los botones del sistema
  quedan a la derecha (margen 150). `TransparencyLevelHint="None"` no se toca: con transparencia, píxeles oscuros
  del video dejan ver lo que hay detrás.
- El logo es el ícono de la app y abre "Acerca de" (`Dialogs/AboutDialog`), con el mismo encabezado que el splash
  (`BrandArt`: logo grande, brillo y línea del acento del tema, versión en la esquina).
  Ahí dice si hay una versión nueva: 5 s después de abrir, `MainWindow.Updates.cs` le pregunta a GitHub por la última
  release (`UpdateCheck`, en `GuitarCam.Projects`; sin cuenta ni datos del usuario, y si falla no avisa nada). Una
  versión nueva sale en un toast con "Descargar" solo la primera vez (`config.json` "updateNotified"); después, solo
  en la campanita. La casilla "Avisarme cuando salga una versión nueva" lo apaga (`config.json` "checkUpdates": "off").
- FFmpeg solo aparece en la barra si falta ("FFmpeg no encontrado", `MainWindow.TopBar.cs`). Al abrir sin FFmpeg, y
  al hacer clic en ese chip, sale "Falta FFmpeg" (`Dialogs/FfmpegDialog`, pedido del usuario). Ofrece Instalar ahora
  (winget, `FfmpegSetup`, si está; se puede cerrar mientras instala y avisa con un toast), Abrir la página (gyan.dev),
  Ya lo instalé (relee el PATH y lo busca de nuevo) y el comando de winget para copiar.
- El chip del proyecto abre un menú (`MainWindow.Versions.cs`): "Proyectos…" (el gestor) y "Volver a una versión
  guardada", con las últimas 20 de `historial/` (`ProjectStore.Snapshots`). Volver guarda antes lo actual como otra
  versión, recarga el proyecto y borra los pasos de deshacer. Sin proyecto abierto, va directo al gestor.
- El gestor (`Controls/ProjectsDialog.axaml.cs`) empaqueta: el botón de paquete de cada fila abre "Para compartir" /
  "Respaldo completo" y pide dónde guardar el ZIP (`ProjectLibrary.Pack`; nunca dentro del mismo proyecto; el actual
  se guarda antes, `FlushCurrent`) y "Abrir paquete…" de la barra lo trae como proyecto nuevo, resaltado en la lista
  (`ProjectLibrary.Unpack`).
- Al lado del nombre, el estado del guardado (`TickTopBar`): Guardando / Guardado / Sin guardar / Error al guardar /
  Solo lectura. "Sin guardar" sale cuando Configuración → Guardado y exportación (`SaveMode`) guarda cada X minutos o a mano; el
  diskette antes de deshacer (`SaveButton`, el atajo "save") guarda ya y está apagado si no hay nada pendiente.
- Cerrar la ventana con cambios sin guardar (modo cada X minutos o a mano, o un guardado que falló) pregunta
  "¿Guardo los cambios?" (`Dialogs/UnsavedDialog`): Guardar y cerrar (Enter), Cerrar sin guardar, Cancelar (Esc).
  Si el guardado falla, no cierra. Después de cada edición no pregunta: lo pendiente se escribe y cierra.
- Una copia hecha por `tools\probar.ps1` lleva un `prueba.txt` al lado del exe (commit, con o sin cambios, hora):
  `TestBuild` lo lee y la barra muestra, después de NATIVE, el badge "PRUEBA a1b2c3d + cambios" (`TestBuildChip`,
  fondo `Hover`, la hora en el tooltip) y el título de la ventana lo repite. Sin ese archivo no aparece nada.
- CPU / GPU / RAM / DSP con la misma forma ("CPU 19% ▬"), con líneas `Border.tsep` entre los grupos.
- Maximizada, Windows corre la ventana 8 px afuera de cada borde (por la barra propia). La grilla raíz lleva
  `Margin="{Binding $parent[Window].OffScreenMargin}"` para compensarlo; no lo saques o se cortan los costados y
  la barra de transporte.
- Los paneles (inspector, barra de proyecto, timeline, mixer) son tarjetas (pedido del usuario, para ver dónde empieza
  y termina cada uno): radio 10, borde `Line` completo y 4 px de margen, así entre dos quedan 8 px de fondo `App`
  (clases `card`, y `card-clip` + `card-edge` en el timeline y el mixer, que pintan hasta el borde). Las barras de
  título y de transporte siguen de lado a lado. Los separadores arrastrables van en una fila de 0 y ocupan ese hueco
  con `Margin="0,-4"`.

## Avisos y toasts

- **Avisos** = la campanita (`Controls/NotificationBell`, a la izquierda del tema). `Status(texto)` agrega el mensaje
  a su lista (los últimos 50, el más nuevo arriba); un punto de acento marca los sin leer y abrirla los marca como
  leídos. Nada se escribe en el centro de la barra de título ni en diálogos.
- **Toasts** (`Controls/ToastHost`): `Toast(texto)` es para lo que conviene ver en el momento; sale abajo a la
  derecha, encima de todo, se va sola a los 6 s (el puntero encima la frena) y también queda en la campanita. Una
  fija (`sticky`) se va solo con su ×: la usa `--probar "texto"` ("Para probar"). Puede llevar un botón (`action`).
  Su letra es más grande que la del resto de la ventana (texto 14, título 12, 420 px de ancho; pedido del usuario,
  para leerla de un vistazo).
  Salen como toast (lo eligió el usuario; el resto queda solo en la campanita): los errores al guardar el proyecto o
  una toma (el reintento del guardado, solo la primera vez seguida), la toma guardada (o su problema), la exportación
  terminada (con "Mostrar" en el Explorador), la cámara que deja de mandar imagen (`TickCamera`, no en solo audio) y
  la interfaz de audio que deja de llamar más de 3 s (`PlaybackEngine.Stalled`, `TickAudioDriver`).

## Transporte (abajo)

- Celdas planas con el valor arriba y una etiqueta chica abajo, al estilo Fender Studio Pro.
- En el medio, **un solo recuadro oscuro** (`Border.metro`) con reloj │ transporte (`Button.tbtn`; Grabar es un ●
  rojo sin texto) │ metrónomo, separados por `Border.vsep`. Queda **siempre centrado en la ventana**: columnas `*,Auto,*`.
- A la izquierda, pegados al recuadro: el medidor de entrada (del ancho del texto de abajo y con su escala en dB,
  `StereoMeter.ShowScale`) con "Entrada 1-2 · 48 kHz · 192 muestras (4,0 ms)" chiquito debajo (el driver va en el
  tooltip) y el botón **Monitor** (`ToggleButton.cell`, el monitoreo por software de Configuración). A la derecha, la vista.
- Metrónomo: una caja con celdas Compás · Tempo · TAP · Cuenta · Click · sonido ▾ (ícono `MetronomeIcon`, dibujado
  porque Segoe no tiene metrónomo).

## Afinador y LUFS (`MainWindow.Meters.cs`)

- En el transporte, después de Monitor, la celda "Afinador" (A) muestra la nota y abre un panel (`Controls/TunerView`)
  con la aguja; escucha la primera pista armada o la entrada 1-2 (`PlaybackEngine.Pitch`, YIN en el `Tick` solo
  mientras está prendido).
- Las notas salen de `Loc` (`note.0`…`note.11`: Do Re Mi en castellano, C D E en inglés; `TunerView.NoteName`).
- A cada lado de la nota, un triángulo dice para dónde girar la clavija (▶ a la izquierda = subí, ◀ a la derecha =
  bajá) y parpadea más rápido cuanto más lejos está (1,5 a 8 por segundo, llevado por el `Tick`, sin asignar). Dentro
  de ±3 cents (`TunerView.InTuneCents`) la nota, la aguja y un "OK!" se ponen en verde (`InTune`), también en la celda.
- El slider "Sensibilidad" es la compuerta de nivel de `PitchTap` (−70 a −30 dB, por defecto −50 =
  `PitchTap.DefaultGateDb`; el umbral de YIN no se toca). Se guarda en `config.json` (`tuner.gateDb`) al cerrar el
  panel; en gclab, `afinar --umbral`.
- El panel se cierra con un clic afuera (`IsLightDismissEnabled`), con A o con la celda; con el foco en el slider,
  los atajos pasan igual.
- A la derecha, la celda "LUFS" con el integrado del master de la pasada actual (`LoudnessMeter`, BS.1770; empieza
  de nuevo con cada play o un clic).

## Inspector (izquierda)

- Número + nombre editable de la toma (`TextBox.title`), los datos del archivo como badges (`Hover`, mono) y un botón
  que pliega o despliega **todas** las secciones.
- Una tarjeta (`Border.section`) por bloque, con ícono + etiqueta en mayúscula y, si se puede apagar, un ON/OFF a la
  derecha. Apagada → controles al 40 % de opacidad, pero se pueden seguir tocando.
- **Los efectos son la cadena de slots del canal** (`RebuildInspectorChain`, pedido del usuario): después de Mezcla,
  una sección por slot en el orden en que suena (Saturación, Compresor, EQ, Puerta; un tipo repetido se numera, "EQ 2", y
  recuerda su propio plegado), con el ⋯ del slot (`SlotMenuButton`) y una manija de puntos (`GripDots`) para
  arrastrarla; al final, "+ Efecto". Se rearma solo cuando cambia la pista o la lista (sus refrescos van aparte, en
  `chainRefresh`); el espectro en vivo va en el primer EQ. Transponer no es un slot y queda fijo.
- **Salida y envíos** (`RebuildInspectorSends`, en `MainWindow.Buses.cs`): después de los slots, la fila SALIDA (Master
  o un bus, `OutputSelect`; una pista en una carpeta dice "Por la carpeta …" y sale con ella) y una sección por bus de
  su lista (`SendTargets`): knob del envío, ON, el chip **PRE** y una × que lo saca de la lista en la cabecera; debajo,
  "+ Envío" (`AddSendButton`) para tildar a qué buses manda. El PRE (`PreChip`: prendido, el envío sale
  antes del fader, después de los efectos y el paneo; el mute igual lo calla). Se rearma solo cuando cambia la pista,
  su carpeta o los buses (nombres, efecto principal, ruteo; sus refrescos van en `sendRefresh`). Su id estable es
  `bus:<id del bus>` y el título que muestra es el nombre del bus (`SendTitles`). Las secciones fijas de Reverb y Delay
  ya no existen: un proyecto nuevo trae esos dos buses y aparecen acá como cualquier otro.
- La cantidad principal de cada efecto va en el knob grande (`EffectKnob`: Vida, los envíos); los
  ajustes finos siguen en sliders.
- Con su encabezado "Toma" se pliega a una columna de íconos de 44, como la barra del proyecto, y se recuerda.
  Plegada, debajo de los dos botones muestra el canal seleccionado (`MainWindow.InspectorRail.cs`, pedido del
  usuario): un ícono por sección en el orden en que suena (Mezcla, los slots, los envíos a buses, Transponer), en acento si está prendida y tenue si no; un clic despliega el inspector, abre esa sección si estaba
  plegada y la sube arriba de todo. Se rearma solo cuando cambia la lista de secciones (`Section` guarda su ícono y su
  ON en `sectionLooks`).
- **Transponer** (`MainWindow.Transpose.cs`, pedido del usuario): su propia tarjeta, después de Delay, con el ícono
  `FxIcon.Transpose`. −12 a +12 semitonos en `Track.Transpose`, mismo largo; no tiene ON/OFF: en "Original" (0) la
  tarjeta queda atenuada como un efecto apagado. Se calcula en segundo plano (`PitchShifter`, unos 4 s por 3 min en
  8 núcleos) y queda en `cache/`; los valores probados en la sesión quedan ahí (volver a uno es inmediato) y lo que no
  se usa se borra una vez al abrir el proyecto. Mientras tanto la pista suena como estaba y **se ve que está
  procesando** (pedido del usuario): sobre sus clips, la onda atenuada, una barra de progreso abajo y "♪ Transponiendo…
  40%" (`TimelineView.DrawTransposing`); el knob Tono en gris con el porcentaje en lugar del nombre; la nota debajo del
  slider. Al llegar, el clip pasa con un crossfade de 10 ms y, si tardó más de 1 s, un toast avisa que ya suena
  transpuesta. Mover el slider cancela lo que ya no se quiere. Todo cambio pasa por `SetTranspose(pista, semis)`
  (inspector, mixer y knob).
- **El canal muestra que está transpuesto**: en el encabezado de pista del timeline, una etiqueta "♪ +2" después del
  nombre (acento; gris mientras se calcula, con `TimelineView.TransposeProgress`); en la tira compacta del mixer, "+2"
  debajo del nombre; en el encabezado alto, el knob Tono (ver la fila de knobs); en la amplia, un panel Transponer después de Delay con − valor + (doble clic en el valor vuelve
  a 0). Mientras se calcula, `transposeClock` refresca esas marcas (`MixerLayout.Transpose`) cada 250 ms.
- El espectro en vivo del EQ sale de un `SpectrumTap` colgado solo de la pista seleccionada mientras el EQ está a la
  vista: el hilo de audio copia a un anillo preasignado y la FFT corre en el `Tick` de la UI.

## Encabezados plegables

Todo encabezado plegable (inspector, Color, Historial) es un `Controls/SectionHeader`: la franja entera reacciona al
hover y se pliega con un clic en cualquier parte fuera de sus botones.

## Barra del proyecto (derecha, `MainWindow.Color.cs`)

- Lo que es del proyecto (lo de la pista va al inspector). Ocupa la altura de la preview y el timeline, se pliega a
  una columna de íconos y recuerda pestaña y secciones plegadas. Las ediciones de Color pasan por `ColorEdit(...)`.
- Las dos barras laterales (esta y el inspector) cambian de ancho arrastrando su borde interior (240–600, el escenario
  conserva 360; doble clic vuelve a 290) y lo recuerdan en `config.json` (`sidebarWidth`, `projectBarWidth`):
  `MainWindow.Sidebars.cs`.
- Efectos de Color: Nitidez, Claridad y Resplandor (`Audio/VideoDetail.cs`; los looks HDR y Soñado suman ahí su parte, `VideoDetail.For`: en la preview, sobre el cuadro decodificado en
  `LiveClip.UpdateVideo`, después del color y el grano, con su radio según lo que muestra el panel, `LiveClip.VisibleHeight`;
  en la exportación, `boxblur` + `lut2`, `unsharp` y `lutrgb` + `boxblur` + `blend` sobre el panel ya recortado), viñeta, grano y barras de cine (No / 1.85 / 2.00 / 2.39 / Libre, con "Alto de las barras" en % del
  panel solo en Libre). En la preview las barras se dibujan fuera del fundido del clip (`PreviewVideoFx.DrawLetterbox`):
  con la opacidad encima dejarían ver el video.
- Pestañas Color / Biblioteca / Historial. Biblioteca (las tomas, usadas o no, importadas o grabadas; la papelera de
  cada fila sin usar, con confirmación en la fila; "Borrar no usadas"; y al escuchar una toma, un mini reproductor bajo
  su fila: onda para saltar con clic o arrastre (`Controls/TakeScrubber`), ±5 s, tiempo y parar, por la salida de
  Windows con `TakePreview`, que hace los saltos con un fundido corto) e Historial (exportaciones y actividad; la pestaña por defecto) viven en `MainWindow.History.cs` y se arman
  solo mientras alguna está a la vista (`RefreshHistory`); lo que hacen con el proyecto y los archivos vive en
  `ProjectHistory` (Projects).
- "Salió / volvió al timeline" se registra al guardar (`ProjectStore.Serialize` → `LogTimelineChanges`).

## Preview y monitor "En vivo"

- **Preview**: los paneles se calculan con `PanelLayout.Compute` sobre un lienzo de 1080 px de alto, **la misma
  cuenta que usa la exportación**: lo que se ve tiene que ser lo que se exporta. Encuadre: cover-fit + zoom/paneo/espejo
  del clip. Etiqueta con el nombre de la pista abajo a la izquierda.
- **Video de la preview**: cada clip tiene su `Video/VideoDecoder` (un hilo que va unos cuadros adelante). Lee con
  FFmpeg dentro del proceso (`NativeVideoSource`, en `GuitarCam.Audio`: los DLL de `lib\ffmpeg`, en el procesador hasta
  1080p y en la placa de video arriba de eso); si no están los DLL o el archivo no abre, con ffmpeg.exe por pipe
  (`PipeVideoSource`, un proceso por salto). Un clip arranca su decodificador 1 s antes de empezar (`LiveClip.PreRollSec`),
  así los cortes no congelan la imagen, y un panel no muestra un cuadro hasta tener uno de ese momento (`LiveClip.Fresh`).
  Un decodificador que no se usa (`VideoDecoder.Idle`) cierra el archivo a los 3 s.
- **Transiciones** (ver Timeline): `LiveTrack.UpdateTransitions` corre en cada tick antes de pedir los cuadros, con la
  misma cuenta que la exportación (`ClipTransitions`); también le pasa al motor el fundido de audio de cada clip
  (`LiveClip.AudioJoins`, cambiado entero solo cuando cambia, como `MixRenderer`): en un fundido cruzado los dos clips muestran video pasando su
  borde (`LiveClip.Lead` / `Tail`, así su decodificador arranca antes) y `PreviewView` dibuja el que sale y encima el
  que entra con su peso. Se ven también en pausa (los fundidos propios del clip, solo reproduciendo, como siempre).
- **"En vivo"** (`Controls/LiveMonitor.cs`), a la derecha de la preview: la cámara cruda, nunca la composición. La
  abre `Video/LiveCamera.cs` (un solo FFmpeg, `CameraCapture`), que también alimenta el video de la toma (`VideoTakeWriter`).

## Timeline

- **Clips**: fondo `Hover` al 40 %, borde `Line` (acento al 60 % si está seleccionado), onda `Dim` (acento si está
  seleccionado, `Faint` si la pista no suena), sin el nombre adentro; sin archivo, gris con "Sin conexión…". Con
  video y carriles de 52 px o más, la tira de miniaturas (`Video/VideoThumbnails.cs`) con la onda abajo sobre
  un degradé (del color de la pista si tiene uno; si no, blanca). Arriba a la derecha, un badge dice si el medio es solo
  audio (nota) o audio + video (cámara), con tooltip (`MediaBadge`). Pista seleccionada con barra de acento de 2 px a la izquierda del encabezado; playhead de acento con triángulo.
- **Bordes de clip**: en cada lado dos manijas chicas como la de la columna (pestañita oscura con dos rayitas), una por
  mitad del clip (`DrawEdgeHandles`): la de arriba, donde termina el fade, mueve el fade; la de abajo, en el borde,
  recorta. El cursor lo dice (diagonal como la línea del fade, ↔ para recortar) y se ilumina la manija que vas a agarrar.
- **Varios clips por pista**: Alt+X divide en el cabezal y D duplica (`ClipEditing.Split` / `DuplicateMany`; con
  varios clips marcados, aunque estén en varias pistas, los copia en bloque después del final del bloque).
- **Copiar y pegar** (`ClipClipboard`, `MainWindow.Timeline.cs`): Ctrl+C / Ctrl+X toman el clip elegido o los
  marcados; Ctrl+V los pega con el comienzo en el cabezal y la pista de arriba en la elegida (o en la suya), con la
  misma distancia entre pistas; si no entran abajo, el bloque sube. Quedan marcados y, parado, el cabezal salta al final
  del bloque, así otro Ctrl+V pone la copia siguiente. Solo en memoria y, si lleva tomas, solo dentro del mismo
  proyecto. Lleva también clips MIDI (con sus notas), que solo caen en pistas MIDI, como las tomas solo en pistas de
  audio; un bloque solo de clips MIDI se pega en otro proyecto (sin pista elegida, en su primera pista MIDI). Los clips
  de una pista comparten su panel y se ve el de arriba (el último), igual en la preview (`LiveTrack.ShownClip`) y en
  la exportación (`ExportTakeSpec.Panel`).
- **Arrastrar a otra pista** cambia la pista **y el tiempo** a la vez: el original queda atenuado y un fantasma
  punteado marca dónde cae (`DrawGhost`). Con Ctrl se intercambia con el clip de abajo.
- **Grilla e imán** (pedido del usuario): el imán va de Compás a 1/64 (y 1/8T). El clic derecho en los números de la
  regla elige cómo se dibuja la grilla (`TimelineView.GridMode`, `timelineGrid` en config.json): **Auto** (por
  defecto) se afina con el zoom, **Igual que el imán** dibuja sus pasos y una subdivisión fija mantiene ese espaciado
  con cualquier zoom; las líneas a menos de 6 px no se dibujan (`MinGridPx`, `FixedGrid`, `BarSubs`).
- **Mover con grilla** (uno o varios): el clip avanza de a pasos enteros de la grilla y **conserva su desfasaje**: uno
  que no está justo en el tiempo cae igual de corrido un compás (o un tiempo) más allá, como se tocó
  (`ClipEditing.Drag`). **Imán a otros clips**: el selector al lado del imán (`SnapClipsBox`: No / Inicio / Final /
  Ambos, `snapToClips` en config.json, No de entrada) elige qué borde visible del clip que movés se pega a los bordes
  visibles de los otros, a menos de 8 px (`EdgePullPx`, `SnapEdges`). Con los dos imanes la grilla ubica el clip y el borde
  solo lo corrige si el clip ya ubicado queda a menos de 8 px: nunca salta más que eso. Mientras está pegado, una línea de acento
  cruza las pistas en ese borde (`stuckTo`, la guía). Anda también con la grilla en "Sin imán". Nada más tira: ni el cabezal ni el cero. Con los dos apagados, o con Alt, se mueve libre, de a muestra
  (`ClipEditing.ToSample`: el milisegundo eran 96 px al zoom máximo). Para volver a ponerlo en sincro, el clic derecho
  tiene **"Alinear a la grilla"**: lleva el comienzo de la toma a la línea más cercana con la subdivisión del imán
  (`ClipEditing.AlignToGrid`; deshabilitada con el imán apagado). Sobre un clip marcado, alinea o quita todos los marcados.
- **Detectar tempo** (clic derecho en un clip de audio, pedido del usuario): escucha lo que suena del clip (lo recortado
  no cuenta) en segundo plano con `TempoDetector` (el clip muestra la barra y "Escuchando el tempo… N%", como al
  transponer; de a un clip por vez) y abre un menú en el mismo lugar con los BPM, qué tan seguro está y
  "Poner el proyecto a N BPM", al doble o a la mitad (es el mismo pulso: no distingue 87 de 174) o dejarlo. El tempo
  no tiene deshacer, así que el toast que lo confirma trae "Volver a N" (`MainWindow.TempoDetect.cs`). Menos de 3 s,
  silencio o sin pulso claro: un toast lo dice.
- **Extraer MIDI** (clic derecho en un clip de audio → Acordes / Melodía, pedido del usuario, como Studio Pro): saca las
  notas de lo que suena del clip (lo recortado no cuenta; transpuesto si la pista lo está) con `AudioToMidi` (Basic
  Pitch) en segundo plano, de a un clip por vez (la barra y "Sacando las notas… N%" sobre el clip), y las pone en un
  clip MIDI del mismo tramo en una pista MIDI nueva justo debajo, en su carpeta ("Nombre (MIDI)"). Arrastrar el clip a
  una pista MIDI hace lo mismo en esa pista, con el modo elegido la última vez (`toMidiMode` en `config.json`): el clip
  de audio no se mueve y el fantasma dice "Extraer MIDI a …" donde caerán las notas. Es un paso de deshacer y el toast
  trae "Ver notas" (abre el piano roll). Sin notas claras o sin el modelo, un toast lo dice (`MainWindow.ToMidi.cs`,
  `TimelineView.ExtractMidiRequested`).
- **Selección de varios clips** (pedido del usuario): arrastrar en la parte vacía de las pistas (o debajo de la
  última) dibuja un rectángulo que marca cada clip que toca, tomas y clips MIDI; con Ctrl suma a lo marcado (`boxed`,
  `UpdateBox`). **Ctrl+clic** en un clip lo suma o lo saca (en una toma, al soltar sin mover: Ctrl+arrastrar sigue
  intercambiando con el clip de otra pista; `ToggleBoxed`). Agarrar
  uno marcado por el cuerpo mueve todos el mismo tiempo, cada uno en su pista (`StartCarry`, `DragCarried`, evento `ClipsMoved`: un solo paso para deshacer); sus bordes recortan solo ese. Supr los saca a
  todos. Un clic sin mover, o cualquier selección común, deja uno solo.
- **Rango**: la herramienta Rango, o Shift+arrastrar con Seleccionar, marca un tramo; arrastrando hacia arriba o abajo
  suma las pistas visibles que cruza (`rangeTracks`). Supr o "Borrar el rango" del clic derecho lo saca de sus clips
  (`ClipEditing.DeleteRange`) dejando el hueco; Shift+Supr o "Borrar y cerrar el hueco" además corre lo de después en
  esas pistas (`ClipEditing.DeleteRangeClosing`; las otras pistas y el A–B no se mueven; los marcadores, solo si el rango abarca todas las pistas). "Repetir este
  rango" lo pasa al rango A–B. La selección se va con un clic, Esc o una edición.
- **Herramientas** (`Controls/TimelineView.Tools.cs`, `TimelineView.Tool`): solo cambian qué hace apretar sobre los
  carriles; encabezados, regla, marcadores y menús andan igual con cualquiera. **Dividir** corta el clip en el clic
  (imantado; Alt, libre) y mientras pasás muestra la línea del corte. **Borrar** saca lo que toca un arrastre, en un solo
  paso de deshacer (los clips se atenúan hasta soltar). **Escuchar**, mientras mantenés apretado sobre un clip, toca
  esa pista sola desde ahí (`LiveTrack.ListenSolo`: un solo del motor que no se guarda ni prende la S); al soltar se
  apaga con el fundido y el cabezal vuelve a donde estaba. **Zoom**: clic ×2, Alt+clic ÷2, arrastrar muestra ese tramo.
  **Mano** arrastra la vista. Cada una tiene su puntero (`Controls/ToolCursors.cs`: el mismo ícono que su botón,
  blanco con borde negro; la mano sale del trazo de lucide porque Segoe no tiene). Teclas 1 a 7, en el orden de la caja.
- **Clips que se pisan**: se ve el último de la pista. El clic derecho → "Traer al frente" (`ClipEditing.BringToFront`,
  prendido solo si otro lo tapa) lo pasa al final: arriba en el timeline, en la vista previa y en la exportación. El
  sonido no cambia: los dos suenan.
- **Bloquear** (`MainWindow.Locks.cs`): "Bloquear clip" en el menú del clip y "Bloquear pista" en el de la pista (no
  en las MIDI, por ahora). La regla es una sola, `ClipEditing.IsLocked` (`Clip.Locked` o `Track.Locked`), y la usa
  gclab: lo bloqueado no cambia de lugar, de largo ni de pista, no se divide ni se borra (`Replace`, `Split`,
  `RemoveClip`… lo rechazan); borrar un tramo lo saltea y "cerrar el hueco" deja quieta su pista. El volumen, los
  fades, la curva y el encuadre sí se tocan. En el timeline un clip bloqueado lleva un candadito arriba a la derecha
  (a la izquierda del ícono de audio/video) y no muestra las pestañas de recorte; agarrarlo lo elige pero no lo mueve.
  Una pista bloqueada lleva el candado después del nombre. No se puede armar una pista con tomas bloqueadas, y una toma
  que igual caiga sobre ella va a una pista nueva (`TakeRecording.AddTake`). Cada negativa avisa con un toast que trae
  "Desbloquear" (uno cada 2 s como mucho).
- **Transiciones** (pedido del usuario: simples de usar; después, que también fundan el audio, porque el corte pelado
  era brusco; `MainWindow.Transitions.cs`, `TimelineView.DrawJoins`): donde un clip termina y otro empieza en una pista
  (a menos de 2 cuadros, `ClipTransitions.JoinSec`), un chip cuadrado de 20 px va centrado en la unión a media
  altura de los clips: blanco con una `|` si es un corte, de acento con una `X` (fundido cruzado: un fundido que baja
  cruzado con uno que sube, como las líneas de fundido de los clips) o una `V` (pasar por negro); el moñito de CapCut no
  (pedido del usuario), y de los costados del chip salen dos flechitas de acento (con borde oscuro, a su altura) hasta los bordes
  del tramo que cubre (`DrawJoinSpan`). Queda encima de las
  manijas de los bordes y se lleva sus clics. Clic → menú "Transición": Corte / Fundido cruzado / Pasar por negro (este
  solo si los dos clips tienen imagen), duración con 0,25 · 0,5 · 1 · 2 s y un slider de 0,1 a 3 s (0,5 por defecto), y
  qué pasa con el sonido. También desde el clic derecho del clip ("Transición al entrar / al salir"). Se guarda en el
  clip que entra (`Clip.Transition`, `TransitionSec`) y va centrada en la unión; nunca cambia los fundidos propios del
  clip. **Imagen**: el fundido cruzado usa el video que esconden los recortes; si de un lado no hay, ese lado se queda en
  su último o primer cuadro (el menú lo avisa); pasar por negro no necesita nada escondido. **Sonido** (los dos tipos):
  fundido cruzado en el mismo tramo, con el audio que esconden los recortes (`ClipTransitions.Audio`); dos pedazos de la
  misma toma se funden en línea recta (suman exactamente la toma), tomas distintas a potencia constante; sin audio
  escondido de ningún lado, cruza con un fundido de 10 ms justo en la unión (el menú lo avisa). Dividir deja la
  transición en la pieza de la izquierda.
- **Franja de marcadores** (`Controls/TimelineView.Markers.cs`, `MainWindow.Markers.cs`): 18 px arriba de los números
  de la regla (`MarkerH`, incluida en `RulerH`). Arrastrar en vacío dibuja el **rango A–B** del loop y lo prende; sus
  bordes y su cuerpo se arrastran; acento con el loop prendido, gris apagado, y con el loop prendido sus bordes siguen
  tenues por las pistas. Los **marcadores** son banderitas `Raised` con el nombre en `Dim` y una línea tenue hacia
  abajo: se arrastran, un clic salta, doble clic los renombra en el lugar (`MainWindow.Rename.cs`); doble clic en
  vacío agrega uno (y abre su nombre). Todo se imanta como los clips (Alt = libre). Clic derecho: el menú de la franja
  o del marcador (nombres de la lista `MarkerNames`, "Repetir hasta el siguiente", borrar); los dos terminan con "Copiar capítulos para YouTube" (`CopyChapters`, `ProjectMarkers.Chapters`: "0:00 Inicio" si el primero no está en 0, y avisa si YouTube los ignoraría). Los marcadores van en
  `Project.Markers` y se deshacen; el rango va en `ProjectSettings.LoopStartSec/LoopEndSec`, fuera del deshacer como el
  tempo (`ProjectMarkers`). Atajos: L loop, I / O ponen A / B en el cabezal, K marcador, `,` / `.` saltan.
- **Marcas de duración por red** (`Controls/TimelineView.Social.cs`, `SocialLengths` en `GuitarCam.Audio`, pedido del
  usuario): el clic derecho en la regla, debajo de la grilla, prende o apaga cada una (varias a la vez; ninguna de
  entrada; `socialMarks` en config.json, de la app y no del proyecto, como la grilla). En ese menú (pedido del usuario)
  el clic izquierdo elige y cierra, y el **clic derecho elige y lo deja abierto**, para cambiar varias seguidas
  (`MenuChoice`; vale también para la grilla y para los otros menús de opciones: entrada de la pista, plegar una
  carpeta, mostrar u ocultar un separador). Una línea punteada tenue cruza las pistas en el límite, contado desde 0
  como la exportación, con el nombre de la red ("Instagram Reels · YouTube Shorts · 3:00") en una cajita a la
  izquierda, justo debajo de la regla (una fila por marca, así dos en 3:00 no se tapan). Si el tema, o la toma que se
  está grabando, pasa una, se pone ámbar con "no entra". Las historias se repiten: una línea en cada corte que necesita
  el tema ("Historia 1 · Instagram · Facebook · WhatsApp"; con poco lugar, solo "Historia 1"; sin nombre cuando tampoco
  entra). Los largos cambian cada tanto: se actualizan en `SocialLengths`.
- **Loop**: al cruzar B (o el final del tema, sin rango) vuelve a A con el crossfade de los saltos, sin reiniciar los
  efectos (`PlaybackEngine.SetLoopRange`). Si arrancás después de B, sigue hasta el final y desde ahí vuelve a A.
- **Clic derecho** (`ShowTimelineMenu`): sobre un clip, solo cosas del clip (dividir, duplicar, copiar, cortar, al inicio, corregir
  corte, restablecer, quitar clip); en el encabezado o en un tramo vacío del carril, solo cosas de la pista (panel de
  video, renombrar, carpeta, guardar como plantilla, pegar en el cabezal, quitar pista); en la parte vacía de la columna de encabezados,
  debajo de la última fila, el menú de "+ Nueva pista" (pedido del usuario). Mute y solo no van: están en el encabezado. Los
  menús no tienen tope de alto (sin scroll), salvo que la ventana sea más baja que el menú.
- **Ganancia por clip**: `Clip.GainDb` (slider del inspector) y `Clip.GainCurve`, puntos en dB contados desde el sample
  0 de la toma (`ClipGainCurve`, así siguen pegados a las notas al mover, recortar o dividir). El clip seleccionado
  muestra su línea (blanca, puntos de acento): clic en la línea agrega un punto, se arrastra, doble clic lo saca.
  Reproducción y exportación la evalúan cada 64 muestras con rampa (`LiveClip.Curve` es una copia que el hilo de audio
  lee sin locks: toda edición asigna una lista nueva y llama a `RefreshCurve`).
- **Sin ayuda al pasar el mouse**: el timeline no tiene tooltips (el usuario los sacó: estorbaban y trababan la
  ruedita); lo que hace un clic lo dice el cursor (`TimelineView.UpdateCursor`). No los vuelvas a poner sin preguntar.
- **Ruedita**: sola, arriba y abajo; Shift, zoom horizontal; Ctrl, a los costados; Alt, altura de las pistas (los tres
  modificadores se cambian en Configuración → Atajos, `Shortcuts.Wheel`, guardados en `config.json` "wheel"); sobre el
  paneo de un encabezado, el paneo. Ctrl+rueda hacia arriba va a la derecha (más adelante en la canción). Cada gesto
  (también la ruedita sola) tiene su "Invertir" en esa misma sección (`Shortcuts.WheelSign`, "wheelReverse").

## Encabezados de pista

- **Zoom** (pedido del usuario): al pie de la columna, una barra de `FooterH` = 40 px (`TimelineFooter`) con dos
  sliders grandes (pista de 6 px, perilla de 18): zoom horizontal (escala logarítmica de "ver todo" a `MaxPps` = 2 px por muestra, `TimelineView.Zoom` /
  `SetZoom`, se mantiene el cabezal en su lugar) y altura de pista (40 a 320). Las filas scrollean por encima de la
  barra. Zooms fijos contados en tiempos (`TimelineView.ZoomPresetBeats`: un tiempo, 1 compás, 4, 16 y toda la
  canción), sin tecla de entrada (el usuario les sacó el 1 a 5 para las herramientas): se asignan en Atajos.
- **"+ Nueva pista"** (pedido del usuario): fija al pie de la columna, justo encima de la barra de zoom (`AddRect`,
  `AddH` = 36 px, `DrawAddButton`); las filas pasan por debajo y el scroll deja lugar para ver la última entera.
- **Alto por fila** (pedido del usuario): `Track.HeightScale` (múltiplo del alto general, así el slider sigue
  escalándola) / `Folder.Height` en px para la carpeta abierta y `Folder.HeightScale` (múltiplo, como la pista) para la plegada
  (null = el alto de siempre). Se
  cambia arrastrando el borde de abajo del encabezado (`RowEdgeAt`, gesto "row-height", el borde se ilumina) y un doble
  clic en el borde lo saca; el botón ↺ del pie (`ResetHeightsButton`) iguala todas. Es vista, como plegar: se guarda
  sin deshacer (`SetRowHeight`). Todo lo que dibuja o toca una fila usa su alto (`LaneH(i)`, `RowH`, `FolderRowH`),
  nunca `laneH` directo.
- Pasado el detalle de los picos (`MediaAudio.PeakBin`), la onda sale de las muestras (`SampleWave`): mín/máx por
  columna y, con más de un píxel por muestra, una línea que las une. La regla baja hasta 1 ms y, en compases, parte los
  tiempos en mitades ("2.3").
- **Volumen**: más presencia que los sliders de efecto: barra de 5 px, marca en 100 y una tapa de fader clara.
- La columna se ensancha arrastrando su manija: una sola franja de `GripW` = 8 px adentro del borde derecho, de todo
  el alto, con dos rayitas en el medio; nunca pisa un clip en 0 s. Va de 324 a 640 y se recuerda en `headerWidth`;
  las filas se arman para 264 y el sobrante va al volumen.
- Con carriles más altos aparece debajo una fila de knobs, todos dinámicos (pedido del usuario: nada fijo, ni Vida,
  Comp o Tono): uno por envío. Los envíos son los buses de la lista de la pista (`TimelineView.SendKnobs`, que la ventana arma de nuevo
  solo después de una edición, `SendKnobsOf` y `editStamp`), con el nombre y el ícono del bus; se eligen con clic
  derecho en la pista → "Envíos a buses" (`SendChoices`). Configuración → Vista los muestra u oculta todos
  (`TrackKnobs`, "timeline.hiddenKnobs" en `config.json`) y los de cada pista reparten el ancho (`TimelineView.SetKnobs`,
  `KnobCount`). Transponer se toca en el inspector y en el panel de la tira amplia. El de Tono es bipolar: −12…+12 semitonos en 0…1, se ilumina desde arriba hacia
  el lado que va, muestra "+2" adentro, la ruedita mueve un semitono y su nombre no prende ni apaga nada. También
  en pistas vacías (`TimelineView.EffectRequested`). Son tan grandes como dejan el alto de la pista y el ancho
  de la columna (`FxKnobD`: de `FxKnobMin` = 20 a `FxKnobMax` = 64 px) y se achican con ella; si ni el mínimo entra, la
  fila no se muestra. Debajo de cada uno, ícono `FxIcon` + nombre (solo el ícono si no entra): clic prende/apaga. El
  knob se arrastra en vertical (Shift = fino), con la ruedita o doble clic para restablecer; la cantidad va adentro del
  knob si mide 36 o más, y si no, en lugar del nombre mientras lo tocás.
- Tocar cualquier parte del encabezado (la manija o un control, menos "quitar") selecciona la pista.
- Los botones dibujados del encabezado (armar, M, S y la papelera, también en la fila de carpeta) brillan con el
  mouse encima (`TimelineView.Hot` / `DrawGlow`: acento en M y S, rojo en armar y papelera). La papelera de la pista
  pregunta antes de quitarla (`MainWindow.AskRemoveTrack`).
- Al reordenar con la manija, la pista sigue al puntero por encima de las demás y el resto se desliza a su lugar
  (`shownSlot`, `Slide`).
- **Quitar todos los mute / solo**: M y S al final de la barra de la esquina del timeline (`ClearMutesButton` /
  `ClearSolosButton`; por ellas la columna mide 324 como mínimo y no 264) y en la barra del mixer. Están prendidas si
  alguna pista tiene uno; un clic los saca todos en un paso de deshacer (`ClearAllToggles`).

## Color e ícono de pista (`MainWindow.TrackLook.cs`)

- Cada pista tiene una insignia (`TrackIcons.DrawBadge`, `Controls/TrackBadge`) con su ícono de instrumento (o su
  número si no tiene) teñida con su color (o el acento), la misma en el encabezado, el inspector y el mixer.
  En el encabezado mide 28 con la columna al mínimo y crece con ella (`BadgeGrowth`) hasta 52, lo que entra al lado
  del nombre y el volumen sin tocar las filas de efectos; lo que crece corre a la derecha el nombre y el volumen.
  Los íconos son rellenos: Fluent Emoji High Contrast de base y lo que le falta de MDI, game-icons y Qlementine
  (`tools/make-track-icons.py`, licencias en `Assets/Icons/LICENSES.txt`); los ids viejos van en `TrackIcons.Legacy`.
- Tocarla abre el selector: 24 colores en dos filas, en orden de arco iris (`TrackIcons.Colors`, guardados en hex) y los íconos por categoría; el color
  deja el selector abierto y el ícono lo cierra.
- Con color, los clips de la pista se tiñen (relleno, borde y onda) y la tira del mixer lo lleva arriba. Los colores
  de pista son datos del usuario, no tokens: no se usan para estados de la interfaz.

## Carpetas de pistas (`MainWindow.Folders.cs`)

- En el timeline, una fila propia (manija, flecha para plegar, ícono y nombre, y papelera: la carpeta solo agrupa, no
  tiene M, S ni fader) de `FolderH` = 64 px abierta,
  con el carril vacío (sus pistas ya se ven abajo); plegada toma la altura del zoom vertical y el carril es un **mini
  mapa** (`DrawMiniMap`): una franja por pista con sus clips en el color de la pista y la onda si la franja llega a
  `MiniWaveAt` px. Si se le cambió la altura a mano (`Folder.Height`), manda esa. Sus pistas con el contenido del encabezado corrido `FolderIndent` = 24 px
  (manija, insignia, nombre y sliders). La carpeta y sus pistas van dentro de un recuadro fino de su color
  (`DrawFolderFrames`, encabezados y carriles); sin riel ni tinte en cada pista, que parecían seleccionadas.
- Una carpeta se selecciona como una pista (clic en su fila o en su canal del mixer; `TimelineView.SelectedFolderId`,
  y entonces no hay pista seleccionada): el inspector muestra la carpeta (`InitFolderInspector`: nombre, color,
  su salida y un slider de envío por bus, `RebuildFolderRouting`, nueva pista adentro y quitarla) y Supr actúa sobre ella. Su ícono abre el selector de color
  (`ShowFolderLook`, los mismos `ColorChips` que el de pista). Elegido un color, abajo pregunta si pinta también
  sus pistas con ese color (`FolderTracksAsk`, un solo paso de deshacer); solo aparece si alguna tiene otro.
- **Renombrar en el lugar** (`MainWindow.Rename.cs`, pedido del usuario): doble clic sobre el texto del nombre de una
  pista o una carpeta, no en el espacio vacío de al lado (`TitleRect` / `FolderTitleRect`), o "Renombrar…" en su menú, pone una caja de texto encima (`TimelineView.NameEditRequested`, `EditingNameId`).
  Enter o un clic afuera lo guarda, Esc lo descarta, la rueda lo guarda. Un paso de deshacer.
- Las filas se arman en píxeles (`TimelineView.Relayout`). Las pistas se arrastran por su manija adentro, afuera o
  entre carpetas (`FindDrop`: el hueco más cercano al borde de arriba de la fila arrastrada, con 8 px de "pegue" para
  que no titile; en el borde de abajo de una carpeta, si ese borde de la fila queda arriba entra al final, si queda
  abajo sale; sobre una carpeta plegada entra última); el resto se corre para hacer lugar, la carpeta destino se marca con su color y una etiqueta dice
  "Entra en…" / "Sale de…". Una carpeta arrastrada viaja plegada.
- **Varias pistas juntas** (pedido del usuario): Ctrl+clic en un encabezado suma o saca esa pista, Shift+clic marca
  el rango desde la última clickeada (`TimelineView.picked`, `Pick`); la seleccionada (la del inspector) es la última
  tocada. Arrastrar la manija de una de ellas las lleva a todas apiladas en su orden (`dragIds`, `BlockTop`) y caen
  juntas (`TrackFolders.Move` con varias); el menú "Carpeta" del clic derecho sobre una de ellas también actúa sobre
  todas. Un clic común vuelve a una sola.
- **Quitar una carpeta** con pistas adentro pregunta (`MainWindow.RemoveFolder`): "Solo la carpeta" (las pistas
  quedan afuera) o "La carpeta y sus pistas" (`TrackFolders.Remove(…, withTracks)`); vacía se quita sin preguntar.
- **Separadores** (`Folder.Separator`, pedido del usuario): una fila de `SeparatorH` = 22 px con la manija, el nombre en su
  color y una línea a lo ancho (`DrawSeparatorHeader`); se arrastran como una carpeta (nunca entran pistas), doble clic
  renombra y el clic derecho abre su menú (`SeparatorMenu`: renombrar, color, mostrar u ocultar en el timeline,
  quitar; elegir un color cierra el selector). Oculto, no ocupa lugar pero conserva su lugar en el orden. Uno nuevo nace
  oculto en el timeline (pedido del usuario: son sobre todo para el mixer), salvo "Separador arriba" del menú de una
  pista, que lo agrega ahí y visible (`NewSeparatorAbove`).
- Doble clic en cualquier otro lugar libre de la fila de una carpeta (encabezado o carril) la pliega o despliega
  (pedido del usuario). Plegar es vista (sin deshacer); el resto pasa por `FolderEdit` o `EditTimeline`.
- En el mixer, cada carpeta es un grupo teñido con su nombre arriba (una franja de `FolderBandH` en todas las
  columnas, para que los faders queden parejos), un canal de carpeta primero y sus pistas, plegable aparte
  (`Folder.MixerCollapsed`).
- **La carpeta solo contiene** (pedido del usuario): no tiene fader, paneo, M, S ni efectos. Sus pistas suman en su nodo
  (un `BusDspChain` sin slots, que deja la suma intacta) y van a su **Salida** (Master o un bus); desde esa suma tiene
  sus **envíos** a buses (sin PRE: no hay fader). Su tira del mixer (`FolderStrip`, del ancho de la tira amplia más
  angosta, `FolderStripW`) lleva ícono y "CARPETA", el medidor de la suma ("Suma" abajo) y, amplia, la fila SALIDA y un
  panel de envío por bus (`SendPanels` con su `Router`); compacta, sus envíos como barras igual que las pistas (`CompactSends`, a la altura de los de
  ellas) y el selector de salida donde ellas tienen el paneo y M S ● (`CompactFolderControls`). Cada edición es un
  `FolderEdit` con su deshacer. Para procesar un grupo, se manda la carpeta a un bus y los efectos van en el bus.

## Plantillas de pista (`TrackPresets`, `%APPDATA%\GuitarCam Studio\pistas.json`)

Clic derecho en una pista → "Guardar como plantilla" guarda su canal (sin mute ni solo), color, ícono, entrada y si
va en el video; "Nueva pista → Plantilla" crea una pista con eso. En el menú de nueva pista las explicaciones son
tooltips con 1 s de demora (`MenuTipDelay`), no texto al costado. "Usar plantilla" (en el mismo menú de la pista de
audio, `ApplyPresetSubmenu`) le pone a una pista que ya existe el sonido de una plantilla (`TrackPresets.ApplyTo`):
slots con ids nuevos, volumen, paneo y envíos a los buses que el proyecto tiene; tomas, nombre, color, ícono, entrada,
salida, mute/solo y Transponer quedan. Un paso de deshacer; los compresores se vuelven a medir con sus tomas.

## Armar y grabar (`MainWindow.Recording.cs`)

- El ● de cada encabezado de pista y de cada tira del mixer (o R, sobre la pista seleccionada) elige dónde graba
  Shift+R (`armedIds`: null = la que elige `TakeRecording.PickTarget` a partir de la seleccionada, vacía = ninguna). Sin pista armada, Grabar queda
  deshabilitado, en video y en solo audio. Una pista MIDI armada graba el teclado MIDI (ver "Pistas MIDI y piano
  roll"): `RecordTargets` son las de audio y `MidiRecordTargets` las MIDI.
- Se pueden armar varias y se graban a la vez: una toma y un WAV por pista desde el mismo sample. Las pistas destino
  siempre se tiñen de rojo (`TimelineView.ArmedIndices`).
- Se graba **desde el cabezal**: la cuenta lleva hasta ahí, la toma cae ahí (`RecordingJournal.StartSec`) y, en una
  pista con clips, reemplaza solo el tramo que cubre (desde 0, toda la pista). Los clips reemplazados quedan en
  Historial (`TakeRecording.AddTake`) y la pista no suena mientras grabás (`LiveTrack.RecordMuted`).
- Cada pista armada muestra su entrada (`Track.Input`, "3" o "3-4"; null = 1-2 de Configuración) en un chip que abre
  el menú de entradas. El motor abre todas las entradas del driver y tiene un `InputTap` por pista armada (su
  `InputProcessor`, medidor, monitoreo por software sin repetir entradas, y su `TakeRecorder`). El video de la
  cámara va a la primera pista armada con `Video`.

## Mixer (`MainWindow.Mixer.cs`)

- Ocupa el lugar del timeline (X o "Timeline / Mixer") con su propia altura (mínimo 250). Una tira por pista, también
  las vacías; amplias o compactas ("Auto" según el ancho).
- Arriba, sobre el mixer y sobre Mastering (no en solo audio), la mini timeline (`Controls/MiniTimeline.cs`): toda la
  canción, el cabezal (clic o arrastre para moverlo) y una regla en compases o en minutos y segundos, con su propio
  switch de dos botones a la izquierda que es el mismo modo que el de la regla del timeline (`SetRulerMode`).
- Las ediciones de canal pasan por `EditTrack`, las de las carpetas por `FolderEdit` y las de los buses por `BusEdit`
  (`BusesEdit` para agregar y quitar). Los desplegables son `SelectButton`, no `ComboBox`.
- **A | B** (`MainWindow.MixCompare.cs`, `MixCompare`): segmentado en la barra del mixer, entre los M/S de limpiar y el
  ancho de tiras. La mezcla que suena es la del proyecto; la otra espera en `ProjectSettings.Compare` (solo ajustes:
  el canal de cada pista, salida y envíos de las carpetas, y volumen, paneo, slots, salida y envíos de los buses, por
  id). El primer clic en la otra letra la arma como copia de esta. Nunca agrega ni saca pistas, carpetas ni buses, y
  no toca mute, solo, nombres, lados ni el master (Mastering es su propia etapa). Se aplica como Deshacer aplica un
  canal (`ApplyChannel`, `ApplyRouting`), sin recargar nada y con los mismos ids de slot, así los efectos no pierden su
  estado; cada cambio es un paso de deshacer (`UndoManager` guarda `Compare`). Clic derecho: "Copiar A en B" y
  "Olvidar la mezcla B".
- Sus medidores usan el degradé verde → amarillo → rojo (`Palette.Meter*`), distinto del `StereoMeter`.
- **Medidores completos** (`Controls/MixerMeter.cs`, en todas las tiras: pistas, carpetas, buses y master, compactas
  y amplias): dos barras, izquierda y derecha (5 px cada una, 7 en el master, con 1 px entre medio); una pista mono
  (todas sus tomas con L y R iguales, `MediaAudio.Mono`, o sin tomas y con una entrada sola o la de Configuración en
  modo mono) muestra una sola barra del ancho del par con el lado más fuerte, así las tiras no cambian de ancho. En
  cada barra, la parte llena es el RMS (~300 ms, `RmsTap`, por lado) y el tramo tenue con su rayita arriba es el pico. Encima de
  las barras, dentro de la misma fila del fader (las filas fijas de `StripLayout` no crecen), van el pico máximo desde el
  último reinicio en dBFS (el mayor de los dos lados; mono 8, `-∞` en silencio, en rojo si hubo clip) y una luz de clip
  por barra, que se prende en rojo arriba de −0,3 dB y queda prendida. Un clic en cualquier parte del medidor reinicia los dos. En las compactas el
  número entra justo (5 caracteres en 27 px). La carpeta muestra la suma de sus pistas y el bus su salida (`MixerLayout.NodeMeters`,
  leídos de `engine.Node`). El pie del
  master muestra los LUFS de corto plazo de la salida (la misma medición que la celda LUFS del transporte,
  `engine.Loudness`), en el lugar donde decía "Salida". Al lado de LIM, **MONO** (`engine.MonoCheck`): pliega la salida
  a mono con 10 ms de fundido, solo lo que se oye (medidores, LUFS y exportación siguen en estéreo); no se guarda.
  Bajo los LUFS, **COR** (`engine.Correlation`, `ShowMasterCorrelation`): la correlación de la salida en décimas, en
  rojo bajo cero y en amarillo bajo +0,3. Cabecera y pie tienen alto fijo: LIM y MONO van en una fila, LUFS y COR en dos líneas cortas.
- Cada canal es una tarjeta (`StripCard`) y todos (pistas, carpetas, buses, master) se arman con `StripLayout`, que les da
  las mismas filas: así faders y medidores tienen siempre la misma altura. Lo que vaya en la cabecera o en los
  controles tiene que entrar en su fila fija, no estirarla. Los paneles de efecto angostos se reacomodan
  (`FxNarrowW`) en vez de recortar controles.
- En las tiras amplias la cabecera (● M S) y el paneo quedan fijos y solo scrollean los efectos, cada uno en su
  panel. La **tira compacta** va como la de una consola (pedido del usuario, `CompactChain`): la lista de EFECTOS
  (un renglón por slot con su ⏻ de encendido; un clic abre sus controles en la ventanita, arrastrado cambia el
  orden, y el + suma uno, `ShowAddSlotMenu`; una rayita bajo el primer compresor muestra cuánto comprime), la de
  ENVÍOS (una barra por bus de su lista con su nombre, su % y su ⏻, `HBar`; el + elige los buses), el paneo como
  barra desde el centro (`HBar` bipolar) y ● M S. Efectos y envíos son secciones de alto fijo, el mismo en todas
  las tiras (pedido del usuario: todo parejo); una manija abajo de cada una (`ListBox`) las estira en todas a la vez
  (`SetListH`, "mixer.fxH" / "mixer.sendsH" en `config.json`; doble clic vuelve al de siempre). `StripTopH` sale de
  las secciones que se ven y su alto, y `ApplyMixerHeights` solo cambia las alturas de las filas registradas
  (`MixerLayout.TopRows`, `FxRows`, `SendRows`), sin rearmar nada. `CompactW` 104, mixer de 440 por defecto.
- La cabecera del mixer tiene el título con presencia (ícono de mixer en acento y "Mixer") y **Efectos** y **Envíos**
  (`SectionSwitch`, pedido del usuario): muestran u ocultan esas secciones en todas las tiras, compactas y amplias
  (`mixerFx`, `mixerSends`, guardados en `config.json` "mixer.fx" / "mixer.sends").
- El **fader** (`Controls/Fader.cs`) tiene la tapa de una consola (pedido del usuario: grande y fácil de agarrar, 30 × 40):
  cuerpo gris sombreado, ranuras, una línea blanca en el nivel y una sombra abajo; la ranura oscura se ilumina en
  acento hasta el nivel. La escala en dB va en el medidor.
- **Slots de efectos** (tira amplia, `WideFx` → `SlotPanels`, que recibe un `SlotOwner`: el canal de una pista o un bus): un panel por slot en el orden en que suena (`SatPanel`,
  `CompPanel`, `EqPanel`, `GatePanel`; un tipo repetido se numera, "EQ 2"). El ⋯ del título (`SlotMenuButton`) sube, baja, duplica o
  saca el slot; también se arrastra de su barra de título (`Controls/SlotDrag.cs`, animado como el reordenar pistas
  del timeline: los demás se corren para hacerle lugar, al soltar se acomoda y recién ahí cambia el orden). "+ Efecto" (`AddSlotButton`) agrega uno al final, ya prendido. El gráfico de cada EQ abre sus bandas en
  un flyout (`ShowSlotEq`). Cada slot se **pliega** (pedido del usuario, `InsertSlot.Collapsed`, guardado en el proyecto
  pero sin paso de deshacer): un clic en su barra o en la flechita lo deja en una fila de 26 px con un resumen de lo
  que tiene puesto ("Válvula · 30%", "Sala · 2,1 s", "1/8 · 35%") y un botón que abre el panel entero en una ventanita
  sobre el mixer, sin desplegarlo (`ShowSlotWindow`: el mismo panel, armado en un `MixerLayout` aparte y refrescado
  con el mixer mientras está abierto). La barra "Comprime" va en el primer compresor y muestra lo de toda la cadena. La tira
  compacta y las perillas del timeline son un atajo al primer slot de cada tipo; si no hay ninguno, prenderlo ahí lo
  vuelve a poner en su lugar de siempre (`Channel.AdoptSpares`). Cada cambio de la lista es un paso de deshacer
  y reconstruye las tiras (la clave de `RefreshMixer` lleva los ids de los slots).
- **Mastering** (`MainWindow.Master.cs`): la tercera vista del selector Timeline · Mixer · Mastering (la X sigue
  alternando timeline / mixer). Usa el lugar del mixer (`MixerHost`, con su altura y su lugar en solo audio): esconde
  la cabecera y las tiras y muestra la cadena. El botón "Master" del canal master (en acento con el master prendido)
  también lleva ahí. Dos grupos, de izquierda a derecha (pedido del usuario): una **caja** con el ON del master, qué
  está haciendo y las etapas que ese ON prende (se atenúan enteras con el master apagado), y al lado, en otra caja igual (mismo encabezado y paneles del mismo alto), **Salida**
  (Limitador → Volumen), que funciona siempre. Un panel por etapa con su ON y flechas entre ellos, perillas de 44 px
  (pedido del usuario): **Look** (sin ON: un `SelectButton` con A mano · Natural · Pegadito · Brillante · Cálido · Al
  frente · Domar la púa, `MasterLooks`; la perilla Cuánto, de Natural al look entero, que reescribe las perillas; y
  **Sugerime**, `MainWindow.Suggest.cs`, que escucha la mezcla entera antes del master en segundo plano,
  `MasterSuggest`, y abre una tarjeta con una línea por propuesta y Aplicar / Cancelar; tocar a mano una perilla que el
  look puso lo deja en "A mano") → **Limpieza** (Corte, Barro;
  `EqOn`) → **Multibanda** (una perilla "Cuánto" por banda con su barra de reducción en vivo, y abajo un gráfico chico,
  `Controls/MasterBandsGraph.cs`, con la curva de cada banda que baja con lo que comprime y los dos cruces como líneas
  que se arrastran; y una cuarta columna, **Púa**, `MasterPickTamer`, que baja 2–5 kHz solo cuando pega la púa;
  `BandsOn`) → **Color** (Graves, Presencia, Aire, Inclinación; `ColorOn`) → **Cinta** (Vida, la
  cinta de las pistas más suave; `TapeOn`) → **Ancho** (Ancho y Graves mono, con el volumen compensado; `WidthOn`) → **Soft clip** (Recorte y su
  barra; en 0 dice "Auto": con picos más de 2 dB sobre el Techo se lleva la mitad de lo que pasa, hasta 4 dB, y el
  resto queda para el limitador; `ClipOn`) | Salida: **Limitador** (Empuje, Techo en pico real, −1 dBTP por defecto,
  Release y su barra; `LimOn`, aparte del ON del master; apagado, los picos pueden pasar de 0 dB) → **Volumen** (sin
  ON: el **Destino** arriba, un `SelectButton` con Como está · YouTube · Instagram · TikTok · Spotify · Apple Music
  (`MasterLoudness`, `MasterSettings.Target`) y abajo qué está haciendo; LUFS de los últimos 3 s e integrado, el
  mismo medidor que la celda LUFS, el pico real de la pasada (`TruePeak`) en rojo si pasó del Techo, y cuánto falta
  para los LUFS del destino, o −14 sin destino).
- **Destino** (`MainWindow.Loudness.cs`): con un destino, cada edición (`MarkDirty`) espera 1,5 s y mide la mezcla
  entera en segundo plano con el mismo `MixMaster.Render` de la exportación; la ganancia que encuentra va a
  `MasterSettings.TargetGainDb` (se guarda) y la reproducción se desliza hasta ella (50 ms), así que se escucha al
  volumen de esa red mientras mezclás y la exportación arranca de esa ganancia y cae en la misma. Ocupa el lugar del
  Empuje (la perilla se apaga y muestra la ganancia). No mide mientras grabás ni mientras hay audio cargándose o
  transponiéndose (no lee disco en la UI); una edición nueva cancela la medición en curso.
- La vista tiene justo el alto de la cadena (`FitMasteringHeight`) y no se estira: los divisores no hacen nada ahí y
  la altura recordada del mixer no cambia.
- No se mide nada: cada banda sigue su propio nivel mientras suena (`MasterBandComp`) y comprime lo que pasa de ahí,
  así una perilla se escucha en el momento, con cualquier mezcla y a cualquier volumen. Sin limitador, la exportación
  con destino de LUFS frena la ganancia donde el pico real toca el Techo.
- **Lados y orden** (`MainWindow.MixerOrder.cs`, pedido del usuario): el mixer tiene dos lados, izquierdo (`MixerOrder.Left`,
  columna 0) y derecho (`Right`, columna 2, antes del master), con el espacio libre en el medio. Por defecto pistas y
  carpetas van a la izquierda y los buses a la derecha (`MixerSides`); cualquier tira se arrastra desde su tarjeta
  (`MixerDraggable`: un clic sigue siendo un clic, esté seleccionada o no, y el arrastre arranca a los 6 px) a otro lugar,
  a una carpeta abierta o al otro lado, deslizándose como todo lo que se reordena (`SlotDrag.SlideEase`, pedido del
  usuario): la tira sigue al mouse, las demás se corren para hacerle lugar (a la derecha en el lado izquierdo, a la
  izquierda en el derecho, que está pegado al master) y al soltar se acomoda antes de cambiar el orden (`MixerDropAt`,
  `MakeMixerRoom`, `FinishMixerDrag`); el mixer scrollea cerca de los bordes. Apretar en el fondo vacío del mixer
  deselecciona todo. Pistas, carpetas y separadores se mueven en el mismo orden del timeline (`TrackFolders`, un
  `EditTimeline` con su deshacer), así que el timeline lo sigue y al revés; los buses se ordenan entre los buses de
  cualquiera de los dos lados. Los **separadores** se ven como una tira angosta (`SeparatorStrip`, `SeparatorW`) con su
  nombre escrito hacia arriba y un ojo arriba que los muestra u oculta en el timeline; un clic abre su menú. "Separador" en
  la cabecera del mixer agrega uno al final (`AddSeparatorButton`).
- **Paneo**: un solo desplegable (`BuildPanMenu`) con la apertura arriba (30 % por defecto) y las plantillas con su
  descripción; elegir una la aplica y cierra el menú.
- **Buses** (`MainWindow.Buses.cs`, `ProjectSettings.Buses`, pedido del usuario): un solo concepto para bus y FX. A la
  derecha, una tira por bus (`BusStrip`), armada como un rack (`BusRack`, pedido del usuario: un bus es sobre todo su
  cadena): arriba ícono y nombre (doble clic o ⋯ → Renombrar; ⋯ → Quitar bus, sin preguntar y en un paso de
  deshacer: se van los envíos que le llegaban y lo que salía a él pasa al Master) y M; a la derecha, fijos, fader,
  medidor y dB, a la altura de los de las pistas; a la izquierda, scrolleando, sus slots (plegables) con los
  mismos paneles, ⋯, arrastre y "+ Efecto" que una pista (`SlotPanels` con un
  `SlotOwner` de bus, donde "+ Efecto" también ofrece Reverb y Delay). Sin SALIDA ni envíos: por ahora un bus sale
  solo al Master (decisión del usuario, `ProjectBuses.CanFeed`). El paneo va
  arriba de la cadena en el mixer amplio y arriba del fader en el compacto. No hay columna "+ Bus" (pedido del usuario): clic derecho en el mixer, también en su fondo vacío (`InitMixerMenu`) → "Nuevo bus / canal FX"
  agrega uno vacío (`ProjectBuses.Add`), y el efecto se le suma con "+ Efecto"; el mismo menú tiene "Nuevo separador". El
  ⋯ de la tira lleva su color propio, así se ve sin pasar el mouse. Los paneles de reverb y delay
  en slot (`ReverbSlotPanel`, `DelaySlotPanel`) tienen sus presets y perillas; un slot apagado deja pasar lo que le
  llega. Los selectores de salida y los paneles de envío de pistas y carpetas ofrecen los buses (`Targets`,
  `ProjectBuses.CanFeed`), y la clave de `RefreshMixer` lleva el ruteo de los buses para rearmar las tiras cuando cambia.
- **Salida y envíos en las tiras** (`Router`: pista, carpeta o bus, con `OutputSelect`, `OutputRow`, `SendPanels`): la
  tira amplia de una pista suma, después de sus slots, la fila SALIDA (si no está en una carpeta) y un panel de envío
  por bus de su lista (knob y %, el nombre del bus, qué hace, ON, **PRE** y × para sacarlo) con "+ Envío" debajo. Cada
  pista elige sus buses (`Send.Hidden`, `ProjectBuses.Shown` / `ShowSend`): sacar uno lo apaga y guarda su nivel;
  una pista o un bus nuevos traen todos. La clave de `RefreshMixer` lleva los envíos sacados para rearmar las tiras.

## Pistas MIDI y piano roll (`MainWindow.Midi.cs`, `Controls/PianoRollView.cs`, `Controls/TimelineView.Midi.cs`)

Etapa 1 del editor MIDI (pedido del usuario: armar bases "al más puro estilo FL Studio", con clips por pista como
Logic o Studio One). Etapas siguientes en PENDIENTES: sampler, después VST3.

- **Pista MIDI** (`Track.Kind = "midi"`, "+ Nueva pista" → Pista MIDI, ícono de piano): nunca lleva panel de video.
  Tomas y clips de audio no entran en ella. Su ● la arma para grabar el teclado MIDI (ver "Teclado MIDI" abajo).
  Tiene su tira en el mixer como cualquier pista (fader, paneo, slots, envíos) y su Transponer sube o baja las notas.
- **Clips MIDI en el timeline**: caja con el color de la pista y sus notas en chiquito (entre su nota más grave y la
  más aguda, con una octava como mínimo). Clic elige, arrastrar el cuerpo lo mueve (en el tiempo y a otra pista MIDI,
  con el imán del timeline; Alt, libre), el borde derecho cambia el largo de a pasos de la grilla, doble clic lo abre
  en el piano roll y doble clic en un tramo vacío del carril crea uno de 4 compases ahí y lo abre. Supr lo quita, D lo
  duplica justo después; clic derecho: abrir, duplicar, copiar, cortar, quitar. Entran en la selección de varios (Ctrl+clic y
  rectángulo, mezclados con tomas): arrastrar uno marcado por el cuerpo los mueve a todos el mismo tiempo, cada uno en
  su pista (`ClipsMoved`, un solo paso de deshacer), y Supr, D y el clic derecho actúan sobre todos
  (`MidiEditing.DuplicateMany`: el bloque entero va justo después de sí mismo), también Ctrl+C / Ctrl+X / Ctrl+V
  (`ClipClipboard.MidiItems`, pedido del usuario). Las herramientas que no son Seleccionar y el rango todavía no los tocan.
- **Tiempos**: el clip empieza en segundos (`AtSec`) como todos, pero su largo y sus notas van en tiempos
  (`LengthBeats`, `MidiNote.Beat/Beats`), así siguen al tempo. Una nota que pasa el final del clip se corta ahí.
- **Piano roll**: una vista más en el lugar del mixer (botón "Piano roll" del transporte, justo después de
  Timeline, pedido del usuario; o doble clic en un clip),
  como Mastering: misma altura y lugar que el mixer, con la mini timeline arriba en Clásico. Arriba, el nombre de la
  pista con el largo y la cantidad de notas, el **imán** propio (1/4 · 1/8 · 1/16 · 1/32 · 1/8T · Libre, `pianoSnap`
  en config.json, 1/16 de entrada), la **grilla** ("Grilla: …", también con clic derecho en la regla; pedido del
  usuario: imán y grilla juntos o cada uno por su lado, como en el timeline): **Igual que el imán** (de entrada; con el
  imán en Libre, Auto), **Auto** (se afina con el zoom: el paso más fino con 14 px entre líneas) o una subdivisión fija
  (Compás a 1/64, 1/8T), guardada en `pianoGrid`; las líneas a menos de 6 px no se dibujan (`PianoRollView.GridMode`).
  Y un ícono de ayuda con los gestos. **Muestra la pista entera** (pedido del usuario): la regla lleva los compases del
  timeline (clic mueve el cabezal) y cada clip de la pista como una franja del color de la pista (el que se edita,
  más fuerte); en la grilla, una línea en el principio y el final de cada clip, fuera del clip que se edita todo más
  oscuro y las notas de los otros clips tenues (sus velocidades también, en la franja VEL). Un clic adentro de otro
  clip lo pasa a editar y sigue como un clic ahí (`ClipPicked`); fuera de todo clip no se escriben notas. Grilla e imán
  cuentan desde el principio del timeline, como los del timeline. **Sigue al timeline** mientras está a la vista
  (`FollowPiano`): clic en un clip MIDI lo muestra (en la misma pista, solo lo trae a la vista); clic en otra pista MIDI
  (su encabezado o un tramo vacío) muestra esa pista entera, en el clip bajo el cabezal (si no hay, el siguiente o el
  último). "Pista entera" (`PianoRollView.Frame.Track`) encaja todos sus clips a lo ancho si un compás queda de al
  menos 48 px; si no, deja el cabezal en el medio. Las notas van con las puntas redondeadas y una rayita abajo que se
  llena según su velocidad (pedidos del usuario). Teclado a la izquierda (sus teclas suenan; las
  filas de las negras más oscuras, una línea en cada Do), el cabezal de acento y, abajo, la franja **VEL** con una barra por nota
  (se arrastra para cambiarla; si la nota está elegida, cambian con ella todas las elegidas que empiezan en esa vertical,
  un acorde de una vez). La franja se agranda o achica desde la barrita que la separa de la grilla (40 px como mínimo,
  la grilla conserva 60; doble clic vuelve a 64; `pianoVelHeight` en config.json), pedidos del usuario. A la izquierda
  lleva su escala (127, 100, 64, 32, las que entran) y, mientras arrastrás, el valor al lado del puntero. En la cabecera
  (pedidos del usuario): "Velocidad" la muestra u oculta (`pianoVelShown`), Lápiz / Línea (`pianoVelTool`: el lápiz
  pinta cada barra que cruza, la línea deja una rampa recta; las dos solo tocan las elegidas si hay) y Paso
  (`pianoVelStep`: una caja para cualquier valor y ▾ con los comunes; los valores van a múltiplos o a 127, Alt libre).
  El clic derecho en la franja pinta 100 (`MidiNote.DefaultVelocity`). "Ajustar" abre una tarjeta con Escalar (% alrededor
  del promedio: menos de 100 empareja, más da dinámica) y Humanizar (± al azar), cada uno con su Aplicar (un paso de
  deshacer, sobre las elegidas o todo el clip; `MidiEditing.ScaleVelocity` / `HumanizeVelocity`, valores en
  `pianoVelScale` / `pianoVelHumanize`). Gestos como FL: clic en vacío escribe una nota con el largo y la velocidad de la
  última tocada (y se arrastra), arrastrar una nota la mueve (con las elegidas; Alt, libre), su borde derecho la
  alarga, Shift+arrastrar una nota copia las elegidas y las suelta donde soltás (con el imán), clic derecho borra
  (arrastrando, varias; en un lugar vacío también deselecciona), Ctrl+arrastrar en vacío elige con un rectángulo,
  Ctrl+clic suma o saca. La regla mueve el cabezal; sostenido, hace scrub en vivo como la del timeline (pausa mientras
  arrastrás, `ScrubChanged`, y corre la vista en los bordes). Teclas con el foco en el piano roll: Supr borra las
  elegidas (nunca el clip), Ctrl+A todas, ↑ ↓ (o Shift+↑ ↓) un semitono, Ctrl+↑ ↓ una octava, Shift+← → un paso
  del imán (sin salir del clip), Esc, y los atajos copiar / cortar /
  pegar / duplicar del timeline con las notas (pedidos del usuario): Ctrl+V pega en el cabezal si está dentro del clip
  (con el imán) o donde estaban, D duplica justo después de las elegidas (en pasos del imán); todo queda dentro del
  clip que se edita (lo que empezaría después de su final no se copia). El portapapeles de notas sirve entre clips. La ruedita con los mismos modificadores que el timeline (zoom, costados, alto de las filas).
  Cada gesto que cambia algo es un paso de deshacer (`NotesCommitted` → `MidiEditing.SetNotes` por `EditTimeline`).
- **Instrumento VST3** (`MainWindow.Vst.cs`, `Track.Instrument`, `PluginRef`): clic derecho en la pista →
  "Instrumento: …", y lo mismo en la cabecera del piano roll: un chip con el nombre (▾ abre la lista; "(cargando…)"
  mientras abre) y un botón que abre la ventana del plugin. La lista (`InstrumentItems`, pedido del usuario) va en dos
  grupos con título (`MenuGroup`): NATIVOS (el tono de prueba) y VST3 (cada instrumento con su marca; desde 12,
  `ByVendorFrom`, un submenú por marca, con el tilde en la del que se usa), y abajo "Buscar plugins de nuevo",
  "Administrar plugins…" y "Abrir la ventana de …".
- **Inspector de una pista MIDI** (`InitMidiInspector`, `RefreshMidiInspector`): INSTRUMENTO, una tarjeta con un
  desplegable (nombre, "Nativo" o "VST3 · marca" y ▾; abre el mismo menú de arriba, nunca una lista a la vista: con
  muchos plugins llenaba el panel), cómo va (listo, abriendo, no abrió) y "Abrir la ventana". Debajo, la fila SALIDA y
  los envíos a buses, las mismas secciones que una pista de audio (`inspectorSends`, que `PlaceInspectorSends` mueve
  acá mientras se ve una pista MIDI).
- **Instrumento o efecto** (`VstPluginInfo.IsInstrument`): la búsqueda (`gcvsthost --scan`) crea cada plugin y le
  pregunta sus entradas: de audio principales (un sidechain no cuenta) y de notas. Toma notas y no audio → instrumento;
  toma audio y no notas → efecto, diga lo que diga su categoría; las dos cosas (un afinador por MIDI, un synth con
  sidechain) o ninguna → su categoría ("Instrument", o sin "Fx" y con notas). Un `plugins.json` de antes
  (`PluginCatalog.CurrentFormat`) se vuelve a abrir una vez en la próxima búsqueda, que la primera lista de
  instrumentos lanza sola (`EnsurePlugins`). La lista sale de `PluginCatalog` (`plugins.json`): la primera
  vez hay que buscarlos ("Buscar plugins VST3…", unos segundos, con toast). Elegir uno es un paso de deshacer; el plugin
  carga en segundo plano (su proceso, `gcvsthost.exe`) y mientras tanto la pista suena con el tono de prueba. Lo que el
  usuario cambia en la ventana del plugin se guarda en el proyecto (su estado, en base64) 1,5 s después de tocarla y al
  cerrarla, fuera del deshacer como el tempo. Si no abre (archivo que falta, error) o se cierra de golpe, un toast lo
  dice (con "Abrirlo de nuevo") y la pista vuelve al tono de prueba; no se reintenta solo. Con la transporte parada el
  instrumento sigue corriendo: las teclas del piano roll suenan a través de él y su cadena.
- **Configuración → Plugins** (`Dialogs/SettingsDialog.Plugins.cs`, pedido del usuario): las carpetas donde se buscan
  (las dos de Windows y las que suma el usuario con +, "vst.folders" en `config.json`), "Buscar nuevos" (solo los
  nuevos o actualizados) y "Revisar todos de nuevo" con el avance ("Abriendo X… (3 de 20)") y Cancelar (para entre un
  archivo y otro, lo encontrado queda); nunca mientras se graba. La lista con filtro (Todos / Instrumentos / Efectos) y
  búsqueda por nombre o marca: destildar uno lo saca de los menús ("vst.hidden"; la pista que ya lo usa lo conserva). Los
  que no abrieron, con el motivo y "Probar de nuevo" (`PluginCatalog.Rescan`). La lista y la búsqueda en curso son una
  sola para la ventana (`PluginScan`): el menú de la pista, el inspector MIDI y esta página se actualizan con
  `PluginScan.Changed`. El menú del instrumento lleva "Administrar plugins…" y el toast de la búsqueda, "Administrar".
- **Sonido**: sin instrumento, un tono de prueba (`NoteSynth`, sin estado: igual en la reproducción, la
  exportación y gclab). Las notas que suenan al tocar el teclado del piano roll o escribir van directo a la salida
  (`PlaybackEngine.PreviewNote`), sin la cadena de la pista.
- **Teclado MIDI** (`MainWindow.MidiInput.cs`, etapa 4, decidido con el usuario): suena en la **pista MIDI elegida**
  (como Logic y Studio One, sin armar nada) y en las **armadas**, con su instrumento (o el tono de prueba, que suena
  igual que la nota escrita) y su cadena, parado o reproduciendo, también durante la cuenta. El pedal de sustain
  sostiene lo que se suelta (`LiveNotes`; el puente VST3 solo lleva notas). Cada pista elige de dónde toca
  (`Track.MidiInput` / `MidiChannel`): armada, en el chip a la izquierda del ● ("MIDI" = todas, o las primeras letras
  del nombre del aparato), en el chip de la tira del mixer o con clic derecho → "Entrada MIDI: …"; el menú tiene
  "Todas las entradas", cada aparato (enchufado o recordado), el canal y "Configurar las entradas MIDI…". En el
  encabezado el instrumento queda a la izquierda del ● (y del chip, armada).
- **Grabar notas**: una pista MIDI armada graba lo que toca su entrada junto con las tomas de audio y la cámara (o
  sola, sin tomas). Al parar, las notas son un clip nuevo en esa pista, del compás donde empezó la toma al compás
  después del final (las de la cuenta entran desde ahí), arriba de lo que había (no reemplaza: los dos suenan) y en el
  mismo paso de deshacer que las tomas (`MidiEditing.AddRecorded`). Cada nota va donde se oía la canción cuando se
  tocó: el bloque menos la latencia de salida (`midiLatencyFrames`) y menos lo que esperó antes del bloque. Descartar o
  cancelar la cuenta no deja nada. Si no llegó ninguna nota, un toast lo dice y manda a mirar la lucecita.
- **Cuantizar** (pedido del usuario: configurable): a mano con Q (atajo `quantize`) en el piano roll (las elegidas, o
  todas sin ninguna elegida, `PianoRollView.Quantize`), en el timeline sobre los clips MIDI elegidos, o con clic derecho
  en un clip → "Cuantizar a 1/16"; al grabar, si Configuración → MIDI → "Cuantizar al grabar" está prendido. Siempre
  al **imán del piano roll** y sobre la grilla de la canción, no la del clip; mueve el comienzo y deja el largo. En
  Libre no cuantiza y lo dice.
- **Configuración → MIDI** (`Dialogs/SettingsDialog.Midi.cs`, `MidiSetup`): una tarjeta por aparato que Windows ve
  (`MidiInputs.Devices`, por nombre; dos iguales, "nombre #2") con una lucecita que titila con lo que llega y lo
  último en palabras ("C4 · 96 · canal 1", "pedal abajo", `MidiSetup.Describe`), su nombre propio (el del aparato de
  marca de agua), prendido (apagado lo suelta para otro programa), canal (Todos o 1–16), velocidad (Suave / Normal /
  Dura / Fija, con su valor) y para qué se usa (Notas · Perillas · Transporte, casillas; las dos últimas todavía no
  hacen nada, quedan para MIDI learn y la pedalera). Los recordados que no están enchufados salen en gris con "Olvidar".
  "Buscar de nuevo" reabre; además cada 2 s se mira cuántas entradas hay y si cambió se reabren solas. Abajo,
  "Cuantizar al grabar". Todo se guarda en `config.json` "midi" (`MidiSettings`), de la máquina.

## Video | Solo audio (`MainWindow.Mode.cs`, `ProjectSettings.Mode`)

- El interruptor de la barra superior (primero en la fila de herramientas, debajo del nombre; estilo propio
  `Border.mode` / `ToggleButton.mode` en `MainWindow.axaml`: riel hundido con el modo elegido encendido en acento,
  fondo, borde, texto e ícono, y el otro tenue) y del "Nuevo proyecto" del gestor.
- En solo audio el timeline (o el mixer) se mueve al lugar de la preview (`AudioArea`) y la fila de abajo se cierra;
  la cámara se suelta (`LiveCamera.SetPaused`), los decodificadores se cierran, se ocultan Color, Encuadre, la
  latencia de cámara, los fades de video y "En el video" (`videoOnly`), y "Exportar MP4" pasa a "Exportar audio"
  (el mismo `ExportDialog` con WAV / MP3, `AudioExporter`).
- Es un paso de deshacer y no borra nada. Todo lo nuevo que solo sirva con imagen va a `videoOnly` o mira `AudioOnly`.

## Exportar

- **Tramo** (MP4 y audio): fila arriba de Destino (`RangePanel`), visible solo si el proyecto tiene un loop A–B:
  "Todo el tema" (viene elegido, no suma pasos) o "Loop A–B" (`ExportOptions.Range`, `ExportJobSpec.Range`). La
  mezcla se renderiza desde 0 igual (lo que suena de antes de A, como una reverb, sigue sonando), las tomas y notas se
  apagan en B (20 ms) y la cola de efectos suena después; el resultado arranca en A con un fundido de 10 ms
  (`ExportRange`, `MixMaster.Render`). El video lleva solo las tomas que se ven en el tramo, recortadas y corridas a
  0:00 (`ExportRange.Takes`); un panel sin toma en el tramo no aparece. El subtítulo y el tamaño estimado usan el largo
  del tramo. No se recuerda: cada exportación arranca en "Todo el tema".
- **Stems**: la casilla del diálogo de exportar (MP4 o audio) deja además "<nombre> - stems" con un WAV float por
  pista que suena, cada una sola con sus envíos y sin el limitador (`AudioExporter.ExportStemsAsync`). Duran lo mismo
  que la exportación, cola incluida, y no llevan la ganancia de loudness.
- **Destino** (solo MP4): fila segmentada arriba de Calidad con Proyecto · Reels · TikTok · YouTube · IG 1:1 · IG 4:5
  (`ExportTargets`). "Proyecto" es el de siempre (el cuadro del proyecto, la mezcla como está) y viene elegido, así que
  no suma pasos; los demás cambian el cuadro solo para ese video (9:16, 16:9, 1:1, 4:5 = 1080×1350). Se recuerda el
  último (`ExportSettings.Target`). El volumen no se elige acá: es el **Destino del master** (ver Mastering), y una
  línea abajo de Calidad (`LoudnessText`, también en "Exportar audio") dice a cuántos LUFS sale.
- **Avanzado** (solo MP4): un link plegado abajo de Calidad (`AdvancedPanel`); plegado dice lo que no está por
  defecto ("Avanzado · 4K · 20 Mbps"). Adentro, Resolución 720p · 1080p · 1440p · 4K (el lado corto del cuadro,
  `Mp4Exporter.ExportSize(aspect, resolución)`; el borde entre paneles crece con ella) y Bitrate del video: "Automático"
  (el de la calidad) o un slider de 1 a 100 Mbps; mover el slider sale de Automático. Con bitrate a mano, x264 pasa de
  CRF a ese bitrate y la placa lo usa en vez del de la calidad; la calidad sigue eligiendo la velocidad del codificador.
  Se recuerda por proyecto (`ExportSettings.Resolution`, `BitrateMbps`) y la estimación del peso lo usa.
- **Masterizado y colas** (`Dsp/MixMaster.cs`): la mezcla se renderiza con el mismo `MixRenderer` sin limitador y hasta
  8 s después del último clip; corta donde reverb y delay bajan de −66 dBFS (o funde el último segundo si siguen
  sonando en el límite). Después va el `MasterLimiter` con la ganancia que lleva a los LUFS del Destino del master (arrancando de la que ya midió la ventana), midiendo con
  `LoudnessMeter.Measure` y corrigiendo hasta 4 pasadas (solo el limitador). En el MP4, durante la cola el último
  cuadro queda congelado y se funde al fondo (`tpad` + `fade` en `Mp4Exporter.BuildFfmpegCommand`).
- **Velocidad**: `MixRenderer.RenderTo` mezcla de a tramos de 64 bloques en tres etapas con su hilo cada una (pistas
  en paralelo → reverb y delay → master), con el mismo resultado bit a bit que de a un bloque. En el MP4, cada toma
  tiene su propio grafo de filtros en FFmpeg (loopback decoders, `-dec`, FFmpeg 7+), así las tomas se filtran a la vez.
  El video lo codifica la placa de video si FFmpeg encuentra una (NVENC, AMF o Quick Sync, `VideoEncoders`, con el
  bitrate de la calidad en vez del CRF de x264) o el procesador; se elige en Configuración → Guardado y exportación
  (`config.json` "export.encoder", de la máquina y no del proyecto). Si la placa o los grafos fallan, la exportación
  se repite sola con el procesador y un solo grafo, como antes.
- **Transiciones**: el sonido lo funde `MixRenderer` (`TrackDspChain.AudioJoins` / `SampleClips`, igual que la
  reproducción). La imagen, en `Mp4Exporter.BuildFfmpegCommand`, los clips de un mismo panel con una transición
  extienden su tramo (el `trim` y el `enable` del `overlay`), con `tpad` que congela el borde cuando no hay video
  escondido; el fundido cruzado es un `fade` con alfa (`yuva420p`) en el que queda arriba de los dos y pasar por negro,
  dos `fade` al color del fondo. Mismo resultado que la preview.
- **Al terminar** se ven los LUFS integrados de la mezcla y los segundos de cola (en el pie, o en la línea del audio);
  quedan en `ExportRecord.Lufs`, así que también se ven al abrir la exportación desde Historial.
