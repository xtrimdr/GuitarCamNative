# Interfaz — cómo es cada parte de la ventana

Referencia para agentes: qué hay en cada parte de la ventana, cómo se comporta y en qué archivo vive. No hace falta
leerla entera: buscá el apartado de lo que vas a tocar. Las reglas generales de diseño (tokens, tipografía, clases,
atajos) están en [src/GuitarCam.App/AGENTS.md](../src/GuitarCam.App/AGENTS.md). Si cambiás cómo funciona algo de
acá, actualizá su apartado en el mismo cambio.

La captura de referencia es [captura.png](captura.png).

Todo lo que se describe acá lo decidió el usuario: no lo cambies sin preguntar, aunque otro DAW (o la web
`..\GuitarCam`, de donde nació la app) lo haga distinto.

## Estructura

```
┌ barra de título propia (44) ─ logo · nombre · NATIVE · (PRUEBA) ··························· CPU │ GPU │ RAM │ DSP │ FFmpeg · 🔔 · tema · ⚙ · [_ □ ×] ┐
├ fila de herramientas (~40) ─ Video|Solo audio · chip del proyecto (guardado) ▾ │ guardar · deshacer/rehacer ··················· [Exportar MP4] ┤
├ inspector (290) ─┬─ preview (un panel por pista con video) ────────┬─ "En vivo" (256) ─┬─ proyecto (290, plegable a 44) ┤
│ TOMA + secciones │                                                 │ cámara + entrada  │ pestañas Color / Historial     │
├──────────────────┴─ separador arrastrable ─────────────────────────┴───────────────────┤                                │
│ timeline (280): regla (32) · encabezados fijos (324) · un carril por pista (64, 40–320) │                                │
├ transporte, siempre abajo (~52) ────────────────────────────────────────────────────────────────────────────────────────────┤
│ ·························· medidor de entrada (kHz · buffer debajo) · Monitor  [ reloj │ ⏮ ■ ▶ ● loop │ Compás Tempo TAP Cuenta Click ▾ ] ··· Timeline/Mixer · Ver todo │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Barra de título

- Son dos filas en un mismo `TitleBar` (para que todo entre con la ventana sin maximizar, pedido del usuario): la
  primera, de 44, es la barra de título (`ExtendClientAreaTitleBarHeightHint="44"`) con la app, los medidores, avisos,
  tema y Configuración; la segunda es la del proyecto: modo, chip, guardar/deshacer y Exportar a la derecha (ahí no
  hay botones del sistema, así que va sin el margen de 150). Las dos arrastran la ventana.
- Es la de la app (`ExtendClientAreaToDecorationsHint`): arrastrable, doble clic maximiza, los botones del sistema
  quedan a la derecha (margen 150). `TransparencyLevelHint="None"` no se toca: con transparencia, píxeles oscuros
  del video dejan ver lo que hay detrás.
- El logo es el ícono de la app y abre "Acerca de" (`Dialogs/AboutDialog`).
- Al lado del nombre, el estado del guardado (`TickTopBar`): Guardando / Guardado / Sin guardar / Error al guardar /
  Solo lectura. "Sin guardar" sale cuando Configuración → Guardado (`SaveMode`) guarda cada X minutos o a mano; el
  diskette antes de deshacer (`SaveButton`, el atajo "save") guarda ya y está apagado si no hay nada pendiente.
- Una copia hecha por `tools\probar.ps1` lleva un `prueba.txt` al lado del exe (commit, con o sin cambios, hora):
  `TestBuild` lo lee y la barra muestra, después de NATIVE, el badge "PRUEBA a1b2c3d + cambios" (`TestBuildChip`,
  fondo `Hover`, la hora en el tooltip) y el título de la ventana lo repite. Sin ese archivo no aparece nada.
- CPU / GPU / RAM / DSP con la misma forma ("CPU 19% ▬"), con líneas `Border.tsep` entre los grupos.
- Maximizada, Windows corre la ventana 8 px afuera de cada borde (por la barra propia). La grilla raíz lleva
  `Margin="{Binding $parent[Window].OffScreenMargin}"` para compensarlo; no lo saques o se cortan los costados y
  la barra de transporte.

## Avisos y toasts

- **Avisos** = la campanita (`Controls/NotificationBell`, a la izquierda del tema). `Status(texto)` agrega el mensaje
  a su lista (los últimos 50, el más nuevo arriba); un punto de acento marca los sin leer y abrirla los marca como
  leídos. Nada se escribe en el centro de la barra de título ni en diálogos.
- **Toasts** (`Controls/ToastHost`): `Toast(texto)` es para lo que conviene ver en el momento; sale abajo a la
  derecha, encima de todo, se va sola a los 6 s (el puntero encima la frena) y también queda en la campanita. Una
  fija (`sticky`) se va solo con su ×: la usa `--probar "texto"` ("Para probar").

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
- La cantidad principal de cada efecto va en el knob grande (`EffectKnob`: Vida, envío a reverb, envío a delay); los
  ajustes finos siguen en sliders.
- Con su encabezado "Toma" se pliega a una columna de íconos de 44, como la barra del proyecto, y se recuerda.
- El espectro en vivo del EQ sale de un `SpectrumTap` colgado solo de la pista seleccionada mientras el EQ está a la
  vista: el hilo de audio copia a un anillo preasignado y la FFT corre en el `Tick` de la UI.

## Encabezados plegables

Todo encabezado plegable (inspector, Color, Historial) es un `Controls/SectionHeader`: la franja entera reacciona al
hover y se pliega con un clic en cualquier parte fuera de sus botones.

## Barra del proyecto (derecha, `MainWindow.Color.cs`)

- Lo que es del proyecto (lo de la pista va al inspector). Ocupa la altura de la preview y el timeline, se pliega a
  una columna de íconos y recuerda pestaña y secciones plegadas. Las ediciones de Color pasan por `ColorEdit(...)`.
- Pestañas Color / Biblioteca / Historial. Biblioteca (las tomas, usadas o no, importadas o grabadas, y "Borrar no
  usadas") e Historial (exportaciones y actividad; la pestaña por defecto) viven en `MainWindow.History.cs` y se arman
  solo mientras alguna está a la vista (`RefreshHistory`); lo que hacen con el proyecto y los archivos vive en
  `ProjectHistory` (Projects).
- "Salió / volvió al timeline" se registra al guardar (`ProjectStore.Serialize` → `LogTimelineChanges`).

## Preview y monitor "En vivo"

- **Preview**: los paneles se calculan con `PanelLayout.Compute` sobre un lienzo de 1080 px de alto, **la misma
  cuenta que usa la exportación**: lo que se ve tiene que ser lo que se exporta. Encuadre: cover-fit + zoom/paneo/espejo
  del clip. Etiqueta con el nombre de la pista abajo a la izquierda.
- **"En vivo"** (`Controls/LiveMonitor.cs`), a la derecha de la preview: la cámara cruda, nunca la composición. La
  abre `Video/LiveCamera.cs` (un solo FFmpeg, `CameraCapture`), que también alimenta el video de la toma (`VideoTakeWriter`).

## Timeline

- **Clips**: fondo `Hover` al 40 %, borde `Line` (acento al 60 % si está seleccionado), onda `Dim` (acento si está
  seleccionado, `Faint` si la pista no suena), sin el nombre adentro; sin archivo, gris con "Sin conexión…". Con
  video y carriles de 52 px o más, la tira de miniaturas (`Video/VideoThumbnails.cs`) con la onda blanca abajo sobre
  un degradé. Pista seleccionada con barra de acento de 2 px a la izquierda del encabezado; playhead de acento con triángulo.
- **Bordes de clip**: en cada lado dos manijas chicas como la de la columna (pestañita oscura con dos rayitas), una por
  mitad del clip (`DrawEdgeHandles`): la de arriba, donde termina el fade, mueve el fade; la de abajo, en el borde,
  recorta. El cursor lo dice (diagonal como la línea del fade, ↔ para recortar) y se ilumina la manija que vas a agarrar.
- **Varios clips por pista**: Alt+X divide en el cabezal y D duplica (`ClipEditing.Split` / `Duplicate`). Los clips
  de una pista comparten su panel y se ve el de arriba (el último), igual en la preview (`LiveTrack.ShownClip`) y en
  la exportación (`ExportTakeSpec.Panel`).
- **Arrastrar a otra pista** cambia la pista **y el tiempo** a la vez: el original queda atenuado y un fantasma
  punteado marca dónde cae (`DrawGhost`). Con Ctrl se intercambia con el clip de abajo.
- **Rango**: Shift+arrastrar sobre una pista marca un rango; Supr o "Borrar el rango" del clic derecho lo saca de sus
  clips (`ClipEditing.DeleteRange`) dejando el hueco. La selección se va con un clic, Esc o una edición.
- **Clic derecho** (`ShowTimelineMenu`): sobre un clip, solo cosas del clip (dividir, duplicar, al inicio, corregir
  corte, restablecer, quitar clip); en el encabezado o en un tramo vacío del carril, solo cosas de la pista (panel de
  video, renombrar, carpeta, guardar como plantilla, quitar pista); en la parte vacía de la columna de encabezados,
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
  barra. Con las teclas 1 a 5, zooms fijos contados en tiempos (`TimelineView.ZoomPresetBeats`: un tiempo, 1 compás,
  4, 16 y toda la canción), configurables como todo atajo.
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
- Con carriles más altos aparece debajo una fila de cuatro knobs, Vida · Comp · Reverb · Delay (pedido del usuario),
  también en pistas vacías (`TimelineView.EffectRequested`). Son tan grandes como dejan el alto de la pista y el ancho
  de la columna (`FxKnobD`: de `FxKnobMin` = 20 a `FxKnobMax` = 64 px) y se achican con ella; si ni el mínimo entra, la
  fila no se muestra. Debajo de cada uno, ícono `FxIcon` + nombre (solo el ícono si no entra): clic prende/apaga. El
  knob se arrastra en vertical (Shift = fino), con la ruedita o doble clic para restablecer; la cantidad va adentro del
  knob si mide 36 o más, y si no, en lugar del nombre mientras lo tocás.
- Tocar cualquier parte del encabezado (la manija o un control, menos "quitar") selecciona la pista.
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
- Tocarla abre el selector: 12 colores (`TrackIcons.Colors`, guardados en hex) y los íconos por categoría; el color
  deja el selector abierto y el ícono lo cierra.
- Con color, los clips de la pista se tiñen (relleno, borde y onda) y la tira del mixer lo lleva arriba. Los colores
  de pista son datos del usuario, no tokens: no se usan para estados de la interfaz.

## Carpetas de pistas (`MainWindow.Folders.cs`)

- En el timeline, una fila propia (manija, flecha para plegar, ícono y nombre, M S ×; abajo su fader VCA en su color,
  `FolderVolumeRect`, desde `ControlsAt` = 62 px como el volumen de una pista) de `FolderH` = 64 px abierta,
  con el carril vacío (sus pistas ya se ven abajo); plegada toma la altura del zoom vertical y el carril es un **mini
  mapa** (`DrawMiniMap`): una franja por pista con sus clips en el color de la pista y la onda si la franja llega a
  `MiniWaveAt` px. Si se le cambió la altura a mano (`Folder.Height`), manda esa. Sus pistas con el contenido del encabezado corrido `FolderIndent` = 24 px
  (manija, insignia, nombre y sliders). La carpeta y sus pistas van dentro de un recuadro fino de su color
  (`DrawFolderFrames`, encabezados y carriles); sin riel ni tinte en cada pista, que parecían seleccionadas.
- Una carpeta se selecciona como una pista (clic en su fila o en su canal del mixer; `TimelineView.SelectedFolderId`,
  y entonces no hay pista seleccionada): el inspector muestra la carpeta (`InitFolderInspector`: nombre, color,
  volumen, M / S, nueva pista adentro y quitarla) y M, S y Supr actúan sobre ella. Su ícono abre el selector de color
  (`ShowFolderLook`, los mismos `ColorChips` que el de pista).
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
- Doble clic en cualquier otro lugar libre de la fila de una carpeta (encabezado o carril) la pliega o despliega
  (pedido del usuario). Plegar es vista (sin deshacer); el resto pasa por `FolderEdit` o `EditTimeline`.
- En el mixer, cada carpeta es un grupo teñido con su nombre arriba (una franja de `FolderBandH` en todas las
  columnas, para que los faders queden parejos), un canal de carpeta primero y sus pistas, plegable aparte
  (`Folder.MixerCollapsed`).

## Plantillas de pista (`TrackPresets`, `%APPDATA%\GuitarCam Studio\pistas.json`)

Clic derecho en una pista → "Guardar como plantilla" guarda su canal (sin mute ni solo), color, ícono, entrada y si
va en el video; "Nueva pista → Plantilla" crea una pista con eso. En el menú de nueva pista las explicaciones son
tooltips con 1 s de demora (`MenuTipDelay`), no texto al costado.

## Armar y grabar (`MainWindow.Recording.cs`)

- El ● de cada encabezado de pista y de cada tira del mixer (o R, sobre la pista seleccionada) elige dónde graba
  Shift+R (`armedIds`: null = la que elige `TakeRecording.PickTarget` a partir de la seleccionada, vacía = ninguna). Sin pista armada, Grabar queda
  deshabilitado, en video y en solo audio.
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
- Las ediciones de canal pasan por `EditTrack`, las de los buses por `BusEdit`. Los desplegables son `SelectButton`,
  no `ComboBox`.
- Sus medidores usan el degradé verde → amarillo → rojo (`Palette.Meter*`), distinto del `StereoMeter`.
- Cada canal es una tarjeta (`StripCard`) y todos (pistas, retornos, master) se arman con `StripLayout`, que les da
  las mismas filas: así faders y medidores tienen siempre la misma altura. Lo que vaya en la cabecera o en los
  controles tiene que entrar en su fila fija, no estirarla. Los paneles de efecto angostos se reacomodan
  (`FxNarrowW`) en vez de recortar controles.
- En las tiras amplias la cabecera (● M S) y el paneo quedan fijos y solo scrollean los efectos, cada uno en su
  panel; en las compactas el ● va debajo del nombre, porque ● M S C no entran en una fila.
- **Paneo**: un solo desplegable (`BuildPanMenu`) con la apertura arriba (30 % por defecto) y las plantillas con su
  descripción; elegir una la aplica y cierra el menú.

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

- **Stems**: la casilla del diálogo de exportar (MP4 o audio) deja además "<nombre> - stems" con un WAV float por
  pista que suena, cada una sola con sus envíos y sin el limitador (`AudioExporter.ExportStemsAsync`).
