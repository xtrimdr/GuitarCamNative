using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// The heading row of a foldable section (ui.tsx Section: chevron, icon, uppercase label, then an action and a
/// power button). The whole band reacts to the pointer and folds the section on a click anywhere outside its
/// buttons, not just on the label: the user asked for a bigger target than the web's text-only heading.
/// Shared by the inspector ("Canal") and the project bar (Color / Historial).
/// </summary>
public sealed class SectionHeader : Border
{
    readonly Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
    readonly TextBlock chevron = new() { Classes = { "icon" }, Text = "", Foreground = Palette.Faint, FontSize = 10, Width = 16, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock label;
    bool hover;
    /// <summary>A Segoe glyph or an effect's <see cref="FxIcon"/>; paint it with <see cref="FxIcon.SetColor"/>.</summary>
    public Control Icon { get; }
    public event Action? Toggled;

    public SectionHeader(string title, string icon)
    {
        Icon = FxIcon.Create(icon, 12);
        FxIcon.SetColor(Icon, Palette.Accent);
        Icon.VerticalAlignment = VerticalAlignment.Center;
        Icon.Margin = new Thickness(0, 0, 8, 0);
        label = new TextBlock { Text = title.ToUpperInvariant(), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };
        Background = Brushes.Transparent;
        Padding = new Thickness(16, 14);
        MinHeight = 52;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(this, title.ToUpperInvariant());
        row.Children.Add(chevron);
        Grid.SetColumn(Icon, 1); row.Children.Add(Icon);
        Grid.SetColumn(label, 2); row.Children.Add(label);
        Child = row;
        Paint();
    }

    /// <summary>Puts the header's buttons on the right: an action (reset, mute…) and the ON/OFF power button.</summary>
    public void SetTrailing(Control? action, Control? power)
    {
        if (action != null) { Grid.SetColumn(action, 3); row.Children.Add(action); }
        if (power != null) { Grid.SetColumn(power, 4); row.Children.Add(power); }
    }

    public void SetCollapsed(bool collapsed)
    {
        chevron.Text = collapsed ? "" : "";
        ToolTip.SetTip(this, collapsed ? "Mostrar" : "Ocultar");
    }

    void Paint()
    {
        Background = hover ? Palette.Raised : Brushes.Transparent;
        label.Foreground = hover ? Palette.Fg : Palette.Dim;
        chevron.Foreground = hover ? Palette.Dim : Palette.Faint;
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); hover = true; Paint(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); hover = false; Paint(); }

    // The header's own buttons mark their press as handled, so this only sees clicks on the band itself.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        Toggled?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Source != this || e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        Toggled?.Invoke();
    }
}
