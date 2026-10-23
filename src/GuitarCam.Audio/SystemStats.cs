using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GuitarCam.Audio;

/// <summary>GPU load and video memory, as the web app's sampler reports them.</summary>
public sealed record GpuStats(double Load, long VramUsed, long VramTotal, string Name);

/// <summary>One reading of the whole PC: CPU %, threads, RAM and the GPU (null while it is still being measured).</summary>
public sealed record SystemStatsSample(double Cpu, int Cores, long RamUsed, long RamTotal, GpuStats? Gpu);

/// <summary>
/// Port of GuitarCam/server/systemStats.js: CPU and RAM straight from the system, GPU from the Windows
/// "GPU Engine" / "GPU Adapter Memory" performance counters. Task Manager's GPU % is the busiest engine, where an
/// engine's load is the sum over the processes using it; the VRAM total comes from the driver's registry key.
/// Same as the web except it reads the counters through PDH on a background thread instead of a PowerShell
/// process, and the sampler stops when nobody has asked for <see cref="IdleStop"/> (the web's IDLE_MS).
/// </summary>
public sealed class SystemStats : IDisposable
{
    /// <summary>The GPU sampler stops after this long without a <see cref="Read"/>.</summary>
    static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(15);
    /// <summary>The counters need a second between collections to give a rate (Get-Counter -SampleInterval 1).</summary>
    static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1);

    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    Thread? sampler;
    long lastRead;
    volatile GpuStats? gpu;
    bool gpuUnavailable = !OperatingSystem.IsWindows();
    ulong lastIdle, lastTotal;

    public SystemStats()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            lastIdle = idle;
            lastTotal = kernel + user;
        }
    }

    public SystemStatsSample Read()
    {
        Interlocked.Exchange(ref lastRead, Stopwatch.GetTimestamp());
        StartSampler();

        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // kernel time includes idle time
            ulong total = kernel + user;
            ulong dTotal = total - lastTotal, dIdle = idle - lastIdle;
            lastIdle = idle;
            lastTotal = total;
            if (dTotal > 0) cpu = Math.Clamp((1 - dIdle / (double)dTotal) * 100, 0, 100);
        }
        var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref mem);
        return new SystemStatsSample(cpu, Environment.ProcessorCount, (long)(mem.TotalPhys - mem.AvailPhys), (long)mem.TotalPhys,
            gpuUnavailable ? null : gpu);
    }

    void StartSampler()
    {
        lock (gate)
        {
            if (sampler != null || gpuUnavailable || stop.IsCancellationRequested) return;
            sampler = new Thread(Sample) { IsBackground = true, Name = "GPU stats", Priority = ThreadPriority.BelowNormal };
            sampler.Start();
        }
    }

    void Sample()
    {
        var (vramTotal, name) = VideoCard();
        IntPtr query = IntPtr.Zero;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0
                || PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var engines) != 0
                || PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out var memory) != 0)
            {
                gpuUnavailable = true;
                return;
            }
            PdhCollectQueryData(query);
            var busy = new Dictionary<string, double>();
            while (!stop.Token.WaitHandle.WaitOne(SampleEvery))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRead)) > IdleStop) break;
                if (PdhCollectQueryData(query) != 0) continue;
                busy.Clear();
                foreach (var (instance, value) in Values(engines))
                {
                    var m = EngineId.Match(instance);
                    if (m.Success) busy[m.Groups[1].Value] = busy.GetValueOrDefault(m.Groups[1].Value) + value;
                }
                double load = busy.Count == 0 ? 0 : busy.Values.Max();
                long used = 0;
                foreach (var (_, value) in Values(memory)) used = Math.Max(used, (long)value);
                gpu = new GpuStats(Math.Min(100, load), used, vramTotal, name);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { gpuUnavailable = true; }
        finally
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            gpu = null;
            lock (gate) sampler = null;
        }
    }

    static readonly Regex EngineId = new(@"(luid_\w+?_phys_\d+_eng_\d+)_engtype_", RegexOptions.IgnoreCase);

    static List<(string Instance, double Value)> Values(IntPtr counter)
    {
        var list = new List<(string, double)>();
        uint size = 0, count;
        if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out count, IntPtr.Zero) != PdhMoreData || size == 0) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out count, buf) != 0) return list;
            int stride = Marshal.SizeOf<PdhItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhItem>(buf + i * stride);
                if (item.Status != 0) continue;
                list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    /// <summary>The biggest adapter in the display class key (Win32_VideoController.AdapterRAM is capped at 4 GB).</summary>
    static (long Total, string Name) VideoCard()
    {
        long total = 0;
        string name = "";
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return (0, "");
            foreach (string sub in cls.GetSubKeyNames())
            {
                if (!sub.StartsWith('0')) continue;
                using var key = cls.OpenSubKey(sub);
                long mem = key?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b),
                    _ => 0,
                };
                if (mem > total) { total = mem; name = key?.GetValue("DriverDesc") as string ?? ""; }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return (total, name);
    }

    public void Dispose() => stop.Cancel();

    const uint PdhFmtDouble = 0x00000200;
    const uint PdhMoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct PdhItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>Whether FFmpeg is on the PATH and its version, like the web server's `ffmpeg -version` check.</summary>
public static class FfmpegInfo
{
    /// <summary>The version ("7.1", "N-118…"), "" if it ran without printing one, or null if it is not on the PATH.</summary>
    public static string? Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.StandardError.ReadToEndAsync();
            string first = p.StandardOutput.ReadLine() ?? "";
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } }
            var m = Regex.Match(first, @"ffmpeg version (\S+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
