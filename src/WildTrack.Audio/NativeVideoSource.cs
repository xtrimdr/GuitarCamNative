using FFmpeg.AutoGen;

namespace WildTrack.Audio;

/// <summary>
/// Decodes a video file inside the process with FFmpeg's own libraries (FFmpeg.AutoGen over the DLLs in
/// <see cref="LibDir"/>), on the video card when it can (D3D11VA, one device shared by every source). Next to
/// ffmpeg.exe through a pipe it doesn't pay a process start on every seek (~130 ms on a 720p take, against ~16 ms
/// here), which froze the preview for a few frames at each cut and jump: a seek goes to the keyframe before and
/// decodes forwards, converting only the frame asked for. The card only takes pictures above 1080p (<see cref="HardwareAbove"/>). Frames come out at the file's own rate, scaled (bicubic, like ffmpeg's scale filter) and turned upright
/// (a phone's rotation tag) to BGRA. Nothing is allocated per frame: it runs next to the audio thread.
/// </summary>
public sealed unsafe class NativeVideoSource : IVideoFrameSource
{
    /// <summary>AVERROR(EAGAIN): the decoder needs another packet before it can give a frame.</summary>
    const int EAgain = -11;
    /// <summary>
    /// Pictures up to this many pixels (1080p) decode on the processor: measured with wtlab leer-video, a 720p take
    /// reads 5× faster there than copying each frame off the card, and seeks about as fast. Bigger ones (a phone's
    /// 4K, HEVC) go to the card, where the processor would fall behind with several tracks.
    /// </summary>
    const int HardwareAbove = 1920 * 1088;
    /// <summary>
    /// Processor threads per source: every cut is a clip with its own source, and "as many as the processor has"
    /// times a few dozen clips is hundreds of threads. A 720p take still reads at ~1000 frames/s with these.
    /// </summary>
    static readonly int DecodeThreads = Math.Min(Environment.ProcessorCount, 8);

    /// <summary>Where the FFmpeg DLLs travel with the exe (the build copies them from lib\ffmpeg).</summary>
    public static string LibDir => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    static readonly object initGate = new();
    static bool? available;
    static bool hwTried;
    static AVBufferRef* hwDevice;
    // held here so the delegate FFmpeg calls back into is never collected
    static readonly AVCodecContext_get_format getFormat = PickFormat;

    AVFormatContext* fmt;
    AVCodecContext* cc;
    AVPacket* pkt;
    AVFrame* frame, swFrame, rgb; // decoded / copied off the card / scaled to BGRA
    SwsContext* sws;
    readonly int stream;
    readonly double timeBase, startOffset, frameDur;
    readonly int rotation, scaledW, scaledH;
    readonly byte[]? unrotated;
    bool flushed;
    double skipUntil;

    public int Width { get; }
    public int Height { get; }
    /// <summary>True when the video card decodes it.</summary>
    public bool Hardware { get; }
    /// <summary>The file's length (s), 0 if it doesn't say.</summary>
    public double DurationSec { get; }
    /// <summary>Why <see cref="Available"/> is false.</summary>
    public static string? Error { get; private set; }

    /// <summary>Loads the FFmpeg DLLs once; false (and <see cref="Error"/>) when they're missing or don't load.</summary>
    public static bool Available
    {
        get
        {
            lock (initGate)
            {
                if (available is { } a) return a;
                try
                {
                    if (!File.Exists(Path.Combine(LibDir, "avcodec-63.dll"))) throw new FileNotFoundException($"faltan los DLL de FFmpeg en {LibDir}");
                    ffmpeg.RootPath = LibDir;
                    DynamicallyLoadedBindings.Initialize();
                    ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
                    available = true;
                }
                catch (Exception e)
                {
                    Error = e.Message;
                    available = false;
                }
                return available.Value;
            }
        }
    }

    /// <summary>The FFmpeg version the DLLs report, for wtlab and the about box; null when they aren't there.</summary>
    public static string? Version => Available ? ffmpeg.av_version_info() : null;

    /// <summary>Opens a file at most <paramref name="maxWidth"/> pixels wide; on the processor if the card can't. Null if it can't be read.</summary>
    public static NativeVideoSource? Open(string path, int maxWidth = 640, bool hardware = true)
    {
        if (!File.Exists(path) || !Available) return null;
        try
        {
            return new NativeVideoSource(path, maxWidth, hardware);
        }
        catch when (hardware)
        {
            return Open(path, maxWidth, false);
        }
        catch
        {
            return null;
        }
    }

    NativeVideoSource(string path, int maxWidth, bool hardware)
    {
        try
        {
            AVFormatContext* f = null;
            if (ffmpeg.avformat_open_input(&f, path, null, null) < 0) throw new IOException(path);
            fmt = f;
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) throw new IOException(path);
            AVCodec* codec = null;
            stream = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (stream < 0 || codec == null) throw new IOException(path);
            var st = fmt->streams[stream];
            timeBase = ffmpeg.av_q2d(st->time_base);
            // times are from the file's start, like ffmpeg -ss
            startOffset = fmt->start_time != ffmpeg.AV_NOPTS_VALUE ? fmt->start_time / (double)ffmpeg.AV_TIME_BASE : 0;
            DurationSec = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            var rate = st->avg_frame_rate.num > 0 ? st->avg_frame_rate : st->r_frame_rate;
            frameDur = rate.num > 0 && rate.den > 0 ? rate.den / (double)rate.num : 1 / 30.0;

            cc = ffmpeg.avcodec_alloc_context3(codec);
            if (cc == null || ffmpeg.avcodec_parameters_to_context(cc, st->codecpar) < 0) throw new IOException(path);
            cc->pkt_timebase = st->time_base;
            var device = hardware && st->codecpar->width * st->codecpar->height > HardwareAbove ? SharedDevice() : null;
            if (device != null)
            {
                cc->hw_device_ctx = ffmpeg.av_buffer_ref(device);
                cc->get_format = getFormat;
                Hardware = true;
            }
            else cc->thread_count = DecodeThreads;
            if (ffmpeg.avcodec_open2(cc, codec, null) < 0) throw new IOException(path);

            rotation = Rotation(st);
            bool sideways = rotation is 90 or 270;
            int w = cc->width, h = cc->height;
            if (w <= 0 || h <= 0) throw new IOException(path);
            int uw = sideways ? h : w, uh = sideways ? w : h;
            Width = Math.Min(maxWidth, uw) & ~1;
            Height = (int)Math.Round((double)uh * Width / uw) & ~1;
            (scaledW, scaledH) = sideways ? (Height, Width) : (Width, Height);
            if (rotation != 0) unrotated = new byte[scaledW * scaledH * 4];

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            swFrame = ffmpeg.av_frame_alloc();
            rgb = ffmpeg.av_frame_alloc();
            rgb->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            rgb->width = scaledW;
            rgb->height = scaledH;
            if (ffmpeg.av_frame_get_buffer(rgb, 0) < 0) throw new OutOfMemoryException();
            sws = ffmpeg.sws_alloc_context();
            if (sws == null) throw new OutOfMemoryException();
            sws->flags = (uint)SwsFlags.SWS_BICUBIC; // ffmpeg's scale filter's default
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>One D3D11 device for every source (each one would cost its own memory on the card); null without one.</summary>
    static AVBufferRef* SharedDevice()
    {
        lock (initGate)
        {
            if (!hwTried)
            {
                hwTried = true;
                AVBufferRef* dev = null;
                if (ffmpeg.av_hwdevice_ctx_create(&dev, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) >= 0) hwDevice = dev;
            }
            return hwDevice;
        }
    }

    static AVPixelFormat PickFormat(AVCodecContext* c, AVPixelFormat* formats)
    {
        for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11) return *p;
        // a format the card can't decode (some 10-bit or 4:2:2 phone video): the processor does it
        return ffmpeg.avcodec_default_get_format(c, formats);
    }

    /// <summary>The clockwise turn that makes the picture upright (0, 90, 180 or 270), as ffmpeg's autorotate does.</summary>
    static int Rotation(AVStream* st)
    {
        var sd = ffmpeg.av_packet_side_data_get(st->codecpar->coded_side_data, st->codecpar->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (sd == null || sd->size < 36) return 0;
        var matrix = *(int_array9*)sd->data;
        double r = ffmpeg.av_display_rotation_get(in matrix);
        if (double.IsNaN(r)) return 0;
        return ((int)Math.Round(-r / 90) * 90 % 360 + 360) % 360;
    }

    public void Seek(double sec)
    {
        long ts = (long)Math.Round((Math.Max(0, sec) + startOffset) / timeBase);
        ffmpeg.av_seek_frame(fmt, stream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(cc);
        flushed = false;
        skipUntil = sec;
    }

    public bool Read(byte[] bgra, double notBefore, out double sec)
    {
        sec = 0;
        double drop = Math.Max(notBefore, skipUntil);
        while (true)
        {
            int r = ffmpeg.avcodec_receive_frame(cc, frame);
            if (r == 0)
            {
                long pts = frame->best_effort_timestamp;
                double t = (pts == ffmpeg.AV_NOPTS_VALUE ? 0 : pts * timeBase) - startOffset;
                double dur = frame->duration > 0 ? frame->duration * timeBase : frameDur;
                // frames before the one asked for (from the keyframe up to it) are only decoded
                if (t + dur <= drop + 1e-6)
                {
                    ffmpeg.av_frame_unref(frame);
                    continue;
                }
                skipUntil = 0;
                bool ok = Convert(bgra);
                ffmpeg.av_frame_unref(frame);
                sec = t;
                return ok;
            }
            if (r != EAgain || flushed) return false;
            if (ffmpeg.av_read_frame(fmt, pkt) < 0)
            {
                // end of the file: what the decoder still holds comes out, then AVERROR_EOF
                flushed = true;
                ffmpeg.avcodec_send_packet(cc, null);
                continue;
            }
            // a damaged packet is skipped, like ffmpeg does
            if (pkt->stream_index == stream) ffmpeg.avcodec_send_packet(cc, pkt);
            ffmpeg.av_packet_unref(pkt);
        }
    }

    bool Convert(byte[] bgra)
    {
        var src = frame;
        if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            ffmpeg.av_frame_unref(swFrame);
            if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) < 0) return false;
            src = swFrame;
        }
        // the frame says its matrix (BT.709 for HD, BT.601 when it doesn't say) and range, and sws_scale_frame
        // follows it like ffmpeg's scale filter; it sets itself up again if the frame's format or size changes
        if (ffmpeg.sws_scale_frame(sws, rgb, src) < 0) return false;
        var target = unrotated ?? bgra;
        int row = scaledW * 4, stride = rgb->linesize[0];
        byte* from = rgb->data[0];
        fixed (byte* d = target)
        {
            for (int y = 0; y < scaledH; y++)
                Buffer.MemoryCopy(from + (long)y * stride, d + (long)y * row, row, row);
        }
        if (unrotated != null) Rotate(unrotated, bgra);
        return true;
    }

    void Rotate(byte[] from, byte[] to)
    {
        int w = scaledW, h = scaledH;
        fixed (byte* s8 = from, d8 = to)
        {
            uint* s = (uint*)s8, d = (uint*)d8;
            switch (rotation)
            {
                case 90:
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            d[x * h + (h - 1 - y)] = s[y * w + x];
                    break;
                case 270:
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            d[(w - 1 - x) * h + y] = s[y * w + x];
                    break;
                default:
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            d[(h - 1 - y) * w + (w - 1 - x)] = s[y * w + x];
                    break;
            }
        }
    }

    public void Dispose()
    {
        var s = sws;
        if (s != null) ffmpeg.sws_free_context(&s);
        sws = null;
        var f3 = rgb;
        if (f3 != null) ffmpeg.av_frame_free(&f3);
        rgb = null;
        var f1 = frame;
        if (f1 != null) ffmpeg.av_frame_free(&f1);
        frame = null;
        var f2 = swFrame;
        if (f2 != null) ffmpeg.av_frame_free(&f2);
        swFrame = null;
        var p = pkt;
        if (p != null) ffmpeg.av_packet_free(&p);
        pkt = null;
        var c = cc;
        if (c != null) ffmpeg.avcodec_free_context(&c);
        cc = null;
        var f = fmt;
        if (f != null) ffmpeg.avformat_close_input(&f);
        fmt = null;
    }
}
