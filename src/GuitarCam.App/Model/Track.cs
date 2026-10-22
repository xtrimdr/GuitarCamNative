using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GuitarCam.App.Video;
using GuitarCam.Audio.Dsp;
using NAudio.Wave;

namespace GuitarCam.App.Model;

/// <summary>A take loaded for playback: its audio, waveform peaks, DSP and video frames.</summary>
public sealed class Track
{
    public const int PeakBin = 64;

    public Take Take { get; }
    public float[] L { get; }
    public float[] R { get; }
    public int SampleRate { get; }
    public float[] PeakMin { get; }
    public float[] PeakMax { get; }
    public double RmsDb { get; }
    public Saturator Sat { get; }
    public Compressor Comp { get; }

    // engine scratch + meters (written on the audio thread, read by the UI)
    public readonly float[] BufL = new float[8192];
    public readonly float[] BufR = new float[8192];
    public float MeterL, MeterR;
    public double GrDb;
    public float ShowL, ShowR; // UI side, with fall-off

    // video: decoded by FFmpeg, the current frame copied into Frame on the UI thread
    public VideoDecoder? Video { get; private set; }
    public WriteableBitmap? Frame { get; private set; }
    public bool InClip { get; private set; }
    long frameVersion;

    public double LengthSec => L.Length / (double)SampleRate;
    public double OffsetSec => Take.AudioOffsetMs / 1000;
    public double ClipStartSec => Math.Max(0, OffsetSec + Take.TrimStartSec);
    public double ClipEndSec => OffsetSec + LengthSec - Take.TrimEndSec;

    public Track(Take take, string sessionDir)
    {
        Take = take;
        using (var r = new AudioFileReader(Path.Combine(sessionDir, take.AudioFile)))
        {
            SampleRate = r.WaveFormat.SampleRate;
            int ch = r.WaveFormat.Channels;
            var all = new float[(int)(r.Length / 4)];
            int read = 0, n;
            while ((n = r.Read(all, read, Math.Min(65536, all.Length - read))) > 0) read += n;
            int len = read / ch;
            L = new float[len];
            R = new float[len];
            for (int i = 0; i < len; i++)
            {
                L[i] = all[i * ch];
                R[i] = ch > 1 ? all[i * ch + 1] : L[i];
            }
        }
        int bins = (L.Length + PeakBin - 1) / PeakBin;
        PeakMin = new float[bins];
        PeakMax = new float[bins];
        for (int b = 0; b < bins; b++)
        {
            float lo = 0, hi = 0;
            for (int i = b * PeakBin, end = Math.Min(L.Length, i + PeakBin); i < end; i++)
            {
                float v = 0.5f * (L[i] + R[i]);
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            PeakMin[b] = lo;
            PeakMax[b] = hi;
        }
        RmsDb = LevelAnalysis.Analyze([L, R], SampleRate).RmsDb;
        Sat = new Saturator(SampleRate);
        Comp = new Compressor(SampleRate);
        ApplySat();
        ApplyComp();
    }

    public void ApplySat() => Sat.Set(SatParams.From(Take.SatOn, Take.SatMode, Take.SatAmount, RmsDb));

    public void ApplyComp()
    {
        Comp.Threshold = Take.Threshold;
        Comp.Ratio = Take.Ratio;
        Comp.Attack = Take.Attack;
        Comp.Release = Take.Release;
        Comp.Knee = Take.Knee;
        Comp.Makeup = Take.Makeup;
        Comp.Limit = Take.Limit;
        Comp.Ceiling = -3;
        Comp.Bypass = !Take.CompOn;
    }

    public void OpenVideo(string sessionDir) => Video = VideoDecoder.Open(Path.Combine(sessionDir, Take.VideoFile));

    /// <summary>
    /// Asks the decoder for the frame at a timeline time and copies a new one into <see cref="Frame"/>.
    /// Returns true when the picture changed. Outside the clip the panel is empty.
    /// </summary>
    public bool UpdateVideo(double timelineSec)
    {
        if (Video == null) return false;
        bool inside = timelineSec >= ClipStartSec && timelineSec < ClipEndSec;
        bool changed = inside != InClip;
        InClip = inside;
        if (!inside) return changed;
        Video.Want(timelineSec - OffsetSec + Take.VideoOffsetMs / 1000);
        Frame ??= new WriteableBitmap(new PixelSize(Video.Width, Video.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var wb = Frame;
        return Video.TryCopyLatest(ref frameVersion, buf =>
        {
            using var fb = wb.Lock();
            int row = Video.Width * 4;
            for (int y = 0; y < Video.Height; y++) Marshal.Copy(buf, y * row, fb.Address + y * fb.RowBytes, row);
        }) || changed;
    }

    public void CloseVideo()
    {
        Video?.Dispose();
        Video = null;
    }
}
