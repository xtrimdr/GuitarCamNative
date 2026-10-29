using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// The top bar's palette button and its popover, port of ThemePicker.tsx: accent presets plus "Color a elección",
/// the surface palettes and "Restablecer", applied live and remembered (<see cref="UiTheme"/>). Same as the web
/// except: "Color a elección" opens the Windows color dialog (the web's &lt;input type="color"&gt;), so the color
/// applies when you accept it instead of while you drag; the swatches don't grow on hover and the popover has
/// no shadow (no decorative effects in the native app); eight extra accent presets and three extra dark
/// surface palettes are available in the same grids.
/// </summary>
public sealed class ThemePicker : Panel
{
    readonly Button button;
    readonly Popup popup;
    readonly UniformGrid accents = new() { Columns = 5 };
    readonly UniformGrid surfaces = new() { Columns = 3 };

    public ThemePicker()
    {
        button = new Button { Classes = { "ghost" }, Content = new TextBlock { Classes = { "icon" }, Text = "" } };
        ToolTip.SetTip(button, Loc.T("theme.title"));
        Avalonia.Automation.AutomationProperties.SetName(button, Loc.T("theme.title"));
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Group(Loc.T("theme.accent"), accents));
        body.Children.Add(Group(Loc.T("theme.surface"), surfaces));
        var reset = new Button
        {
            Classes = { "ghost" }, Content = "Restablecer", FontSize = 11, Height = 28, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(reset, Loc.T("color.reset"));
        reset.Click += (_, _) => Pick(UiTheme.Default);
        body.Children.Add(reset);
        var card = new Border
        {
            Width = 256, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
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
        popup.Opened += (_, _) => { Build(); card.Focus(); };
        popup.Closed += (_, _) => button.Classes.Set("on", false);
        button.Click += (_, _) =>
        {
            popup.IsOpen = !popup.IsOpen;
            button.Classes.Set("on", popup.IsOpen);
        };
        Children.Add(button);
        Children.Add(popup);
        UiTheme.Changed += () => { if (popup.IsOpen) Build(); };
    }

    static StackPanel Group(string title, Control content)
    {
        var g = new StackPanel { Spacing = 8 };
        g.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, LetterSpacing = 0.6, Foreground = Palette.Faint });
        g.Children.Add(new Border { Margin = new Thickness(-4), Child = content });
        return g;
    }

    static void Pick(UiTheme.Settings t) => UiTheme.Apply(t);

    static Button Swatch(Control content, string tip, double margin = 4)
    {
        var b = new Button { Classes = { "swatch" }, Content = content, Margin = new Thickness(margin), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    /// <summary>
    /// The ring-2 ring-fg ring-offset-2 of the selected swatch: a 2 px outline 2 px away. It takes the swatch's
    /// margin (its button gets none) instead of hanging outside it, as the button's presenter clips its content.
    /// </summary>
    static Border Ring(bool on, Control inner) => new()
    {
        BorderBrush = on ? Palette.Fg : Brushes.Transparent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(2), Child = inner,
    };

    void Build()
    {
        var current = UiTheme.Current;
        accents.Children.Clear();
        foreach (var (name, hex) in UiTheme.Accents)
        {
            bool on = current.Accent == hex;
            var fill = new Border { Height = 32, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse(hex)) };
            if (on) fill.Child = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xb3, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center };
            var b = Swatch(Ring(on, fill), Loc.Or("theme.accent." + name, name), 0);
            string pick = hex;
            b.Click += (_, _) => Pick(UiTheme.Current with { Accent = pick });
            accents.Children.Add(b);
        }
        bool preset = UiTheme.Accents.Any(a => a.Hex == current.Accent);
        var custom = new Border
        {
            Height = 32, CornerRadius = new CornerRadius(6),
            Background = preset
                ? new ConicGradientBrush
                {
                    GradientStops = { new(Color.Parse("#f87171"), 0), new(Color.Parse("#facc15"), 1 / 6.0), new(Color.Parse("#4ade80"), 2 / 6.0),
                        new(Color.Parse("#22d3ee"), 3 / 6.0), new(Color.Parse("#818cf8"), 4 / 6.0), new(Color.Parse("#f472b6"), 5 / 6.0), new(Color.Parse("#f87171"), 1) },
                }
                : new SolidColorBrush(Color.Parse(current.Accent)),
        };
        var choose = Swatch(Ring(!preset, custom), Loc.T("theme.custom"), 0);
        choose.Click += (_, _) =>
        {
            if (ColorDialog.Show(TopLevel.GetTopLevel(this), Color.Parse(UiTheme.Current.Accent)) is { } c)
                Pick(UiTheme.Current with { Accent = $"#{c.R:x2}{c.G:x2}{c.B:x2}" });
            popup.IsOpen = true;
        };
        accents.Children.Add(choose);

        surfaces.Children.Clear();
        foreach (var s in UiTheme.Surfaces)
        {
            var bars = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
            bars.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(s.Panel) });
            var second = new Border { Height = 8, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(s.Raised) };
            Grid.SetColumn(second, 2);
            bars.Children.Add(second);
            var stack = new DockPanel();
            DockPanel.SetDock(bars, Dock.Top);
            stack.Children.Add(bars);
            stack.Children.Add(new TextBlock { Text = Loc.Or("theme.surface." + s.Id, s.Name), FontSize = 10, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(s.Dim), VerticalAlignment = VerticalAlignment.Bottom });
            var tile = new Border
            {
                Height = 48, CornerRadius = new CornerRadius(6), Padding = new Thickness(6), BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(s.App), BorderBrush = new SolidColorBrush(current.Surface == s.Id ? Color.Parse(current.Accent) : s.Line), Child = stack,
            };
            var b = Swatch(tile, Loc.Or("theme.surface." + s.Id, s.Name));
            string id = s.Id;
            b.Click += (_, _) => Pick(UiTheme.Current with { Surface = id });
            surfaces.Children.Add(b);
        }
    }

    /// <summary>The Windows color dialog (ChooseColor), modal to the window.</summary>
    static class ColorDialog
    {
        static readonly int[] Custom = new int[16];

        public static Color? Show(TopLevel? owner, Color initial)
        {
            var handle = GCHandle.Alloc(Custom, GCHandleType.Pinned);
            try
            {
                var cc = new ChooseColorStruct
                {
                    Size = Marshal.SizeOf<ChooseColorStruct>(),
                    Owner = owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
                    Rgb = initial.R | initial.G << 8 | initial.B << 16,
                    CustColors = handle.AddrOfPinnedObject(),
                    Flags = RgbInit | FullOpen,
                };
                if (!ChooseColor(ref cc)) return null;
                return Color.FromRgb((byte)(cc.Rgb & 0xff), (byte)(cc.Rgb >> 8 & 0xff), (byte)(cc.Rgb >> 16 & 0xff));
            }
            finally { handle.Free(); }
        }

        const int RgbInit = 0x1, FullOpen = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        struct ChooseColorStruct
        {
            public int Size;
            public IntPtr Owner, Instance;
            public int Rgb;
            public IntPtr CustColors;
            public int Flags;
            public IntPtr CustData, Hook, TemplateName;
        }

        [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")] static extern bool ChooseColor(ref ChooseColorStruct cc);
    }
}
