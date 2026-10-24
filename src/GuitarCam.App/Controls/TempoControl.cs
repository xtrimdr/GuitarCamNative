using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>Port of TempoControl.tsx: separate integer/decimal drags, wheel, typing and an eight-tap
/// average. Avalonia captures the pointer and uses the platform text editor; BPM stays in tenths.</summary>
public sealed class TempoControl : Border
{
    const double PixelsPerStep = 4;
    const double TapResetMs = 2000;
    readonly TextBlock integer = new() { Classes = { "mono" }, FontSize = 14 };
    readonly TextBlock fraction = new() { Classes = { "mono", "dim" }, FontSize = 14 };
    readonly StackPanel readout = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBox editor = new() { Width = 52, MinHeight = 0, Height = 20, FontSize = 14, Classes = { "mono" }, Padding = new Thickness(2, 0), IsVisible = false };
    readonly List<double> taps = [];
    IPointer? pointer;
    double bpm = 120, startBpm, startY, step;
    bool editing;
    public event Action<double>? Changed;

    public TempoControl()
    {
        Height = 32; CornerRadius = new CornerRadius(6); BorderThickness = new Thickness(1);
        BorderBrush = Palette.Line; Background = Palette.App; ClipToBounds = true;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        // value over a small label, like the cells of the metronome panel (Studio One style)
        var value = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        top.Children.Add(new TextBlock { Text = "♩ =", FontSize = 14, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        value.Children.Add(top);
        value.Children.Add(new TextBlock { Text = "Tempo", FontSize = 10, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        readout.Width = 44;
        readout.Children.Add(integer);
        readout.Children.Add(new TextBlock { Text = ".", Foreground = Palette.Faint, Classes = { "mono" }, FontSize = 14 });
        readout.Children.Add(fraction);
        var holder = new Grid(); holder.Children.Add(readout); holder.Children.Add(editor); top.Children.Add(holder);
        ToolTip.SetTip(value, "Arrastrá arriba/abajo (entero o decimal) · rueda del mouse (Shift = fino) · doble click para escribir");
        AutomationProperties.SetName(editor, "Tempo en BPM");
        void Part(TextBlock part, double increment)
        {
            part.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            part.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(part).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                if (e.ClickCount == 2) { EndDrag(); BeginEdit(); return; }
                startY = e.GetPosition(this).Y; startBpm = bpm; step = increment;
                pointer = e.Pointer; pointer.Capture(part); part.Foreground = Palette.Accent;
            };
            part.PointerMoved += (_, e) =>
            {
                if (pointer != null) Change(startBpm + Math.Floor((startY - e.GetPosition(this).Y) / PixelsPerStep + .5) * step);
            };
            part.PointerReleased += (_, _) => EndDrag();
            part.PointerCaptureLost += (_, _) => { pointer = null; integer.Foreground = Palette.Fg; fraction.Foreground = Palette.Dim; };
        }
        Part(integer, 1); Part(fraction, .1);
        value.PointerWheelChanged += (_, e) => { Change(bpm + Math.Sign(e.Delta.Y) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? .1 : 1)); e.Handled = true; };
        editor.LostFocus += (_, _) => Commit();
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { FinishEdit(); e.Handled = true; }
        };
        var tap = new Button { Content = "TAP", Classes = { "ghost" }, FontSize = 11, FontWeight = FontWeight.SemiBold, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(0), VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(tap, Shortcuts.Tip("Tocá al ritmo para medir el tempo", "tap"));
        Shortcuts.Changed += () => ToolTip.SetTip(tap, Shortcuts.Tip("Tocá al ritmo para medir el tempo", "tap"));
        tap.Click += (_, _) => Tap();
        row.Children.Add(value); Grid.SetColumn(tap, 1); row.Children.Add(tap); Child = row;
        Set(bpm);
    }

    void EndDrag()
    {
        var p = pointer; pointer = null; p?.Capture(null);
        integer.Foreground = Palette.Fg; fraction.Foreground = Palette.Dim;
    }
    void BeginEdit()
    {
        editing = true; editor.Text = bpm.ToString("0.0", CultureInfo.InvariantCulture);
        readout.IsVisible = false; editor.IsVisible = true; editor.Focus(); editor.SelectAll();
    }
    void FinishEdit() { editing = false; editor.IsVisible = false; readout.IsVisible = true; }
    void Commit()
    {
        if (!editing) return;
        string text = editor.Text ?? "";
        FinishEdit();
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)) Change(v);
    }
    void Change(double v) => Changed?.Invoke(Math.Round(Math.Clamp(v, 40, 300), 1, MidpointRounding.AwayFromZero));
    public void Set(double value)
    {
        bpm = value;
        string[] parts = value.ToString("0.0", CultureInfo.InvariantCulture).Split('.');
        integer.Text = parts[0]; fraction.Text = parts[1];
    }
    public void Tap()
    {
        double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        if (taps.Count > 0 && now - taps[^1] > TapResetMs) taps.Clear();
        taps.Add(now);
        if (taps.Count > 8) taps.RemoveAt(0);
        if (taps.Count > 1) Change(Math.Floor(60000 * (taps.Count - 1) / (now - taps[0]) + .5));
    }
}
