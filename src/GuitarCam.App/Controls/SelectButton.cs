using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// The mixer's small &lt;select&gt; (h-6, 11 px, Raised box): the chosen option and a chevron, the list in a flyout.
/// Avalonia's ComboBox keeps a wide drop-down column that leaves no room for the text in a 200 px strip.
/// Setting <see cref="Items"/> or <see cref="SelectedIndex"/> from code never raises <see cref="Selected"/>.
/// </summary>
public sealed class SelectButton : Button
{
    readonly TextBlock text = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    string[] items = [];
    string?[] hints = [];
    int selected = -1;
    public event Action<int>? Selected;

    public SelectButton()
    {
        Height = MinHeight = 24;
        Padding = new Thickness(6, 0, 4, 0);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        BorderBrush = Palette.Line;
        Background = Palette.Raised;
        Foreground = Palette.Fg;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        var chevron = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 8, Foreground = Palette.Dim, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        Content = grid;
        Click += (_, _) => Open();
    }

    /// <summary>Replaces the options (only when they changed); hints show as each option's tooltip.</summary>
    public void SetItems(string[] next, string?[]? nextHints = null)
    {
        hints = nextHints ?? new string?[next.Length];
        if (items.SequenceEqual(next)) return;
        items = next;
        SelectedIndex = selected;
    }

    public int SelectedIndex
    {
        get => selected;
        set { selected = value; text.Text = value >= 0 && value < items.Length ? items[value] : ""; }
    }

    void Open()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var item = new MenuItem { Header = items[i], FontSize = 12, FontWeight = i == selected ? FontWeight.SemiBold : FontWeight.Normal };
            if (hints[i] is { } hint) ToolTip.SetTip(item, hint);
            item.Click += (_, _) => { if (index != selected) Selected?.Invoke(index); };
            menu.Items.Add(item);
        }
        menu.ShowAt(this);
    }
}
