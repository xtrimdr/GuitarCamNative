using System.Buffers.Binary;
using System.Diagnostics;

namespace GuitarCam.Audio;

/// <summary>
/// Writes a recording to disk while it happens: 32-bit float stereo WAV, with the header's sizes
/// rewritten every couple of seconds. If the program dies mid-take, the file plays up to the last
/// checkpoint, and <see cref="WavFile.Repair"/> recovers the rest.
/// Not for the audio callback: feed it from a thread that may block on the disk.
/// </summary>
public sealed class RecordingWriter : IDisposable
{
    const int HeaderSize = 44;

    readonly FileStream fs;
    readonly byte[] buf = new byte[8192 * 8];
    readonly Stopwatch sinceCheckpoint = Stopwatch.StartNew();
    long frames;

    public string Path { get; }
    public int SampleRate { get; }
    public long Frames => frames;
    public double CheckpointSec { get; set; } = 2;

    public RecordingWriter(string path, int sampleRate)
    {
        Path = path;
        SampleRate = sampleRate;
        fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
        fs.Write(WavFile.FloatStereoHeader(sampleRate, 0));
        fs.Flush(true);
    }

    public void Append(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        for (int i = 0; i < left.Length;)
        {
            int n = Math.Min(buf.Length / 8, left.Length - i);
            var span = buf.AsSpan(0, n * 8);
            for (int j = 0; j < n; j++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(span[(j * 8)..], left[i + j]);
                BinaryPrimitives.WriteSingleLittleEndian(span[(j * 8 + 4)..], right[i + j]);
            }
            fs.Write(span);
            i += n;
        }
        frames += left.Length;
        if (sinceCheckpoint.Elapsed.TotalSeconds >= CheckpointSec) Checkpoint();
    }

    /// <summary>Data first, then the sizes that cover it, each forced to the disk.</summary>
    public void Checkpoint()
    {
        fs.Flush(true);
        long end = fs.Position;
        fs.Position = 0;
        fs.Write(WavFile.FloatStereoHeader(SampleRate, frames));
        fs.Flush(true);
        fs.Position = end;
        sinceCheckpoint.Restart();
    }

    public void Dispose()
    {
        try
        {
            Checkpoint();
        }
        finally
        {
            fs.Dispose();
        }
    }
}

public static class WavFile
{
    public readonly record struct Info(int SampleRate, int Channels, int BitsPerSample, long DataBytes)
    {
        public double DurationSec => SampleRate > 0 && Channels > 0 && BitsPerSample > 0
            ? DataBytes / (double)(SampleRate * Channels * (BitsPerSample / 8))
            : 0;
    }

    internal static byte[] FloatStereoHeader(int sampleRate, long frames)
    {
        long data = frames * 8;
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, data + 36));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 3); // IEEE float
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), sampleRate * 8);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 8);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 32);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)Math.Min(uint.MaxValue, data));
        return h;
    }

    /// <summary>Reads the format and the data size from the header (any chunk layout).</summary>
    public static Info ReadInfo(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var (fmt, dataPos, dataSize) = Scan(fs);
        return fmt with { DataBytes = Math.Min(dataSize, fs.Length - dataPos) };
    }

    /// <summary>
    /// Fixes the sizes of a WAV whose writer didn't finish: the data runs to the end of the file
    /// (cut to whole frames). Returns the recovered format and size.
    /// </summary>
    public static Info Repair(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var (fmt, dataPos, _) = Scan(fs);
        int block = Math.Max(1, fmt.Channels * fmt.BitsPerSample / 8);
        long data = (fs.Length - dataPos) / block * block;
        fs.SetLength(dataPos + data);
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Min(uint.MaxValue, fs.Length - 8));
        fs.Position = 4;
        fs.Write(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Min(uint.MaxValue, data));
        fs.Position = dataPos - 4;
        fs.Write(b);
        fs.Flush(true);
        return fmt with { DataBytes = data };
    }

    static (Info Fmt, long DataPos, long DataSize) Scan(FileStream fs)
    {
        var head = new byte[12];
        if (fs.Read(head) < 12 || !head.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !head.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("No es un WAV");
        Info? fmt = null;
        var ch = new byte[8];
        while (fs.Read(ch) == 8)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(ch.AsSpan(4));
            if (ch.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                var f = new byte[16];
                fs.ReadExactly(f);
                fmt = new Info(BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(4)), BinaryPrimitives.ReadInt16LittleEndian(f.AsSpan(2)),
                    BinaryPrimitives.ReadInt16LittleEndian(f.AsSpan(14)), 0);
                fs.Position += size - 16 + (size & 1);
            }
            else if (ch.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (fmt == null) throw new InvalidDataException("WAV sin formato antes de los datos");
                return (fmt.Value, fs.Position, size);
            }
            else fs.Position += size + (size & 1);
        }
        throw new InvalidDataException("WAV sin datos");
    }
}
