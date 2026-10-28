# Pendientes — GuitarCam Studio Native

Ideas y cosas para implementar en la versión nativa. Cada ítem: qué es, por qué, y notas si hay.
Al terminar uno, pasarlo a [HECHO.md](HECHO.md) con la fecha (y sacarlo de acá).

## Ideas nuevas (solo nativa)

_Ideas y mejoras para la nativa._

> **Foco:** la app sigue siendo para hacer videos de guitarra. Lo de DAW tiene que servir para eso
> y no puede complicar el camino de "grabo, mezclo y exporto el video".

### Pedidas

#### 1. Usarla también como DAW, solo audio
- [x] Proyectos o pistas sin video (hecho, ver [HECHO.md](HECHO.md))
- [x] Edición de clips: dividir, duplicar, mover a otra pista y a otro tiempo, borrar un pedazo (rango), también de
  varias pistas y cerrando el hueco (corre los marcadores si es en todas), y barra de herramientas (hechos, ver
  [HECHO.md](HECHO.md))
- [x] Ganancia por clip con su curva de volumen, y la onda dibujada con la curva aplicada (hecho, ver [HECHO.md](HECHO.md))
- [x] Varios clips por pista: se reproducen, se dibujan, se exportan (comparten el panel), se graba desde el cabezal y
  "Traer al frente" elige cuál se ve donde se pisan (hecho, ver [HECHO.md](HECHO.md))
- [x] Importar audio externo (hecho, ver [HECHO.md](HECHO.md)). Falta: que el clip importado siga el tempo del proyecto (hoy se
  usa tal cual) y importar el audio de un video
- [ ] Transponer: probar si conviene un modo "fino" en cents. Límite conocido: dos armónicos a menos de ~7 Hz (la 3ª
  de un acorde grave) mantienen su batido original
- [x] Efectos en slots por canal, etapa 1 con los efectos internos (hecho, ver [HECHO.md](HECHO.md)). Queda para la
  etapa 2: compensar la latencia de los slots (hoy todos dan 0) y el estado guardable de un plugin
- [x] Mejorar el mixer: medidores completos, envíos pre/post y buses unificados (bus = FX: salida y envíos de pistas,
  carpetas y buses; las carpetas solo agrupan) (hecho, ver [HECHO.md](HECHO.md))
- [ ] **Perillas configurables** (pedido del usuario 2026-10-26, cambio grande: decidirlo antes de hacerlo). Los envíos
  ya son una perilla por bus de la lista de cada pista (hecho, ver HECHO). Falta lo demás: que cada perilla sea un
  atajo que elige el usuario: un parámetro de un efecto de la pista (Vida, umbral o cantidad del compresor…) y, con VST3, uno o
  varios parámetros de un plugin (ej. Mix y Decay de una Valhalla). Abierto: ¿un juego para todas las pistas, por
  proyecto o por pista? ¿se elige con clic derecho en la perilla, en Configuración o en los dos? ¿qué hace sin destino
  (apagada con aviso, o agrega el efecto)? Lo mismo serviría para MIDI learn.
- [ ] **Buses que mandan a otros buses** (sacado el 2026-10-26 a pedido del usuario: el circuito no quedaba claro).
  El motor y `gclab` ya lo soportan; falta una forma de verlo y editarlo que se entienda (un diagrama del ruteo, o
  solo una salida por bus sin envíos) antes de volver a abrirlo.
- …y más: ir sumando acá

#### 2. Soporte de plugins VST
- [x] Instrumentos VST3 en las pistas MIDI (hecho, ver [HECHO.md](HECHO.md)). Falta:
  - [ ] **Efectos VST3** en los slots de pistas y buses (amps, pedales, reverbs de terceros), como un tipo de slot más,
    mezclado con los internos. El puente ya pasa audio de entrada (`Slot.in`); en vivo un efecto suma un bloque de
    demora que hay que compensar en las otras pistas y en el click (PDC), como el limitador.
  - [ ] EZdrummer 3 abre pero no suena por el puente (procesa y devuelve silencio, también en tiempo real y después de
    esperar): probar si carga su kit al abrir su ventana o si le falta la autorización de Toontrack.
  - [ ] Que un instrumento retome las notas largas cuando el cabezal cae en su medio (hoy no se redisparan), y que el
    loop mande las notas de después de A a tiempo (hoy el primer bloque después del salto puede salir tarde).
  - [ ] Elegir el preset del plugin desde la app y mostrar su carga de CPU.
- Notas: decidido con el usuario (2026-10-28): **puente en C++ con el SDK oficial de Steinberg, en otro proceso**,
  comunicado por memoria compartida, así si un plugin se cuelga no tira abajo la grabación. El SDK de VST3 es MIT
  desde la 3.8 (octubre de 2025): ya no hay tema de GPL. Hace falta el compilador de C++ de Visual Studio y es un
  proyecto nuevo en `src/`.
  La exportación tiene que pasar por los mismos plugins, igual que hoy el mix offline usa el mismo grafo (ojo: hoy
  el render corre por tramos en paralelo, con plugins con estado eso se complica).
  Compensar la latencia de los plugins (PDC) en todas las pistas y en el click, como ya se hace con el limitador.
  La ventana propia de cada plugin es una ventana Win32 nativa, por fuera de Avalonia.
  Idea charlada con el usuario (2026-10-26): un VST es una tarjeta de slot más (la misma que pliega, abre en
  ventanita y se arrastra), con "Abrir" para su ventana y 2 a 4 perillas que elige el usuario de sus parámetros.
  Para eso, que todo efecto (nativo o VST) exponga una misma lista de parámetros (id, nombre, valor 0..1, texto):
  sirve también para las perillas configurables y el MIDI learn. Un plugin sin parámetros útiles queda con
  encendido y "Abrir".

#### 3. MIDI
**Prioridad del usuario (2026-10-28): un editor para armar bases con instrumentos virtuales.** Decidido:
- **Clips MIDI por pista**, como Logic o Studio One: cada clip tiene sus notas y se edita en un piano roll. Sin
  patrones reusables ni step sequencer (se puede sumar después).
- El piano roll va **abajo, en lugar del mixer** (se alternan), sin esperar a los paneles desacoplables.
- Primero un **sampler propio** (tus WAV), después VST3 (punto 2).

Etapas (cada una sirve sola):
- [x] **Etapa 1: pistas y clips MIDI con piano roll** (hecho, ver [HECHO.md](HECHO.md)). Lo que anotó el usuario al
  probarla (2026-10-28):
  - [x] Varios clips MIDI a la vez, el piano roll después de Timeline y su grilla (hechos, ver [HECHO.md](HECHO.md)).
  - [ ] Le falta: que los clips MIDI entren en las herramientas (dividir, borrar, escuchar) y en el rango; que la nota
    que suena al tocar el piano roll pase por la cadena de la pista; copiar y pegar notas dentro del piano roll.
  - [ ] Más cosas en el piano roll (pedido del usuario, 2026-10-28, "bends y eso"): pitch bend y otras curvas por clip
    (modulación, sustain, expresión), dibujadas en carriles como la franja de velocidad. Con el tono de prueba se
    pueden oír los bends; lo demás tiene sentido con el sampler y los VST3, que las reciben como eventos MIDI.
- [ ] **Etapa 2: sampler.** Un WAV por nota (kit de batería) o uno melódico que se transpone. Voces preasignadas en el
  hilo de audio, y el mismo camino en la reproducción y en la exportación.
- [ ] **Etapa 3: instrumentos VST3** con el puente del punto 2.
- [x] **Etapa 4: entradas MIDI** (hecho, ver [HECHO.md](HECHO.md)). Lo que quedó para después:
  - [ ] Probarlo con un teclado de verdad: la latencia al tocar un VST3 (buffer + el bloque del puente) y si las notas
    grabadas caen donde se tocaron (`midiLatencyFrames`); en esta máquina no había ninguna entrada MIDI.
  - [ ] Que una toma MIDI sobreviva si el programa se muere a la mitad (hoy las notas viven en memoria hasta parar;
    las tomas de audio tienen su diario).
  - [ ] Pasarle al VST3 el pedal, el pitch bend y la modulación como eventos (el puente solo lleva notas: el pedal se
    resuelve antes, en `LiveNotes`), junto con los carriles de curvas del piano roll.
  - [ ] Salidas MIDI (synths de hardware, clock hacia afuera) y Windows MIDI Services (varias apps con el mismo
    teclado a la vez).
- [ ] Después: importar y exportar .mid.

Otras de MIDI (se apoyan en las entradas de la etapa 4):
- [ ] Mapear perillas y faders de un controlador al mixer (MIDI learn)
- [ ] MIDI clock / tempo sincronizado con otros equipos
- [ ] (Baja prioridad: al usuario no le interesa mucho por ahora) Pedalera o controlador MIDI: grabar/parar, play,
  marcar tomas con los pies

#### 4. Edición de video más completa
- [ ] Color por toma, además del color global
- [ ] Más filtros y efectos (nitidez, desenfoque, glow, estabilización, chroma key/pantalla verde)
  Investigación de CapCut y orden propuesto en [docs/filtros-capcut.md](docs/filtros-capcut.md):
  - [ ] Importar LUT .cube
  - [ ] HSL de 8 colores; después Curvas y ruedas lift/gamma/gain
  - [ ] Look brumoso/pastel (como "Sueño de Mistwood"): en CapCut es un shader de 4 colores con ruido, no un LUT
  - [ ] (Para después, lo pidió el usuario 2026-10-25) Efectos de movimiento: sacudida, zoom y destello al pulso del
    click, glitch
  - [ ] Light leak y partículas de luz (bokeh, luciérnagas, chispas) con semilla fija, igual en vista previa y exportación
  - [ ] VHS, glitch / RGB split, película vieja (polvo, rayas), desenfoque de entrada y salida
- [ ] Textos y títulos: nombre del tema, créditos, carteles
- [ ] Transiciones entre layouts (las de video entre clips ya están: ver HECHO 2026-10-25)
- [ ] Recorte y encuadre animados (zoom/paneo que cambian con el tiempo)

#### 7. Paneles reubicables y desacoplables (pedido 2026-10-25)
Poder acomodar la ventana a cada video (vertical: la preview grande a un costado) y sacar paneles a otros monitores.
Decidido con el usuario:
- El diseño es **de la app** (`config.json`), no del proyecto. Al pasar el proyecto a 9:16 se **sugiere** el diseño
  Vertical con un toast; nunca se cambia solo.
- Pueden salir a otra ventana: **Mixer** (con Mastering), **Preview**, **En vivo**, **Inspector** y **Proyecto**. El
  **transporte** y la barra de título quedan fijos en la ventana principal; el timeline también.
- Hecho a mano (sin `Dock.Avalonia`, salvo que el usuario cambie de idea): sin dependencias nuevas y sin MVVM.

Etapas (cada una sirve sola):
- [x] **Etapas 1 y 2: paneles que se mueven sin reconstruirse y diseños Clásico / Vertical** (hecho, ver
  [HECHO.md](HECHO.md)).
- [ ] **Etapa 3: sacar a otra ventana.** _En pausa (el usuario, 2026-10-26: no es prioridad hoy)._ Botón en el encabezado del panel; cerrar la ventana lo devuelve a su lugar. Se
  recuerdan monitor, posición y tamaño; si el monitor ya no está, vuelve a la principal. Preview con pantalla completa
  (monitor para grabar lejos de la compu). Con el mixer afuera se ven timeline y mixer a la vez: la X lo trae al frente.
  Los atajos funcionan desde cualquier ventana. Ojo: dos ventanas dibujando tampoco pueden generar basura por cuadro, y la
  preview de afuera usa el mismo decodificador.
- [ ] **Etapa 4 (si hace falta): arrastrar y soltar** como CapCut, con guías para soltar y diseños propios guardados.

### Sugerencias (para decidir)

_Ideas que surgieron charlando. Todas apuntan al video._

- [ ] **Layout que cambia en el tiempo ("director")**: en el solo, el panel del punteo crece o pasa a
  pantalla completa, y después vuelve al split. Se marca en el timeline como automatización.
- [ ] **Paneles automáticos**: los paneles se reacomodan solos según quién toca. Si entra un arreglo de
  4 s, los que están se corren con una animación, aparece el nuevo y cuando termina vuelven a ocupar todo.
  Notas: que la disposición sea una función del tiempo del timeline (`Layout(t)`: tramos con el mismo
  conjunto de pistas activas, la grilla de `PanelLayout` para cada uno, ~0,4 s de interpolación con easing),
  así la preview, el scrub y la exportación dan lo mismo. Que la transición termine justo cuando entra el
  clip, que los huecos cortos (< ~2 s) no desarmen nada, que se mantenga el orden y que sea opcional (apagada
  por defecto). La preview es fácil; lo difícil es la exportación, que hoy usa rectángulos fijos en el
  `filter_complex`: o se usan expresiones de FFmpeg con `t` (`overlay`, `scale` con `eval=frame`, y el
  encuadre acompañando) o se componen los cuadros en .NET y se le mandan a FFmpeg por pipe.
- [ ] **Automatización** de volumen, paneo y efectos en el timeline (base para lo anterior)
- [ ] **Varias cámaras por toma**: plano general + primer plano de la mano, y elegir cuál se ve en cada momento
- [ ] **Importar video del celular** y sincronizarlo solo por el audio de la cámara (correlación,
  como la calibración de latencia)
- [ ] **Amp sim + cargador de IRs de gabinete**: grabar la guitarra directa (DI) y que suene bien sin VSTs
- [ ] **Grabación en loop / tomas múltiples (comping)**: grabar varias pasadas del solo y elegir
  los mejores pedazos
- [ ] **Visualizadores de audio** como capa del video (onda o espectro animado)
- [ ] **Acordes / tablatura / letra en pantalla** mientras grabás (tipo teleprompter) y opcionalmente en el video

### Opcional — ideas de Fender Studio Pro (2026-10-24)

Se relevaron los 224 temas del manual y se cruzaron con la nativa. El detalle, las fuentes y el orden
sugerido están en [docs/FENDER_STUDIO_PRO_ANALISIS.md](docs/FENDER_STUDIO_PRO_ANALISIS.md).
Estas ideas son opcionales, sin compromiso de implementación. Comping, MIDI, automatización, amp sim, importación
de video y normalización ya están anotados arriba; el análisis amplía su alcance sin duplicarlos acá.

- [ ] **Regrabar un rango automáticamente**: escuchar compases previos del tema, grabar solamente entre
  entrada y salida y escuchar el después. Amplía la grabación actual desde el cabezal; requiere silenciar
  la toma anterior solo dentro del tramo y conservar la sincronía de cámara.
- [ ] **Fundidos cruzados de audio**: unir clips o correcciones con un fundido breve ajustable, igual en
  reproducción y exportación; distinguirlo de las transiciones visuales.
- [ ] **Exportar un rango**: solo el tramo A–B (o entre marcadores), con su cola de efectos como la exportación
  completa.
- [ ] **Aplicar una plantilla de pista a una pista existente**: hoy una plantilla (`TrackPresets`) solo crea una pista
  nueva; cargarla sobre una pista con clips, sin tocarlos, y definir qué pasa con los envíos y los retornos compartidos.
- [ ] **Bloquear edición de pistas o clips**: proteger contra mover, recortar, borrar y reemplazar al
  grabar, con deshacer y una regla compartida por ventana y `gclab`.
- [ ] **Comparar mezclas A/B**: guardar estados de canales y buses para comparar sonidos dentro del
  proyecto, sin duplicar los medios.
- [ ] **Chequeo mono y correlación**: revisar la mezcla y capturas con varios micrófonos; distinguir
  monitoreo de lo que se exporta.
- [ ] **Puerta / expansor suave**: reducir ruido entre frases sin cortar el sustain de la guitarra.
- [ ] **Paquete portable del proyecto**: ZIP para trasladar o respaldar, distinguiendo copia reducida
  para compartir de respaldo completo con tomas no usadas e historial.
- [ ] **Rescatar audio recién tocado**: investigar un búfer opcional y preasignado para recuperar una
  idea sin haber apretado Grabar. Adaptación propia: la grabación retrospectiva del manual es MIDI.
- [ ] **Congelar pistas al incorporar VST**: render temporal reversible para reducir DSP, conservando
  originales y estado de plugins.

## Notas

- `AudioGapLog` es un diagnóstico provisorio: sacarlo (o dejarlo detrás de una opción) cuando el audio esté confirmado.

- Puntitos en la preview (resuelto): con la ventana permitiendo transparencia, algunos píxeles oscuros
  del video dejaban ver la ventana de atrás. `TransparencyLevelHint="None"` lo corta, y con el video
  decodificado quedan solo unos pocos píxeles negros sobre casi negro, invisibles. Ojo al revisar
  capturas: las de GDI (CopyFromScreen/PrintWindow) dan alfa 0 en los píxeles negros y los visores los
  pintan blancos, aunque en pantalla se vean bien. Mirar el RGB, no el alfa.
- Lo que falta probar a mano (saltos del video mientras reproduce, fps con muchas tomas, calibración,
  medidores con sonido…) está todo en [docs/PRUEBAS.md](docs/PRUEBAS.md).
- Cámara: mientras la app está abierta, la cámara queda tomada, así que otro programa no
  la puede usar. Si se desconecta, el monitor dice "Sin señal de cámara" y no se reconecta sola: hay que
  tocar "Refrescar dispositivos".
