using NAudio.Wave;

namespace WildTrack.Lab;

static class Wav
{
    /// <summary>Planar stereo (a mono file is duplicated).</summary>
    public static (float[][] Channels, int SampleRate) Read(string path)
    {
        using var r = new AudioFileReader(path);
        int ch = r.WaveFormat.Channels;
        var all = new List<float>();
        var buf = new float[ch * 4096];
        int n;
        while ((n = r.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.AsSpan(0, n));
        int len = all.Count / ch;
        var L = new float[len];
        var R = new float[len];
        for (int i = 0; i < len; i++)
        {
            L[i] = all[i * ch];
            R[i] = ch > 1 ? all[i * ch + 1] : L[i];
        }
        return ([L, R], r.WaveFormat.SampleRate);
    }

    /// <summary>32-bit float stereo.</summary>
    public static void Write(string path, float[] left, float[] right, int sampleRate, int length = -1)
    {
        if (length < 0) length = left.Length;
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        var buf = new float[8192];
        for (int i = 0; i < length;)
        {
            int n = Math.Min(buf.Length / 2, length - i);
            for (int j = 0; j < n; j++)
            {
                buf[2 * j] = left[i + j];
                buf[2 * j + 1] = right[i + j];
            }
            w.WriteSamples(buf, 0, n * 2);
            i += n;
        }
    }
}
