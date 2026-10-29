# Filtros y efectos de CapCut: qué tiene y qué nos sirve

Investigación del 2026-10-25. Sale de tres fuentes:

1. **Tu CapCut instalado** (versión 9.2.0, Microsoft Store): el preset de ajuste que guardaste
   (`%LOCALAPPDATA%\CapCut\User Data\Cache\onlineMaterial\Ajuste predefinido3.json`) trae la lista **exacta** de
   parámetros del panel Ajustar, y los 24 borradores de `User Data\Projects` dicen qué filtros, efectos y valores usás.
2. Las páginas de ayuda de CapCut y guías de terceros (poco precisas; los menús cambian seguido).
3. Lo que ya tiene WildTrack (`VideoColorProcessor`, `PreviewVideoFx`, `Mp4Exporter`).

No hizo falta el GitHub de REA: con los archivos locales alcanzó. **No copiamos los LUT ni los shaders de CapCut**
(son material de ellos); recreamos el *look* con nuestra propia matemática, como ya se hizo con "Brujo".

## 1. Lo que WildTrack ya tiene

- 18 looks (Brujo, Sepia, B/N, Noir, Cálido, Frío, Cine, Vintage, Película, Blockbuster, Bleach, Dorado, Noche,
  Nórdico, Neón, Sci-fi, Desierto) con intensidad (`Amount`).
- Ajustes: exposición, contraste, altas luces, sombras, saturación, intensidad (vibrance), temperatura, matiz, desvanecer.
- Viñeta (con tamaño), grano y franjas de cine.
- Todo lo de color se hornea en **un LUT 3D de 33³** que usan igual la vista previa y FFmpeg (`lut3d`). Esto es clave:
  **cualquier ajuste que dependa solo del color del píxel entra gratis en ese LUT**, sin costo en tiempo real.

## 2. El panel Ajustar de CapCut, completo

Del preset (campo → nombre en CapCut en español → ¿lo tenemos?):

| Campo | En CapCut | WildTrack |
|---|---|---|
| `brightness_value` | Brillo | ≈ Exposición |
| `contrast_value` | Contraste | ✅ |
| `saturation_value` | Saturación | ✅ |
| `natural_saturation_value` | Intensidad | ✅ Vibrance |
| `highlight_value` / `shadow_value` | Resaltes / Sombras | ✅ |
| `white_value` / `black_value` | **Blancos / Negros** | ❌ (entra en el LUT) |
| `light_sensation_value` | **Sensación de luz** (realza medios tonos de forma adaptativa) | ❌ (aproximable en el LUT) |
| `temperature_value` / `tone_value` | Temperatura / Tono | ✅ |
| `fade_value` | Desvanecer | ✅ |
| `vignetting_value` | Viñeta | ✅ |
| `particle_value` | Partículas (grano) | ✅ Grano |
| `sharpen_value` | **Nitidez** | ❌ (filtro espacial) |
| `clear_value` | **Claridad** (contraste local) | ❌ (filtro espacial) |
| `color_curves` (luma, R, G, B) | **Curvas** | ❌ (entra en el LUT) |
| `hsl` (8 colores × tono/sat/lum) | **HSL** | ❌ (entra en el LUT) |
| `hsl_curves` (tono-tono, tono-sat, tono-lum, lum-sat, sat-sat, sat-lum) | Curvas HSL | ❌ (entra en el LUT) |
| `primary_color_wheels` (lift, gamma, gain, offset) | **Ruedas de color** | ❌ (entra en el LUT) |
| `log_color_wheels` (sombras, medios, altas, offset) | Ruedas log | ❌ (entra en el LUT) |
| `filter` + `skin_protect_enable` | Filtro + **proteger piel** | Looks ✅, proteger piel ❌ |
| `lut` | **Importar LUT (.cube)** | ❌ |
| `color_match`, `color_correct`, `smart_color_adjust` | Igualar color / corrección automática (IA) | ❌ |

### Lo que usás vos de verdad

En tus borradores aparecen una y otra vez **Nitidez** (0,68 – 1,0), **Claridad** (0,21 – 1,0), Negros (−0,1 a −0,26),
Blancos, Sensación de luz, temperatura fría y saturación baja con contraste arriba. O sea: **lo que más te falta en
WildTrack es Nitidez y Claridad**, seguido de Blancos/Negros.

Filtros que usaste: **Brujo** (ya está), **Bosque negro**, **Negro vintage**, **Hielo negro**, **Noche santa**,
**Luz central**, **Sueño de Mistwood**. Categorías que muestra tu CapCut: "bw", "Filtros", "NEW".

## 3. Efectos de video de CapCut

En tus borradores: **Bokeh de ensueño** (Luz), **Luciérnaga**, **Chispa**, **Flash de paparazzi** (Chispas),
**Esfera de discoteca 1** (Fiesta), **Colorido** (club nocturno), **Constelación** (estrellas), **Punto hexagonal**,
**Fuga blanca**. Categorías públicas de la biblioteca: Tendencias, Básico, Lente, Luz, Retro, Fiesta, Chispas,
Estrellas, Distorsión, Glitch, Cinematográfico, Apertura y cierre, efectos de cuerpo y de IA.

Casi todos tienen los mismos 3–4 controles: **Velocidad**, **Intensidad**, **Animación de fondo**, **Tamaño**,
**Filtro** (cuánto tiñe). Eso nos sirve de modelo: cada efecto nuestro con 2–3 deslizadores, no más.

Los efectos de CapCut son escenas del motor "Amazing" de ByteDance (Lua + shaders); no se pueden reutilizar. Pero
los que usás son de tres familias fáciles de recrear:

- **Partículas de luz** (bokeh, luciérnagas, chispas, constelación, polvo): puntos que flotan con brillo suave.
- **Destellos** (paparazzi, flash, fuga blanca): fogonazos blancos breves.
- **Luces de fiesta** (discoteca, colorido): manchas de color que giran o titilan.

## 4. Propuesta para WildTrack, por orden de valor / esfuerzo

### A. Al LUT, sin costo en tiempo real (lo más barato y útil)

1. **Blancos y Negros** — dos deslizadores más en Color.
2. **Sensación de luz** — curva que abre los medios tonos protegiendo extremos.
3. **Importar LUT .cube** — se parsea a `Lut3DTable` y se encadena con el look; FFmpeg lo lee directo. Abre la
   puerta a miles de LUT gratis (y a exportar los tuyos desde otro editor).
4. **HSL** de 8 colores (rojo, naranja, amarillo, verde, cian, azul, violeta, magenta). Para guitarra: tocar el tono
   de la madera, bajar el verde de la pared, etc. Se puede reusar la idea de `FittedLook` (tablas por tono).
5. **Curvas** (luma + RGB) y, más adelante, **ruedas lift/gamma/gain**. Necesitan un editor de curva dibujado a mano.
6. **Nuevos looks** inspirados en los que usás: un B/N frío con negros profundos ("Hielo negro"), un B/N de bosque con
   verdes oscuros ("Bosque negro"), un B/N cálido de época ("Negro vintage"), uno nocturno azul-violeta, uno con
   luz central (viñeta fuerte + medios abiertos) y uno brumoso/pastel ("Mistwood"). Nombres nuestros, no los de CapCut.

### B. Filtros espaciales (cuestan CPU en la vista previa)

7. **Nitidez** → exportación: `unsharp=5:5:k` (radio chico). Vista previa: máscara de enfoque 3×3 sobre el cuadro
   decodificado, en el mismo paso que el LUT (`LiveClip.UpdateVideo`), sin asignar memoria.
8. **Claridad** → exportación: `unsharp=13:13:k` (radio grande, poca cantidad) con peso en medios tonos. Vista previa:
   desenfoque por cajas separable sobre el cuadro reducido a ¼, que es barato.
9. Después: **desenfoque** (fondo), **glow / bloom** (encaja con el look de escenario), **proteger piel**.

Medir la carga antes de decidir: la vista previa ya pasa cada cuadro por el LUT en CPU.

### C. Efectos animados (lo más vistoso, lo más trabajo)

10. **Partículas de luz** (bokeh/luciérnagas/chispas) generadas por código con semilla fija, así la vista previa y la
    exportación salen iguales. En la exportación se dibujan nosotros y entran a FFmpeg por pipe como capa `overlay`
    (el exportador ya mete la viñeta como `rawvideo` por entrada aparte).
11. **Lo que CapCut no puede hacer y nosotros sí: efectos que siguen la música.** Sabemos el tempo del click y tenemos
    el audio de cada toma: zoom o sacudida al pulso, destellos en los golpes del rasgueo, partículas que crecen con
    el volumen, viñeta que respira. Encaja justo con el foco de la app (videos de guitarra) y no agrega pasos.

## 5. Looks recreados (2026-10-25)

Cómo funcionan por dentro: los filtros de CapCut son un LUT (PNG de 512×512, 64³) y a veces un shader corto.
"Luz central" suma un suavizado de piel y una imagen de esquinas oscuras (en WildTrack: el look + la viñeta).
Los efectos (Luciérnaga, Bokeh, Chispa…) son secuencias de PNG prerenderizadas que se mezclan encima.

Para dar el mismo resultado sin usar sus archivos, cada look es un modelo nuestro (`FittedLook`: curva de tono,
brillo/saturación/giro por tono, saturación por brillo, virado y curvas por canal; ~120 números) ajustado por
mínimos cuadrados contra el filtro, con colores al azar y cuadros reales de tus tomas. Diferencia media medida en
cuadros que no se usaron para ajustar (0–255 por canal):

| Look WildTrack | Inspirado en | Diferencia |
|---|---|---|
| Brujo | Brujo | 1,6 |
| Solo rojo | Bosque negro | 6,5 |
| Plata | Negro vintage | 3,2 |
| Noche azul | Noche santa | 7,6 |
| Vivo | Luz central (solo el color) | 2,8 |
| Hielo | Hielo negro | 5,6 |

Por debajo de ~3 no se distingue; 6–8 se nota solo comparando lado a lado. Falta "Sueño de Mistwood" (es un shader
de 4 colores con ruido, no un LUT).

## 6. Diccionario: lo más usado en internet (2026-10-25)

No hay ranking oficial de CapCut (el orden de popularidad solo se ve adentro de la app). Esto sale de cruzar listas
de "mejores filtros/efectos", las páginas de plantillas de CapCut (con cantidad de usos) y guías para videos de
música. Lo que aparece en varias fuentes a la vez es lo que cuenta como "usable".

### Filtros (looks)

Se agrupan como en CapCut, por familia. ✅ = ya está en WildTrack.

| Familia | Lo más pedido | En WildTrack |
|---|---|---|
| **Natural** | Fresco / Limpio (más brillo, colores naturales), Brillante, Clásico (casi sin cambio) | ❌ Fresco · ❌ Limpio |
| **Cálidos** | Cálido, Dorado / Otoño, Verano | ✅ Cálido · ✅ Dorado · ✅ Desierto |
| **Cine** | Cine (sombras hondas), Teal & Orange, Película, Moody (oscuro y con contraste) | ✅ Cine · ✅ Blockbuster (= teal & orange) · ✅ Película · ❌ Moody |
| **Suaves** | Crema (colores suaves sin perder brillo), Suave / Soñado (menos contraste + brillo difuso), Pastel | ✅ Crema · ✅ Soñado (con Resplandor) |
| **Retro** | Retro / película desvaída, Vintage, VHS | ✅ Vintage · ✅ Bleach · ✅ Sepia |
| **B/N** | B/N, B/N de alto contraste, color selectivo | ✅ B/N · ✅ Noir · ✅ Plata · ✅ Solo rojo · ✅ Brujo |
| **Noche** | Noche, Neón (azules y violetas), frío | ✅ Noche · ✅ Noche azul · ✅ Neón · ✅ Frío · ✅ Hielo · ✅ Nórdico · ✅ Sci-fi |
| **Intensos** | Vívido, HDR (detalle en luces y sombras) | ✅ Vivo · ✅ HDR (con Claridad) |

Conclusión: **ya cubrimos casi todo lo popular.** Faltan 4 looks de color puro (**Fresco, Limpio, Moody, Crema**), que
salen gratis en el LUT, y 2 que dependen de filtros espaciales (**Soñado** = glow, **HDR** = Claridad). Las guías
recomiendan usar los filtros al 40–70 %: conviene que la Intensidad arranque más baja en los looks fuertes.

### Efectos

Los que más se repiten, ordenados por cuánto sirven para un video de guitarra:

| Efecto | Qué hace | Para guitarra |
|---|---|---|
| **Sacudida** (shake) | Tiembla el cuadro | ⭐ Al pulso o en los golpes: nadie más lo sincroniza solo con el click |
| **Zoom al pulso** | Acerca y vuelve en cada tiempo | ⭐ Idem; es el "beat sync" que la gente arma a mano con keyframes |
| **Destello** (flash) | Fogonazo blanco o de color | ⭐ En cambios de parte o en el primer golpe; "Flash Velocity" es de lo más usado |
| **Light leak** | Manchas de luz cálida que pasan | ⭐ Look de película, entre tomas |
| **Bokeh / partículas de luz** | Luces desenfocadas que flotan | Ambiente (lo que ya usás: bokeh, luciérnagas, chispas) |
| **VHS / retro** | Líneas, color corrido, grano, temblor de cinta | Covers y estética retro |
| **Glitch / RGB split** | Canales corridos, cortes digitales | Rock / electrónica, transiciones |
| **Desenfoque** (blur) | Entrar o salir desenfocado | Inicios y finales |
| **Película vieja** | Polvo, rayas, parpadeo | Combina con Sepia / Vintage |

Lo que **no** entra: efectos de IA (caricaturas, cambiar la cara), efectos de cuerpo y plantillas de "trends", que
cambian cada semana.

### Orden propuesto

1. Looks **Fresco, Limpio, Moody y Crema** (rápido, al LUT).
2. **Efectos al pulso**: sacudida, zoom y destello sincronizados con el click (lo que nos diferencia).
3. **Light leak** y **partículas de luz** (bokeh, luciérnagas, chispas).
4. **Nitidez / Claridad** → habilita **HDR**; **glow** → habilita **Soñado**.
5. **VHS**, **glitch**, **película vieja**, **desenfoque**.

## 7. Qué no conviene copiar

- Corrección automática, igualar color e IA de CapCut: dependen de modelos propios; mucho trabajo para poca ganancia.
- Efectos de cuerpo y de cara (retoque, piel de vidrio): no sirven para videos de guitarra.
- La biblioteca infinita de efectos: mejor 6–8 efectos buenos con 2–3 controles que cientos.

## Fuentes

- Archivos locales de CapCut 9.2.0 (preset de ajuste y borradores de proyecto del usuario).
- [CapCut — Color grading](https://www.capcut.com/resource/capcut-color-grading)
- [CapCut — Filtros](https://capcut.com/resource/capcut-filter) y [Best video filter app](https://www.capcut.com/resource/best-video-filter-app)
- [CapCut — Efectos alternativos recomendados](https://www.capcut.com/help/recommended-alternative-effects)
- [CapCut — Effects download](https://www.capcut.com/resource/capcut-effects-download)
- [MiniTool — Brighten video in CapCut](https://moviemaker.minitool.com/news/how-to-brighten-video-in-capcut.html)
- [Tuts+ — Body effects in CapCut](https://photography.tutsplus.com/tutorials/a-complete-guide-to-body-effects-in-capcut--cms-108858)
- [FacelessAsset — 25 best CapCut filters 2026](https://facelessasset.com/best-capcut-filters-2026-25-viral-filters-to-make-every-video-look-professional/)
- [CapCut — Photo filters (Teal & Orange, Glow, B/N alto contraste)](https://www.capcut.com/resource/photo-filters)
- [CapCut — Flash / light leak (usos por plantilla)](https://www.capcut.com/explore/flash-transition-light-leak-effect)
- [CapCut — Trending effects](https://www.capcut.com/explore/trending-effect)
- [Tesseract — Best effects in CapCut PC](https://tesseract.academy/best-effects-use-in-capcut-pc-to-level-up-your-tiktok-videos/)
- [Crazy Laura — CapCut effects every YouTuber should use](https://www.crazylaura.com/capcut-effects-every-youtuber-should-use/)
- [FlexClip — Shake effect en CapCut](https://www.flexclip.com/learn/how-to-do-the-shake-effect-on-capcut.html)
