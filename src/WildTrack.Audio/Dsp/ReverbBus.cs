using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WildTrack.Audio.Dsp;

/// <summary>
/// Port of <c>ReverbSettings</c> in <c>GuitarCam/src/types.ts</c>.
/// </summary>
public readonly record struct ReverbParams(
    double Decay,
    double PreDelayMs,
    double Tone,
    double LowCut,
    double Width,
    double ReturnGain,
    bool Muted = false)
{
    public static readonly ReverbParams Defaults = new(
        Decay: 1.8,
        PreDelayMs: 20,
        Tone: 6000,
        LowCut: 150,
        Width: 1,
        ReturnGain: 0.8,
        Muted: false);
}

public sealed record ReverbPresetDef(
    string Label,
    string Hint,
    double Decay,
    double PreDelayMs,
    double Tone,
    double LowCut,
    double Width);

/// <summary>
/// Port of the reverb bus in <c>GuitarCam/src/audio/mixGraph.ts</c> and presets in <c>Mixer.tsx</c>:
/// <c>lowCut</c> (high-pass) → <c>preDelay</c> → stereo convolution with the seeded synthetic impulse
/// (<see cref="MakeImpulse"/>) → <c>returnGain</c>.
/// Uses zero-latency multi-stage partitioned convolution (128-tap SIMD head + 128-sample short FFT tail +
/// 1024-sample SIMD long FFT tail) with preallocated buffers so it is exact sample-for-sample and very fast
/// in real-time ASIO callbacks.
/// </summary>
public sealed class ReverbBus
{
    public static readonly ReverbPresetDef[] Presets =
    [
        new("Cuarto", "Ambiente corto y cercano", 0.6, 5, 7000, 200, 0.8),
        new("Sala", "Sala chica, natural", 1.3, 15, 6000, 150, 1),
        new("Salón", "Salón grande, más cola", 2.4, 25, 5000, 150, 1),
        new("Placa", "Brillante, clásica para punteos", 1.8, 10, 9500, 250, 1),
        new("Catedral", "Enorme y oscura", 5, 40, 4000, 120, 1),
    ];

    public static string PresetOf(ReverbParams r)
    {
        foreach (var p in Presets)
        {
            if (Math.Abs(r.Decay - p.Decay) < 0.01 &&
                Math.Abs(r.PreDelayMs - p.PreDelayMs) < 0.01 &&
                Math.Abs(r.Tone - p.Tone) < 0.01 &&
                Math.Abs(r.LowCut - p.LowCut) < 0.01 &&
                Math.Abs(r.Width - p.Width) < 0.01)
                return p.Label;
        }
        return "";
    }

    public static ReverbParams ApplyPreset(ReverbParams current, string label)
    {
        foreach (var p in Presets)
        {
            if (p.Label == label)
            {
                return current with
                {
                    Decay = p.Decay,
                    PreDelayMs = p.PreDelayMs,
                    Tone = p.Tone,
                    LowCut = p.LowCut,
                    Width = p.Width,
                };
            }
        }
        return current;
    }

    static double NextMulberry32(ref uint a)
    {
        unchecked
        {
            a += 0x6d2b79f5u;
            uint t = a;
            t = (uint)((int)(t ^ (t >> 15)) * (int)(t | 1u));
            t ^= t + (uint)((int)(t ^ (t >> 7)) * (int)(t | 61u));
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    /// <summary>
    /// Port of <c>makeImpulse</c> in <c>GuitarCam/src/audio/mixGraph.ts</c>: seeded synthetic stereo room
    /// with RT60 exponential decay, high-frequency damping over time, width scaling and unity power gain.
    /// </summary>
    public static (float[] Left, float[] Right) MakeImpulse(int sampleRate, ReverbParams r)
    {
        double decay = Math.Clamp(r.Decay, 0.1, 12);
        int len = Math.Max(1, (int)Math.Ceiling(sampleRate * Math.Min(14.0, decay * 1.15 + 0.05)));
        double tone = Math.Clamp(r.Tone, 500, 20000);
        var ch0 = new float[len];
        var ch1 = new float[len];
        for (int c = 0; c < 2; c++)
        {
            uint seed = unchecked(0x9e3779b9u + (uint)(c * 7919));
            float[] d = c == 0 ? ch0 : ch1;
            double y = 0;
            for (int i = 0; i < len; i++)
            {
                double t = i / (double)sampleRate;
                double fc = tone * (1.0 - 0.75 * Math.Min(1.0, t / decay));
                double a = Math.Exp((-2.0 * Math.PI * fc) / sampleRate);
                y = (1.0 - a) * (NextMulberry32(ref seed) * 2.0 - 1.0) + a * y;
                double env = Math.Exp((-6.9078 * t) / decay) * Math.Min(1.0, t / 0.004);
                d[i] = (float)(y * env);
            }
        }
        double w = Math.Clamp(r.Width, 0, 1);
        double energy = 0;
        for (int i = 0; i < len; i++)
        {
            double m = ((double)ch0[i] + ch1[i]) / 2.0;
            double s = (((double)ch0[i] - ch1[i]) / 2.0) * w;
            ch0[i] = (float)(m + s);
            ch1[i] = (float)(m - s);
            energy += (double)ch0[i] * ch0[i] + (double)ch1[i] * ch1[i];
        }
        double k = energy > 0 ? 1.0 / Math.Sqrt(energy / 2.0) : 0;
        for (int i = 0; i < len; i++)
        {
            ch0[i] = (float)(ch0[i] * k);
            ch1[i] = (float)(ch1[i] * k);
        }
        return (ch0, ch1);
    }

    sealed record ReverbConfig(Biquad.Coeffs LowCut, double PreDelaySamples, float ReturnGain);

    readonly int fs;
    readonly Biquad lowCutFilter;
    readonly float[] preBufL;
    readonly float[] preBufR;
    readonly int preMax;
    int preWrite;
    int busTailRemaining;
    ReverbKey currentKey;
    int irVersion;
    volatile ReverbConfig config;
    volatile ConvolverPair convolver;

    readonly record struct ReverbKey(int DecayMilli, int ToneMilli, int WidthMilli);

    public ReverbBus(int sampleRate)
    {
        fs = sampleRate;
        lowCutFilter = new Biquad(sampleRate);
        preMax = sampleRate + 4; // max 1s DelayNode in mixGraph.ts
        preBufL = new float[preMax];
        preBufR = new float[preMax];
        convolver = new ConvolverPair([0f], [0f]);
        config = BuildConfig(ReverbParams.Defaults);
        Set(ReverbParams.Defaults);
    }

    static double F(double v) => (float)v;

    ReverbConfig BuildConfig(ReverbParams r)
    {
        var lowCut = Biquad.Compute(fs, new BiquadSpec(BiquadType.Highpass, F(Math.Clamp(r.LowCut, 20, 2000)), F(0.707), 0));
        double delaySec = F(Math.Clamp(r.PreDelayMs, 0, 500) / 1000.0);
        double preSamples = Math.Clamp(delaySec * fs, 0, preMax - 2);
        float ret = r.Muted ? 0f : (float)Math.Clamp(r.ReturnGain, 0, 2);
        return new ReverbConfig(lowCut, preSamples, ret);
    }

    static ReverbKey KeyOf(ReverbParams r) => new(
        (int)Math.Round(r.Decay * 1000),
        (int)Math.Round(r.Tone * 1000),
        (int)Math.Round(r.Width * 1000));

    public void Set(ReverbParams r)
    {
        config = BuildConfig(r);
        var key = KeyOf(r);
        if (key != currentKey)
        {
            currentKey = key;
            Interlocked.Increment(ref irVersion);
            var (irL, irR) = MakeImpulse(fs, r);
            convolver = new ConvolverPair(irL, irR);
        }
    }

    /// <summary>
    /// Updates the reverb bus from the UI thread during live playback: swaps scalar/filter parameters
    /// immediately and generates any new impulse response (up to 5 s for Catedral) on a background
    /// thread before atomically replacing the convolver reference.
    /// </summary>
    public void SetLive(ReverbParams r)
    {
        config = BuildConfig(r);
        var key = KeyOf(r);
        if (key != currentKey)
        {
            currentKey = key;
            int version = Interlocked.Increment(ref irVersion);
            int sampleRate = fs;
            Task.Run(() =>
            {
                var (irL, irR) = MakeImpulse(sampleRate, r);
                var pair = new ConvolverPair(irL, irR);
                if (Volatile.Read(ref irVersion) == version)
                    convolver = pair;
            });
        }
    }

    public void Reset()
    {
        lowCutFilter.Reset();
        Array.Clear(preBufL);
        Array.Clear(preBufR);
        preWrite = 0;
        busTailRemaining = 0;
        convolver.Reset();
    }

    static bool HasAnySignal(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        for (int i = 0; i < l.Length; i++)
            if (l[i] != 0f || r[i] != 0f) return true;
        return false;
    }

    /// <summary>
    /// Processes the summed reverb send <paramref name="left"/> / <paramref name="right"/> in place
    /// into the stereo reverb return (ready to add into the master bus).
    /// </summary>
    public void Process(Span<float> left, Span<float> right)
    {
        if (HasAnySignal(left, right))
        {
            busTailRemaining = fs * 8;
        }
        else if (busTailRemaining <= 0)
        {
            return;
        }
        else
        {
            busTailRemaining -= left.Length;
        }

        var cfg = config;
        lowCutFilter.Process(left, right, cfg.LowCut);

        double d = cfg.PreDelaySamples;
        int dInt = (int)d;
        double frac = d - dInt;
        for (int i = 0; i < left.Length; i++)
        {
            preBufL[preWrite] = left[i];
            preBufR[preWrite] = right[i];

            int r0 = preWrite - dInt;
            if (r0 < 0) r0 += preMax;
            if (frac <= 1e-7)
            {
                left[i] = preBufL[r0];
                right[i] = preBufR[r0];
            }
            else
            {
                int r1 = r0 - 1;
                if (r1 < 0) r1 += preMax;
                left[i] = (float)(preBufL[r0] * (1.0 - frac) + preBufL[r1] * frac);
                right[i] = (float)(preBufR[r0] * (1.0 - frac) + preBufR[r1] * frac);
            }

            if (++preWrite == preMax) preWrite = 0;
        }

        convolver.Process(left, right);

        float g = cfg.ReturnGain;
        for (int i = 0; i < left.Length; i++)
        {
            left[i] *= g;
            right[i] *= g;
        }
    }

    sealed class ConvolverPair(float[] irL, float[] irR)
    {
        readonly PartitionedConvolver left = new(irL);
        readonly PartitionedConvolver right = new(irR);

        public void Reset()
        {
            left.Reset();
            right.Reset();
        }

        public void Process(Span<float> l, Span<float> r)
        {
            left.Process(l);
            right.Process(r);
        }
    }

    /// <summary>
    /// Zero-latency non-uniform partitioned overlap-add convolver:
    /// - Head [0..63]: 64-tap direct time-domain product (sample-by-sample, invariant to any buffer size:
    ///   64, 96, 100, 128, 144, 192, 441, 480, 1000…).
    /// - Mid [64..1023]: 15 partitions of 64 samples (128-point FFT, triggered every 64 samples).
    /// - Long [1024..end]: partitions of 1024 samples (2048-point FFT, triggered every 1024 samples).
    /// </summary>
    sealed class PartitionedConvolver
    {
        const int HeadLen = 64;
        const int MidBlock = 64;
        const int LongBlock = 1024;
        const int ScratchSize = 2048;

        readonly float[] head = new float[HeadLen];
        readonly float[] hist = new float[HeadLen * 2];
        readonly float[] tailScratch = new float[ScratchSize];
        readonly FftStage midStage;
        readonly FftStage longStage;
        readonly int maxSilenceTail;
        int histIdx;
        int tailRemaining;

        public PartitionedConvolver(float[] ir)
        {
            Array.Copy(ir, 0, head, 0, Math.Min(HeadLen, ir.Length));
            midStage = new FftStage(ir, startTap: HeadLen, maxTaps: LongBlock - HeadLen, blockSize: MidBlock);
            longStage = new FftStage(ir, startTap: LongBlock, maxTaps: Math.Max(0, ir.Length - LongBlock), blockSize: LongBlock);
            maxSilenceTail = ir.Length + LongBlock * 2 + HeadLen;
        }

        public void Reset()
        {
            Array.Clear(hist);
            histIdx = 0;
            tailRemaining = 0;
            midStage.Reset();
            longStage.Reset();
        }

        static bool HasNonZero(ReadOnlySpan<float> span)
        {
            for (int i = 0; i < span.Length; i++)
                if (span[i] != 0f) return true;
            return false;
        }

        public void Process(Span<float> buf)
        {
            if (HasNonZero(buf))
            {
                tailRemaining = maxSilenceTail;
            }
            else if (tailRemaining <= 0)
            {
                return;
            }
            else
            {
                tailRemaining -= buf.Length;
            }

            float[] hd = head;
            float[] hs = hist;
            int idx = histIdx;
            int step = Vector<float>.Count;
            var vHd0 = Unsafe.As<float, Vector<float>>(ref hd[0]);
            var vHd1 = Unsafe.As<float, Vector<float>>(ref hd[step]);
            var vHd2 = Unsafe.As<float, Vector<float>>(ref hd[step * 2]);
            var vHd3 = Unsafe.As<float, Vector<float>>(ref hd[step * 3]);
            var vHd4 = step == 8 ? Unsafe.As<float, Vector<float>>(ref hd[32]) : Vector<float>.Zero;
            var vHd5 = step == 8 ? Unsafe.As<float, Vector<float>>(ref hd[40]) : Vector<float>.Zero;
            var vHd6 = step == 8 ? Unsafe.As<float, Vector<float>>(ref hd[48]) : Vector<float>.Zero;
            var vHd7 = step == 8 ? Unsafe.As<float, Vector<float>>(ref hd[56]) : Vector<float>.Zero;

            int offset = 0;
            while (offset < buf.Length)
            {
                int n = Math.Min(ScratchSize, buf.Length - offset);
                var slice = buf.Slice(offset, n);
                var tail = tailScratch.AsSpan(0, n);
                tail.Clear();

                midStage.AddCausal(slice, tail);
                longStage.AddCausal(slice, tail);

                if (step == 8)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float x = slice[i];
                        hs[idx] = x;
                        hs[idx + HeadLen] = x;

                        var vSum = Unsafe.As<float, Vector<float>>(ref hs[idx]) * vHd0
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 8]) * vHd1
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 16]) * vHd2
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 24]) * vHd3
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 32]) * vHd4
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 40]) * vHd5
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 48]) * vHd6
                                 + Unsafe.As<float, Vector<float>>(ref hs[idx + 56]) * vHd7;

                        if (--idx < 0) idx = HeadLen - 1;
                        slice[i] = tail[i] + Vector.Sum(vSum);
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        float x = slice[i];
                        hs[idx] = x;
                        hs[idx + HeadLen] = x;

                        var vSum = Vector<float>.Zero;
                        for (int k = 0; k < HeadLen; k += step)
                            vSum += Unsafe.As<float, Vector<float>>(ref hs[idx + k]) * Unsafe.As<float, Vector<float>>(ref hd[k]);

                        if (--idx < 0) idx = HeadLen - 1;
                        slice[i] = tail[i] + Vector.Sum(vSum);
                    }
                }
                offset += n;
            }
            histIdx = idx;
        }
    }

    sealed class FftStage
    {
        readonly int B;
        readonly int N;
        readonly int vecLen;
        readonly int parts;
        readonly Vector<float>[][] irRe;
        readonly Vector<float>[][] irIm;
        readonly Vector<float>[][] fdlRe;
        readonly Vector<float>[][] fdlIm;
        readonly bool[] fdlActive;
        readonly float[] blockIn;
        readonly float[] fftRe;
        readonly float[] fftIm;
        readonly float[] sumRe;
        readonly float[] sumIm;
        readonly Vector<float>[] sumReVec;
        readonly Vector<float>[] sumImVec;
        readonly float[] tailOut;
        readonly float[] overlap;
        readonly float[] twiddleRe;
        readonly float[] twiddleIm;

        int blockPos;
        int fdlIdx;

        public FftStage(float[] ir, int startTap, int maxTaps, int blockSize)
        {
            B = blockSize;
            N = blockSize * 2;
            vecLen = N / Vector<float>.Count;
            int avail = Math.Max(0, Math.Min(maxTaps, ir.Length - startTap));
            parts = (avail + B - 1) / B;

            irRe = new Vector<float>[parts][];
            irIm = new Vector<float>[parts][];
            fdlRe = new Vector<float>[parts][];
            fdlIm = new Vector<float>[parts][];
            fdlActive = new bool[parts];
            blockIn = new float[B];
            fftRe = new float[N];
            fftIm = new float[N];
            sumRe = new float[N];
            sumIm = new float[N];
            sumReVec = new Vector<float>[vecLen];
            sumImVec = new Vector<float>[vecLen];
            tailOut = new float[B];
            overlap = new float[B];

            twiddleRe = new float[N];
            twiddleIm = new float[N];
            for (int len = 2; len <= N; len <<= 1)
            {
                int half = len >> 1;
                double step = -2.0 * Math.PI / len;
                for (int j = 0; j < half; j++)
                {
                    twiddleRe[half + j] = (float)Math.Cos(step * j);
                    twiddleIm[half + j] = (float)Math.Sin(step * j);
                }
            }

            for (int p = 0; p < parts; p++)
            {
                var re = new float[N];
                var im = new float[N];
                int offset = startTap + p * B;
                int count = Math.Min(B, startTap + avail - offset);
                for (int i = 0; i < count; i++) re[i] = ir[offset + i];
                Fft(re, im, invert: false);
                irRe[p] = MemoryMarshal.Cast<float, Vector<float>>(re.AsSpan()).ToArray();
                irIm[p] = MemoryMarshal.Cast<float, Vector<float>>(im.AsSpan()).ToArray();
                fdlRe[p] = new Vector<float>[vecLen];
                fdlIm[p] = new Vector<float>[vecLen];
            }
        }

        public void Reset()
        {
            Array.Clear(blockIn);
            Array.Clear(tailOut);
            Array.Clear(overlap);
            Array.Clear(fdlActive);
            for (int p = 0; p < parts; p++)
            {
                Array.Clear(fdlRe[p]);
                Array.Clear(fdlIm[p]);
            }
            blockPos = 0;
            fdlIdx = 0;
        }

        public void AddCausal(ReadOnlySpan<float> input, Span<float> output)
        {
            if (parts == 0) return;
            float[] tOut = tailOut;
            float[] bIn = blockIn;
            int idx = 0;
            while (idx < input.Length)
            {
                int rem = Math.Min(input.Length - idx, B - blockPos);
                for (int k = 0; k < rem; k++)
                {
                    output[idx + k] += tOut[blockPos + k];
                    bIn[blockPos + k] = input[idx + k];
                }
                blockPos += rem;
                idx += rem;
                if (blockPos == B)
                {
                    blockPos = 0;
                    StepBlock();
                }
            }
        }

        void StepBlock()
        {
            bool nonZero = false;
            float[] bIn = blockIn;
            for (int i = 0; i < B; i++)
            {
                if (bIn[i] != 0f) { nonZero = true; break; }
            }

            if (nonZero)
            {
                Array.Copy(bIn, 0, fftRe, 0, B);
                Array.Clear(fftRe, B, B);
                Array.Clear(fftIm, 0, N);
                Fft(fftRe, fftIm, invert: false);
                MemoryMarshal.Cast<float, Vector<float>>(fftRe.AsSpan()).CopyTo(fdlRe[fdlIdx]);
                MemoryMarshal.Cast<float, Vector<float>>(fftIm.AsSpan()).CopyTo(fdlIm[fdlIdx]);
                fdlActive[fdlIdx] = true;
            }
            else
            {
                fdlActive[fdlIdx] = false;
            }

            Vector<float>[] vSumRe = sumReVec;
            Vector<float>[] vSumIm = sumImVec;
            Array.Clear(vSumRe, 0, vecLen);
            Array.Clear(vSumIm, 0, vecLen);

            bool anyActive = false;
            int idx = fdlIdx;
            int vLen = vecLen;
            for (int p = 0; p < parts; p++)
            {
                if (fdlActive[idx])
                {
                    anyActive = true;
                    Vector<float>[] xRe = fdlRe[idx];
                    Vector<float>[] xIm = fdlIm[idx];
                    Vector<float>[] hRe = irRe[p];
                    Vector<float>[] hIm = irIm[p];
                    for (int k = 0; k < vLen; k++)
                    {
                        var xr = xRe[k];
                        var xi = xIm[k];
                        var hr = hRe[k];
                        var hi = hIm[k];
                        vSumRe[k] += xr * hr - xi * hi;
                        vSumIm[k] += xr * hi + xi * hr;
                    }
                }
                if (--idx < 0) idx = parts - 1;
            }

            if (++fdlIdx == parts) fdlIdx = 0;

            float[] tOut = tailOut;
            float[] ov = overlap;
            if (anyActive)
            {
                float[] sRe = sumRe;
                float[] sIm = sumIm;
                MemoryMarshal.AsBytes(vSumRe.AsSpan()).CopyTo(MemoryMarshal.AsBytes(sRe.AsSpan()));
                MemoryMarshal.AsBytes(vSumIm.AsSpan()).CopyTo(MemoryMarshal.AsBytes(sIm.AsSpan()));
                Fft(sRe, sIm, invert: true);
                for (int k = 0; k < B; k++)
                {
                    tOut[k] = sRe[k] + ov[k];
                    ov[k] = sRe[B + k];
                }
            }
            else
            {
                for (int k = 0; k < B; k++)
                {
                    tOut[k] = ov[k];
                    ov[k] = 0f;
                }
            }
        }

        void Fft(float[] re, float[] im, bool invert)
        {
            for (int i = 1, j = 0; i < N; i++)
            {
                int bit = N >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            float sign = invert ? -1f : 1f;
            for (int len = 2; len <= N; len <<= 1)
            {
                int half = len >> 1;
                for (int i = 0; i < N; i += len)
                {
                    for (int j = 0; j < half; j++)
                    {
                        float wRe = twiddleRe[half + j];
                        float wIm = twiddleIm[half + j] * sign;
                        int u = i + j, v = u + half;
                        float vr = re[v] * wRe - im[v] * wIm;
                        float vi = re[v] * wIm + im[v] * wRe;
                        re[v] = re[u] - vr;
                        im[v] = im[u] - vi;
                        re[u] += vr;
                        im[u] += vi;
                    }
                }
            }
            if (invert)
            {
                float invN = 1f / N;
                for (int i = 0; i < N; i++)
                {
                    re[i] *= invN;
                    im[i] *= invN;
                }
            }
        }
    }
}
