using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.Audio;

/// <summary>wav = 24-bit PCM, mp3 = 320 kbps (LAME through FFmpeg).</summary>
public enum AudioFormat { Wav, Mp3 }

/// <summary>Where an audio export is: mixing, converting to MP3 or saving the file.</summary>
public enum AudioExportStage { Mix, Encode, Finalize }

public sealed record AudioExportProgress(AudioExportStage Stage, double Fraction, double ElapsedSec);

/// <summary>
/// Exports the project's mix as an audio file (native only: the web exports only MP4). Same offline render as the
/// MP4 export (<see cref="MixRenderer"/>, the graph playback uses) and the same destination rules
/// (<see cref="Mp4Exporter.CheckDestination"/>, a hidden <c>.part</c> renamed at the end), so a crash or a cancel
/// never leaves half a file under the real name. WAV is written directly; MP3 goes through FFmpeg's LAME encoder.
/// </summary>
public static class AudioExporter
{
    public static string Extension(AudioFormat f) => f == AudioFormat.Mp3 ? ".mp3" : ".wav";

    public static AudioFormat ParseFormat(string? s) => s?.Trim().ToLowerInvariant() == "mp3" ? AudioFormat.Mp3 : AudioFormat.Wav;

    public static string FormatId(AudioFormat f) => f == AudioFormat.Mp3 ? "mp3" : "wav";

    public static bool IsAudioFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".mp3";

    /// <summary>Rough size for the dialog: 24-bit stereo at the mix rate, or 320 kbps.</summary>
    public static double EstimateBytes(AudioFormat f, double durationSec, int sampleRate = 48000) =>
        Math.Max(0, durationSec) * (f == AudioFormat.Mp3 ? 320_000 / 8.0 : sampleRate * 6.0);

    public static async Task<ExportResult> ExportAsync(ExportJobSpec job, AudioFormat format,
        Action<AudioExportProgress>? onProgress = null, CancellationToken ct = default)
    {
        if (job.MixTracks.All(t => t.Clips.Count == 0))
            throw new ExportException("Necesitás al menos un canal en el timeline para exportar.");
        string ext = Extension(format);
        var target = Mp4Exporter.CheckDestination(job.ProjectDir, job.OutDir,
            string.IsNullOrWhiteSpace(job.FileName) ? job.ProjectName : job.FileName, ext);
        if (target.Exists && !job.Overwrite)
            throw new ExportException($"Ya existe {target.FileName}{ext} en esa carpeta.", exists: true);
        if (!target.IsDefault && !Directory.Exists(target.Folder))
            throw new ExportException("La carpeta de destino ya no existe.");
        Directory.CreateDirectory(target.Folder);

        var sw = Stopwatch.StartNew();
        void Report(AudioExportStage stage, double f) => onProgress?.Invoke(new AudioExportProgress(stage, f, sw.Elapsed.TotalSeconds));

        string tempWorkDir = Path.Combine(Path.GetTempPath(), "GuitarCamExport");
        Directory.CreateDirectory(tempWorkDir);
        string mixWavPath = Path.Combine(tempWorkDir, $"mix_{Guid.NewGuid():N}.wav");
        string partPath = Path.Combine(target.Folder, $".{target.FileName}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.part");
        try
        {
            Report(AudioExportStage.Mix, 0);
            double duration = Math.Max(1.0, MixRenderer.MixDurationSec(job.MixTracks, job.SampleRate));
            var (mixL, mixR) = await Task.Run(() => MixRenderer.Render(job.MixTracks, job.MixBus, duration, job.SampleRate,
                f => Report(AudioExportStage.Mix, f), ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (format == AudioFormat.Wav)
            {
                Report(AudioExportStage.Finalize, 0);
                await Task.Run(() => WritePcm24StereoWav(partPath, mixL, mixR, job.SampleRate), ct).ConfigureAwait(false);
            }
            else
            {
                Report(AudioExportStage.Encode, 0);
                await Task.Run(() => Mp4Exporter.WriteFloatStereoWav(mixWavPath, mixL, mixR, job.SampleRate), ct).ConfigureAwait(false);
                await EncodeMp3(mixWavPath, partPath, duration, f => Report(AudioExportStage.Encode, f), ct).ConfigureAwait(false);
                Report(AudioExportStage.Finalize, 0);
            }
            ct.ThrowIfCancellationRequested();

            string outFilename = $"{target.FileName}{ext}";
            try
            {
                File.Move(partPath, target.FullPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ExportException($"No se pudo reemplazar {outFilename}: puede que esté abierto en otro programa.");
            }
            long bytes = File.Exists(target.FullPath) ? new FileInfo(target.FullPath).Length : 0;
            string rel = Path.GetRelativePath(Path.GetFullPath(job.ProjectDir), target.FullPath);
            string loggedFile = !string.IsNullOrEmpty(rel) && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)
                ? rel.Replace('\\', '/')
                : target.FullPath;
            return new ExportResult(outFilename, loggedFile, target.FullPath, target.Folder, target.IsDefault, bytes, duration, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            Mp4Exporter.TryDelete(partPath);
            Mp4Exporter.TryDelete(mixWavPath);
        }
    }

    /// <summary>24-bit PCM, what every editor, phone and DAW opens. The mix comes out of the master limiter, so clamping only guards rounding.</summary>
    static void WritePcm24StereoWav(string path, float[] left, float[] right, int sampleRate)
    {
        int n = Math.Min(left.Length, right.Length);
        long data = (long)n * 6;
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, data + 36));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), sampleRate * 6);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 6);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 24);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)Math.Min(uint.MaxValue, data));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        fs.Write(h);
        byte[] buf = new byte[8192 * 6];
        for (int i = 0; i < n;)
        {
            int chunk = Math.Min(8192, n - i);
            for (int j = 0; j < chunk; j++)
            {
                Put(buf, j * 6, left[i + j]);
                Put(buf, j * 6 + 3, right[i + j]);
            }
            fs.Write(buf, 0, chunk * 6);
            i += chunk;
        }
        fs.Flush(true);

        static void Put(byte[] b, int at, float v)
        {
            int s = (int)Math.Round(Math.Clamp(v, -1f, 1f) * 8388607f);
            b[at] = (byte)s;
            b[at + 1] = (byte)(s >> 8);
            b[at + 2] = (byte)(s >> 16);
        }
    }

    static async Task EncodeMp3(string wav, string part, double durationSec, Action<double> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg", ["-y", "-i", wav, "-c:a", "libmp3lame", "-b:a", "320k", "-ar", "48000",
            "-progress", "pipe:1", "-nostats", "-f", "mp3", part])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) throw new ExportException("No se pudo iniciar FFmpeg.");
        }
        catch (Win32Exception ex)
        {
            throw new ExportException(ex.NativeErrorCode == 2 ? "No se encontró FFmpeg en el PATH." : $"No se pudo iniciar FFmpeg: {ex.Message}");
        }
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 20000) stderr.Remove(0, stderr.Length - 20000);
            }
        };
        proc.BeginErrorReadLine();
        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { }
        });
        while (await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
            if (double.TryParse(line[12..], NumberStyles.Float, CultureInfo.InvariantCulture, out double us) && us > 0)
                progress(Math.Clamp(us / 1e6 / durationSec, 0, 1));
        }
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
        {
            string s;
            lock (stderr) s = stderr.ToString().Trim();
            throw new ExportException("Error al convertir a MP3 con FFmpeg", s.Length > 1200 ? s[^1200..] : s);
        }
    }
}
