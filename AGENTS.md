# Guía para agentes — GuitarCam Studio Native

Instrucciones para cualquier agente de IA (Claude, Codex, Copilot, Cursor…) que siga con este proyecto. Acá van las
reglas que valen siempre. Las demás se cargan según dónde trabajes:

- [src/GuitarCam.App/AGENTS.md](src/GuitarCam.App/AGENTS.md): diseño de la ventana (tokens, tipografía, clases, atajos, mensajes).
- [src/GuitarCam.App/Controls/AGENTS.md](src/GuitarCam.App/Controls/AGENTS.md): controles dibujados a mano y medidores.
- [docs/interfaz.md](docs/interfaz.md): cómo es y dónde vive cada parte de la ventana. Leé solo el apartado de lo que vas a tocar.

Si algo de estas guías deja de ser cierto, actualizalo en el mismo cambio. Contexto general en [README.md](README.md),
lo que falta en [PENDIENTES.md](PENDIENTES.md); [HECHO.md](HECHO.md) es historial y no hace falta leerlo de entrada.

## 1. Qué es

- **GuitarCam Studio Native** sirve para hacer **videos de guitarra**: grabar tomas con cámara y audio, mezclarlas y
  exportar un video con un panel por toma. El exe es `GuitarCam.exe` y los ajustes van en `%APPDATA%\GuitarCam Studio`.
- **El foco manda.** Las funciones de DAW tienen que servir para el video y no complicar el camino de **"grabo,
  mezclo y exporto"**. Si algo nuevo agrega pasos o pantallas a ese camino, replantealo o preguntá.
- **Las decisiones son del usuario.** Lo que describen estas guías lo eligió el usuario: no lo cambies sin preguntar,
  aunque otro DAW lo haga distinto. La app nació como port de una web (`..\GuitarCam`) que **ya no es referencia**:
  las menciones a la web en el código son historia, no reglas.

## 2. Idioma

| Qué | Idioma |
|---|---|
| Textos de la interfaz, mensajes, tooltips | **Español rioplatense, con voseo** ("Abrí", "Elegí", "probalo"), vía `Loc` |
| Comandos, opciones y textos de `gclab` | Español (`grabar --segundos 15 --vida 0.3`) |
| README, PENDIENTES, HECHO, guías | Español |
| Identificadores, comentarios y `<summary>` | Inglés |
| Mensajes de commit | Inglés, imperativo, una línea descriptiva ("Decode real video in the preview with FFmpeg") |
| Valores guardados en el JSON | Inglés (`"tube"`, `"tape"`, `"side-by-side"`…) |

- La app es multilenguaje (`Assets/Lang/<código>.json`, **es.json es la referencia**): texto nuevo → clave nueva en
  todos los idiomas. Detalles en la guía de la ventana.
- `Audio` y `Projects` no conocen `Loc`: sus mensajes pasan por `Texts.T(clave, español, args...)` (en `GuitarCam.Audio`,
  prefijos `err.`, `store.`, `camera.`…), que `Loc.Load` conecta; sin esa conexión (gclab) sale el español.
- Siguen en español a propósito las plantillas que el usuario ya guardó y los nombres que se crean ("Canal 1").
- Vocabulario de músico, no de programador: **canal**, **pista**, **click**, **Vida** (saturación; niveles Apenas /
  Sutil / Con vida / Caliente / Al palo), **Válvula / Cinta**, **Rasgueo / Arpegio / Punteo**, **Ver todo**.
- Tono: claro y corto en botones y tooltips; el humor (suave) va en estados vacíos, avisos y confirmaciones, nunca
  cuando el usuario puede perder algo.

## 3. Arquitectura

```
src/
  GuitarCam.Audio      motor de audio: DSP, ASIO dúplex, click, grabación a disco. Sin UI.
  GuitarCam.Projects   modelo del proyecto, guardado atómico, historial. Sin UI.
  GuitarCam.App        la ventana (Avalonia 11.3). Depende de Audio y Projects.
  GuitarCam.Lab        gclab.exe: consola para probar audio, DSP y proyectos sin la ventana.
tools/                 probar.ps1 (compila, comprueba y abre una copia de prueba), make-icon.ps1 (ícono), make-shortcut.ps1 (acceso directo), make-track-icons.py (íconos de pista)
```

- Las dependencias van **hacia abajo**: App → Projects → Audio. `Audio` y `Projects` no referencian Avalonia; todo lo
  que se pueda probar desde `gclab` vive ahí.
- Cada función nueva del motor o del proyecto se puede ejercitar desde **`gclab`** (comando u opción nueva, con su
  ayuda actualizada). `gclab` usa cultura invariante (punto decimal) a propósito.
- `net10.0-windows`, x64, `Nullable`. Audio por **ASIO** (NAudio), video con **FFmpeg/ffprobe** por proceso y pipe (en el PATH),
  salvo la preview: la decodifica FFmpeg dentro del proceso (`NativeVideoSource`, FFmpeg.AutoGen con los DLL de FFmpeg 9
  en `lib\ffmpeg`, fuera de git; ver el README).
- Sin MVVM ni bindings: code-behind con controles con nombre. No metas un framework sin que el usuario lo pida.
- Sin dependencias nuevas de NuGet sin preguntar. Hoy: Avalonia 11.3, NAudio 2.2, FFmpeg.AutoGen 9.0.

### Modelo del proyecto (`GuitarCam.Projects`)

- **Media**: grabaciones e importados. **Nunca se reescriben.**
- **Track**: el canal (volumen, paneo, mute/solo, efectos…), si lleva panel de video, `Color` ("#rrggbb") e `Icon`
  (null = sin color / con su número), `FolderId` = su carpeta.
- **Folder** (`Project.Folders`): agrupa pistas. Sus hijas van juntas en `Project.Tracks` y la carpeta va justo antes
  (`Folder.Index`). Toda edición de orden pasa por `TrackFolders` y `TrackFolders.Normalize` repara lo que quede raro.
  **Solo contiene** (decisión del usuario): sin fader, paneo, M/S ni efectos. Sus pistas suman ahí y van a su `Output`;
  desde esa suma tiene sus `Sends` (nunca PRE: no tiene fader). Un **separador** es una `Folder` con `Separator`: va en
  el mismo orden, nunca toma pistas ni rutea, y se puede ocultar del timeline (`HiddenInTimeline`). Pistas de la raíz,
  carpetas y buses tienen su lado del mixer (`MixerSide`, `MixerSides`: null = el de siempre).
- **Buses y ruteo**: un solo concepto para "bus" y "FX" (`ProjectSettings.Buses`, `Bus`: nombre, fader, paneo, mute,
  slots, salida y envíos; una reverb o un delay son un slot más, `InsertSlot.Reverb` / `Delay`). Un proyecto nuevo trae
  "Reverb" y "Delay" (`ProjectBuses.Defaults`). Pista (`Channel`), carpeta y bus son `IRouted`: `Output` (null = Master,
  o un bus; una pista en carpeta sale por la carpeta) y `Sends` (`Send`: bus, nivel 0..1, ON y PRE). Las ediciones
  pasan por `ProjectBuses`. **Por ahora un bus sale solo al Master** (decisión del usuario, 2026-10-26: las cadenas
  entre buses no se entendían): `CanFeed` rechaza un bus como origen y `Normalize` le borra salida y envíos, además de
  sacar lo que apunta a buses que no existen. El motor sigue sabiendo correr buses que alimentan buses (`Order`), por
  si vuelven. Cada pista (y carpeta) elige a qué buses manda (pedido del usuario): un envío con `Hidden` está sacado
  de su lista (apagado, con su nivel guardado) y un bus sin envío se muestra, así una pista o un bus nuevos traen todos
  (`ProjectBuses.Shown`, `ShowSend`); el timeline, el mixer y el inspector muestran solo esos. La versión 3 trajo esto: los campos de la 2
  (Reverb/Delay fijos, retornos, `reverbSend`…, el fader/M/S/slots de carpeta) se leen como `Old*` y se convierten al
  normalizar.
- **Clip**: un pedazo de un media en una pista (`AtSec`, `TrimStartSec`, `TrimEndSec`, `GainDb`, `GainCurve`, encuadre).

Convenciones: tiempos del timeline en **segundos**, correcciones de latencia/sincronía en **ms**. JSON en camelCase
(`ProjectStore.Json`). Cada clase lleva `[JsonExtensionData] Extra`, que conserva lo desconocido: no lo saques.
`Normalize()` rellena lo que falte o venga roto (todo campo nuevo tiene valor por defecto y se normaliza ahí). Cambio
incompatible de formato → subí `Project.CurrentVersion` y migrá al abrir. IDs con prefijo (`Ids.New("prj")`).

### Carpeta del proyecto y guardado

```
guitarcam.json      el proyecto
guitarcam.json.bak  el guardado anterior
guitarcam.lock      mientras está abierto (una segunda ventana lo abre en solo lectura)
historial/          copias para volver atrás
media/              grabaciones e importados
cache/              lo que se rehace desde media/ (tomas transpuestas, TransposeCache): borrarlo solo cuesta tiempo
exports/
```

- Todo guardado es **atómico**: temporal → forzado a disco → `File.Replace`. Nunca escribas `guitarcam.json` directo.
- Toda edición pasa por `MarkDirty()` → `SaveSoon()`: cuándo se escribe lo elige el usuario (`SaveMode`, en
  Configuración → Guardado y exportación): 1,5 s después de cada edición (por defecto), cada X minutos o a mano (serializa en la UI,
  escribe en segundo plano, en orden). Ctrl+S y el diskette fuerzan; las tomas, las exportaciones y el cambiar de proyecto
  (`FlushSave()`) guardan siempre, sea cual sea el modo; cerrar la ventana con cambios pendientes pregunta antes
  (`UnsavedDialog`) y no cierra si no pudo guardar.
- **Perder una toma es el peor error posible.** `RecordingWriter` escribe mientras graba y reescribe el encabezado
  cada ~2 s. Todo lo que toque archivos del usuario piensa en qué pasa si el programa muere a la mitad.

## 4. Audio en tiempo real

**Nivel profesional**, como Logic Pro o Fender Studio Pro: nada de clics ni cortes. Arrancar, parar y hacer scrub
llevan su fundido (`PlaybackEngine.TransportFadeMs`, elegible en Configuración); los saltos, crossfade de 10 ms;
`Stop`/`Pause` cortan en el acto y son solo para grabar, exportar o cambiar de proyecto. El estado de los efectos no
se reinicia en medio de la música. Lo que se dude se mide (`AudioGapLog`) en vez de suponerlo.

El callback de ASIO (`AsioDuplex.ProcessBlock`, `PlaybackEngine.Process`) corre en el hilo del driver:

- **No asignes memoria, no bloquees, no uses locks, no toques disco ni la UI.** Tampoco LINQ que asigne, `async`,
  logs, excepciones ni métodos genéricos (no se precompilan).
- Buffers preasignados (8192 muestras por canal en el motor y en cada `LiveTrack`). Datos nuevos al motor =
  **reemplazar la referencia entera** (`volatile LiveTrack[] tracks`); la posición va con `Interlocked`. Lo que va a
  disco sale por otro hilo (`RecordingWriter`).
- **La basura de la UI también corta el audio**: un GC gen1 deja huecos de 10–15 ms. En cada `Tick` o `Render` no se
  asigna (ni se reconstruyen controles, ni pinceles o textos por cuadro).
- `AudioWarmUp` precompila al arrancar `GuitarCam.Audio`, `GuitarCam.App.Audio` y `GuitarCam.App.Model`: el código de
  tiempo real nuevo va en esos namespaces (o se suma a la lista).
- Motor a 48 kHz (`PlaybackEngine.SampleRate`); los medios se remuestrean con `AudioLoader`. Si un cambio sube mucho la
  carga "DSP x %" de la barra de título, decilo.

Cadena por pista: clips (fades, ganancia; el audio ya transpuesto si la pista tiene Transponer) → los **slots de
efectos** del canal en su orden (`Channel.Inserts`; por defecto saturación → compresor con su EQ de preset antes y
después → EQ de pista, que se pueden sacar, repetir y reordenar) → paneo → fader → su salida (su carpeta, un bus o el
master); los envíos salen del fader (post, por defecto) o de antes (PRE: después de los slots y el paneo; mute y solo
igual los callan) hacia los buses. Carpetas y buses son nodos (`BusDspChain`: suma de lo que le llega → slots → paneo →
fader/mute → su salida y sus envíos) que corren en orden, cada uno después de todo lo que lo alimenta
(`ChannelDsp.Nodes`: primero las carpetas, después `ProjectBuses.Order`) → master → limitador. El click va por fuera
del limitador, atrasado la misma latencia. `TrackDspChain` (con `TrackSend`), `InsertRack` (los slots, de pista o
bus), `BusDspChain` y `MixGraphBus` (`GuitarCam.Audio/Dsp/MixRenderer.cs`) los usan igual la reproducción y la
exportación: no los dupliques. La conversión proyecto → motor es una sola, `ChannelDsp` (`Track`, `Bus`, `Nodes`).
`gclab comprobar-dsp` corre las comprobaciones del DSP.

Slots: cada efecto es un `ITrackInsert` (`GuitarCam.Audio/Dsp/TrackInserts.cs`); un VST3 tiene que ser un tipo de slot
más. `TrackDspChain.SetInserts` reusa el efecto de cada slot por su id (no pierde el estado al editar) y, si cambia la
lista, el hilo de audio funde 5 ms de salida y 5 ms de entrada. La conversión canal → motor es una sola,
`ChannelDsp.Specs` (en vivo, exportación y gclab); las ediciones de la lista, `ChannelInserts`. La pestaña Canal y la
tira amplia muestran la cadena entera (cada slot editado por su id); `Channel.Sat/Comp/Eq` son el primer slot de cada
tipo, el atajo de las perillas del timeline y la tira compacta.

## 5. Estilo de código

- Namespaces por archivo, clases `sealed`, sin `private` explícito, `readonly` donde se pueda, expresiones de
  colección (`[]`), `var`, nombres cortos en código numérico (`n`, `ml`, `pos`).
- **Cada clase lleva un `<summary>`** con qué es y por qué está hecha así. Comentarios internos pocos y para el *por qué*.
- Constantes con significado, con nombre y comentario (`SaveDelay`, `Lookahead`, `PeakBin`).
- El proyecto pertenece al hilo de la UI; lo de otros hilos vuelve con `Dispatcher.UIThread.Post`.

## 6. Cómo trabajar

1. `dotnet build` de `GuitarCamNative.slnx` compila sin warnings nuevos.
2. **Se trabaja en esta carpeta, en `master`, sin ramas ni worktrees** (lo pidió el usuario): así hay un solo build y
   lo que se prueba es lo que está acá. Probá el motor y el proyecto con `gclab`. **Al terminar cada cambio corré
   `tools\probar.ps1 -NoAbrir`** (compila, corre `gclab comprobar-dsp` y deja una copia en
   `pruebas\`, fuera de AppData a propósito: desde la app de Claude, que es MSIX, lo escrito en AppData queda dentro
   del paquete y el acceso directo no lo ve) **y preguntale al usuario** (con un cuadro de pregunta, si tu herramienta lo
   tiene) **si quiere probarlo**. Si dice que sí, `tools\probar.ps1 -Abrir "texto"` abre esa copia con qué probar,
   corto y concreto (sale como toast fijo; `-Proyecto carpeta` la abre en ese proyecto). Si el script dice
   "NO SE PUEDE PROBAR", contale por qué y no abras nada. Nunca abras el exe de `bin\` ni lo mandes a probar desde
   ahí: bloquea los DLL y el próximo build falla. Si hay un GuitarCam abierto, el script le pregunta al usuario en un
   cuadro si lo cierra (guarda antes); nunca lo cierres ni lo mates por otro lado. Si eligió cancelar (código 2), no
   insistas. La copia muestra en la barra de título "PRUEBA" + el commit (+ cambios), así el usuario sabe qué
   está probando.
3. Para probar de verdad hacen falta una interfaz ASIO y un proyecto con tomas: si no los tenés, decí qué no probaste.
4. En capturas de GDI los píxeles negros tienen alfa 0 y algunos visores los pintan blancos: mirá el RGB.
5. Al terminar un ítem de PENDIENTES, sacalo de ahí y agregalo arriba de todo en HECHO con la fecha (AAAA-MM-DD) y una
   línea de qué se hizo y dónde. Ideas nuevas van en PENDIENTES, en su sección.
6. Si cambiás atajos, comandos de `gclab` o la estructura, actualizá el README.
7. No subas WAV ni binarios. No hagas commit ni push si el usuario no lo pide.
