using System.Diagnostics;
using Avalonia.Media.Imaging;
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

    // video: one frame per second, extracted with FFmpeg into a cache folder
    public string? FramesDir { get; private set; }
    public int FrameCount { get; private set; }
    Bitmap? frame;
    int frameIndex = -1;

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

    /// <summary>Extracts 1 frame per second (FFmpeg) into a cache folder, once per video file.</summary>
    public async Task ExtractFramesAsync(string sessionDir)
    {
        string video = Path.Combine(sessionDir, Take.VideoFile);
        if (!File.Exists(video)) return;
        var info = new FileInfo(video);
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GuitarCamNative", "frames", $"{Path.GetFileNameWithoutExtension(video)}_{info.Length}");
        string done = Path.Combine(dir, "done");
        if (!File.Exists(done))
        {
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo("ffmpeg",
                ["-v", "error", "-y", "-i", video, "-vf", "fps=1,scale=360:-2", Path.Combine(dir, "%05d.png")])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) return;
            await File.WriteAllTextAsync(done, "");
        }
        FrameCount = Directory.GetFiles(dir, "*.png").Length;
        FramesDir = dir;
    }

    /// <summary>The video frame at a timeline time, or null outside the clip.</summary>
    public Bitmap? FrameAt(double timelineSec)
    {
        if (FramesDir == null || FrameCount == 0) return null;
        if (timelineSec < ClipStartSec || timelineSec >= ClipEndSec) return null;
        double videoSec = timelineSec - OffsetSec + Take.VideoOffsetMs / 1000;
        int idx = Math.Clamp((int)videoSec, 0, FrameCount - 1);
        if (idx != frameIndex)
        {
            frameIndex = idx;
            // no Dispose: the compositor may still be drawing the old one (that showed as garbage); the GC frees it
            string path = Path.Combine(FramesDir, $"{idx + 1:00000}.png");
            frame = File.Exists(path) ? new Bitmap(path) : null;
        }
        return frame;
    }
}
