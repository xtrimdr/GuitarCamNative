using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WildTrack.Audio.Dsp;
using WildTrack.Projects;

namespace WildTrack.App.Controls;

/// <summary>Port of EqEditor.tsx: presets, interactive log graph with the track's live spectrum behind it, six band
/// chips and selected band controls. The graph uses the shared DSP response formulas.</summary>
public sealed class EqEditor : StackPanel
{
    EqSettings eq = new();
    int selected;
    bool filling;
    bool customPreset;
    readonly ComboBox preset = new() { FontSize = 11, MinHeight = 26 };
    readonly ComboBox type = new() { FontSize = 12, MinHeight = 28 };
    readonly Button power = new() { Classes = { "chip" }, FontSize = 11 };
    readonly Button[] chips = new Button[6];
    readonly EqGraph graph = new();
    readonly InspectorSlider freq, gain, q;
    readonly StackPanel slope = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    static readonly string[] Types = ["highpass", "lowshelf", "peaking", "highshelf", "lowpass"];
    public event Action<EqSettings>? Changed;

    public EqEditor()
    {
        Spacing = 10;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(new TextBlock { Text = "Plantilla", Classes = { "dim" }, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        preset.ItemsSource = TrackEq.Presets.Select(p => p.Label).ToArray();
        Grid.SetColumn(preset, 1); row.Children.Add(preset); Children.Add(row);
        preset.SelectionChanged += (_, _) =>
        {
            int index = preset.SelectedIndex - (customPreset ? 1 : 0);
            if (filling || index < 0) return;
            Publish(FromPreset(index, eq));
        };
        Children.Add(graph);
        graph.Selected += i => { selected = i; Set(eq); };
        graph.Edited += (i, edit) => Patch(i, edit);
        var bands = new UniformGrid { Columns = 6 };
        for (int i = 0; i < 6; i++)
        {
            int index = i;
            var b = new Button { Content = (i + 1).ToString(), Classes = { "chip", "mono" }, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0), MinHeight = 28 };
            b.Click += (_, _) => { selected = index; Set(eq); };
            b.DoubleTapped += (_, _) => Patch(index, x => x.On = !x.On);
            chips[i] = b; bands.Children.Add(b);
        }
        Children.Add(bands);
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        type.ItemsSource = Types.Select(t => TypeName(BiquadSpec.ParseType(t))).ToArray();
        type.SelectionChanged += (_, _) => { if (!filling && type.SelectedIndex >= 0) Patch(selected, b => { b.Type = Types[type.SelectedIndex]; b.On = true; }); };
        controls.Children.Add(type);
        power.Click += (_, _) => Patch(selected, b => b.On = !b.On);
        Grid.SetColumn(power, 1); controls.Children.Add(power);
        var reset = new Button { Content = "Reset", Classes = { "chip" }, FontSize = 11 };
        ToolTip.SetTip(reset, Loc.T("eq.reset.tip"));
        reset.Click += (_, _) => Patch(selected, b =>
        {
            var d = EqSettings.Defaults()[selected]; b.On = d.On; b.Type = d.Type; b.Freq = d.Freq; b.Gain = d.Gain; b.Q = d.Q; b.Slope = d.Slope;
        });
        Grid.SetColumn(reset, 2); controls.Children.Add(reset); Children.Add(controls);
        freq = new InspectorSlider(Loc.T("eq.freq"), 0, 1000, 1, 0, p => $"{TrackEq.FmtHz(FromPos(p))} Hz",
            p => Patch(selected, b => { b.Freq = FromPos(p); b.On = true; }));
        gain = new InspectorSlider(Loc.T("eq.gain"), -18, 18, 0.1, 0, v => $"{v:+0.0;-0.0;0.0} dB", v => Patch(selected, b => { b.Gain = v; b.On = true; }));
        q = new InspectorSlider("Ancho (Q)", 0.3, 12, 0.05, 1, v => $"{v:0.00}", v => Patch(selected, b => { b.Q = v; b.On = true; }),
            Loc.T("eq.q.tip"));
        Children.Add(freq); Children.Add(gain); Children.Add(q);
        slope.Children.Add(new TextBlock { Text = "Pendiente", Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
        foreach (int s in new[] { 12, 24 })
        {
            var b = new Button { Content = $"{s} dB/oct", Classes = { "chip" }, FontSize = 11, Padding = new Thickness(5, 2) };
            ToolTip.SetTip(b, Loc.T(s == 12 ? "eq.cut.soft" : "eq.cut.firm"));
            b.Click += (_, _) => Patch(selected, x => { x.Slope = s; x.On = true; }); slope.Children.Add(b);
        }
        Children.Add(slope);
        Children.Add(new TextBlock { Text = Loc.T("eq.help"),
            FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
    }
    /// <summary>One frame of the live spectrum (dB per bin, as <see cref="SpectrumTap.Read"/> gives it), or null while
    /// the track isn't playing, so the last one falls away.</summary>
    public void PushSpectrum(float[]? bins, int sampleRate) => graph.PushSpectrum(bins, sampleRate);
    /// <summary>eqPresetOf: the factory preset these bands are exactly, or -1 ("Personalizado").</summary>
    public static int PresetIndex(EqSettings eq) => Array.FindIndex(TrackEq.Presets, p => p.Bands.Select((x, i) =>
        x.On == eq.Bands[i].On && BiquadSpec.TypeToString(x.Type) == eq.Bands[i].Type && x.Freq == eq.Bands[i].Freq
        && x.Gain == eq.Bands[i].Gain && x.Q == eq.Bands[i].Q && x.Slope == eq.Bands[i].Slope).All(x => x));

    /// <summary>applyEqPreset: the preset's bands, switched on; unknown fields of the current EQ survive.</summary>
    public static EqSettings FromPreset(int index, EqSettings current)
    {
        var p = TrackEq.Presets[index];
        return new EqSettings { On = true, Bands = p.Bands.Select(b => new EqBand { On = b.On,
            Type = BiquadSpec.TypeToString(b.Type), Freq = b.Freq, Gain = b.Gain, Q = b.Q, Slope = b.Slope }).ToList(), Extra = current.Extra };
    }

    internal static double ToPos(double hz) => Math.Round(Math.Log(Math.Clamp(hz, 20, 20000) / 20) / Math.Log(1000) * 1000);
    internal static double FromPos(double pos) => Math.Round(20 * Math.Exp(pos / 1000 * Math.Log(1000)));
    static string TypeName(BiquadType t) => Loc.Or("eq.type." + t, TrackEq.TypeLabel(t));
    void Publish(EqSettings value) { Changed?.Invoke(value); }
    void Patch(int i, Action<EqBand> edit)
    {
        var next = System.Text.Json.JsonSerializer.Deserialize<EqSettings>(System.Text.Json.JsonSerializer.Serialize(eq, ProjectStore.Json), ProjectStore.Json)!;
        next.On = true; edit(next.Bands[i]); Publish(next);
    }
    public void Set(EqSettings value)
    {
        filling = true; eq = value;
        var b = eq.Bands[selected];
        int p = PresetIndex(eq);
        customPreset = p < 0;
        preset.ItemsSource = (customPreset ? new[] { Loc.T("mix.custom.m") } : Array.Empty<string>()).Concat(TrackEq.Presets.Select(x => Loc.Or("mix.eq." + x.Id, x.Label))).ToArray();
        preset.SelectedIndex = customPreset ? 0 : p;
        ToolTip.SetTip(preset, p < 0 ? Loc.T("mix.eq.manual") : Loc.Or("mix.eq." + TrackEq.Presets[p].Id + ".hint", TrackEq.Presets[p].Hint));
        type.SelectedIndex = Array.IndexOf(Types, b.Type);
        power.Content = b.On ? "ON" : "OFF";
        ToolTip.SetTip(power, Loc.T(b.On ? "eq.band.off" : "eq.band.on"));
        for (int i = 0; i < 6; i++)
        {
            var band = eq.Bands[i];
            chips[i].Foreground = band.On ? Palette.Accent : Palette.Faint;
            chips[i].BorderBrush = i == selected ? Palette.Fg : Palette.Line;
            ToolTip.SetTip(chips[i], Loc.T("eq.chip.tip", TypeName(BiquadSpec.ParseType(band.Type)), TrackEq.FmtHz(band.Freq), band.On ? "" : Loc.T("eq.chip.off")));
        }
        freq.ResetValue = ToPos(EqSettings.Defaults()[selected].Freq);
        freq.Set(ToPos(b.Freq)); gain.Set(b.Gain); q.Set(b.Q);
        bool cut = b.Type is "highpass" or "lowpass";
        gain.IsVisible = !cut; q.IsVisible = b.Type == "peaking"; slope.IsVisible = cut;
        foreach (var child in slope.Children.OfType<Button>()) child.Foreground = Equals(child.Content, $"{b.Slope} dB/oct") ? Palette.Accent : Palette.Dim;
        graph.Set(eq, selected);
        filling = false;
    }
}

/// <summary>EqEditor.tsx EqGraph: logarithmic response, captured point drags, wheel Q/slope and double-click bands.
/// With <see cref="Interactive"/> off (the mixer's summary) it only draws: no labels, small dots for the bands that are on.</summary>
sealed class EqGraph : Control
{
    EqSettings eq = new();
    int selected, hover = -1, dragging = -1;
    Point down;
    double freq, gain;
    IPointer? pointer;
    StreamGeometry curve = new();
    StreamGeometry totalFill = new();
    StreamGeometry selectedFill = new();
    EqBandSpec[] currentBands = [];
    public event Action<int>? Selected;
    public event Action<int, Action<EqBand>>? Edited;
    readonly bool interactive;
    float[] smooth = [];
    bool hadSpectrum;
    public EqGraph(bool interactive = true, double height = 150)
    {
        this.interactive = interactive;
        Height = height; ClipToBounds = true;
        if (interactive)
            ToolTip.SetTip(this, Loc.T("eq.graph.tip"));
    }
    double X(double hz) => Math.Log(Math.Clamp(hz, 20, 20000) / 20) / Math.Log(1000) * Bounds.Width;
    double F(double x) => EqEditor.FromPos(x / Math.Max(1, Bounds.Width) * 1000);
    double Y(double db) => (18 - db) / 36 * Height;
    double Db(double y) => 18 - y / Height * 36;
    double Handle(EqBand b) => b.Type is "highpass" or "lowpass" ? 0 : b.Gain;
    int Hit(Point p) => eq.Bands.Select((b, i) => (b, i, d: Math.Pow(X(b.Freq) - p.X, 2) + Math.Pow(Y(Handle(b)) - p.Y, 2)))
        .Where(x => x.d <= 196).OrderByDescending(x => x.b.On).ThenBy(x => x.d).Select(x => x.i).FirstOrDefault(-1);
    public void Set(EqSettings value, int index)
    {
        bool selectionChanged = selected != index;
        eq = value; selected = index;
        var bands = eq.Bands.Select(b => EqBandSpec.FromStrings(b.On, b.Type, b.Freq, b.Gain, b.Q, b.Slope)).ToArray();
        if (selectionChanged || !bands.SequenceEqual(currentBands)) { currentBands = bands; Rebuild(); }
        else InvalidateVisual();
    }
    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); Rebuild(); }
    void Rebuild()
    {
        var specs = new BiquadSpec[TrackEq.TotalStages];
        TrackEq.GetSpecs(true, currentBands, specs);
        curve = new StreamGeometry();
        using (var g = curve.Open())
        {
            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0 * Bounds.Width;
                double db = specs.Sum(s => Biquad.ResponseDb(s, 48000, F(x)));
                var p = new Point(x, Math.Clamp(Y(db), -2, Height + 2));
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        totalFill = Fill(specs);
        var band = selected < currentBands.Length ? TrackEq.BandSpecs(currentBands[selected]) : (BiquadSpec.Flat, BiquadSpec.Flat);
        selectedFill = Fill([band.Item1, band.Item2]);
        InvalidateVisual();
    }
    StreamGeometry Fill(BiquadSpec[] specs)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(new Point(0, Y(0)), true);
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0 * Bounds.Width;
            g.LineTo(new Point(x, Math.Clamp(Y(specs.Sum(s => Biquad.ResponseDb(s, 48000, F(x)))), -2, Height + 2)));
        }
        g.LineTo(new Point(Bounds.Width, Y(0))); g.EndFigure(true);
        return geo;
    }
    /// <summary>EqGraph's draw loop: max per 2 px column, +3 dB/oct tilt so a guitar reads flat-ish, slow fall;
    /// without a spectrum the last one falls 2 dB per frame until it is gone.</summary>
    public void PushSpectrum(float[]? bins, int sampleRate)
    {
        double w = Bounds.Width;
        if (w <= 0) return;
        int cols = (int)Math.Ceiling(w / 2) + 1;
        if (smooth.Length != cols) { smooth = new float[cols]; Array.Fill(smooth, -120f); }
        if (bins != null)
        {
            double binHz = sampleRate / 2.0 / bins.Length;
            for (int i = 0; i < cols; i++)
            {
                double f0 = Hz(i * 2, w), f1 = Hz(i * 2 + 2, w);
                int b0 = (int)Math.Floor(f0 / binHz);
                int b1 = Math.Max(b0, (int)Math.Floor(f1 / binHz));
                float m = -200;
                for (; b0 <= b1 && b0 < bins.Length; b0++) if (bins[b0] > m) m = bins[b0];
                float db = m + 3 * (float)Math.Log2(Math.Max(20, f0) / 1000);
                smooth[i] = db > smooth[i] ? db : smooth[i] - 1.2f;
            }
            hadSpectrum = true;
        }
        else if (hadSpectrum)
        {
            bool any = false;
            for (int i = 0; i < cols; i++) { smooth[i] -= 2; if (smooth[i] > -110) any = true; }
            hadSpectrum = any;
        }
        else return;
        InvalidateVisual();
    }
    static double Hz(double x, double w) => 20 * Math.Pow(1000, x / w);
    static readonly IBrush SpectrumFill = Palette.Alpha(Colors.White, 0.07);
    static readonly IPen SpectrumLine = Palette.Pen(Palette.Alpha(Colors.White, 0.18));

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), new Rect(Bounds.Size), 6, 6);
        foreach (double f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            ctx.DrawLine(Palette.Pen(Palette.Line), new Point(X(f), 0), new Point(X(f), Height));
            if (interactive) ctx.DrawText(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", 9, Palette.Faint, new Point(X(f) + 2, Height - 12));
        }
        foreach (int db in new[] { -12, -6, 0, 6, 12 })
        {
            ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, Y(db)), new Point(Bounds.Width, Y(db)));
            if (interactive && db != 0) ctx.DrawText($"{db:+0;-0}", 9, Palette.Faint, new Point(3, Y(db) - 10));
        }
        if (hadSpectrum)
        {
            double h = Height;
            var spectrum = new StreamGeometry();
            using (var g = spectrum.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                for (int i = 0; i < smooth.Length; i++) g.LineTo(new Point(i * 2, h - Math.Clamp((smooth[i] + 96) / 84, 0, 1) * h));
                g.LineTo(new Point(Bounds.Width, h));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(SpectrumFill, SpectrumLine, spectrum);
        }
        if (interactive) ctx.DrawGeometry(Palette.Alpha(Palette.AccentColor, .16), null, selectedFill);
        ctx.DrawGeometry(Palette.Alpha(Palette.AccentColor, eq.On ? .1 : .04), null, totalFill);
        ctx.DrawGeometry(null, Palette.Pen(eq.On ? Palette.Accent : Palette.Dim, interactive ? 2 : 1.5), curve);
        if (!interactive)
        {
            foreach (var b in eq.Bands.Where(b => b.On))
                ctx.DrawEllipse(Palette.Alpha(Palette.AccentColor, 0.75), null, new Point(X(b.Freq), Y(Handle(b))), 2.5, 2.5);
            return;
        }
        for (int i = 0; i < 6; i++)
        {
            var b = eq.Bands[i]; var p = new Point(X(b.Freq), Y(Handle(b))); double r = i == selected ? 8 : 7;
            if (i == selected) ctx.DrawEllipse(null, Palette.Pen(Palette.Fg, 1.5), p, r + 3, r + 3);
            ctx.DrawEllipse(b.On ? Palette.Accent : Palette.App, b.On ? null : Palette.Pen(Palette.Dim), p, r, r);
            ctx.DrawText((i + 1).ToString(), 9, b.On ? Palette.App : Palette.Dim, new Point(p.X - 3, p.Y - 6), Palette.UiBold);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!interactive) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this); int i = Hit(p);
        if (e.ClickCount == 2)
        {
            if (i >= 0) Edited?.Invoke(i, b => b.On = !b.On);
            else
            {
                int free = eq.Bands.Select((b, k) => (b, k)).Where(x => !x.b.On && x.b.Type is not ("highpass" or "lowpass"))
                    .OrderBy(x => Math.Abs(Math.Log(x.b.Freq / F(p.X)))).Select(x => x.k).FirstOrDefault(-1);
                if (free >= 0) { Selected?.Invoke(free); Edited?.Invoke(free, b => { b.On = true; b.Type = "peaking"; b.Freq = Math.Clamp(F(p.X), 20, 20000); b.Gain = Math.Round(Math.Clamp(Db(p.Y), -18, 18), 1); }); }
            }
            return;
        }
        if (i < 0) return;
        Selected?.Invoke(i); down = p; freq = eq.Bands[i].Freq; gain = eq.Bands[i].Gain;
        dragging = i; pointer = e.Pointer; pointer.Capture(this); Edited?.Invoke(i, b => b.On = true); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!interactive) return;
        var p = e.GetPosition(this); hover = Hit(p);
        if (dragging < 0) return;
        double k = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.25 : 1;
        Edited?.Invoke(dragging, b =>
        {
            b.On = true; b.Freq = Math.Clamp(F(X(freq) + (p.X - down.X) * k), 20, 20000);
            if (b.Type is not ("highpass" or "lowpass")) b.Gain = Math.Round(Math.Clamp(gain + (Db(p.Y) - Db(down.Y)) * k, -18, 18), 1);
        });
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; dragging = -1; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { pointer = null; dragging = -1; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!interactive) return;
        int i = hover >= 0 ? hover : selected; bool up = e.Delta.Y > 0, fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Selected?.Invoke(i); Edited?.Invoke(i, b =>
        {
            b.On = true;
            if (b.Type is "highpass" or "lowpass") b.Slope = up ? 24 : 12;
            else if (b.Type == "peaking") b.Q = Math.Round(Math.Clamp(b.Q * Math.Pow(fine ? 1.03 : 1.12, up ? 1 : -1), 0.3, 12), 2);
            else b.Gain = Math.Round(Math.Clamp(b.Gain + (up ? 1 : -1) * (fine ? 0.1 : 0.5), -18, 18), 1);
        }); e.Handled = true;
    }
}
