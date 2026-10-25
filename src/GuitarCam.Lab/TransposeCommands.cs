using System.Diagnostics;
using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.Lab;

/// <summary>
/// gclab transponer and the transpose checks of comprobar-dsp: <see cref="PitchShifter"/> measured on signals whose
/// right answer is known (a sine's exact frequency, a chord's partials, where a pluck starts, a mono take).
/// </summary>
static class TransposeCommands
{
    const int Sr = 48000;

    public static int Run(Args a)
    {
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("transponer: falta el archivo.");
        double semis = a.Double("semitonos", 2);
        if (Math.Abs(semis) > PitchShifter.MaxSemitones) throw new ArgumentException($"--semitonos: de -{PitchShifter.MaxSemitones} a +{PitchShifter.MaxSemitones}.");
        string output = a.Get("salida", Path.ChangeExtension(file, null) + $"_{semis:+0.##;-0.##}st.wav");
        var audio = AudioLoader.Load(file, Sr);
        var sw = Stopwatch.StartNew();
        var (l, r) = PitchShifter.Shift(audio.Left, audio.Right, semis);
        sw.Stop();
        Wav.Write(output, l, r, Sr);
        double sec = l.Length / (double)Sr;
        Console.WriteLine($"{Path.GetFileName(file)}: {sec:0.0} s, {semis:+0.##;-0.##} semitonos en {sw.Elapsed.TotalSeconds:0.0} s ({sec / Math.Max(0.001, sw.Elapsed.TotalSeconds):0}× tiempo real)");
        Console.WriteLine($"Escrito: {Path.GetFullPath(output)}");
        return 0;
    }

    /// <summary>The transpose rows of comprobar-dsp; true when all of them pass.</summary>
    public static bool Check()
    {
        bool ok = true;

        // a sine lands on the exact new frequency, at the same level and length
        foreach (double semis in new[] { 7.0, -5.0, 12.0, -12.0, 0.5 })
        {
            var sine = Tone(Sr * 3, i => 0.5 * Math.Sin(2 * Math.PI * 440 * i / Sr));
            var (l, _) = PitchShifter.Shift(sine, sine, semis);
            double want = 440 * Math.Pow(2, semis / 12);
            double got = Frequency(l, Sr / 2, Sr * 5 / 2);
            double cents = 1200 * Math.Log2(got / want);
            double levelDb = 20 * Math.Log10(Rms(l, Sr / 2, Sr * 5 / 2) / Rms(sine, Sr / 2, Sr * 5 / 2));
            bool pass = l.Length == sine.Length && Math.Abs(cents) < 0.1 && Math.Abs(levelDb) < 0.3;
            ok &= pass;
            Row($"Transponer seno 440 Hz {semis:+0.#;-0.#} st", $"{Signed(cents, 3)}c {Signed(levelDb, 1)}dB", "0c 0dB", pass);
        }

        // a chord with guitar-like harmonics: almost all the energy lands on the moved partials
        double[] notes = [82.41, 123.47, 164.81]; // E2 B2 E3: a power chord, its partials apart or in unison
        var chord = Tone(Sr * 4, i =>
        {
            double t = i / (double)Sr, v = 0;
            foreach (double f in notes)
                for (int h = 1; h <= 8; h++) v += 0.06 / h * Math.Sin(2 * Math.PI * f * h * t + h * 0.7 + f);
            return v;
        });
        foreach (double semis in new[] { 3.0, -4.0, 7.0, 12.0, -12.0, 1e-5 })
        {
            var sw = Stopwatch.StartNew();
            var (l, _) = PitchShifter.Shift(chord, chord, semis);
            sw.Stop();
            double alpha = Math.Pow(2, semis / 12);
            double off = OffPartialsDb(l, notes.SelectMany(f => Enumerable.Range(1, 8).Select(h => f * h * alpha)).ToArray());
            // what is left are the beats of partials closer than the frame can tell apart (a low chord's 5th and
            // 3rd harmonics), which keep their original rate; nearly 1 the vocoder gives back the chord
            bool identity = Math.Abs(semis) < 0.01;
            bool pass = off < (identity ? -80 : -35);
            ok &= pass;
            Row(identity ? "Acorde a 0,00001 st: vuelve igual" : $"Acorde {semis:+0;-0} st: energía fuera de notas", $"{off:0.0} dB", identity ? "< -80 dB" : "< -35 dB", pass);
            if (semis > 0) Console.WriteLine($"{"  (4 s estéreo en",-36} {sw.Elapsed.TotalMilliseconds:0} ms, {4 / sw.Elapsed.TotalSeconds:0}× tiempo real)");
        }

        // a pluck starts where it started (in sync with the video) and does not smear before its attack
        int at = Sr;
        var rnd = new Random(7);
        var pluck = Tone(Sr * 3, i =>
        {
            if (i < at) return 0;
            double t = (i - at) / (double)Sr;
            return Math.Exp(-t * 6) * (0.3 * Math.Sin(2 * Math.PI * 196 * t) + 0.15 * Math.Sin(2 * Math.PI * 392 * t))
                + Math.Exp(-t * 400) * 0.4 * (rnd.NextDouble() * 2 - 1);
        });
        foreach (double semis in new[] { 5.0, -5.0 })
        {
            var (l, _) = PitchShifter.Shift(pluck, pluck, semis);
            int onset = Onset(l);
            double shiftMs = (onset - Onset(pluck)) * 1000.0 / Sr;
            double pre = 10 * Math.Log10(Energy(l, at - Sr * 40 / 1000, at - Sr * 3 / 1000) / Energy(l, at, at + Sr * 40 / 1000) + 1e-30);
            // a frame of 85 ms leaves a few ms of soft pre-echo, under the ear's premasking
            bool pass = Math.Abs(shiftMs) <= 4 && pre < -20;
            ok &= pass;
            Row($"Ataque {semis:+0;-0} st: corrimiento, pre-eco", $"{shiftMs:+0.0;-0.0} ms {pre:0} dB", "±4 ms <-20", pass);
        }

        // a mono take stays mono and 0 semitones is the take itself
        var stereo = Tone(Sr * 2, i => 0.3 * Math.Sin(2 * Math.PI * 330 * i / Sr) + 0.1 * Math.Sin(2 * Math.PI * 1234 * i / Sr));
        var (ml, mr) = PitchShifter.Shift(stereo, stereo, 2);
        double monoDiff = 0;
        for (int i = 0; i < ml.Length; i++) monoDiff = Math.Max(monoDiff, Math.Abs(ml[i] - mr[i]));
        var (zl, _) = PitchShifter.Shift(stereo, stereo, 0);
        bool monoOk = monoDiff < 1e-5 && zl.AsSpan().SequenceEqual(stereo);
        ok &= monoOk;
        Row("Toma mono sigue mono; 0 st = igual", $"{monoDiff:0.0e0}", "< 1e-5", monoOk);
        return ok;
    }

    static string Signed(double v, int decimals)
    {
        v = Math.Round(v, decimals);
        if (v == 0) v = 0; // no "-0"
        return v.ToString((v >= 0 ? "+0." : "0.") + new string('0', decimals));
    }

    static void Row(string what, string got, string want, bool pass) =>
        Console.WriteLine($"{what,-36} {got,12} {want,10}  {(pass ? "igual" : "DISTINTO")}");

    static float[] Tone(int n, Func<int, double> f)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)f(i);
        return x;
    }

    /// <summary>A sine's frequency from its interpolated rising zero crossings.</summary>
    static double Frequency(float[] x, int from, int to)
    {
        double first = -1, last = -1;
        int count = 0;
        for (int i = from; i < to; i++)
        {
            if (x[i - 1] < 0 && x[i] >= 0)
            {
                double t = i - 1 + x[i - 1] / (x[i - 1] - x[i]);
                if (first < 0) first = t;
                else count++;
                last = t;
            }
        }
        return count * Sr / (last - first);
    }

    static double Rms(float[] x, int from, int to) => Math.Sqrt(Energy(x, from, to) / (to - from));

    static double Energy(float[] x, int from, int to)
    {
        double e = 0;
        for (int i = Math.Max(0, from); i < Math.Min(x.Length, to); i++) e += x[i] * (double)x[i];
        return e;
    }

    /// <summary>First sample above a tenth of the peak.</summary>
    static int Onset(float[] x)
    {
        float peak = 0;
        foreach (float v in x) peak = Math.Max(peak, Math.Abs(v));
        for (int i = 0; i < x.Length; i++)
            if (Math.Abs(x[i]) > peak * 0.1f) return i;
        return -1;
    }

    /// <summary>Energy away from the given partials (more than 4 bins from each) relative to the total, on a 64k Blackman-Harris FFT of the middle.</summary>
    static double OffPartialsDb(float[] x, double[] partials)
    {
        const int n = 65536;
        int start = (x.Length - n) / 2;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            double u = 2 * Math.PI * i / n;
            double w = 0.35875 - 0.48829 * Math.Cos(u) + 0.14128 * Math.Cos(2 * u) - 0.01168 * Math.Cos(3 * u);
            re[i] = x[start + i] * w;
        }
        new Fft(n).Transform(re, im, inverse: false);
        double total = 0, off = 0;
        for (int b = 1; b < n / 2; b++)
        {
            double p = re[b] * re[b] + im[b] * im[b];
            total += p;
            double hz = b * (double)Sr / n;
            bool near = partials.Any(f => Math.Abs(hz - f) * n / Sr <= 4);
            if (!near) off += p;
        }
        return 10 * Math.Log10(off / total + 1e-30);
    }
}
