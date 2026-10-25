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
- [x] Edición de clips: dividir, duplicar, mover a otra pista y a otro tiempo, borrar un pedazo (rango) (hechos). Falta
  borrar el rango "cerrando el hueco" (que lo de después se corra) y rangos que abarquen varias pistas
- [x] Ganancia por clip con su curva de volumen, y la onda dibujada con la curva aplicada (hecho, ver [HECHO.md](HECHO.md))
- [ ] Varios clips por pista: ya se reproducen, se dibujan, se exportan (comparten el panel) y se graba desde el
  cabezal (hecho, ver [HECHO.md](HECHO.md)); falta elegir qué clip se ve cuando se pisan
- [x] Importar audio externo (hecho, ver [HECHO.md](HECHO.md)). Falta: que el clip importado siga el tempo del proyecto (hoy se
  usa tal cual) y importar el audio de un video
- [ ] Mejorar el mixer: inserts reordenables, más sends/buses, grupos, medidores más completos
- …y más: ir sumando acá

#### 2. Soporte de plugins VST
- [ ] Cargar VST3 en las pistas y en los buses (amps, pedales, reverbs de terceros)
- Notas: en .NET no hay un host VST3 listo; probablemente haga falta un puente en C++.
  Conviene correr los plugins en otro proceso, así si uno se cuelga no tira abajo la grabación.
  La exportación tiene que pasar por los mismos plugins, igual que hoy el mix offline usa el mismo grafo.

#### 3. MIDI
- [ ] Pedalera o controlador MIDI: grabar/parar, play, marcar tomas, con los pies y sin soltar la guitarra
- [ ] Mapear perillas y faders de un controlador al mixer (MIDI learn)
- [ ] Pistas MIDI con instrumentos VST (batería, bajo) para armar la base
- [ ] MIDI clock / tempo sincronizado con otros equipos

#### 4. Edición de video más completa
- [ ] Color por toma, además del color global
- [ ] Más filtros y efectos (nitidez, desenfoque, glow, estabilización, chroma key/pantalla verde)
- [ ] Textos y títulos: nombre del tema, créditos, carteles
- [ ] Transiciones entre clips y entre layouts
- [ ] Recorte y encuadre animados (zoom/paneo que cambian con el tiempo)

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
- [ ] **Exportar con presets para redes**: Reels/Shorts/TikTok (9:16), YouTube (16:9), Instagram
  (1:1, 4:5), con el volumen normalizado al que pide cada una (ej. -14 LUFS)
- [ ] **LUFS de la mezcla al terminar de exportar** (el medidor del master ya está, ver [HECHO.md](HECHO.md)):
  sirve para los presets de redes
- [ ] **Visualizadores de audio** como capa del video (onda o espectro animado)
- [ ] **Acordes / tablatura / letra en pantalla** mientras grabás (tipo teleprompter) y opcionalmente en el video
- [ ] **Exportar con la GPU** (NVENC) para que el render sea mucho más rápido

### Opcional — ideas de Fender Studio Pro (2026-10-24)

Se relevaron los 224 temas del manual y se cruzaron con la nativa. El detalle, las fuentes y el orden
sugerido están en [docs/FENDER_STUDIO_PRO_ANALISIS.md](docs/FENDER_STUDIO_PRO_ANALISIS.md).
Estas ideas son opcionales, sin compromiso de implementación. Comping, MIDI, automatización, amp sim, importación
de video y normalización ya están anotados arriba; el análisis amplía su alcance sin duplicarlos acá.

- [ ] **Rango A–B de reproducción**: repetir un tramo para practicar o mezclar; base compartida para
  regrabación y exportación parcial. Hoy el loop vuelve a cero al final del proyecto.
- [ ] **Marcadores con nombre**: Intro / Estribillo / Solo, agregar en el cabezal y navegar con atajos;
  empezar con posiciones en segundos, útiles también para el futuro director de video.
- [ ] **Regrabar un rango automáticamente**: escuchar compases previos del tema, grabar solamente entre
  entrada y salida y escuchar el después. Amplía la grabación actual desde el cabezal; requiere silenciar
  la toma anterior solo dentro del tramo y conservar la sincronía de cámara.
- [ ] **Fundidos cruzados de audio**: unir clips o correcciones con un fundido breve ajustable, igual en
  reproducción y exportación; distinguirlo de las transiciones visuales.
- [ ] **Conservar colas de efectos al exportar**: hoy la duración llega al último clip; dejar terminar
  reverb/delay con un límite y definir el cierre visual del MP4. Añadir también exportación de un rango.
- [ ] **Presets de canal del usuario**: guardar y cargar un sonido en una pista existente, sin sus clips;
  definir qué pasa con los envíos y los retornos compartidos.
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

- Parar (Espacio, ■) todavía corta seco: falta el fundido de salida, como ya tienen play, los saltos y el scrub.
  Ojo con quien llama a `Stop`/`Pause` y espera que pare en el acto (grabar, exportar).
- `AudioGapLog` es un diagnóstico provisorio: sacarlo (o dejarlo detrás de una opción) cuando el audio esté confirmado.

- Puntitos en la preview (resuelto): con la ventana permitiendo transparencia, algunos píxeles oscuros
  del video dejaban ver la ventana de atrás. `TransparencyLevelHint="None"` lo corta, y con el video
  decodificado quedan solo unos pocos píxeles negros sobre casi negro, invisibles. Ojo al revisar
  capturas: las de GDI (CopyFromScreen/PrintWindow) dan alfa 0 en los píxeles negros y los visores los
  pintan blancos, aunque en pantalla se vean bien. Mirar el RGB, no el alfa.
- "Volver a una versión guardada" no tiene dónde tocarse en la ventana: el menú provisorio del chip se fue
  con el gestor y la pestaña Historial no lo trae. Hoy solo con `gclab proyecto versiones` / `volver`.
  Falta decidir dónde va.
- Lo que falta probar a mano (saltos del video mientras reproduce, fps con muchas tomas, calibración,
  medidores con sonido…) está todo en [docs/PRUEBAS.md](docs/PRUEBAS.md).
- Cámara: mientras la app está abierta, la cámara queda tomada, así que otro programa no
  la puede usar. Si se desconecta, el monitor dice "Sin señal de cámara" y no se reconecta sola: hay que
  tocar "Refrescar dispositivos".
