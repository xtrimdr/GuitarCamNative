using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GuitarCam.Audio;

namespace GuitarCam.App.Controls;

/// <summary>
/// The "En vivo" monitor next to the stage: port of LiveMonitor.tsx. The raw camera (never the composition),
/// the input device with its gain and the input meter; it turns red while recording and accent during the
/// count-in. Same as the web except the pulsing dot and the red glow around the frame, left out because the
/// native window has no decorative animations (AGENTS.md); the colors and the solid ring stay.
/// </summary>
public sealed class LiveMonitor : UserControl
{
    readonly Border root = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(-8) };
    readonly TextBlock title = new() { Text = "EN VIVO", FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.9, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock ready = new() { Text = "Listo para grabar", FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock elapsed = new() { FontSize = 11, Foreground = Brushes.White, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center };
    readonly Border recBadge, countBadge;
    readonly TextBlock countText = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.App, Classes = { "mono" } };
    readonly Border frameBox = new() { Background = Palette.Raised, CornerRadius = new CornerRadius(6), ClipToBounds = true };
    readonly LiveFrame frame = new();
    readonly StackPanel placeholder = new() { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
    readonly Border recTag;
    readonly Border countOverlay;
    readonly TextBlock countBig = new() { FontSize = 48, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Classes = { "mono" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock resolution = new() { FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" } };
    readonly TextBlock audioLabel = new() { FontSize = 11, Foreground = Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock gainText = new() { FontSize = 10, Foreground = Palette.Faint, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center };
    (bool Rec, int? Beat)? shown;

    /// <summary>The input meter; the window feeds it from the same read as the transport bar's.</summary>
    public StereoMeter Meter { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    /// <summary>"Configurar entradas": the window opens Configuración on its audio tab.</summary>
    public event Action? OpenInputs;

    public LiveMonitor()
    {
        recBadge = Badge(Palette.Rec, new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { Dot(), new TextBlock { Text = "GRABANDO", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }, elapsed },
        });
        countBadge = Badge(Palette.Accent, countText);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(title);
        var right = new Panel { Children = { ready, recBadge, countBadge } };
        Grid.SetColumn(right, 1);
        head.Children.Add(right);

        placeholder.Children.Add(new TextBlock { Text = "Sin señal de cámara", FontSize = 12, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center });
        placeholder.Children.Add(new TextBlock { Text = "Elegila en Configuración", FontSize = 11, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        recTag = new Border
        {
            Background = Palette.Rec, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2), Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Dot(), new TextBlock { Text = "REC", FontSize = 10, FontWeight = FontWeight.Bold, LetterSpacing = 0.5, Foreground = Brushes.White } } },
        };
        countOverlay = new Border { Background = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)), Child = countBig };
        frameBox.Child = new Panel { Children = { frame, placeholder, recTag, countOverlay } };

        var inputs = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2), Margin = new Thickness(-4, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 0,
        };
        var inputsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
        inputsRow.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(audioLabel, 1);
        inputsRow.Children.Add(audioLabel);
        Grid.SetColumn(gainText, 2);
        inputsRow.Children.Add(gainText);
        inputs.Content = inputsRow;
        ToolTip.SetTip(inputs, "Configurar entradas");
        inputs.Click += (_, _) => OpenInputs?.Invoke();
        var audio = new Border
        {
            BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0),
            Child = new StackPanel { Spacing = 6, Children = { inputs, Meter } },
        };

        root.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                head, frameBox, resolution, audio,
                new TextBlock { Text = "Lo que entra ahora: cámara y audio. La toma pasa a la composición cuando la terminás.", FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap },
            },
        };
        Content = root;
        SetState(false, null, 0);
    }

    static Border Badge(IBrush bg, Control child) => new()
    {
        Background = bg, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, Child = child,
    };

    static Ellipse Dot() => new() { Width = 6, Height = 6, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Recording, the count-in beat and the time recorded (the web's isRecording / countdownBeat / elapsedSec).</summary>
    public void SetState(bool recording, int? beat, double elapsedSec)
    {
        if (recording)
        {
            int m = (int)(elapsedSec / 60);
            elapsed.Text = $"{m:00}:{(elapsedSec - m * 60).ToString("00.0", System.Globalization.CultureInfo.InvariantCulture)}";
        }
        if (shown == (recording, beat)) return;
        shown = (recording, beat);
        bool counting = !recording && beat != null;
        root.Background = recording ? Palette.RecSoft : counting ? Palette.AccentSoft : Brushes.Transparent;
        title.Foreground = recording ? Palette.Rec : counting ? Palette.Accent : Palette.Faint;
        ready.IsVisible = !recording && !counting;
        recBadge.IsVisible = recTag.IsVisible = recording;
        countBadge.IsVisible = countOverlay.IsVisible = counting;
        countText.Text = $"Cuenta {beat}/4";
        countBig.Text = $"{beat}";
        frameBox.BorderThickness = new Thickness(recording || counting ? 2 : 1);
        frameBox.BorderBrush = recording ? Palette.Rec : counting ? Palette.Accent : Palette.Line;
    }

    /// <summary>The input device ("Sin interfaz de audio" without one) and its gain.</summary>
    public void SetAudio(string? device, double gainDb)
    {
        audioLabel.Text = device ?? "Sin interfaz de audio";
        gainText.Text = $"{(gainDb > 0 ? "+" : "")}{gainDb:0} dB";
    }

    /// <summary>Shows the camera's newest frame, mirrored or not. Call it every tick.</summary>
    public void UpdateCamera(CameraCapture? cam, bool mirror)
    {
        frame.Mirror = mirror;
        bool signal = frame.Update(cam);
        placeholder.IsVisible = !signal;
        resolution.Text = cam?.Resolution ?? "";
        resolution.IsVisible = resolution.Text.Length > 0;
    }

    /// <summary>The camera image, scaled to the width, in the frame's aspect (16:9 until there's one).</summary>
    sealed class LiveFrame : Control
    {
        WriteableBitmap? bmp;
        long seen = -1;
        CameraCapture? source;
        bool mirror;
        bool signal;

        public bool Mirror
        {
            get => mirror;
            set
            {
                if (mirror == value) return;
                mirror = value;
                InvalidateVisual();
            }
        }

        /// <summary>True while the camera sends images.</summary>
        public bool Update(CameraCapture? cam)
        {
            if (cam != source)
            {
                source = cam;
                seen = -1;
            }
            bool has = cam is { HasSignal: true };
            if (has != signal)
            {
                signal = has;
                InvalidateVisual();
            }
            if (cam == null || !has) return false;
            int w = CameraCapture.PreviewWidth, h = cam.PreviewHeight;
            if (h <= 0) return false;
            if (bmp == null || bmp.PixelSize.Height != h)
            {
                bmp?.Dispose();
                bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                InvalidateMeasure();
            }
            var target = bmp;
            if (cam.TryCopyPreview(ref seen, buf =>
                {
                    if (buf.Length != w * h * 4) return;
                    using var fb = target.Lock();
                    for (int y = 0; y < h; y++) Marshal.Copy(buf, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
                }))
                InvalidateVisual();
            return true;
        }

        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsFinite(available.Width) ? available.Width : CameraCapture.PreviewWidth;
            double aspect = bmp != null ? bmp.PixelSize.Height / (double)bmp.PixelSize.Width : 9.0 / 16;
            return new Size(w, Math.Round(w * aspect));
        }

        public override void Render(DrawingContext ctx)
        {
            if (bmp == null || !signal) return;
            var dest = new Rect(Bounds.Size);
            var src = new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
            if (mirror)
            {
                using (ctx.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(dest.Width, 0)))
                    ctx.DrawImage(bmp, src, dest);
            }
            else ctx.DrawImage(bmp, src, dest);
        }
    }
}
