using NAudio.Wave;

namespace GuitarCam.Audio;

/// <summary>
/// "Escuchar el canal" in Historial: plays a take's raw file (no effects) through Windows' default output, like the
/// web's &lt;audio&gt; element, so it never touches the ASIO engine or the timeline. One at a time; Stopped fires when it
/// ends or fails on its own.
/// </summary>
public sealed class TakePreview : IDisposable
{
    WaveOutEvent? output;
    AudioFileReader? reader;

    public string? PlayingId { get; private set; }

    public event Action? Stopped;

    /// <summary>Starts the file, stopping whatever was playing. Throws if the file can't be opened.</summary>
    public void Play(string id, string path)
    {
        Stop();
        var r = new AudioFileReader(path);
        var o = new WaveOutEvent();
        try { o.Init(r); }
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
            (output, reader, PlayingId) = (null, null, null);
            o.Dispose();
            r.Dispose();
            Stopped?.Invoke();
        };
        (output, reader, PlayingId) = (o, r, id);
        o.Play();
    }

    /// <summary>Stops without raising <see cref="Stopped"/>: the caller already knows.</summary>
    public void Stop()
    {
        if (output is not { } o) return;
        var r = reader;
        (output, reader, PlayingId) = (null, null, null);
        o.Stop();
        o.Dispose();
        r?.Dispose();
    }

    public void Dispose() => Stop();
}
