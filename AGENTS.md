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
- `net10.0-windows`, x64, `Nullable`. Audio por **ASIO** (NAudio), video con **FFmpeg/ffprobe** por proceso y pipe (en el PATH).
- Sin MVVM ni bindings: code-behind con controles con nombre. No metas un framework sin que el usuario lo pida.
- Sin dependencias nuevas de NuGet sin preguntar. Hoy: Avalonia 11.3, NAudio 2.2.

### Modelo del proyecto (`GuitarCam.Projects`)

- **Media**: grabaciones e importados. **Nunca se reescriben.**
- **Track**: el canal (volumen, paneo, mute/solo, efectos…), si lleva panel de video, `Color` ("#rrggbb") e `Icon`
  (null = sin color / con su número), `FolderId` = su carpeta.
- **Folder** (`Project.Folders`): agrupa pistas. Sus hijas van juntas en `Project.Tracks` y la carpeta va justo antes
  (`Folder.Index`). Toda edición de orden pasa por `TrackFolders` y `TrackFolders.Normalize` repara lo que quede raro.
  Su fader, M y S actúan como un VCA (`TrackFolders.Mix`), en vivo y en la exportación.
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
  Configuración → Guardado): 1,5 s después de cada edición (por defecto), cada X minutos o a mano (serializa en la UI,
  escribe en segundo plano, en orden). Ctrl+S y el diskette fuerzan; las tomas, las exportaciones y el cerrar o cambiar
  de proyecto (`FlushSave()`) guardan siempre, sea cual sea el modo.
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

Cadena por pista: clips (fades, ganancia; el audio ya transpuesto si la pista tiene Transponer) → saturación → EQ del preset antes → compresor → EQ del preset después → EQ
de pista → paneo → fader → suma; del fader salen los envíos post-fader a reverb y delay, que vuelven al master →
limitador. El click va por fuera del limitador, atrasado la misma latencia. `TrackDspChain` y `MixGraphBus`
(`GuitarCam.Audio/Dsp/MixRenderer.cs`) los usan igual la reproducción y la exportación: no los dupliques.
`gclab comprobar-dsp` corre las comprobaciones del DSP.

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
   `%LOCALAPPDATA%\GuitarCam Pruebas`) **y preguntale al usuario** (con un cuadro de pregunta, si tu herramienta lo
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
