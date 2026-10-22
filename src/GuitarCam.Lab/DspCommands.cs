using System.Diagnostics;
using System.Text.Json;
using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.Lab;

static class DspCommands
{
    const int Block = 128; // a render quantum, like the worklets

    public static int Process(Args a)
    {
        if (a.Positional.Count == 0) throw new ArgumentException("Falta el WAV de entrada.");
        string inPath = a.Positional[0];
        var (ch, sr) = Wav.Read(inPath);
        var (rms, peak) = LevelAnalysis.Analyze(ch, sr);
        Console.WriteLine($"{Path.GetFileName(inPath)}: {sr} Hz, {ch[0].Length / (double)sr:0.0} s, nivel típico {rms:0.0} dB RMS, picos {peak:0.0} dB");

        var sat = new Saturator(sr);
        string? satArg = a.Get("sat");
        if (satArg != null)
        {
            var mode = satArg.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? SatMode.Tape : SatMode.Tube;
            var p = SatParams.From(true, mode, a.Double("vida", 0.3), rms);
            sat.Set(p);
            Console.WriteLine($"Saturación {mode}: drive {p.Drive:0.###}, bias {p.Bias:0.##}, tono {p.Tone:0} Hz, bump {p.Bump:0.#} dB");
        }
        var comp = new Compressor(sr)
        {
            Bypass = !a.Flag("comp"),
            Threshold = a.Double("umbral", -24),
            Ratio = a.Double("ratio", 4),
            Attack = a.Double("ataque", 10) / 1000,
            Release = a.Double("release", 150) / 1000,
            Knee = a.Double("knee", 6),
            Makeup = a.Double("makeup", 0),
            Limit = a.Flag("limit"),
            Ceiling = -3,
        };

        var sw = Stopwatch.StartNew();
        double minGr = 0;
        int len = ch[0].Length;
        for (int i = 0; i < len; i += Block)
        {
            int n = Math.Min(Block, len - i);
            var l = ch[0].AsSpan(i, n);
            var r = ch[1].AsSpan(i, n);
            sat.Process(l, r);
            comp.Process(l, r);
            minGr = Math.Min(minGr, comp.TakeMeter());
        }
        sw.Stop();
        string outPath = a.Get("salida", Path.ChangeExtension(inPath, null) + "_proc.wav");
        Wav.Write(outPath, ch[0], ch[1], sr);
        double audioSec = len / (double)sr;
        Console.WriteLine($"Procesado en {sw.Elapsed.TotalMilliseconds:0} ms ({audioSec / sw.Elapsed.TotalSeconds:0}× tiempo real){(comp.Bypass ? "" : $", reducción máxima {minGr:0.0} dB")}");
        Console.WriteLine($"Escrito: {Path.GetFullPath(outPath)}");
        return 0;
    }

    public static int Click(Args a)
    {
        int sr = 48000;
        double beatLen = sr * 60 / a.Double("bpm", 100);
        var sounds = Enum.GetValues<ClickSound>();
        int beats = sounds.Length * 4 + 1;
        int len = (int)(beats * beatLen) + sr;
        var L = new float[len];
        for (int s = 0; s < sounds.Length; s++)
        {
            Console.WriteLine($"  compás {s + 1}: {sounds[s]}");
            for (int b = 0; b < 4; b++)
            {
                var c = ClickSynth.Render(sounds[s], b == 0, sr);
                int at = (int)Math.Round((s * 4 + b) * beatLen);
                for (int i = 0; i < c.Length && at + i < len; i++) L[at + i] += c[i];
            }
        }
        string path = a.Get("salida", "clicks.wav");
        Wav.Write(path, L, L, sr);
        Console.WriteLine($"Escrito: {Path.GetFullPath(path)}");
        return 0;
    }

    sealed record Case(string Name, object[] Chain, Action<float[], float[]> Native);

    /// <summary>
    /// Runs the web app's own worklet code (fxWorklet.ts, in Node) and the C# ports on the same audio and
    /// compares them sample by sample.
    /// </summary>
    public static int Verify(Args a)
    {
        string root = FindRoot();
        string worklet = Path.GetFullPath(a.Get("web", Path.Combine(root, "..", "GuitarCam", "src", "audio", "fxWorklet.ts")));
        if (!File.Exists(worklet)) throw new FileNotFoundException($"No encontré {worklet} (usá --web <ruta a fxWorklet.ts>).");
        string script = Path.Combine(root, "tools", "worklet-ref.mjs");

        float[][] ch;
        int sr;
        if (a.Positional.Count > 0)
        {
            (ch, sr) = Wav.Read(a.Positional[0]);
            int max = sr * 20;
            if (ch[0].Length > max) ch = [ch[0][..max], ch[1][..max]];
            Console.WriteLine($"Señal: {Path.GetFileName(a.Positional[0])} ({ch[0].Length / (double)sr:0.0} s)");
        }
        else
        {
            sr = 48000;
            ch = TestSignal(sr, 8);
            Console.WriteLine("Señal: guitarra sintética de prueba (8 s). Pasá un WAV para usar una toma real.");
        }
        var (rms, _) = LevelAnalysis.Analyze(ch, sr);

        // AudioParams are float32 in Web Audio, so the ports get the same rounded values.
        static double F(double v) => (float)v;
        object SatStage(SatParams p) => new
        {
            name = "gc-saturator",
            @params = new { bypass = p.Bypass ? 1 : 0, drive = p.Drive, bias = p.Bias, tone = p.Tone, toneMix = p.ToneMix, bump = p.Bump },
        };
        Action<float[], float[]> SatNative(SatParams p)
        {
            var s = new Saturator(sr);
            s.Set(p with { Drive = F(p.Drive), Bias = F(p.Bias), Tone = F(p.Tone), ToneMix = F(p.ToneMix), Bump = F(p.Bump) });
            return (l, r) => Run(l, r, s.Process);
        }
        object CompStage(double th, double ratio, double makeup, bool limit) => new
        {
            name = "gc-compressor",
            @params = new { bypass = 0, threshold = th, ratio, knee = 6, attack = 0.01, release = 0.15, makeup, limit = limit ? 1 : 0, ceiling = -3 },
        };
        Action<float[], float[]> CompNative(double th, double ratio, double makeup, bool limit)
        {
            var c = new Compressor(sr)
            {
                Bypass = false, Threshold = th, Ratio = ratio, Knee = 6, Attack = F(0.01), Release = F(0.15),
                Makeup = makeup, Limit = limit, Ceiling = -3,
            };
            return (l, r) => Run(l, r, c.Process);
        }
        int look = (int)Math.Round(MasterLimiter.LookaheadSec * sr);

        var tube30 = SatParams.From(true, SatMode.Tube, 0.3, rms);
        var tube100 = SatParams.From(true, SatMode.Tube, 1, rms);
        var tape80 = SatParams.From(true, SatMode.Tape, 0.8, rms);
        var tape50 = SatParams.From(true, SatMode.Tape, 0.5, rms);
        var cases = new List<Case>
        {
            new("Saturación válvula, Vida 30%", [SatStage(tube30)], SatNative(tube30)),
            new("Saturación válvula, Vida 100%", [SatStage(tube100)], SatNative(tube100)),
            new("Saturación cinta, Vida 80%", [SatStage(tape80)], SatNative(tape80)),
            new("Compresor -30 dB 4:1 +8", [CompStage(-30, 4, 8, false)], CompNative(-30, 4, 8, false)),
            new("Compresor + limitador de pista", [CompStage(-30, 4, 14, true)], CompNative(-30, 4, 14, true)),
            new("Cinta 50% → compresor + limitador", [SatStage(tape50), CompStage(-30, 6, 12, true)], (l, r) =>
            {
                SatNative(tape50)(l, r);
                CompNative(-30, 6, 12, true)(l, r);
            }),
            new("Limitador master (-6 dB)",
                [new { name = "gc-limiter", options = new { lookahead = look, ceilingDb = -6.0 } }],
                (l, r) => Run(l, r, new MasterLimiter(sr, look, -6).Process)),
        };

        string tmp = Path.Combine(Path.GetTempPath(), "gclab-verify");
        Directory.CreateDirectory(tmp);
        string inFile = Path.Combine(tmp, "in.f32");
        WritePlanar(inFile, ch[0], ch[1]);
        int len = ch[0].Length;

        Console.WriteLine();
        Console.WriteLine($"{"Prueba",-36} {"dif. máx.",12} {"vs señal",10}  resultado");
        bool allOk = true;
        for (int k = 0; k < cases.Count; k++)
        {
            var c = cases[k];
            string job = Path.Combine(tmp, $"job{k}.json");
            string outFile = Path.Combine(tmp, $"out{k}.f32");
            File.WriteAllText(job, JsonSerializer.Serialize(new { sampleRate = sr, length = len, input = inFile, output = outFile, chain = c.Chain }));
            RunNode(script, worklet, job);
            var (jl, jr) = ReadPlanar(outFile, len);

            var nl = (float[])ch[0].Clone();
            var nr = (float[])ch[1].Clone();
            c.Native(nl, nr);

            double maxDiff = 0, maxSig = 0;
            for (int i = 0; i < len; i++)
            {
                maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(nl[i] - jl[i]), Math.Abs(nr[i] - jr[i])));
                maxSig = Math.Max(maxSig, Math.Max(Math.Abs(jl[i]), Math.Abs(jr[i])));
            }
            double diffDb = maxDiff > 0 ? 20 * Math.Log10(maxDiff) : double.NegativeInfinity;
            double relDb = maxDiff > 0 ? 20 * Math.Log10(maxDiff / maxSig) : double.NegativeInfinity;
            bool ok = relDb < -100;
            allOk &= ok;
            Console.WriteLine($"{c.Name,-36} {Db(diffDb),12} {Db(relDb),10}  {(ok ? "igual" : "DISTINTO")}");
        }
        Console.WriteLine();
        Console.WriteLine(allOk
            ? "Todo coincide con la app web (diferencias por debajo de -100 dB: redondeo de punto flotante)."
            : "Hay diferencias audibles: el port no coincide en algún caso.");
        return allOk ? 0 : 3;
    }

    static string Db(double db) => double.IsNegativeInfinity(db) ? "exacto" : $"{db:0.0} dB";

    static void Run(float[] l, float[] r, SpanAction process)
    {
        for (int i = 0; i < l.Length; i += Block)
        {
            int n = Math.Min(Block, l.Length - i);
            process(l.AsSpan(i, n), r.AsSpan(i, n));
        }
    }

    delegate void SpanAction(Span<float> l, Span<float> r);

    static void RunNode(string script, string worklet, string job)
    {
        var psi = new ProcessStartInfo("node", [script, worklet, job]) { RedirectStandardError = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi) ??throw new InvalidOperationException("No pude ejecutar node.");
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"node falló: {err}");
    }

    static void WritePlanar(string path, float[] l, float[] r)
    {
        using var f = File.Create(path);
        f.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(l.AsSpan()));
        f.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.AsSpan()));
    }

    static (float[], float[]) ReadPlanar(string path, int len)
    {
        var bytes = File.ReadAllBytes(path);
        var all = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
        return (all[..len].ToArray(), all[len..(2 * len)].ToArray());
    }

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GuitarCamNative.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("No encontré la carpeta de la solución.");
    }

    /// <summary>Plucked notes with harmonics and a bit of pick noise, at a typical guitar level.</summary>
    static float[][] TestSignal(int sr, double seconds)
    {
        int len = (int)(sr * seconds);
        var L = new float[len];
        var R = new float[len];
        double[] notes = [82.41, 110, 146.83, 196, 246.94, 329.63, 220, 164.81];
        var rnd = new Random(7);
        double step = 0.5;
        for (int k = 0; k * step * sr < len; k++)
        {
            double f = notes[k % notes.Length];
            double amp = 0.25 + 0.5 * rnd.NextDouble();
            int at = (int)(k * step * sr);
            for (int i = 0; at + i < len && i < sr * 1.5; i++)
            {
                double t = i / (double)sr;
                double env = Math.Exp(-t * 3);
                double v = 0;
                for (int h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * f * h * t) / h;
                if (i < 200) v += (rnd.NextDouble() * 2 - 1) * (1 - i / 200.0);
                v *= amp * env * 0.4;
                L[at + i] += (float)v;
                R[at + i] += (float)(v * 0.9);
            }
        }
        return [L, R];
    }
}
