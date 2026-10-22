# GuitarCam Native (pruebas)

Laboratorio para ver si GuitarCam Studio se puede pasar a .NET nativo. Corre al lado de la app web
(`..\GuitarCam`), no la reemplaza.

- `src/GuitarCam.Audio`: motor de audio portado de la app web
  - `Dsp/Saturator.cs`, `Dsp/Compressor.cs`, `Dsp/MasterLimiter.cs`: ports de los worklets de `fxWorklet.ts`
  - `Dsp/LevelAnalysis.cs`: `analyze()` de `compPresets.ts` (nivel típico de la toma, para la "Vida")
  - `ClickSynth.cs`: los 6 sonidos de click de `clickSounds.ts`
  - `AsioDuplex.cs`: entrada y salida ASIO en un mismo callback (NAudio)
- `src/GuitarCam.Lab`: `gclab.exe`, la consola de pruebas
- `tools/worklet-ref.mjs`: corre los worklets originales en Node para comparar muestra a muestra

## Uso

```
dotnet build
src\GuitarCam.Lab\bin\Debug\net10.0-windows\gclab.exe
```

| Comando | Qué hace |
|---|---|
| `dispositivos` | drivers ASIO y dispositivos WASAPI |
| `panel` | panel del driver ASIO (tamaño de buffer) |
| `latencia` | latencia real ida y vuelta, con un cable de salida a entrada |
| `grabar --monitor --sat valvula` | graba sobre el click; con `--monitor` te escuchás con el efecto, vía ASIO |
| `procesar toma.wav --sat cinta --comp` | aplica efectos a un WAV |
| `verificar [toma.wav]` | compara el DSP portado con el de la app web |
| `click` | escribe todos los clicks a un WAV |
