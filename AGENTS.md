# Guía para agentes — GuitarCam Studio Native

Instrucciones para cualquier agente de IA (Claude, Codex, Copilot, Cursor…) que siga con este proyecto.
Leela entera antes de tocar código. Describe cómo está hecha la app hoy: si algo de acá deja de ser
cierto, actualizá este archivo en el mismo cambio.

Para el contexto general mirá [README.md](README.md). Lo que falta hacer está en [PENDIENTES.md](PENDIENTES.md),
y la captura de referencia de la interfaz, en [docs/captura.png](docs/captura.png).

---

## 1. Qué es y qué no es

- **GuitarCam Studio** sirve para hacer **videos de guitarra**: grabar tomas con cámara y audio,
  mezclarlas y exportar un video con un panel por toma. Esta carpeta es la app **.NET nativa**
  y se llama **GuitarCam Studio Native** (en la barra de título, "GuitarCam Studio" + el badge "NATIVE"; el
  exe sigue siendo `GuitarCam.exe` y los ajustes, `%APPDATA%\GuitarCam Studio`).
- **El foco manda.** Las funciones de DAW (clips, plugins, MIDI…) tienen que servir para el video y no
  pueden complicar el camino de **"grabo, mezclo y exporto"**. Si una función nueva agrega pasos o
  pantallas a ese camino, replanteala o preguntá.
- **La app web ya no es referencia.** Nació como port de la web (`..\GuitarCam`), pero hoy la nativa
  decide sola: sus decisiones de diseño, comportamiento y formato las toma el usuario con este proyecto, y
  lo que haga o no la web no cuenta. Las menciones a la web que quedan en el código y en las notas son
  historia (de dónde salió algo), no reglas. Ante la duda, mirá cómo está hecho en la nativa y preguntá.

## 2. Idioma

| Qué | Idioma |
|---|---|
| Textos de la interfaz, mensajes de estado, tooltips | **Español rioplatense, con voseo** ("Abrí", "Elegí", "usá", "probalo") |
| Comandos y opciones de `gclab` | Español (`grabar --segundos 15 --vida 0.3`) |
| README, PENDIENTES, HECHO, esta guía | Español |
| Identificadores, comentarios y `<summary>` en el código | Inglés |
| Mensajes de commit | Inglés, en imperativo, una línea descriptiva ("Decode real video in the preview with FFmpeg") |
| Valores que se guardan en el JSON | Inglés (`"tube"`, `"tape"`, `"side-by-side"`, `"rhythm"`…) |

**Multilenguaje** (solo nativa, pedido del usuario; hecho): los textos de la interfaz salen de `Loc`
(`Loc.T("clave")`, o `{loc:T clave}` en XAML) y viven en `Assets/Lang/<código>.json`; **es.json es la
referencia** (lo que falta en otro idioma cae al español). El idioma se elige en Configuración → "Idioma",
se guarda en `config.json` (`language`) y se aplica al reiniciar. Toda la ventana (`GuitarCam.App`) usa
claves, diálogos y atajos incluidos. Las capas `Audio` y `Projects` no conocen `Loc`: sus mensajes de error y de aviso
(FFmpeg, cámara, guardado, exportación, recuperación) pasan por `Texts.T(clave, español, args...)`, que vive en `GuitarCam.Audio`
y que `Loc.Load` conecta; sin esa conexión (gclab) sale el español de siempre. Con prefijos `err.`, `store.`, `camera.`.
Siguen en español, a propósito, las plantillas que el usuario ya guardó, los nombres que se crean ("Canal 1") y los textos de `gclab`. Los nombres que vienen del
motor en español (estilos del compresor, presets de EQ, reverb y delay, divisiones) se traducen al mostrarlos
con `Loc.Or(clave, texto del motor)`: `Audio` no conoce `Loc`. Los títulos de
sección del inspector, de Color y de Historial son ids estables en español (`SectionTitle` y
`bar.section.*` los traducen solo al mostrarlos), porque con ellos se recuerda qué secciones están
plegadas. `Projects` no conoce `Loc`: sus frases (Actividad, días, "hace 5 min") pasan por `Texts.T(clave,
español)`, que `Loc.Load` conecta; sin esa conexión (gclab) queda el español de siempre. Texto nuevo → clave nueva
en todos los idiomas. Tono: claro y corto en botones, etiquetas y tooltips; el humor (suave, nunca tosco)
va en estados vacíos, avisos y confirmaciones, y nunca cuando el usuario puede perder algo.

El vocabulario de la interfaz es de músico, no de programador: **canal**, **pista**, **click**, **Vida**
(cantidad de saturación), **Válvula / Cinta**, **Rasgueo / Arpegio / Punteo** (presets del compresor),
**Ver todo**. Los niveles de "Vida" se nombran Apenas / Sutil / Con vida / Caliente / Al palo.

## 3. Arquitectura

```
src/
  GuitarCam.Audio      motor de audio: DSP portado, ASIO dúplex, click, grabación a disco. Sin UI.
  GuitarCam.Projects   modelo del proyecto, guardado atómico, historial. Sin UI.
  GuitarCam.App        la ventana (Avalonia 11.3). Depende de Audio y Projects.
  GuitarCam.Lab        gclab.exe: consola para probar audio, DSP y proyectos sin la ventana.
tools/make-icon.ps1    dibuja el ícono (Assets/GuitarCam.ico: exe, ventana y barra de tareas; Assets/GuitarCam-64.png: logo de la barra de título).
tools/make-shortcut.ps1  crea el acceso directo del escritorio al exe.
tools/make-track-icons.py  genera Controls/TrackIcons.Data.cs con los íconos de instrumentos (IconOven, MIT).
```

Reglas:

- Las dependencias van **hacia abajo**: App → Projects → Audio. `Audio` y `Projects` no referencian
  Avalonia ni nada de la UI; todo lo que se pueda probar desde `gclab` tiene que vivir ahí.
- Cada función nueva del motor o del proyecto se puede ejercitar desde **`gclab`** (un comando o una
  opción nueva) antes o a la vez que desde la ventana. La ayuda de `gclab` se actualiza con cada comando.
- Plataforma: `net10.0-windows`, x64, `Nullable` habilitado. Audio por **ASIO** (NAudio), video con
  **FFmpeg/ffprobe** por proceso y pipe (tienen que estar en el PATH).
- No hay MVVM ni bindings: la ventana es code-behind con controles con nombre (`x:Name`). Seguí ese
  estilo; no metas un framework de MVVM sin que el usuario lo pida.

### Modelo del proyecto (`GuitarCam.Projects`)

Tres capas:

- **Media**: grabaciones y archivos importados. **Nunca se reescriben.**
- **Track**: el canal (volumen, paneo, mute/solo, saturación, compresor…), si lleva panel de video, y su color
  (`Color`, "#rrggbb") e ícono de instrumento (`Icon`), null = sin color / con su número.
- **Clip**: un pedazo de un media puesto en una pista (`AtSec`, `TrimStartSec`, `TrimEndSec`, `GainDb`, encuadre).

Convenciones:

- Tiempos del timeline en **segundos**; correcciones de latencia/sincronía en **ms** (como en la web).
- JSON en camelCase (`ProjectStore.Json`). Cada clase lleva `[JsonExtensionData] Extra`: lo que la app
  no conoce se conserva y se vuelve a escribir tal cual. No lo saques.
- `Normalize()` rellena lo que falte o venga roto para que el resto de la app nunca vea `null`. Todo
  campo nuevo tiene un valor por defecto y se normaliza ahí.
- Si cambiás el formato de forma incompatible, subí `Project.CurrentVersion` y migrá al abrir.
- IDs con prefijo (`Ids.New("prj")`).

### Carpeta del proyecto y guardado

```
guitarcam.json        el proyecto
guitarcam.json.bak    el guardado anterior
guitarcam.lock        mientras está abierto (una segunda ventana lo abre en solo lectura)
historial/            copias para volver atrás
media/                grabaciones e importados
exports/
```

- Todo guardado es **atómico**: archivo temporal → forzado a disco → `File.Replace`. Nunca escribas
  `guitarcam.json` directo.
- La ventana guarda sola **1,5 s después de la última edición** (`MarkDirty()` → `SaveSoon()`): el
  proyecto se serializa en el hilo de la UI y se escribe en segundo plano, en orden. Ctrl+S fuerza.
  Antes de cerrar o cambiar de proyecto, `FlushSave()`.
- Toda edición del usuario pasa por `MarkDirty()`. No hay botón de "guardar" que el usuario tenga que acordarse de tocar.
- Perder una toma es el peor error posible. `RecordingWriter` escribe la grabación mientras ocurre y
  reescribe el encabezado cada ~2 s para que un corte no la pierda. Cualquier cosa nueva que toque
  archivos del usuario tiene que pensar en qué pasa si el programa muere a la mitad.

## 4. Audio en tiempo real

**Nivel profesional** (pedido del usuario, 2026-10-24): todo lo que toque el motor de audio se hace como en un DAW
profesional (Logic Pro, Fender Studio Pro). Nada de clics ni cortes: arrancar, parar, saltar y hacer scrub llevan su
fundido (play / stop / scrub, del largo que se elige en Configuración, `PlaybackEngine.TransportFadeMs`; los saltos,
crossfade fijo de 10 ms; `Stop`/`Pause` cortan en el acto y son solo para grabar, exportar o cambiar de proyecto); el estado de los efectos no se reinicia en medio de la música; lo que se dude, se mide (`AudioGapLog`) en vez
de suponerlo. Antes de dar algo por hecho, pensá cómo lo resuelve un DAW profesional y hacelo a ese nivel.

El callback de ASIO (`AsioDuplex.ProcessBlock`, `PlaybackEngine.Process`) corre en el hilo del driver:

- **No asignes memoria, no bloquees, no uses locks, no toques disco ni la UI** dentro del callback. LINQ
  que asigna, `async`, logs y excepciones también quedan afuera.
- Buffers preasignados (8192 muestras por canal en el motor y en cada `LiveTrack`).
- Para pasarle datos nuevos al motor se **reemplaza la referencia entera** (`volatile LiveTrack[] tracks`)
  en vez de modificar la colección. La posición va con `Interlocked`.
- Los medidores son picos que escribe el hilo de audio y lee/resetea la UI en su `Tick` (16 ms). La
  caída visual (×0,82 por tick en pistas, 24 dB/s en el medidor estéreo) se hace del lado de la UI.
- Lo que va a disco sale del callback por otro hilo (ver `RecordingWriter`).
- **La basura de la UI también corta el audio**: el callback es código .NET, así que cada GC lo frena. Los gen0
  no se notan, pero un gen1 deja huecos de 10–15 ms (con buffers de 4 ms, un corte). En lo que corre en cada `Tick`
  o en cada `Render` no se asigna: nada de reconstruir controles (el mixer arma sus dos vistas una vez y alterna),
  ni de `new Pen`/`FormattedText`/pinceles por cuadro (ver [Controles dibujados a mano](#controles-dibujados-a-mano)).
- **Nada se compila en el callback**: `AudioWarmUp` precompila al arrancar todo `GuitarCam.Audio`, `GuitarCam.App.Audio` y
  `GuitarCam.App.Model`, y compacta la memoria al terminar de abrir un proyecto. El código de tiempo real nuevo va en esos
  namespaces (o se suma a la lista); los métodos genéricos no se precompilan, así que en el callback no se usan.
- Sample rate del motor: 48 kHz (`PlaybackEngine.SampleRate`).
- `PlaybackEngine` muestra la carga del callback ("DSP x %" con su barrita, arriba junto a CPU / GPU / RAM; roja desde el 80 %): si un cambio la sube mucho, decilo.

Cadena por pista (como en la web, `buildTrack` de `mixGraph.ts`): clips (con fades y ganancia) →
saturación → EQ del preset antes (corte de graves, barro) → compresor → EQ del preset después (presencia,
aire) → EQ de pista → paneo (`StereoPannerNode` de Web Audio) → fader → suma; del fader salen los envíos
post-fader a los buses de reverb y delay, que vuelven al master → limitador master. El click va por
fuera del limitador, atrasado la misma latencia. La cadena (`TrackDspChain`) y los buses (`MixGraphBus`)
viven en `GuitarCam.Audio/Dsp/MixRenderer.cs` y los usan igual la reproducción y el render offline de la
exportación: no los dupliques. Los medios se cargan y remuestrean a 48 kHz con `AudioLoader`.

### DSP

- El DSP nació como port de los worklets de la web; cada clase dice de dónde viene en su `<summary>`, pero
  eso es historia: hoy el código de la nativa es la referencia y se puede cambiar de común acuerdo con el usuario.
- `gclab comprobar-dsp` corre comprobaciones propias del DSP (remuestreo, tomas, reinicio de la cadena y los buses).

## 5. Diseño de la interfaz

### Principios

- **Oscura, sobria, de estudio.** Fondo casi negro, superficies apenas más claras, un solo color de acento.
  El contenido (el video y las ondas) es lo que tiene que resaltar, no los controles.
- **Coherente consigo misma.** Los tokens, tamaños y comportamientos son los de esta guía. Ante la duda,
  mirá cómo lo hace el resto de la nativa.
- **Se usa con la guitarra colgada**: atajos de una tecla, objetivos de clic cómodos, nada que exija
  precisión ni varios pasos para grabar o reproducir.
- **Siempre se ve el estado**: qué está sonando, si hay audio, si un archivo falta, si se guardó.
- Lo que todavía no está hecho se muestra deshabilitado o con un aviso ("Exportar todavía no está en la
  versión nativa"). Nunca un botón que no hace nada y no dice nada.

### Colores (tokens)

Están definidos **dos veces y tienen que coincidir**: en `App.axaml` (para XAML) y en
`Controls/Palette.cs` (para los controles que dibujan). Si agregás o cambiás uno, cambiá los dos (y que
coincida con la web). Los valores de la tabla son los del tema por defecto (cian sobre Grafito): el selector
de tema (`UiTheme.cs`, port de `theme.ts`) cambia en vivo los pinceles de superficie y de acento. La nativa
amplía el catálogo de la web a 16 acentos y 9 fondos oscuros, a pedido del usuario. Por eso:
usá siempre el pincel (`Palette.Accent`, `{StaticResource Accent}`) y no un color escrito a mano; leé
`Palette.AccentColor` y compañía cuando dibujás, no lo guardes; las variantes del acento en XAML son
`AccentFg` (texto sobre acento), `AccentHover`, `AccentTint` y `Accent25/40/50/70` (alfa).

| Token | Valor | Uso |
|---|---|---|
| `App` | `#0b0b0d` | fondo de la ventana, del timeline, de la preview; texto sobre acento |
| `Panel` | `#131316` | barras (título, transporte de abajo), inspector, encabezados de pista |
| `Raised` | `#1c1c20` | secciones, chips, botones, pista seleccionada |
| `Hover` | `#26262b` | hover; fondo de toggles apagados y badges |
| `Line` | `#27272c` | todos los bordes y separadores (1 px) |
| `Fg` | `#ececef` | texto principal |
| `Dim` | `#a1a1aa` | texto secundario, valores, íconos |
| `Faint` | `#6b6b74` | etiquetas en mayúscula, ayudas, deshabilitado |
| `Accent` | `#22d3ee` | **el** color: activo, seleccionado, play, playhead, ondas, medidores |
| `AccentSoft` | `#2422d3ee` | fondo de chips activados y del badge "NATIVE" de la barra de título |
| `Rec` | `#ef4444` | grabar y clip/saturación de nivel. Nada más |
| `RecSoft` | `#24ef4444` | fondo suave asociado a grabación |
| `Composition` | `#09090b` | fondo del cuadro del video |
| ámbar | `#f59e0b` | mute activo y zona de −6 a 0 dB en medidores |
| gris offline | `#71717a` | clips cuyo archivo falta |
| `InTune` | `#22c55e` | "afinado" en el afinador. Es el verde de los medidores del mixer (`Palette.MeterGreen`), no uno nuevo |

Reglas: no inventes colores nuevos para estados nuevos sin necesidad; el acento no se usa como
decoración; el rojo significa **grabación o peligro de clip**, nunca "error genérico". Las variantes con
transparencia salen de `Palette.Alpha(color, a)` (por ejemplo, relleno de clip 0,10 / 0,16 seleccionado,
borde 0,45 / 0,9). El hover del acento es `#4fdcf1`. El tema es siempre oscuro (`RequestedThemeVariant="Dark"`).

### Tipografía

- UI: **Segoe UI Variable Text** (fallback Segoe UI). Números y tiempos: **Cascadia Mono** (fallback Consolas).
  Íconos: **Segoe Fluent Icons** (fallback Segoe MDL2 Assets), como `TextBlock` con clase `icon` y el
  glifo en `Text` (`&#xE768;`). No agregues paquetes de íconos ni imágenes para íconos. Excepción: los
  íconos de los efectos (Vida/saturación, compresor, EQ, reverb, delay) salen siempre de `FxIcon`
  (`FxIcon.Sat`…), que dibuja los trazos de lucide de la web (`fxIcons.ts`). Usalo en todo lugar que
  muestre un efecto, para que el inspector y el mixer no vuelvan a tener íconos distintos. Otra excepción: los
  íconos de instrumentos de las pistas (pedido del usuario) salen de `TrackIcons`, con los trazos de IconOven (MIT,
  24×24 como lucide; licencia en `Assets/Icons/IconOven-LICENSE.txt`, generados con `tools/make-track-icons.py`).
- Escala usada (no inventes tamaños intermedios):

| Tamaño | Uso |
|---|---|
| 10.5 SemiBold, mayúsculas, espaciado 0.8, `Faint` | etiquetas de sección (`TextBlock.label`: "MEZCLA", "PISTAS", "BPM") |
| 11 | info secundaria chica, textos dentro del timeline |
| 12 `Dim` | texto secundario (`TextBlock.dim`), chips |
| 12.5 SemiBold | nombre de pista, botón de acento |
| 13.5 SemiBold | nombre de la app en la barra de título |
| 14 / 19 mono | BPM / reloj del transporte |
| 16 SemiBold | título del inspector (nombre de la toma) |

- **Todo valor numérico va en mono** (`Classes="dim mono"`), alineado a la derecha de su etiqueta.

### Formato de valores

Usá exactamente estos formatos (respetan la cultura del sistema, así que en es-AR salen con coma):

- Ganancia: `+3,5 dB` / `-2,0 dB` / `0,0 dB` → `$"{db:+0.0;-0.0;0.0} dB"`; volumen cero: `-∞ dB`.
- Paneo: `Centro`, `30 L`, `30 R` en el inspector; `C`, `L30`, `R30` compacto en el encabezado de pista.
- Tiempo: `mm:ss.fff` en el reloj; compás.tiempo (`2.2`) al lado.
- Porcentajes con palabra: `50% · Con vida`.
- Separador de datos en una línea: ` · ` (punto medio con espacios).
- Fechas relativas en el menú de versiones: "recién", "hace 5 min", "hoy 14:30", "ayer 09:10", `dd/MM HH:mm`.
- `gclab` usa cultura invariante (punto decimal) a propósito.

### Formas y espaciado

- Radios: **8** secciones, **6** botones/chips/cajas, **5** clips y badges, **4** toggles M/S y etiquetas,
  **2** barras de medidor.
- Bordes de 1 px en `Line`. Sin sombras, sin gradientes, sin animaciones decorativas.
- Padding de sección `12,10`; separación vertical entre bloques 10–12; márgenes del inspector `14,16`.
- Alturas mínimas: chips 30, transporte 36×36.

### Estructura de la ventana

```
┌ barra de título propia (44) ─ logo · chip del proyecto (guardado) ▾ · Video|Solo audio · deshacer/rehacer · estado ···· CPU │ GPU │ RAM │ DSP │ cámara │ FFmpeg · tema · ⚙ · [Exportar MP4] ┐
├ inspector (290) ─┬─ preview (un panel por pista con video) ────────┬─ "En vivo" (256) ─┬─ proyecto (290, plegable a 44) ┤
│ TOMA + secciones │                                                 │ cámara + entrada  │ pestañas Color / Historial     │
├──────────────────┴─ separador arrastrable ─────────────────────────┴───────────────────┤                                │
│ timeline (280): regla (32) · encabezados fijos (324) · un carril por pista (64, 40–160) │                                │
├ transporte, siempre abajo (~52) ────────────────────────────────────────────────────────────────────────────────────────────┤
│ ·························· medidor de entrada (kHz · buffer debajo) · Monitor  [ reloj │ ⏮ ■ ▶ ● loop │ Compás Tempo TAP Cuenta Click ▾ ] ··· Timeline/Mixer · Ver todo │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Transporte abajo, al estilo Fender Studio Pro** (pedido del usuario; en la web va arriba del timeline):
  celdas planas con el valor arriba y una etiqueta chica abajo. A la izquierda, pegados al recuadro del medio,
  el medidor de entrada (del ancho del texto de abajo y con su escala en dB, `StereoMeter.ShowScale`) con
  "Entrada 1-2 · 48 kHz · 192 muestras (4,0 ms)" chiquito debajo (el driver va en el tooltip) y el botón **Monitor** (`ToggleButton.cell`, el monitoreo por software de Configuración); a la derecha
  la vista, y en el medio **un
  solo recuadro oscuro** (`Border.metro`) con reloj │ transporte (`Button.tbtn`, también Grabar: un ● rojo sin
  texto, pedido del usuario; en la web es un chip grande con la pista destino) │ metrónomo,
  separados por `Border.vsep`. Ese recuadro queda **siempre centrado en la ventana**: columnas `*,Auto,*`;
  la carga DSP subió a la barra de título (pedido del usuario), con la misma forma que CPU / GPU / RAM
  ("CPU 19% ▬", con líneas `Border.tsep` entre los grupos).

- El logo de la barra de título es el ícono de la app y abre "Acerca de" (`Dialogs/AboutDialog`, solo de la
  nativa: la web no tiene). La barra de título es la de la app (`ExtendClientAreaToDecorationsHint`), arrastrable, doble clic
  maximiza; los botones del sistema quedan a la derecha (margen 150). `TransparencyLevelHint="None"` no se
  toca: con transparencia, píxeles oscuros del video dejan ver lo que hay detrás.
- Maximizada, Windows corre la ventana 8 px afuera de cada borde de la pantalla (por la barra de título
  propia). La grilla raíz lleva `Margin="{Binding $parent[Window].OffScreenMargin}"` para compensarlo; no
  lo saques o se cortan los costados y la barra de transporte.
- **Avisos** = la campanita de la barra de título (`Controls/NotificationBell`, a la izquierda del tema). `Status(texto)` agrega el mensaje a su lista (los últimos 50, el más nuevo arriba); un punto de acento marca los sin leer y abrirla los marca como leídos. Nada se escribe en el centro de la barra de título ni en diálogos. **Toasts** (`Controls/ToastHost`, solo nativa, pedido del usuario): `Toast(texto)` es para lo que conviene ver en el momento; sale abajo a la derecha, encima de todo, se va sola a los 6 s (el puntero encima la frena) y también queda en la campanita. Una fija (`sticky`) se va solo con su ×: la usa `--probar "texto"` ("Para probar"), que el agente pasa al abrir la app después de cada cambio.
- **Inspector**: número + nombre editable de la toma (`TextBox.title`), los datos del archivo como badges
  (`Hover`, mono) y un botón que pliega o despliega **todas** las secciones (pedido del usuario; en la web
  solo los efectos). Después, una tarjeta (`Border.section`) por bloque, cada una con ícono +
  etiqueta en mayúscula y, si se puede apagar, un ON/OFF a la derecha. Apagada → los controles quedan al
  40 % de opacidad, pero se pueden seguir tocando. La cantidad principal de cada efecto va en el knob
  grande del compresor (`EffectKnob`: Vida, envío a reverb, envío a delay; pedido del usuario, en la web
  son sliders); los ajustes finos siguen en sliders. Todo encabezado plegable de la app (inspector, Color,
  Historial) es un `Controls/SectionHeader`: la franja entera reacciona al hover y se pliega con un clic
  en cualquier parte fuera de sus botones (pedido del usuario). Con su encabezado "Toma"
  se pliega a una columna de íconos de 44, como la barra del proyecto, y se recuerda. El espectro en vivo
  del EQ sale de un `SpectrumTap` colgado solo de la pista seleccionada mientras el EQ está a la vista: el
  hilo de audio copia a un anillo preasignado y la FFT corre en el `Tick` de la UI.
- **Mixer** (`MainWindow.Mixer.cs`): ocupa el lugar del timeline (X o "Timeline / Mixer" abajo a la
  derecha) con su propia altura (mínimo 250). Una tira por pista, también las vacías; amplias o
  compactas ("Auto" según el ancho, como la web). Las ediciones de canal pasan por `EditTrack`, las de
  los buses por `BusEdit`. Los medidores del mixer usan el degradé de la web (verde → amarillo → rojo,
  `Palette.Meter*`), distinto del `StereoMeter`. Los desplegables son `SelectButton`, no `ComboBox`.
  Cada canal es una tarjeta (`StripCard`) y todos (pistas, retornos, master) se arman con `StripLayout`,
  que les da las mismas filas: así los faders y medidores tienen siempre la misma altura. Lo que vaya en
  la cabecera o en los controles tiene que entrar en su fila fija, no estirarla. Los paneles de efecto
  angostos se reacomodan (`FxNarrowW`) en vez de recortar controles. En las tiras amplias la cabecera (● M S) y el paneo quedan
  fijos y solo scrollean los efectos, cada uno en su panel (pedido del usuario); en las compactas el ● va debajo del
  nombre, porque ● M S C no entran en una fila.
- **Barra del proyecto** (derecha, `MainWindow.Color.cs`): lo que es del proyecto, como en la web
  (lo de la pista va al inspector). Ocupa la altura de la preview y el timeline, se pliega a una columna
  de íconos y recuerda pestaña y secciones plegadas. Las ediciones de Color pasan por `ColorEdit(...)`.
  Las pestañas Color / Biblioteca / Historial: Biblioteca (las tomas, usadas o no, importadas o grabadas, y "Borrar no usadas") e Historial (exportaciones y actividad) viven en `MainWindow.History.cs` (Historial es la de por defecto) y se arman solo mientras alguna está a la vista
  (`RefreshHistory`); lo que hace con el proyecto y los archivos vive en `ProjectHistory` (Projects).
  "Salió / volvió al timeline" se registra al guardar (`ProjectStore.Serialize` → `LogTimelineChanges`).
- **Monitor "En vivo"** (`Controls/LiveMonitor.cs`), a la derecha de la preview: la cámara cruda, nunca
  la composición. La cámara la abre `Video/LiveCamera.cs` (un solo FFmpeg, `CameraCapture`), que también
  alimenta el video de la toma (`VideoTakeWriter`).
- **Preview**: los paneles se calculan con `PanelLayout.Compute` sobre un lienzo de 1080 px de alto,
  **la misma cuenta que usa la exportación** (port de `panelLayout.js`). Lo que se ve tiene que ser lo que
  se exporta. Encuadre: cover-fit + zoom/paneo/espejo del clip. Etiqueta con el nombre de la pista abajo a la izquierda.
- **Encabezados de pista** (solo nativa, pedido del usuario): la columna se ensancha arrastrando su manija (una sola franja de
  `GripW` = 8 px adentro del borde derecho, de todo el alto, con dos rayitas en el medio; nunca pisa un clip en 0 s) (324–460, se recuerda en `headerWidth`; las filas se arman para 264 y el sobrante va al volumen). Con carriles más altos aparecen debajo, de a
  una fila de 22 px, Vida · Comp · Reverb · Delay (ícono `FxIcon`, clic prende/apaga, arrastrar el slider cambia la
  cantidad, doble clic la restablece), también en pistas vacías (`TimelineView.EffectRequested`). Tocar cualquier
  parte del encabezado (la manija o un control, menos "quitar") selecciona la pista. Al reordenar con la manija, la
  pista sigue al puntero por encima de las demás y el resto se desliza a su lugar (`shownSlot`, `Slide`; la web salta).
- **Color e ícono de pista** (solo nativa, pedido del usuario): cada pista tiene una insignia (`TrackIcons.DrawBadge`,
  `Controls/TrackBadge`) con su ícono de instrumento (o su número si no tiene) teñida con su color (o el acento), la
  misma en el encabezado del timeline, en el inspector y en el mixer. Tocarla abre el selector (`MainWindow.TrackLook.cs`):
  12 colores (`TrackIcons.Colors`, guardados en hex) y los íconos por categoría; el color deja el selector abierto y el
  ícono lo cierra. Con color, los clips de la pista se tiñen (relleno, borde y onda) y la tira del mixer lo lleva arriba.
  Los colores de pista son datos del usuario, no tokens: no se usan para estados de la interfaz.
- **Timeline**: clips como en la web: fondo `Hover` al 40 %, borde `Line` (acento al 60 % si está
  seleccionado), onda `Dim` (acento si está seleccionado, `Faint` si la pista no suena), sin el nombre
  adentro; un clip sin archivo, gris con "Sin conexión…". Con video y carriles de 52 px o más, la tira de
  miniaturas (`Video/VideoThumbnails.cs`) con la onda blanca abajo sobre un degradé, como `Filmstrip` de la web. Pista
  seleccionada con barra de acento de 2 px a la izquierda del encabezado; playhead de acento con triángulo.
  Bordes de clip (solo nativa, pedido del usuario): en cada lado dos rayitas finas, una por mitad del clip
  (`DrawEdgeHandles`): la de arriba, donde termina el fade, mueve el fade; la de abajo, en el borde, recorta. El
  cursor lo dice (diagonal como la línea del fade, ↔ para recortar) y se ilumina la manija que vas a agarrar.
  Varios clips por pista (solo nativa): Alt+X divide en el cabezal y D duplica (`ClipEditing.Split` /
  `Duplicate`); los clips de una pista comparten su panel y se ve el de arriba (el último), igual en la preview
  (`LiveTrack.ShownClip`) y en la exportación (`ExportTakeSpec.Panel`).
  Arrastrar un clip a otra pista lo cambia de pista **y de tiempo** a la vez (solo nativa, pedido del usuario; la web
  conserva el tiempo): el original queda atenuado y un fantasma punteado marca dónde cae (`DrawGhost`). Con Ctrl
  se intercambia con el clip de abajo, como en la web. Shift+arrastrar sobre una pista marca un rango (solo nativa,
  pedido del usuario) y Supr o "Borrar el rango" del clic derecho lo saca de sus clips (`ClipEditing.DeleteRange`),
  dejando el hueco; la selección se va con un clic, Esc o una edición.
  Ganancia por clip (solo nativa): `Clip.GainDb` (slider del inspector) y `Clip.GainCurve`, puntos en dB contados desde
  el sample 0 de la toma (`ClipGainCurve`, así siguen pegados a las notas al mover, recortar o dividir). El clip
  seleccionado muestra su línea (blanca, puntos de acento): clic en la línea agrega un punto, se arrastra, doble clic
  lo saca. Reproducción y exportación la evalúan cada 64 muestras con rampa (`LiveClip.Curve` es una copia que el
  hilo de audio lee sin locks: toda edición asigna una lista nueva y llama a `RefreshCurve`).
- **Video | Solo audio** (solo nativa, pedido del usuario; `MainWindow.Mode.cs`, `ProjectSettings.Mode`): el
  interruptor de la barra de título y del "Nuevo proyecto" del gestor. En solo audio el timeline (o el mixer) se
  mueve al lugar de la preview (`AudioArea`) y la fila de abajo se cierra; la cámara se suelta
  (`LiveCamera.SetPaused`), los decodificadores se cierran, se ocultan Color, Encuadre, la latencia de cámara, los
  fades de video y "En el video" (`videoOnly`), y "Exportar MP4" pasa a "Exportar audio" (el mismo `ExportDialog`
  con WAV / MP3, `AudioExporter`). Es un paso de deshacer y no borra nada. Todo lo nuevo que solo sirva con imagen
  va a `videoOnly` o mira `AudioOnly`.
- **Armar para grabar** (solo nativa, pedido del usuario): el ● de cada encabezado de pista y de cada tira del
  mixer (o R, sobre la pista seleccionada) elige dónde graba Shift+R (`armedIds` en `MainWindow.Recording.cs`: null = la elección de la web, vacía = ninguna;
  sin pista armada, Grabar queda deshabilitado, en video y en solo audio). Se pueden armar varias y R las graba a la vez, una toma y un WAV por pista desde el
  mismo sample; las pistas destino siempre se tiñen de rojo (`TimelineView.ArmedIndices`). Se graba **desde el
  cabezal** (solo nativa; la web arranca en 0): la cuenta lleva hasta ahí, la toma cae ahí
  (`RecordingJournal.StartSec`) y, en una pista con clips, desde el cabezal reemplaza solo el tramo que cubre
  (desde 0, toda la pista, como antes). Cada pista armada
  muestra su entrada (`Track.Input`, "3" o "3-4"; null = 1-2 de Configuración) en un chip que abre el menú de
  entradas. El motor abre todas las entradas del driver y tiene un `InputTap` por pista armada (su
  `InputProcessor`, medidor, monitoreo por software sin repetir entradas, y su `TakeRecorder`). El video de la
  cámara va a la primera pista armada con `Video`. Si tiene clips, la toma los reemplaza (`TakeRecording.AddTake`,
  quedan en Historial) y la pista no suena mientras grabás (`LiveTrack.RecordMuted`).
- **Quitar todos los mute / solo** (solo nativa, pedido del usuario, como Cubase): M y S al final de la barra de la
  esquina del timeline (`ClearMutesButton` / `ClearSolosButton`; por ellas la columna mide 324 como mínimo y no 264) y
  en la barra del mixer; prendidas si alguna pista tiene uno, un clic los saca todos en un paso de deshacer
  (`ClearAllToggles`).
- **Afinador y LUFS** (solo nativa, `MainWindow.Meters.cs`): en la barra de transporte, después de Monitor, la celda
  "Afinador" (A) muestra la nota y abre un panel (`Controls/TunerView`) con la aguja; escucha la primera pista armada o
  la entrada 1-2 (`PlaybackEngine.Pitch`, YIN en el `Tick` solo mientras está prendido). Las notas salen de `Loc`
  (`note.0`…`note.11`: Do Re Mi en castellano, C D E en inglés; `TunerView.NoteName`). A cada lado de la nota, un
  triángulo dice para dónde girar la clavija (▶ a la izquierda = subí, ◀ a la derecha = bajá) y parpadea más rápido
  cuanto más lejos está (1,5 a 8 por segundo, llevado por el `Tick`, sin asignar); dentro de ±3 cents
  (`TunerView.InTuneCents`) la nota, la aguja y un "OK!" se ponen en verde (`InTune`), también la nota de la celda.
  El slider "Sensibilidad" del panel es la compuerta de nivel de `PitchTap` (−70 a −30 dB, por defecto −50 =
  `PitchTap.DefaultGateDb`, que es como detectaba antes; el umbral de YIN no se toca), se guarda en `config.json`
  (`tuner.gateDb`) al cerrar el panel y en `gclab afinar --umbral`. El panel se cierra con un clic afuera
  (`IsLightDismissEnabled`), con A o con la celda; con el foco en el slider, los atajos pasan igual. A la derecha, la celda "LUFS"
  con el integrado del master de la pasada actual (`LoudnessMeter`, BS.1770; empieza de nuevo con cada play o un clic).
- **Stems** (solo nativa): la casilla del diálogo de exportar (MP4 o audio) deja además "<nombre> - stems" con un
  WAV float por pista que suena, cada una sola con sus envíos y sin el limitador (`AudioExporter.ExportStemsAsync`).
- **Plantillas de paneo** (pedido del usuario; en la web son chips sueltos con el slider "Apertura" en la barra del
  mixer): un solo desplegable "Paneo" (`BuildPanMenu`) con la apertura arriba (30 % por defecto, la web arranca en 60)
  y las plantillas con su descripción; elegir una la aplica y cierra el menú.

### Componentes (clases de estilo en `App.axaml`)

| Clase | Qué es |
|---|---|
| `Button.chip` / `ToggleButton.chip` | botón normal, de contorno como en la web: fondo transparente, borde `Line`, texto `Dim` (`Faint` si es un toggle apagado). Activado (o `Button.chip.on`): fondo `AccentSoft`, borde acento al 40 %, texto `Accent`. Deshabilitado: opacidad 0,35 |
| `Border.seg` + `ToggleButton.seg` | control segmentado (`Segmented` de la web): caja `Raised` y la opción activa como pastilla blanca al 11 %. `.seg.pick` = variante de acento (estilo del compresor) |
| `Border.metro` + `ToggleButton.cell` + `Border.vsep` | recuadro oscuro del centro de la barra de transporte (reloj │ transporte │ metrónomo, con divisores verticales `vsep`). El metrónomo, al estilo Studio One (pedido del usuario; en la web son chips sueltos): una caja con celdas valor arriba (`TextBlock.metro-value`) y etiqueta chica abajo (`TextBlock.metro-label`): Compás · Tempo · TAP · Cuenta · Click (sin atajos visibles; ícono `MetronomeIcon`, dibujado porque Segoe no tiene metrónomo) · sonido ▾ |
| `Button.ghost` | botón de ícono sin fondo (barra de título) |
| `Button.accent` | **la** acción principal. Uno solo por pantalla (hoy: "Exportar MP4", o "Exportar audio" en solo audio) |
| `Button.tbtn` / `ToggleButton.tbtn` | botones de la barra de transporte: planos, 36×36, ícono `Fg`; hover `Hover`. Activo (loop, o `.on` mientras suena): ícono `Accent`. `Button.clock` = el reloj plano |
| `StackPanel.bar-cell` | celda de la barra de transporte (valor arriba, `TextBlock.metro-label` abajo) |
| `Border.section` (+ `.off`) | tarjeta del inspector |
| `TextBox.field`, `Button.btn` (`.sm`, `.primary`, `.outline`, `.danger`), `Button.act` (`.danger`) | campos y botones de diálogos y listas (gestor de proyectos, Historial): botón de fila 28×28, confirmación roja |
| `Button.chip.purge` | "Borrar no usadas": contorno tenue que se pone rojo con el hover |
| `TextBlock.label` / `.dim` / `.mono` / `.icon` | etiqueta / secundario / números / ícono |

Para un control nuevo, primero reutilizá estas clases. Los controles Fluent (sliders, switches) toman el
acento de `SystemAccentColor*`; no les pongas colores a mano.

### Controles dibujados a mano

`TimelineView`, `PreviewView` y `StereoMeter` heredan de `Control` y dibujan en `Render`:

- Todo color, pluma y texto sale de `Palette` (`Palette.Text`, `ctx.DrawText(...)`, `Palette.Pen`, `Palette.Alpha`).
- Cacheá lo caro (ver el caché de ondas por clip) y redibujá con `InvalidateVisual()` solo cuando algo cambió
  (un medidor quieto o un cabezal parado no redibujan: ver `TimelineView.SetPlayhead`, `StereoMeter.Push`).
  Lo que se mueve en cada cuadro va en una capa aparte: el cabezal y los medidores de los encabezados del timeline
  se dibujan en `TimelineView.Layer`, y mientras suena solo se redibuja esa capa, no el timeline entero.
- Texto, pinceles y plumas siempre de `Palette` (`Palette.Text`/`DrawText`, `Palette.Alpha`, `Palette.Pen`): los
  cachea por contenido y color. `Palette.Text` da un `TextLayout` (se arma una vez; un `FormattedText` se vuelve a
  armar en cada dibujo) que no se modifica.
- Las líneas de 1 px van en medio píxel (`y + laneH - 0.5`) para que queden nítidas.
- Interacción: el control expone eventos (`SeekRequested`, `SelectionChanged`, `TrackToggled`) y la
  ventana decide qué hacer. El control no toca el motor ni el guardado.

### Medidores

Comportamiento del `StereoMeter`: ataque instantáneo, caída de 24 dB/s, pico retenido
1,5 s, rango −60 dB (−48 en los de pista). Acento hasta −6 dB, ámbar de −6 a 0, rojo arriba de −1 dB
(pico) o −0,3 dB (luz de clip). La luz de clip queda prendida hasta que se hace clic en el medidor.

### Interacción y atajos

| Tecla | Acción |
|---|---|
| Espacio | play / stop |
| Inicio | al principio |
| C | click on/off |
| M / S | mute / solo de la pista seleccionada |
| F | ver todo |
| X | timeline / mixer |
| Ctrl+S | guardar ya |
| Ctrl+Z / Ctrl+Y o Ctrl+Shift+Z | deshacer / rehacer |
| Supr | quitar el clip; en una pista vacía, quitar la pista |
| Alt+X / D | dividir el clip en el cabezal / duplicar el clip |
| Ctrl+Shift+N | nueva pista (en video pregunta audio + video o solo audio) |
| Esc | cancelar la cuenta antes de grabar; cancelar el gesto en curso en el timeline; en el gestor, cerrar el formulario y después el gestor |
| Shift+R o Num * | grabar; grabando, detener y guardar la toma; durante la cuenta, cancelarla (Espacio hace lo mismo mientras graba) |
| R | armar / desarmar la pista seleccionada (pedido del usuario; en la web R graba) |
| rueda / Shift o Ctrl+rueda / Alt+rueda | desplazar / zoom en el puntero / alto de pista |
| clic en la regla, doble clic en un carril | mover el playhead |
| B / P | mostrar / ocultar el panel de la toma (izquierda) / el del proyecto (derecha) |
| A | afinador (abre / cierra su panel) |

- **Los atajos son configurables** (solo nativa, pedido del usuario): la tabla de acciones con sus teclas por
  defecto es `Shortcuts.cs` y Configuración → "Atajos de teclado" las cambia (se guardan en `config.json`,
  "shortcuts"). La tabla de arriba son los valores por defecto. Un atajo nuevo se agrega a `Shortcuts.All`, se
  atiende en el `switch` de `OnKey` por su id (nunca comparando `Key` a mano) y su tooltip sale de
  `Shortcuts.Tip(texto, id)` (los fijos, registrados en `shortcutTips`). Esc no se configura: siempre cancela.

- Los atajos se manejan en `MainWindow.OnKey` (evento en túnel) a través de `Shortcuts.Find`. Todo lo que tenga
  atajo lo muestra **solo en el tooltip**, entre paréntesis y leído de `Shortcuts` (`"Reproducir / parar (Espacio)"`),
  y en el README. Nunca en el texto visible del control (nada de "Click C" ni cajitas de tecla, aunque la web las
  tenga): el usuario los puede cambiar, y entonces solo cambia el tooltip.
- Todo botón de ícono solo tiene `ToolTip.Tip`.
- Los cambios se aplican en vivo (se escucha mientras movés el slider). El paneo se imanta al centro (|x| < 0,04).

### Mensajes al usuario

- En la campanita de avisos, en primera persona y en criollo: **qué pasó + qué hago ahora o qué podés hacer**.
  - "No pude guardar: {motivo}. Lo vuelvo a intentar…"
  - "No pude abrir el proyecto: {motivo}"
  - "Volví a la versión de hace 5 min (lo que había antes también quedó en el historial)"
- Nada de diálogos modales para errores, ni stack traces, ni jerga ("excepción", "null").
- Un archivo que falta no rompe nada: el clip queda **offline** (gris, "Sin conexión: falta el archivo de
  audio") y el resto sigue andando.
- Cargas largas en segundo plano (`Task.Run`) con un aviso ("Cargando el proyecto…") y resultado en la
  barra de estado. La UI nunca se congela esperando disco, FFmpeg o el driver.

## 6. Estilo de código

Escribí como el código que ya está:

- Espacios de nombres por archivo, clases `sealed`, sin modificador `private` explícito, `readonly`
  donde se pueda, expresiones de colección (`[]`), `var`, nombres locales cortos en código numérico (`n`, `ml`, `pos`).
- **Cada clase lleva un `<summary>`** que explica qué es y por qué está hecha así (incluido de dónde se
  portó). Comentarios internos pocos y para el *por qué*, no para el *qué*.
- Las constantes con significado tienen nombre y comentario (`SaveDelay`, `Lookahead`, `PeakBin`).
- Hilos: el proyecto pertenece al hilo de la UI; resultados de otros hilos vuelven con
  `Dispatcher.UIThread.Post`. El flag `syncing` evita que llenar el inspector dispare ediciones.
- Las ediciones del inspector pasan por `Edit(t => ...)`, que marca sucio, refresca textos y redibuja el timeline.
- Sin dependencias nuevas de NuGet sin preguntar. Hoy: Avalonia 11.3, NAudio 2.2.

## 7. Cómo trabajar

1. `dotnet build` desde la raíz (`GuitarCamNative.slnx`) tiene que compilar sin warnings nuevos.
2. Probá el motor/proyecto con `gclab` y la ventana con
   `src\GuitarCam.App\bin\Debug\net10.0-windows\GuitarCam.exe` (arranca en el gestor de proyectos;
   con una carpeta de proyecto como argumento la abre directo).
   **Al terminar cada cambio, compilá ese exe y abrilo** (`dotnet build` de la solución, pedido del usuario): así lo
   prueba sin pedirlo. Abrilo con `--probar "texto"`: qué tiene que probar, corto y concreto (sale como toast fijo). Si la app está abierta y bloquea los DLL, avisale que la
   cierre y volvé a compilar; no la cierres vos.
3. Hacen falta una interfaz ASIO y un proyecto con tomas para probar de verdad. Si no los tenés, decí qué no pudiste probar.
4. Al revisar capturas de pantalla: las de GDI dan alfa 0 en píxeles negros y algunos visores los pintan
   blancos. Mirá el RGB, no el alfa.
5. Al terminar un ítem de [PENDIENTES.md](PENDIENTES.md), sacalo de ahí y agregalo arriba de todo en
   [HECHO.md](HECHO.md) con la fecha (AAAA-MM-DD) y una línea de qué se hizo y dónde. Ideas nuevas van en
   PENDIENTES, en su sección. HECHO.md es historial: no hace falta leerlo de entrada.
6. Si cambiás atajos, comandos de `gclab` o la estructura, actualizá el README.
7. No subas WAV ni binarios (`*.wav`, `bin/`, `obj/` están en `.gitignore`). No hagas commit ni push si el usuario no lo pide.
