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
    /// Loopback: plays seeded noise bursts and finds them in the input by cross-correlation (like the web
    /// app's loopback calibration). In and out run on one callback, so the result is the true round trip.
    /// </summary>
    public static int Latency(Args a)
    {
        var driver = AsioDuplex.PickDriver(a.Get("driver"));
        int sr = a.Int("sr", 48000);
        int inCh = a.Int("entrada", 1) - 1;
        int outCh = a.Int("salida", 1) - 1;
        const int pulses = 10;
        const int burstLen = 2048;
        int period = sr / 2;
        int start = sr * 3 / 10;
        int total = start + pulses * period + sr / 2;

        var burst = new float[burstLen];
        uint s = 0x1234567;
        for (int i = 0; i < burstLen; i++)
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (burstLen - 1));
            burst[i] = (float)((s / (double)uint.MaxValue * 2 - 1) * 0.5 * hann);
        }

        var rec = new float[total];
        long frame = 0;
        using var done = new ManualResetEventSlim();
        Console.WriteLine("Conectá un cable de la salida a la entrada (o poné un micrófono frente al parlante) y bajá el monitoreo directo.");
        using var io = new AsioDuplex(driver, sr, inCh, 1, outCh, (input, output, n) =>
        {
            long f0 = frame;
            for (int i = 0; i < n; i++)
            {
                long f = f0 + i;
                if (f < total) rec[f] = input[0][i];
                long k = f - start;
                if (k >= 0 && k / period < pulses && k % period < burstLen)
                    output[0][i] = output[1][i] = burst[k % period];
            }
            frame = f0 + n;
            if (frame >= total) done.Set();
        });
        io.Start();
        WaitStarted(io);
        done.Wait(TimeSpan.FromSeconds(total / (double)sr + 5));
        io.Stop();
        if (io.Error != null) throw io.Error;
        PrintStream(io);

        int maxLag = sr / 4;
        double eb = burst.Sum(v => (double)v * v);
        var lags = new List<int>();
        var scores = new List<double>();
        for (int p = 0; p < pulses; p++)
        {
            int e = start + p * period;
            double best = 0;
            int bestLag = 0;
            for (int lag = 0; lag < maxLag && e + lag + burstLen <= total; lag++)
            {
                double c = 0, er = 0;
                for (int j = 0; j < burstLen; j++)
                {
                    double x = rec[e + lag + j];
                    c += burst[j] * x;
                    er += x * x;
                }
                double score = er > 0 ? Math.Abs(c) / Math.Sqrt(eb * er) : 0;
                if (score > best)
                {
                    best = score;
                    bestLag = lag;
                }
            }
            lags.Add(bestLag);
            scores.Add(best);
        }
        lags.Sort();
        double median = lags[lags.Count / 2];
        double quality = scores.Average();
        Console.WriteLine();
        if (quality < 0.3)
        {
            Console.WriteLine($"No encontré los pulsos en la entrada {inCh + 1} (correlación {quality:0.00}). ¿Está conectado el cable y subida la ganancia?");
            return 2;
        }
        Console.WriteLine($"Latencia ida y vuelta: {median} muestras = {1000 * median / sr:0.00} ms");
        Console.WriteLine($"Dispersión entre pulsos: {lags[^1] - lags[0]} muestras · correlación {quality:0.00}");
        return 0;
    }

    public static int Record(Args a)
    {
        var driver = AsioDuplex.PickDriver(a.Get("driver"));
        int sr = a.Int("sr", 48000);
        double seconds = a.Double("segundos", 15);
        double bpm = Math.Clamp(a.Double("bpm", 100), 20, 300);
        int countIn = Math.Max(0, a.Int("cuenta", 4));
        var sound = ParseClick(a.Get("click", "madera"));
        double clickDb = a.Double("volumen-click", 0);
        string channel = a.Get("canal", "1").ToLowerInvariant();
        bool monitor = a.Flag("monitor");
        string? satArg = a.Get("sat");
        string path = Path.GetFullPath(a.Get("salida", $"toma_{DateTime.Now:yyyyMMdd_HHmmss}.wav"));

        var clickHi = ClickSynth.Render(sound, true, sr, clickDb);
        var clickLo = ClickSynth.Render(sound, false, sr, clickDb);
        double beatLen = sr * 60 / bpm;
        long recStart = (long)Math.Round(countIn * beatLen);
        int recLen = (int)(seconds * sr);
        var recL = new float[recLen];
        var recR = new float[recLen];

        var sat = new Saturator(sr);
        if (satArg != null)
            sat.Set(SatParams.From(true, satArg.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? SatMode.Tape : SatMode.Tube, a.Double("vida", 0.3)));
        var monL = new float[8192];
        var monR = new float[8192];

        int inputs = channel == "estereo" || channel == "2" ? 2 : 1;
        long frame = 0, nextBeat = 0;
        int beat = 0;
        float[]? click = null;
        int clickPos = 0;
        float peak = 0;
        using var done = new ManualResetEventSlim();

        using var io = new AsioDuplex(driver, sr, a.Int("entrada", 1) - 1, inputs, a.Int("salida-ch", 1) - 1, (input, output, n) =>
        {
            float[] inL = channel == "2" ? input[1] : input[0];
            float[] inR = channel == "estereo" ? input[1] : inL;
            long f0 = frame;
            float pk = peak;
            for (int i = 0; i < n; i++)
            {
                long f = f0 + i;
                if (f == nextBeat)
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
                long r = f - recStart;
                if (r >= 0 && r < recLen)
                {
                    recL[r] = inL[i];
                    recR[r] = inR[i];
                }
                float x = Math.Max(Math.Abs(inL[i]), Math.Abs(inR[i]));
                if (x > pk) pk = x;
            }
            peak = pk;
            if (monitor && n <= monL.Length)
            {
                var ml = monL.AsSpan(0, n);
                var mr = monR.AsSpan(0, n);
                inL.AsSpan(0, n).CopyTo(ml);
                inR.AsSpan(0, n).CopyTo(mr);
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
        Wav.Write(path, recL, recR, sr);
        Console.WriteLine($"Listo: {path}");
        Console.WriteLine("El tiempo 0 del archivo es el primer tiempo después de la cuenta, tal como salió por la salida;");
        Console.WriteLine("lo que tocaste llega tarde por la latencia ida y vuelta, que hay que restar (como hace la app web).");
        return 0;
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
