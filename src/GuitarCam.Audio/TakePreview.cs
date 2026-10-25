using NAudio.Wave;

namespace GuitarCam.Audio;

/// <summary>
/// "Escuchar el canal" in the Biblioteca: plays a take's raw file (no effects) through Windows' default output, like
/// the web's &lt;audio&gt; element, so it never touches the ASIO engine or the timeline. One at a time; Stopped fires
/// when it ends or fails on its own. The library's mini player pauses, resumes and seeks it: a seek is applied on the
/// output's own thread with a short fade out and in (<see cref="Fader"/>), so jumping around doesn't click.
/// </summary>
public sealed class TakePreview : IDisposable
{
    /// <summary>Output buffering: shorter than WaveOutEvent's 300 ms so a seek is heard (and shown) about when it's asked.</summary>
    const int LatencyMs = 150;

    WaveOutEvent? output;
    AudioFileReader? reader;
    Fader? fader;
    double baseSec;
    long basePlayed;

    public string? PlayingId { get; private set; }
    public bool Paused => output?.PlaybackState == PlaybackState.Paused;
    public double DurationSec => reader?.TotalTime.TotalSeconds ?? 0;

    /// <summary>What is being heard, from the bytes the output already played since the last seek.</summary>
    public double PositionSec
    {
        get
        {
            if (output is not { } o) return 0;
            double played = (o.GetPosition() - basePlayed) / (double)o.OutputWaveFormat.AverageBytesPerSecond;
            return Math.Clamp(baseSec + played, 0, DurationSec);
        }
    }

    public event Action? Stopped;

    /// <summary>Starts the file, stopping whatever was playing. Throws if the file can't be opened.</summary>
    public void Play(string id, string path)
    {
        Stop();
        var r = new AudioFileReader(path);
        var f = new Fader(r);
        var o = new WaveOutEvent { DesiredLatency = LatencyMs };
        try { o.Init(f); }
        catch
        {
            o.Dispose();
            r.Dispose();
            throw;
        }
        // raised on the UI thread when it was created there (WaveOutEvent posts to the SynchronizationContext)
        o.PlaybackStopped += (_, _) =>
        {
            if (output != o) return; // stopped by Stop()
            (output, reader, fader, PlayingId) = (null, null, null, null);
            o.Dispose();
            r.Dispose();
            Stopped?.Invoke();
        };
        (output, reader, fader, PlayingId) = (o, r, f, id);
        (baseSec, basePlayed) = (0, 0);
        o.Play();
    }

    public void Pause() => output?.Pause();

    public void Resume() => output?.Play();

    /// <summary>Jumps to a time; the position shows it at once, the audio follows after the output's buffer.</summary>
    public void Seek(double sec)
    {
        if (output is not { } o || fader is not { } f) return;
        sec = Math.Clamp(sec, 0, Math.Max(0, DurationSec - 0.05));
        f.Seek(sec);
        (baseSec, basePlayed) = (sec, o.GetPosition());
    }

    /// <summary>Stops without raising <see cref="Stopped"/>: the caller already knows.</summary>
    public void Stop()
    {
        if (output is not { } o) return;
        var r = reader;
        (output, reader, fader, PlayingId) = (null, null, null, null);
        o.Stop();
        o.Dispose();
        r?.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The loudest sample of each of <paramref name="bins"/> equal slices of the file (0..1, channels together), for the
    /// mini player's wave. Reads the whole file: call it off the UI thread.
    /// </summary>
    public static float[] Peaks(string path, int bins)
    {
        using var r = new AudioFileReader(path);
        int ch = r.WaveFormat.Channels;
        long frames = r.Length / r.WaveFormat.BlockAlign;
        var peaks = new float[bins];
        if (frames == 0) return peaks;
        var buf = new float[16384 * ch];
        long frame = 0;
        int n;
        while ((n = r.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                int b = (int)Math.Min(bins - 1, (frame + i / ch) * bins / frames);
                float v = Math.Abs(buf[i]);
                if (v > peaks[b]) peaks[b] = v;
            }
            frame += n / ch;
        }
        return peaks;
    }

    /// <summary>
    /// Sits between the file and the output and does the seeks on the output's thread (the reader isn't thread safe):
    /// the buffer that carries a seek fades the old audio out over <see cref="FadeMs"/>, jumps and fades the new one in.
    /// </summary>
    sealed class Fader(AudioFileReader reader) : ISampleProvider
    {
        const double FadeMs = 6;
        readonly int fadeFrames = Math.Max(1, (int)(reader.WaveFormat.SampleRate * FadeMs / 1000));
        long pendingTicks = -1;
        int fadeInLeft;

        public WaveFormat WaveFormat => reader.WaveFormat;

        public void Seek(double sec) => Interlocked.Exchange(ref pendingTicks, TimeSpan.FromSeconds(sec).Ticks);

        public int Read(float[] buffer, int offset, int count)
        {
            int ch = reader.WaveFormat.Channels;
            long seek = Interlocked.Exchange(ref pendingTicks, -1);
            if (seek < 0) return FadeIn(buffer, offset, reader.Read(buffer, offset, count), ch);
            int outN = Math.Min(count, fadeFrames * ch);
            int got = reader.Read(buffer, offset, outN);
            int gotFrames = got / ch;
            for (int i = 0; i < got; i++) buffer[offset + i] *= 1 - (float)(i / ch) / Math.Max(1, gotFrames);
            reader.CurrentTime = TimeSpan.FromTicks(seek);
            fadeInLeft = fadeFrames;
            return got + FadeIn(buffer, offset + got, reader.Read(buffer, offset + got, count - got), ch);
        }

        int FadeIn(float[] buffer, int offset, int n, int ch)
        {
            for (int i = 0; i < n && fadeInLeft > 0; i += ch, fadeInLeft--)
            {
                float g = 1 - (float)fadeInLeft / fadeFrames;
                for (int c = 0; c < ch && i + c < n; c++) buffer[offset + i + c] *= g;
            }
            return n;
        }
    }
}
