# Hecho — GuitarCam Studio Native

Lo que ya se terminó de [PENDIENTES.md](PENDIENTES.md), el más nuevo arriba. Cada ítem: fecha (AAAA-MM-DD),
qué se hizo y dónde. Es historial: no hace falta leerlo para trabajar, salvo para buscar cómo se resolvió algo.

- 2026-10-25: Release 0.9.5 en GitHub con su instalador. `tools\make-installer.ps1` ahora le pone la versión al exe y
  publica en una carpeta limpia; el instalador lleva los DLL de FFmpeg de la preview (`ffmpeg\`).
- 2026-10-25: Transiciones entre clips de una pista (Corte / Fundido cruzado / Pasar por negro, 0,1 a 3 s, 0,5 por
  defecto) que funden la imagen y el sonido. Un chip blanco (`|`, `X`, `V`), centrado en cada unión del timeline, abre el
  menú (`MainWindow.Transitions.cs`, `TimelineView.DrawJoins`); también desde el clic derecho del clip. Se guarda en el
  clip que entra (`Clip.Transition`, `TransitionSec`); la cuenta es una sola para reproducción, preview y exportación
  (`GuitarCam.Audio/ClipTransitions.cs`; `LiveTrack.UpdateTransitions`, `PreviewView.DrawPicture`; el audio en
  `LiveTrack.AddClips` y `TrackDspChain.SampleClips`; en `Mp4Exporter`, `fade` con alfa o a color y `tpad` para
  congelar el borde sin video escondido). `gclab proyecto editar … transicion` y `gclab proyecto transiciones`.
- 2026-10-25: Preview sin cortes al pasar de un clip a otro: el video se decodifica con FFmpeg dentro del proceso
  (`GuitarCam.Audio/NativeVideoSource.cs`, FFmpeg.AutoGen 9 + DLL en `lib\ffmpeg`; un salto en una toma de 720p pasa
  de ~130 ms a ~20 ms, sin asignar memoria por cuadro, colores iguales a ffmpeg.exe), con ffmpeg.exe por pipe de
  respaldo (`Video/PipeVideoSource.cs`). Cada clip arranca su decodificador 1 s antes de empezar y muestra el primer
  cuadro en el mismo tick (`LiveClip.PreRollSec`, `VideoDecoder.Want`); un panel no muestra cuadros viejos
  (`LiveClip.Fresh`); los decodificadores sin usar se cierran a los 3 s (`VideoDecoder.Idle`). `gclab leer-video` lo mide.

- 2026-10-25: Clips: **Ctrl+C / Ctrl+X / Ctrl+V** (`ClipClipboard`, `MainWindow.Timeline.cs`, también en los menús de
  clic derecho) pegan en el cabezal desde la pista elegida, con la distancia entre pistas; **D** duplica varios clips
  marcados, en varias pistas, como un bloque (`ClipEditing.DuplicateMany`). `gclab proyecto editar … duplicar a,b` y
  `pegar`, con sus comprobaciones en `verificar-clips`.
- 2026-10-25: Masterización, Etapa 3: panel **Look** al principio de la cadena con seis looks (Natural, Pegadito,
  Brillante, Cálido, Al frente, Domar la púa, como los describió el usuario; `Dsp/MasterLooks.cs`) y su perilla
  Cuánto (de Natural al look entero, reescribe las perillas; tocar una a mano lo deja en "A mano"). Cuarta banda
  **Púa** (`MasterPickTamer`): un corte dinámico en 2–5 kHz (en − k·banda: en 0 pasa exacto) contra el nivel sostenido
  de la banda, que baja los ataques y deja el tono; con su perilla y su barra en el multibanda. **Sugerime**
  (`Dsp/MasterSuggest.cs`, `MainWindow.Suggest.cs`): escucha la mezcla antes del master en segundo plano (octavas
  contra el balance de una mezcla terminada y lo picuda que es cada banda) y muestra una tarjeta con lo que propone y
  Aplicar / Cancelar, como eligió el usuario. `gclab masterizar --look --cuanto-look --pua --sugerime`; tres
  comprobaciones nuevas en `comprobar-dsp`.
- 2026-10-25: Masterización, Etapa 1 completa: gráfico chico del multibanda en lugar de las dos perillas de cruce, con
  la curva de cada banda (Linkwitz-Riley) que baja con lo que comprime y los cruces que se arrastran (Shift fino, doble
  clic vuelve) (`Controls/MasterBandsGraph.cs`, sin asignar por cuadro).
- 2026-10-25: Masterización, Etapa 2: el **Destino** de LUFS pasa al master (Como está, YouTube, Instagram, TikTok,
  Spotify −14, Apple Music −16; `MasterLoudness`, `MasterSettings.Target`). Después de cada edición mide la mezcla en
  segundo plano con el mismo `MixMaster.Render` de la exportación y guarda la ganancia (`TargetGainDb`), que la
  reproducción aplica en lugar del Empuje (`MainWindow.Loudness.cs`); la exportación arranca de ella y cae en la misma,
  así que se escucha igual que sale. El diálogo de exportar ya no elige volumen (los presets solo cambian el cuadro; se
  fue la casilla de −14 de "Exportar audio"). Soft clip con **Auto**: de lo que el pico más fuerte pasa del Techo más
  allá de 2 dB, se lleva la mitad (hasta 4 dB) y el resto queda para el limitador; el ADAA va solo sobre lo que recorta,
  así que debajo del codo pasa exacto (antes filtraba los agudos) y su medidor ya no marca de más. `gclab masterizar
  --volumen`, `gclab exportar --volumen`; tres comprobaciones nuevas en `comprobar-dsp`.
- 2026-10-25: Scrollbars de toda la app fijos: de 8 px y sin flechas cuando hacen falta, ocultos si no, y al costado
  del contenido en vez de encima (`App.axaml`). Mastering llega bien al final de la cadena (margen en el contenido, no
  `Padding` del `ScrollViewer`), sin scroll vertical, y Salida va en su propia caja igual a la del master, con los
  paneles de las dos del mismo alto (`MainWindow.Master.cs`).
- 2026-10-25: Diseño de la ventana (etapas 1 y 2 de "Paneles reubicables"): botón en la barra de título con Clásico y
  Vertical (video de arriba a abajo a la izquierda o a la derecha, timeline al lado y mixer debajo, columna de ancho
  arrastrable), guardado en `config.json`, y un toast que lo ofrece al pasar a 9:16 (`MainWindow.Layout.cs`;
  `MoveBottomView` ahora sirve para solo audio y para Vertical).
- 2026-10-25: "Volver a una versión guardada" en la ventana: el chip del proyecto abre un menú con "Proyectos…" y las
  últimas 20 versiones de `historial/` (`MainWindow.Versions.cs`, `ProjectStore.Restore`); antes solo con gclab.
- 2026-10-25: Avisos que salen como toast, elegidos por el usuario: errores al guardar (proyecto y tomas), toma
  guardada, exportación terminada con "Mostrar" (`ToastHost` ahora acepta un botón), cámara que se corta
  (`TickCamera`) e interfaz de audio que deja de responder (`PlaybackEngine.Stalled`, `TickAudioDriver`).
- 2026-10-25: "Borrar y cerrar el hueco" sobre todas las pistas corre también los marcadores: los de después se
  corren el largo del rango y los de adentro quedan al inicio del rango, sin perderse (`ProjectMarkers.CloseGap`,
  desde `ClipEditing.DeleteRangeClosing`; vuelve con deshacer). En algunas pistas solas no se mueven, y el rango A–B
  tampoco. Comprobado en `gclab proyecto verificar-clips`.
- 2026-10-25: "Avanzado" en el diálogo de exportar MP4, plegado: resolución 720p / 1080p / 1440p / 4K y bitrate a
  mano (1–100 Mbps, o Automático = el de la calidad), recordados por proyecto (`ExportSettings.Resolution`,
  `BitrateMbps`; `Mp4Exporter.ExportSize`, `VideoEncoders.Args`). `gclab exportar --resolucion --bitrate`.
- 2026-10-25: Exportación más rápida (un MP4 de 95 s con 2 tomas: de 73 s a ~46 s; el audio solo, de 6,9 s a 4,3 s).
  El video se codifica con la placa de video si FFmpeg encuentra NVENC, AMF o Quick Sync (`VideoEncoders`, se elige
  en Configuración → Guardado y exportación, `gclab exportar --codificador`); cada toma se filtra en su propio grafo
  de FFmpeg en paralelo (`Mp4Exporter.BuildFfmpegCommand`, mismos cuadros que antes); la mezcla corre en tres etapas
  en paralelo (`MixRenderer.RenderTo`, bit a bit igual). Si la placa falla, repite sola con el procesador.
- 2026-10-25: Con barras de cine, el fundido de video de la preview dejaba ver el video a través de las barras
  (Avalonia aplica la opacidad a cada dibujo por separado): ahora las barras van fuera del fundido
  (`PreviewVideoFx.DrawLetterbox`, `PreviewView`). Barras de cine **Libre** con slider "Alto de las barras" (1–40 % del
  panel por barra, `VideoFx.LetterboxHeight`), igual en la preview y en el MP4 (`VideoColorProcessor.LetterboxBar`).
- 2026-10-25: Pasar a solo audio mientras se reproducía dejaba el timeline (y los medidores) muerto en los dos modos:
  los decodificadores se cerraban en otro hilo mientras el tick dibujaba esos clips, el tick tiraba un error y su
  timer no volvía a arrancar. Ahora se sueltan en la UI (`LiveClip.DetachVideo`, `MainWindow.Mode.cs`) y un error en el
  tick queda en `sesiones.log` sin frenar el timer (`SafeTick`).
- 2026-10-25: Biblioteca: cada canal sin usar tiene su papelera (confirmación en la fila) y al escucharlo aparece un
  mini reproductor bajo la fila con la onda (clic o arrastre para saltar), ±5 s, pausa y tiempo (`MainWindow.History.cs`,
  `Controls/TakeScrubber.cs`, `TakePreview`). Las dos barras laterales cambian de ancho arrastrando el borde y lo
  recuerdan (`MainWindow.Sidebars.cs`).
- 2026-10-25: El Ancho del mastering ya no sube el volumen al abrir (ni lo baja al cerrar): compensa la energía que
  cambia el ancho según cuánto centro y costados tiene la mezcla, medido en vivo (`StereoWidth`), así el ON/OFF compara
  solo el ancho. `gclab comprobar-dsp` lo mide (antes: hasta 3,9 dB de diferencia; ahora 0,1).
- 2026-10-25: En modo video la onda sobre las miniaturas toma el color de la pista (antes siempre blanca), y cada clip
  lleva arriba a la derecha un badge de solo audio / audio + video con tooltip (`TimelineView.MediaBadge`).
- 2026-10-25: "Abrir carpeta" del gestor entiende qué elegiste: el proyecto, una subcarpeta suya (media\, exports\…),
  la carpeta que tiene un solo proyecto adentro, o uno sin `guitarcam.json` pero con `.bak` o historial (lo rescata).
  Un proyecto de la web o una carpeta con varios lo dicen claro en vez de "no está vacía".
  `ProjectLibrary.Inspect`, `ProjectStore.IsRecoverable`, `gclab proyectos buscar`.

- 2026-10-25: **Herramientas del timeline**: Seleccionar, Rango, Dividir, Borrar, Escuchar, Zoom y Mano, en una caja
  centrada en la barra del proyecto (teclas 1 a 7; los zooms fijos que tenían 1 a 5 quedan sin tecla), cada una con
  su puntero. `MainWindow.Tools.cs`, `Controls/TimelineView.Tools.cs`, `Controls/ToolCursors.cs`, `LiveTrack.ListenSolo`.
- 2026-10-25: **Rangos de varias pistas y "Borrar y cerrar el hueco"** (Shift+Supr): lo de después se corre en esas
  pistas. `ClipEditing.DeleteRangeClosing`, `gclab proyecto editar borrar-rango pista,pista desde --hasta fin --cerrar`.
- 2026-10-25: **Traer al frente**: donde dos clips de una pista se pisan, el clic derecho elige cuál se ve (timeline,
  vista previa y exportación). `ClipEditing.BringToFront`, `gclab proyecto editar al-frente clip`.
- 2026-10-25: **Transponer más rápido y con feedback**: el `PitchShifter` analiza y sintetiza otros bloques mientras
  integra las fases (resultado idéntico, 3 min de 7,0 s a 4,2 s), los clips muestran "Transponiendo… %" con una barra,
  el knob Tono queda gris con el porcentaje y un toast avisa cuando está lista; los valores probados quedan en caché
  durante la sesión. `PitchShifter.Shift`, `TimelineView.DrawTransposing`, `MainWindow.Transpose.cs`.
- 2026-10-25: **knob de Transponer en el encabezado de pista y elegir qué knobs se ven**: Tono se suma a Vida · Comp ·
  Reverb · Delay (bipolar, −12…+12) y Configuración → Vista los prende y apaga (todos por defecto). `TrackKnobs`,
  `TimelineView.SetKnobs`, `SettingsDialog.BuildView`.
- 2026-10-25: **Transponer se ve en el canal y es un efecto más**: tarjeta propia en el inspector (después de Delay,
  atenuada en Original), panel − / + en la tira amplia del mixer, "+2" en la compacta y una etiqueta "♪ +2" junto al
  nombre en el encabezado de pista (gris mientras se calcula). `MainWindow.Transpose.cs`, `TimelineView.DrawHeader`.
- 2026-10-25: **avisa al cerrar con cambios sin guardar**: en los modos cada X minutos o a mano (o si un guardado
  falló) cerrar la ventana pregunta Guardar y cerrar / Cerrar sin guardar / Cancelar, y si no puede guardar no cierra.
  `Dialogs/UnsavedDialog`, `Closing` en `MainWindow.axaml.cs`.
- 2026-10-25: **la grilla manda, el borde corrige**: con imán y bordes a la vez, la cercanía al borde se mide desde
  donde la grilla pone el clip y no desde el mouse (con pasos de compás saltaba ~26 px fuera de la grilla, visto en
  un video del usuario). `ClipEditing.Drag`, caso en `gclab proyecto verificar-clips`.
- 2026-10-25: **imanes sin saltos y selector de imán a clips**: con los dos imanes gana el más cercano (el paso de
  la grilla o un borde a menos de 8 px); antes el borde ganaba siempre y al entrar o salir de su alcance el clip
  saltaba medio paso. Se pega a bordes visibles, y el botón pasó a ser un selector No / Inicio / Final / Ambos.
  `ClipEditing.Drag` (`SnapEdges`), `MainWindow.Timeline.cs`, `gclab proyecto editar --a-clips`.
- 2026-10-25: **guía del imán a otros clips**: mientras un clip está pegado al borde de otro, una línea de acento
  cruza las pistas en ese borde. `ClipEditing.Drag` (`stuckTo`), `TimelineView`.
- 2026-10-25: **la onda sigue al clip con el zoom al máximo**: la clave del caché de ondas era una sola suma y a
  96 000 px/s no veía los movimientos chicos (la onda quedaba vieja hasta hacer zoom); ahora cada valor va aparte
  (`TimelineView.WaveKey`).
- 2026-10-25: **grilla elegible e imán hasta 1/64**: el clic derecho en la regla elige la grilla (Auto, Igual que el
  imán, o una subdivisión fija que no cambia con el zoom) y el imán suma 1/32 y 1/64. `TimelineView.GridMode`,
  `MainWindow.Timeline.cs` (`ShowGridMenu`), `ClipEditing.GridSeconds`.
- 2026-10-25: **imán a la grilla e imán a otros clips, por separado**: "Sin imán" ya no pega al cabezal, al cero ni a
  los bordes (el imán de 8 px heredado de la web); los bordes de otros clips son un toggle propio al lado del imán,
  guardado en config.json. `ClipEditing.Drag` (`edges`), `MainWindow.Timeline.cs`, `gclab proyecto editar --a-clips`.
- 2026-10-25: **mover fino y alinear a la grilla**: los clips se mueven, recortan y fundan de a muestra (antes de a
  ms, que al zoom máximo eran 96 px, más grueso que la grilla aun con Alt) y el clic derecho suma "Alinear a la
  grilla" con la subdivisión del imán, para uno o todos los marcados ("Quitar" también los saca a todos).
  `ClipEditing.ToSample` / `AlignToGrid`, `MainWindow.Timeline.cs`, casos en `gclab proyecto verificar-clips`.
- 2026-10-25: **mover con grilla conserva el desfasaje**: un clip (o varios) que no está justo en la grilla se
  mueve de a pasos enteros sin saltar a la línea, así cae igual de corrido en otro compás; Alt sigue siendo libre.
  `ClipEditing.Drag`, caso nuevo en `gclab proyecto verificar-clips`.
- 2026-10-25: **rectángulo de selección de clips**: arrastrar en vacío sobre las pistas marca los clips que toca
  (Ctrl suma); moverlos los lleva juntos en el tiempo, cada uno en su pista, y Supr los borra a todos, en un solo paso
  para deshacer. `Controls/TimelineView.cs` (`boxed`, `StartCarry`), `MainWindow.Timeline.cs` (`ClipsMoved`).
- 2026-10-25: **el limitador, fuera de la caja del master**: la vista Mastering separa en una caja lo que prende el ON
  del master (Limpieza a Soft clip, que se atenúa entera con el master apagado) y deja afuera "Salida" (Limitador →
  Volumen), que trabaja siempre; el estado apagado dice "la mezcla va directo a la salida". `MainWindow.Master.cs`.
- 2026-10-25: **limitador y medidor en pico real**: el Techo pasa a dBTP con −1 por defecto (lo que piden las
  redes; el AAC del MP4 clipeaba con −0,3 por muestra). `TruePeak` sobremuestrea ×4 con el filtro de BS.1770 y el
  `MasterLimiter` lo usa como detector además de cada muestra; el panel Volumen muestra "Pico real" en dBTP (rojo si
  pasa del Techo), la exportación sin limitador frena en el pico real y `gclab masterizar` lo informa. Se guarda con
  otra clave (`limCeilingDbtp`): los proyectos de antes arrancan en −1. Comprobaciones en `comprobar-dsp`.

- 2026-10-25: **Transponer por pista**: slider en Mezcla del inspector (−12 a +12 semitonos, `Track.Transpose`) que
  cambia el tono sin cambiar el largo, así sigue en sincronía con el video. `PitchShifter` corre el espectro de cada
  cuadro (85 ms, FFT ×4) sin estirar ni remuestrear y rearma las fases desde los bins más fuertes (PVDR + phase
  locking), con estéreo coherente; se hace en segundo plano y queda en `cache/` (`TransposeCache`), mientras tanto
  suena el original y al llegar entra con un crossfade de 10 ms (`LiveClip.Sound`). Exportar y `gclab mezclar` /
  `exportar` usan el mismo caché; `gclab transponer` y `proyecto editar … transponer`, y comprobaciones en `comprobar-dsp`.
- 2026-10-25: **Cinta, Ancho, Soft clip y Volumen en el mastering**: tres etapas nuevas antes del limitador
  (`Saturator` en modo cinta, `StereoWidth` con graves mono, `SoftClipper` con ADAA, igual en vivo y al exportar) y un
  panel de LUFS / pico / "para −14" al final; la vista tiene el alto de la cadena y no se estira.
- 2026-10-25: **vista Mastering y multibanda sin medir**: tercer botón Timeline · Mixer · Mastering con la cadena
  en el lugar del mixer (en vez de la tarjeta flotante); cada banda fija su umbral siguiendo su propio nivel
  (`MasterBandComp`), así no hay "Midiendo la mezcla" (se sacó `MasterAnalysis`); el limitador tiene ON
  (`LimOn`, `MasterLimiter.Bypass`) y sin él la exportación con LUFS frena en el Techo.
- 2026-10-25: **tarjeta del master en cadena horizontal y limitador ajustable**: un panel por etapa (Limpieza →
  Multibanda → Color → Limitador) con perillas más grandes, ON propio para la EQ de color (`ColorOn`), y el limitador
  con Empuje, Techo y Release (`MasterSettings.Lim*`, `MasterLimiter.Set`, `MixGraphBus.SetMaster`), igual en vivo y
  al exportar (`MixMaster`). La medición ya no se cancela con cada edición. Sin Comparar.
- 2026-10-25: **master, etapa 1**: el botón "Master" del mixer abre una tarjeta con una EQ (Corte · Barro antes del
  multibanda; Graves · Presencia · Aire · Inclinación después), un compresor de 3 bandas Linkwitz-Riley con una
  perilla "Cuánto" y un medidor por banda y cruces ajustables (tuvo un Comparar a igual volumen; el usuario lo sacó
  porque no se entendía). Los umbrales salen de medir la mezcla en segundo plano y ajustar cada banda como el compresor de pista, con
  makeup que conserva el volumen; se guardan en el proyecto, así la reproducción y la exportación suenan igual.
  `Dsp/MasterChain.cs`, `Dsp/MasterAnalysis.cs`, `MasterSettings`, `MainWindow.Master.cs`; `gclab masterizar` y
  comprobaciones en `comprobar-dsp`.

- 2026-10-25: **rango A–B de reproducción**: franja arriba de la regla donde se arrastra el rango (bordes y cuerpo
  móviles), I / O lo ponen en el cabezal, L prende el loop, y un rango de pista (Shift+arrastrar) se puede repetir
  desde su menú. El loop vuelve a A con el crossfade de los saltos y sin reiniciar los efectos (antes volvía a 0 con
  un corte y reiniciaba la cadena). `ProjectSettings.LoopStartSec/LoopEndSec`, `PlaybackEngine.SetLoopRange`,
  `TimelineView.Markers.cs`, `MainWindow.Markers.cs`; `gclab proyecto repetir`.
- 2026-10-25: **marcadores con nombre**: banderitas en la misma franja que se arrastran, renombran en el lugar y
  saltan con un clic; K agrega uno en el cabezal, `,` / `.` van al anterior / siguiente, y el clic derecho ofrece
  nombres (Intro, Estrofa, Estribillo, Puente, Solo, Final) y "Repetir hasta el siguiente". Se deshacen como las
  pistas (`Project.Markers`, `ProjectMarkers`, `UndoManager`); `gclab proyecto marcadores`, `verificar-marcadores`.
- 2026-10-25: **exportar con presets para redes**: fila "Destino" en el diálogo de exportar (Proyecto por defecto,
  Reels · TikTok 9:16, YouTube 16:9, IG 1:1, IG 4:5 = 1080×1350), con la mezcla llevada a −14 LUFS; en "Exportar
  audio", una casilla de −14 LUFS (`ExportTargets`, `ExportDialog`, `ExportSettings.Target` / `AudioLufs`;
  `gclab exportar --destino --lufs`).
- 2026-10-25: **LUFS de la mezcla al terminar de exportar**: el resultado se mide con `LoudnessMeter.Measure` (el
  cálculo del medidor del master) y se ve en la pantalla final y en Historial (`ExportResult.Lufs`, `ExportRecord.Lufs`).
- 2026-10-25: **colas de efectos al exportar**: la mezcla sigue hasta 8 s después del último clip y corta cuando la
  reverb y el delay se apagan (−66 dBFS); el video congela el último cuadro y lo funde al fondo durante la cola
  (`Dsp/MixMaster.cs`, `Mp4Exporter.BuildFfmpegCommand`, `gclab exportar --cola`; comprobado en `comprobar-dsp`).
- 2026-10-25: **color de carpeta a sus pistas**: al elegirlo, el selector (y el inspector) pregunta si pinta también
  sus pistas con ese color (`MainWindow.TrackLook.FolderTracksAsk` / `PaintFolderTracks`, un paso de deshacer).
- 2026-10-25: **24 colores de pista en dos filas**, en orden de arco iris (`TrackIcons.Colors`, `ColorsPerRow`;
  `MainWindow.TrackLook.ColorChips`): se suman los tonos intermedios y se conservan los 12 de antes con el mismo hex.
- 2026-10-25: **knobs de efecto en los encabezados de pista**: Vida · Comp · Reverb · Delay pasan de cuatro filas
  de slider a una sola fila de knobs que crecen con el alto de la pista y el ancho de la columna (20 a 64 px) y se
  achican con ella (`TimelineView.FxKnobD`, `DrawFxKnob`); arrastre vertical, ruedita y doble clic para restablecer.
- 2026-10-25: **íconos de pista nuevos y más grandes**: rellenos, Fluent Emoji High Contrast de base y lo que le
  falta de Material Design Icons, game-icons y Qlementine (`tools/make-track-icons.py` los pasa a una grilla de 32,
  licencias en `Assets/Icons/LICENSES.txt`; los ids viejos de IconOven van a su par en `TrackIcons.Legacy`). En el
  timeline la insignia crece con la columna de encabezados de 28 a 52 (`TimelineView.BadgeSize`) y la columna llega
  a 640 (antes 460); en el mixer y el inspector pasó a 28 / 22.
- 2026-10-25: **Looks Fresco, Limpio, Moody y Crema** (los populares que faltaban, ver docs/filtros-capcut.md) y
  ajustes **Blancos, Negros y Sensación de luz** en Color → Ajustes, horneados en el mismo LUT que la vista previa y la
  exportación (`VideoColorProcessor.BuildLut`, `ColorGrade.Whites/Blacks/Light`, `gclab looks --blancos --negros --luz`).
- 2026-10-25: **5 looks nuevos y Brujo más fiel**: Solo rojo, Plata, Noche azul, Vivo y Hielo, que dan el mismo
  resultado que los filtros de CapCut que usa el usuario con un modelo propio (curva de tono, saturación y giro de
  tono por color, virado y curvas por canal) ajustado por mínimos cuadrados sobre cuadros reales (`FittedLooks.cs`).
  Brujo pasó a ese modelo. `gclab looks` los lista, comprueba el rango y escribe un look como `.cube`.
  Investigación en [docs/filtros-capcut.md](docs/filtros-capcut.md).
- 2026-10-25: **"+ Nueva pista" fija al pie** de la columna de encabezados, encima de los sliders de zoom
  (`TimelineView.AddRect`, `DrawAddButton`), en vez de ir debajo de la última pista.
- 2026-10-25: **varias pistas juntas**: Ctrl+clic / Shift+clic en los encabezados las marca, la manija las arrastra
  en bloque y el menú "Carpeta" las mueve todas (`TimelineView.Pick`, `TrackFolders.Move` con varias). **Quitar una
  carpeta** con pistas pregunta si van también (`MainWindow.RemoveFolder`, `gclab … quitar-carpeta --con-pistas`).
  Arreglos: el resize de una pista ya no hace saltar al resto al soltar, y el lugar donde cae una pista arrastrada se
  elige por el hueco más cercano con 8 px de "pegue" (`FindDrop`), así se puede sacar la última pista de una carpeta.
- 2026-10-25: **Barra superior en dos filas** para que entre con la ventana sin maximizar: arriba la barra de título
  (app, medidores, avisos, tema, ⚙); abajo modo, proyecto, guardar/deshacer y Exportar (`MainWindow.axaml`, `TitleBar`). Exportar, más prolijo: 32 de alto,
  ícono de descarga, hover más claro (`AccentHover`) y apagado sin volverse gris (`Button.accent` en `App.axaml`).
- 2026-10-25: **Ctrl+rueda al revés y "Invertir" por gesto**: Ctrl+rueda hacia arriba va a la derecha
  (`TimelineView.OnPointerWheelChanged`); la ruedita sola, zoom, costados y altura tienen cada uno su "Invertir" en
  Configuración → Atajos (`Shortcuts.WheelSign`, guardado en `config.json` "wheelReverse").
- 2026-10-24: **un solo camino para probar**: `tools\probar.ps1` compila, corre `gclab comprobar-dsp`, copia la app a
  `%LOCALAPPDATA%\GuitarCam Pruebas` y la abre desde ahí (el `bin\` nunca queda bloqueado); la copia lleva
  `prueba.txt` y la barra de título muestra "PRUEBA <commit>" (`TestBuild.cs`); el acceso directo del escritorio
  (`make-shortcut.ps1`) abre la última copia; si hay un GuitarCam abierto, un cuadro pregunta si lo cierra (guarda
  antes, `CloseMainWindow`) para que la copia nueva no abra el proyecto en solo lectura. Se trabaja en `master`, sin ramas ni worktrees (AGENTS.md §6).
- 2026-10-24: **hover de botones sin gris y con glow**: los botones encendidos que se pintan desde código (M/S, armar,
  ON, C/LIM, muestras de color, sonido del click) llevan la clase `lit` y conservan su color al pasar el mouse; los
  `:pressed` que faltaban (acento, primario, peligro, play encendido…) y los grises de Fluent van al token `Hover`;
  todo botón brilla suave con el acento (`HoverGlow`, un `DropShadowEffect` del botón que recolorea `UiTheme`) o en rojo
  los de peligro, y los botones de color (Exportar, primario, peligro) ya no cambian de color en hover (`App.axaml`).
- 2026-10-24: **modo de guardado elegible**: Configuración → Guardado (`SaveMode.cs`, `SettingsDialog.BuildSave`)
  elige entre guardar al editar, cada X minutos (1–30) o a mano; diskette en la barra de título (`SaveButton`) y estado
  "Sin guardar" (`TickTopBar`). Tomas, exportaciones y cerrar o cambiar de proyecto guardan siempre.
- 2026-10-24: **ruedita configurable**: Shift+rueda hace zoom y Ctrl+rueda va a los costados (antes al revés); los
  modificadores de zoom, costados y altura se eligen en Configuración → Atajos, girando la ruedita con ellos apretados
  (`Shortcuts.Wheel`, `config.json` "wheel"; tooltips del pie del timeline leídos de ahí).
- 2026-10-24: **carpeta plegada en el mixer compacto y barra espaciadora**: el nombre de la carpeta ya no ensancha su
  grupo (`Controls/NoWidth.cs`; nombre y cantidad se recortan juntos en `MainWindow.FolderGroup`); soltar una tecla de
  transporte no llega al botón enfocado (`OnTransportKeyUp`) y los M / S / ● del mixer no toman el foco.
- 2026-10-24: **alto propio por pista y carpeta, y zooms fijos**: `Track.HeightScale` (proporción del alto general: el slider las sigue escalando) / `Folder.Height` (arrastrando el borde de
  abajo del encabezado; doble clic en el borde o el botón ↺ del pie, `ResetHeightsButton`, lo igualan); el timeline mide
  cada fila con `LaneH` / `RowH` / `FolderRowH`; teclas 1 a 5 = un tiempo, 1, 4 y 16 compases y toda la canción
  (`TimelineView.ZoomPreset`, atajos `zoom1`…`zoom5`).
- 2026-10-24: **timeline sin tooltips y ruedita nueva**: se sacaron los tooltips del timeline (trababan el zoom y el
  scroll y no sumaban); queda el cursor (`TimelineView.UpdateCursor`, cursores armados una vez en `CursorOf`) y se
  borraron sus textos. Rueda sola: arriba y abajo; Ctrl: zoom; Shift: a los costados; Alt: altura.
- 2026-10-24: **carpetas más firmes y renombrar en el lugar**: la carpeta se selecciona (timeline y mixer) y el inspector
  la muestra con nombre, color, volumen y M / S (`InitFolderInspector`); su ícono abre el selector de color (`ShowFolderLook`,
  `ColorChips` compartido con el de pista); M, S y Supr actúan sobre ella; las pistas de una carpeta van con sangría
  (`FolderIndent`, 24 px) y la carpeta entera dentro de un recuadro fino de su color (`DrawFolderFrames`, sin riel); doble clic en el nombre de una pista o carpeta lo edita ahí mismo (`MainWindow.Rename.cs`).
- 2026-10-24: **más zoom en el timeline y barra de zoom**: zoom horizontal hasta 2 px por muestra (la onda dibuja las
  muestras, `TimelineView.SampleWave`; regla hasta 1 ms y tiempos partidos) y pistas de hasta 320 px; los sliders de zoom
  horizontal y de altura van al pie de la columna de encabezados (`TimelineFooter`); el volumen del encabezado tiene más
  presencia (barra gruesa, marca en 100, tapa de fader).
- 2026-10-24: **carpetas de pistas, plantillas de pista y menú de nueva pista**: `Folder` / `Track.FolderId` y
  `TrackFolders` (Projects: orden como secuencia, mover adentro / afuera, quitar, VCA con `Mix`); el timeline arma filas
  en píxeles (`TimelineView.Relayout`, `FindDrop`) con fila de carpeta, riel de color, plegado y el lugar donde cae la pista
  marcado; el mixer las muestra como grupos con un canal de carpeta (`MainWindow.Folders.cs`); el fader, M y S de la carpeta
  llegan al motor (`LiveTrack.FolderGain`…) y a la exportación. `TrackPresets` (`pistas.json`): clic derecho en una pista →
  Guardar como plantilla, y Nueva pista → Plantilla. Las ayudas del menú de nueva pista pasaron a tooltips con 1 s de demora.
  `gclab proyecto verificar-carpetas`.
- 2026-10-24: **color e ícono por pista**: `Track.Color` / `Track.Icon` (también en las plantillas) y una insignia
  (`TrackIcons.DrawBadge`, `Controls/TrackBadge`) en el encabezado del timeline, el inspector y el mixer; tocarla abre el
  selector (`MainWindow.TrackLook.cs`) con 12 colores y 53 íconos de instrumentos en 5 categorías: IconOven (MIT),
  generados con `tools/make-track-icons.py` en `Controls/TrackIcons.Data.cs`. Con color, los clips se tiñen.
- 2026-10-24: **toasts y `--probar`**: `Controls/ToastHost` muestra avisos abajo a la derecha, encima de todo (también del
  gestor y los diálogos); se van solos a los 6 s salvo con el puntero encima, y los fijos esperan su ×. `MainWindow.Toast`
  los manda también a la campanita. `GuitarCam.exe --probar "texto"` abre con un toast fijo "Para probar" (`App.ParseArgs`).
- 2026-10-24: **manijas en el timeline**: la columna de las pistas se ensancha solo desde su manija (una franja de 8 px adentro
  del borde, de todo el alto; antes compartía píxeles con el recorte de un clip en 0 s), y cada clip tiene manijas chicas en
  los bordes: mitad de arriba = fade, mitad de abajo = recorte, con el cursor que corresponde (`TimelineView.DrawEdgeHandles`,
  `HitHandle`, `OnGrip`).
- 2026-10-24: **parar con fundido**: Espacio y ■ paran con el mismo fundido de 10 ms (smoothstep) que play, los saltos y el
  scrub (`PlaybackEngine.FadeOutAndPause`, `StopTransport`); las pistas se apagan antes de los buses, así la reverb y el delay
  siguen sonando. `Stopping` distingue el fundido en curso (play en ese momento vuelve a subir donde está). `Stop`/`Pause`
  siguen cortando en el acto para grabar, exportar y cambiar de proyecto. El largo del fundido de play / stop / scrub se elige
  en Configuración → Entradas y salidas, "Fundido al arrancar y parar" (2–50 ms, 10 por defecto; `config.json`
  `audio.transportFadeMs`, `PlaybackEngine.TransportFadeMs`); el crossfade de los saltos queda fijo en 10 ms.
- 2026-10-24: **corte en el primer play de la sesión**: lo que solo corre sonando (mezcla de clips, saturación, compresor,
  EQ, LUFS, grabación) se compilaba (JIT) la primera vez adentro del callback y el bloque se pasaba del buffer (5,8 ms en uno
  de 4). `AudioWarmUp` precompila en segundo plano al arrancar todo `GuitarCam.Audio`, `GuitarCam.App.Audio` y
  `GuitarCam.App.Model`, y al terminar de abrir un proyecto hace una recolección completa con todo parado. Sin medir con la placa.
- 2026-10-24: **saltos de audio al mover el cabezal mientras suena**: `PlaybackEngine.Seek` escribía la posición desde la UI
  y el callback la pisaba al terminar el bloque (a veces se perdía el salto, pero la cadena se reiniciaba igual), y el corte era
  seco. Ahora, sonando, es un pedido que toma el hilo de audio: un bloque se apaga en la posición vieja y el siguiente se
  enciende en la nueva (`pendingSeek`, `SeekRamp`), sin reiniciar los efectos (el compresor reiniciado arrancaba sin
  reducción y el salto sonaba más fuerte); las colas de reverb y delay siguen. `AudioGapLog` (diagnóstico, provisorio) anota
  los huecos del callback en `%APPDATA%\GuitarCam Studioudio-cortes.log`: con saltos sonando no hubo ninguno.
  Arrastrar el cabezal sonando (regla y mini timeline del mixer) ya no hace "grrr": como en Studio Pro o Logic, el audio se
  pausa con fundido mientras arrastrás y sigue con fundido desde donde soltás (`FadeOutAndPause`, `ScrubChanged`); un clic
  suelto sigue siendo un salto. Play también arranca con fundido.
  Los fundidos duran 10 ms fijos con curva suave (`TransportFadeSec`, `TransportRamp`), no un bloque del buffer (4 ms con 192
  muestras hacía clic con material saturado). El salto sonando es un crossfade de igual potencia entre la posición vieja y la
  nueva, sumado en los clips antes de la cadena (`LiveTrack.AddClips`, `CrossfadeWeights`): bajar y después subir dejaba un
  "hipo" que se veía hasta en el cabezal, que ahora muestra el destino desde el clic (`PositionSec` lee el salto pendiente). Un clic en la regla
  pedía dos saltos (al apretar y al soltar) y el segundo tiraba el cabezal para atrás: ahora es uno solo (`scrubSought`).
- 2026-10-24: **la onda del clip con su ganancia** (ítem 1): `TimelineView.Waveform` multiplica cada columna por la
  ganancia del clip y su curva (lo mismo que suena), y el caché se rehace al cambiar la curva o la ganancia. Sin mirarla en la ventana.
- 2026-10-24: **afinador pulido**: notas en castellano desde `Loc` (`note.*`, en inglés C D E) en el panel y en la celda;
  triángulos a los costados que dicen para dónde girar la clavija y parpadean más rápido cuanto más desafinado
  (`TunerView`); "OK!" en verde dentro de ±3 cents (token `InTune` = verde de los medidores del mixer); slider
  "Sensibilidad" = compuerta de nivel de `PitchTap.GateDb` (por defecto −50 dB, igual que antes), en `config.json`
  (`tuner.gateDb`) y en `gclab afinar --umbral`; el panel se cierra con un clic afuera. `comprobar-dsp` suma un tono de
  −60 dB que no pasa con −50 y sí con −70. Sin probar con una guitarra ni mirando el panel en la ventana.
- 2026-10-24: **afinador y medidor de LUFS** (sugerencias): celda "Afinador" (A) en la barra de transporte con un panel
  de aguja (`TunerView`); YIN sobre la primera pista armada o la entrada 1-2 (`PitchTap`). Celda "LUFS" con el integrado
  del master de cada pasada, corto y momentáneo en el tooltip (`LoudnessMeter`, BS.1770). `gclab lufs`, `gclab afinar` y
  comprobaciones en `comprobar-dsp` (notas de B1 a A4 dentro de 0,3 cents; seno de −23 dBFS = −23,0 LUFS). Sin probar
  con una guitarra de verdad ni la celda LUFS mientras suena.
- 2026-10-24: **stems** (ítem 1): casilla "Además, cada pista en su WAV (stems)" en el diálogo de exportar (MP4 y audio,
  se recuerda en `settings.export.stems`): deja "<nombre> - stems" con un WAV float de 32 bits por pista que suena, cada
  una sola con sus efectos y envíos, sin el limitador del master y desde 0 (`AudioExporter.ExportStemsAsync`,
  `MixRenderer.Render(only:)`). `gclab exportar … --stems`. Probado con "Un beso y una flor": los 4 stems suman la mezcla
  (diferencia −39 dB, lo que hace el limitador). Sin ver el paso "Separando las pistas" del diálogo.
- 2026-10-24: **ganancia por clip y curva de volumen** (ítem 1): slider "Ganancia del clip" en el inspector
  (−24 a +12 dB) y `Clip.GainCurve` (`ClipGainCurve`): en el clip seleccionado, clic en su línea agrega un punto, se
  arrastra y doble clic lo saca; "Restaurar clip" saca las dos cosas. Reproducción y exportación la aplican igual (cada 64
  muestras con rampa). `gclab proyecto editar carpeta ganancia|curva` y 6 comprobaciones en `verificar-clips` (también se
  arreglaron dos que ya fallaban por el imán con grilla). Probado en la ventana: agregar y sacar puntos con el mouse;
  sin probar el arrastre de un punto.
- 2026-10-24: **grabar desde el cabezal** (ítem 1): la cuenta lleva hasta donde está el cabezal, la toma cae ahí
  (`RecordingJournal.StartSec`; el motor cuenta desde `recStartFrame` en `PlaybackEngine`) y al parar el cabezal vuelve
  a ese punto. En una pista con clips, desde el cabezal la toma reemplaza solo el tramo que cubre
  (`ClipEditing.DeleteRange` en `TakeRecording.AddTake`); desde 0, toda la pista, como antes. La barra roja del timeline
  crece desde el inicio de la toma. 2 comprobaciones nuevas en `verificar-tomas`. Sin probar con una interfaz ASIO:
  ni la cuenta, ni el click después de ella, ni el video de la toma desde el cabezal.
- 2026-10-24: **cabezal y medidores del timeline en una capa aparte** (`TimelineView.Layer`): mientras suena solo se
  redibuja esa capa, no cada clip, onda y etiqueta. El timeline entero se redibuja solo si se desplaza, si se graba
  (la barra roja crece con el cabezal) o si se arrastra un encabezado (los medidores vuelven al fondo para que la
  tarjeta levantada los tape). Con "Un beso y una flor" sonando: de ~4,2 a ~2 MB/s de basura y de 22 a 11 GC cada
  40 s; los gen1 de la reproducción ya no dejan huecos (queda uno, en el primer gen1 después de abrir y dar play).
- 2026-10-24: **cortes de audio por el recolector de memoria**: cambiar Compacto / Amplio en el mixer lo reconstruía
  entero y los GC frenaban el callback de ASIO 10–40 ms; ahora arma las dos vistas juntas y alterna (`MixerLayout`).
  Además la UI dejó de generar basura por cuadro: el timeline y el medidor de entrada no se redibujan si nada se movió,
  y `Palette` cachea textos (`TextLayout`), pinceles y plumas. Medido con "Un beso y una flor": de ~20 MB/s y ~48 GC
  cada 20 s a ~4 MB/s y ~10 (quieta, 1,7 MB/s); huecos de audio de 1,5 bloques o más, de 13 cada 20 s a 1 cada 40 s.
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
