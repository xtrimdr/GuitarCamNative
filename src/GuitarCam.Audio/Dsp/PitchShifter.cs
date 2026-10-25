namespace GuitarCam.Audio.Dsp;

/// <summary>
/// Offline transpose that keeps the length (the take stays in sync with its video), done directly on the spectrum:
/// each 85 ms frame stays where it is in time and its spectrum is read at f/ratio, so every frequency moves by the ratio
/// with no stretching and no resampling (stretch-then-resample spreads an attack over several milliseconds, because
/// each frame lands somewhere else). The phases are rebuilt as in "Phase Vocoder Done Right" (Průša &amp; Holighaus
/// 2017): from the loudest bins outwards (a max-heap), along time with the moved instantaneous frequency and along
/// frequency with the analysis phase differences (identity phase locking, Laroche &amp; Dolson 1999), which also carry
/// where in the frame each sound sits. So the partials of a chord stay locked to each other (no "phasiness"), attacks
/// stay put and ratio 1 gives back the input. Both channels turn by the same phase rotation, taken from their sum, so
/// the stereo image and a mono take stay exactly as they were. Frequencies that would pass Nyquist are simply not
/// read, so nothing aliases. The analysis and the synthesis of a block of frames run in parallel; only the phase
/// integration goes frame by frame. Runs on worker threads, never on the audio thread.
/// </summary>
public static class PitchShifter
{
    /// <summary>Changes whenever the result would sound different, so cached renders are made again.</summary>
    public const int Version = 1;
    public const double MaxSemitones = 12;

    /// <summary>85 ms at 48 kHz: resolves the harmonics of a low E (82 Hz apart) into separate bins.</summary>
    const int FrameSize = 4096;
    /// <summary>Hop of an eighth of the frame, as the method wants.</summary>
    const int Hop = FrameSize / 8;
    /// <summary>
    /// The FFT is four times the frame: the zero padding gives a lowered frame room to grow (an octave down doubles
    /// it) and a fine grid to read the spectrum between bins (twice the frame leaves −34 dB of error an octave up, four −39).
    /// </summary>
    const int FftSize = FrameSize * 4;
    const int Half = FftSize / 2, Bins = Half + 1;
    /// <summary>
    /// The phases are grown on every 4th bin, one per bin of the frame without padding; the padding's bins in between
    /// only follow (four times faster, the same sound).
    /// </summary>
    const int Grid = 2;
    const int Coarse = Half / Grid + 1;
    /// <summary>A bin this far under the frame's loudest (−100 dB) does not steer the phase of its neighbours.</summary>
    const double Tolerance = 1e-5;
    /// <summary>Frames analysed and synthesised at once, in parallel (about 25 MB of buffers).</summary>
    const int Block = 32;

    /// <summary>One frame: the sum's magnitude and phase, each channel relative to the sum (Q = X·conj(S)/|S|), the new phases and the FFT buffers.</summary>
    sealed class Frame
    {
        public readonly double[] Mag = new double[Bins], Phase = new double[Bins];
        public readonly double[] QlRe = new double[Bins], QlIm = new double[Bins], QrRe = new double[Bins], QrIm = new double[Bins];
        public readonly double[] Psi = new double[Bins];
        /// <summary>What the output bins take from it: magnitude, frequency (rad/sample) and phase step to the next bin.</summary>
        public readonly double[] Omega = new double[Bins], YMag = new double[Bins], YOmega = new double[Bins], Slope = new double[Bins];
        /// <summary>The loudness rank of every Grid-th output bin, for the phase integration's queue.</summary>
        public readonly int[] Rank = new int[Coarse];
        public double YMax;
        public readonly double[] Re = new double[FftSize], Im = new double[FftSize];
    }

    /// <summary>
    /// The audio moved by <paramref name="semitones"/> (fractions are cents), the same length as it came. 0 gives a copy.
    /// <paramref name="progress"/> goes from 0 to 1.
    /// </summary>
    public static (float[] Left, float[] Right) Shift(float[] left, float[] right, double semitones,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        int len = left.Length;
        if (Math.Abs(semitones) < 1e-6 || len == 0) return ((float[])left.Clone(), (float[])right.Clone());
        double ratio = Math.Pow(2, Math.Clamp(semitones, -MaxSemitones, MaxSemitones) / 12);
        const int m = FrameSize, n = FftSize;

        int frames = (len + m / 2) / Hop + 1;
        var win = new double[m];
        for (int k = 0; k < m; k++) win[k] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * k / m);
        // read at f/ratio, a frame is the input frame squeezed by the ratio in time: its window is m/ratio long
        // and it holds 1/ratio of the level
        double grain = m / ratio;
        int reach = (int)Math.Min(Half - 1, Math.Ceiling(grain / 2));
        var synthWin = new double[2 * reach + 1];
        for (int o = -reach; o <= reach; o++)
        {
            double u = o / grain + 0.5;
            synthWin[o + reach] = u <= 0 || u >= 1 ? 0 : 0.5 - 0.5 * Math.Cos(2 * Math.PI * u);
        }

        var fft = new Fft(n);
        var block = new Frame[Block];
        for (int i = 0; i < Block; i++) block[i] = new Frame();
        var outL = new double[len];
        var outR = new double[len];
        var norm = new double[len];

        // the input bin each output bin takes its phase from (the nearest to b/ratio; -1 past Nyquist)
        var src = new int[Bins];
        for (int b = 0; b < Bins; b++)
        {
            double s = b / ratio;
            src[b] = s >= Half ? -1 : (int)Math.Round(s);
        }
        // the last frame of the previous block
        var phasePrev = new double[Bins];
        var yMagPrev = new double[Bins];
        var rankPrev = new int[Coarse];
        var psiPrev = new double[Bins];
        var done = new bool[Coarse];
        var coarseSlope = new double[Coarse];
        var heap = new RankQueue(2 * Coarse);
        var seeds = new RankQueue(Coarse);
        double yMaxPrev = 0;
        var options = new ParallelOptions { CancellationToken = ct };

        for (int f0 = 0; f0 < frames; f0 += Block)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(f0 / (double)frames);
            int count = Math.Min(Block, frames - f0);
            Parallel.For(0, count, options, k => Analyze(block[k], (f0 + k) * Hop));
            Parallel.For(0, count, options, k => Read(block[k], f0 + k == 0, k > 0 ? block[k - 1].Phase : phasePrev));

            for (int k = 0; k < count; k++)
            {
                var fr = block[k];
                if (f0 + k == 0)
                {
                    for (int b = 0; b < Bins; b++) fr.Psi[b] = src[b] < 0 ? 0 : fr.Phase[src[b]];
                }
                else if (k == 0) Integrate(fr, psiPrev, yMagPrev, rankPrev, yMaxPrev);
                else Integrate(fr, block[k - 1].Psi, block[k - 1].YMag, block[k - 1].Rank, block[k - 1].YMax);
            }
            var last = block[count - 1];
            Array.Copy(last.Phase, phasePrev, Bins);
            Array.Copy(last.Psi, psiPrev, Bins);
            Array.Copy(last.YMag, yMagPrev, Bins);
            Array.Copy(last.Rank, rankPrev, Coarse);
            yMaxPrev = last.YMax;

            Parallel.For(0, count, options, k => Synthesize(block[k]));
            for (int k = 0; k < count; k++)
            {
                var fr = block[k];
                int center = (f0 + k) * Hop;
                for (int o = -reach; o <= reach; o++)
                {
                    int t = center + o;
                    if ((uint)t >= (uint)len) continue;
                    double w = synthWin[o + reach];
                    int p = (o + n) & (n - 1);
                    outL[t] += fr.Re[p] * w;
                    outR[t] += fr.Im[p] * w;
                    norm[t] += w * w;
                }
            }
        }
        progress?.Invoke(1);

        var l = new float[len];
        var r = new float[len];
        for (int i = 0; i < len; i++)
        {
            double g = 1 / Math.Max(norm[i], 0.1);
            l[i] = (float)(outL[i] * g);
            r[i] = (float)(outR[i] * g);
        }
        return (l, r);

        // windowed frame centered on `center`, zero-phase and zero-padded, L in the real part and R in the imaginary
        void Analyze(Frame fr, int center)
        {
            var re = fr.Re;
            var im = fr.Im;
            Array.Clear(re);
            Array.Clear(im);
            for (int k = 0; k < m; k++)
            {
                int s = center + k - m / 2;
                if ((uint)s >= (uint)len) continue;
                int p = (k - m / 2 + n) & (n - 1);
                re[p] = left[s] * win[k];
                im[p] = right[s] * win[k];
            }
            fft.Transform(re, im, inverse: false);
            for (int b = 0; b < Bins; b++)
            {
                int c = (n - b) & (n - 1);
                double a = re[b], bi = im[b], cr = re[c], ci = -im[c];
                double lr = 0.5 * (a + cr), li = 0.5 * (bi + ci);
                double rr = 0.5 * (bi - ci), ri = 0.5 * (cr - a);
                double sr = lr + rr, si = li + ri;
                double sm = Math.Sqrt(sr * sr + si * si);
                fr.Mag[b] = sm;
                fr.Phase[b] = Math.Atan2(si, sr);
                if (sm > 0)
                {
                    double ur = sr / sm, ui = -si / sm;
                    fr.QlRe[b] = lr * ur - li * ui;
                    fr.QlIm[b] = lr * ui + li * ur;
                    fr.QrRe[b] = rr * ur - ri * ui;
                    fr.QrIm[b] = rr * ui + ri * ur;
                }
                else
                {
                    fr.QlRe[b] = Math.Sqrt(lr * lr + li * li);
                    fr.QrRe[b] = Math.Sqrt(rr * rr + ri * ri);
                    fr.QlIm[b] = fr.QrIm[b] = 0;
                }
            }
        }

        // the frame's instantaneous frequencies, and what each output bin takes from the input at b/ratio: magnitude,
        // frequency × ratio and the phase step to the next bin
        void Read(Frame fr, bool first, double[] before)
        {
            var mag = fr.Mag;
            var omega = fr.Omega;
            for (int b = 0; b < Bins; b++)
            {
                double expected = 2 * Math.PI * b / n;
                omega[b] = first ? expected : expected + PrincArg(fr.Phase[b] - before[b] - expected * Hop) / Hop;
            }
            double yMax = 0;
            for (int b = 0; b < Bins; b++)
            {
                if (src[b] < 0)
                {
                    fr.YMag[b] = 0;
                    fr.YOmega[b] = 0;
                    fr.Slope[b] = 0;
                    continue;
                }
                double s = b / ratio;
                int i = Math.Min(Half - 1, (int)s);
                double v = mag[i] + (mag[i + 1] - mag[i]) * (s - i);
                fr.YMag[b] = v;
                fr.YOmega[b] = omega[src[b]] * ratio;
                // phase change per bin = where in the frame the sound sits: kept as it is, so attacks stay put
                int j = Math.Min(Half - 1, (int)((b + 0.5) / ratio));
                fr.Slope[b] = PrincArg(fr.Phase[j + 1] - fr.Phase[j]);
                if (v > yMax) yMax = v;
            }
            fr.YMax = yMax;
            for (int c = 0; c < Coarse; c++) fr.Rank[c] = RankQueue.Rank(fr.YMag[c * Grid]);
        }

        // the frame's phases grown from the previous frame, loudest bins first (PVDR, algorithm 1), on every
        // Grid-th bin; the bins in between follow the louder of their two neighbours with the phase step per bin
        void Integrate(Frame fr, double[] psiPrev, double[] yMagPrev, int[] rankPrev, double yMaxPrev)
        {
            var rank = fr.Rank;
            var psi = fr.Psi;
            var yMag = fr.YMag;
            var yOmega = fr.YOmega;
            var slope = fr.Slope;
            double tol = Tolerance * Math.Max(fr.YMax, yMaxPrev);
            for (int c = 0; c + 1 < Coarse; c++)
            {
                double step = 0;
                for (int k = c * Grid; k < (c + 1) * Grid; k++) step += slope[k];
                coarseSlope[c] = step;
            }
            int remaining = 0;
            seeds.Clear();
            for (int c = 0; c < Coarse; c++)
            {
                int b = c * Grid;
                done[c] = yMag[b] < tol;
                if (done[c]) psi[b] = psiPrev[b] + Hop * yOmega[b];
                else
                {
                    remaining++;
                    seeds.Push(rank[c], c);
                }
            }
            heap.Clear();
            for (int c = 0; c < Coarse; c++)
                if (yMagPrev[c * Grid] >= tol) heap.Push(rankPrev[c], c);

            while (remaining > 0)
            {
                if (heap.Count == 0)
                {
                    // nothing left to grow from (sound out of silence): the loudest free bin keeps its own phase
                    int best = seeds.Pop();
                    while (done[best]) best = seeds.Pop();
                    psi[best * Grid] = fr.Phase[src[best * Grid]];
                    done[best] = true;
                    remaining--;
                    heap.Push(rank[best], best + Coarse);
                    continue;
                }
                int v = heap.Pop();
                if (v < Coarse)
                {
                    if (done[v]) continue;
                    int b = v * Grid;
                    psi[b] = psiPrev[b] + Hop * yOmega[b];
                    done[v] = true;
                    remaining--;
                    heap.Push(rank[v], v + Coarse);
                }
                else
                {
                    int c = v - Coarse;
                    if (c + 1 < Coarse && !done[c + 1])
                    {
                        psi[(c + 1) * Grid] = psi[c * Grid] + coarseSlope[c];
                        done[c + 1] = true;
                        remaining--;
                        heap.Push(rank[c + 1], c + 1 + Coarse);
                    }
                    if (c > 0 && !done[c - 1])
                    {
                        psi[(c - 1) * Grid] = psi[c * Grid] - coarseSlope[c - 1];
                        done[c - 1] = true;
                        remaining--;
                        heap.Push(rank[c - 1], c - 1 + Coarse);
                    }
                }
            }

            for (int c = 0; c + 1 < Coarse; c++)
            {
                int b0 = c * Grid, b1 = b0 + Grid;
                if (yMag[b0] >= yMag[b1])
                {
                    double p = psi[b0];
                    for (int b = b0 + 1; b < b1; b++) psi[b] = p += slope[b - 1];
                }
                else
                {
                    double p = psi[b1];
                    for (int b = b1 - 1; b > b0; b--) psi[b] = p -= slope[b];
                }
            }
        }

        // each channel = its part of the sum read at b/ratio, turned to the new phase; the grain is left in Re (L) and Im (R)
        void Synthesize(Frame fr)
        {
            var re = fr.Re;
            var im = fr.Im;
            double scale = 1 / (ratio * n);
            for (int b = 0; b < Bins; b++)
            {
                double s = b / ratio;
                int i = (int)s;
                double lr = 0, li = 0, rr = 0, ri = 0;
                if (i < Half)
                {
                    double t = s - i;
                    double qlr = fr.QlRe[i] + (fr.QlRe[i + 1] - fr.QlRe[i]) * t, qli = fr.QlIm[i] + (fr.QlIm[i + 1] - fr.QlIm[i]) * t;
                    double qrr = fr.QrRe[i] + (fr.QrRe[i + 1] - fr.QrRe[i]) * t, qri = fr.QrIm[i] + (fr.QrIm[i + 1] - fr.QrIm[i]) * t;
                    double c = Math.Cos(fr.Psi[b]) * scale, sn = Math.Sin(fr.Psi[b]) * scale;
                    lr = qlr * c - qli * sn;
                    li = qlr * sn + qli * c;
                    rr = qrr * c - qri * sn;
                    ri = qrr * sn + qri * c;
                }
                if (b == 0 || b == Half) { li = 0; ri = 0; } // DC and Nyquist stay real
                re[b] = lr - ri;
                im[b] = li + rr;
                if (b > 0 && b < Half)
                {
                    re[n - b] = lr + ri;
                    im[n - b] = rr - li;
                }
            }
            fft.Transform(re, im, inverse: true);
        }
    }

    static double PrincArg(double x) => x - 2 * Math.PI * Math.Round(x / (2 * Math.PI));

    /// <summary>
    /// The phase integration's "loudest first" queue: bins sorted into quarter-dB buckets, so pushing and popping cost
    /// the same however many bins there are (a binary heap made the integration three quarters of the work). Within a
    /// bucket the order is arbitrary, a quarter of a dB does not matter. Each value is pushed at most once per frame.
    /// </summary>
    sealed class RankQueue(int capacity)
    {
        /// <summary>From −200 dB to +80 dB (a full-scale sine in an unnormalised frame is about +60).</summary>
        const double FloorDb = -200, StepsPerDb = 4;
        public const int Ranks = (int)(280 * StepsPerDb) + 1;

        readonly int[] head = new int[Ranks];
        readonly int[] next = new int[capacity];
        int top = -1;
        public int Count { get; private set; }

        public static int Rank(double mag) =>
            mag <= 0 ? 0 : (int)Math.Clamp((20 * Math.Log10(mag) - FloorDb) * StepsPerDb, 0, Ranks - 1);

        public void Clear()
        {
            Array.Fill(head, -1);
            top = -1;
            Count = 0;
        }

        public void Push(int rank, int val)
        {
            next[val] = head[rank];
            head[rank] = val;
            if (rank > top) top = rank;
            Count++;
        }

        public int Pop()
        {
            while (head[top] < 0) top--;
            int val = head[top];
            head[top] = next[val];
            Count--;
            return val;
        }
    }
}

/// <summary>In-place radix-2 complex FFT in double precision, with its twiddles and bit reversal precomputed; one instance can serve several threads.</summary>
public sealed class Fft
{
    readonly int n;
    readonly double[] cos, sin;
    readonly int[] rev;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("FFT size must be a power of two.");
        n = size;
        cos = new double[n / 2];
        sin = new double[n / 2];
        for (int i = 0; i < n / 2; i++)
        {
            cos[i] = Math.Cos(2 * Math.PI * i / n);
            sin[i] = Math.Sin(2 * Math.PI * i / n);
        }
        rev = new int[n];
        int bits = System.Numerics.BitOperations.Log2((uint)n);
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            rev[i] = r;
        }
    }

    /// <summary>Forward is e^(−i·2πkn/N); the inverse is not scaled.</summary>
    public void Transform(double[] re, double[] im, bool inverse)
    {
        for (int i = 0; i < n; i++)
        {
            int j = rev[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        double sign = inverse ? 1 : -1;
        for (int len = 2; len <= n; len <<= 1)
        {
            int halfLen = len >> 1, step = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0, t = 0; k < halfLen; k++, t += step)
                {
                    double wr = cos[t], wi = sign * sin[t];
                    int a = i + k, b = a + halfLen;
                    double xr = re[b] * wr - im[b] * wi;
                    double xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }
    }
}
