namespace WildTrack.Audio.Dsp;

/// <summary>
/// What "Sugerime" heard in the mix: the level of each octave band (dB, power per octave, from 31,5 Hz to 16 kHz, over
/// the moments that sound) and how spiky each of the multiband's bands and the Púa's are (crest: the loud peaks over
/// the band's RMS, dB).
/// </summary>
public sealed record MixProfile(double[] OctaveDb, double[] BandCrestDb, double PickCrestDb);

/// <summary>One thing "Sugerime" proposes: its text (already in the window's language, <see cref="Texts"/>).</summary>
public sealed record SuggestionLine(string Text);

/// <summary>What "Sugerime" proposes: the master's settings to take and why, a line each.</summary>
public sealed record MasterSuggestion(MasterParams Params, IReadOnlyList<SuggestionLine> Lines);

/// <summary>
/// "Sugerime" (asked by the user: it shows what it would do and you confirm). It renders the mix before the master
/// (the same <see cref="MixRenderer"/> as playback, master and limiter out) and listens to it with a bank of octave
/// filters and the multiband's own crossover (<see cref="Crossover3"/>), streaming: the song is never kept whole.
/// Then it compares the octaves with <see cref="Reference"/>, the balance of a finished guitar mix, and proposes
/// gentle moves: cleanup where something is in excess (rumble, mud), colour where something is missing (lows,
/// presence, air), each capped, and how much each band compresses from how spiky it is. Only the loud moments count
/// (<see cref="GateDb"/> under the loudest), so a quiet intro does not decide.
/// </summary>
public static class MasterSuggest
{
    public static readonly double[] Octaves = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    /// <summary>A finished guitar mix's octave balance (dB, power per octave), its 250 Hz–2 kHz average at 0.</summary>
    public static readonly double[] Reference = [-14, -6, -2, -0.5, 0.5, 0.5, -0.5, -3.5, -8, -15];
    /// <summary>How long each look at the mix is (s): long enough for the lows, short enough to tell loud from quiet.</summary>
    const double BlockSec = 0.1;
    /// <summary>The peaks for the crest are taken over this (s), and the loud ones are this percentile.</summary>
    const double PeakSec = 0.01, PeakPercentile = 0.99;
    /// <summary>Blocks this far under the loudest one do not count.</summary>
    const double GateDb = 20;
    /// <summary>An octave band filter's Q (one octave wide).</summary>
    const double OctaveQ = 1.414;

    static Biquad.Coeffs BandPass(double fs, double f, double q)
    {
        double w0 = 2 * Math.PI * Math.Min(f, fs * 0.45) / fs, alpha = Math.Sin(w0) / (2 * q), a0 = 1 + alpha;
        return new Biquad.Coeffs(new BiquadSpec(BiquadType.Peaking, f, q), alpha / a0, 0, -alpha / a0, -2 * Math.Cos(w0) / a0, (1 - alpha) / a0);
    }

    /// <summary>Listens to the mix before the master. <paramref name="durationSec"/> is how much of it (its end).</summary>
    public static MixProfile Analyze(IReadOnlyList<MixTrackSpec> tracks, MixBusSpec bus, double durationSec, int sampleRate,
        CancellationToken ct = default)
    {
        var acc = new Listener(sampleRate);
        MixRenderer.RenderTo(tracks, bus, durationSec, (_, l, r) =>
        {
            ct.ThrowIfCancellationRequested();
            acc.Write(l, r);
        }, sampleRate, null, ct, limit: false, master: false);
        return acc.Result();
    }

    /// <summary>Listens to audio already rendered (wtlab, the checks).</summary>
    public static MixProfile Analyze(float[] left, float[] right, int sampleRate)
    {
        var acc = new Listener(sampleRate);
        acc.Write(left, right);
        return acc.Result();
    }

    /// <summary>The octave filters, the crossover and the per-block energies and peaks, fed stretch by stretch.</summary>
    sealed class Listener
    {
        /// <summary>The band passes, the octaves and then the Púa's, inlined (b1 = 0, b2 = −b0): this runs over the whole song.</summary>
        readonly double[] b0, a1, a2, x1, x2, y1, y2;
        readonly Crossover3 xo;

        readonly int block, peakLen;
        readonly double[] octE;
        readonly double[] bandE = new double[MasterBands.Count + 1];
        readonly float[] bandPk = new float[MasterBands.Count + 1];
        // per block: total energy, each octave's, each band's; per short stretch: each band's peak
        readonly List<double> blockTotal = [];
        readonly List<double[]> blockOct = [];
        readonly List<double[]> blockBand = [];
        readonly List<float[]> peaks = [];
        readonly List<int> peakBlock = [];
        int inBlock, inPeak;
        double total;
        /// <summary>The crossover splits this much at a time.</summary>
        const int Chunk = 4096;
        readonly float[][] bl = [new float[Chunk], new float[Chunk], new float[Chunk]], br = [new float[Chunk], new float[Chunk], new float[Chunk]];

        public Listener(int fs)
        {
            var bps = Octaves.Select(f => BandPass(fs, f, OctaveQ)).Append(BandPass(fs, MasterPickTamer.CenterHz, MasterPickTamer.Q)).ToArray();
            b0 = bps.Select(c => c.B0).ToArray();
            a1 = bps.Select(c => c.A1).ToArray();
            a2 = bps.Select(c => c.A2).ToArray();
            x1 = new double[bps.Length]; x2 = new double[bps.Length]; y1 = new double[bps.Length]; y2 = new double[bps.Length];
            octE = new double[Octaves.Length];
            xo = new Crossover3(fs);
            xo.Set(MasterBands.DefaultLowXHz, MasterBands.DefaultHighXHz);

            block = (int)(BlockSec * fs);
            peakLen = (int)(PeakSec * fs);
        }

        public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            for (int at = 0; at < left.Length; at += Chunk)
            {
                int n = Math.Min(Chunk, left.Length - at);
                var l = left.Slice(at, n);
                var r = right.Slice(at, n);
                xo.Split(l, r, bl[0].AsSpan(0, n), br[0].AsSpan(0, n), bl[1].AsSpan(0, n), br[1].AsSpan(0, n), bl[2].AsSpan(0, n), br[2].AsSpan(0, n));
                Listen(l, r);
            }
        }

        void Listen(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            for (int i = 0; i < left.Length; i++)
            {
                double m = 0.5 * (left[i] + right[i]);
                total += m * m;
                double p = 0;
                for (int o = 0; o < b0.Length; o++)
                {
                    double y = b0[o] * (m - x2[o]) - a1[o] * y1[o] - a2[o] * y2[o];
                    x2[o] = x1[o]; x1[o] = m; y2[o] = y1[o]; y1[o] = y;
                    if (o == octE.Length) { p = y; break; }
                    octE[o] += y * y;
                }
                for (int b = 0; b < MasterBands.Count; b++)
                {
                    double v = 0.5 * (bl[b][i] + br[b][i]);
                    bandE[b] += v * v;
                    bandPk[b] = Math.Max(bandPk[b], (float)Math.Abs(v));
                }

                bandE[^1] += p * p;
                bandPk[^1] = Math.Max(bandPk[^1], (float)Math.Abs(p));
                if (++inPeak == peakLen)
                {
                    peaks.Add((float[])bandPk.Clone());
                    peakBlock.Add(blockTotal.Count);
                    Array.Clear(bandPk);
                    inPeak = 0;
                }
                if (++inBlock == block)
                {
                    blockTotal.Add(total / block);
                    blockOct.Add(octE.Select(e => e / block).ToArray());
                    blockBand.Add(bandE.Select(e => e / block).ToArray());
                    total = 0;
                    Array.Clear(octE);
                    Array.Clear(bandE);
                    inBlock = 0;
                }
            }
        }

        public MixProfile Result()
        {
            int n = blockTotal.Count;
            double loudest = n > 0 ? blockTotal.Max() : 0;
            double gate = loudest * Math.Pow(10, -GateDb / 10);
            var keep = new bool[n];
            int kept = 0;
            for (int i = 0; i < n; i++) if (blockTotal[i] > 1e-10 && blockTotal[i] >= gate) { keep[i] = true; kept++; }
            var octDb = new double[Octaves.Length];
            var bandRms = new double[MasterBands.Count + 1];
            for (int o = 0; o < Octaves.Length; o++)
            {
                double s = 0;
                for (int i = 0; i < n; i++) if (keep[i]) s += blockOct[i][o];
                octDb[o] = kept > 0 && s > 0 ? 10 * Math.Log10(s / kept) : -120;
            }
            for (int b = 0; b <= MasterBands.Count; b++)
            {
                double s = 0;
                for (int i = 0; i < n; i++) if (keep[i]) s += blockBand[i][b];
                bandRms[b] = kept > 0 && s > 0 ? Math.Sqrt(s / kept) : 0;
            }
            // the loud peaks: the percentile of the short peaks inside the blocks that count
            var crest = new double[MasterBands.Count + 1];
            for (int b = 0; b <= MasterBands.Count; b++)
            {
                var v = new List<float>();
                for (int i = 0; i < peaks.Count; i++)
                    if (peakBlock[i] < n && keep[peakBlock[i]]) v.Add(peaks[i][b]);
                if (v.Count == 0 || bandRms[b] <= 0) continue;
                v.Sort();
                double pk = v[Math.Min(v.Count - 1, (int)(PeakPercentile * v.Count))];
                crest[b] = 20 * Math.Log10(Math.Max(1e-9, pk) / bandRms[b]);
            }
            return new MixProfile(octDb, crest[..MasterBands.Count], crest[^1]);
        }
    }

    /// <summary>The octave levels against <see cref="Reference"/>, both with their 250 Hz–2 kHz average at 0 (dB, + = more than a finished mix).</summary>
    public static double[] Deviation(MixProfile p)
    {
        double Mid(IReadOnlyList<double> v) => (v[3] + v[4] + v[5] + v[6]) / 4;
        double m = Mid(p.OctaveDb), rm = Mid(Reference);
        return p.OctaveDb.Select((v, i) => v - m - (Reference[i] - rm)).ToArray();
    }

    static double Half(double v) => Math.Round(v * 2) / 2;

    /// <summary>A band's "Cuánto" from how spiky it is: a flat band little, a spiky one more.</summary>
    static double Amount(double crestDb) => crestDb switch { > 17 => 0.55, > 13 => 0.4, _ => 0.25 };

    /// <summary>What to propose for <paramref name="p"/>, on top of <paramref name="current"/> (the limiter, the clip, the crossovers and the Destino stay).</summary>
    public static MasterSuggestion Suggest(MixProfile p, MasterParams current)
    {
        var d = Deviation(p);
        var lines = new List<SuggestionLine>();
        static string Db(double v) => $"{v:+0.0;-0.0;0.0} dB";

        // rumble: more under 40 Hz than a finished mix has → cut higher
        double lowCut = d[0] > 4 ? 40 : 30;
        lines.Add(new(d[0] > 4
            ? Texts.T("suggest.lowcut.rumble", "Corte en {0} Hz: hay retumbe en los graves.", lowCut)
            : Texts.T("suggest.lowcut", "Corte en {0} Hz.", lowCut)));

        double mudDev = (d[3] + d[4]) / 2, mud = 0;
        if (mudDev > 1.5)
        {
            mud = -Math.Clamp(Half(mudDev * 0.6), 1, 3);
            lines.Add(new(Texts.T("suggest.mud", "Barro {0}: exceso entre 250 y 500 Hz.", Db(mud))));
        }

        double lowsDev = (d[1] + d[2]) / 2, lows = 0;
        if (lowsDev < -2) lows = Math.Min(2.5, Half(-lowsDev * 0.5));
        else if (lowsDev > 3) lows = -Math.Min(2.5, Half(lowsDev * 0.5));
        if (lows != 0) lines.Add(new(Texts.T(lows > 0 ? "suggest.lows.up" : "suggest.lows.down",
            lows > 0 ? "Graves {0}: le falta cuerpo." : "Graves {0}: sobran graves.", Db(lows))));

        double presDev = (d[6] + d[7]) / 2, presence = 0;
        if (presDev < -2) presence = Math.Min(2.5, Half(-presDev * 0.5));
        else if (presDev > 2) presence = -Math.Min(2.5, Half(presDev * 0.5));
        if (presence != 0) lines.Add(new(Texts.T(presence > 0 ? "suggest.presence.up" : "suggest.presence.down",
            presence > 0 ? "Presencia {0}: está un poco atrás." : "Presencia {0}: está áspera.", Db(presence))));

        double airDev = (d[8] + d[9]) / 2, air = 0;
        if (airDev < -2) air = Math.Min(3, Half(-airDev * 0.6));
        else if (airDev > 3) air = -Math.Min(2, Half(airDev * 0.4));
        if (air != 0) lines.Add(new(Texts.T(air > 0 ? "suggest.air.up" : "suggest.air.down",
            air > 0 ? "Aire {0}: le falta brillo arriba." : "Aire {0}: tiene de más arriba.", Db(air))));

        double low = Amount(p.BandCrestDb[0]), mid = Amount(p.BandCrestDb[1]), high = Amount(p.BandCrestDb[2]);
        lines.Add(new(Texts.T("suggest.bands", "Multibanda {0} / {1} / {2} %, según la dinámica de cada banda.",
            Math.Round(low * 100), Math.Round(mid * 100), Math.Round(high * 100))));

        // the pick: 2–5 kHz much spikier than the mids around it
        double pick = p.PickCrestDb - p.BandCrestDb[1] > 3 || p.PickCrestDb > 20 ? 0.5 : 0;
        if (pick > 0) lines.Add(new(Texts.T("suggest.pick", "Púa al {0} %: ataques fuertes entre 2 y 5 kHz.", Math.Round(pick * 100))));

        if (mud == 0 && lows == 0 && presence == 0 && air == 0)
            lines.Insert(0, new(Texts.T("suggest.even", "Mezcla equilibrada: casi no hace falta EQ.")));

        var next = current with
        {
            On = true,
            LowCutHz = lowCut, MudDb = mud, LowsDb = lows, PresenceDb = presence, AirDb = air, TiltDb = 0,
            Low = new(low), Mid = new(mid), High = new(high), PickAmount = pick,
        };
        return new MasterSuggestion(next, lines);
    }
}
