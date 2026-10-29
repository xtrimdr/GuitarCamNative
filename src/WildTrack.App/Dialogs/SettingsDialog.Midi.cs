using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WildTrack.App.Controls;
using WildTrack.Audio.Midi;
using WildTrack.Projects;

namespace WildTrack.App.Dialogs;

/// <summary>
/// "MIDI" (native only, stage 4 of the MIDI editor): the keyboards, controllers and pedals Windows sees, plugged in or
/// remembered (<see cref="MidiSetup"/>). Each one: on or off, a name of its own, the channel it listens to, how its
/// velocity is taken, what it is for (notes, knobs, transport: boxes, the user's call) and a light that blinks with
/// what arrives and says what it was, so it can be checked without opening anything else. Below, whether recorded notes
/// go to the piano roll's magnet on their own. Rebuilt on <see cref="MidiSetup.Changed"/> while shown; the light is
/// a timer that only touches what changed.
/// </summary>
public sealed partial class SettingsDialog
{
    readonly StackPanel midiPage = new();
    readonly StackPanel midiRows = new() { Spacing = 8 };
    readonly ToggleSwitch quantizeSwitch = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock quantizeHelp = Help("");
    readonly List<(MidiInputs.Port Port, Ellipse Light, TextBlock Last)> midiLights = [];
    readonly int[] midiShown = new int[64];
    DispatcherTimer? midiTimer;
    /// <summary>How long the light stays on after a message: long enough to see a single key.</summary>
    static readonly TimeSpan LightOn = TimeSpan.FromMilliseconds(160);

    void BuildMidi()
    {
        var rescan = Act("", Loc.T("set.midi.rescan"), MidiSetup.Rescan);
        var inputs = Section(Loc.T("set.midi.inputs"), rescan);
        inputs.Children.Add(Help(Loc.T("set.midi.inputs.help")));
        inputs.Children.Add(midiRows);
        midiPage.Children.Add(Card(inputs));

        var rec = Section(Loc.T("set.midi.recording"));
        var label = new TextBlock { Text = Loc.T("set.midi.quantize"), FontSize = 12, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        quantizeSwitch.IsCheckedChanged += (_, _) =>
        {
            if (filling || MidiSetup.Settings.QuantizeOnRecord == (quantizeSwitch.IsChecked == true)) return;
            MidiSetup.Settings.QuantizeOnRecord = quantizeSwitch.IsChecked == true;
            MidiSetup.Save();
        };
        rec.Children.Add(Row(label, quantizeSwitch));
        rec.Children.Add(quantizeHelp);
        midiPage.Children.Add(Card(rec));

        MidiSetup.Changed += () => { if (IsVisible && Body.Content == midiPage) RefreshMidi(); };
    }

    void RefreshMidi()
    {
        filling = true;
        quantizeSwitch.IsChecked = MidiSetup.Settings.QuantizeOnRecord;
        string snap = AppConfig.Get("pianoSnap") ?? "1/16";
        quantizeHelp.Text = snap == "off" ? Loc.T("set.midi.quantize.free") : Loc.T("set.midi.quantize.help", snap);
        filling = false;
        FillMidiRows();
        midiTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => TickMidi());
        midiTimer.Start();
    }

    void StopMidi() => midiTimer?.Stop();

    void FillMidiRows()
    {
        midiRows.Children.Clear();
        midiLights.Clear();
        Array.Clear(midiShown);
        var ports = MidiSetup.Inputs.Ports;
        foreach (var port in ports) midiRows.Children.Add(MidiRow(port));
        var gone = MidiSetup.Settings.Inputs.Where(s => ports.All(p => p.Device.Name != s.Name)).ToList();
        foreach (var s in gone) midiRows.Children.Add(MidiGoneRow(s));
        if (ports.Count == 0 && gone.Count == 0)
            midiRows.Children.Add(new Border
            {
                Background = Palette.Raised, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12),
                Child = new TextBlock { Text = Loc.T("set.midi.none"), FontSize = 12, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap },
            });
    }

    Control MidiRow(MidiInputs.Port port)
    {
        var s = port.Settings;
        var light = new Ellipse { Width = 8, Height = 8, Fill = Palette.Hover, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(light, Loc.T("set.midi.light.tip"));
        var alias = new TextBox
        {
            Classes = { "field" }, Height = 30, FontSize = 13, Text = s.Alias ?? "", Watermark = port.Device.Name,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(alias, Loc.T("set.midi.alias.tip"));
        void SaveAlias()
        {
            string? text = string.IsNullOrWhiteSpace(alias.Text) ? null : alias.Text.Trim();
            if (text == s.Alias) return;
            s.Alias = text;
            MidiSetup.Save();
        }
        alias.LostFocus += (_, _) => SaveAlias();
        alias.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveAlias(); Focus(); e.Handled = true; } };
        var on = new ToggleSwitch { IsChecked = s.Enabled, VerticalAlignment = VerticalAlignment.Center, OnContent = null, OffContent = null };
        ToolTip.SetTip(on, Loc.T("set.midi.enabled.tip"));
        on.IsCheckedChanged += (_, _) =>
        {
            if (s.Enabled == (on.IsChecked == true)) return;
            s.Enabled = on.IsChecked == true;
            MidiSetup.Save();
            MidiSetup.Rescan();
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        head.Children.Add(light);
        Grid.SetColumn(alias, 1);
        head.Children.Add(alias);
        Grid.SetColumn(on, 2);
        head.Children.Add(on);

        string state = port.Open ? Loc.T("set.midi.open") : port.Error ?? Loc.T("set.midi.off");
        var info = new TextBlock
        {
            Text = s.Alias != null ? $"{port.Device.Name} · {state}" : state, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = port.Error != null ? Palette.Accent : Palette.Faint,
        };
        var last = new TextBlock { Text = Loc.T("set.midi.last.none"), FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" }, TextTrimming = TextTrimming.CharacterEllipsis };

        var box = new StackPanel { Spacing = 10 };
        box.Children.Add(head);
        box.Children.Add(Row(info, last));
        var details = new StackPanel { Spacing = 10, Opacity = s.Enabled ? 1 : 0.4 };

        // the channel: all, or one of the 16
        var channel = Combo();
        channel.Height = 30;
        channel.FontSize = 12;
        channel.ItemsSource = new[] { Loc.T("set.midi.channel.all") }.Concat(Enumerable.Range(1, 16).Select(c => c.ToString())).ToList();
        channel.SelectedIndex = s.Channel;
        channel.SelectionChanged += (_, _) =>
        {
            if (channel.SelectedIndex < 0 || channel.SelectedIndex == s.Channel) return;
            s.Channel = channel.SelectedIndex;
            MidiSetup.Save();
        };
        ToolTip.SetTip(channel, Loc.T("set.midi.channel.tip"));

        // the velocity curve, with the fixed value when it is "Fija"
        var fixedVel = new InspectorSlider(Loc.T("set.midi.fixed"), 1, 127, 1, 100, v => $"{v:0}", v =>
        {
            s.FixedVelocity = (int)Math.Round(v);
            MidiSetup.Save();
        }) { IsVisible = s.Velocity == VelocityCurves.Fixed };
        fixedVel.Set(s.FixedVelocity);
        fixedVel.Range.ValueChanged += (_, e) => fixedVel.Set(e.NewValue);
        var curves = new UniformGrid { Columns = 4 };
        var curveButtons = new List<ToggleButton>();
        foreach (var (id, key) in new[] { (VelocityCurves.Soft, "soft"), (VelocityCurves.Normal, "normal"), (VelocityCurves.Hard, "hard"), (VelocityCurves.Fixed, "fixed") })
        {
            var b = new ToggleButton
            {
                Content = Loc.T($"set.midi.vel.{key}"), Classes = { "seg" }, IsChecked = s.Velocity == id, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            ToolTip.SetTip(b, Loc.T($"set.midi.vel.{key}.tip"));
            b.Click += (_, _) =>
            {
                s.Velocity = id;
                foreach (var o in curveButtons) o.IsChecked = o == b;
                fixedVel.IsVisible = id == VelocityCurves.Fixed;
                MidiSetup.Save();
            };
            curveButtons.Add(b);
            curves.Children.Add(b);
        }
        var settingsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 10 };
        settingsRow.Children.Add(Field(Loc.T("set.midi.channel"), channel));
        var velField = Field(Loc.T("set.midi.velocity"), new Border { Classes = { "seg" }, Child = curves });
        Grid.SetColumn(velField, 1);
        settingsRow.Children.Add(velField);
        details.Children.Add(settingsRow);
        details.Children.Add(fixedVel);

        // what it is for: boxes (a keyboard with knobs ticks two, a pedal ticks Transporte)
        var uses = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, get, set) in new (string, Func<bool>, Action<bool>)[]
        {
            ("notes", () => s.Notes, v => s.Notes = v),
            ("knobs", () => s.Knobs, v => s.Knobs = v),
            ("transport", () => s.Transport, v => s.Transport = v),
        })
        {
            var check = new CheckBox { Content = Loc.T($"set.midi.use.{key}"), IsChecked = get(), FontSize = 12, Margin = new Thickness(0, 0, 18, 0) };
            ToolTip.SetTip(check, Loc.T($"set.midi.use.{key}.tip"));
            check.IsCheckedChanged += (_, _) =>
            {
                if (get() == (check.IsChecked == true)) return;
                set(check.IsChecked == true);
                MidiSetup.Save();
            };
            uses.Children.Add(check);
        }
        details.Children.Add(Field(Loc.T("set.midi.uses"), uses));
        box.Children.Add(details);

        midiLights.Add((port, light, last));
        return new Border { Background = Palette.Raised, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12), Child = box };
    }

    /// <summary>A device that isn't plugged in: its name, "desconectado", and a button to forget it.</summary>
    Control MidiGoneRow(MidiInputSettings s)
    {
        var name = new TextBlock { Text = s.Shown, FontSize = 13, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        right.Children.Add(new TextBlock { Text = Loc.T("set.midi.unplugged"), FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(Act("", Loc.T("set.midi.forget"), () => MidiSetup.Forget(s.Name)));
        return new Border
        {
            BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 6),
            Child = Row(name, right),
        };
    }

    /// <summary>The lights: on for a moment after each message, and the last one in words when it changed.</summary>
    void TickMidi()
    {
        if (!IsVisible || Body.Content != midiPage)
        {
            StopMidi();
            return;
        }
        for (int i = 0; i < midiLights.Count && i < midiShown.Length; i++)
        {
            var (port, light, last) = midiLights[i];
            long at = port.LastTicks;
            bool lit = at != 0 && Stopwatch.GetElapsedTime(at) < LightOn;
            var fill = lit ? Palette.Accent : Palette.Hover;
            if (light.Fill != fill) light.Fill = fill;
            int msg = port.LastMessage;
            if (msg == 0 || msg == midiShown[i]) continue;
            midiShown[i] = msg;
            last.Text = MidiSetup.Describe(msg);
            last.Foreground = Palette.Dim;
        }
    }
}
