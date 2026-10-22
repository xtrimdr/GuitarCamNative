using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace GuitarCam.App.Controls;

/// <summary>TakeInspector's ui.tsx Slider: label, numeric value, stepped range and double-click reset.</summary>
public sealed class InspectorSlider : StackPanel
{
    readonly TextBlock value;
    readonly Func<double, string> format;
    readonly Action<double> change;
    bool filling;
    public Slider Range { get; }
    public TextBlock Label { get; }
    public double ResetValue { get; set; }
    public StackPanel Trailing { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 4 };

    public InspectorSlider(string label, double min, double max, double step, double reset,
        Func<double, string> format, Action<double> change, string? hint = null)
    {
        this.format = format;
        this.change = change;
        ResetValue = reset;
        Spacing = 4;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new TextBlock { Text = label, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
        Label = name;
        ToolTip.SetTip(name, hint);
        row.Children.Add(name);
        value = new TextBlock { Classes = { "mono" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 1);
        ToolTip.SetTip(value, "Doble click para resetear");
        value.PointerPressed += (_, e) => { if (e.ClickCount == 2) change(ResetValue); };
        row.Children.Add(value);
        Children.Add(row);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Range = new InspectorRange { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true,
            SmallChange = step };
        AutomationProperties.SetName(Range, label);
        Range.ValueChanged += (_, e) => { if (!filling) this.change(e.NewValue); };
        bottom.Children.Add(Range);
        Grid.SetColumn(Trailing, 1);
        bottom.Children.Add(Trailing);
        Children.Add(bottom);
    }

    public void Set(double v, double? max = null)
    {
        filling = true;
        if (max is { } m) Range.Maximum = Math.Max(Range.Minimum + 0.01, m);
        Range.Value = v;
        Range.InvalidateVisual();
        value.Text = format(v);
        filling = false;
    }
}
