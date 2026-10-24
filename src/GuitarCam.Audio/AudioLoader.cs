using NAudio.Wave;

namespace GuitarCam.Audio;

/// <summary>
/// Loads audio files into stereo float32 planar buffers at the engine's sample rate (48 kHz by default),
/// matching Web Audio's <c>decodeAudioData</c> behavior in <c>GuitarCam/src/audio/mixGraph.ts</c>.
/// Used by both live playback (<c>MediaAudio</c> / <c>LiveTrack</c>) and offline rendering (<c>gclab mezclar</c>
/// and export).
/// <para>
/// Known difference vs. the web app: when a source file is not at <c>targetSampleRate</c> (e.g. 44.1 kHz
/// imported into a 48 kHz project), <see cref="Resample"/> uses a 32-tap Blackman-windowed sinc filter
/// in C#, which preserves duration and pitch but does not match Chromium's internal <c>SincResampler</c>
/// sample for sample. Files recorded at <c>targetSampleRate</c> (48 kHz) pass through untouched.
/// </para>
/// </summary>
public static class AudioLoader
{
    public const int DefaultSampleRate = 48000;

    public readonly record struct LoadedAudio(float[] Left, float[] Right, int SampleRate, int SourceSampleRate)
    {
        public double DurationSec => SampleRate > 0 ? Left.Length / (double)SampleRate : 0;
    }

    /// <summary>
    /// Reads an audio file (WAV, MP3, etc.) into planar stereo float32 arrays and resamples to
    /// <paramref name="targetSampleRate"/> if the file's sample rate differs.
    /// </summary>
    public static LoadedAudio Load(string path, int targetSampleRate = DefaultSampleRate)
    {
        float[] l;
        float[] r;
        int srcRate;
        using (var reader = new AudioFileReader(path))
        {
            srcRate = reader.WaveFormat.SampleRate;
            int ch = reader.WaveFormat.Channels;
            // The reader's Length is only a hint: Media Foundation (FLAC, M4A…) can report less than the file
            // holds, so read until the decoder runs dry and grow the buffer when the hint was short.
            var all = new float[Math.Max(65536, (int)(reader.Length / 4))];
            int read = 0, n;
            while (true)
            {
                if (read == all.Length) Array.Resize(ref all, all.Length * 2);
                if ((n = reader.Read(all, read, Math.Min(65536, all.Length - read))) <= 0) break;
                read += n;
            }
            int len = read / ch;
            l = new float[len];
            r = new float[len];
            for (int i = 0; i < len; i++)
            {
                l[i] = all[i * ch];
                r[i] = ch > 1 ? all[i * ch + 1] : l[i];
            }
        }

        if (targetSampleRate > 0 && srcRate != targetSampleRate)
        {
            (l, r) = Resample(l, r, srcRate, targetSampleRate);
            return new LoadedAudio(l, r, targetSampleRate, srcRate);
        }

        return new LoadedAudio(l, r, srcRate, srcRate);
    }

    /// <summary>
    /// The real length, sample rate and channel count of a file, found by decoding it to the end without keeping
    /// anything: <c>TotalTime</c> is wrong for some formats (see <see cref="Load"/>).
    /// </summary>
    public static (double DurationSec, int SampleRate, int Channels) Measure(string path)
    {
        using var reader = new AudioFileReader(path);
        var buf = new float[65536];
        long samples = 0;
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0) samples += n;
        int ch = reader.WaveFormat.Channels, rate = reader.WaveFormat.SampleRate;
        return (samples / (double)ch / rate, rate, ch);
    }

    /// <summary>
    /// Resamples stereo planar buffers from <paramref name="srcRate"/> to <paramref name="dstRate"/>
    /// using a 32-tap Blackman-windowed sinc filter.
    /// </summary>
    public static (float[] Left, float[] Right) Resample(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        int srcRate,
        int dstRate)
    {
        if (srcRate == dstRate || left.Length == 0)
            return (left.ToArray(), right.ToArray());

        int srcLen = left.Length;
        int dstLen = Math.Max(1, (int)Math.Round((long)srcLen * dstRate / (double)srcRate));
        var outL = new float[dstLen];
        var outR = new float[dstLen];

        const int halfTaps = 16;
        double ratio = srcRate / (double)dstRate;
        double cutoff = Math.Min(1.0, dstRate / (double)srcRate) * 0.92;

        for (int i = 0; i < dstLen; i++)
        {
            double srcPos = i * ratio;
            int center = (int)Math.Floor(srcPos);
            double frac = srcPos - center;

            double sumL = 0;
            double sumR = 0;
            double weightSum = 0;

            for (int k = -halfTaps + 1; k <= halfTaps; k++)
            {
                int idx = center + k;
                double x = k - frac;
                double wPos = (x + halfTaps) / (2.0 * halfTaps);
                double win = 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * wPos) + 0.08 * Math.Cos(4.0 * Math.PI * wPos);
                double cx = x * cutoff;
                double sinc = Math.Abs(cx) < 1e-8 ? 1.0 : Math.Sin(Math.PI * cx) / (Math.PI * cx);
                double coeff = sinc * win * cutoff;
                weightSum += coeff;
                if ((uint)idx < (uint)srcLen)
                {
                    sumL += left[idx] * coeff;
                    sumR += right[idx] * coeff;
                }
            }

            double invW = weightSum > 1e-9 ? 1.0 / weightSum : 1.0;
            outL[i] = (float)(sumL * invW);
            outR[i] = (float)(sumR * invW);
        }

        return (outL, outR);
    }
}
