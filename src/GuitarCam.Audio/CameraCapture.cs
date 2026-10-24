using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GuitarCam.Audio;

/// <summary>Resolution asked of a webcam (the web's CameraResMode): "native", "1080p", "720p", "480p".</summary>
public enum CameraResMode
{
    Native,
    R1080,
    R720,
    R480,
}

/// <summary>What to open: a DirectShow camera by name, or DroidCam's MJPEG over the network.</summary>
public sealed record CameraSource(string? Device, CameraResMode Mode = CameraResMode.Native, string? IpHost = null)
{
    public bool IsIp => IpHost != null;

    public static string ModeToString(CameraResMode m) => m switch
    {
        CameraResMode.R1080 => "1080p",
        CameraResMode.R720 => "720p",
        CameraResMode.R480 => "480p",
        _ => "native",
    };

    public static CameraResMode ParseMode(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "1080p" or "1080" => CameraResMode.R1080,
        "720p" or "720" => CameraResMode.R720,
        "480p" or "480" => CameraResMode.R480,
        _ => CameraResMode.Native,
    };

    /// <summary>Same URL the web's /api/droidcam-mjpeg proxy builds (server/index.js): port 4747 and /video by default.</summary>
    public static string IpUrl(string host)
    {
        host = host.Trim();
        if (host.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return host.Contains("/video", StringComparison.OrdinalIgnoreCase) ? host : $"{host.TrimEnd('/')}/video";
        return $"http://{(host.Contains(':') ? host : $"{host}:4747")}/video";
    }
}

/// <summary>
/// The cameras FFmpeg sees through DirectShow, and the web's choice of one (refreshDevices in App.tsx):
/// Motorola Smart Connect first (no watermarks), then other phone cameras, else the first.
/// </summary>
public static class CameraDevices
{
    static readonly Regex VideoLine = new("\"(?<name>[^\"]+)\"\\s+\\(video\\)", RegexOptions.Compiled);
    static readonly Regex SmartConnect = new("smart connect|motorola|ready for", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PhoneCam = new("iriun|camo|droidcam|phone|celular|virtual", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Video device names, in DirectShow's order. Empty if FFmpeg is missing.</summary>
    public static string[] List()
    {
        string err = RunFfmpeg(["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"]);
        return VideoLine.Matches(err).Select(m => m.Groups["name"].Value).Distinct().ToArray();
    }

    /// <summary>The formats a camera offers, as FFmpeg prints them (for gclab).</summary>
    public static string[] Options(string device)
    {
        string err = RunFfmpeg(["-hide_banner", "-list_options", "true", "-f", "dshow", "-i", $"video={device}"]);
        return err.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("pixel_format=") || l.Contains("vcodec=")).Select(l => l[(l.IndexOf(']') + 1)..].Trim()).ToArray();
    }

    /// <summary>Keeps the current one if it's still there; otherwise the web's preference order.</summary>
    public static string? Pick(IReadOnlyList<string> devices, string? current)
    {
        if (current != null && devices.Contains(current)) return current;
        if (devices.Count == 0) return null;
        return devices.FirstOrDefault(d => SmartConnect.IsMatch(d)) ?? devices.FirstOrDefault(d => PhoneCam.IsMatch(d)) ?? devices[0];
    }

    static string RunFfmpeg(string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg", args) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return "";
            p.StandardOutput.ReadToEndAsync();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return err;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }
}

/// <summary>
/// The live camera, read by one FFmpeg process as raw I420 frames at the camera's own size. The same frames
/// feed the "En vivo" monitor (scaled to 640 px BGRA, like LiveMonitor.tsx draws its canvas) and, while a take
/// records, a <see cref="VideoTakeWriter"/>: a camera opens only once, like the web's single MediaStream.
/// Port of the camera setup in App.tsx: a webcam is tried at the asked resolution, then native, 720p and 480p;
/// the IP mode reads DroidCam's MJPEG (the web proxies it to an &lt;img&gt;). Frames carry the Stopwatch time
/// they arrived at, which is what lines the video up with the audio.
/// </summary>
public sealed class CameraCapture : IDisposable
{
    /// <summary>Width of the monitor image (LiveMonitor.tsx targetW).</summary>
    public const int PreviewWidth = 640;
    /// <summary>A camera that sent nothing for this long counts as "Sin señal".</summary>
    static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(1.5);

    static readonly Regex Size = new(@",\s(?<w>\d{2,5})x(?<h>\d{2,5})", RegexOptions.Compiled);
    static readonly Regex Fps = new(@"(?<f>[\d.]+) fps", RegexOptions.Compiled);

    readonly CameraSource source;
    readonly Thread thread;
    readonly object gate = new();
    volatile bool disposed;
    Process? proc;

    byte[] preview = [];
    long previewVersion;
    long lastFrameAt;
    VideoTakeWriter? writer;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int PreviewHeight { get; private set; }
    /// <summary>"1280×720 @ 30fps", or "… (IP Directa)"; empty until the camera runs (cameraResolution in the web).</summary>
    public string Resolution { get; private set; } = "";
    /// <summary>Why the camera didn't open, in the web's words; null while fine or still opening.</summary>
    public string? Error { get; private set; }
    public bool Opening { get; private set; } = true;
    /// <summary>Set false when nobody looks at the monitor, to skip the conversion.</summary>
    public bool PreviewEnabled { get; set; } = true;
    public CameraSource Source => source;

    public bool HasSignal => Interlocked.Read(ref lastFrameAt) is var t && t != 0 && Stopwatch.GetElapsedTime(t) < SignalTimeout;

    CameraCapture(CameraSource source)
    {
        this.source = source;
        thread = new Thread(Run) { IsBackground = true, Name = "camera" };
        thread.Start();
    }

    public static CameraCapture Open(CameraSource source) => new(source);

    /// <summary>Frames from now on also go to <paramref name="w"/> (null stops that). The writer decides which count.</summary>
    public void Attach(VideoTakeWriter? w)
    {
        lock (gate) writer = w;
    }

    /// <summary>Copies the newest monitor frame (BGRA, <see cref="PreviewWidth"/> × <see cref="PreviewHeight"/>) if it changed.</summary>
    public bool TryCopyPreview(ref long seen, Action<byte[]> copy)
    {
        lock (gate)
        {
            if (previewVersion == seen || preview.Length == 0) return false;
            seen = previewVersion;
            copy(preview);
            return true;
        }
    }

    IEnumerable<string[]> Attempts()
    {
        string[] common = ["-hide_banner", "-nostdin", "-nostats"];
        string[] output = ["-an", "-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2", "-pix_fmt", "yuv420p", "-f", "rawvideo", "-flush_packets", "1", "-"];
        if (source.IsIp)
        {
            yield return [.. common, "-rw_timeout", "5000000", "-use_wallclock_as_timestamps", "1", "-i", CameraSource.IpUrl(source.IpHost!), .. output];
            yield break;
        }
        // requested mode first, then pure native (critical for DroidCam!), like the web's fallbacks
        CameraResMode[] modes = source.Mode == CameraResMode.Native
            ? [CameraResMode.Native, CameraResMode.R720, CameraResMode.R480]
            : [source.Mode, CameraResMode.Native, CameraResMode.R720, CameraResMode.R480];
        foreach (var m in modes)
        {
            string[] size = m switch
            {
                CameraResMode.R1080 => ["-video_size", "1920x1080", "-framerate", "30"],
                CameraResMode.R720 => ["-video_size", "1280x720", "-framerate", "30"],
                CameraResMode.R480 => ["-video_size", "640x480", "-framerate", "30"],
                _ => [],
            };
            yield return [.. common, "-f", "dshow", "-rtbufsize", "128M", .. size, "-i", $"video={source.Device}", .. output];
        }
    }

    void Run()
    {
        foreach (var args in Attempts())
        {
            if (disposed) return;
            if (Start(args, out var stdout, out double fps))
            {
                Resolution = source.IsIp ? Texts.T("camera.resIp", "{0}×{1} (IP Directa)", Width, Height) : $"{Width}×{Height}{(fps > 0 ? $" @ {Math.Round(fps)}fps" : "")}";
                Opening = false;
                Pump(stdout);
                return;
            }
        }
        Opening = false;
        if (!disposed)
            Error = source.IsIp
                ? Texts.T("camera.errIp", "No pudimos conectar al stream directo de DroidCam en {0}. Verificá que la IP y puerto (4747) sean los que muestra la app en el celu y que el cliente DroidCam de PC esté detenido.", source.IpHost)
                : Texts.T("camera.errDroid", "DroidCam Video está seleccionado pero no entrega imagen. Asegurate de que el programa DroidCam Client en Windows esté abierto y con el video corriendo (\"Start\"), o cambiá al botón \"Modo IP DroidCam\" arriba.");
    }

    /// <summary>Starts FFmpeg and waits for the output stream's size, which FFmpeg prints before the first frame.</summary>
    bool Start(string[] args, out Stream stdout, out double fps)
    {
        stdout = Stream.Null;
        fps = 0;
        var psi = new ProcessStartInfo("ffmpeg", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        lock (gate) proc = p;
        bool output = false;
        int w = 0, h = 0;
        double inFps = 0;
        // reads stderr until the output stream line, then keeps draining it in the background
        while (p.StandardError.ReadLine() is { } line)
        {
            if (line.StartsWith("Output #0")) output = true;
            if (!line.Contains("Video:")) continue;
            if (Fps.Match(line) is { Success: true } f && !output) inFps = double.Parse(f.Groups["f"].Value, CultureInfo.InvariantCulture);
            if (output && Size.Match(line) is { Success: true } s)
            {
                w = int.Parse(s.Groups["w"].Value, CultureInfo.InvariantCulture);
                h = int.Parse(s.Groups["h"].Value, CultureInfo.InvariantCulture);
                break;
            }
        }
        if (w <= 0 || h <= 0 || disposed)
        {
            Kill(p);
            return false;
        }
        _ = Task.Run(async () => { try { await p.StandardError.ReadToEndAsync(); } catch (Exception) { } });
        Width = w;
        Height = h;
        PreviewHeight = (int)Math.Round((double)PreviewWidth * h / w) & ~1;
        fps = inFps;
        stdout = p.StandardOutput.BaseStream;
        return true;
    }

    void Pump(Stream stdout)
    {
        int ySize = Width * Height;
        var frame = new byte[ySize * 3 / 2];
        var bgra = new byte[PreviewWidth * PreviewHeight * 4];
        var spare = new byte[bgra.Length];
        var xs = new int[PreviewWidth];
        for (int x = 0; x < xs.Length; x++) xs[x] = Math.Min(Width - 1, x * Width / PreviewWidth);
        while (!disposed)
        {
            try
            {
                stdout.ReadExactly(frame);
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
            {
                break;
            }
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref lastFrameAt, now);
            VideoTakeWriter? w;
            lock (gate) w = writer;
            w?.Offer(frame, now);
            if (!PreviewEnabled) continue;
            ToBgra(frame, Width, Height, xs, spare, PreviewHeight);
            lock (gate)
            {
                (preview, spare) = (spare, preview.Length == spare.Length ? preview : new byte[spare.Length]);
                previewVersion++;
            }
        }
        Interlocked.Exchange(ref lastFrameAt, 0);
    }

    /// <summary>Nearest-neighbour downscale of an I420 frame to BGRA (BT.601, limited range, as webcams send).</summary>
    static void ToBgra(byte[] yuv, int w, int h, int[] xs, byte[] dst, int dh)
    {
        int ySize = w * h, cw = w / 2, uOff = ySize, vOff = ySize + ySize / 4;
        int dw = xs.Length;
        Parallel.For(0, dh, y =>
        {
            int sy = Math.Min(h - 1, y * h / dh);
            int yRow = sy * w, cRow = (sy >> 1) * cw;
            int o = y * dw * 4;
            for (int x = 0; x < dw; x++, o += 4)
            {
                int sx = xs[x];
                int c = yuv[yRow + sx] - 16;
                int d = yuv[uOff + cRow + (sx >> 1)] - 128;
                int e = yuv[vOff + cRow + (sx >> 1)] - 128;
                int y1 = 298 * c + 128;
                dst[o] = Clamp((y1 + 516 * d) >> 8);
                dst[o + 1] = Clamp((y1 - 100 * d - 208 * e) >> 8);
                dst[o + 2] = Clamp((y1 + 409 * e) >> 8);
                dst[o + 3] = 255;
            }
        });
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    public void Dispose()
    {
        disposed = true;
        Process? p;
        lock (gate)
        {
            p = proc;
            writer = null;
        }
        if (p != null)
        {
            Kill(p);
            p.Dispose();
        }
        thread.Join(1000);
    }
}
