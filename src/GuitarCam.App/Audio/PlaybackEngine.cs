using System.Diagnostics;
using GuitarCam.App.Model;
using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App.Audio;

/// <summary>
/// Plays the timeline through ASIO: per take sat → comp → pan → fader, summed into the master limiter;
/// the click goes around the limiter, like in the web app. Also meters the interface's input 1-2.
/// Reverb, delay and the EQs aren't ported yet.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    public const int SampleRate = 48000;

    AsioDuplex? io;
    volatile Track[] tracks = [];
    readonly MasterLimiter limiter = new(SampleRate, (int)Math.Round(MasterLimiter.LookaheadSec * SampleRate));
    readonly float[] mixL = new float[8192];
    readonly float[] mixR = new float[8192];
    float[] clickHi = ClickSynth.Render(ClickSound.Madera, true, SampleRate);
    float[] clickLo = ClickSynth.Render(ClickSound.Madera, false, SampleRate);
    float[]? click;
    int clickPos;

    long position; // timeline frame
    volatile bool playing;
    long endFrame;
    float inL, inR, outL, outR;

    public string Status { get; private set; } = "Audio apagado";
    public bool IsOpen => io != null;
    public bool Playing => playing;
    public bool ClickOn { get; set; }
    public double Bpm { get; set; } = 120;
    public double LoadPercent { get; private set; }
    public double PositionSec => Interlocked.Read(ref position) / (double)SampleRate;

    public void Open()
    {
        try
        {
            string driver = AsioDuplex.PickDriver(null);
            io = new AsioDuplex(driver, SampleRate, 0, 2, 0, Process);
            io.Start();
            for (int i = 0; i < 100 && io.FramesPerBuffer == 0; i++) Thread.Sleep(10);
            double ms = 1000.0 * io.FramesPerBuffer / SampleRate;
            Status = $"{driver} · {SampleRate / 1000} kHz · {io.FramesPerBuffer} muestras ({ms:0.0} ms)";
        }
        catch (Exception e)
        {
            io?.Dispose();
            io = null;
            Status = $"Sin audio: {e.Message}";
        }
    }

    public void SetTracks(IEnumerable<Track> list)
    {
        playing = false;
        tracks = list.ToArray();
        endFrame = tracks.Length == 0 ? 0 : (long)(tracks.Max(t => t.ClipEndSec) * SampleRate);
        Interlocked.Exchange(ref position, 0);
    }

    public double LengthSec => endFrame / (double)SampleRate;

    public void Play()
    {
        if (Interlocked.Read(ref position) >= endFrame) Seek(0);
        long pos = Interlocked.Read(ref position);
        double beatLen = SampleRate * 60 / Bpm;
        nextBeat = (long)Math.Ceiling(pos / beatLen);
        playing = true;
    }

    public void Stop() => playing = false;

    public void Seek(double sec)
    {
        Interlocked.Exchange(ref position, (long)(Math.Clamp(sec, 0, LengthSec) * SampleRate));
        double beatLen = SampleRate * 60 / Bpm;
        nextBeat = (long)Math.Ceiling(Interlocked.Read(ref position) / beatLen);
    }

    long nextBeat;

    /// <summary>Peak levels since the last call (input 1-2, master out).</summary>
    public (float InL, float InR, float OutL, float OutR) TakeMeters()
    {
        var m = (inL, inR, outL, outR);
        inL = inR = outL = outR = 0;
        return m;
    }

    void Process(float[][] input, float[][] output, int n)
    {
        long t0 = Stopwatch.GetTimestamp();
        float pl = inL, pr = inR;
        for (int i = 0; i < n; i++)
        {
            float a = Math.Abs(input[0][i]);
            float b = input.Length > 1 ? Math.Abs(input[1][i]) : a;
            if (a > pl) pl = a;
            if (b > pr) pr = b;
        }
        inL = pl;
        inR = pr;

        if (playing && n <= mixL.Length)
        {
            long pos = Interlocked.Read(ref position);
            var ml = mixL.AsSpan(0, n);
            var mr = mixR.AsSpan(0, n);
            ml.Clear();
            mr.Clear();
            var list = tracks;
            bool anySolo = list.Any(t => t.Take.Solo);
            foreach (var t in list) MixTrack(t, pos, n, anySolo, ml, mr);
            limiter.Process(ml, mr);

            double beatLen = SampleRate * 60 / Bpm;
            float ol = outL, or = outR;
            for (int i = 0; i < n; i++)
            {
                long f = pos + i;
                if (ClickOn && f >= (long)Math.Round(nextBeat * beatLen))
                {
                    click = nextBeat % 4 == 0 ? clickHi : clickLo;
                    clickPos = 0;
                    nextBeat++;
                }
                float c = 0;
                if (click != null)
                {
                    c = click[clickPos];
                    if (++clickPos >= click.Length) click = null;
                }
                output[0][i] = ml[i] + c;
                output[1][i] = mr[i] + c;
                if (Math.Abs(output[0][i]) > ol) ol = Math.Abs(output[0][i]);
                if (Math.Abs(output[1][i]) > or) or = Math.Abs(output[1][i]);
            }
            outL = ol;
            outR = or;
            pos += n;
            Interlocked.Exchange(ref position, pos);
            if (pos >= endFrame) playing = false;
        }
        double used = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        double budget = n / (double)SampleRate;
        LoadPercent = LoadPercent * 0.9 + 0.1 * (100 * used / budget);
    }

    static void MixTrack(Track t, long pos, int n, bool anySolo, Span<float> ml, Span<float> mr)
    {
        var take = t.Take;
        var bl = t.BufL.AsSpan(0, n);
        var br = t.BufR.AsSpan(0, n);
        long offset = (long)Math.Round(take.AudioOffsetMs * SampleRate / 1000);
        long lo = (long)Math.Round(take.TrimStartSec * SampleRate);
        long hi = t.L.Length - (long)Math.Round(take.TrimEndSec * SampleRate);
        for (int i = 0; i < n; i++)
        {
            long src = pos + i - offset;
            bool inside = src >= lo && src < hi;
            bl[i] = inside ? t.L[src] : 0;
            br[i] = inside ? t.R[src] : 0;
        }
        t.Sat.Process(bl, br);
        t.Comp.Process(bl, br);
        t.GrDb = Math.Min(t.GrDb, t.Comp.TakeMeter());

        bool mute = take.Muted || (anySolo && !take.Solo);
        float g = mute ? 0 : take.Volume;
        // StereoPannerNode on a stereo input (Web Audio spec)
        double pan = Math.Clamp(take.Pan, -1, 1);
        double x = pan <= 0 ? pan + 1 : pan;
        float gl = (float)Math.Cos(x * Math.PI / 2), gr = (float)Math.Sin(x * Math.PI / 2);
        float pkL = t.MeterL, pkR = t.MeterR;
        for (int i = 0; i < n; i++)
        {
            float l = bl[i], r = br[i];
            float ol, or;
            if (pan <= 0)
            {
                ol = l + r * gl;
                or = r * gr;
            }
            else
            {
                ol = l * gl;
                or = r + l * gr;
            }
            ol *= g;
            or *= g;
            ml[i] += ol;
            mr[i] += or;
            if (Math.Abs(ol) > pkL) pkL = Math.Abs(ol);
            if (Math.Abs(or) > pkR) pkR = Math.Abs(or);
        }
        t.MeterL = pkL;
        t.MeterR = pkR;
    }

    public void SetClickSound(ClickSound s)
    {
        clickHi = ClickSynth.Render(s, true, SampleRate);
        clickLo = ClickSynth.Render(s, false, SampleRate);
    }

    public void Dispose()
    {
        io?.Dispose();
        io = null;
    }
}
