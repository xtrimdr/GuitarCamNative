# Controles dibujados a mano

Se suma a [src/GuitarCam.App/AGENTS.md](../AGENTS.md). `TimelineView`, `PreviewView`, `StereoMeter` y los demás que
heredan de `Control` dibujan en `Render`. Lo que corre en cada `Render` o `Tick` no asigna memoria: la basura de la UI
también corta el audio (ver "Audio en tiempo real" en la guía de la raíz).

- Texto, pinceles y plumas siempre de `Palette` (`Palette.Text` / `ctx.DrawText(...)`, `Palette.Pen`, `Palette.Alpha`),
  que los cachea por contenido y color. Nada de `new Pen`, `FormattedText` ni pinceles por cuadro. `Palette.Text` da
  un `TextLayout` que se arma una vez y no se modifica.
- Cacheá lo caro (ver el caché de ondas por clip) y redibujá con `InvalidateVisual()` solo cuando algo cambió (un
  medidor quieto o un cabezal parado no redibujan: `TimelineView.SetPlayhead`, `StereoMeter.Push`).
- Lo que se mueve en cada cuadro va en una capa aparte: el cabezal y los medidores de los encabezados del timeline se
  dibujan en `TimelineView.Layer`, y mientras suena solo se redibuja esa capa.
- Las líneas de 1 px van en medio píxel (`y + laneH - 0.5`) para que queden nítidas.
- El control expone eventos (`SeekRequested`, `SelectionChanged`, `TrackToggled`…) y la ventana decide qué hacer. El
  control no toca el motor ni el guardado.

## Medidores

`StereoMeter`: ataque instantáneo, caída de 24 dB/s, pico retenido 1,5 s, rango −60 dB (−48 en los de pista). Acento
hasta −6 dB, ámbar de −6 a 0, rojo arriba de −1 dB (pico) o −0,3 dB (luz de clip). La luz de clip queda prendida
hasta que se hace clic en el medidor. Los medidores del mixer usan otro degradé (verde → amarillo → rojo,
`Palette.Meter*`). Los picos los escribe el hilo de audio y los lee y resetea la UI en su `Tick` (16 ms); la caída
visual (×0,82 por tick en pistas, 24 dB/s en el estéreo) se hace del lado de la UI.
