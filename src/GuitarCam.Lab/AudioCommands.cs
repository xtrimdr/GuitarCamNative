using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;
using NAudio.CoreAudioApi;

namespace GuitarCam.Lab;

static class AudioCommands
{
    public static int Devices()
    {
        Console.WriteLine("Drivers ASIO:");
        foreach (var n in AsioDuplex.DriverNames()) Console.WriteLine($"  {n}");
        Console.WriteLine($"  (por defecto: {AsioDuplex.PickDriver(null)})");
        using var en = new MMDeviceEnumerator();
        foreach (var (flow, title) in new[] { (DataFlow.Capture, "Entradas WASAPI:"), (DataFlow.Render, "Salidas WASAPI:") })
        {
            Console.WriteLine(title);
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                var f = d.AudioClient.MixFormat;
                Console.WriteLine($"  {d.FriendlyName}  ({f.SampleRate} Hz, {f.Channels} canales)");
            }
        }
        return 0;
    }

    public static int Panel(Args a)
    {
        var driver = AsioDuplex.PickDriver(a.Get("driver"));
        Console.WriteLine($"Abriendo el panel de {driver}…");
        AsioDuplex.ShowControlPanel(driver);
        Console.WriteLine("Cerralo cuando termines y apretá Enter.");
        Console.ReadLine();
        return 0;
    }

    static void PrintStream(AsioDuplex io)
    {
        double ms = 1000.0 * io.FramesPerBuffer / io.SampleRate;
        Console.WriteLine($"Driver: {io.Driver} · {io.SampleRate} Hz · buffer {io.FramesPerBuffer} muestras ({ms:0.0} ms) · formato {io.SampleType}");
        Console.WriteLine($"Latencia de salida que informa el driver: {io.ReportedOutputLatency} muestras ({1000.0 * io.ReportedOutputLatency / io.SampleRate:0.0} ms)");
    }

    static void WaitStarted(AsioDuplex io)
    {
        for (int i = 0; i < 200 && io.FramesPerBuffer == 0; i++) Thread.Sleep(10);
        if (io.FramesPerBuffer == 0) throw new InvalidOperationException("El driver no arrancó (¿lo está usando otro programa?).");
    }

    /// <summary>
    /// Exercises <see cref="LatencyCalibration"/> and <see cref="CalibrationSession"/> (<c>loopback</c> or <c>tap</c>).
    /// Supports <c>--modo cable|tocando</c>, <c>--ver</c>, <c>--borrar</c>, <c>--datos</c>, and <c>--simular &lt;ms&gt;</c>.
    /// </summary>
    public static int Latency(Args a)
    {
        int sr = a.Int("sr", 48000);
        int inCh = a.Int("entrada", 1) - 1;
        int outCh = a.Int("salida", 1) - 1;
        string? dataDir = a.Get("datos");
        var method = LatencyCalibration.ParseMethod(a.Get("modo", "cable"));
        string outKey = $"{inCh}:{outCh}";

        if (a.Flag("borrar"))
        {
            string dev = a.Get("driver") ?? TryPickDriver() ?? "ASIO";
            LatencyCalibration.WriteCalibration(dev, outKey, null, dataDir);
            Console.WriteLine($"Medición descartada para {dev} ({outKey}). Se vuelve a usar la latencia informada por el driver.");
            return 0;
        }

        if (a.Flag("ver"))
        {
            string dev = a.Get("driver") ?? TryPickDriver() ?? "ASIO";
            var stored = LatencyCalibration.ReadCalibration(dev, outKey, dataDir);
            if (stored == null)
            {
                Console.WriteLine($"Sin calibración guardada para {dev} ({outKey}).");
                return 0;
            }
            double est = a.Double("estimada", stored.EstimateMs);
            bool stale = LatencyCalibration.IsCalibrationStale(stored, est, sr);
            Console.WriteLine($"Calibración guardada ({dev} · {outKey}): {stored.LatencyMs:0.0} ms ± {stored.SpreadMs:0.0} ms · modo {stored.Method} · {stored.SampleRate} Hz{(stale ? " · ¡DESACTUALIZADA!" : "")}");
            return 0;
        }

        int lookahead = (int)Math.Round(MasterLimiter.LookaheadSec * sr);

        if (a.Get("simular") != null)
        {
            double simMs = a.Double("simular", 12.5);
            string simDev = a.Get("driver", "Simulado");
            var simSession = new CalibrationSession(method, sr, lookahead);
            SimulateCalibration(simSession, method, simMs, sr, lookahead);
            var simRes = simSession.Analyze();
            if (simRes == null)
            {
                Console.WriteLine("Falló la calibración simulada.");
                return 2;
            }
            SaveAndPrintResult(simRes.Value, simDev, outKey, 15.0, sr, dataDir, !a.Flag("no-guardar"));
            return 0;
        }

        var driver = AsioDuplex.PickDriver(a.Get("driver"));
        var session = new CalibrationSession(method, sr, lookahead);
        var inProc = new InputProcessor { InputGainDb = a.Double("ganancia", InputProcessor.DefaultInputGainDb) };
        var procL = new float[8192];
        var procR = new float[8192];
        using var done = new ManualResetEventSlim();

        Console.WriteLine(method == CalibrationMethod.Loopback
            ? "Conectá un cable de la salida a la entrada (o poné un micrófono frente al parlante) y bajá el monitoreo directo."
            : $"Tocá una nota corta y apagada en cada click después de los {LatencyCalibration.TapCountIn} tiempos de cuenta ({LatencyCalibration.TapBpm:0} BPM).");

        using var io = new AsioDuplex(driver, sr, inCh, 1, outCh, (input, output, n) =>
        {
            if (n <= procL.Length)
            {
                inProc.Process(input[0].AsSpan(0, n), input[0].AsSpan(0, n), procL.AsSpan(0, n), procR.AsSpan(0, n), out _, out _, out _);
                session.ProcessBlock(procL.AsSpan(0, n), procR.AsSpan(0, n), output[0].AsSpan(0, n), output[1].AsSpan(0, n), n);
            }
            if (session.IsFinished) done.Set();
        });

        io.Start();
        WaitStarted(io);
        PrintStream(io);

        while (!done.Wait(40))
        {
            if (session.GetProgress() is { } p)
            {
                string label = p.CountIn
                    ? $"Cuenta… {p.Step}"
                    : method == CalibrationMethod.Loopback
                        ? $"Pulso {p.Step}/{p.Total}"
                        : $"Golpe {p.Step}/{p.Total}";
                Console.Write($"\r  {label,-24}");
            }
            if (io.Error != null) break;
        }
        io.Stop();
        Console.WriteLine();
        if (io.Error != null) throw io.Error;

        var res = session.Analyze();
        if (res == null)
        {
            Console.WriteLine(method == CalibrationMethod.Loopback
                ? "No volvió la señal. Revisá que el cable vaya de la salida a una entrada de la interfaz (o el micrófono frente al parlante) y que la ganancia no esté al mínimo."
                : "No detecté suficientes golpes. Tocá una nota corta y apagada en cada click, fuerte y sin dejarla sonar.");
            return 2;
        }

        double estMs = Math.Round(((io.ReportedOutputLatency + io.FramesPerBuffer + lookahead) / (double)sr) * 1000.0, 1);
        SaveAndPrintResult(res.Value, driver, outKey, estMs, sr, dataDir, !a.Flag("no-guardar"));
        return 0;
    }

    static void SaveAndPrintResult(
        CalibrationResult res,
        string driver,
        string outKey,
        double estimateMs,
        int sr,
        string? dataDir,
        bool save)
    {
        double latMs = Math.Round(res.LatencyMs * 10.0) / 10.0;
        double sprMs = Math.Round(res.SpreadMs * 10.0) / 10.0;
        Console.WriteLine($"Latencia ida y vuelta ({LatencyCalibration.MethodToString(res.Method)}): {latMs:0.0} ms ± {sprMs:0.0} ms ({res.Matched}/{res.Total} detectados)");
        if (save)
        {
            var cal = new StoredCalibration
            {
                LatencyMs = latMs,
                SpreadMs = sprMs,
                Method = LatencyCalibration.MethodToString(res.Method),
                EstimateMs = estimateMs,
                SampleRate = sr,
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            LatencyCalibration.WriteCalibration(driver, outKey, cal, dataDir);
        }
    }

    static void SimulateCalibration(CalibrationSession session, CalibrationMethod method, double totalLatencyMs, int sr, int lookahead)
    {
        // Note: session delays output by `lookahead` samples so the measured latency includes `lookahead`.
        // Therefore, a hardware round-trip of (totalLatencyMs - lookaheadMs) yields `totalLatencyMs` in Analyze().
        int totalLagSamples = Math.Max(lookahead, (int)Math.Round(totalLatencyMs * sr / 1000.0));
        int hwLag = totalLagSamples - lookahead;
        int total = session.TotalFrames;
        var outL = new float[total];
        var outR = new float[total];
        var inL = new float[total];
        var inR = new float[total];

        if (method == CalibrationMethod.Loopback)
        {
            // First pass to capture what the session plays, then feed it back shifted by hwLag.
            var probe = new CalibrationSession(method, sr, lookahead);
            probe.ProcessBlock(inL, inR, outL, outR, total);
            for (int i = 0; i + hwLag < total; i++)
                inL[i + hwLag] = inR[i + hwLag] = outL[i] * 0.6f;
        }
        else
        {
            double beatSec = 60.0 / LatencyCalibration.TapBpm;
            for (int b = LatencyCalibration.TapCountIn; b < LatencyCalibration.TapCountIn + LatencyCalibration.TapBeats; b++)
            {
                int clickFrame = (int)Math.Round((CalibrationSession.LeadSec + b * beatSec) * sr);
                int hitFrame = clickFrame + totalLagSamples;
                int hitLen = (int)(0.025 * sr);
                for (int k = 0; k < hitLen && hitFrame + k < total; k++)
                {
                    float env = MathF.Exp(-k / (0.005f * sr));
                    float s = 0.5f * env * MathF.Sin(2f * MathF.PI * 220f * k / sr);
                    inL[hitFrame + k] = inR[hitFrame + k] = s;
                }
            }
        }

        const int block = 192;
        for (int pos = 0; pos < total; pos += block)
        {
            int n = Math.Min(block, total - pos);
            session.ProcessBlock(
                inL.AsSpan(pos, n),
                inR.AsSpan(pos, n),
                outL.AsSpan(pos, n),
                outR.AsSpan(pos, n),
                n);
        }
    }

    static string? TryPickDriver()
    {
        try { return AsioDuplex.PickDriver(null); }
        catch { return null; }
    }

    public static int Record(Args a)
    {
        int sr = a.Int("sr", 48000);
        double seconds = a.Double("segundos", 15);
        double bpm = Math.Clamp(a.Double("bpm", 100), 20, 300);
        int countIn = Math.Max(0, a.Int("cuenta", 4));
        var sound = ParseClick(a.Get("click", "madera"));
        double clickDb = a.Double("volumen-click", 0);
        string channel = a.Get("canal", "1").ToLowerInvariant();
        double gainDb = a.Double("ganancia", InputProcessor.DefaultInputGainDb);
        bool monitor = a.Flag("monitor");
        bool discard = a.Flag("descartar");
        double overdubOffsetMs = a.Double("ajuste-sincro", 0);
        double appliedLatencyMs = a.Double("latencia-ms", 15);
        string? satArg = a.Get("sat");
        string path = Path.GetFullPath(a.Get("salida", $"toma_{DateTime.Now:yyyyMMdd_HHmmss}.wav"));
        if (a.Get("entradas") is { } list) return RecordInputs(a, list, path, sr, seconds, bpm, countIn, sound, clickDb, gainDb, overdubOffsetMs, appliedLatencyMs);

        var inProc = new InputProcessor
        {
            ChannelMode = InputProcessor.ParseMode(channel),
            InputGainDb = gainDb,
        };
        var procL = new float[8192];
        var procR = new float[8192];

        if (a.Flag("simular"))
        {
            using var simRec = new TakeRecorder(path, sr);
            int totalFrames = (int)Math.Round(seconds * sr);
            // --ruido-2: input 2 hisses with peaks around that level and input 1 alternates strums and near-silence every half second
            float hiss = (float)a.Double("ruido-2", 0);
            var rng = new Random(1);
            var raw0 = new float[192];
            var raw1 = new float[192];
            int monoBlocks = 0, blocks = 0, switches = 0;
            bool lastMono = false;
            for (int pos = 0; pos < totalFrames; pos += 192)
            {
                int n = Math.Min(192, totalFrames - pos);
                float amp = hiss > 0 && (pos / (sr / 2)) % 2 == 1 ? 0.0008f : 0.25f;
                for (int i = 0; i < n; i++)
                {
                    raw0[i] = amp * MathF.Sin(2f * MathF.PI * 220f * (pos + i) / sr);
                    raw1[i] = hiss / 3f * MathF.Sqrt(-2f * MathF.Log(1f - rng.NextSingle())) * MathF.Cos(2f * MathF.PI * rng.NextSingle());
                }
                inProc.Process(raw0.AsSpan(0, n), raw1.AsSpan(0, n), procL.AsSpan(0, n), procR.AsSpan(0, n), out _, out _, out _);
                simRec.WriteFromCallback(procL.AsSpan(0, n), procR.AsSpan(0, n));
                bool mono = procL.AsSpan(0, n).SequenceEqual(procR.AsSpan(0, n));
                if (mono) monoBlocks++;
                if (blocks > 0 && mono != lastMono) switches++;
                lastMono = mono;
                blocks++;
            }
            if (hiss > 0)
                Console.WriteLine($"Entrada 1 en los dos lados: {monoBlocks}/{blocks} bloques · cambios mono/estéreo: {switches}");
            var simResult = simRec.Finish(
                discard: discard,
                measuredLatencyMs: appliedLatencyMs,
                defaultOverdubOffsetMs: overdubOffsetMs,
                isFirstTake: a.Flag("primera-toma"),
                usedCountIn: countIn > 0,
                usedClick: !a.Flag("sin-click"));
            if (simResult == null)
            {
                Console.WriteLine("Toma descartada (duración ≤ 0.3 s o descartada explícitamente).");
                return 0;
            }
            Console.WriteLine($"Listo: {simResult.WavPath} · {simResult.DurationSec:0.00} s · syncMs {simResult.SyncMs:0.0} ms");
            return 0;
        }

        var driver = AsioDuplex.PickDriver(a.Get("driver"));
        var clickHi = ClickSynth.Render(sound, true, sr, clickDb);
        var clickLo = ClickSynth.Render(sound, false, sr, clickDb);
        int limLatency = (int)Math.Round(MasterLimiter.LookaheadSec * sr);
        double beatLen = sr * 60 / bpm;
        long recStart = (long)Math.Round(countIn * beatLen);
        int recLen = (int)Math.Round(seconds * sr);

        var sat = new Saturator(sr);
        if (satArg != null)
            sat.Set(SatParams.From(true, satArg.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? SatMode.Tape : SatMode.Tube, a.Double("vida", 0.3)));
        var monL = new float[8192];
        var monR = new float[8192];

        int inputs = channel == "estereo" || channel == "2" || channel == "ch2-mono" ? 2 : 1;
        long frame = 0, nextBeat = 0;
        int beat = 0;
        float[]? click = null;
        int clickPos = 0;
        float peak = 0;
        using var recorder = new TakeRecorder(path, sr);
        using var done = new ManualResetEventSlim();

        using var io = new AsioDuplex(driver, sr, a.Int("entrada", 1) - 1, inputs, a.Int("salida-ch", 1) - 1, (input, output, n) =>
        {
            if (n > procL.Length) return;
            var ch0 = input[0].AsSpan(0, n);
            var ch1 = input.Length > 1 ? input[1].AsSpan(0, n) : ch0;
            var pl = procL.AsSpan(0, n);
            var pr = procR.AsSpan(0, n);
            inProc.Process(ch0, ch1, pl, pr, out float maxL, out float maxR, out _);
            float pk = Math.Max(peak, Math.Max(maxL, maxR));
            peak = pk;

            long f0 = frame;
            for (int i = 0; i < n; i++)
            {
                long f = f0 + i;
                if (f >= nextBeat + limLatency)
                {
                    click = beat % 4 == 0 ? clickHi : clickLo;
                    clickPos = 0;
                    beat++;
                    nextBeat = (long)Math.Round(beat * beatLen);
                }
                if (click != null)
                {
                    output[0][i] += click[clickPos];
                    output[1][i] += click[clickPos];
                    if (++clickPos >= click.Length) click = null;
                }
            }

            // Samples at or after recStart go to the TakeRecorder on the exact same audio frame clock.
            long blockEnd = f0 + n;
            if (blockEnd > recStart && f0 < recStart + recLen)
            {
                int startIdx = (int)Math.Max(0, recStart - f0);
                int endIdx = (int)Math.Min(n, recStart + recLen - f0);
                int count = endIdx - startIdx;
                if (count > 0)
                    recorder.WriteFromCallback(pl.Slice(startIdx, count), pr.Slice(startIdx, count));
            }

            if (monitor && n <= monL.Length)
            {
                var ml = monL.AsSpan(0, n);
                var mr = monR.AsSpan(0, n);
                pl.CopyTo(ml);
                pr.CopyTo(mr);
                sat.Process(ml, mr);
                for (int i = 0; i < n; i++)
                {
                    output[0][i] += ml[i];
                    output[1][i] += mr[i];
                }
            }
            frame = f0 + n;
            if (frame >= recStart + recLen) done.Set();
        });
        io.Start();
        WaitStarted(io);
        PrintStream(io);
        Console.WriteLine($"Click {sound} a {bpm:0.#} BPM, cuenta de {countIn}. Grabando {seconds:0.#} s en {path}");
        if (monitor) Console.WriteLine($"Monitoreo por software{(satArg != null ? $" con saturación {satArg}" : "")}: latencia ≈ ida y vuelta (medila con \"latencia\").");
        while (!done.Wait(100))
        {
            double db = peak > 0 ? 20 * Math.Log10(peak) : -120;
            peak = 0;
            int bars = (int)Math.Clamp((db + 60) / 60 * 30, 0, 30);
            long f = frame;
            string state = f < recStart ? $"cuenta {Math.Min(countIn, (int)(f / beatLen) + 1)}" : $"REC {(f - recStart) / (double)sr,5:0.0} s";
            Console.Write($"\r  {state,-12} [{new string('#', bars),-30}] {db,6:0.0} dB{(db > -0.5 ? " ¡CLIP!" : "      ")}");
            if (io.Error != null) break;
        }
        io.Stop();
        Console.WriteLine();
        if (io.Error != null) throw io.Error;

        double estMs = Math.Round(((io.ReportedOutputLatency + io.FramesPerBuffer + limLatency) / (double)sr) * 1000.0, 1);
        var cal = LatencyCalibration.ReadCalibration(driver, $"{a.Int("entrada", 1) - 1}:{a.Int("salida-ch", 1) - 1}");
        double measuredLatencyMs = cal != null && !LatencyCalibration.IsCalibrationStale(cal, estMs, sr) ? cal.LatencyMs : estMs;
        var result = recorder.Finish(
            discard: discard,
            measuredLatencyMs: measuredLatencyMs,
            defaultOverdubOffsetMs: overdubOffsetMs,
            isFirstTake: false,
            usedCountIn: countIn > 0,
            usedClick: true);

        if (result == null)
        {
            Console.WriteLine("Toma descartada (duración ≤ 0.3 s o descartada explícitamente).");
            return 0;
        }

        Console.WriteLine($"Listo: {result.WavPath} ({result.DurationSec:0.00} s · syncMs {result.SyncMs:0.0} ms)");
        Console.WriteLine("El tiempo 0 del archivo es el primer tiempo después de la cuenta, tal como salió por la salida;");
        Console.WriteLine("lo que tocaste llega tarde por la latencia ida y vuelta, que se compensa con syncMs (como hace la app web).");
        return 0;
    }

    /// <summary>
    /// <c>grabar --entradas 1,2,3-4</c>: one take per input at once, like the window with several armed tracks
    /// (an <see cref="InputTap"/> and a WAV each: toma_e1.wav, toma_e3-4.wav…). With <c>--simular</c>, input N
    /// carries a sine of 110·N Hz and each WAV is checked against the inputs it should have.
    /// </summary>
    static int RecordInputs(Args a, string list, string path, int sr, double seconds, double bpm, int countIn,
        ClickSound sound, double clickDb, double gainDb, double overdubOffsetMs, double appliedLatencyMs)
    {
        var routes = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => InputRoute.Parse(s) ?? throw new ArgumentException($"Entrada inválida: {s} (usá 1, 2, 3-4…)")).ToList();
        if (routes.Count == 0) throw new ArgumentException("--entradas necesita al menos una entrada (1, 2, 3-4…)");
        string Wav(InputRoute r) => Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}_e{r.Key}.wav");
        var taps = routes.Select(r => new InputTap(r, gainDb) { Recorder = new TakeRecorder(Wav(r), sr) }).ToList();
        var mainL = new float[InputTap.MaxBlock];
        var mainR = new float[InputTap.MaxBlock];

        if (a.Flag("simular"))
        {
            int inputs = routes.Max(r => r.Last) + 1;
            var raw = new float[inputs][];
            for (int c = 0; c < inputs; c++) raw[c] = new float[192];
            int total = (int)Math.Round(seconds * sr);
            for (int pos = 0; pos < total; pos += 192)
            {
                int n = Math.Min(192, total - pos);
                for (int c = 0; c < inputs; c++)
                    for (int i = 0; i < n; i++) raw[c][i] = 0.1f * MathF.Sin(2f * MathF.PI * 110f * (c + 1) * (pos + i) / sr);
                foreach (var t in taps)
                {
                    t.Process(raw, n, mainL, mainR);
                    t.Recorder!.WriteFromCallback(t.BlockL, t.BlockR);
                }
            }
            bool ok = true;
            float g = (float)Math.Pow(10, gainDb / 20);
            foreach (var t in taps)
            {
                var res = t.Recorder!.Finish(measuredLatencyMs: appliedLatencyMs, defaultOverdubOffsetMs: overdubOffsetMs, usedCountIn: countIn > 0, usedClick: true);
                var r = t.Route!.Value;
                var (l, rr) = ReadStereo(res!.WavPath, 2000);
                // what each side should carry: the input (or the pair's second one), with the pre-amp gain and the soft limit
                float Want(int ch, int i) => InputProcessor.SoftLimit(g * 0.1f * MathF.Sin(2f * MathF.PI * 110f * (ch + 1) * i / sr));
                int right = r.Stereo ? r.First + 1 : r.First;
                double err = 0;
                for (int i = 0; i < l.Length; i++) err = Math.Max(err, Math.Max(Math.Abs(l[i] - Want(r.First, i)), Math.Abs(rr[i] - Want(right, i))));
                bool good = err < 1e-5 && Math.Abs(res.DurationSec - seconds) < 0.01;
                ok &= good;
                Console.WriteLine($"{(good ? "OK " : "MAL")} {r.Label,-14} → {Path.GetFileName(res.WavPath)} · {res.DurationSec:0.00} s · error máx {err:0.0e0}");
            }
            return ok ? 0 : 1;
        }

        var driver = AsioDuplex.PickDriver(a.Get("driver"));
        var clickHi = ClickSynth.Render(sound, true, sr, clickDb);
        var clickLo = ClickSynth.Render(sound, false, sr, clickDb);
        int limLatency = (int)Math.Round(MasterLimiter.LookaheadSec * sr);
        double beatLen = sr * 60 / bpm;
        long recStart = (long)Math.Round(countIn * beatLen);
        int recLen = (int)Math.Round(seconds * sr);
        long frame = 0, nextBeat = 0;
        int beat = 0;
        float[]? click = null;
        int clickPos = 0;
        using var done = new ManualResetEventSlim();
        using var io = new AsioDuplex(driver, sr, 0, 0, a.Int("salida-ch", 1) - 1, (input, output, n) =>
        {
            if (n > mainL.Length) return;
            long f0 = frame;
            for (int i = 0; i < n; i++)
            {
                if (f0 + i >= nextBeat + limLatency)
                {
                    click = beat % 4 == 0 ? clickHi : clickLo;
                    clickPos = 0;
                    beat++;
                    nextBeat = (long)Math.Round(beat * beatLen);
                }
                if (click != null)
                {
                    output[0][i] += click[clickPos];
                    output[1][i] += click[clickPos];
                    if (++clickPos >= click.Length) click = null;
                }
            }
            int start = (int)Math.Clamp(recStart - f0, 0, n), end = (int)Math.Clamp(recStart + recLen - f0, 0, n);
            foreach (var t in taps)
            {
                t.Process(input, n, mainL, mainR);
                if (end > start) t.Recorder!.WriteFromCallback(t.BlockL[start..end], t.BlockR[start..end]);
            }
            frame = f0 + n;
            if (frame >= recStart + recLen) done.Set();
        });
        foreach (var r in routes.Where(r => r.Last >= io.InputChannels))
            Console.WriteLine($"Ojo: el driver tiene {io.InputChannels} entradas; {r.Label} va a grabar silencio.");
        io.Start();
        WaitStarted(io);
        PrintStream(io);
        Console.WriteLine($"Click {sound} a {bpm:0.#} BPM, cuenta de {countIn}. Grabando {seconds:0.#} s de {string.Join(", ", routes.Select(r => r.Label))}");
        while (!done.Wait(100))
        {
            long f = frame;
            string state = f < recStart ? $"cuenta {Math.Min(countIn, (int)(f / beatLen) + 1)}" : $"REC {(f - recStart) / (double)sr,5:0.0} s";
            var levels = taps.Select(t => { var (l, r) = t.TakePeaks(); float pk = Math.Max(l, r); return $"{t.Route!.Value.Key}:{(pk > 0 ? 20 * Math.Log10(pk) : -120),6:0.0}"; });
            Console.Write($"\r  {state,-12} {string.Join("  ", levels)} dB   ");
            if (io.Error != null) break;
        }
        io.Stop();
        Console.WriteLine();
        if (io.Error != null) throw io.Error;
        foreach (var t in taps)
        {
            var res = t.Recorder!.Finish(measuredLatencyMs: appliedLatencyMs, defaultOverdubOffsetMs: overdubOffsetMs, usedCountIn: countIn > 0, usedClick: true);
            Console.WriteLine(res == null ? $"{t.Route!.Value.Label}: descartada" : $"Listo: {res.WavPath} ({res.DurationSec:0.00} s)");
        }
        return 0;
    }

    /// <summary>The first <paramref name="frames"/> frames of a 32-bit float stereo WAV written by <see cref="RecordingWriter"/>.</summary>
    static (float[] L, float[] R) ReadStereo(string path, int frames)
    {
        var bytes = File.ReadAllBytes(path);
        int n = Math.Min(frames, (bytes.Length - 44) / 8);
        var l = new float[n];
        var r = new float[n];
        for (int i = 0; i < n; i++)
        {
            l[i] = BitConverter.ToSingle(bytes, 44 + i * 8);
            r[i] = BitConverter.ToSingle(bytes, 48 + i * 8);
        }
        return (l, r);
    }

    public static ClickSound ParseClick(string s) => s.ToLowerInvariant() switch
    {
        "bip" => ClickSound.Bip,
        "madera" => ClickSound.Madera,
        "clave" => ClickSound.Clave,
        "aguja" => ClickSound.Aguja,
        "cencerro" => ClickSound.Cencerro,
        "bateria" or "batería" => ClickSound.Bateria,
        _ => throw new ArgumentException($"Click desconocido: {s} (bip, madera, clave, aguja, cencerro, bateria)"),
    };
}
