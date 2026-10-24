# Pruebas a mano — GuitarCam Studio Native

Lo que está programado y compila pero todavía nadie probó en la ventana, porque hace falta el equipo
(interfaz, guitarra, cable, celular) o escuchar. Está agrupado según lo que necesitás tener a mano, así
se puede hacer de una sentada. Al probar algo, tachalo acá (o borralo) y, si falla, anotalo en
[PENDIENTES.md](../PENDIENTES.md).

Antes de empezar: trabajá sobre una **copia** de un proyecto ("Duplicar" en el gestor), no sobre el original.

## Solo con la app (sin audio)

- [ ] **Medidor CPU / GPU / RAM** en la barra de título: con la ventana a 1600 px de ancho o más
  (maximizada en un monitor de 1920) tiene que aparecer y moverse; más angosta, desaparece.
- [ ] **"Color a elección"** en el selector de tema: abre el diálogo de color de Windows, el acento cambia
  en vivo y se recuerda al cerrar y abrir.
- [ ] **"Mostrar en el Explorador"** (versión nueva por el shell), desde el gestor, el Historial y el fin
  de la exportación: el Explorador aparece adelante, con el archivo seleccionado, en menos de medio segundo.
- [ ] **Preview**: arrastrar dentro de un panel (paneo del encuadre) y la rueda (zoom); cada gesto es un
  paso de deshacer.
- [ ] **Comparar lado a lado con la web** (captura de las dos, mismo proyecto): pestaña Color y Mixer.

## Con la interfaz y un proyecto con tomas con sonido

- [ ] **Medidores del mixer**: suben con la toma (verde → amarillo → rojo), el pico queda 1,5 s, la luz
  LIM del master se prende si se empuja el fader.
- [ ] **Presets de reverb y delay** en los retornos del mixer: que cada uno suene distinto y razonable
  (Sala, Placa…; delay con nota y "Libre (ms)", estéreo / ping-pong).
- [ ] **Menú del click**: al elegir cada sonido (Bip, Madera, Clave, Aguja, Cencerro, Batería) se escucha;
  al mover el volumen (−24 a +9 dB) también; se recuerda al reabrir.
- [ ] **Escuchar una toma desde el Historial**: suena por los parlantes de Windows (no por ASIO) y el
  ícono vuelve solo al terminar.
- [ ] **Fades de video** de un clip: se ven mientras reproduce (parado no se ven, como la web).
- [ ] **Saltos para atrás mientras reproduce** (clic en la regla más atrás): el video vuelve sin trabarse
  y queda en sincronía con el audio.
- [ ] **Muchas tomas con video** (6 o más): cuántos cuadros por segundo llegan de verdad en la preview y
  cuánto marca la carga DSP.
- [ ] **Buffer desde "Panel del driver"** (Configuración → Entradas y salidas): cambiar el tamaño en el
  panel de la Focusrite; el audio se reabre solo y "Buffer" muestra el nuevo valor. Probalo también
  mientras graba: tiene que esperar a que termine la toma.

## Con la guitarra

- [ ] **Calibración "Tocando"** (Configuración → Sincronización): 4 de cuenta + 12 clicks; da un valor
  cercano a la estimación, y una toma grabada después queda en sincronía con el click.
- [ ] **Toma con cámara** de punta a punta: grabar, que audio y video queden en sincronía en la preview y
  en el MP4 exportado.

## Con un cable (salida → entrada)

- [ ] **Calibración "Con cable"**: mide, guarda y la dispersión es chica; al cambiar de par de entradas o
  salidas avisa que el equipo cambió.

## Con el celular o otra cámara

- [ ] **DroidCam por IP** (Configuración → Cámara): se ve en "En vivo" y graba la toma con video.
- [ ] **Una cámara que ofrezca 1080p**: elegirla, que la toma salga a 1080p y el monitor no se trabe.
- [ ] **Cámara que se desconecta**: el monitor dice "Sin señal de cámara"; con "Refrescar dispositivos"
  vuelve.

## Sin la ventana

