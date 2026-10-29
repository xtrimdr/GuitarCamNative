namespace WildTrack.Audio.Dsp;

/// <summary>What the tuner hears: the frequency, the nearest note (A4 = 440 Hz) and how far from it in cents.</summary>
public readonly record struct TunerReading(double Hz, int Midi, double Cents)
{
    static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>"E", "F#": letters, like the tuners a guitarist already knows.</summary>
    public string Note => Names[PitchClass];
    /// <summary>0 = C … 11 = B, for the window's own note names.</summary>
    public int PitchClass => ((Midi % 12) + 12) % 12;
    public int Octave => Midi / 12 - 1;

    public static TunerReading FromHz(double hz, double a4 = 440)
    {
        double m = 69 + 12 * Math.Log2(hz / a4);
        int midi = (int)Math.Round(m);
        return new TunerReading(hz, midi, (m - midi) * 100);
    }
}

/// <summary>
/// The tuner (native only, the web has none): the audio thread copies the input's mono mix into a preallocated
/// ring, like <see cref="SpectrumTap"/>; <see cref="Read"/> runs on the reader's thread and finds the pitch with
/// YIN (de Cheveigné &amp; Kawahara, 2002) on the latest samples, halved to 24 kHz so it stays cheap enough for
/// every UI tick. It covers 50–1400 Hz: a 7-string's low B to well above the 12th fret of the high E. Below
/// <see cref="GateDb"/> (the sensitivity, <see cref="DefaultGateDb"/> unless the user moves it) or without a clear
/// period it reads nothing. No allocation after construction.
/// </summary>
public sealed class PitchTap
{
    const int RingSize = 4096;
    const int Mask = RingSize - 1;
    /// <summary>Samples after halving: the analysis window plus the longest period.</summary>
    const int Half = RingSize / 2;
    const int Window = 1024;
    /// <summary>Quieter than this (RMS) is no note: the tuner waits. The sensitivity slider moves it within
    /// <see cref="MinGateDb"/>…<see cref="MaxGateDb"/>.</summary>
    public const double DefaultGateDb = -50;
    public const double MinGateDb = -70, MaxGateDb = -30;
    /// <summary>YIN's absolute threshold on the normalized difference: lower is stricter.</summary>
    const double Threshold = 0.15;

    readonly float[] ring = new float[RingSize];
    long written;
    readonly double[] x = new double[Half];
    readonly double[] d = new double[Half - Window];
    readonly int sampleRate;

    double gateDb = DefaultGateDb;

    public PitchTap(int sampleRate) => this.sampleRate = sampleRate;

    /// <summary>The level gate in dB RMS (lower = more sensitive), kept within range by <see cref="NormalizeGate"/>.
    /// Set and read on the reader's thread.</summary>
    public double GateDb
    {
        get => gateDb;
        set => gateDb = NormalizeGate(value);
    }

    /// <summary>A saved or typed gate: whole dB within range; anything unreadable is the default.</summary>
    public static double NormalizeGate(double db) => double.IsFinite(db) ? Math.Clamp(Math.Round(db), MinGateDb, MaxGateDb) : DefaultGateDb;

    /// <summary>Audio thread: appends a block. No allocation, no locks.</summary>
    public void Write(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        long w = Interlocked.Read(ref written);
        int n = l.Length;
        for (int i = 0; i < n; i++) ring[(int)((w + i) & Mask)] = 0.5f * (l[i] + r[i]);
        Interlocked.Add(ref written, n);
    }

    /// <summary>The pitch of the latest samples, or null when there is no clear note.</summary>
    public TunerReading? Read()
    {
        long w = Interlocked.Read(ref written);
        if (w < RingSize) return null;
        double sum = 0;
        for (int i = 0; i < Half; i++)
        {
            // pairs averaged: a plain halving, the guitar has little up there and YIN only needs the period
            int a = (int)((w - RingSize + 2 * i) & Mask);
            x[i] = 0.5 * (ring[a] + ring[(a + 1) & Mask]);
            sum += x[i] * x[i];
        }
        if (10 * Math.Log10(sum / Half + 1e-20) < GateDb) return null;
        double sr = sampleRate / 2.0;
        int tauMin = (int)(sr / 1400), tauMax = Math.Min(d.Length - 1, (int)(sr / 50));

        // difference function and its cumulative mean normalization
        d[0] = 1;
        double running = 0;
        for (int tau = 1; tau <= tauMax; tau++)
        {
            double s = 0;
            for (int j = 0; j < Window; j++)
            {
                double diff = x[j] - x[j + tau];
                s += diff * diff;
            }
            running += s;
            d[tau] = running > 0 ? s * tau / running : 1;
        }
        int best = -1;
        for (int tau = Math.Max(2, tauMin); tau < tauMax; tau++)
        {
            if (d[tau] >= Threshold) continue;
            while (tau + 1 < tauMax && d[tau + 1] < d[tau]) tau++;
            best = tau;
            break;
        }
        if (best < 0) return null;
        // parabolic interpolation around the minimum for sub-sample accuracy (a cent is 0,06 % of the period)
        double y0 = d[best - 1], y1 = d[best], y2 = d[best + 1];
        double den = y0 - 2 * y1 + y2;
        double t = best + (Math.Abs(den) > 1e-12 ? 0.5 * (y0 - y2) / den : 0);
        return TunerReading.FromHz(sr / t);
    }
}
