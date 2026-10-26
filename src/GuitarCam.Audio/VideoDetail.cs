using System.Globalization;
using System.Numerics;

namespace GuitarCam.Audio;

/// <summary>
/// Sharpness ("Nitidez") and clarity ("Claridad"): the two spatial filters of the video effects, built so the preview
/// (<see cref="Processor"/>, on the decoded BGRA frame) and the MP4 export (<see cref="ExportFilters"/>, FFmpeg's
/// <c>boxblur</c> + <c>lut2</c> and <c>unsharp</c>) give the same picture:
/// <list type="bullet">
/// <item><description>Both work on the luma only (the same change added to R, G and B), like <c>unsharp</c> with no chroma amount.</description></item>
/// <item><description>Clarity: <c>y + k·w(y)·(y − blur(y))</c>, a 3-pass box blur with a wide radius and <c>w</c> peaking in the mid-tones,
/// so it adds local contrast without crushing blacks or burning whites. Sharpness: FFmpeg's <c>unsharp</c> (a binomial blur) on top.</description></item>
/// <item><description>Radii are a fraction of the panel's visible height, not pixels: the preview decodes at 640 wide and the export
/// filters each panel at its final size, so a fixed pixel radius would look different in each.</description></item>
/// </list>
/// </summary>
public static class VideoDetail
{
    /// <summary>Clarity's blur radius (as a Gaussian's sigma) in panel heights: about 22 px on a 1080-high panel.</summary>
    const double ClaritySigma = 1 / 50.0;
    /// <summary>Sharpness' blur radius (sigma) in panel heights: under 2 px on a 1080-high panel, a fine edge.</summary>
    const double SharpSigma = 1 / 600.0;
    /// <summary>Clarity at 100: the local difference added again at full weight in the mid-tones.</summary>
    const double ClarityMax = 1.0;
    /// <summary>Sharpness at 100: <c>unsharp</c>'s luma amount.</summary>
    const double SharpMax = 1.5;
    /// <summary>Box blur passes for clarity: three make it close to a Gaussian.</summary>
    const int BoxPasses = 3;
    /// <summary>FFmpeg's <c>unsharp</c> refuses more than 25 scale bits, (steps x + steps y)·2: 6 steps each way (13×13).</summary>
    const int MaxSteps = 6;

    public static bool HasDetail(ExportVideoFxSpec? fx)
    {
        var n = VideoColorProcessor.NormalizeVideoFx(fx);
        return n.On && (n.Sharpness > 0 || n.Clarity > 0);
    }

    static double ClarityAmount(ExportVideoFxSpec n) => n.On ? n.Clarity / 100 * ClarityMax : 0;
    static double SharpAmount(ExportVideoFxSpec n) => n.On ? n.Sharpness / 100 * SharpMax : 0;

    /// <summary>Box radius whose 3 passes spread like the clarity sigma for a panel <paramref name="visibleH"/> pixels high (variance r(r+1)).</summary>
    public static int ClarityRadius(double visibleH, int w, int h)
    {
        double s = Math.Max(0, visibleH) * ClaritySigma;
        int r = (int)Math.Round((-1 + Math.Sqrt(1 + 4 * s * s)) / 2, MidpointRounding.AwayFromZero);
        // under boxblur's limit (half the smaller side), so the mirrored edges stay inside the line
        return Math.Clamp(r, 1, Math.Max(1, (Math.Min(w, h) - 1) / 2));
    }

    /// <summary><c>unsharp</c>'s steps (matrix size 2·steps+1) whose binomial spreads like the sharpness sigma (variance steps/2).</summary>
    public static int SharpSteps(double visibleH)
    {
        double s = Math.Max(0, visibleH) * SharpSigma;
        return Math.Clamp((int)Math.Round(2 * s * s, MidpointRounding.AwayFromZero), 1, MaxSteps);
    }

    /// <summary>Clarity's weight: 0 at black and white, 1 in the mid-tones (luma 0..1).</summary>
    static double MidWeight(double n) => n <= 0 || n >= 1 ? 0 : 4 * n * (1 - n);

    static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// The export's filters for a panel already cropped to <paramref name="w"/>×<paramref name="h"/>, as graph
    /// pieces: <paramref name="picture"/> is the chain so far (<c>[0:v]trim…,crop…</c>); the result is the chain to keep
    /// appending to, and <paramref name="chain"/> gets the labeled pieces clarity needs (a split, the blur and <c>lut2</c>).
    /// </summary>
    public static string ExportFilters(ExportVideoFxSpec? fx, int w, int h, string picture, List<string> chain, string tag)
    {
        var n = VideoColorProcessor.NormalizeVideoFx(fx);
        double clarity = ClarityAmount(n), sharp = SharpAmount(n);
        if ((clarity <= 0 && sharp <= 0) || w < 3 || h < 3) return picture;
        // full-resolution chroma: lut2 needs both inputs alike, and the preview never subsamples it
        picture += ",format=yuv444p";
        if (clarity > 0)
        {
            int r = ClarityRadius(h, w, h);
            // limited-range luma: x is the pixel, y its blur; same formula as the preview's table
            string nx = "((x-16)/219)";
            string expr = $"clip(floor(x+{F(clarity)}*max(0,4*{nx}*(1-{nx}))*(x-y)+0.5),0,255)";
            chain.Add($"{picture},split[da{tag}][db{tag}]");
            chain.Add($"[db{tag}]boxblur=lr={r}:lp={BoxPasses}:cr=0:cp=0[dc{tag}]");
            chain.Add($"[da{tag}][dc{tag}]lut2=c0='{expr}':c1=x:c2=x[dd{tag}]");
            picture = $"[dd{tag}]null";
        }
        if (sharp > 0)
        {
            int m = 2 * SharpSteps(h) + 1;
            picture += $",unsharp=lx={m}:ly={m}:la={F(sharp)}:cx=3:cy=3:ca=0";
        }
        return picture;
    }

    /// <summary>
    /// The preview's side: applies clarity and sharpness to a BGRA frame in place. Owns its work buffers and keeps
    /// them between frames (one per clip), so a frame allocates nothing once the size settles. Each step runs in bands
    /// on <see cref="BandPool"/> (rows; columns for clarity's vertical blur, walked a row at a time to stay in cache),
    /// with the same integer math as FFmpeg's <c>boxblur</c> and <c>unsharp</c>, so the bands change nothing.
    /// </summary>
    public sealed class Processor
    {
        /// <summary>Rows (or columns) per band.</summary>
        const int Band = 32;

        byte[] frame = [], luma = [], work = [], blur = [];
        uint[] hsum = [], pad = [], acc = [];
        int[] colSums = [];
        readonly byte[] table = new byte[256 * 256];
        double tableAmount = -1;
        // this frame's parameters, read by the bands
        int w, h, radius, steps, amount;
        bool doClarity, doSharp;
        readonly Action<int> lumaRows, clarityCols, mixRows, sharpRows;

        public Processor()
        {
            lumaRows = LumaRows;
            clarityCols = ClarityCols;
            mixRows = MixRows;
            sharpRows = SharpRows;
        }

        /// <summary>The parameters a frame would use, to tell whether it needs filtering again.</summary>
        public static (int Radius, int Steps, double Clarity, double Sharp) Key(ExportVideoFxSpec n, int w, int h, double visibleH) =>
            (ClarityRadius(visibleH, w, h), SharpSteps(visibleH), ClarityAmount(n), SharpAmount(n));

        /// <summary>
        /// Filters <paramref name="bgra"/> (<paramref name="width"/>×<paramref name="height"/>), whose panel shows
        /// <paramref name="visibleH"/> of its rows (what the export's crop keeps).
        /// </summary>
        public void Apply(byte[] bgra, int width, int height, double visibleH, ExportVideoFxSpec n)
        {
            double clarity = ClarityAmount(n), sharp = SharpAmount(n);
            if ((clarity <= 0 && sharp <= 0) || width < 3 || height < 3) return;
            int count = width * height;
            int rowBands = (height + Band - 1) / Band, colBands = (width + Band - 1) / Band;
            if (luma.Length != count)
            {
                luma = new byte[count];
                work = new byte[count];
                blur = new byte[count];
                hsum = new uint[count];
            }
            if (colSums.Length < width) colSums = new int[width];
            // a padded row and a row of sums for each band, so bands never share them
            if (pad.Length < rowBands * (width + 2 * MaxSteps)) pad = new uint[rowBands * (width + 2 * MaxSteps)];
            if (acc.Length < rowBands * width) acc = new uint[rowBands * width];
            if (clarity > 0 && tableAmount != clarity) BuildTable(clarity);
            (frame, w, h) = (bgra, width, height);
            (doClarity, doSharp) = (clarity > 0, sharp > 0);
            radius = ClarityRadius(visibleH, width, height);
            steps = SharpSteps(visibleH);
            amount = (int)(sharp * 65536);

            // luma and clarity's horizontal blur → its vertical blur → clarity's table and sharpness' horizontal sum
            // → sharpness' vertical sum and the change added to the picture
            BandPool.Run(rowBands, lumaRows);
            if (doClarity) BandPool.Run(colBands, clarityCols);
            BandPool.Run(rowBands, mixRows);
            BandPool.Run(rowBands, sharpRows);
            frame = [];
        }

        void LumaRows(int band)
        {
            int y0 = band * Band, y1 = Math.Min(h, y0 + Band);
            var (px, y8) = (frame, luma);
            for (int i = y0 * w, p = i * 4, end = y1 * w; i < end; i++, p += 4)
                // BT.601 luma, as swscale turns RGB into YUV by default
                y8[i] = (byte)((7471 * px[p] + 38470 * px[p + 1] + 19595 * px[p + 2] + 32768) >> 16);
            if (!doClarity) return;
            // horizontal passes: luma → work → blur → work
            var (wk, bl) = (work, blur);
            for (int o = y0 * w, end = y1 * w; o < end; o += w)
            {
                Box(y8, wk, o, w, radius);
                Box(wk, bl, o, w, radius);
                Box(bl, wk, o, w, radius);
            }
        }

        /// <summary>Clarity's vertical passes for a band of columns: work → blur → work → blur.</summary>
        void ClarityCols(int band)
        {
            int x0 = band * Band, x1 = Math.Min(w, x0 + Band);
            BoxV(work, blur, x0, x1);
            BoxV(blur, work, x0, x1);
            BoxV(work, blur, x0, x1);
        }

        /// <summary>Clarity's table (into work), then sharpness' horizontal binomial sum of the result.</summary>
        void MixRows(int band)
        {
            int y0 = band * Band, y1 = Math.Min(h, y0 + Band);
            var (y8, wk, bl, t) = (luma, work, blur, table);
            if (doClarity)
                for (int i = y0 * w, end = y1 * w; i < end; i++) wk[i] = t[(y8[i] << 8) | bl[i]];
            if (!doSharp) return;
            var src = doClarity ? wk : y8;
            int taps = 2 * steps;
            Span<uint> wts = stackalloc uint[2 * MaxSteps + 1];
            Binomial(wts, taps);
            var pd = pad.AsSpan(band * (w + 2 * MaxSteps), w + taps);
            for (int y = y0; y < y1; y++)
            {
                // the row with its edges repeated, then the binomial as weighted shifted copies
                var row = src.AsSpan(y * w, w);
                for (int k = 0; k < steps; k++) { pd[k] = row[0]; pd[w + steps + k] = row[w - 1]; }
                for (int x = 0; x < w; x++) pd[steps + x] = row[x];
                var hs = hsum.AsSpan(y * w, w);
                hs.Clear();
                for (int k = 0; k <= taps; k++) MulAdd(hs, pd.Slice(k, w), wts[k]);
            }
        }

        /// <summary>
        /// Sharpness' vertical sum and FFmpeg's <c>unsharp</c> formula, <c>src + ((src − blur)·amount &gt;&gt; 16)</c>
        /// (the whole 2D sum fits 32 bits unsigned, 255·2^24 at most, why FFmpeg caps the size), then the luma change
        /// added to B, G and R alike.
        /// </summary>
        void SharpRows(int band)
        {
            int y0 = band * Band, y1 = Math.Min(h, y0 + Band);
            var (px, y8) = (frame, luma);
            var src = doClarity ? work : y8;
            int taps = 2 * steps, scalebits = steps * 4, amt = amount;
            uint half = 1u << (scalebits - 1);
            Span<uint> wts = stackalloc uint[2 * MaxSteps + 1];
            Binomial(wts, taps);
            var ac = acc.AsSpan(band * w, w);
            for (int y = y0; y < y1; y++)
            {
                int row = y * w;
                if (doSharp)
                {
                    ac.Clear();
                    for (int k = 0; k <= taps; k++)
                        MulAdd(ac, hsum.AsSpan(Math.Clamp(y - steps + k, 0, h - 1) * w, w), wts[k]);
                }
                for (int x = 0, p = row * 4; x < w; x++, p += 4)
                {
                    int v = src[row + x];
                    if (doSharp)
                    {
                        int b = (int)((ac[x] + half) >> scalebits);
                        v = Clip(v + (((v - b) * amt) >> 16));
                    }
                    int d = v - y8[row + x];
                    if (d == 0) continue;
                    px[p] = Clip(px[p] + d);
                    px[p + 1] = Clip(px[p + 1] + d);
                    px[p + 2] = Clip(px[p + 2] + d);
                }
            }
        }

        static void Binomial(Span<uint> wts, int taps)
        {
            wts[0] = 1;
            for (int k = 1; k <= taps; k++) wts[k] = wts[k - 1] * (uint)(taps - k + 1) / (uint)k;
        }

        static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        /// <summary>The clarity formula for every (pixel, blur) pair, like the export's <c>lut2</c> (full-range luma here).</summary>
        void BuildTable(double k)
        {
            for (int x = 0; x < 256; x++)
            {
                double wgt = k * MidWeight(x / 255.0);
                for (int y = 0; y < 256; y++)
                    table[(x << 8) | y] = Clip((int)Math.Floor(x + wgt * (x - y) + 0.5));
            }
            tableAmount = k;
        }

        /// <summary>One box pass over a row, the way <c>vf_boxblur</c>'s <c>blur8</c> does it (16-bit fixed point, mirrored edges).</summary>
        static void Box(byte[] src, byte[] dst, int o, int len, int r)
        {
            int length = r * 2 + 1;
            int inv = ((1 << 16) + length / 2) / length;
            int sum = src[o + r];
            for (int x = 0; x < r; x++) sum += src[o + x] << 1;
            sum = sum * inv + (1 << 15);
            int i = 0;
            for (; i <= r; i++)
            {
                sum += (src[o + r + i] - src[o + r - i]) * inv;
                dst[o + i] = (byte)(sum >> 16);
            }
            for (; i < len - r; i++)
            {
                sum += (src[o + r + i] - src[o + i - r - 1]) * inv;
                dst[o + i] = (byte)(sum >> 16);
            }
            for (; i < len; i++)
            {
                sum += (src[o + 2 * len - r - i - 1] - src[o + i - r - 1]) * inv;
                dst[o + i] = (byte)(sum >> 16);
            }
        }

        /// <summary><see cref="Box"/> down the columns <paramref name="x0"/>..<paramref name="x1"/>, all of them a row at a time.</summary>
        void BoxV(byte[] src, byte[] dst, int x0, int x1)
        {
            int r = radius, length = r * 2 + 1, n = x1 - x0;
            int inv = ((1 << 16) + length / 2) / length;
            var s0 = colSums.AsSpan(x0, n);
            for (int x = 0; x < n; x++)
            {
                int s = src[r * w + x0 + x];
                for (int k = 0; k < r; k++) s += src[k * w + x0 + x] << 1;
                s0[x] = s * inv + (1 << 15);
            }
            for (int i = 0; i < h; i++)
            {
                int add = i < h - r ? r + i : 2 * h - r - i - 1;
                int sub = i <= r ? r - i : i - r - 1;
                var a = src.AsSpan(add * w + x0, n);
                var b = src.AsSpan(sub * w + x0, n);
                var d = dst.AsSpan(i * w + x0, n);
                for (int x = 0; x < n; x++)
                {
                    int s = s0[x] + (a[x] - b[x]) * inv;
                    s0[x] = s;
                    d[x] = (byte)(s >> 16);
                }
            }
        }

        /// <summary><c>acc += v·k</c>, a vector at a time.</summary>
        static void MulAdd(Span<uint> acc, ReadOnlySpan<uint> v, uint k)
        {
            int x = 0, n = acc.Length;
            if (Vector.IsHardwareAccelerated)
            {
                var kv = new Vector<uint>(k);
                for (; x <= n - Vector<uint>.Count; x += Vector<uint>.Count)
                    (new Vector<uint>(acc.Slice(x)) + new Vector<uint>(v.Slice(x)) * kv).CopyTo(acc.Slice(x));
            }
            for (; x < n; x++) acc[x] += v[x] * k;
        }
    }
}
