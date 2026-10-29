namespace WildTrack.Audio;

public enum ClickSound { Bip, Madera, Clave, Aguja, Cencerro, Bateria }

/// <summary>
/// Metronome clicks (port of clickSounds.ts). The web app schedules oscillators, filters and gain ramps on
/// the AudioContext; here each click is rendered once into a buffer and mixed in at the beat frame.
/// Every sound peaks around 0.35 at 0 dB, like there.
/// </summary>
public static class ClickSynth
{
    const double BasePeak = 0.35;
    const double LengthSec = 0.32;

    enum Wave { Sine, Triangle, Square }
    enum FilterType { Bandpass, Highpass }

    public static float[] Render(ClickSound sound, bool accent, int sampleRate, double volumeDb = 0)
    {
        var r = new Renderer(sampleRate, (int)Math.Round(sampleRate * LengthSec));
        double lvl = BasePeak * Math.Pow(10, volumeDb / 20);
        switch (sound)
        {
            case ClickSound.Bip:
            {
                var s = r.Tone(Wave.Triangle, _ => accent ? 1200 : 800, 0.04);
                for (int i = 0; i < s.Length; i++)
                {
                    double t = i / r.Fs;
                    s[i] *= t < 0.045 ? lvl * Math.Pow(0.003, t / 0.045) : lvl * 0.003;
                }
                r.Add(s);
                break;
            }
            case ClickSound.Madera:
            {
                // Two inharmonic modes of a struck block + a tick of noise for the attack.
                double f = accent ? 1750 : 1250;
                r.Add(r.Env(r.Tone(Wave.Sine, _ => f, 0.08), lvl * 0.75, 0.07));
                r.Add(r.Env(r.Tone(Wave.Sine, _ => f * 2.57, 0.04), lvl * 0.3, 0.035));
                r.Add(r.Env(r.Filter(r.Noise(0.012), FilterType.Bandpass, f * 2, 1.5), lvl * 0.35, 0.008, 0.0005));
                break;
            }
            case ClickSound.Clave:
            {
                double f = accent ? 2900 : 2300;
                r.Add(r.Env(r.Tone(Wave.Sine, _ => f, 0.06), lvl * 0.9, 0.05));
                r.Add(r.Env(r.Filter(r.Noise(0.008), FilterType.Highpass, 4000), lvl * 0.25, 0.005, 0.0005));
                break;
            }
            case ClickSound.Aguja:
            {
                // Square wave, high-passed so only the bright edge is left: very short and cutting.
                double f = accent ? 2400 : 1800;
                r.Add(r.Env(r.Filter(r.Tone(Wave.Square, _ => f, 0.03), FilterType.Highpass, 1500), lvl * 0.55, 0.025, 0.0005));
                break;
            }
            case ClickSound.Cencerro:
            {
                // Two detuned squares through a band-pass, like the classic drum-machine cowbell.
                double k = accent ? 1.25 : 1;
                var a = r.Tone(Wave.Square, _ => 560 * k, 0.24);
                var b = r.Tone(Wave.Square, _ => 835 * k, 0.24);
                for (int i = 0; i < a.Length; i++) a[i] += b[i];
                r.Add(r.Env(r.Filter(a, FilterType.Bandpass, 2400 * k, 1.2), lvl * 0.82, 0.22));
                break;
            }
            case ClickSound.Bateria:
            {
                if (accent)
                {
                    // Kick: sine with a fast pitch drop.
                    var kick = r.Tone(Wave.Sine, t => t < 0.12 ? 160 * Math.Pow(48.0 / 160, t / 0.12) : 48, 0.29);
                    r.Add(r.Env(kick, lvl * 0.7, 0.28, 0.002));
                }
                r.Add(r.Env(r.Filter(r.Noise(0.06), FilterType.Highpass, 7000), lvl * (accent ? 0.35 : 0.85), 0.05, 0.0005));
                break;
            }
        }
        return r.Output();
    }

    sealed class Renderer(int sampleRate, int length)
    {
        readonly double[] mix = new double[length];
        public double Fs { get; } = sampleRate;

        public void Add(double[] s)
        {
            for (int i = 0; i < mix.Length; i++) mix[i] += s[i];
        }

        public float[] Output() => Array.ConvertAll(mix, v => (float)v);

        /// <summary>Oscillator from t = 0, stopped at <paramref name="dur"/> + 10 ms (like tone()).</summary>
        public double[] Tone(Wave wave, Func<double, double> freq, double dur)
        {
            var o = new double[length];
            int stop = Math.Min(length, (int)Math.Round((dur + 0.01) * Fs));
            double ph = 0;
            for (int i = 0; i < stop; i++)
            {
                double dt = freq(i / Fs) / Fs;
                o[i] = wave switch
                {
                    Wave.Sine => Math.Sin(2 * Math.PI * ph),
                    Wave.Triangle => ph < 0.25 ? 4 * ph : ph < 0.75 ? 2 - 4 * ph : 4 * ph - 4,
                    _ => (ph < 0.5 ? 1 : -1) + PolyBlep(ph, dt) - PolyBlep((ph + 0.5) % 1, dt),
                };
                ph += dt;
                if (ph >= 1) ph -= 1;
            }
            return o;
        }

        // Band-limits the square's edges (Web Audio oscillators are band-limited too).
        static double PolyBlep(double t, double dt)
        {
            if (t < dt)
            {
                t /= dt;
                return t + t - t * t - 1;
            }
            if (t > 1 - dt)
            {
                t = (t - 1) / dt;
                return t * t + t + t + 1;
            }
            return 0;
        }

        /// <summary>The same seeded white noise as the web app (xorshift32).</summary>
        public double[] Noise(double dur)
        {
            var o = new double[length];
            int stop = Math.Min(Math.Min(length, (int)Math.Round(Fs * 0.3)), (int)Math.Round((dur + 0.01) * Fs));
            uint s = 0x2545f491;
            for (int i = 0; i < stop; i++)
            {
                s ^= s << 13;
                s ^= s >> 17;
                s ^= s << 5;
                o[i] = s / (double)0xffffffff * 2 - 1;
            }
            return o;
        }

        /// <summary>Web Audio BiquadFilterNode formulas (highpass Q in dB, bandpass Q linear).</summary>
        public double[] Filter(double[] x, FilterType type, double freq, double q = 0.7)
        {
            double w = 2 * Math.PI * freq / Fs;
            double cs = Math.Cos(w), sn = Math.Sin(w);
            double b0, b1, b2, a0, a1, a2;
            if (type == FilterType.Bandpass)
            {
                double al = sn / (2 * q);
                (b0, b1, b2, a0, a1, a2) = (al, 0, -al, 1 + al, -2 * cs, 1 - al);
            }
            else
            {
                double al = sn / (2 * Math.Pow(10, q / 20));
                (b0, b1, b2, a0, a1, a2) = ((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + al, -2 * cs, 1 - al);
            }
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double y = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1;
                x1 = x[i];
                y2 = y1;
                y1 = y;
                x[i] = y;
            }
            return x;
        }

        /// <summary>Linear attack to <paramref name="peak"/>, then exponential down to -60 dB after <paramref name="dur"/>.</summary>
        public double[] Env(double[] x, double peak, double dur, double attack = 0.001)
        {
            for (int i = 0; i < x.Length; i++)
            {
                double t = i / Fs;
                double g = t < attack ? peak * t / attack
                    : t < attack + dur ? peak * Math.Pow(0.001, (t - attack) / dur)
                    : peak * 0.001;
                x[i] *= g;
            }
            return x;
        }
    }
}
