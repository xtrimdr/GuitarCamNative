using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GuitarCam.App.Controls;

/// <summary>
/// Toasts (native only, asked by the user): small cards in the bottom right corner, over everything, for a message
/// worth seeing right away instead of waiting in the <see cref="NotificationBell"/> (the window also adds it there).
/// A plain toast goes away by itself after <see cref="Life"/> (the pointer over it holds it); a sticky one, like
/// the "what to test" note of <c>--probar</c>, stays until closed. At most <see cref="MaxShown"/> at once, newest
/// at the bottom. No shadow and no animation, like the rest of the window.
/// </summary>
public sealed class ToastHost : StackPanel
{
    static readonly TimeSpan Life = TimeSpan.FromSeconds(6);
    const int MaxShown = 3;

    public ToastHost()
    {
        Spacing = 8;
        Width = 340;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
    }

    /// <summary>Shows a toast (UI thread). <paramref name="title"/> goes above as a section label.</summary>
    public void Show(string text, string? title = null, bool sticky = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Border? card = null;
        DispatcherTimer? timer = null;
        void Close()
        {
            timer?.Stop();
            Children.Remove(card!);
        }

        var body = new StackPanel { Spacing = 4 };
        if (title != null)
            body.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, Foreground = Palette.Accent });
        body.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap });

        var close = new Button
        {
            Classes = { "ghost" }, Width = 24, Height = 24, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10 },
        };
        ToolTip.SetTip(close, Loc.T("toast.close"));
        Avalonia.Automation.AutomationProperties.SetName(close, Loc.T("toast.close"));
        close.Click += (_, _) => Close();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(body);
        Grid.SetColumn(close, 1);
        row.Children.Add(close);

        card = new Border
        {
            Padding = new Thickness(12, 10, 6, 10), CornerRadius = new CornerRadius(8),
            Background = Palette.Raised, BorderBrush = sticky ? Palette.Alpha(Palette.AccentColor, 0.45) : Palette.Line,
            BorderThickness = new Thickness(1), Child = row,
            Tag = sticky ? null : (Action)Close,
        };
        if (!sticky)
        {
            timer = new DispatcherTimer { Interval = Life };
            timer.Tick += (_, _) => Close();
            card.PointerEntered += (_, _) => timer.Stop();
            card.PointerExited += (_, _) => timer.Start();
            timer.Start();
        }

        Children.Add(card);
        // the oldest plain toast makes room first; sticky ones only go when closed
        while (Children.Count > MaxShown && Children.OfType<Border>().FirstOrDefault(c => c != card && c.Tag is Action) is { Tag: Action closeOld })
            closeOld();
    }
}
