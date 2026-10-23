using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GuitarCam.App.Controls;
using GuitarCam.App.Audio;
using GuitarCam.Audio;

namespace GuitarCam.App;

/// <summary>
/// The top bar from App.tsx's header: the save state next to the project name, the system meter, camera
/// resolution, sample rate and FFmpeg check, and the theme picker. Same as the web except the save state
/// follows guitarcam.json (the web's follows the take uploads: here takes are written while they are recorded),
/// so "Guardando" also shows during the 1.5 s before an edit is saved, and a read-only project says
/// "Solo lectura". The bar also holds the status text here, so the system meter hides below
/// <see cref="SystemMeterMinWidth"/> (the web hides it below its lg breakpoint) and FFmpeg shows only its version
/// number ("8.1.2", the build tag goes to the tooltip).
/// </summary>
public partial class MainWindow
{
    /// <summary>Narrower than this, CPU / GPU / RAM would leave no room for the status text.</summary>
    const double SystemMeterMinWidth = 1600;

    string? saveStateShown;
    string? cameraResShown;

    void InitTopBar()
    {
        UiTheme.Changed += OnThemeChanged;
        InputMeter.OpenRequested += () =>
        {
            if (Projects.IsVisible || Export.IsVisible || RecordingBusy) return;
            Settings.Show("audio");
        };
        SizeChanged += (_, e) => SysMeter.IsVisible = SysMeterSep.IsVisible = e.NewSize.Width >= SystemMeterMinWidth;
        SampleRateText.Text = $"{PlaybackEngine.SampleRate / 1000.0:0.#} kHz";
        Opened += async (_, _) =>
        {
            string? version = await Task.Run(FfmpegInfo.Probe);
            bool ready = version != null;
            FfmpegDot.Fill = ready ? Palette.Brush("#10b981") : Palette.Rec;
            string number = System.Text.RegularExpressions.Regex.Match(version ?? "", @"^\d+(\.\d+)*").Value;
            FfmpegText.Text = ready ? $"FFmpeg {(number.Length > 0 ? number : version)}".Trim() : "FFmpeg no encontrado";
            ToolTip.SetTip(FfmpegState, ready ? $"FFmpeg disponible para exportar ({version})" : "FFmpeg no encontrado en el PATH: no vas a poder exportar");
        };
    }

    /// <summary>Called from <see cref="Tick"/>: cheap string checks, the controls only change when the state does.</summary>
    void TickTopBar()
    {
        string state = store == null ? "none" : store.ReadOnly ? "readonly" : saveFailure != null ? "error" : dirty || !saving.IsCompleted ? "saving" : "saved";
        if (state != saveStateShown)
        {
            saveStateShown = state;
            SaveState.IsVisible = state != "none";
            SaveSpin.IsVisible = state == "saving";
            SaveIcon.IsVisible = state == "saved";
            SaveText.Text = state switch { "error" => "Error al guardar", "saving" => "Guardando", "readonly" => "Solo lectura", _ => "Guardado" };
            SaveText.Foreground = state switch { "error" => Palette.Rec, "saving" => Palette.Dim, _ => Palette.Faint };
            ToolTip.SetTip(SessionChip, store == null ? "Proyectos" : $"{store.Dir}\nClick para gestionar los proyectos");
        }
        string res = camera.Resolution;
        if (res != cameraResShown)
        {
            cameraResShown = res;
            CameraResText.Text = res;
            CameraResText.IsVisible = res.Length > 0;
        }
    }

    /// <summary>Brushes follow the theme on their own; what was drawn or built from a color is redrawn here.</summary>
    void OnThemeChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var v in this.GetVisualDescendants()) v.InvalidateVisual();
            RefreshColorPanel();
            RefreshMixer();
            ShowTrack(sel);
        });
    }
}
