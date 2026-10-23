# Migración de la app web a la nativa

Relevamiento completo de la app web (`..\GuitarCam`, commit `563da2e`) y qué falta para tenerla en la
nativa. Cada ítem dice **qué hace y cómo funciona en la web**, y en qué estado está acá:

- ✅ portado · ◐ a medias (se dice qué falta) · ✗ falta

Al 2026-10-22, lo entregado por los agentes que todavía no se revisó (A3, encuadre de A4, B2–B5) no
está marcado acá: figura en [PENDIENTES → Entregado, falta revisar](../PENDIENTES.md). Se marca al aprobarlo.

Regla: todo se porta **igual que en la web** (diseño, textos, flujo). Ver
[AGENTS.md → Migrar algo de la web](../AGENTS.md#migrar-algo-de-la-web). Las únicas diferencias que ya
están decididas son las del formato de proyecto propio (ver §2).

---

## 1. Pantalla principal

### 1.1 Barra superior ◐
- **Logo + "Studio"**, **chip del proyecto** (nombre; tooltip con la carpeta; abre el gestor "Proyectos").
  Nativa: abre el gestor (§2.1) ✅.
- **Indicador de guardado**: girando mientras guarda, tilde cuando terminó, "Error al guardar" en rojo
  si falló ✗.
- **Deshacer / Rehacer** (Ctrl+Z / Ctrl+Y o Ctrl+Shift+Z) ✅. Ver §9.
- **Medidor de entrada estéreo** (clic: abre Configuración → Entradas) ◐ (se ve, sin abrir Configuración).
- **Medidor del sistema**: CPU / GPU / RAM de toda la PC, cada 1,5 s. CPU y RAM salen del sistema; GPU
  (el motor más cargado, como el Administrador de tareas) y VRAM, de los contadores de rendimiento de
  Windows. Tooltips "Procesador…", "Placa de video…", "Memoria…" ✗. En nativo se lee directo, sin servidor.
- **Aviso "FFmpeg no encontrado"** si falta en el PATH (no se va a poder exportar) ✗.
- **Selector de tema** (ícono de paleta): acento (presets + "Color a elección") y fondo (Grafito,
  Pizarra, Madera, Bosque, Ciruela, Negro). Siempre oscuro. Por defecto, cian sobre Grafito. Se recuerda
  entre sesiones ✗. *(Hoy la nativa tiene un solo tema fijo: hay que pasar los tokens a recursos dinámicos.)*
- **Engranaje** → Configuración (§7) ✗.
- **Exportar MP4** (botón de acento) → diálogo de exportación (§8) ✗.

### 1.2 Barra lateral izquierda: "Toma" (inspector de la pista seleccionada) ◐ (secciones ✅; faltan plegar el panel y las plantillas de paneo)
Se puede plegar a una tira de íconos (se recuerda). Las secciones se pliegan por título y eso también se
recuerda. Arriba, "Mostrar / Colapsar todos los efectos".
- **Sin selección**: "Seleccioná una toma" + "Hacé click en una pista o en su panel del video", o
  "Todavía no hay tomas grabadas." ◐
- **Pista vacía**: "Pista vacía" + "También podés agregar acá una toma del historial desde «Nueva pista»".
  El canal se puede editar igual y lo hereda la próxima toma que se grabe ahí ✗.
- **Mezcla**: mute, Volumen, Paneo; plantillas de paneo para todas las pistas (§4.3) ◐ (volumen y paneo sí).
- **Saturación** (§5.2): switch, Válvula / Cinta, "Vida" con su ayuda ✅.
- **Compresor** (§5.3): switch, "¿Qué estás tocando?" (Rasgueo / Arpegio / Punteo), Cantidad, medidor
  "Comprime"; "Ajuste fino a mano" con Umbral, Ratio, Ataque, Release, Knee, Ganancia; EQ del preset
  (Corte de graves, Barro 280 Hz, Presencia, Brillo 9 kHz); "Limitar picos"; "Volver al estilo por
  defecto" ✅.
- **EQ** (§5.4): switch + editor gráfico ✅ (falta el espectro en vivo).
- **Reverb**: switch + "Envío a reverb" ✅. **Delay**: switch + "Envío a delay" ✅.
- **Sincronización**: "Offset de audio" (mueve la toma con su anclaje) y "Latencia de cámara" ✅ (en la
  nativa, por clip).
- **Encuadre**: Zoom, espejar, "Resetear encuadre" ✅ (y el arrastre y la rueda en la preview, §1.4).
- **Clip**: Recorte inicio/final, Fade in/out de audio, "El video sigue los fades del audio" o fades de
  video propios, "Restaurar clip" ✅.

### 1.3 Barra lateral derecha: proyecto (pestañas Color / Historial) ◐ (falta el contenido de Historial)
Se pliega igual que la izquierda; la pestaña elegida se recuerda. Lo que es por pista va a la izquierda y
lo que es del proyecto, a la derecha.
- **Color** → §6 ✅. **Historial** → §2.3 ✗ (la pestaña está y dice que todavía no está en la nativa).
- Nativa: plegar (a una columna de íconos), pestaña y secciones plegadas se recuerdan en `config.json`
  (`MainWindow.Color.cs`). Termina arriba de la barra de transporte, que acá va abajo de lado a lado.

### 1.4 Escenario (preview) ✅
- **Formato**: "Lado a lado" / "Apilado" y relación de aspecto: Horizontal (YouTube) 16:9, Vertical
  (Reels / Shorts / TikTok) 9:16, Cuadrado 1:1. Elegir 9:16 pasa a apilado y 16:9 a lado a lado ✅
  (`MainWindow.Preview.cs`; un paso de deshacer por cambio, como `checkpoint()` sin clave).
- **Composición**: un panel por toma con la grilla compartida con la exportación ✅; encuadre por toma
  (cover + zoom + paneo + espejo) ✅; color, viñeta, grano y barras de cine (§6) ✅; fades de video a
  negro ✅ (solo mientras suena o graba, como la web); panel vacío fuera del clip ✅; borde de acento en la
  toma elegida ✅.
- **Arrastrar un panel** mueve el encuadre (panX/panY), **rueda** = zoom, **clic** selecciona la toma ✅.
- Textos de estado: "La composición aparece cuando termines la primera toma", "Mientras grabás, la cámara
  se ve en el monitor "En vivo"", "Cargando video…" ✅.
- La cámara en vivo **nunca** entra en la composición: va al monitor.
- Nativa, diferencias: los íconos de "Lado a lado" / "Apilado" (Columns2 / Rows2 de lucide) están
  dibujados, porque Segoe no los tiene; la separación entre paneles es la de la exportación (4 px de
  salida) y no los 3 px del lienzo de la web; los textos usan la escala de la nativa en vez de tamaños del
  lienzo; una pista cuyo primer clip no tiene video no ocupa panel (así lo hace la exportación nativa).
  Falta la cuenta grande (1-2-3-4) sobre el escenario: hoy la cuenta se ve en el monitor "En vivo".

### 1.5 Monitor en vivo ✅
Al lado del escenario: la cámara cruda (sin composición), el dispositivo de entrada y el medidor
estéreo. Se pone rojo mientras graba. Mensajes: "Listo para grabar", "Sin señal de cámara / Elegila en
Configuración"; botón "Configurar entradas".
- Nativa: `Controls/LiveMonitor.cs`, con los mismos textos, "Cuenta n/4" y "GRABANDO mm:ss.s".
  Diferencias: sin el punto que late ni el brillo rojo animado alrededor del cuadro (la nativa no tiene
  animaciones decorativas; quedan los colores y el borde), y el medidor de entrada sigue también en la
  barra de transporte (en la web, en pantallas anchas, solo está en el monitor).

### 1.6 Barra de transporte ◐
- **Grabar** (R): botón con destino "→ Nombre de la pista" o "→ pista nueva". Durante la cuenta:
  "Cancelar n/4" (Esc). Grabando: "Detener y guardar la toma" (Espacio / R) y "Parar sin guardar esta toma" ✅
  (`MainWindow.Recording.cs`; el reloj y el cabezal se ponen rojos, el timeline muestra "Grabando…").
- **Al inicio** (Inicio) ✅. **Detener**: vuelve a donde arrancó; si ya estaba detenido, al inicio ✗.
  **Play / Pausa** (Espacio) desde el cabezal ◐ (hoy play/stop). **Loop** ("Repetir en loop", prendido
  por defecto) ✗.
- **Reloj**: posición / total, en compases.tiempos.semicorcheas o en minutos:segundos (clic alterna;
  compartido con la regla) ◐ (hoy fijo).
- **Tempo** (§4.5) ◐ (solo se muestra).
- **Cuenta** (Shift+C, cuenta de 4 antes de grabar, prendida por defecto) ✗.
- **Click** (C) ✅ y su menú: sonido (Bip, Madera, Clave, Aguja, Cencerro, Batería: clic para elegirlo y
  escucharlo) y **volumen** (-24 a +9 dB). Se recuerda entre sesiones ◐ (todo implementado, también la
  escucha con `PlaybackEngine.PreviewClick`; falta probarlo a mano).
- **Timeline / Mixer** (X) ✅.

### 1.7 Panel inferior ◐
- **Altura** con separador arrastrable, una para la timeline y otra para el mixer, recordadas ◐ (la del
  mixer se recuerda; la del timeline vuelve a 280 al abrir).
- Con el mixer visible, arriba va una **mini timeline** (clic o arrastre mueve el cabezal) ✅.

---

## 2. Proyectos y persistencia

> **Diferencia decidida:** la nativa usa su propio formato (`guitarcam.json`: medios / pistas / clips,
> guardado atómico, historial de versiones, recuperación de grabaciones) ✅, y los proyectos de la web
> se **importan** a una carpeta nueva "Nombre (nativa)" ✅. Todo lo demás del gestor va igual que en la web.

### 2.1 Gestor "Proyectos" (`SessionPicker`) ✅
Diálogo que aparece al arrancar (sin proyecto no se puede hacer nada) y desde el chip del proyecto.
- **Lista** de los proyectos de la carpeta de proyectos más los recientes de otros lados, con pistas,
  tomas, duración, BPM y tamaño. **Buscador** ("Buscar"). Estados vacíos: "Todavía no tenés proyectos.
  Creá uno con "Nuevo proyecto"." y "Ningún proyecto coincide con la búsqueda."
- **Nuevo proyecto**: nombre + **Plantilla** (Vacío, las de fábrica o las del usuario; se recuerda la
  última). El nombre se limpia para Windows y, si ya existe, carpeta y nombre llevan "(2)" (y el
  formulario lo avisa).
- **Por fila**: Renombrar (F2; si está dentro de la carpeta de proyectos también renombra la carpeta;
  bloquea nombres repetidos: "Ya hay otro proyecto con ese nombre."), Guardar como plantilla, Duplicar
  (tomas y ajustes, no exportaciones), Mostrar en el Explorador, Borrar (la carpeta entera a la
  Papelera; no el proyecto abierto: "Abrí otro proyecto para poder borrar este"), "Quitar de la lista"
  para los que ya no existen ("No se encontró la carpeta").
- **Abrir carpeta…** (selector nativo de Windows): una carpeta vacía crea un proyecto ahí; una con
  proyecto lo abre. En nativo además: una carpeta de la web ofrece importarla.
- **Carpeta de proyectos**: por defecto `Documentos\GuitarCam Proyectos`, se cambia desde el pie del diálogo.
- Renombrar o duplicar el proyecto abierto guarda antes lo pendiente y pausa el autoguardado; no se
  mueve la carpeta mientras se está guardando una toma.
- Mientras abre, progreso de carga de tomas.

### 2.2 Plantillas de proyecto ✅
Una plantilla es una lista de pistas vacías con sus canales + formato, aspecto, BPM, reverb, delay,
color y efectos de video. De fábrica: "Rítmica + Punteo", "Rítmica L/R + Punteo", "Rítmica L/R +
Arpegio + Punteo", "Rítmica + Punteo vertical", "Toma única". Las del usuario son del programa (no del
proyecto); guardar con el mismo nombre reemplaza; se borran desde el formulario de nuevo proyecto
("mía"). "Guardar como plantilla" toma cada pista con mute/solo limpios y el nombre sin "Toma N — ".

### 2.3 Historial (pestaña derecha, `HistoryPanel`) ✗
- **Tomas**: todas las grabadas, filtro Todas / En uso / Sin usar, buscador, agrupadas por día ("Hoy",
  "Ayer", fecha). Por toma: escuchar el WAV crudo (sin efectos), mostrar en el Explorador, quitar del
  timeline (el archivo queda) o volver a agregarla; "No usada"; reintentar si no se pudo guardar.
- **"Borrar no usadas (n · tamaño)"**: manda a la Papelera las tomas fuera del timeline.
- **Exportaciones**: los MP4 exportados, estén donde estén; ver el video, mostrar en la carpeta, mandar
  a la Papelera (confirmación en la misma fila).
- **Actividad**: el registro del proyecto en frases ("Creaste el proyecto", "Grabaste 3 tomas",
  "Moviste una toma"…), agrupando ráfagas de lo mismo.
- En nativo, el registro ya se guarda (`Log`) ✅; falta mostrarlo.

### 2.4 Guardado ◐
- Web: autoguardado con demora; cada toma se sube apenas se graba y, si falla, queda pendiente para
  reintentar; aviso al cerrar si hay algo sin guardar. Las tomas nunca se borran desde la interfaz.
- Nativa: autoguardado a 1,5 s, Ctrl+S, historial de versiones, recuperación de grabaciones ✅. Falta el
  indicador de guardado de la barra (§1.1).

---

## 3. Timeline ◐ (edición de clips ✅; faltan miniaturas, regla en minutos y botón del medio)

- **Regla** en compases y tiempos (4/4 al tempo) o en minutos y segundos ◐ (solo compases). Clic o
  arrastre mueve el cabezal con imán; Alt sin imán ◐ (clic sí).
- **Barra de herramientas**: modo de regla, **imán a la grilla** (cabezal, mover clips, recortes y
  fades), **Ver todo**, **altura de pistas** ◐ (Ver todo sí).
- **Encabezado de pista**: agarre para reordenar, número, nombre, M, S, quitar, volumen, perilla de
  paneo, medidor ✅. Las pistas vacías dicen "Pista vacía · seleccionala para grabar acá" /
  "Apretá R para grabar en esta pista"; mientras graba, "Grabando…".
- **Carriles**: tira de miniaturas del video debajo de la onda ✗; onda ✅ (gris, acento si está
  seleccionado, como la web); parte cortada antes del 0 rayada con la tijera y "N s" ✅.
- **Clips**: arrastrar el cuerpo para moverlo (imán a 0, cabezal, bordes de otros clips y grilla, sobre
  el anclaje para no romper la sincronía; Alt libre); arrastrar arriba/abajo a otra pista (mueve o
  intercambia: "Mover a «…»" / "Intercambiar con «…»"); bordes para recortar; esquinas para fades ✅
  (en pistas con varios clips solo se intercambia el que está bajo el puntero: diferencia nativa).
- **Menú contextual** de carril o clip: Activar sonido / Silenciar, Solo, Llevar al inicio, Fijar el
  corte en 0, Restaurar recortes y fades, Quitar clip (Supr; la pista queda vacía), Quitar pista ✅.
- **"+ Nueva pista"**: pista vacía o una toma sin usar del historial ✅.
- **Reordenar pistas** arrastrando el agarre (las otras se corren; el orden define los paneles) ✅.
- **Selección**: clic en un encabezado o carril vacío lo selecciona; clic en un lugar en blanco
  deselecciona ◐.
- **Zoom y desplazamiento**: rueda = horizontal; Shift = zoom anclado al cabezal; Alt = alto de pista;
  botón del medio arrastra en ambos ejes; alejar hasta 10 min u 8× el proyecto; sigue al cabezal
  mientras reproduce ◐ (rueda, zoom y alto sí, con anclaje al puntero; falta el botón del medio).
- Altura de la timeline y de las pistas se recuerdan ✗.

---

## 4. Mixer ✅ (`MainWindow.Mixer.cs`; falta probar los medidores con audio de verdad)

### 4.1 Tiras de canal
Una por pista, también las vacías ("vacía"; lo que se edita lo hereda la próxima toma). Ancho Auto /
Compacto / Amplio (recordado). Las perillas y faders aceptan rueda (Shift = fino) y doble clic para
resetear.
- **Amplia** (≥ 250 px): Paneo y un panel por efecto con su switch: Sat (Vida + modo), Comp (LIM,
  cantidad, estilo, medidor de reducción), EQ (resumen; se edita en la Toma), Reverb (envío), Delay
  (envío). Fader con escala en dB y medidor.
- **Compacta** (92 px): perillas Pan / Comp / Rev / Delay, botones M / S / C (C = compresor con barra
  de reducción), fader y medidor.

### 4.2 Retornos y master
- **Reverb**: presets Cuarto / Sala / Salón / Placa / Catedral ("Personalizada" si se tocó), Decay,
  Pre-delay, Tono, Graves, Ancho, mute del retorno y fader.
- **Delay**: presets Slapback / Corchea / Punteada / Ping-pong / Ambiente, Estéreo o Ping-pong, valor
  de nota a tempo o "Libre (ms)" con perilla de tiempo, Repet., Tono, Graves, Ancho, Ducking, mute y fader.
- **Master** con luz LIM.

### 4.3 Plantillas de paneo
En el encabezado: Abanico, Alternado L/R, Distribuido L→R, Centro, con "Apertura". Se aplican a todas
las pistas.

### 4.4 Medidores
Los mismos de la web, con escala en dB y lectura de picos ✅ (degradé verde/amarillo/rojo, -48 a +3 dB;
LIM del master con la reducción del limitador, `MasterLimiter.TakeReductionDb`).

### 4.5 Tempo (`TempoControl`)
BPM tipo FL Studio: arrastrar arriba/abajo la parte entera o la decimal, rueda (Shift = fino), doble
clic para escribirlo, botón **TAP** y tecla **T** (tap tempo) ✗.

---

## 5. Motor de audio

### 5.1 Grafo de mezcla (el mismo para escuchar y exportar)
Por toma: fuente → fade del clip → saturación → compresor (con su EQ de preset alrededor) → EQ de pista →
paneo → fader (volumen/mute) → master; y del fader salen los envíos post-fader a reverb y delay.
Master → limitador (-0,3 dB, con anticipación) → salida. El click y la cuenta no pasan por el limitador
pero suenan con su misma demora.
- Fades de clip ✅ · saturación ✅ · compresor y su EQ ✅ · EQ de pista ✅ · paneo ✅ · fader y
  mute/solo ✅ · envíos y buses de reverb/delay ✅ · limitador master ✅ · click ✅. La reproducción y el
  render offline usan la misma cadena (`TrackDspChain`, `MixGraphBus`) y dan lo mismo (−141 dB). Con el
  transporte parado los buses siguen sonando hasta que se apaga la cola, como en la web.
- **Solo**: las pistas sin solo se silencian para escuchar y exportar; mute/solo afectan solo el audio
  (el video de todas las tomas sale igual) ✅.

### 5.2 Saturación ✅
tanh con ADAA sobre el residuo (la señal limpia pasa intacta; Vida 0% es transparente). Válvula =
curva con bias (pares); Cinta = simétrica con corte de agudos que cierra con el drive y +1,5 dB en
90 Hz. "Vida" fija el drive según el nivel medido de la toma.

### 5.3 Compresor ◐ (presets y EQ del preset portados y verificados; falta la UI y enchufarlos)
Port del worklet ✅ con limitador de pista ✅. Falta: **presets** Rasgueo / Arpegio / Punteo + cantidad,
que calculan umbral, ratio, tiempos, knee y make-up a partir del nivel de la toma (`compPresets.ts`;
editar un valor pasa a "Manual"); la **EQ del preset** (`CompEq`: HPF, barro, presencia, aire); el
compresor **prendido por defecto** en tomas nuevas (Punteo en la segunda pista, Rasgueo en las demás).

### 5.4 EQ de pista ◐ (DSP y presets portados y verificados; falta el editor y enchufarlo)
6 bandas siempre (corte de graves, 4 campanas, corte de agudos; cualquier banda cambia de tipo), cada
una de 2 biquads (24 dB/oct = dos etapas Butterworth). Editor gráfico (`EqEditor`): curva arrastrable,
**espectro en vivo** detrás (FFT de 8192 después del EQ), por banda tipo / on / reset / Frecuencia /
Ganancia / Ancho (Q) / Pendiente (Corte suave / Corte firme); presets Plano, Rítmica, Punteo, Acústica,
Al frente ("Personalizado" si se tocó).

### 5.5 Reverb ◐ (bus y presets portados y verificados; falta enchufarlo y su UI)
Bus: pasa-altos → pre-delay → convolución con impulso sintético con semilla (decay / tono / ancho) →
retorno → master. Igual en vivo y en la exportación.

### 5.6 Delay ◐ (bus, ducker y presets portados y verificados; falta enchufarlo y su UI)
Dos líneas con pasa-altos y pasa-bajos dentro de la realimentación (cada repetición más oscura).
Estéreo: cada lado repite sobre sí mismo, R hasta 12 ms después (× ancho). Ping-pong: entra en mono a L
y rebota L → R → L. Tiempo como nota del BPM o libre en ms; los cambios en vivo se deslizan. Después,
matriz de ancho → **ducker** (baja las repeticiones hasta 24 dB × "duck" mientras suena la señal
directa) → retorno → master.

### 5.7 Entrada y monitoreo ◐
- Ganancia de entrada (+12 dB por defecto), modo de canales Estéreo / Mono 1 / Mono 2, limitador suave
  en la captura ✅ (motor, verificado); medidor de entrada con pico retenido y luz de clip ◐ (medidor sí).
- **Monitoreo por software** opcional (para interfaces sin monitoreo directo) ◐ (motor sí; falta la opción en Configuración).

### 5.8 Grabación ✅
Todo programado sobre un mismo reloj de audio: pistas de fondo, click, cuenta de 4 y el comienzo de la
grabación caen en el mismo cuadro. La toma nueva:
- Llena la pista vacía seleccionada; si no, la siguiente vacía después de la seleccionada; si no hay,
  agrega una al final.
- Nombre "Toma N — Base Rítmica / Solo / Punteo / Arreglo" según el lugar, o el de la pista si tenía uno propio.
- Se corrige con `syncMs = ajuste fino - latencia del equipo` (calibrada o estimada), salvo la primera
  toma sin click ni cuenta.
- Hereda el canal de la pista vacía; compresor prendido con preset según el lugar; espejo según la cámara.
- Se descarta si dura menos de 0,3 s. "Parar sin guardar" la tira.
- Nativa: motor de grabación ✅ (`RecordingSession.cs`: cuenta, mismo reloj, `syncMs`, descarte ≤ 0,3 s;
  cerrar la app grabando conserva la toma; errores de disco y muestras perdidas a la vista), con
  `RecordingWriter` y el diario `*.grabando.json` ✅. Flujo en la ventana ✅ (`MainWindow.Recording.cs`,
  `TakeRecording.cs`, `gclab proyecto toma` / `verificar-tomas`): R, cuenta, destino, nombre, canal,
  descarte, deshacer, cerrar la ventana grabando guarda la toma. Con cámara, el video va al lado
  (`media/{id}.mp4`, §11) con la latencia de cámara y el espejo del momento. Diferencia: sin cámara (o si
  no manda imagen, con aviso) la toma es solo audio y una pista nueva no ocupa panel (la web no deja
  grabar sin cámara).
- **Sincronía por toma**: en la nativa, cada clip puede tener su propio ajuste de sincronía y de latencia de
  cámara (`Clip.SyncMs` / `VideoOffsetMs`, si faltan valen los de la grabación); entra en deshacer, como en la web ✅.

### 5.9 Calibración de latencia ✅ (en Configuración → Sincronización)
En Configuración → Sincronización. **Con cable** (pulsos de ruido con semilla y correlación cruzada;
cable de salida a entrada o micrófono frente a los parlantes) o **Tocando** (4 de cuenta + 12 clicks;
detecta tus ataques). Guarda mediana y dispersión por par entrada + salida, con la latencia estimada y
el sample rate de ese momento, para avisar si el equipo cambió. "Descartar la medición y usar lo que
informa el navegador" (en nativo: lo que informa el driver ASIO).
- Nativa: la medición (con cable y tocando), el guardado y el aviso de equipo cambiado ✅
  (`LatencyCalibration.cs`, `gclab latencia`); falta la pantalla de Configuración ✗.

---

## 6. Color y efectos de video ✅ (pestaña Color, preview y exportación con la misma LUT)

Pestaña **Color** de la barra derecha, secciones Plantillas / Filtro / Ajustes / Efectos, todo deshacible.
- **Filtro (look)**: Ninguno, Brujo, Sepia, B/N, Noir, Cálido, Frío, Cine, Vintage, Película,
  Blockbuster, Bleach, Dorado, Noche, Nórdico, Neón, Sci-fi, Desierto, con **Intensidad**.
- **Ajustes** (-100 a 100): Exposición, Contraste, Luces, Sombras, Saturación, Vibrancia, Temperatura,
  Tinte, Desvanecer (0 a 100); "Volver todos los ajustes a 0"; "Ver sin color / Aplicar color".
- Todo es una función por píxel cocinada en una **LUT 3D de 33³**: la preview la aplica en la GPU con
  interpolación trilineal y la exportación escribe la misma LUT como `.cube` (`lut3d`), así son idénticas.
- **Efectos**: Viñeta y Tamaño (la misma máscara en preview y export), Grano (ruido de luma, nuevo por
  cuadro; iguales en intensidad, no grano por grano), Barras de cine (Sin barras / 1.85 / 2.00 / 2.39);
  "Ver sin efectos / Aplicar efectos".
- **Plantillas de video** (filtro + ajustes + efectos), del programa; mismo nombre = actualizar. Nativa:
  `%APPDATA%\GuitarCam Studio\plantillas-video.json` (`VideoTemplates.cs`), `gclab proyectos plantillas-video…`.
- Orden por toma: LUT → grano → espejo → escala/recorte → viñeta → barras → padding → fades.

---

## 7. Configuración (`SettingsDialog`) ✅ (falta el botón del panel del driver)

Diálogo de ajustes del programa (no del proyecto), con los atajos apagados mientras está abierto.
- **Entradas y salidas**: interfaz (refrescar dispositivos), canales (Estéreo: Input 1 → L, Input 2 → R;
  Mono 1; Mono 2), ganancia de entrada, salida ("Predeterminada de Windows"; no se cambia mientras se
  graba o calibra), monitorear la entrada por esa salida. En nativo: driver ASIO, canales del driver y
  "panel del driver" (tamaño de buffer), como en `gclab panel`.
- **Cámara**: Webcam / Driver o IP DroidCam (IP y puerto, por defecto 192.168.1.50:4747); resolución
  Auto (nativa) / 1080p / 720p / 480p a 30 fps; espejar la imagen en vivo.
- **Sincronización**: estado (latencia calibrada / estimada), Calibrar latencia (§5.9), ajuste fino de
  audio para tomas nuevas, latencia de cámara por defecto (80 ms).
- Nativa (`Dialogs/SettingsDialog.axaml(.cs)`, botón ⚙): las tres pestañas con la forma y los textos de la
  web. Diferencias: "Interfaz" lista los drivers ASIO y "Dispositivo" los pares de salida del driver (no
  hay "Predeterminada de Windows"); donde la web dice "navegador" dice "driver"; driver y salida se
  recuerdan (`audio.driver`, `audio.outputOffset` en `config.json`), el resto vive en memoria como en la
  web. Cámara: la lista es la de DirectShow por FFmpeg (`Video/LiveCamera.cs`); como en la web, la cámara,
  la resolución, la IP, el espejo y la latencia de cámara viven en memoria y al abrir se vuelve a elegir
  la cámara por la preferencia de la web. Falta el botón del panel del driver (tamaño de buffer, hoy `gclab panel`).

---

## 8. Exportación ◐ (motor, diálogo y `gclab exportar` ✅; falta compararlo con la web lado a lado)

- **Diálogo de configuración**: nombre del archivo (arranca en uno libre, "nombre (2)"), carpeta (la
  `exports` del proyecto o otra elegida; "Esa carpeta ya no existe. Elegí otra."), calidad **Alta** (CRF
  18, para publicar) / **Estándar** (CRF 22) / **Borrador** (CRF 28). Si ya existe: "Reemplazar y
  exportar". Carpeta y calidad se recuerdan por proyecto.
- **Progreso** en etapas: Mezclando el audio → Enviando la mezcla → Renderizando el video → Guardando
  el archivo, con tiempo, fps, velocidad y lo que falta; cancelable ("¿Cancelar la exportación?").
- **Listo**: "Video exportado", mostrar en la carpeta, copiar la ruta. **Error**: "No se pudo exportar"
  con el detalle.
- **Cómo funciona**: la mezcla se renderiza offline con **el mismo grafo** que la reproducción (sin el
  retardo del limitador, para que quede alineada). FFmpeg arma un solo `filter_complex`: lienzo de
  fondo, cada toma recortada y sincronizada (`audioOffsetMs - videoOffsetMs`), con LUT, grano, espejo,
  zoom/paneo, viñeta, barras, padding y fades, superpuesta en su panel; audio = la mezcla, sin procesar.
  libx264 + AAC 320k, hasta el final del último clip. Escribe un `.part` oculto que se renombra al
  terminar; cancelar mata FFmpeg y lo borra. Queda registrada en el proyecto.
- En nativo el render offline y FFmpeg corren en el mismo proceso, sin servidor ni subida.
- Nativa: render offline de la mezcla con el mismo grafo que la reproducción ✅ (`MixRenderer`,
  `gclab mezclar`; los medios se remuestrean a 48 kHz con `AudioLoader`) ✅. `Mp4Exporter`: el mismo
  `filter_complex` que `server/index.js` (LUT de color y viñeta idénticas byte a byte a las de la web),
  progreso, cancelación y `.part` ✅; `gclab exportar` ✅. El diálogo (`Dialogs/ExportDialog`) está hecho
  y se abre desde "Exportar MP4" ✅; falta la captura lado a lado con la web ◐.

---

## 9. Deshacer / rehacer ✅

Fotos del estado editable (pistas, formato, aspecto, reverb, delay, color, efectos) antes de cada
edición; ediciones seguidas con la misma clave en menos de 800 ms (arrastrar un slider) son un solo
paso. Se vacía al cambiar de proyecto y después de borrar no usadas.

---

## 10. Teclado

| Tecla | Acción | Nativa |
|---|---|---|
| Espacio | play / pausa (grabando: detener y guardar) | ◐ |
| R | grabar / detener; durante la cuenta, cancela | ✅ |
| Esc | cancelar la cuenta | ✅ |
| T | tap tempo | ✗ |
| C / Shift+C | click / cuenta | ◐ (C sí) |
| M / S | mute / solo de la pista seleccionada | ✅ |
| Supr | quitar el clip (la pista queda vacía); en una pista vacía, quitar la pista | ✗ |
| X | timeline / mixer | ✅ |
| Inicio | al principio | ✅ |
| Ctrl+Z / Ctrl+Y | deshacer / rehacer | ✅ |
| F2 | renombrar (en el gestor) | ✗ |

Los atajos no corren mientras se escribe en un campo ni con Configuración abierta.

---

## 11. Otros

- **Cámara IP (DroidCam)**: el MJPEG se lee, se dibuja en un lienzo y de ahí se graba ✅ (FFmpeg lee
  `http://IP:4747/video`, la misma dirección que arma el proxy de la web; sin probar con un celular).
- **Grabación de video**: H.264 en MP4 cuando se puede (lo decodifica la GPU), keyframe cada 1 s para
  que el scrub no se trabe, 8 Mbps ✅ (`CameraCapture.cs` + `VideoTakeWriter.cs`). Diferencias: un solo
  FFmpeg lee la cámara para el monitor y la toma; el video arranca en la muestra exacta en que arranca el
  audio (la web lo arranca con un temporizador) y cada cuadro cae en el lugar que dice su hora de llegada,
  así no se corre aunque la cámara vaya a 29,97; mientras graba es MP4 fragmentado, así un corte no lo
  pierde.
- **Video al reproducir y al mover el cabezal**: cada toma sigue al cabezal ✅ (decodificador FFmpeg).
- **Miniaturas** de la tira de video, generadas y cacheadas por toma ✗.
- **Selector de carpetas moderno de Windows** y "Mostrar en el Explorador" trayendo la ventana al frente ✗.
- **Papelera de reciclaje** para todo lo que se borra (nunca borrado definitivo) ✗.
- **Lanzador**: acceso directo con ícono que levanta la app y la cierra con la ventana. En nativo es
  directamente el `.exe`; falta el ícono y el acceso directo ✗.

---

## Orden sugerido

1. **Gestor de proyectos** igual al de la web, con plantillas (§2.1, §2.2): hoy es lo más distinto.
2. **Deshacer/rehacer** y **edición de clips en el timeline** (§9, §3): base para editar sin miedo.
3. **Completar la mezcla**: fades, EQ, presets del compresor y su EQ, reverb, delay, envíos (§5), con
   `gclab verificar` para cada port.
4. **Inspector completo** (§1.2) y **Mixer** (§4), con tempo y transporte completos (§1.6, §4.5).
5. **Exportación** (§8) con color y efectos (§6): cierra "mezclo y exporto" para proyectos importados.
6. **Grabación** (§5.8), calibración (§5.9), monitor en vivo (§1.5), cámara (§11) y Configuración (§7).
7. **Historial** (§2.3), barras laterales plegables, medidor del sistema y tema (§1.1).
