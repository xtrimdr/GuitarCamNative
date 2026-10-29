using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WildTrack.App.Controls;

/// <summary>
/// One area of the Canal tab that holds a single kind of section, the effect slots or the output and sends, apart from
/// the rest (asked by the user: the effects and the sends read as one list). Full width and darker than the tab, with
/// its title, how many it holds and its "+" on top; its sections go in <see cref="Body"/> as cards (<see cref="Card"/>),
/// so they never look like the flat sections around (Mezcla, Transponer, Clip…). Folds from its title row like a
/// section (asked by the user); the count and the "+" stay at hand while folded.
/// </summary>
public sealed class InspectorTray : Border
{
    /// <summary>Space between the cards; the slot drag slides them by a card's height plus this.</summary>
    public const double Gap = 6;
    /// <summary>The inspector's side margin, which the tray spans like the flat sections do.</summary>
    const double Bleed = 14;

    readonly TextBlock count = new() { Classes = { "mono" }, FontSize = 10.5, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly Decorator add = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly SectionHeader header;
    readonly StackPanel inside;
    readonly TextBlock empty;

    public StackPanel Body { get; }
    /// <summary>Folded: only its title row (with the count and the "+") shows.</summary>
    public bool Collapsed { get; private set; }
    /// <summary>A click on its title row asks to fold or unfold it; the window decides and remembers it.</summary>
    public event Action? Toggled;

    public InspectorTray(string title, string emptyText, StackPanel body)
    {
        Body = body;
        body.Spacing = Gap;
        Background = Palette.App;
        BorderBrush = Palette.Line;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Margin = new Thickness(-Bleed, 0);
        Padding = new Thickness(8, 6);
        // the inspector's own foldable heading, slimmer: the tray folds like any section
        header = new SectionHeader(title, "") { Padding = new Thickness(8, 4, 0, 4), MinHeight = 34, CornerRadius = new CornerRadius(6) };
        header.Icon.IsVisible = false;
        header.SetTrailing(new StackPanel { Orientation = Orientation.Horizontal, Children = { count, add } }, null);
        header.Toggled += () => Toggled?.Invoke();
        empty = new TextBlock { Text = emptyText, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 8, 2) };
        inside = new StackPanel { Margin = new Thickness(0, 6, 0, 4), Children = { body, empty } };
        Child = new StackPanel { Children = { header, inside } };
        SetCollapsed(false);
    }

    public void SetCollapsed(bool collapsed)
    {
        Collapsed = collapsed;
        inside.IsVisible = !collapsed;
        header.SetCollapsed(collapsed);
    }

    /// <summary>After a rebuild: how many sections it holds (the empty note when none) and its "+" (none = nothing to add).</summary>
    public void Set(int n, Button? plus)
    {
        count.Text = n > 0 ? n.ToString() : "";
        empty.IsVisible = n == 0;
        if (plus != null)
        {
            plus.HorizontalAlignment = HorizontalAlignment.Right;
            plus.MinHeight = plus.Height = 24;
            plus.Padding = new Thickness(8, 0);
        }
        add.Child = plus;
    }

    /// <summary>Makes an inspector section one of the tray's cards: its own rounded box instead of a full-width band.</summary>
    public static void Card(Border section)
    {
        section.Margin = new Thickness(0);
        section.BorderThickness = new Thickness(1);
        section.BorderBrush = Palette.Line;
        section.CornerRadius = new CornerRadius(8);
        section.ClipToBounds = true;
    }
}
