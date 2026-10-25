namespace GuitarCam.Audio.Dsp;

/// <summary>The finished mix of an export: the audio, how long it is, how much of it is effect tail and its loudness.</summary>
public sealed record MasteredMix(float[] Left, float[] Right, double DurationSec, double TailSec, double Lufs, double GainDb);

/// <summary>
/// The last step of an export's audio (native only). Renders the mix through <see cref="MixRenderer"/> without the
/// master limiter and past the last clip, so the reverb and delay can ring out (up to <see cref="MaxTailSec"/>, cut
/// where they fall under <see cref="TailFloorDb"/>). Then it runs the same <see cref="MasterLimiter"/> playback uses,
/// with a gain in front that brings the integrated loudness (<see cref="LoudnessMeter.Measure"/>, the master's LUFS
/// cell) to the target of a social preset. The limiter squashes a bit of what the gain adds, so it measures again and
/// corrects a few times; each pass is only the limiter, not the whole mix. Without a target the gain is the limiter's Empuje (0 dB by default) and the
/// result is what <see cref="MixRenderer.Render"/> gives with the limiter on.
/// </summary>
public static class MixMaster
{
    /// <summary>The longest an effect tail can ring after the last clip; a delay with high feedback would go on forever.</summary>
    public const double MaxTailSec = 8;
    /// <summary>Under this peak level a tail counts as finished: inaudible even when the loudness gain pushes it up.</summary>
    public const double TailFloorDb = -66;
    /// <summary>Kept after the last audible sample of the tail, so the very end is not shaved.</summary>
    const double TailPadSec = 0.1;
    /// <summary>A tail still sounding at the limit fades over this long, so the cut is never heard.</summary>
    const double CutFadeSec = 1.0;
    /// <summary>Every export ends with this short fade, so the last sample never clicks.</summary>
    const double EndFadeSec = 0.01;
    /// <summary>The most the loudness gain moves the mix: a near-silent mix is not blown up into noise.</summary>
    public const double MaxGainDb = 24;
    /// <summary>Close enough to the target: platforms normalise with their own meters anyway.</summary>
    const double LufsTolerance = 0.2;
    const int MaxPasses = 4;
    const int Block = 128;
    /// <summary>Share of the progress the mix takes; the limiter passes are the rest.</summary>
    const double MixShare = 0.9;

    /// <summary>
    /// Renders and masters the mix. <paramref name="contentSec"/> is where the project ends (last clip or take);
    /// <paramref name="maxTailSec"/> = 0 cuts there like before. <paramref name="targetLufs"/> = null keeps the mix's level.
    /// </summary>
    public static MasteredMix Render(
        IReadOnlyList<MixTrackSpec> tracks,
        MixBusSpec bus,
        double contentSec,
        int sampleRate,
        double maxTailSec = MaxTailSec,
        double? targetLufs = null,
        Action<double>? onProgress = null,
        CancellationToken ct = default)
    {
        double tail = Math.Clamp(double.IsFinite(maxTailSec) ? maxTailSec : 0, 0, MaxTailSec);
        var (preL, preR) = MixRenderer.Render(tracks, bus, contentSec + tail, sampleRate,
            onProgress == null ? null : f => onProgress(f * MixShare), ct, limit: false);
        int content = Math.Min(preL.Length, Math.Max(1, (int)Math.Ceiling(contentSec * sampleRate)));
        var (end, cut) = TailEnd(preL, preR, content, sampleRate);

        // the limiter as the master card has it: Empuje is where the gain starts, a loudness target moves it from there;
        // with the limiter off nothing holds the peaks, so the gain stops where the loudest one touches the Techo
        var mp = bus.Master;
        double gain = mp.LimOn ? Math.Clamp(double.IsFinite(mp.LimDriveDb) ? mp.LimDriveDb : 0, 0, MasterParams.DriveMaxDb) : 0;
        double maxGain = mp.LimOn ? MaxGainDb : Math.Min(MaxGainDb, mp.LimCeilingDb - PeakDb(preL, preR, end));
        var (l, r) = Limit(preL, preR, end, gain, sampleRate, cut, mp);
        double lufs = LoudnessMeter.Measure(l, r, sampleRate);
        if (targetLufs is { } t && double.IsFinite(t) && double.IsFinite(lufs))
        {
            for (int pass = 0; pass < MaxPasses && Math.Abs(t - lufs) > LufsTolerance; pass++)
            {
                ct.ThrowIfCancellationRequested();
                double next = Math.Clamp(gain + (t - lufs), -MaxGainDb, maxGain);
                if (Math.Abs(next - gain) < 0.01) break; // at the clamp: it will not get closer
                gain = next;
                (l, r) = Limit(preL, preR, end, gain, sampleRate, cut, mp);
                lufs = LoudnessMeter.Measure(l, r, sampleRate);
                onProgress?.Invoke(MixShare + (1 - MixShare) * (pass + 1) / MaxPasses);
            }
        }
        onProgress?.Invoke(1.0);
        return new MasteredMix(l, r, end / (double)sampleRate, (end - content) / (double)sampleRate, lufs, gain);
    }

    /// <summary>
    /// Where the export ends: the last sample after <paramref name="content"/> above <see cref="TailFloorDb"/>, plus a
    /// little. <c>Cut</c> = the tail was still sounding where the render stops.
    /// </summary>
    public static (int End, bool Cut) TailEnd(float[] left, float[] right, int content, int sampleRate)
    {
        int len = Math.Min(left.Length, right.Length);
        content = Math.Clamp(content, 1, Math.Max(1, len));
        float floor = (float)Math.Pow(10, TailFloorDb / 20);
        int last = -1;
        for (int i = len - 1; i >= content; i--)
            if (Math.Abs(left[i]) > floor || Math.Abs(right[i]) > floor) { last = i; break; }
        if (last < 0) return (content, false);
        int pad = (int)(TailPadSec * sampleRate);
        return last + 1 + pad >= len ? (len, true) : (last + 1 + pad, false);
    }

    static double PeakDb(float[] l, float[] r, int frames)
    {
        float pk = 0;
        for (int i = 0; i < Math.Min(frames, Math.Min(l.Length, r.Length)); i++) pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        return pk > 0 ? 20 * Math.Log10(pk) : -120;
    }

    /// <summary>
    /// The master limiter over the first <paramref name="frames"/> of the pre-limiter mix, with <paramref name="gainDb"/>
    /// in front; strips the look-ahead like <see cref="MixRenderer.Render"/> and fades the end.
    /// </summary>
    static (float[] Left, float[] Right) Limit(float[] preL, float[] preR, int frames, double gainDb, int sampleRate, bool cut, MasterParams mp)
    {
        var lim = new MasterLimiter(sampleRate, MasterLimiter.LookaheadSamples(sampleRate), MasterLimiter.CeilingDb);
        lim.Set(mp.LimCeilingDb, mp.LimReleaseMs);
        lim.Bypass = !mp.LimOn;
        var clip = new SoftClipper();
        clip.Set(mp.On && mp.ClipOn ? mp.ClipDb : 0, mp.LimCeilingDb);
        int latency = lim.Latency;
        float g = (float)Math.Pow(10, gainDb / 20);
        var outL = new float[frames];
        var outR = new float[frames];
        var bl = new float[Block];
        var br = new float[Block];
        int total = frames + latency;
        for (int pos = 0; pos < total; pos += Block)
        {
            int n = Math.Min(Block, total - pos);
            for (int i = 0; i < n; i++)
            {
                int src = pos + i;
                // past the end it keeps feeding what was rendered, like the live limiter would hear it
                bl[i] = src < preL.Length ? preL[src] * g : 0;
                br[i] = src < preR.Length ? preR[src] * g : 0;
            }
            clip.Process(bl.AsSpan(0, n), br.AsSpan(0, n));
            lim.Process(bl.AsSpan(0, n), br.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                int dst = pos + i - latency;
                if (dst < 0 || dst >= frames) continue;
                outL[dst] = bl[i];
                outR[dst] = br[i];
            }
        }
        int fade = Math.Min(frames, (int)((cut ? CutFadeSec : EndFadeSec) * sampleRate));
        for (int i = 0; i < fade; i++)
        {
            float k = (float)i / fade;
            int at = frames - 1 - i;
            outL[at] *= k;
            outR[at] *= k;
        }
        return (outL, outR);
    }
}
