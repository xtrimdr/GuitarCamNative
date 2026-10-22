using NAudio.Wave;
using NAudio.Wave.Asio;
using NAudio.Wave.SampleProviders;

namespace GuitarCam.Audio;

/// <summary>
/// Full-duplex ASIO stream. One callback gets the input buffers and fills the output ones on the same
/// sample clock, so recording, click and monitoring line up to the sample (what START_LEAD_SEC scheduling
/// does in the web app, without a scheduler).
/// </summary>
public sealed class AsioDuplex : IDisposable
{
    /// <summary>Called on the driver's thread. <c>output</c> arrives zeroed. Don't allocate or block in it.</summary>
    public delegate void ProcessBlock(float[][] input, float[][] output, int frames);

    /// <summary>Drivers that wrap other APIs. A device's own driver is preferred over these.</summary>
    static readonly string[] GenericDrivers = ["ASIO4ALL", "FL Studio", "FlexASIO", "ASIO2WASAPI", "Generic"];

    readonly AsioOut asio;
    readonly ProcessBlock process;
    float[][] inBuf = [];
    float[][] outBuf = [];

    public string Driver { get; }
    public int SampleRate { get; }
    public int FramesPerBuffer { get; private set; }
    public AsioSampleType SampleType { get; private set; }
    public Exception? Error { get; private set; }

    public static string[] DriverNames() => AsioOut.GetDriverNames();

    /// <summary>The given driver, else the first device driver that opens (an installed driver whose device isn't plugged in fails).</summary>
    public static string PickDriver(string? wanted)
    {
        var names = DriverNames();
        if (names.Length == 0) throw new InvalidOperationException("No hay drivers ASIO instalados.");
        if (!string.IsNullOrEmpty(wanted))
            return names.FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault(n => n.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No encontré el driver ASIO \"{wanted}\".");
        bool generic(string n) => GenericDrivers.Any(g => n.Contains(g, StringComparison.OrdinalIgnoreCase));
        return names.Where(n => !generic(n)).FirstOrDefault(Opens) ?? names.FirstOrDefault(Opens)
            ?? throw new InvalidOperationException("Ningún driver ASIO se pudo abrir (¿está conectada la interfaz?).");
    }

    static bool Opens(string driver)
    {
        try
        {
            using var a = new AsioOut(driver);
            return a.DriverInputChannelCount > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void ShowControlPanel(string driver)
    {
        using var a = new AsioOut(driver);
        a.ShowControlPanel();
    }

    /// <param name="inputOffset">First input channel (0-based).</param>
    /// <param name="outputOffset">First of the two output channels (0-based).</param>
    public AsioDuplex(string driver, int sampleRate, int inputOffset, int inputChannels, int outputOffset, ProcessBlock process)
    {
        Driver = driver;
        SampleRate = sampleRate;
        this.process = process;
        asio = new AsioOut(driver);
        if (!asio.IsSampleRateSupported(sampleRate))
        {
            asio.Dispose();
            throw new InvalidOperationException($"El driver \"{driver}\" no acepta {sampleRate} Hz.");
        }
        asio.InputChannelOffset = inputOffset;
        asio.ChannelOffset = outputOffset;
        asio.InitRecordAndPlayback(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)), inputChannels, sampleRate);
        asio.AudioAvailable += OnAudio;
    }

    public int DriverInputChannels => asio.DriverInputChannelCount;
    public int DriverOutputChannels => asio.DriverOutputChannelCount;
    public int ReportedOutputLatency => asio.PlaybackLatency;

    public void Start() => asio.Play();

    public void Stop() => asio.Stop();

    void OnAudio(object? sender, AsioAudioAvailableEventArgs e)
    {
        try
        {
            int n = e.SamplesPerBuffer;
            FramesPerBuffer = n;
            SampleType = e.AsioSampleType;
            if (inBuf.Length != e.InputBuffers.Length || (inBuf.Length > 0 && inBuf[0].Length != n))
                inBuf = Alloc(e.InputBuffers.Length, n);
            if (outBuf.Length != e.OutputBuffers.Length || (outBuf.Length > 0 && outBuf[0].Length != n))
                outBuf = Alloc(e.OutputBuffers.Length, n);
            for (int c = 0; c < inBuf.Length; c++) Read(e.InputBuffers[c], inBuf[c], e.AsioSampleType);
            foreach (var o in outBuf) Array.Clear(o);
            process(inBuf, outBuf, n);
            for (int c = 0; c < outBuf.Length; c++) Write(outBuf[c], e.OutputBuffers[c], e.AsioSampleType);
            e.WrittenToOutputBuffers = true;
        }
        catch (Exception ex)
        {
            Error ??= ex;
        }
    }

    static float[][] Alloc(int channels, int n)
    {
        var a = new float[channels][];
        for (int c = 0; c < channels; c++) a[c] = new float[n];
        return a;
    }

    static unsafe void Read(IntPtr src, float[] dst, AsioSampleType type)
    {
        switch (type)
        {
            case AsioSampleType.Int32LSB:
            {
                var p = (int*)src;
                for (int i = 0; i < dst.Length; i++) dst[i] = p[i] / 2147483648f;
                break;
            }
            case AsioSampleType.Int24LSB:
            {
                var p = (byte*)src;
                for (int i = 0; i < dst.Length; i++, p += 3) dst[i] = (p[0] | p[1] << 8 | (sbyte)p[2] << 16) / 8388608f;
                break;
            }
            case AsioSampleType.Int16LSB:
            {
                var p = (short*)src;
                for (int i = 0; i < dst.Length; i++) dst[i] = p[i] / 32768f;
                break;
            }
            case AsioSampleType.Float32LSB:
                new ReadOnlySpan<float>((void*)src, dst.Length).CopyTo(dst);
                break;
            default:
                throw new NotSupportedException($"Formato ASIO no soportado: {type}");
        }
    }

    static unsafe void Write(float[] src, IntPtr dst, AsioSampleType type)
    {
        switch (type)
        {
            case AsioSampleType.Int32LSB:
            {
                var p = (int*)dst;
                for (int i = 0; i < src.Length; i++) p[i] = (int)(Math.Clamp(src[i], -1f, 1f) * 2147483647.0);
                break;
            }
            case AsioSampleType.Int24LSB:
            {
                var p = (byte*)dst;
                for (int i = 0; i < src.Length; i++, p += 3)
                {
                    int v = (int)(Math.Clamp(src[i], -1f, 1f) * 8388607.0);
                    p[0] = (byte)v;
                    p[1] = (byte)(v >> 8);
                    p[2] = (byte)(v >> 16);
                }
                break;
            }
            case AsioSampleType.Int16LSB:
            {
                var p = (short*)dst;
                for (int i = 0; i < src.Length; i++) p[i] = (short)(Math.Clamp(src[i], -1f, 1f) * 32767.0);
                break;
            }
            case AsioSampleType.Float32LSB:
                src.AsSpan().CopyTo(new Span<float>((void*)dst, src.Length));
                break;
            default:
                throw new NotSupportedException($"Formato ASIO no soportado: {type}");
        }
    }

    public void Dispose()
    {
        asio.AudioAvailable -= OnAudio;
        asio.Stop();
        asio.Dispose();
    }
}
