# Pendientes — GuitarCam Native

Ideas y cosas para implementar en la versión nativa. Cada ítem: qué es, por qué, y notas si hay.
Al terminar uno, pasarlo a **Hecho** con la fecha.

## Ideas nuevas (solo nativa)

_Cosas que no están en la app web o que la versión nativa debería hacer distinto._

> **Foco:** la app sigue siendo para hacer videos de guitarra. Lo de DAW tiene que servir para eso
> y no puede complicar el camino de "grabo, mezclo y exporto el video".

### Pedidas

#### 1. Usarla también como DAW, solo audio
- [ ] Proyectos o pistas sin video: grabar solo audio (la pista no ocupa panel en el video)
- [ ] Edición de clips: dividir (cortar en el cursor), recortar, mover, duplicar, borrar un pedazo
- [ ] Ganancia por clip, aparte del fader de la pista (con su curva de volumen dentro del clip)
- [ ] Varios clips por pista (hoy es una toma por carril)
- [ ] Importar audio externo (WAV, MP3, FLAC…): pistas de acompañamiento, loops, tomas de otro lado
- [ ] Mejorar el mixer: inserts reordenables, más sends/buses, grupos, medidores más completos
- [ ] Exportar solo el audio (WAV/MP3), la mezcla o las pistas por separado (stems)
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
- [ ] **Automatización** de volumen, paneo y efectos en el timeline (base para lo anterior)
- [ ] **Varias cámaras por toma**: plano general + primer plano de la mano, y elegir cuál se ve en cada momento
- [ ] **Importar video del celular** y sincronizarlo solo por el audio de la cámara (correlación,
  como la calibración de latencia)
- [ ] **Amp sim + cargador de IRs de gabinete**: grabar la guitarra directa (DI) y que suene bien sin VSTs
- [ ] **Afinador** integrado (en la barra de transporte o en el monitor de entrada)
- [ ] **Grabación en loop / tomas múltiples (comping)**: grabar varias pasadas del solo y elegir
  los mejores pedazos
- [ ] **Exportar con presets para redes**: Reels/Shorts/TikTok (9:16), YouTube (16:9), Instagram
  (1:1, 4:5), con el volumen normalizado al que pide cada una (ej. -14 LUFS)
- [ ] **Medidor de LUFS** en el master
- [ ] **Visualizadores de audio** como capa del video (onda o espectro animado)
- [ ] **Acordes / tablatura / letra en pantalla** mientras grabás (tipo teleprompter) y opcionalmente en el video
- [ ] **Exportar con la GPU** (NVENC) para que el render sea mucho más rápido

## Falta portar de la app web

_Lo que la web ya tiene y el lab todavía no. Ya portado: saturación, compresor, limitador master,
análisis de nivel, sonidos de click, ASIO dúplex._

- [ ] EQ de pista (6 bandas, `audio/eq.ts`)
- [ ] Reverb (bus con impulso sintético) y delay (bus con ducking)
- [ ] Mezcla completa: falta sends, EQ del compresor (`CompEq`), fades y automatizar presets
  (ya hay pistas, paneo, mute/solo, fader, master con limitador)
- [ ] Calibración de latencia guardada por dispositivo
- [ ] Captura de cámara y grabación de video
- [ ] Interfaz: mover/recortar clips, mixer, configuración, color; grabar desde la ventana
- [ ] Sesiones: guardar (hoy solo abre `session.json`, no escribe)
- [ ] Video real en la preview (hoy 1 cuadro por segundo, extraído con FFmpeg)
- [ ] Exportación a MP4 (FFmpeg, paneles, color, efectos de video)

## Problemas conocidos

- [ ] Preview: algunos píxeles oscuros del video salen con alfa 0 en la superficie de la ventana. Con la
  ventana opaca (`TransparencyLevelHint="None"`) se ven como puntitos negros casi invisibles; antes se
  veía la ventana de atrás a través. El render fuera de pantalla (`RenderTargetBitmap`) sale limpio, y
  no cambia escalando en CPU, forzando alfa 255 ni subiendo los negros: pasa entre el compositor de
  Avalonia y la pantalla. Probar con video decodificado de verdad (otra ruta de dibujo) antes de seguir.

## Hecho

- 2026-10-22: primera ventana (`src/GuitarCam.App`, Avalonia 11.3): abre el proyecto más reciente de
  la web, timeline con ondas, preview con la grilla de paneles portada, inspector de toma (volumen,
  paneo, saturación, compresor), reproducción por ASIO con click, medidores de entrada y de pista
