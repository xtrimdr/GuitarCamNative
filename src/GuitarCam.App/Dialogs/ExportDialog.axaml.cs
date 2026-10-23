using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GuitarCam.App.Audio;
using GuitarCam.App.Model;
using GuitarCam.App.Video;
using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App.Dialogs;

public sealed record ExportOptions(
    string? OutDir,
    string FileName,
    bool Overwrite,
    ExportQuality Quality);

public sealed record ExportSummary(
    int Width,
    int Height,
    string AspectRatio,
    int Takes,
    double DurationSec);

public sealed record ExportedVideo(
    string FileName,
    string FullPath,
    string ProjectDir,
    string LoggedFile,
    long? Bytes = null,
    double? DurationSec = null,
    double? ElapsedSec = null);

/// <summary>
/// Port of <c>ExportSetupDialog</c>, <c>ExportProgressDialog</c>, <c>ExportErrorDialog</c> and
/// <c>ExportDialog</c> from <c>GuitarCam/src/components/ExportDialog.tsx</c>.
/// Native differences vs. the web app:
/// <list type="bullet">
/// <item><description>Mix and FFmpeg run in-process via <see cref="Mp4Exporter"/> instead of uploading to a local HTTP server (the 4 stages and weights 0.12 / 0.03 / 0.83 / 0.02 are preserved identically).</description></item>
/// <item><description>The post-export video preview uses <see cref="VideoDecoder"/> into a <see cref="WriteableBitmap"/> instead of an HTML <c>&lt;video&gt;</c> element.</description></item>
/// </list>
/// </summary>
public sealed partial class ExportDialog : UserControl
{
    enum Phase { Setup, Running, Error, Done }

    Phase phase = Phase.Setup;
    ProjectStore? currentStore;
    IReadOnlyList<LiveTrack>? currentLiveTracks;
    ExportSummary summary = new(1920, 1080, "16:9", 0, 1);

    // Setup state
    string fileName = "";
    string? folder;
    ExportQuality quality = ExportQuality.Alta;
    ExportCheck? check;
    string? checkError;
    bool picking;
    bool firstCheck = true;
    int checkVersion;
    bool syncingInput;

    // Running state
    string runningFileName = "";
    string runningFolder = "";
    ExportProgress runningProgress = new(ExportStage.Mix, 0, 0, null, 0);
    DateTime runningStartedUtc;
    bool confirmCancel;
    CancellationTokenSource? exportCts;
    readonly DispatcherTimer progressTimer;

    // Error state
    string errorMessage = "";
    string? errorDetails;
    bool detailsOpen;
    ExportOptions? retryOptions;

    // Done state
    ExportedVideo? doneVideo;
    bool doneMissing;
    long? doneBytes;
    VideoDecoder? doneDecoder;
    WriteableBitmap? doneBitmap;
    long doneFrameVersion;
    double donePlayheadSec;
    double doneDurationSec = 1;
    bool donePlaying;
    bool syncingDoneSlider;
    readonly DispatcherTimer previewTimer;

    /// <summary>True while an export job is actively rendering.</summary>
    public bool IsRunning => IsVisible && phase == Phase.Running;

    /// <summary>Raised when the dialog is closed by the user.</summary>
    public event Action? Dismissed;

    /// <summary>Raised when an export completes and has been recorded into the project.</summary>
    public event Action<ExportResult>? ExportCompleted;

    public ExportDialog()
    {
        InitializeComponent();

        progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        progressTimer.Tick += (_, _) =>
        {
            if (IsVisible && phase == Phase.Running) RenderRunning();
        };

        previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        previewTimer.Tick += (_, _) => TickDonePreview();

        CloseButton.Click += (_, _) => OnCloseClicked();
        CopyPathButton.Click += async (_, _) => await CopyDonePath();

        // Setup events
        FileNameBox.TextChanged += (_, _) =>
        {
            if (syncingInput) return;
            fileName = FileNameBox.Text ?? "";
            RenderSetupState();
            _ = DebouncedCheck(immediate: false);
        };
        FileNameBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitSetup();
            }
        };
        PickFolderButton.Click += async (_, _) => await PickOutputFolder();
        ResetFolderButton.Click += (_, _) =>
        {
            folder = null;
            RenderSetupState();
            _ = DebouncedCheck(immediate: true);
        };
        QualityAltaBtn.Click += (_, _) => SetQuality(ExportQuality.Alta);
        QualityEstandarBtn.Click += (_, _) => SetQuality(ExportQuality.Estandar);
        QualityBorradorBtn.Click += (_, _) => SetQuality(ExportQuality.Borrador);
        UseSuggestedButton.Click += (_, _) =>
        {
            if (check == null) return;
            SetFileNameInput(check.Suggested);
            _ = DebouncedCheck(immediate: true);
        };
        SetupCancelButton.Click += (_, _) => HideDialog();
        SetupStartButton.Click += async (_, _) => await SubmitSetup();

        // Running events
        RunningCancelButton.Click += (_, _) =>
        {
            confirmCancel = true;
            RenderRunningFooter();
        };
        KeepRunningButton.Click += (_, _) =>
        {
            confirmCancel = false;
            RenderRunningFooter();
        };
        AbortExportButton.Click += (_, _) => exportCts?.Cancel();

        // Error events
        ToggleDetailsButton.Click += (_, _) =>
        {
            detailsOpen = !detailsOpen;
            RenderError();
        };
        ErrorCloseButton.Click += (_, _) => HideDialog();
        ErrorRetryButton.Click += (_, _) =>
        {
            OpenSetup(retryOptions);
        };

        // Done events
        DonePlayPauseButton.Click += (_, _) => ToggleDonePlay();
        DoneVideoSlider.ValueChanged += (_, e) =>
        {
            if (syncingDoneSlider) return;
            donePlayheadSec = Math.Clamp(e.NewValue, 0, doneDurationSec);
            doneDecoder?.Want(donePlayheadSec);
            UpdateDoneTimeText();
        };
        DoneCloseButton.Click += (_, _) => HideDialog();
        DoneRevealButton.Click += (_, _) => RevealDoneFile();
    }

    /// <summary>
    /// Validates that the project can be exported and opens the setup dialog.
    /// Returns <c>false</c> with a status-bar message in <paramref name="statusError"/> if there are no video takes.
    /// </summary>
    public bool TryOpen(ProjectStore store, IReadOnlyList<LiveTrack>? liveTracks, out string? statusError)
    {
        if (IsRunning)
        {
            statusError = null;
            return false;
        }
        var takes = ExtractVideoTakes(store, liveTracks);
        if (takes.Count == 0)
        {
            statusError = "Necesitás al menos una toma en el timeline para exportar.";
            return false;
        }

        statusError = null;
        currentStore = store;
        currentLiveTracks = liveTracks;
        summary = ComputeSummary(store, liveTracks, takes);
        IsVisible = true;
        OpenSetup(initial: null);
        return true;
    }

    /// <summary>Opens the post-export preview dialog for an already-exported record.</summary>
    public void ShowExported(string projectDir, ExportRecord record)
    {
        if (IsRunning) return;
        bool absolute = Path.IsPathRooted(record.File);
        string fullPath = absolute
            ? record.File
            : Path.Combine(projectDir, record.File.Replace('/', Path.DirectorySeparatorChar));
        string name = Path.GetFileName(fullPath);
        IsVisible = true;
        OpenDone(new ExportedVideo(
            name,
            fullPath,
            projectDir,
            record.File,
            record.Bytes > 0 ? record.Bytes : null,
            record.DurationSec > 0 ? record.DurationSec : null,
            ElapsedSec: null));
    }

    public void HideDialog()
    {
        if (phase == Phase.Running) return;
        StopDonePreview();
        progressTimer.Stop();
        IsVisible = false;
        Dismissed?.Invoke();
    }

    /// <summary>Handles Escape (and Enter in setup) while the modal is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key != Key.Escape) return false;
        e.Handled = true;
        switch (phase)
        {
            case Phase.Setup:
                if (!picking) HideDialog();
                break;
            case Phase.Running:
                confirmCancel = true;
                RenderRunningFooter();
                break;
            case Phase.Error:
            case Phase.Done:
                HideDialog();
                break;
        }
        return true;
    }

    void OnCloseClicked()
    {
        if (phase == Phase.Running) return;
        HideDialog();
    }

    // --- Phase transitions ------------------------------------------------------------------------

    void OpenSetup(ExportOptions? initial)
    {
        StopDonePreview();
        progressTimer.Stop();
        phase = Phase.Setup;

        var p = currentStore?.Project;
        string defaultName = string.IsNullOrWhiteSpace(p?.Name) ? "GuitarCam" : p.Name;
        string? savedFolder = string.IsNullOrWhiteSpace(p?.Settings.Export.Dir) ? null : p.Settings.Export.Dir;
        var savedQuality = Mp4Exporter.ParseQuality(p?.Settings.Export.Quality);

        SetFileNameInput(initial?.FileName ?? defaultName);
        folder = initial != null ? initial.OutDir : savedFolder;
        quality = initial?.Quality ?? savedQuality;
        check = null;
        checkError = null;
        picking = false;
        firstCheck = initial == null;

        UpdatePhaseVisibility();
        RenderSetupState();
        _ = DebouncedCheck(immediate: true);

        Dispatcher.UIThread.Post(() =>
        {
            FileNameBox.Focus();
            FileNameBox.SelectAll();
        });
    }

    void OpenError(string message, string? details, ExportOptions options)
    {
        StopDonePreview();
        progressTimer.Stop();
        phase = Phase.Error;
        errorMessage = message;
        errorDetails = string.IsNullOrWhiteSpace(details) ? null : details;
        detailsOpen = false;
        retryOptions = options;
        UpdatePhaseVisibility();
        RenderError();
        Focus();
    }

    void OpenDone(ExportedVideo video)
    {
        StopDonePreview();
        progressTimer.Stop();
        phase = Phase.Done;
        doneVideo = video;
        doneMissing = !File.Exists(video.FullPath);
        doneBytes = !doneMissing ? new FileInfo(video.FullPath).Length : video.Bytes;
        doneDurationSec = Math.Max(0.5, video.DurationSec ?? summary.DurationSec);
        donePlayheadSec = 0;

        UpdatePhaseVisibility();
        RenderDone();

        if (!doneMissing)
            StartDonePreview(video.FullPath);
        Focus();
    }

    void UpdatePhaseVisibility()
    {
        Card.Width = phase == Phase.Done ? 768 : 512;
        SetupPanel.IsVisible = phase == Phase.Setup;
        RunningPanel.IsVisible = phase == Phase.Running;
        ErrorPanel.IsVisible = phase == Phase.Error;
        DonePanel.IsVisible = phase == Phase.Done;
        CloseButton.IsVisible = phase != Phase.Running;
        CopyPathButton.IsVisible = phase == Phase.Done && doneVideo != null;
    }

    // --- Phase 1: Setup ---------------------------------------------------------------------------

    void SetFileNameInput(string value)
    {
        syncingInput = true;
        fileName = value;
        FileNameBox.Text = value;
        syncingInput = false;
    }

    void SetQuality(ExportQuality q)
    {
        quality = q;
        RenderSetupState();
    }

    async Task DebouncedCheck(bool immediate)
    {
        int v = ++checkVersion;
        if (currentStore == null) return;
        string projDir = currentStore.Dir;
        string? curFolder = folder;
        string curName = fileName;
        bool isFirst = firstCheck;

        try
        {
            if (!immediate && !isFirst)
                await Task.Delay(200);
            if (v != checkVersion || currentStore == null) return;

            var c = await Task.Run(() => Mp4Exporter.CheckDestination(projDir, curFolder, curName));
            if (v != checkVersion) return;

            if (firstCheck && c.Exists)
            {
                firstCheck = false;
                SetFileNameInput(c.Suggested);
                _ = DebouncedCheck(immediate: true);
                return;
            }
            firstCheck = false;
            check = c;
            checkError = null;
        }
        catch (Exception e)
        {
            if (v != checkVersion) return;
            checkError = e.Message;
        }
        RenderSetupState();
    }

    async Task PickOutputFolder()
    {
        if (picking || currentStore == null) return;
        picking = true;
        PickFolderSpin.IsVisible = true;
        RenderSetupState();
        try
        {
            var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (sp == null) return;
            string startPath = check?.Folder ?? folder ?? currentStore.Dir;
            var start = Directory.Exists(startPath) ? await sp.TryGetFolderFromPathAsync(startPath) : null;
            var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Elegí dónde guardar el video",
                SuggestedStartLocation = start,
            });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } chosen)
            {
                folder = chosen;
                _ = DebouncedCheck(immediate: true);
            }
        }
        catch (Exception e)
        {
            checkError = e.Message;
        }
        finally
        {
            picking = false;
            PickFolderSpin.IsVisible = false;
            RenderSetupState();
        }
    }

    void RenderSetupState()
    {
        HeaderTitle.Text = "Exportar MP4";
        string takesWord = summary.Takes == 1 ? "toma" : "tomas";
        HeaderSubtitle.Text = $"{summary.Width}×{summary.Height} · {summary.AspectRatio} · {summary.Takes} {takesWord} · {Mp4Exporter.FormatTime(summary.DurationSec)}";
        ToolTip.SetTip(HeaderSubtitle, null);

        string shownFolder = check?.Folder ?? (folder ?? "Carpeta del proyecto");
        FolderText.Text = shownFolder;
        ToolTip.SetTip(FolderText, check?.Folder ?? folder);
        PickFolderButton.IsEnabled = !picking;
        DefaultFolderHint.IsVisible = string.IsNullOrWhiteSpace(folder);
        ResetFolderButton.IsVisible = !string.IsNullOrWhiteSpace(folder);

        SetActiveSegment(QualityAltaBtn, quality == ExportQuality.Alta);
        SetActiveSegment(QualityEstandarBtn, quality == ExportQuality.Estandar);
        SetActiveSegment(QualityBorradorBtn, quality == ExportQuality.Borrador);

        var qSpec = Mp4Exporter.GetQualitySpec(quality);
        double estBytes = Mp4Exporter.EstimateBytes(quality, summary.Width, summary.Height, summary.DurationSec);
        EstBytesText.Text = $"≈ {Mp4Exporter.FormatBytes(estBytes)}";
        QualityDescText.Text = qSpec.Title;

        bool hasError = checkError != null || (check is { FolderExists: false });
        bool hasCollision = !hasError && check is { Exists: true };
        WarningBanner.IsVisible = hasError || hasCollision;
        if (hasError)
        {
            WarningBanner.BorderBrush = new SolidColorBrush(Color.Parse("#66ef4444"));
            WarningBanner.Background = Res("RecSoft");
            WarningIcon.Foreground = Res("Rec");
            WarningText.Text = checkError ?? "Esa carpeta ya no existe. Elegí otra.";
            UseSuggestedButton.IsVisible = false;
        }
        else if (hasCollision && check != null)
        {
            WarningBanner.BorderBrush = Res("Line");
            WarningBanner.Background = Res("Raised");
            WarningIcon.Foreground = Res("Accent");
            WarningText.Text = $"Ya hay un {check.FileName}.mp4 en esa carpeta: se va a reemplazar.";
            UseSuggestedText.Text = $"Usar «{check.Suggested}»";
            UseSuggestedButton.IsVisible = true;
        }

        FullPathPreview.Text = check?.FullPath ?? " ";

        bool nameOk = fileName.Trim().Length > 0;
        bool ready = nameOk && check is { FolderExists: true } && checkError == null && !picking;
        SetupStartButton.IsEnabled = ready;
        SetupStartText.Text = check is { Exists: true } ? "Reemplazar y exportar" : "Exportar";
    }

    static void SetActiveSegment(Button btn, bool active)
    {
        if (active)
        {
            if (!btn.Classes.Contains("on")) btn.Classes.Add("on");
        }
        else btn.Classes.Remove("on");
    }

    async Task SubmitSetup()
    {
        if (currentStore == null || check == null || !check.FolderExists || checkError != null || picking) return;
        if (fileName.Trim().Length == 0) return;

        var opts = new ExportOptions(folder, check.FileName, check.Exists, quality);
        await RunExportJob(opts);
    }

    // --- Phase 2: Running -------------------------------------------------------------------------

    async Task RunExportJob(ExportOptions opts)
    {
        if (currentStore == null) return;

        phase = Phase.Running;
        confirmCancel = false;
        runningFileName = $"{opts.FileName}.mp4";
        runningFolder = string.IsNullOrWhiteSpace(opts.OutDir)
            ? Path.Combine(currentStore.Dir, "exports")
            : opts.OutDir;
        runningStartedUtc = DateTime.UtcNow;
        runningProgress = new ExportProgress(ExportStage.Mix, 0, 0, null, 0);

        UpdatePhaseVisibility();
        RenderRunning();
        RenderRunningFooter();
        progressTimer.Start();

        using var cts = new CancellationTokenSource();
        exportCts = cts;

        try
        {
            var job = await Task.Run(() => BuildJob(currentStore, opts, currentLiveTracks), cts.Token);
            var res = await Mp4Exporter.ExportAsync(
                job,
                p => Dispatcher.UIThread.Post(() =>
                {
                    if (phase != Phase.Running) return;
                    runningProgress = p;
                    RenderRunning();
                }),
                cts.Token);

            RecordExport(currentStore.Project, opts, res);
            ExportCompleted?.Invoke(res);

            OpenDone(new ExportedVideo(
                res.FileName,
                res.FullPath,
                currentStore.Dir,
                res.File,
                res.Bytes,
                res.DurationSec,
                res.ElapsedSec));
        }
        catch (OperationCanceledException)
        {
            OpenSetup(opts);
        }
        catch (ExportException ex)
        {
            OpenError(ex.Message, ex.Details, opts);
        }
        catch (Exception ex)
        {
            OpenError(string.IsNullOrWhiteSpace(ex.Message) ? "Error al exportar el video MP4" : ex.Message, null, opts);
        }
        finally
        {
            if (exportCts == cts) exportCts = null;
            progressTimer.Stop();
        }
    }

    void RenderRunning()
    {
        HeaderTitle.Text = "Exportando…";
        HeaderSubtitle.Text = runningFileName;
        ToolTip.SetTip(HeaderSubtitle, Path.Combine(runningFolder, runningFileName));

        double elapsed = Math.Max(runningProgress.ElapsedSec, (DateTime.UtcNow - runningStartedUtc).TotalSeconds);
        var p = runningProgress with { ElapsedSec = elapsed };

        ProgressPctText.Text = $"{p.Percent}%";
        double? eta = p.EtaSec;
        ProgressTimeText.Text = eta is > 1
            ? $"{Mp4Exporter.FormatTime(elapsed)} transcurrido · quedan ~{Mp4Exporter.FormatTime(eta.Value)}"
            : $"{Mp4Exporter.FormatTime(elapsed)} transcurrido";

        double trackW = ProgressBarTrack.Bounds.Width > 0 ? ProgressBarTrack.Bounds.Width : 470;
        ProgressBarFill.Width = Math.Clamp(p.Overall * trackW, 0, trackW);

        var r = p.Render;
        string? videoDetail = null;
        if (r is { Duration: > 0 })
        {
            string fpsPart = r.Fps is > 0 ? $" · {Math.Round(r.Fps.Value)} fps" : "";
            string spdPart = r.Speed is > 0 ? $" · {r.Speed.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}×" : "";
            videoDetail = $"{Mp4Exporter.FormatTime(r.Time ?? 0)} / {Mp4Exporter.FormatTime(r.Duration.Value)}{fpsPart}{spdPart}";
        }

        (ExportStage Stage, string Label, string? Detail)[] steps =
        [
            (ExportStage.Mix, "Mezclando el audio", null),
            (ExportStage.Upload, "Enviando la mezcla", null),
            (ExportStage.Video, "Renderizando el video", videoDetail),
            (ExportStage.Finalize, "Guardando el archivo", null),
        ];

        int activeIdx = (int)p.Stage;
        StepsList.Children.Clear();
        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            bool done = i < activeIdx;
            bool active = i == activeIdx;
            double frac = active ? p.StageFraction : done ? 1 : 0;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

            var badge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = done ? Res("AccentSoft") : Brushes.Transparent,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (done)
            {
                badge.Child = new TextBlock
                {
                    Classes = { "icon" },
                    Text = "\uE73E",
                    FontSize = 11,
                    Foreground = Res("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else if (active)
            {
                badge.Child = new Avalonia.Controls.Shapes.Arc
                {
                    Classes = { "spin" },
                    Width = 14,
                    Height = 14,
                    Stroke = Res("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                badge.Child = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Res("Line"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            row.Children.Add(badge);

            var label = new TextBlock
            {
                Text = s.Label,
                FontSize = 13,
                Foreground = done || active ? Res("Fg") : Res("Faint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            if (active && s.Detail != null)
            {
                var detail = new TextBlock
                {
                    Classes = { "mono" },
                    Text = s.Detail,
                    FontSize = 11,
                    Foreground = Res("Faint"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                Grid.SetColumn(detail, 2);
                row.Children.Add(detail);
            }

            if (active && s.Stage != ExportStage.Finalize)
            {
                var pct = new TextBlock
                {
                    Classes = { "mono" },
                    Text = $"{(int)Math.Floor(frac * 100)}%",
                    FontSize = 11,
                    Foreground = Res("Dim"),
                    Width = 36,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                Grid.SetColumn(pct, 3);
                row.Children.Add(pct);
            }

            StepsList.Children.Add(row);
        }
    }

    void RenderRunningFooter()
    {
        RunningCancelButton.IsVisible = !confirmCancel;
        ConfirmCancelRow.IsVisible = confirmCancel;
    }

    // --- Phase 3: Error ---------------------------------------------------------------------------

    void RenderError()
    {
        HeaderTitle.Text = "No se pudo exportar";
        HeaderSubtitle.Text = "";
        ToolTip.SetTip(HeaderSubtitle, null);
        ErrorMessageText.Text = errorMessage;
        ErrorDetailsSection.IsVisible = errorDetails != null;
        if (errorDetails != null)
        {
            DetailsChevron.Text = detailsOpen ? "\uE70D" : "\uE76C";
            ErrorDetailsBox.IsVisible = detailsOpen;
            ErrorDetailsText.Text = errorDetails;
        }
    }

    // --- Phase 4: Done ----------------------------------------------------------------------------

    void RenderDone()
    {
        if (doneVideo == null) return;
        HeaderTitle.Text = doneVideo.ElapsedSec != null ? "Video exportado" : doneVideo.FileName;
        HeaderSubtitle.Text = doneVideo.FullPath;
        ToolTip.SetTip(HeaderSubtitle, doneVideo.FullPath);

        MissingVideoText.IsVisible = doneMissing;
        VideoPreviewContainer.IsVisible = !doneMissing;
        DoneRevealButton.IsEnabled = !doneMissing;

        var parts = new List<string>();
        if (doneBytes is > 0) parts.Add(Mp4Exporter.FormatBytes(doneBytes.Value));
        if (doneVideo.ElapsedSec is { } el) parts.Add($"renderizado en {Mp4Exporter.FormatTime(el)}");
        DoneMetaText.Text = string.Join(" · ", parts);
    }

    void StartDonePreview(string path)
    {
        StopDonePreview();
        doneDecoder = VideoDecoder.Open(path, maxWidth: 960);
        if (doneDecoder == null)
        {
            doneMissing = true;
            RenderDone();
            return;
        }
        doneBitmap = new WriteableBitmap(
            new PixelSize(doneDecoder.Width, doneDecoder.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        DoneVideoImage.Source = doneBitmap;
        DoneVideoSlider.Maximum = Math.Max(0.5, doneDurationSec);
        donePlayheadSec = 0;
        donePlaying = true;
        DonePlayPauseIcon.Text = "\uE769";
        doneDecoder.Want(0);
        UpdateDoneTimeText();
        previewTimer.Start();
    }

    void StopDonePreview()
    {
        previewTimer.Stop();
        donePlaying = false;
        doneDecoder?.Dispose();
        doneDecoder = null;
        DoneVideoImage.Source = null;
        doneBitmap?.Dispose();
        doneBitmap = null;
        doneFrameVersion = 0;
    }

    void ToggleDonePlay()
    {
        if (doneDecoder == null) return;
        donePlaying = !donePlaying;
        DonePlayPauseIcon.Text = donePlaying ? "\uE769" : "\uE768";
    }

    void TickDonePreview()
    {
        if (doneDecoder == null || doneBitmap == null) return;
        if (donePlaying)
        {
            donePlayheadSec += 1.0 / VideoDecoder.Fps;
            if (donePlayheadSec >= doneDurationSec) donePlayheadSec = 0;
            doneDecoder.Want(donePlayheadSec);
            syncingDoneSlider = true;
            DoneVideoSlider.Value = donePlayheadSec;
            syncingDoneSlider = false;
            UpdateDoneTimeText();
        }
        var wb = doneBitmap;
        int w = doneDecoder.Width, h = doneDecoder.Height;
        if (doneDecoder.TryCopyLatest(ref doneFrameVersion, buf =>
        {
            using var fb = wb.Lock();
            int row = w * 4;
            for (int y = 0; y < h; y++)
                Marshal.Copy(buf, y * row, fb.Address + y * fb.RowBytes, row);
        }))
        {
            DoneVideoImage.InvalidateVisual();
        }
    }

    void UpdateDoneTimeText()
    {
        DoneVideoTimeText.Text = $"{Mp4Exporter.FormatTime(donePlayheadSec)} / {Mp4Exporter.FormatTime(doneDurationSec)}";
    }

    async Task CopyDonePath()
    {
        if (doneVideo == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try
        {
            await clipboard.SetTextAsync(doneVideo.FullPath);
            CopyPathIcon.Text = "\uE73E";
            CopyPathIcon.Foreground = Res("Accent");
            await Task.Delay(1500);
            CopyPathIcon.Text = "\uE8C8";
            CopyPathIcon.Foreground = Res("Faint");
        }
        catch
        {
        }
    }

    void RevealDoneFile()
    {
        if (doneVideo == null) return;
        if (!File.Exists(doneVideo.FullPath))
        {
            doneMissing = true;
            RenderDone();
            return;
        }
        try
        {
            Explorer.Select(doneVideo.FullPath);
        }
        catch
        {
            doneMissing = true;
            RenderDone();
        }
    }

    // --- Job Builder & Project Recorder -----------------------------------------------------------

    public static ExportSummary ComputeSummary(ProjectStore store, IReadOnlyList<LiveTrack>? liveTracks = null, IReadOnlyList<ExportTakeSpec>? videoTakes = null)
    {
        var p = store.Project;
        var takes = videoTakes ?? ExtractVideoTakes(store, liveTracks);
        var (w, h) = Mp4Exporter.ExportSize(p.Settings.Aspect);
        double maxDur = 1.0;
        foreach (var t in takes)
        {
            double end = t.Window.End;
            if (end > maxDur) maxDur = end;
        }
        if (liveTracks != null)
        {
            foreach (var lt in liveTracks)
                if (lt.EndSec > maxDur) maxDur = lt.EndSec;
        }
        else
        {
            foreach (var trk in p.Tracks)
            {
                foreach (var c in trk.Clips)
                {
                    var m = p.FindMedia(c.MediaId);
                    var win = ClipEditing.Window(c, m);
                    if (win.End > maxDur) maxDur = win.End;
                }
            }
        }
        return new ExportSummary(w, h, p.Settings.Aspect, takes.Count, maxDur);
    }

    public static List<ExportTakeSpec> ExtractVideoTakes(ProjectStore store, IReadOnlyList<LiveTrack>? liveTracks = null)
    {
        var p = store.Project;
        var takes = new List<ExportTakeSpec>();
        foreach (var trk in p.Tracks)
        {
            if (!trk.Video || trk.Clips.Count == 0) continue;
            var c = trk.Clips[0];
            var m = p.FindMedia(c.MediaId);
            string? videoPath = store.Resolve(m?.Video);
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) continue;

            double dur = m?.DurationSec ?? 0;
            if (liveTracks != null)
            {
                var lt = liveTracks.FirstOrDefault(x => x.Track.Id == trk.Id);
                var lc = lt?.Clips.FirstOrDefault(x => x.Clip.Id == c.Id);
                if (lc is { LengthSec: > 0 }) dur = lc.LengthSec;
            }
            if (dur <= 0 && store.Resolve(m?.Audio) is { } ap && File.Exists(ap))
            {
                try
                {
                    var loaded = AudioLoader.Load(ap, PlaybackEngine.SampleRate);
                    dur = loaded.DurationSec;
                }
                catch
                {
                }
            }

            takes.Add(new ExportTakeSpec
            {
                Id = c.Id,
                Name = string.IsNullOrWhiteSpace(trk.Name) ? (m?.Name ?? "") : trk.Name,
                VideoPath = videoPath,
                DurationSec = dur,
                AtSec = c.AtSec,
                SyncMs = c.EffectiveSyncMs(m),
                VideoOffsetMs = c.EffectiveVideoOffsetMs(m),
                TrimStartSec = c.TrimStartSec,
                TrimEndSec = c.TrimEndSec,
                FadeInSec = c.FadeInSec,
                FadeOutSec = c.FadeOutSec,
                LinkVideoFades = c.LinkVideoFades,
                VideoFadeInSec = c.VideoFadeInSec,
                VideoFadeOutSec = c.VideoFadeOutSec,
                Zoom = c.Framing.Zoom,
                PanX = c.Framing.PanX,
                PanY = c.Framing.PanY,
                Mirror = c.Framing.Mirror,
            });
        }
        return takes;
    }

    /// <summary>
    /// Builds a complete <see cref="ExportJobSpec"/> from <paramref name="store"/>, reusing already-loaded
    /// <see cref="LiveTrack"/> audio buffers when provided or loading audio via <see cref="AudioLoader"/>.
    /// </summary>
    public static ExportJobSpec BuildJob(
        ProjectStore store,
        ExportOptions options,
        IReadOnlyList<LiveTrack>? liveTracks = null,
        int sampleRate = PlaybackEngine.SampleRate)
    {
        var p = store.Project;
        var takes = ExtractVideoTakes(store, liveTracks);
        var mixTracks = new List<MixTrackSpec>();
        var audioCache = new Dictionary<string, (float[] L, float[] R, double RmsDb)>(StringComparer.OrdinalIgnoreCase);

        foreach (var trk in p.Tracks)
        {
            var lt = liveTracks?.FirstOrDefault(x => x.Track.Id == trk.Id);
            var mixClips = new List<MixClipSpec>();
            double? firstRmsDb = lt?.RmsDb;

            foreach (var c in trk.Clips)
            {
                var m = p.FindMedia(c.MediaId);
                var lc = lt?.Clips.FirstOrDefault(x => x.Clip.Id == c.Id);
                float[]? l = lc?.Audio?.L;
                float[]? r = lc?.Audio?.R;
                if (l == null || r == null)
                {
                    if (store.Resolve(m?.Audio) is not { } audioPath || !File.Exists(audioPath)) continue;
                    if (!audioCache.TryGetValue(audioPath, out var cached))
                    {
                        var loaded = AudioLoader.Load(audioPath, sampleRate);
                        double rms = LevelAnalysis.Analyze([loaded.Left, loaded.Right], sampleRate).RmsDb;
                        cached = (loaded.Left, loaded.Right, rms);
                        audioCache[audioPath] = cached;
                    }
                    l = cached.L;
                    r = cached.R;
                    firstRmsDb ??= cached.RmsDb;
                }
                mixClips.Add(new MixClipSpec
                {
                    Left = l,
                    Right = r,
                    DurationSec = m?.DurationSec ?? (l.Length / (double)sampleRate),
                    AtSec = c.AtSec,
                    TrimStartSec = c.TrimStartSec,
                    TrimEndSec = c.TrimEndSec,
                    FadeInSec = c.FadeInSec,
                    FadeOutSec = c.FadeOutSec,
                    GainDb = c.GainDb,
                    SyncMs = c.EffectiveSyncMs(m),
                });
            }

            var ch = trk.Channel;
            var satMode = ch.Sat.Mode == "tape" ? SatMode.Tape : SatMode.Tube;
            var sat = SatParams.From(ch.Sat.On, satMode, ch.Sat.Amount, firstRmsDb ?? -24.0);
            var compEq = new CompEqParams(ch.Comp.Eq.HpfHz, ch.Comp.Eq.MudDb, ch.Comp.Eq.PresenceDb, ch.Comp.Eq.PresenceHz, ch.Comp.Eq.AirDb);
            var comp = new CompState(
                ch.Comp.On,
                ch.Comp.Preset,
                ch.Comp.Amount,
                ch.Comp.Limit,
                ch.Comp.Threshold,
                ch.Comp.Ratio,
                ch.Comp.Attack,
                ch.Comp.Release,
                ch.Comp.Knee,
                ch.Comp.Makeup,
                compEq);

            var bands = new EqBandSpec[TrackEq.BandCount];
            for (int i = 0; i < TrackEq.BandCount; i++)
            {
                if (i < ch.Eq.Bands.Count)
                {
                    var b = ch.Eq.Bands[i];
                    bands[i] = EqBandSpec.FromStrings(b.On, b.Type, b.Freq, b.Gain, b.Q, b.Slope, TrackEq.DefaultBands[i].Type);
                }
                else bands[i] = TrackEq.DefaultBands[i];
            }

            mixTracks.Add(new MixTrackSpec
            {
                Clips = mixClips,
                Volume = ch.Volume,
                Pan = ch.Pan,
                Muted = ch.Muted,
                Solo = ch.Solo,
                Sat = sat,
                Comp = comp,
                EqOn = ch.Eq.On,
                EqBands = bands,
                ReverbOn = ch.ReverbOn,
                ReverbSend = ch.ReverbSend,
                DelayOn = ch.DelayOn,
                DelaySend = ch.DelaySend,
            });
        }

        var rev = p.Settings.Reverb;
        var dly = p.Settings.Delay;
        var mixBus = new MixBusSpec
        {
            Bpm = p.Settings.Bpm,
            Reverb = new ReverbParams(rev.Decay, rev.PreDelayMs, rev.Tone, rev.LowCut, rev.Width, rev.ReturnGain, rev.Muted),
            Delay = new DelayParams(
                DelayParams.ParseMode(dly.Mode),
                dly.Sync,
                dly.Division,
                dly.TimeMs,
                dly.Feedback,
                dly.Tone,
                dly.LowCut,
                dly.Width,
                dly.Duck,
                dly.ReturnGain,
                dly.Muted),
        };

        var col = p.Settings.Color;
        var vfx = p.Settings.VideoFx;

        return new ExportJobSpec
        {
            ProjectDir = store.Dir,
            ProjectName = string.IsNullOrWhiteSpace(p.Name) ? "GuitarCam" : p.Name,
            OutDir = options.OutDir,
            FileName = options.FileName,
            Overwrite = options.Overwrite,
            Quality = options.Quality,
            Layout = p.Settings.Layout,
            AspectRatio = p.Settings.Aspect,
            BorderSize = 4,
            Color = new ExportColorSpec(
                col.On,
                col.Look,
                col.Amount,
                col.Exposure,
                col.Contrast,
                col.Highlights,
                col.Shadows,
                col.Saturation,
                col.Vibrance,
                col.Temperature,
                col.Tint,
                col.Fade),
            VideoFx = new ExportVideoFxSpec(
                vfx.On,
                vfx.Vignette,
                vfx.VignetteSize,
                vfx.Grain,
                vfx.Letterbox),
            Takes = takes,
            MixTracks = mixTracks,
            MixBus = mixBus,
            SampleRate = sampleRate,
        };
    }

    /// <summary>Records a finished export into <see cref="Project.Exports"/>, <see cref="Project.Settings"/> and <see cref="Project.Log"/>.</summary>
    public static void RecordExport(Project project, ExportOptions options, ExportResult result)
    {
        project.Settings.Export.Dir = string.IsNullOrWhiteSpace(options.OutDir) ? null : options.OutDir;
        project.Settings.Export.Quality = Mp4Exporter.QualityToId(options.Quality);
        project.Exports.Insert(0, new ExportRecord
        {
            File = result.File,
            CreatedAt = DateTime.UtcNow,
            Layout = project.Settings.Layout,
            Aspect = project.Settings.Aspect,
            Quality = Mp4Exporter.QualityToId(options.Quality),
            DurationSec = Math.Round(result.DurationSec, 2),
            Bytes = result.Bytes,
            // the takes with a panel in the video, as the dialog's summary counts them
            TakeIds = project.Tracks.Where(t => t.Video).SelectMany(t => t.Clips)
                .Where(c => project.FindMedia(c.MediaId)?.Video != null).Select(c => c.MediaId).Distinct().ToList(),
        });
        if (project.Exports.Count > 50)
            project.Exports.RemoveRange(50, project.Exports.Count - 50);
        project.AddLog("exported", result.FileName);
    }

    IBrush Res(string key) => (IBrush)this.FindResource(key)!;
}
