# Opcional — ideas del manual de Fender Studio Pro para WildTrack Native

Relevamiento: **2026-10-24**. Las propuestas nuevas de este informe son opcionales, sin compromiso de
implementación ni cambios en el funcionamiento de la app. Las funciones ya pedidas conservan su estado
en `PENDIENTES.md`.

La oportunidad principal está en **grabar y corregir una interpretación sin soltar la guitarra**, y en
terminar un video con menos trabajo. Recomiendo empezar por rangos de reproducción, marcadores,
regrabación automática de un tramo y fundidos de audio. Las prioridades y dificultades de este documento
son una evaluación para nuestra arquitectura, no afirmaciones del manual.

## Qué se relevó

El [enlace original](https://fenderstudiopromanual.fender.com/en/Studio-Pro-User-Manual.htm#Introduction.htm?TocPath=_____1)
abre un visor MadCap: el texto está en documentos separados bajo `en/Content/`. Se leyó
`Data/HelpSystem.xml`, se obtuvo el índice desde `Data/Tocs/Fender_Studio_Pro_User_Manual_Online.js`
y su `Chunk0`, y se descargaron **224 de 224 temas**, sin fallos. Se extrajo únicamente el cuerpo
`mc-main-content`: 1.668.885 caracteres en total, sin descargar imágenes, audio ni paquetes.

Se hizo lectura dirigida de grabación, edición, mezcla, guardado, controladores y video; la descarga
completa no equivale a una revisión exhaustiva de cada instrumento o parámetro. El
[índice del relevamiento](referencias/fender-studio-pro-indice.json) conserva títulos, enlaces, tamaños
y huellas de los textos extraídos, sin reproducir el manual. El sitio mezcla pies de página de distintas
versiones; las fuentes son las páginas consultadas en esa fecha.

Se cruzó con `AGENTS.md`, `README.md`, `PENDIENTES.md` y el código actual de grabación, reproducción,
clips, plantillas y exportación. Las funciones que figuran como hechas se reconocieron en la
documentación y el código; no se probó la app ni el hardware durante este relevamiento.

## Primero: mejoras con efecto directo sobre el trabajo diario

| Propuesta | Para qué nos sirve | Situación en WildTrack | Dificultad estimada |
|---|---|---|---|
| Repetir un rango A–B | Practicar o mezclar ocho compases sin volver al principio | Hay loop de la duración completa, falta rango | Media |
| Marcadores con nombre | Saltar a Intro, Estribillo o Solo | No se encontró un modelo de marcadores | Baja–media |
| Regrabar un tramo automáticamente | Escuchar la entrada, tocar y terminar sin usar el mouse | Hay grabación desde el cabezal, falta salida automática y reproducción previa | Alta |
| Fundidos cruzados de audio | Unir una corrección con la toma anterior sin clics | Hay fades por clip, falta una operación entre clips | Media |
| Conservar colas al exportar | Evitar que se corte la última reverb o repetición | La duración actual termina en el último clip | Media |
| Presets de canal del usuario | Recuperar el sonido de una guitarra en otra pista | Hay presets de efectos y plantillas de proyecto | Baja–media |
| Bloquear clips o pistas | Proteger una toma terminada de movimientos y reemplazos accidentales | No se encontró esa propiedad | Media |
| Volver a versiones desde Historial | Recuperar una edición sin usar consola | El guardado y la restauración ya existen; falta acceso en la ventana | Baja–media |

### 1. Rango A–B reutilizable

Fender permite definir un tramo con localizadores y repetirlo; también ofrece que la repetición siga
la selección. Adaptación: arrastrar sobre la regla y ofrecer **“Repetir este tramo”**. La misma región
podría servir después para regrabar o exportar un fragmento.
[Fuentes: grabación en loop](https://fenderstudiopromanual.fender.com/en/Content/Recording_Topics/Loop_Recording_on_Audio_Tracks.htm)
y [edición, Loop Follows Selection](https://fenderstudiopromanual.fender.com/en/Content/Editing_Topics/Common_Editing_Actions.htm).

Hoy `PlaybackEngine.Process` vuelve a cero al llegar al final, solamente cuando no graba. Harían falta
límites de rango y resolver el cruce de bloque de audio con precisión de muestras. La selección temporal
de borrado de `TimelineView` desaparece con una edición: conviene distinguirla de un rango persistente.
También hay que decidir cómo se reinician los efectos al repetir para que el salto resulte predecible.

### 2. Marcadores de partes del tema

Fender admite marcadores con nombre y navegación entre ellos. Para nosotros alcanzaría una franja
discreta en la regla, con agregar en el cabezal y saltar al anterior/siguiente mediante atajos
configurables. **“Solo”** podría convertirse después en el punto donde cambia el layout del video.
[Fuente: Marker Track](https://fenderstudiopromanual.fender.com/en/Content/Mixing_Topics/Using_the_Marker_Track.htm).

Primera versión: posiciones en segundos, coherentes con el modelo actual. No moverlas silenciosamente
al cambiar BPM. Los marcadores musicales que sigan compases serían una ampliación explícita.

### 3. Regrabar un tramo con reproducción previa y salida automática

El manual distingue cuenta de click, reproducción previa del tema, entrada/salida automática de
grabación y reproducción posterior. Es especialmente útil para corregir una frase con la guitarra
colgada. Propuesta: **“Regrabar este tramo”**, escuchar uno o dos compases previos y grabar solamente
dentro del rango, con una continuación breve opcional para evaluar la unión.
[Fuente: Activating Recording](https://fenderstudiopromanual.fender.com/en/Content/Recording_Topics/Activating_Recording.htm).

Esto amplía lo que ya existe: `TakeRecording.AddTake` reemplaza el tramo cubierto cuando se empieza
después de cero. La cuenta actual todavía no reproduce las pistas anteriores: `PlaybackEngine.Process`
las habilita al llegar a `recStart`. Además, `RecordMuted` silencia toda la pista durante la grabación;
para escuchar el antes y el después habría que silenciarla solamente en el intervalo reemplazado.

Es un trabajo de motor, recuperación y sincronía de cámara, no solamente un botón. El corte exacto
de audio debe decidirlo el motor; el guardado y cierre de archivos deben seguir fuera del callback.
También hay que revisar el caso especial actual: comenzar en cero reemplaza toda la pista.

### 4. Fundidos cruzados entre clips

Fender combina eventos mediante fundidos cruzados y los aplica al promover fragmentos de distintas
tomas. Nos serviría una acción **“Unir con fundido”** y un fundido breve en los límites de una
regrabación. La duración podría ajustarse después.
[Fuentes: Crossfade](https://fenderstudiopromanual.fender.com/en/Content/Editing_Topics/Common_Editing_Actions.htm)
y [Comping](https://fenderstudiopromanual.fender.com/en/Content/Editing_Topics/Comping.htm).

Nuestros fades de clip son una base, pero falta coordinar ambos lados de la unión y verificar que haya
material disponible para solapar. La misma envolvente debe evaluarse en reproducción y mezcla offline.
El fundido de audio y una transición visual son decisiones separadas: no conviene disolver la imagen
automáticamente cada vez que se corrige una nota.

### 5. Colas de efectos y exportación de un fragmento

El manual ofrece tiempo adicional para la caída de efectos y selección del tramo exportado.
En WildTrack, `MixRenderer.MixDurationSec` calcula el final del último clip y los exportadores usan
esa duración: una cola que siga sonando después puede quedar cortada. Propongo **“Conservar colas”**,
con duración máxima, y exportar un rango para sacar un adelanto del tema.
[Fuente: Mixing Down, Export Range](https://fenderstudiopromanual.fender.com/en/Content/Mixing_Topics/Mixing_Down.htm).

Hay que continuar procesando los buses con entrada silenciosa y acordar el cierre visual del MP4
durante ese tiempo. Audio, video y stems deben compartir límites explícitos. Para exportar desde un punto
intermedio, el estado previo de los efectos también importa: empezar con buses vacíos puede cambiar
el sonido respecto de escuchar ese tramo dentro del tema.

### 6. Guardar y cargar un sonido de canal

Los Track Presets del manual recuperan configuraciones de pista y canal. Adaptación pequeña:
**“Guardar sonido…”** y **“Cargar sonido…”** desde el inspector o mixer, con nombres del usuario como
“Tele limpia” o “Solo con delay”.
[Fuente: Track Presets](https://fenderstudiopromanual.fender.com/en/Content/Editing_Topics/Track%20Presets.htm).

Ya existe `Channel` serializable y almacenamiento de plantillas en `ProjectTemplates`. Un preset de
sonido reutilizaría ese patrón, aplicándose a una pista existente sin tocar sus clips. Conviene comenzar
por efectos, volumen y paneo, y evitar cambiar automáticamente la entrada física al recuperar un sonido.
Los retornos compartidos de reverb y delay necesitan una regla clara: guardar el envío de la pista no
equivale a guardar el bus completo del proyecto.

### 7. Bloquear lo que ya quedó bien

Fender distingue bloqueo temporal y de edición, y permite bloquear una pista contra grabaciones nuevas.
En nuestra app protegería una toma cuyo audio y encuadre ya están listos.
[Fuente: Lock Tracks or Events](https://fenderstudiopromanual.fender.com/en/Content/Editing_Topics/Lock_Track_Lock_Event.htm).

Empezaría con una acción de contexto **“Bloquear edición”**, reversible y visible. Debe cubrir mover,
recortar, borrar y reemplazar al grabar; conviene permitir seguir mezclando. La protección tiene que
vivir también en `Projects`, para que los comandos de `wtlab` y futuras herramientas respeten la regla.

### 8. Historial de versiones accesible

El manual expone guardar una versión y restaurarla desde la interfaz. Nuestro equivalente ya existe
en `ProjectStore` y `wtlab`, y `PENDIENTES.md` reconoce que falta dónde tocarlo en la ventana.
Recomiendo un bloque **“Versiones guardadas”** en Historial, con fecha, nombre opcional y restauración
conservando el estado anterior, como hace el almacenamiento actual.
[Fuente: Saving Options](https://fenderstudiopromanual.fender.com/en/Content/Saving%2C%20Import%20and%20Export/Saving%20Options.htm).

## Después: desarrollar ideas que ya están en nuestros pendientes

| Idea existente | Qué aporta el manual | Alcance recomendado para WildTrack |
|---|---|---|
| Grabación en loop y comping | Pasadas separadas, escucha de alternativas y elección por fragmentos | Primero elegir una pasada completa; luego combinar frases. Mantener juntos audio, video y corrección de sincronía. [Comping](https://fenderstudiopromanual.fender.com/en/Content/Editing_Topics/Comping.htm) |
| Pedalera MIDI y MIDI learn | Vincular un control físico con una acción o parámetro | Priorizar grabar/parar, play y marcadores; después aprender perillas. No exige esperar a pistas MIDI con instrumentos. [Control Linking](https://fenderstudiopromanual.fender.com/en/Content/Control_Link_Topics/Control_Linking.htm) |
| Automatización | Curvas editables y distintos modos de captura | Empezar dibujando volumen/paneo y parámetros de video. La curva de ganancia por clip existente no reemplaza una automatización de canal. [Automation Types](https://fenderstudiopromanual.fender.com/en/Content/Automation_Topics/Automation_Types.htm) |
| Mejorar el mixer | Grupos y control conjunto | Unir dos rítmicas para ajustar su volumen conservando diferencias; distinguir grupo de edición, grupo de faders y bus de suma. [Groups](https://fenderstudiopromanual.fender.com/en/Content/Mixing_Topics/Groups.htm) |
| Amp sim y cargador de IR | Cadena de ampli, gabinete y pedales | Un sonido DI inicial fácil de ajustar y un cargador de IR; investigar el DSP y las licencias de los IR por separado. El manual describe Ampire, no entrega su implementación. [Distortion](https://fenderstudiopromanual.fender.com/en/Content/Built-In_Effects_Topics/Distortion.htm) |
| Importar video y extraer audio | Importación conjunta o solamente del audio | Incorporar material del celular conservando originales; la sincronización automática por correlación sigue siendo una propuesta nuestra. [Import Video](https://fenderstudiopromanual.fender.com/en/Content/Video_Playback_and_Sync_Topics/Import_Video.htm) |
| Volumen de exportación y presets | Objetivo de sonoridad y límite de pico verdadero | Aprovechar LUFS existente; falta medir true peak y definir el ajuste offline. Usar objetivos editables, sin asumir que un valor sirve para todas las plataformas. [Target Loudness](https://fenderstudiopromanual.fender.com/en/Content/Mixing_Topics/Mixing_Down.htm) |
| Borrar cerrando el hueco y rangos multipista | Edición con desplazamiento posterior | Mover juntos audio, video y futuros marcadores; ofrecerlo como acción explícita. [Ripple Edit](https://fenderstudiopromanual.fender.com/en/Content/Editing_Topics/Common_Editing_Actions.htm) |

El comping con cámara merece especial cuidado: elegir otro fragmento debe elegir también sus cuadros
correspondientes. La transición puede resultar visualmente evidente aunque el audio quede perfecto.
La grabación en loop tampoco sale de habilitar el toggle actual: hacen falta conservar cada pasada,
sus offsets de cámara y su recuperación si se interrumpe la grabación.

## Otras propuestas útiles, con menor prioridad

- **Comparar mezclas A/B:** guardar dos estados de canales y buses para elegir entre una mezcla seca
  y otra con más ambiente. Una primera versión puede limitarse al sonido; no hace falta toda la gestión
  de visibilidad de Fender. [Scenes](https://fenderstudiopromanual.fender.com/en/Content/Mixing_Topics/Scenes.htm).
- **Chequeo mono y fase:** escuchar el master en mono y sumar una lectura de correlación para revisar
  doblajes o capturas con varios micrófonos. Debe quedar claro si afecta solamente la escucha o también
  el archivo final. [Metering, Analysis, and Signal Generation](https://fenderstudiopromanual.fender.com/en/Content/Built-In_Effects_Topics/Metering%2C%20Analysis%2C%20and%20Signal%20Generation.htm).
- **Puerta o expansor suave:** útil para ruido entre frases de guitarra eléctrica; empezar con pocos
  parámetros y cuidar que no corte el sustain. [Dynamics](https://fenderstudiopromanual.fender.com/en/Content/Built-In_Effects_Topics/Dynamics.htm).
- **Paquete portable del proyecto:** exportar un ZIP con JSON y medios, útil para trasladar o respaldar.
  A diferencia del paquete reducido del manual, una copia de seguridad nuestra debería poder incluir
  tomas descartadas e historial; no confundir “para compartir” con “respaldo completo”.
  [Zip Export and Import](https://fenderstudiopromanual.fender.com/en/Content/Saving%2C%20Import%20and%20Export/Zip_export_and_import.htm).
- **Rescatar lo que acabo de tocar:** adaptación propia inspirada en Retrospective Recording. El tema
  del manual habla de **MIDI**, no de un búfer de audio o cámara. Para WildTrack se podría investigar un
  anillo preasignado de audio, opcional y con duración acotada; recuperar video requeriría otro diseño.
  [Retrospective Recording](https://fenderstudiopromanual.fender.com/en/Content/Fundamentals_Topics/Retrospective_Record.htm).
- **Congelar pistas cuando lleguen VST:** renderizar temporalmente pistas costosas conservando originales
  y estado de efectos. Tiene más sentido junto con plugins que antes de ellos.
  [Track Freeze](https://fenderstudiopromanual.fender.com/en/Content/Editing_Topics/Track_Freeze.htm).

## Qué ya tenemos y qué postergaría

Ya están cubiertos el transporte inferior, cuenta previa, metrónomo, armar varias pistas, entradas
por pista, monitor, afinador, medidor LUFS, ganancia y curva por clip, fades, mixer, stems, atajos
configurables, plantillas, deshacer y autoguardado. Hay ampliaciones posibles, pero no deben volver
a aparecer como funciones completamente ausentes.

También existe `PlaybackEngine.StopTransport`, que vuelve al punto desde donde empezó la reproducción:
no lo propongo como una novedad a partir del manual.

Postergaría la página independiente de mastering, Dolby Atmos, un editor completo de partituras,
Launcher, Show y servicios de publicación en la nube. Son áreas del manual que tienen utilidad en
otros flujos; hoy sumarían bastante superficie antes de resolver la grabación y el montaje de guitarra.
La idea ya anotada de tablatura en pantalla puede abordarse sin construir un editor de notación entero.

## Orden sugerido

1. **Edición y revisión:** rango A–B, marcadores y versiones desde Historial; corregir el cierre de colas
   de exportación como mejora independiente.
2. **Regrabar con comodidad:** entrada/salida automáticas, reproducción previa y fundidos de audio.
3. **Elegir la mejor interpretación:** pasadas en loop, selección de toma y después comping por frases.
4. **Reutilizar y controlar:** presets de canal, bloqueo de edición y pedalera MIDI; el control MIDI
   puede adelantarse si ya hay una pedalera disponible para probar.
5. **Dar forma al resultado:** automatización de mezcla y del director de video, e importación del celular.

Todo cambio de motor o proyecto debería poder ejercitarse desde `wtlab`, compartir comportamiento
entre reproducción y exportación y conservar medios originales. No se asignan plazos: la dificultad
indicada es relativa y todavía no hay un diseño técnico completo de cada función.
