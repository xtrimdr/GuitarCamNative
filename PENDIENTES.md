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
- [x] Ganancia por clip con su curva de volumen (hecho, ver [HECHO.md](HECHO.md)). Falta: que la onda del clip
  se dibuje con la curva aplicada
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

## Notas

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
