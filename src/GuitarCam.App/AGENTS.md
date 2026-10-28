# Ventana (GuitarCam.App) — reglas de diseño

Se suma a la guía de la raíz ([AGENTS.md](../../AGENTS.md)). Cómo es y dónde vive cada parte de la ventana (barra de
título, transporte, inspector, timeline, mixer, carpetas, grabación…) está en [docs/interfaz.md](../../docs/interfaz.md):
leé el apartado de lo que vas a tocar, y si cambiás cómo funciona, actualizalo. Los controles que dibujan a mano tienen
sus reglas en [Controls/AGENTS.md](Controls/AGENTS.md).

## Principios

- **Oscura, sobria, de estudio.** Fondo casi negro, superficies apenas más claras, un solo color de acento. Lo que
  resalta es el contenido (el video y las ondas), no los controles. El tema es siempre oscuro (`RequestedThemeVariant="Dark"`).
- **Coherente consigo misma.** Ante la duda, mirá cómo lo hace el resto de la ventana.
- **Se usa con la guitarra colgada**: atajos de una tecla, objetivos de clic cómodos, nada que exija precisión ni
  varios pasos para grabar o reproducir.
- **Siempre se ve el estado**: qué está sonando, si hay audio, si un archivo falta, si se guardó.
- Lo que no está hecho se muestra deshabilitado o con un aviso. Nunca un botón que no hace nada y no dice nada.
- No hay MVVM ni bindings: code-behind con controles con nombre (`x:Name`). Las ediciones del inspector pasan por
  `Edit(t => ...)` (marca sucio, refresca textos, redibuja el timeline); el flag `syncing` evita que llenar el
  inspector dispare ediciones.

## Textos (`Loc`)

Los textos salen de `Loc` (`Loc.T("clave")`, o `{loc:T clave}` en XAML) y viven en `Assets/Lang/<código>.json`;
**es.json es la referencia** (lo que falta en otro idioma cae al español). Texto nuevo → clave nueva en todos los
idiomas. Los nombres que vienen del motor en español (estilos del compresor, presets de EQ, reverb y delay,
divisiones) se traducen al mostrarlos con `Loc.Or(clave, texto del motor)`. Los títulos de sección del inspector,
de Color y de Historial son ids estables en español (`SectionTitle` y `bar.section.*` los traducen al mostrarlos),
porque con ellos se recuerda qué secciones están plegadas. El idioma se elige en Configuración, se guarda en
`config.json` (`language`) y se aplica al reiniciar.

## Colores (tokens)

Definidos **dos veces y tienen que coincidir**: `App.axaml` (XAML) y `Controls/Palette.cs` (lo que se dibuja). El
selector de tema (`UiTheme.cs`: 16 acentos, 9 fondos oscuros) cambia en vivo los pinceles de superficie y acento, así
que usá siempre el pincel (`Palette.Accent`, `{StaticResource Accent}`), nunca un color escrito a mano, y leé
`Palette.AccentColor` y compañía al dibujar, sin guardarlo. Variantes del acento en XAML: `AccentFg` (texto sobre
acento), `AccentHover`, `AccentTint`, `Accent25/40/50/70`. Valores del tema por defecto:

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
| `Accent` | `#22d3ee` | **el** color: activo, seleccionado, play, playhead, ondas, medidores (hover `#4fdcf1`) |
| `AccentSoft` | `#2422d3ee` | fondo de chips activados y del badge "NATIVE" |
| `Rec` | `#ef4444` | grabar y clip/saturación de nivel. Nada más |
| `RecSoft` | `#24ef4444` | fondo suave asociado a grabación |
| `Composition` | `#09090b` | fondo del cuadro del video |
| ámbar | `#f59e0b` | mute activo y zona de −6 a 0 dB en medidores |
| gris offline | `#71717a` | clips cuyo archivo falta |
| `InTune` | `#22c55e` | "afinado" en el afinador (el verde de los medidores del mixer, `Palette.MeterGreen`) |

No inventes colores para estados nuevos sin necesidad; el acento no es decoración; el rojo es **grabación o peligro
de clip**, nunca "error genérico". Las transparencias salen de `Palette.Alpha(color, a)` (relleno de clip 0,10 / 0,16
seleccionado, borde 0,45 / 0,9). Los colores de pista son datos del usuario, no tokens.

## Tipografía e íconos

- UI: **Segoe UI Variable Text** (fallback Segoe UI). Números y tiempos: **Cascadia Mono** (fallback Consolas).
  **Todo valor numérico va en mono** (`Classes="dim mono"`), alineado a la derecha de su etiqueta.
- Íconos: **Segoe Fluent Icons** (fallback Segoe MDL2 Assets), `TextBlock` con clase `icon` y el glifo en `Text`. Sin
  paquetes de íconos ni imágenes. Excepciones: los efectos (Vida, compresor, EQ, reverb, delay, transponer) siempre con `FxIcon`
  (trazos de lucide), en todo lugar que muestre un efecto; los instrumentos de las pistas con `TrackIcons` (rellenos:
  Fluent Emoji High Contrast y lo que le falta de otros sets libres; licencias en `Assets/Icons/LICENSES.txt`,
  generados con `tools/make-track-icons.py`).
- Escala (no inventes tamaños intermedios):

| Tamaño | Uso |
|---|---|
| 10.5 SemiBold, mayúsculas, espaciado 0.8, `Faint` | etiquetas de sección (`TextBlock.label`) |
| 11 | info secundaria chica, textos dentro del timeline |
| 12 `Dim` | texto secundario (`TextBlock.dim`), chips |
| 12.5 SemiBold | nombre de pista, botón de acento |
| 13.5 SemiBold | nombre de la app en la barra de título |
| 14 / 19 mono | BPM / reloj del transporte |
| 16 SemiBold | título del inspector (nombre de la toma) |

## Formato de valores

Respetan la cultura del sistema (en es-AR, con coma):

- Ganancia: `$"{db:+0.0;-0.0;0.0} dB"` (`+3,5 dB`); volumen cero: `-∞ dB`.
- Paneo: `Centro`, `30 L`, `30 R` en el inspector; `C`, `L30`, `R30` compacto en el encabezado de pista.
- Tiempo: `mm:ss.fff` en el reloj; compás.tiempo (`2.2`) al lado.
- Porcentajes con palabra: `50% · Con vida`. Separador de datos en una línea: ` · `.
- Fechas relativas: "recién", "hace 5 min", "hoy 14:30", "ayer 09:10", `dd/MM HH:mm`.

## Formas y espaciado

- Radios: **8** secciones, **6** botones/chips/cajas, **5** clips y badges, **4** toggles M/S y etiquetas, **2** barras de medidor.
- Bordes de 1 px en `Line`. Sin sombras, sin gradientes, sin animaciones decorativas. Única sombra: el **glow del
  hover** de todo botón (`HoverGlow`, un `DropShadowEffect` del acento que recolorea `UiTheme`; `RecGlow` en los
  rojos), puesto al final de los estilos de botón en `App.axaml` para que ninguna clase lo pierda. **El texto nunca
  brilla**: los botones transparentes con texto (filas enteras `rowmain` / `tplmain`, `link`), los `CheckBox` y las
  muestras de color van sin glow, porque se dibujaría alrededor de las letras. Excepción pedida por el usuario: el
  botón de guardar, mientras hay algo para guardar, lleva clase `pending` (brilla y late suave con la opacidad;
  `failed` en rojo si no se pudo guardar). En las filas el hover lo marca la fila
  (fondo `Raised` y borde `Line`); en el `CheckBox` brilla solo la cajita. Va como `Effect`
  del botón: un `BoxShadow` en el presentador queda cortado. Otra excepción pedida por el usuario: la tapa del fader
  (`Fader`) imita la de una consola, con sombreado, ranuras y una sombra abajo, para que se vea y se agarre fácil. Los botones de color (acento, primario, peligro, grabar,
  `lit`) no cambian de color en hover: el glow es la respuesta. Su fondo y su texto van en el presentador
  (`/template/ ContentPresenter#PART_ContentPresenter`, sin estado en el selector), como los chips ON: puesto solo en
  el `Button`, el texto quieto sale blanco del Fluent.
- Un botón que pinta sus colores desde código (M/S encendidos, armar, ON, C/LIM, muestras de color) lleva la clase
  **`lit`** mientras está encendido: si no, el hover le tapa el color con gris.
- **Scrollbars** (pedido del usuario): de 8 px (`ScrollBarSize`) y sin flechas, siempre de ese tamaño cuando hacen falta, y sin aparecer si el contenido
  entra. El contenido les deja su lugar al costado, no pasa por debajo (estilo global en `App.axaml`). No uses el
  `Padding` de un `ScrollViewer` para separar el contenido: queda fuera de lo que se desplaza. Usá `Margin` en el contenido.
- Padding de sección `12,10`; separación entre bloques 10–12; márgenes del inspector `14,16`. Alturas mínimas: chips 30, transporte 36×36.

## Componentes (clases de `App.axaml`)

Para un control nuevo, primero reutilizá estas. Los controles Fluent (sliders, switches) toman el acento de
`SystemAccentColor*`; no les pongas colores a mano.

| Clase | Qué es |
|---|---|
| `Button.chip` / `ToggleButton.chip` | botón normal de contorno: fondo transparente, borde `Line`, texto `Dim` (`Faint` si es un toggle apagado). Activado (o `.on`): fondo `AccentSoft`, borde acento al 40 %, texto `Accent`. Deshabilitado: opacidad 0,35 |
| `Border.seg` + `ToggleButton.seg` | control segmentado: caja `Raised` y la opción activa como pastilla blanca al 11 %. `.seg.pick` = variante de acento |
| `Border.metro` + `ToggleButton.cell` + `Border.vsep` | recuadro oscuro del transporte, con celdas valor arriba (`TextBlock.metro-value`) y etiqueta abajo (`TextBlock.metro-label`) |
| `StackPanel.bar-cell` | celda de la barra de transporte (valor arriba, `metro-label` abajo) |
| `Button.tbtn` / `ToggleButton.tbtn` | botones del transporte: planos, 36×36, ícono `Fg`, hover `Hover`; activo (o `.on`), ícono `Accent`. `Button.clock` = el reloj |
| `Button.ghost` | botón de ícono sin fondo (barra de título) |
| `Button.accent` | **la** acción principal; una sola por pantalla ("Exportar MP4" / "Exportar audio") |
| `Border.section` (+ `.off`) | tarjeta del inspector |
| `TextBox.field`, `Button.btn` (`.sm`, `.primary`, `.outline`, `.danger`), `Button.act` (`.danger`) | campos y botones de diálogos y listas: botón de fila 28×28, confirmación roja |
| `Button.chip.purge` | "Borrar no usadas": contorno tenue que se pone rojo con el hover |
| `TextBlock.label` / `.dim` / `.mono` / `.icon` | etiqueta / secundario / números / ícono |

## Atajos

- Son configurables: la tabla de acciones y sus teclas por defecto es `Shortcuts.cs` (la lista para el usuario está en
  el README); Configuración → "Atajos de teclado" las cambia y se guardan en `config.json` ("shortcuts").
- Un atajo nuevo se agrega a `Shortcuts.All` y se atiende en el `switch` de `MainWindow.OnKey` (evento en túnel, vía
  `Shortcuts.Find`) por su id, nunca comparando `Key` a mano. Esc no se configura: siempre cancela.
- Los modificadores de la ruedita en el timeline (zoom, costados, altura) también son configurables: `Shortcuts.Wheel`,
  `Shortcuts.FindWheel` (guardados aparte, "wheel"); no compares `KeyModifiers` a mano para eso.
- El atajo se muestra **solo en el tooltip**, entre paréntesis y leído de `Shortcuts` (`Shortcuts.Tip(texto, id)`;
  los fijos, registrados en `shortcutTips`). Nunca en el texto visible del control.
- Todo botón de ícono tiene `ToolTip.Tip`. Los cambios se aplican en vivo (se escucha mientras movés el slider). El
  paneo se imanta al centro (|x| < 0,04).

## Menús

- Las opciones que se tildan o se prenden y apagan en un menú (grilla, marcas de duración, entrada de la pista, plegar
  carpeta, ocultar separador) van con `MenuCheck` / `MenuChoice` (pedido del usuario): clic izquierdo elige y cierra,
  clic derecho elige y deja el menú abierto, y el tilde o el texto se actualizan en el lugar. Lo que se lee al
  aplicar se busca de nuevo (por id), porque el menú sigue abierto mientras cambia. Las acciones comunes, con `MenuAction`.

## Mensajes al usuario

- Van a la campanita (`Status`) o, si conviene verlos en el momento, a un toast (`Toast`). En primera persona y en
  criollo: **qué pasó + qué hago ahora o qué podés hacer** ("No pude guardar: {motivo}. Lo vuelvo a intentar…").
- Nada de diálogos modales para errores, ni stack traces, ni jerga ("excepción", "null").
- Un archivo que falta no rompe nada: el clip queda **offline** (gris, "Sin conexión: falta el archivo de audio").
- Cargas largas en segundo plano (`Task.Run`) con un aviso; la UI nunca se congela esperando disco, FFmpeg o el driver.
