namespace WildTrack.Audio;

/// <summary>
/// Where the preview's video frames come from: a file decoded to BGRA at a fixed size, read forwards from a seek
/// point. Used from one thread at a time (the preview decoder's own thread), so implementations need no locks.
/// </summary>
public interface IVideoFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Repositions so the next <see cref="Read"/> gives the frame shown at <paramref name="sec"/> (video time).</summary>
    void Seek(double sec);

    /// <summary>
    /// Reads the next frame into <paramref name="bgra"/> (Width × Height × 4 bytes) and its start time. Frames that
    /// end before <paramref name="notBefore"/> are dropped without converting them. False at the end of the file or on an error.
    /// </summary>
    bool Read(byte[] bgra, double notBefore, out double sec);
}
