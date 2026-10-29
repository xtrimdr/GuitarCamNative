using WildTrack.Audio;

namespace WildTrack.Lab;

/// <summary>
/// wtlab tempo (the tempo of a file, like "Detectar tempo" on a clip) and the rows of comprobar-dsp for
/// <see cref="TempoDetector"/>: click tracks and a strummed pattern at known tempos, a 44.1 kHz file, noise (no pulse),
/// too short and silence.
/// </summary>
static class TempoChecks
{
    const int Sr = 48000;

    /// <summary>wtlab tempo archivo [--desde s] [--hasta s].</summary>
    public static int Run(Args a)
    {
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("tempo: falta el archivo.");
        var audio = AudioLoader.Load(file, Sr);
        int from = (int)(a.Double("desde", 0) * audio.SampleRate);
        int to = a.Get("hasta") != null ? (int)(a.Double("hasta", 0) * audio.SampleRate) : audio.Left.Length;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = TempoDetector.Detect(audio.Left, audio.Right, audio.SampleRate, from, to - from);
        sw.Stop();
        if (r == null)
            Console.WriteLine($"No hay tempo: dura menos de {TempoDetector.MinSec:0} s o es silencio.");
        else if (r.Confidence < TempoDetector.MinConfidence)
            Console.WriteLine($"No encontré un pulso claro (seguridad {r.Confidence:0.00}; lo más parecido: {r.Bpm:0.0} BPM).");
        else
            Console.WriteLine($"{r.Bpm:0.0} BPM (seguridad {r.Confidence:0.00}), primer golpe en {r.FirstBeatSec + from / (double)audio.SampleRate:0.000} s; " +
                $"también podría ser {r.Bpm / 2:0.0} o {r.Bpm * 2:0.0}. ({sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }
        static string Got(TempoDetector.Result? r) => r == null ? "nada" : $"{r.Bpm:0.0}";

        foreach (double bpm in new[] { 72, 97.5, 128, 150 })
        {
            var (l, r) = Clicks(bpm, 20, Sr, 0.3, accent: false);
            var t = TempoDetector.Detect(l, r, Sr);
            Row($"Tempo: click a {bpm:0.#} BPM", Got(t), $"{bpm:0.0}", t != null && Math.Abs(t.Bpm - bpm) <= 0.2 && t.Confidence > 0.5);
            if (bpm == 128 && t != null)
                Row("Tempo: primer golpe del click", $"{t.FirstBeatSec:0.000} s", "0.300 s", Math.Abs(t.FirstBeatSec - 0.3) < 0.025);
        }

        // 174 is the same pulse as 87: either one is right (the window offers the double and the half)
        var (fl, fr) = Clicks(174, 20, Sr, 0, accent: false);
        var fast = TempoDetector.Detect(fl, fr, Sr);
        Row("Tempo: click a 174 (o 87)", Got(fast), "174/87", fast != null && (Math.Abs(fast.Bpm - 174) <= 0.3 || Math.Abs(fast.Bpm - 87) <= 0.2));

        var (sl, sr) = Strum(100, 24, Sr);
        var strum = TempoDetector.Detect(sl, sr, Sr);
        Row("Tempo: rasgueo a 100 con corcheas", Got(strum), "100.0", strum != null && Math.Abs(strum.Bpm - 100) <= 0.3);

        const int Sr44 = 44100;
        var (cl, cr) = Clicks(110, 15, Sr44, 0.1, accent: true);
        var at44 = TempoDetector.Detect(cl, cr, Sr44);
        Row("Tempo: click a 110 en 44.1 kHz", Got(at44), "110.0", at44 != null && Math.Abs(at44.Bpm - 110) <= 0.2);

        var rng = new Random(7);
        var noise = new float[Sr * 15];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(0.2 * (rng.NextDouble() * 2 - 1));
        var hiss = TempoDetector.Detect(noise, noise, Sr);
        Row("Tempo: ruido sin pulso", hiss == null ? "nada" : $"{hiss.Confidence:0.00}", $"< {TempoDetector.MinConfidence:0.00}",
            hiss == null || hiss.Confidence < TempoDetector.MinConfidence);

        var (shortL, shortR) = Clicks(120, 2, Sr, 0, accent: false);
        Row("Tempo: 2 s es muy poco", Got(TempoDetector.Detect(shortL, shortR, Sr)), "nada", TempoDetector.Detect(shortL, shortR, Sr) == null);
        var quiet = new float[Sr * 10];
        Row("Tempo: silencio", Got(TempoDetector.Detect(quiet, quiet, Sr)), "nada", TempoDetector.Detect(quiet, quiet, Sr) == null);

        // only the stretch asked for: 90 BPM for 10 s, then 140
        var (al, ar) = Clicks(90, 10, Sr, 0, accent: false);
        var (bl, br) = Clicks(140, 10, Sr, 0, accent: false);
        float[] jl = [.. al, .. bl], jr = [.. ar, .. br];
        var second = TempoDetector.Detect(jl, jr, Sr, al.Length, bl.Length);
        Row("Tempo: solo el tramo pedido", Got(second), "140.0", second != null && Math.Abs(second.Bpm - 140) <= 0.3);
        return allOk;
    }

    /// <summary>A woodblock-like click (a decaying 1.5 kHz burst) on every beat from <paramref name="firstSec"/>; with
    /// <paramref name="accent"/> the first of every four is louder.</summary>
    static (float[] L, float[] R) Clicks(double bpm, double seconds, int sr, double firstSec, bool accent)
    {
        var x = new float[(int)(seconds * sr)];
        double period = 60 / bpm;
        int k = 0;
        for (double t = firstSec; t < seconds; t += period, k++)
        {
            int at = (int)Math.Round(t * sr);
            double amp = accent && k % 4 == 0 ? 0.6 : 0.35;
            for (int i = 0; i < sr / 20 && at + i < x.Length; i++)
                x[at + i] += (float)(amp * Math.Exp(-i / (0.006 * sr)) * Math.Sin(2 * Math.PI * 1500 * i / sr));
        }
        return (x, (float[])x.Clone());
    }

    /// <summary>A strummed pattern: a chord-like noise burst on every beat and a softer one on the off-beats, over a
    /// held drone, with a little timing wobble, like a strummed guitar at <paramref name="bpm"/>.</summary>
    static (float[] L, float[] R) Strum(double bpm, double seconds, int sr)
    {
        var rng = new Random(3);
        var x = new float[(int)(seconds * sr)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 110 * i / sr));
        double half = 30 / bpm;
        int k = 0;
        for (double t = 0; t < seconds; t += half, k++)
        {
            int at = (int)Math.Round((t + (rng.NextDouble() - 0.5) * 0.012) * sr);
            double amp = k % 2 == 0 ? 0.4 : 0.18;
            for (int i = 0; i < sr / 4 && at + i < x.Length; i++)
            {
                if (at + i < 0) continue;
                double tone = Math.Sin(2 * Math.PI * 196 * i / sr) + Math.Sin(2 * Math.PI * 247 * i / sr) + Math.Sin(2 * Math.PI * 294 * i / sr);
                x[at + i] += (float)(amp * Math.Exp(-i / (0.08 * sr)) * (0.25 * tone + 0.4 * (rng.NextDouble() * 2 - 1) * Math.Exp(-i / (0.01 * sr))));
            }
        }
        return (x, (float[])x.Clone());
    }
}
