using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GuitarCam.Audio;

namespace GuitarCam.App.Controls;

/// <summary>
/// CPU / GPU / RAM load of the whole PC in the top bar, port of SystemMeter.tsx: polled every 1.5 s, a 40 px
/// bar per value that turns red from 90 %. Same as the web except it reads the system directly
/// (<see cref="SystemStats"/>) instead of asking the server, and pauses while the window is minimized.
/// </summary>
public sealed class SystemMeter : StackPanel
{
    static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(1500);

    readonly SystemStats stats = new();
    readonly DispatcherTimer timer;
    readonly (Border Fill, TextBlock Value, StackPanel Row) cpu, gpu, ram;

    public SystemMeter()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 12;
        VerticalAlignment = VerticalAlignment.Center;
        Opacity = 0; // until the first reading (the window decides IsVisible by width)
        cpu = Bar("CPU");
        gpu = Bar("GPU");
        ram = Bar("RAM");
        timer = new DispatcherTimer(PollEvery, DispatcherPriority.Background, (_, _) => Poll());
        AttachedToVisualTree += (_, _) => { Poll(); timer.Start(); };
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }

    (Border, TextBlock, StackPanel) Bar(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Background = Brushes.Transparent };
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        row.Children.Add(new Border
        {
            Width = 40, Height = 6, CornerRadius = new CornerRadius(3), Background = Palette.Raised, ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center, Child = fill,
        });
        var value = new TextBlock { Classes = { "mono" }, FontSize = 11, Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(value);
        Children.Add(row);
        return (fill, value, row);
    }

    static void Show((Border Fill, TextBlock Value, StackPanel Row) bar, double? pct, string tip)
    {
        bool hot = pct >= 90;
        bar.Fill.Width = 40 * Math.Clamp(pct ?? 0, 0, 100) / 100;
        bar.Fill.Background = hot ? Palette.Rec : Palette.Accent;
        bar.Value.Text = pct is { } p ? $"{Math.Round(p):0}%" : "–";
        bar.Value.Foreground = hot ? Palette.Rec : Palette.Dim;
        ToolTip.SetTip(bar.Row, tip);
    }

    static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0");

    void Poll()
    {
        if (!IsVisible || TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized }) return;
        var s = stats.Read();
        double ramPct = s.RamTotal > 0 ? s.RamUsed * 100.0 / s.RamTotal : 0;
        Show(cpu, s.Cpu, $"Procesador: {Math.Round(s.Cpu):0}% ({s.Cores} hilos)");
        if (s.Gpu is { } g)
        {
            string tip = $"{(g.Name.Length > 0 ? g.Name : "Placa de video")}: {Math.Round(g.Load):0}%";
            if (g.VramTotal > 0) tip += $"\nMemoria de video: {Gb(g.VramUsed)} / {Gb(g.VramTotal)} GB ({Math.Round(g.VramUsed * 100.0 / g.VramTotal):0}%)";
            Show(gpu, g.Load, tip);
        }
        else Show(gpu, null, "Midiendo la placa de video…");
        Show(ram, ramPct, $"Memoria: {Gb(s.RamUsed)} / {Gb(s.RamTotal)} GB ({Math.Round(ramPct):0}%)");
        Opacity = 1;
    }
}
