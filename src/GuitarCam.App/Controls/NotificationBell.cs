using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;

namespace GuitarCam.App.Controls;

/// <summary>
/// The top bar's bell and its popover (native only; the web shows these messages in a toast). Every message the
/// app used to write in the title bar goes here instead: <see cref="Add"/> keeps the last <see cref="MaxKept"/>,
/// newest first, a dot on the bell says there are unread ones, and opening the popover marks them read.
/// </summary>
public sealed class NotificationBell : Panel
{
    const int MaxKept = 50;

    readonly Button button;
    readonly Popup popup;
    readonly Ellipse dot;
    readonly StackPanel list = new() { Spacing = 2 };
    readonly List<(DateTime At, string Text)> items = [];

    public NotificationBell()
    {
        dot = new Ellipse
        {
            Width = 7, Height = 7, Fill = Palette.Accent, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0),
            IsHitTestVisible = false,
        };
        button = new Button { Classes = { "ghost" }, Content = new TextBlock { Classes = { "icon" }, Text = "" } };
        ToolTip.SetTip(button, "Avisos");
        Avalonia.Automation.AutomationProperties.SetName(button, "Avisos");

        var clear = new Button
        {
            Classes = { "ghost" }, Content = "Borrar todo", FontSize = 11, Height = 24, Padding = new Thickness(6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        clear.Click += (_, _) => { items.Clear(); Build(); };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(new TextBlock { Text = "AVISOS", FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(new ScrollViewer { MaxHeight = 360, Content = list });
        var card = new Border
        {
            Width = 340, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
            Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), Child = body,
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            popup!.IsOpen = false;
            e.Handled = true;
        };
        popup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.BottomEdgeAlignedRight, VerticalOffset = 8,
            IsLightDismissEnabled = true, Child = card,
        };
        popup.Opened += (_, _) => { SetUnread(false); Build(); card.Focus(); };
        popup.Closed += (_, _) => button.Classes.Set("on", false);
        button.Click += (_, _) =>
        {
            popup.IsOpen = !popup.IsOpen;
            button.Classes.Set("on", popup.IsOpen);
        };
        Children.Add(button);
        Children.Add(dot);
        Children.Add(popup);
    }

    /// <summary>Adds a message (UI thread). Empty text is ignored: it used to mean "clear the title bar".</summary>
    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        items.Insert(0, (DateTime.Now, text));
        if (items.Count > MaxKept) items.RemoveAt(items.Count - 1);
        if (popup.IsOpen) Build();
        else SetUnread(true);
    }

    void SetUnread(bool on)
    {
        dot.IsVisible = on;
        ToolTip.SetTip(button, on ? "Avisos (hay nuevos)" : "Avisos");
    }

    void Build()
    {
        list.Children.Clear();
        if (items.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "No hay avisos.", FontSize = 12, Foreground = Palette.Faint, Margin = new Thickness(0, 4) });
            return;
        }
        foreach (var (at, text) in items)
        {
            var row = new StackPanel { Spacing = 1, Margin = new Thickness(0, 4) };
            row.Children.Add(new TextBlock { Text = at.ToString("HH:mm:ss"), FontSize = 10.5, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Foreground = Palette.Faint });
            row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap });
            list.Children.Add(row);
            list.Children.Add(new Border { Height = 1, Background = Palette.Line });
        }
    }
}
