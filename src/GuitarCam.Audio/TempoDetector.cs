using GuitarCam.Audio.Dsp;

namespace GuitarCam.Audio;

/// <summary>
/// Finds the tempo of a piece of audio (a loop, a sample, a take): an onset curve from the spectral flux (how much new
/// energy each 10 ms brings, compressed so a soft pick counts next to a strum), its autocorrelation over the beat
/// lengths of <see cref="MinBpm"/>..<see cref="MaxBpm"/> with a nudge towards 120 BPM (the octave guess: 87 or 174 is
/// the same pulse, so the window offers the double and the half too), and then a comb over the whole curve that
/// tries the tempos around it and every phase, so a long file lands on the tenth and says where its first beat is.
/// Offline only (allocates, runs on a worker thread).
/// </summary>
public static class TempoDetector
{
    /// <summary>Analysis frame and hop: 43 ms and 10.7 ms at 48 kHz.</summary>
    const int FrameSize = 2048, Hop = 512;
    /// <summary>The tempos it picks from; the double or the half of the result can go past them.</summary>
    public const double MinBpm = 55, MaxBpm = 215;
    /// <summary>Where the octave guess leans to, and how wide (in octaves) that lean is.</summary>
    const double PriorBpm = 120, PriorOctaves = 1.0;
    /// <summary>Shorter than this there are too few beats to trust.</summary>
    public const double MinSec = 3;
    /// <summary>Only the first three minutes are read: plenty for a tempo, and a long take stays fast.</summary>
    const double MaxSec = 180;
    /// <summary>Above this the flux is mostly hiss and cymbal wash.</summary>
    const double FluxTopHz = 8000;
    /// <summary>Below this level (≈ −70 dBFS RMS) it is silence.</summary>
    const double SilenceRms = 3e-4;
    /// <summary>The local mean taken off the onset curve, so a crescendo isn't a pulse.</summary>
    const double DetrendSec = 0.5;
    /// <summary>How far around the autocorrelation's tempo the comb looks, and in what steps.</summary>
    const double RefineSpan = 0.03, RefineStepBpm = 0.02;
    /// <summary>Under this <see cref="Result.Confidence"/> there is no clear pulse.</summary>
    public const double MinConfidence = 0.15;

    /// <param name="Bpm">The tempo, in BPM.</param>
    /// <param name="Confidence">0..1: how regular the pulse is (a click is near 1, noise near 0).</param>
    /// <param name="FirstBeatSec">The first beat, in seconds from the start that was analysed.</param>
    public sealed record Result(double Bpm, double Confidence, double FirstBeatSec);

    /// <summary>How much of the work the onset curve is (the comb is the rest; the autocorrelation is a blink).</summary>
    const double OnsetShare = 0.85;
    /// <summary>The progress is told this many times at most.</summary>
    const int ProgressSteps = 100;

    /// <summary>The tempo of <paramref name="count"/> samples from <paramref name="start"/> (all of it by default);
    /// null when it is too short or silent. <paramref name="progress"/> hears 0..1 as it goes, from its thread.</summary>
    public static Result? Detect(float[] left, float[] right, int sampleRate, int start = 0, int count = -1, Action<double>? progress = null)
    {
        start = Math.Clamp(start, 0, left.Length);
        if (count < 0 || count > left.Length - start) count = left.Length - start;
        count = Math.Min(count, (int)(MaxSec * sampleRate));
        if (count < MinSec * sampleRate) return null;

        double sq = 0;
        for (int i = start; i < start + count; i++) { double v = 0.5 * (left[i] + right[i]); sq += v * v; }
        if (Math.Sqrt(sq / count) < SilenceRms) return null;

        double rate = sampleRate / (double)Hop;
        var env = Onsets(left, right, sampleRate, start, count, progress);
        int n = env.Length;
        Detrend(env, (int)Math.Round(DetrendSec * rate));
        // blurred, a beat that falls between two frames (150 BPM is 37.5 of them) still lines up with its lag
        env = Smooth(env);

        int minLag = (int)Math.Floor(60 * rate / MaxBpm), maxLag = (int)Math.Ceiling(60 * rate / MinBpm);
        if (n < 2 * maxLag + 2) return null;
        var ac = Autocorrelation(env, 2 * maxLag + 2);

        // a pulse at L also repeats at 2L: counting it keeps a straight eighth-note pattern from reading as its half
        int best = -1;
        double bestScore = double.NegativeInfinity;
        var score = new double[maxLag + 2];
        for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double bpm = 60 * rate / lag, oct = Math.Log2(bpm / PriorBpm) / PriorOctaves;
            score[lag] = (ac[lag] + 0.5 * ac[2 * lag]) * Math.Exp(-0.5 * oct * oct);
            if (lag >= minLag && lag <= maxLag && score[lag] > bestScore) { bestScore = score[lag]; best = lag; }
        }
        if (best < 0 || bestScore <= 0) return null;
        double lagF = best + Parabola(score[best - 1], score[best], score[best + 1]);
        double confidence = Math.Clamp(Math.Max(ac[best], Math.Max(ac[best - 1], ac[best + 1])), 0, 1);

        var (tempo, phase) = Refine(env, rate, 60 * rate / lagF, progress);
        progress?.Invoke(1);
        double firstBeat = (phase * Hop + FrameSize / 2.0) / sampleRate;
        return new Result(tempo, confidence, firstBeat);
    }

    /// <summary>The spectral flux of a mono mix: per frame, the sum of how much each bin's log magnitude grew.</summary>
    static double[] Onsets(float[] left, float[] right, int sampleRate, int start, int count, Action<double>? progress)
    {
        int frames = Math.Max(0, (count - FrameSize) / Hop + 1);
        var env = new double[frames];
        int every = Math.Max(1, frames / ProgressSteps);
        var fft = new Fft(FrameSize);
        double[] re = new double[FrameSize], im = new double[FrameSize], window = new double[FrameSize];
        for (int i = 0; i < FrameSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FrameSize);
        int top = Math.Min(FrameSize / 2, (int)(FluxTopHz * FrameSize / sampleRate));
        var prev = new double[top];
        // 1000 / FrameSize puts a full-scale partial near log(1 + 250): soft notes still rise above the floor
        double gain = 1000.0 / FrameSize;
        for (int f = 0; f < frames; f++)
        {
            if (f % every == 0) progress?.Invoke(OnsetShare * f / frames);
            int at = start + f * Hop;
            for (int i = 0; i < FrameSize; i++)
            {
                re[i] = 0.5 * (left[at + i] + right[at + i]) * window[i];
                im[i] = 0;
            }
            fft.Transform(re, im, inverse: false);
            double flux = 0;
            for (int k = 1; k < top; k++)
            {
                double m = Math.Log(1 + gain * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                if (f > 0 && m > prev[k]) flux += m - prev[k];
                prev[k] = m;
            }
            env[f] = flux;
        }
        return env;
    }

    /// <summary>Takes off the mean of the <paramref name="width"/> frames around each one and keeps what pokes above it.</summary>
    static void Detrend(double[] env, int width)
    {
        int n = env.Length, half = Math.Max(1, width / 2);
        var sum = new double[n + 1];
        for (int i = 0; i < n; i++) sum[i + 1] = sum[i] + env[i];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(n, i + half + 1);
            env[i] = Math.Max(0, env[i] - (sum[b] - sum[a]) / (b - a));
        }
    }

    /// <summary>The autocorrelation coefficient (mean taken off, unbiased, 1 at lag 0) up to <paramref name="lags"/>.</summary>
    static double[] Autocorrelation(double[] env, int lags)
    {
        int n = env.Length;
        double mean = 0;
        for (int i = 0; i < n; i++) mean += env[i];
        mean /= n;
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = env[i] - mean;
        var ac = new double[lags + 1];
        for (int lag = 0; lag <= lags; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < n; i++) s += x[i] * x[i + lag];
            ac[lag] = s / (n - lag);
        }
        double zero = ac[0];
        for (int lag = 0; lag <= lags; lag++) ac[lag] = zero > 0 ? ac[lag] / zero : 0;
        return ac;
    }

    /// <summary>Where the peak of a parabola through three points sits, from the middle one (−0.5..0.5).</summary>
    static double Parabola(double a, double b, double c)
    {
        double d = a - 2 * b + c;
        return d < 0 ? Math.Clamp(0.5 * (a - c) / d, -0.5, 0.5) : 0;
    }

    /// <summary>A light [¼ ½ ¼] blur: a beat played a little early or late still hits the comb.</summary>
    static double[] Smooth(double[] env)
    {
        int n = env.Length;
        var s = new double[n];
        for (int i = 0; i < n; i++)
            s[i] = 0.5 * env[i] + 0.25 * (env[Math.Max(0, i - 1)] + env[Math.Min(n - 1, i + 1)]);
        return s;
    }

    /// <summary>
    /// Tries the tempos within <see cref="RefineSpan"/> of <paramref name="bpm"/> and, for each, every phase in half
    /// frames: the one whose beats land on the most onset (per beat) wins. Gives the tempo and its first beat's frame.
    /// </summary>
    static (double Bpm, double Phase) Refine(double[] env, double rate, double bpm, Action<double>? progress)
    {
        int n = env.Length;
        double bestBpm = bpm, bestPhase = 0, best = double.NegativeInfinity;
        double from = bpm * (1 - RefineSpan), span = 2 * RefineSpan * bpm;
        for (double b = from; b <= bpm * (1 + RefineSpan); b += RefineStepBpm)
        {
            progress?.Invoke(OnsetShare + (1 - OnsetShare) * (b - from) / span);
            double period = 60 * rate / b;
            for (double phase = 0; phase < period; phase += 0.5)
            {
                double s = 0;
                int beats = 0;
                for (double t = phase; t < n - 1; t += period, beats++)
                {
                    int i = (int)t;
                    double f = t - i;
                    s += env[i] * (1 - f) + env[i + 1] * f;
                }
                if (beats > 0 && s / beats > best) { best = s / beats; bestBpm = b; bestPhase = phase; }
            }
        }
        return (bestBpm, bestPhase);
    }
}
