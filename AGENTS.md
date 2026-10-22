# Guía para agentes — GuitarCam Native

Instrucciones para cualquier agente de IA (Claude, Codex, Copilot, Cursor…) que siga con este proyecto.
Leela entera antes de tocar código. Describe cómo está hecha la app hoy: si algo de acá deja de ser
cierto, actualizá este archivo en el mismo cambio.

Para el contexto general mirá [README.md](README.md). Lo que falta hacer está en [PENDIENTES.md](PENDIENTES.md),
y la captura de referencia de la interfaz, en [docs/captura.png](docs/captura.png).

---

## 1. Qué es y qué no es

- **GuitarCam Studio** sirve para hacer **videos de guitarra**: grabar tomas con cámara y audio,
  mezclarlas y exportar un video con un panel por toma. Esta carpeta es el **port a .NET nativo** de la
  app web (`..\GuitarCam`, React + Web Audio). Corre al lado de la web, no la reemplaza.
- **El foco manda.** Las funciones de DAW (clips, plugins, MIDI…) tienen que servir para el video y no
  pueden complicar el camino de **"grabo, mezclo y exporto"**. Si una función nueva agrega pasos o
  pantallas a ese camino, replanteala o preguntá.
- **La app web es la referencia.** Cuando algo ya existe en la web, se porta con el mismo comportamiento
  (y el mismo sonido, muestra a muestra en el DSP) antes de "mejorarlo". Para la interfaz esto es una
  regla dura: ver [Migrar algo de la web](#migrar-algo-de-la-web).

### Migrar algo de la web

**Todo lo que se migra tiene que quedar lo más parecido posible a como está en la app web**: el mismo
diseño, la misma disposición, los mismos textos y el mismo flujo. El usuario ya pulió esa interfaz y le
gusta como quedó; la nativa no es una oportunidad para rediseñarla.

- **Antes de escribir código, leé el componente de la web** (`..\GuitarCam\src\components\…`, `App.tsx`,
  `index.css`) y, si se puede, miralo andando (`npm run dev` en `..\GuitarCam`). Anotá qué pantallas,
  secciones, columnas, botones, menús y atajos tiene.
- **Misma estructura**: si en la web es un diálogo con una lista y acciones por fila (por ejemplo el
  gestor "Proyectos" de `SessionPicker`), en la nativa también. No lo reemplaces por algo más simple
  (un menú, un selector de carpetas, un mensaje en la barra de estado) porque sea más rápido de hacer.
- **Mismos textos**, palabra por palabra (títulos, botones, tooltips, mensajes, estados vacíos), mismos
  íconos (el equivalente en Segoe Fluent Icons), mismo orden, mismos atajos, mismos valores por defecto.
- **Mismos comportamientos de detalle**: confirmaciones en línea, nombres únicos "(2)", qué se recuerda
  entre sesiones, qué pasa con el teclado mientras está abierto, etc.
- **Diferencias permitidas, solo si hacen falta**: lo que impone la plataforma (Avalonia en vez de
  HTML) o el formato de proyecto nativo (por ejemplo, la web listaba `session.json` y la nativa lista
  `guitarcam.json`, más "Importar de la app web"). Cada diferencia se avisa al usuario antes y se anota
  en el `<summary>` del componente ("Igual que SessionPicker de la web salvo…").
- **Si todavía no se puede portar entero**, se porta la pantalla con la misma forma y lo que falta queda
  deshabilitado con un tooltip ("Todavía no está en la versión nativa"). No se inventa un reemplazo provisorio.
- **Al terminar**, compará la nativa con la web lado a lado (captura de las dos) y decí qué quedó distinto.

## 2. Idioma

| Qué | Idioma |
|---|---|
| Textos de la interfaz, mensajes de estado, tooltips | **Español rioplatense, con voseo** ("Abrí", "Elegí", "usá", "probalo") |
| Comandos y opciones de `gclab` | Español (`grabar --segundos 15 --vida 0.3`) |
| README, PENDIENTES, esta guía | Español |
| Identificadores, comentarios y `<summary>` en el código | Inglés |
| Mensajes de commit | Inglés, en imperativo, una línea descriptiva ("Decode real video in the preview with FFmpeg") |
| Valores que se guardan en el JSON | Inglés, iguales a la web (`"tube"`, `"tape"`, `"side-by-side"`, `"rhythm"`…) |

El vocabulario de la interfaz es de músico, no de programador: **toma**, **pista**, **click**, **Vida**
(cantidad de saturación), **Válvula / Cinta**, **Rasgueo / Arpegio / Punteo** (presets del compresor),
**Ver todo**. Los niveles de "Vida" se nombran Apenas / Sutil / Con vida / Caliente / Al palo.

## 3. Arquitectura

```
src/
  GuitarCam.Audio      motor de audio: DSP portado, ASIO dúplex, click, grabación a disco. Sin UI.
  GuitarCam.Projects   modelo del proyecto, guardado atómico, historial, importador de la web. Sin UI.
  GuitarCam.App        la ventana (Avalonia 11.3). Depende de Audio y Projects.
  GuitarCam.Lab        gclab.exe: consola para probar audio, DSP y proyectos sin la ventana.
tools/worklet-ref.mjs  corre los worklets originales de la web en Node para comparar.
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

Tres capas, igual que en la web:

- **Media**: grabaciones y archivos importados. **Nunca se reescriben.**
- **Track**: el canal (volumen, paneo, mute/solo, saturación, compresor…) y si lleva panel de video.
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
- El importador de la web **solo lee** el proyecto web y crea uno nuevo "Nombre (nativa)" al lado
  (hard links si es la misma unidad, copia si no).
- Perder una toma es el peor error posible. `RecordingWriter` escribe la grabación mientras ocurre y
  reescribe el encabezado cada ~2 s para que un corte no la pierda. Cualquier cosa nueva que toque
  archivos del usuario tiene que pensar en qué pasa si el programa muere a la mitad.

## 4. Audio en tiempo real

El callback de ASIO (`AsioDuplex.ProcessBlock`, `PlaybackEngine.Process`) corre en el hilo del driver:

- **No asignes memoria, no bloquees, no uses locks, no toques disco ni la UI** dentro del callback. LINQ
  que asigna, `async`, logs y excepciones también quedan afuera.
- Buffers preasignados (8192 muestras por canal en el motor y en cada `LiveTrack`).
- Para pasarle datos nuevos al motor se **reemplaza la referencia entera** (`volatile LiveTrack[] tracks`)
  en vez de modificar la colección. La posición va con `Interlocked`.
- Los medidores son picos que escribe el hilo de audio y lee/resetea la UI en su `Tick` (16 ms). La
  caída visual (×0,82 por tick en pistas, 24 dB/s en el medidor estéreo) se hace del lado de la UI.
- Lo que va a disco sale del callback por otro hilo (ver `RecordingWriter`).
- Sample rate del motor: 48 kHz (`PlaybackEngine.SampleRate`).
- `PlaybackEngine` muestra la carga del callback ("DSP x %"): si un cambio la sube mucho, decilo.

Cadena por pista (como en la web, `buildTrack` de `mixGraph.ts`): clips (con fades y ganancia) →
saturación → EQ del preset antes (corte de graves, barro) → compresor → EQ del preset después (presencia,
aire) → EQ de pista → paneo (`StereoPannerNode` de Web Audio) → fader → suma; del fader salen los envíos
post-fader a los buses de reverb y delay, que vuelven al master → limitador master. El click va por
fuera del limitador, atrasado la misma latencia. La cadena (`TrackDspChain`) y los buses (`MixGraphBus`)
viven en `GuitarCam.Audio/Dsp/MixRenderer.cs` y los usan igual la reproducción y el render offline de la
exportación: no los dupliques. Los medios se cargan y remuestrean a 48 kHz con `AudioLoader`.

### Ports de la web

- Cada port dice de dónde viene en su `<summary>`: *"Port of gc-saturator (GuitarCam/src/audio/fxWorklet.ts)"*.
- Mismos nombres de parámetros y mismas fórmulas, aunque haya una forma "más linda". Si la web tiene un
  bug, se porta igual y se anota; arreglarlo es otro cambio, acordado.
- Verificá contra el original con `gclab verificar [toma.wav]` (usa `tools/worklet-ref.mjs`, necesita Node).
  Un port de DSP no está terminado hasta que esa comparación da bien.

## 5. Diseño de la interfaz

### Principios

- **Oscura, sobria, de estudio.** Fondo casi negro, superficies apenas más claras, un solo color de acento.
  El contenido (el video y las ondas) es lo que tiene que resaltar, no los controles.
- **Igual a la web.** Los tokens, tamaños y comportamientos salen de la app web (`index.css` y sus
  componentes). Ante la duda, mirá cómo lo hace la web.
- **Se usa con la guitarra colgada**: atajos de una tecla, objetivos de clic cómodos, nada que exija
  precisión ni varios pasos para grabar o reproducir.
- **Siempre se ve el estado**: qué está sonando, si hay audio, si un archivo falta, si se guardó.
- Lo que todavía no está portado **se muestra igual que en la web** (para que el layout no cambie),
  deshabilitado o con un mensaje en la barra de estado ("Exportar todavía no está en la versión nativa").
  Nunca un botón que no hace nada y no dice nada.

### Colores (tokens)

Están definidos **dos veces y tienen que coincidir**: en `App.axaml` (para XAML) y en
`Controls/Palette.cs` (para los controles que dibujan). Si agregás o cambiás uno, cambiá los dos (y que
coincida con la web).

| Token | Valor | Uso |
|---|---|---|
| `App` | `#0b0b0d` | fondo de la ventana, del timeline, de la preview; texto sobre acento |
| `Panel` | `#131316` | barras (título, transporte), inspector, encabezados de pista |
| `Raised` | `#1c1c20` | secciones, chips, botones, pista seleccionada |
| `Hover` | `#26262b` | hover; fondo de toggles apagados y badges |
| `Line` | `#27272c` | todos los bordes y separadores (1 px) |
| `Fg` | `#ececef` | texto principal |
| `Dim` | `#a1a1aa` | texto secundario, valores, íconos |
| `Faint` | `#6b6b74` | etiquetas en mayúscula, ayudas, deshabilitado |
| `Accent` | `#22d3ee` | **el** color: activo, seleccionado, play, playhead, ondas, medidores |
| `AccentSoft` | `#2422d3ee` | fondo de chips activados y del badge "NATIVO" |
| `Rec` | `#ef4444` | grabar y clip/saturación de nivel. Nada más |
| `RecSoft` | `#24ef4444` | fondo suave asociado a grabación |
| `Composition` | `#09090b` | fondo del cuadro del video |
| ámbar | `#f59e0b` | mute activo y zona de −6 a 0 dB en medidores |
| gris offline | `#71717a` | clips cuyo archivo falta |

Reglas: no inventes colores nuevos para estados nuevos sin necesidad; el acento no se usa como
decoración; el rojo significa **grabación o peligro de clip**, nunca "error genérico". Las variantes con
transparencia salen de `Palette.Alpha(color, a)` (por ejemplo, relleno de clip 0,10 / 0,16 seleccionado,
borde 0,45 / 0,9). El hover del acento es `#4fdcf1`. El tema es solo oscuro (`RequestedThemeVariant="Dark"`).

### Tipografía

- UI: **Segoe UI Variable Text** (fallback Segoe UI). Números y tiempos: **Cascadia Mono** (fallback Consolas).
  Íconos: **Segoe Fluent Icons** (fallback Segoe MDL2 Assets), como `TextBlock` con clase `icon` y el
  glifo en `Text` (`&#xE768;`). No agregues paquetes de íconos ni imágenes para íconos.
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
  **18** (círculo) botones de transporte, **2** barras de medidor.
- Bordes de 1 px en `Line`. Sin sombras, sin gradientes, sin animaciones decorativas.
- Padding de sección `12,10`; separación vertical entre bloques 10–12; márgenes del inspector `14,16`.
- Alturas mínimas: chips 30, transporte 36×36.

### Estructura de la ventana

```
┌ barra de título propia (44) ─ logo · chip del proyecto ▾ · deshacer/rehacer · estado · ENTRADA 1-2 [medidor] · ⚙ · [Exportar MP4] ┐
├ inspector (290) ─┬─ preview (el cuadro del video con un panel por pista con video) ───────────────────────────────────────┤
│ TOMA + secciones │                                                                                                         │
├──────────────────┴─ separador arrastrable ──────────────────────────────────────────────────────────────────────────────────┤
│ transporte: ⏮ ▶ ● · reloj · BPM · Click · sonido ·························· DSP % · driver ASIO · Ver todo                  │
│ timeline (340 en total): regla de compases (32) · encabezados fijos (264) · un carril por pista (64, 40–160)               │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- La barra de título es la de la app (`ExtendClientAreaToDecorationsHint`), arrastrable, doble clic
  maximiza; los botones del sistema quedan a la derecha (margen 150). `TransparencyLevelHint="None"` no se
  toca: con transparencia, píxeles oscuros del video dejan ver lo que hay detrás.
- **Barra de estado** = el `StatusText` del centro de la barra de título. Ahí van los avisos, no en diálogos.
- **Inspector**: título de la toma + una tarjeta (`Border.section`) por bloque, cada una con ícono +
  etiqueta en mayúscula y, si se puede apagar, un `ToggleSwitch` a la derecha. Apagada → clase `off`
  (los controles quedan al 40 % de opacidad, pero se pueden seguir tocando).
- **Preview**: los paneles se calculan con `PanelLayout.Compute` sobre un lienzo de 1080 px de alto,
  **la misma cuenta que usa la exportación** (port de `panelLayout.js`). Lo que se ve tiene que ser lo que
  se exporta. Encuadre: cover-fit + zoom/paneo/espejo del clip. Etiqueta con el nombre de la pista abajo a la izquierda.
- **Timeline**: clips como en la web: fondo `Hover` al 40 %, borde `Line` (acento al 60 % si está
  seleccionado), onda `Dim` (acento si está seleccionado, `Faint` si la pista no suena), sin el nombre
  adentro; un clip sin archivo, gris con "Sin conexión…". Pista
  seleccionada con barra de acento de 2 px a la izquierda del encabezado; playhead de acento con triángulo.

### Componentes (clases de estilo en `App.axaml`)

| Clase | Qué es |
|---|---|
| `Button.chip` / `ToggleButton.chip` | botón normal (Raised + borde). Activado: fondo `AccentSoft`, borde y texto `Accent` |
| `Button.ghost` | botón de ícono sin fondo (barra de título) |
| `Button.accent` | **la** acción principal. Uno solo por pantalla (hoy: "Exportar MP4") |
| `Button.transport` (+ `.play`, `.rec`) | botones redondos de transporte. Play relleno de acento, grabar con punto rojo |
| `Border.section` (+ `.off`) | tarjeta del inspector |
| `TextBlock.label` / `.dim` / `.mono` / `.icon` | etiqueta / secundario / números / ícono |

Para un control nuevo, primero reutilizá estas clases. Los controles Fluent (sliders, switches) toman el
acento de `SystemAccentColor*`; no les pongas colores a mano.

### Controles dibujados a mano

`TimelineView`, `PreviewView` y `StereoMeter` heredan de `Control` y dibujan en `Render`:

- Todo color, pluma y texto sale de `Palette` (`Palette.Text`, `ctx.DrawText(...)`, `Palette.Pen`, `Palette.Alpha`).
- Cacheá lo caro (ver el caché de ondas por clip) y redibujá con `InvalidateVisual()` solo cuando algo cambió.
- Las líneas de 1 px van en medio píxel (`y + laneH - 0.5`) para que queden nítidas.
- Interacción: el control expone eventos (`SeekRequested`, `SelectionChanged`, `TrackToggled`) y la
  ventana decide qué hacer. El control no toca el motor ni el guardado.

### Medidores

Mismo comportamiento que el `StereoMeter` de la web: ataque instantáneo, caída de 24 dB/s, pico retenido
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
| Ctrl+S | guardar ya |
| Ctrl+Z / Ctrl+Y o Ctrl+Shift+Z | deshacer / rehacer |
| Supr | quitar el clip (la pista queda vacía); en una pista vacía, quitar la pista |
| Esc | cancelar la cuenta antes de grabar; cancelar el gesto en curso en el timeline; en el gestor, cerrar el formulario y después el gestor |
| R | grabar; grabando, detener y guardar la toma; durante la cuenta, cancelarla (Espacio hace lo mismo mientras graba) |
| rueda / Shift o Ctrl+rueda / Alt+rueda | desplazar / zoom en el puntero / alto de pista |
| clic en la regla, doble clic en un carril | mover el playhead |

- Los atajos se manejan en `MainWindow.OnKey` (evento en túnel). Cada atajo nuevo va también en el
  tooltip del botón, entre paréntesis: `"Reproducir / parar (Espacio)"`, y en el README.
- Todo botón de ícono solo tiene `ToolTip.Tip`.
- Los cambios se aplican en vivo (se escucha mientras movés el slider). El paneo se imanta al centro (|x| < 0,04).

### Mensajes al usuario

- En la barra de estado, en primera persona y en criollo: **qué pasó + qué hago ahora o qué podés hacer**.
  - "No pude guardar: {motivo}. Lo vuelvo a intentar…"
  - "Es un proyecto de la app web: usá \"Importar proyecto de la app web…\""
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
   `src\GuitarCam.App\bin\Debug\net10.0-windows\GuitarCam.exe` (abre el último proyecto).
3. Hacen falta una interfaz ASIO y un proyecto con tomas para probar de verdad. Si no los tenés, decí qué no pudiste probar.
4. Al revisar capturas de pantalla: las de GDI dan alfa 0 en píxeles negros y algunos visores los pintan
   blancos. Mirá el RGB, no el alfa.
5. Al terminar un ítem de [PENDIENTES.md](PENDIENTES.md), pasalo a **Hecho** con la fecha (AAAA-MM-DD) y
   una línea de qué se hizo y dónde. Ideas nuevas van ahí, en su sección.
6. Si cambiás atajos, comandos de `gclab` o la estructura, actualizá el README.
7. No subas WAV ni binarios (`*.wav`, `bin/`, `obj/` están en `.gitignore`). No hagas commit ni push si el usuario no lo pide.
