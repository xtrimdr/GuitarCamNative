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

/// <summary>
/// What the setup step chose. <c>Target</c> is the social preset (<see cref="ExportTargets"/>) and <c>TargetLufs</c>
/// the loudness the mix is mastered to (the preset's, or −14 for "Exportar audio" with the box ticked; null = as it is).
/// <c>Resolution</c> and <c>BitrateMbps</c> are "Avanzado": the frame's short side and the video bitrate (null = the quality's).
/// </summary>
public sealed record ExportOptions(
    string? OutDir,
    string FileName,
    bool Overwrite,
    ExportQuality Quality,
    string Target = ExportTargets.Project,
    double? TargetLufs = null,
    int Resolution = Mp4Exporter.DefaultResolution,
    double? BitrateMbps = null);

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
    double? ElapsedSec = null,
    double? Lufs = null,
    double TailSec = 0);

/// <summary>
/// Port of <c>ExportSetupDialog</c>, <c>ExportProgressDialog</c>, <c>ExportErrorDialog</c> and
/// <c>ExportDialog</c> from <c>GuitarCam/src/components/ExportDialog.tsx</c>.
/// Native differences vs. the web app:
/// <list type="bullet">
/// <item><description>Mix and FFmpeg run in-process via <see cref="Mp4Exporter"/> instead of uploading to a local HTTP server (the 4 stages and weights 0.12 / 0.03 / 0.83 / 0.02 are preserved identically).</description></item>
/// <item><description>The post-export video preview uses <see cref="VideoDecoder"/> into a <see cref="WriteableBitmap"/> instead of an HTML <c>&lt;video&gt;</c> element.</description></item>
/// <item><description>A solo audio project gets "Exportar audio" in the same dialog: WAV or MP3 instead of the quality,
/// three steps (mix, MP3, save) and no picture when it is done (<see cref="AudioExporter"/>).</description></item>
/// <item><description>"Destino" picks a social preset (<see cref="ExportTargets"/>): its frame for this video and the
/// mix mastered to its loudness. "Proyecto" is the default and changes nothing, so grabo-mezclo-exporto has no extra
/// step. The done screen shows the mix's integrated LUFS and the effect tail (<see cref="GuitarCam.Audio.Dsp.MixMaster"/>).</description></item>
/// <item><description>"Avanzado" (folded) picks the resolution (720p to 4K) and a bitrate by hand; its defaults are what
/// the dialog did without it, and folded it says what is not the default, so the easy path stays easy.</description></item>
/// </list>
/// </summary>
public sealed partial class ExportDialog : UserControl
{
    enum Phase { Setup, Running, Error, Done }

    Phase phase = Phase.Setup;
    ProjectStore? currentStore;
    IReadOnlyList<LiveTrack>? currentLiveTracks;
    ExportSummary summary = new(1920, 1080, "16:9", 0, 1);
    /// <summary>"Exportar audio" of a solo audio project, or an audio file opened from Historial.</summary>
    bool audio;
    AudioFormat audioFormat = AudioFormat.Wav;
    AudioExportProgress audioProgress = new(AudioExportStage.Mix, 0, 0);
    /// <summary>The export is done and the stems are being written ("Stems", native only).</summary>
    bool stemsRunning;
    double stemsFraction;
    /// <summary>"4 stems en «Canción - stems»" for the done screen; null without stems.</summary>
    string? doneStems;
    string Ext => audio ? AudioExporter.Extension(audioFormat) : ".mp4";

    // Setup state
    string fileName = "";
    string? folder;
    ExportQuality quality = ExportQuality.Alta;
    string target = ExportTargets.Project;
    int resolution = Mp4Exporter.DefaultResolution;
    /// <summary>"Avanzado"'s bitrate; null = the quality's.</summary>
    double? bitrateMbps;
    bool advancedOpen;
    bool syncingBitrate;
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
        FormatWavBtn.Click += (_, _) => SetAudioFormat(AudioFormat.Wav);
        FormatMp3Btn.Click += (_, _) => SetAudioFormat(AudioFormat.Mp3);
        foreach (var t in ExportTargets.All)
        {
            var b = new Button
            {
                Classes = { "seg" },
                Content = Loc.Or($"export.target.{t.Id}", t.Label),
                Tag = t.Id,
            };
            ToolTip.SetTip(b, Loc.T($"export.target.{t.Id}.tip"));
            b.Click += (_, _) => SetTarget(t.Id);
            TargetButtons.Children.Add(b);
        }
        foreach (int r in Mp4Exporter.Resolutions)
        {
            var b = new Button { Classes = { "seg" }, Content = ResolutionLabel(r), Tag = r };
            b.Click += (_, _) => { resolution = r; RenderSetupState(); };
            ResolutionButtons.Children.Add(b);
        }
        AdvancedToggle.Click += (_, _) => { advancedOpen = !advancedOpen; RenderSetupState(); };
        BitrateAutoCheck.IsCheckedChanged += (_, _) =>
        {
            if (syncingBitrate) return;
            bitrateMbps = BitrateAutoCheck.IsChecked == true ? null : Mp4Exporter.NormalizeBitrate(BitrateSlider.Value);
            RenderSetupState();
        };
        // moving the slider is choosing a bitrate: it leaves "Automático" by itself
        BitrateSlider.ValueChanged += (_, _) =>
        {
            if (syncingBitrate) return;
            bitrateMbps = Mp4Exporter.NormalizeBitrate(BitrateSlider.Value);
            RenderSetupState();
        };
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
        audio = store.Project.Settings.AudioOnly;
        var takes = ExtractVideoTakes(store, liveTracks);
        if (audio ? !store.Project.Tracks.Any(t => t.Clips.Count > 0) : takes.Count == 0)
        {
            statusError = Loc.T(audio || !store.Project.Tracks.Any(t => t.Clips.Count > 0) ? "exp.needTrack" : "exp.needVideo");
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
        audio = AudioExporter.IsAudioFile(fullPath);
        IsVisible = true;
        OpenDone(new ExportedVideo(
            name,
            fullPath,
            projectDir,
            record.File,
            record.Bytes > 0 ? record.Bytes : null,
            record.DurationSec > 0 ? record.DurationSec : null,
            ElapsedSec: null,
            Lufs: record.Lufs));
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
        if (initial == null) audioFormat = AudioExporter.ParseFormat(p?.Settings.Export.AudioFormat);
        if (initial == null) StemsCheck.IsChecked = p?.Settings.Export.Stems == true;
        target = ExportTargets.Get(initial?.Target ?? p?.Settings.Export.Target).Id;

        SetFileNameInput(initial?.FileName ?? defaultName);
        folder = initial != null ? initial.OutDir : savedFolder;
        quality = initial?.Quality ?? savedQuality;
        resolution = Mp4Exporter.NormalizeResolution(initial?.Resolution ?? p?.Settings.Export.Resolution ?? Mp4Exporter.DefaultResolution);
        bitrateMbps = Mp4Exporter.NormalizeBitrate(initial != null ? initial.BitrateMbps : p?.Settings.Export.BitrateMbps);
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

        if (!doneMissing && !audio)
            StartDonePreview(video.FullPath);
        Focus();
    }

    void UpdatePhaseVisibility()
    {
        Card.Width = phase == Phase.Done && !audio ? 768 : 512;
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

    void SetTarget(string id)
    {
        target = ExportTargets.Get(id).Id;
        RenderSetupState();
    }

    void SetAudioFormat(AudioFormat f)
    {
        audioFormat = f;
        RenderSetupState();
        _ = DebouncedCheck(immediate: true);
    }

    async Task DebouncedCheck(bool immediate)
    {
        int v = ++checkVersion;
        if (currentStore == null) return;
        string projDir = currentStore.Dir;
        string? curFolder = folder;
        string curName = fileName;
        string ext = Ext;
        bool isFirst = firstCheck;

        try
        {
            if (!immediate && !isFirst)
                await Task.Delay(200);
            if (v != checkVersion || currentStore == null) return;

            var c = await Task.Run(() => Mp4Exporter.CheckDestination(projDir, curFolder, curName, ext));
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
                Title = audio ? Loc.T("exp.pick.audio") : Loc.T("exp.pick.video"),
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
        HeaderTitle.Text = audio ? Loc.T("export.audio") : Loc.T("export.mp4");
        string takesWord = Loc.T(summary.Takes == 1 ? "exp.channel.one" : "exp.channel.many");
        var preset = ExportTargets.Get(target);
        string aspect = ExportTargets.AspectFor(target, summary.AspectRatio);
        var (outW, outH) = Mp4Exporter.ExportSize(aspect, resolution);
        int tracksWithClips = currentStore?.Project.Tracks.Count(t => t.Clips.Count > 0) ?? 0;
        HeaderSubtitle.Text = audio
            ? Loc.T("exp.sub.audio", Loc.T(tracksWithClips == 1 ? "tracks.one" : "tracks.many", tracksWithClips), Mp4Exporter.FormatTime(summary.DurationSec))
            : $"{outW}×{outH} · {aspect} · {summary.Takes} {takesWord} · {Mp4Exporter.FormatTime(summary.DurationSec)}";
        ExtText.Text = Ext;
        FileNameBox.Watermark = Loc.T(audio ? "exp.mySong" : "exp.myVideo");
        QualityLabel.Text = Loc.T(audio ? "exp.format" : "exp.quality");
        FormatSeg.IsVisible = audio;
        QualitySeg.IsVisible = !audio;
        TargetPanel.IsVisible = !audio;
        foreach (var c in TargetButtons.Children)
            if (c is Button b) SetActiveSegment(b, (string?)b.Tag == target);
        TargetDescText.Text = preset.Aspect != null
            ? Loc.T("exp.target.desc", outW, outH, aspect)
            : Loc.T("exp.target.project.desc", aspect);
        // the loudness is the master's Destino, heard while mixing (MasterLoudness)
        var dest = MasterLoudness.Get(currentStore?.Project.Settings.Master.Target);
        LoudnessText.Text = dest.Lufs is { } goal
            ? Loc.T("exp.loudness.target", Lufs(goal), Loc.Or("master.target." + dest.Id, dest.Label))
            : Loc.T("exp.loudness.asis");
        ToolTip.SetTip(HeaderSubtitle, null);

        string shownFolder = check?.Folder ?? (folder ?? Loc.T("exp.projectFolder"));
        FolderText.Text = shownFolder;
        ToolTip.SetTip(FolderText, check?.Folder ?? folder);
        PickFolderButton.IsEnabled = !picking;
        DefaultFolderHint.IsVisible = string.IsNullOrWhiteSpace(folder);
        ResetFolderButton.IsVisible = !string.IsNullOrWhiteSpace(folder);

        SetActiveSegment(QualityAltaBtn, quality == ExportQuality.Alta);
        SetActiveSegment(QualityEstandarBtn, quality == ExportQuality.Estandar);
        SetActiveSegment(QualityBorradorBtn, quality == ExportQuality.Borrador);

        SetActiveSegment(FormatWavBtn, audioFormat == AudioFormat.Wav);
        SetActiveSegment(FormatMp3Btn, audioFormat == AudioFormat.Mp3);

        var qSpec = Mp4Exporter.GetQualitySpec(quality);
        double estBytes = audio ? AudioExporter.EstimateBytes(audioFormat, summary.DurationSec)
            : Mp4Exporter.EstimateBytes(quality, outW, outH, summary.DurationSec, bitrateMbps);
        EstBytesText.Text = $"≈ {Mp4Exporter.FormatBytes(estBytes)}";
        QualityDescText.Text = !audio ? qSpec.Title
            : audioFormat == AudioFormat.Mp3 ? Loc.T("exp.mp3.note")
            : Loc.T("exp.wav.note");

        RenderAdvanced(outW, outH);

        bool hasError = checkError != null || (check is { FolderExists: false });
        bool hasCollision = !hasError && check is { Exists: true };
        WarningBanner.IsVisible = hasError || hasCollision;
        if (hasError)
        {
            WarningBanner.BorderBrush = new SolidColorBrush(Color.Parse("#66ef4444"));
            WarningBanner.Background = Res("RecSoft");
            WarningIcon.Foreground = Res("Rec");
            WarningText.Text = checkError ?? Loc.T("exp.folderGone");
            UseSuggestedButton.IsVisible = false;
        }
        else if (hasCollision && check != null)
        {
            WarningBanner.BorderBrush = Res("Line");
            WarningBanner.Background = Res("Raised");
            WarningIcon.Foreground = Res("Accent");
            WarningText.Text = Loc.T("exp.exists", check.FileName, Ext);
            UseSuggestedText.Text = Loc.T("exp.useSuggested", check.Suggested);
            UseSuggestedButton.IsVisible = true;
        }

        FullPathPreview.Text = check?.FullPath ?? " ";

        bool nameOk = fileName.Trim().Length > 0;
        bool ready = nameOk && check is { FolderExists: true } && checkError == null && !picking;
        SetupStartButton.IsEnabled = ready;
        SetupStartText.Text = Loc.T(check is { Exists: true } ? "exp.replaceStart" : "exp.start");
    }

    static string ResolutionLabel(int shortSide) => shortSide == 2160 ? "4K" : $"{shortSide}p";

    /// <summary>"Avanzado": folded or open, what is not the default next to its title, and the controls as they are.</summary>
    void RenderAdvanced(int outW, int outH)
    {
        AdvancedPanel.IsVisible = !audio;
        AdvancedBody.IsVisible = advancedOpen;
        AdvancedChevron.Text = advancedOpen ? "\uE70D" : "\uE76C";
        var custom = new List<string>();
        if (resolution != Mp4Exporter.DefaultResolution) custom.Add(ResolutionLabel(resolution));
        if (bitrateMbps is { } m) custom.Add(Loc.T("exp.adv.mbps", $"{m:0}"));
        AdvancedSummary.Text = custom.Count > 0 ? "· " + string.Join(" · ", custom) : "";
        foreach (var c in ResolutionButtons.Children)
            if (c is Button b) SetActiveSegment(b, (int)b.Tag! == resolution);
        ResolutionSizeText.Text = $"{outW}×{outH}";

        double auto = Mp4Exporter.VideoMbps(quality, outW, outH);
        syncingBitrate = true;
        BitrateAutoCheck.IsChecked = bitrateMbps == null;
        BitrateSlider.Value = Math.Clamp(Math.Round(bitrateMbps ?? auto), Mp4Exporter.MinBitrateMbps, Mp4Exporter.MaxBitrateMbps);
        syncingBitrate = false;
        BitrateSlider.Opacity = bitrateMbps == null ? 0.45 : 1;
        BitrateText.Text = Loc.T("exp.adv.mbps", $"{bitrateMbps ?? auto:0.#}");
        BitrateHint.Text = bitrateMbps == null ? Loc.T("exp.adv.bitrate.autoHint") : Loc.T("exp.adv.bitrate.customHint");
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

        double? lufs = MasterLoudness.Get(currentStore.Project.Settings.Master.Target).Lufs;
        var opts = new ExportOptions(folder, check.FileName, check.Exists, quality, target, lufs, resolution, bitrateMbps);
        await RunExportJob(opts);
    }

    // --- Phase 2: Running -------------------------------------------------------------------------

    async Task RunExportJob(ExportOptions opts)
    {
        if (currentStore == null) return;

        phase = Phase.Running;
        confirmCancel = false;
        runningFileName = $"{opts.FileName}{Ext}";
        runningFolder = string.IsNullOrWhiteSpace(opts.OutDir)
            ? Path.Combine(currentStore.Dir, "exports")
            : opts.OutDir;
        runningStartedUtc = DateTime.UtcNow;
        runningProgress = new ExportProgress(ExportStage.Mix, 0, 0, null, 0);
        audioProgress = new AudioExportProgress(AudioExportStage.Mix, 0, 0);
        bool withStems = StemsCheck.IsChecked == true;
        (stemsRunning, stemsFraction, doneStems) = (false, 0, null);

        UpdatePhaseVisibility();
        RenderRunning();
        RenderRunningFooter();
        progressTimer.Start();

        using var cts = new CancellationTokenSource();
        exportCts = cts;

        try
        {
            var job = await Task.Run(() => BuildJob(currentStore, opts, currentLiveTracks), cts.Token);
            var format = audioFormat;
            var res = audio
                ? await AudioExporter.ExportAsync(job, format, p => Dispatcher.UIThread.Post(() =>
                {
                    if (phase != Phase.Running) return;
                    audioProgress = p;
                    RenderRunning();
                }), cts.Token)
                : await Mp4Exporter.ExportAsync(
                job,
                p => Dispatcher.UIThread.Post(() =>
                {
                    if (phase != Phase.Running) return;
                    runningProgress = p;
                    RenderRunning();
                }),
                cts.Token);

            currentStore.Project.Settings.Export.Stems = withStems;
            RecordExport(currentStore.Project, opts, res, audio ? format : null);
            ExportCompleted?.Invoke(res);
            if (withStems)
            {
                stemsRunning = true;
                RenderRunning();
                var (stemFolder, count) = await AudioExporter.ExportStemsAsync(job, res.FullPath, f => Dispatcher.UIThread.Post(() =>
                {
                    if (phase != Phase.Running) return;
                    stemsFraction = f;
                    RenderRunning();
                }), cts.Token, res.DurationSec);
                doneStems = Loc.T("exp.done.stems", count, Path.GetFileName(stemFolder));
            }

            OpenDone(new ExportedVideo(
                res.FileName,
                res.FullPath,
                currentStore.Dir,
                res.File,
                res.Bytes,
                res.DurationSec,
                res.ElapsedSec,
                double.IsFinite(res.Lufs) ? res.Lufs : null,
                res.TailSec));
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
            OpenError(string.IsNullOrWhiteSpace(ex.Message) ? (audio ? Loc.T("exp.err.audio") : Loc.T("exp.err.video")) : ex.Message, null, opts);
        }
        finally
        {
            stemsRunning = false;
            if (exportCts == cts) exportCts = null;
            progressTimer.Stop();
        }
    }

    void RenderRunning()
    {
        HeaderTitle.Text = Loc.T("exp.running");
        HeaderSubtitle.Text = runningFileName;
        ToolTip.SetTip(HeaderSubtitle, Path.Combine(runningFolder, runningFileName));

        double elapsed = Math.Max(runningProgress.ElapsedSec, (DateTime.UtcNow - runningStartedUtc).TotalSeconds);
        if (stemsRunning)
        {
            // the export itself is done: one more step, a file per track
            ProgressPctText.Text = $"{(int)Math.Floor(stemsFraction * 100)}%";
            ProgressTimeText.Text = Loc.T("exp.elapsed", Mp4Exporter.FormatTime(elapsed));
            double w = ProgressBarTrack.Bounds.Width > 0 ? ProgressBarTrack.Bounds.Width : 470;
            ProgressBarFill.Width = Math.Clamp(stemsFraction * w, 0, w);
            RenderSteps([(Loc.T(audio ? "exp.step.saving" : "exp.step.rendering"), null, false), (Loc.T("exp.step.stems"), null, true)], 1, stemsFraction);
            return;
        }
        if (audio)
        {
            RenderAudioRunning(elapsed);
            return;
        }
        var p = runningProgress with { ElapsedSec = elapsed };

        ProgressPctText.Text = $"{p.Percent}%";
        double? eta = p.EtaSec;
        ProgressTimeText.Text = eta is > 1
            ? Loc.T("exp.elapsed.eta", Mp4Exporter.FormatTime(elapsed), Mp4Exporter.FormatTime(eta.Value))
            : Loc.T("exp.elapsed", Mp4Exporter.FormatTime(elapsed));

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

        RenderSteps(
        [
            (Loc.T("exp.step.mixing"), null, true),
            (Loc.T("exp.step.sending"), null, true),
            (Loc.T("exp.step.rendering"), videoDetail, true),
            (Loc.T("exp.step.saving"), null, false),
        ], (int)p.Stage, p.StageFraction);
    }

    /// <summary>The audio export's steps: the mix, the MP3 conversion (only for MP3) and saving the file.</summary>
    void RenderAudioRunning(double elapsed)
    {
        var a = audioProgress;
        bool mp3 = audioFormat == AudioFormat.Mp3;
        // the mix is most of the work; FFmpeg's MP3 pass is quick
        double overall = a.Stage switch
        {
            AudioExportStage.Mix => (mp3 ? 0.75 : 0.9) * a.Fraction,
            AudioExportStage.Encode => 0.75 + 0.23 * a.Fraction,
            _ => 0.98,
        };
        ProgressPctText.Text = $"{(int)Math.Floor(overall * 100)}%";
        ProgressTimeText.Text = Loc.T("exp.elapsed", Mp4Exporter.FormatTime(elapsed));
        double trackW = ProgressBarTrack.Bounds.Width > 0 ? ProgressBarTrack.Bounds.Width : 470;
        ProgressBarFill.Width = Math.Clamp(overall * trackW, 0, trackW);
        var steps = new List<(string, string?, bool)> { (Loc.T("exp.step.mixing"), null, true) };
        if (mp3) steps.Add((Loc.T("exp.step.mp3"), null, true));
        steps.Add((Loc.T("exp.step.saving"), null, false));
        int active = a.Stage == AudioExportStage.Mix ? 0 : a.Stage == AudioExportStage.Encode ? 1 : steps.Count - 1;
        RenderSteps([.. steps], active, a.Fraction);
    }

    void RenderSteps((string Label, string? Detail, bool ShowPct)[] steps, int activeIdx, double stageFraction)
    {
        StepsList.Children.Clear();
        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            bool done = i < activeIdx;
            bool active = i == activeIdx;
            double frac = active ? stageFraction : done ? 1 : 0;

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

            if (active && s.ShowPct)
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
        HeaderTitle.Text = Loc.T("exp.failed");
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
        HeaderTitle.Text = doneVideo.ElapsedSec != null ? Loc.T(audio ? "exp.done.audio" : "exp.done.video") : doneVideo.FileName;
        HeaderSubtitle.Text = doneVideo.FullPath;
        ToolTip.SetTip(HeaderSubtitle, doneVideo.FullPath);

        MissingVideoText.IsVisible = doneMissing;
        VideoPreviewContainer.IsVisible = !doneMissing && !audio;
        DoneStage.IsVisible = doneMissing || !audio;
        AudioDoneRow.IsVisible = audio && !doneMissing;
        AudioDoneName.Text = doneVideo.FileName;
        bool mp3 = doneVideo.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        AudioDoneInfo.Text = Loc.T("exp.done.info", Mp4Exporter.FormatTime(doneDurationSec), mp3 ? "MP3 320 kbps" : Loc.T("exp.wav24"))
            + (doneVideo.Lufs is { } al ? $" · {Lufs(al)} LUFS" : "");
        DoneRevealButton.IsEnabled = !doneMissing;

        var parts = new List<string>();
        if (doneBytes is > 0) parts.Add(Mp4Exporter.FormatBytes(doneBytes.Value));
        if (doneVideo.ElapsedSec is { } el) parts.Add(Loc.T("exp.done.in", Loc.T(audio ? "exp.done.exported" : "exp.done.rendered"), Mp4Exporter.FormatTime(el)));
        if (doneVideo.ElapsedSec != null && doneStems != null) parts.Add(doneStems);
        if (doneVideo.Lufs is { } l && !audio) parts.Add($"{Lufs(l)} LUFS");
        if (doneVideo.TailSec >= 0.1) parts.Add(Loc.T("exp.done.tail", doneVideo.TailSec.ToString("0.0")));
        ToolTip.SetTip(DoneMetaText, doneVideo.Lufs != null ? Loc.T("exp.done.lufs.tip") : null);
        DoneMetaText.Text = string.Join(" · ", parts);
    }

    /// <summary>"−14,0": loudness with one decimal and a real minus sign, in the system's culture.</summary>
    static string Lufs(double v) => v.ToString("0.0").Replace('-', '−');

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
        return new ExportSummary(w, h, p.Settings.Aspect, Mp4Exporter.PanelCount(takes), maxDur);
    }

    public static List<ExportTakeSpec> ExtractVideoTakes(ProjectStore store, IReadOnlyList<LiveTrack>? liveTracks = null)
    {
        var p = store.Project;
        var takes = new List<ExportTakeSpec>();
        int panel = 0;
        foreach (var trk in p.Tracks)
        {
            if (!trk.Video || trk.Clips.Count == 0) continue;
            // the track gets a panel when its first clip has a picture, like the preview; all its clips share it
            if (!VideoExists(trk.Clips[0])) continue;
            foreach (var c in trk.Clips)
                if (VideoExists(c)) takes.Add(TakeSpec(trk, c, panel));
            panel++;
        }
        return takes;

        bool VideoExists(Clip c) => store.Resolve(p.FindMedia(c.MediaId)?.Video) is { } v && File.Exists(v);

        ExportTakeSpec TakeSpec(Track trk, Clip c, int panel)
        {
            var m = p.FindMedia(c.MediaId);
            string videoPath = store.Resolve(m?.Video)!;

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

            return new ExportTakeSpec
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
                Transition = c.Transition,
                TransitionSec = c.TransitionSec,
                Panel = panel,
            };
        }
    }

    /// <summary>
    /// The mix as <see cref="MixRenderer"/> takes it: every track's clips (the loaded audio of <paramref name="liveTracks"/>
    /// when there is, else loaded with <see cref="AudioLoader"/>) and channel, and the buses with the master.
    /// </summary>
    public static (List<MixTrackSpec> Tracks, MixBusSpec Bus) BuildMix(ProjectStore store, IReadOnlyList<LiveTrack>? liveTracks = null,
        int sampleRate = PlaybackEngine.SampleRate)
    {
        var p = store.Project;
        var mixTracks = new List<MixTrackSpec>();
        var nodes = ChannelDsp.Nodes(p);
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
                // what the clip plays when it is already its track's transpose, else the cache's (or made now)
                float[]? l = null, r = null;
                if (lc?.Sound is { } sound && lc.SoundSemis == trk.Transpose)
                {
                    l = sound.L;
                    r = sound.R;
                }
                else if (lc?.Audio is { } live && store.Resolve(m?.Audio) is { } livePath)
                    (l, r) = store.TrackAudio(trk, livePath, live.L, live.R);
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
                    (l, r) = store.TrackAudio(trk, audioPath, cached.L, cached.R);
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
                    GainCurve = c.CurvePoints(),
                    SyncMs = c.EffectiveSyncMs(m),
                    Transition = c.Transition,
                    TransitionSec = c.TransitionSec,
                    TakeId = c.MediaId,
                });
            }

            mixTracks.Add(ChannelDsp.Track(p, nodes, trk, mixClips, firstRmsDb ?? -24.0));
        }

        var mixBus = ChannelDsp.Bus(p);
        return (mixTracks, mixBus);
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
        var (mixTracks, mixBus) = BuildMix(store, liveTracks, sampleRate);

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
            Resolution = options.Resolution,
            BitrateMbps = options.BitrateMbps,
            Layout = p.Settings.Layout,
            AspectRatio = ExportTargets.AspectFor(options.Target, p.Settings.Aspect),
            TargetLufs = options.TargetLufs,
            // Configuración → Guardado y exportación: the video card's encoder if there is one, or the processor
            Encoder = VideoEncoders.ParsePreference(AppConfig.Get(VideoEncoders.ConfigKey)),
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
                col.Fade,
                col.Whites,
                col.Blacks,
                col.Light),
            VideoFx = new ExportVideoFxSpec(
                vfx.On,
                vfx.Vignette,
                vfx.VignetteSize,
                vfx.Grain,
                vfx.Letterbox,
                vfx.LetterboxHeight,
                vfx.Sharpness,
                vfx.Clarity),
            Takes = takes,
            MixTracks = mixTracks,
            MixBus = mixBus,
            SampleRate = sampleRate,
        };
    }

    /// <summary>Records a finished export into <see cref="Project.Exports"/>, <see cref="Project.Settings"/> and <see cref="Project.Log"/>.</summary>
    public static void RecordExport(Project project, ExportOptions options, ExportResult result, AudioFormat? audioFormat = null)
    {
        project.Settings.Export.Dir = string.IsNullOrWhiteSpace(options.OutDir) ? null : options.OutDir;
        if (audioFormat is { } f) project.Settings.Export.AudioFormat = AudioExporter.FormatId(f);
        else
        {
            project.Settings.Export.Quality = Mp4Exporter.QualityToId(options.Quality);
            project.Settings.Export.Target = ExportTargets.Get(options.Target).Id;
            project.Settings.Export.Resolution = Mp4Exporter.NormalizeResolution(options.Resolution);
            project.Settings.Export.BitrateMbps = Mp4Exporter.NormalizeBitrate(options.BitrateMbps);
        }
        project.Exports.Insert(0, new ExportRecord
        {
            File = result.File,
            CreatedAt = DateTime.UtcNow,
            Layout = audioFormat == null ? project.Settings.Layout : null,
            Aspect = audioFormat == null ? ExportTargets.AspectFor(options.Target, project.Settings.Aspect) : null,
            Target = audioFormat == null && options.Target != ExportTargets.Project ? options.Target : null,
            Lufs = double.IsFinite(result.Lufs) ? Math.Round(result.Lufs, 1) : null,
            Quality = audioFormat is { } af ? AudioExporter.FormatId(af) : Mp4Exporter.QualityToId(options.Quality),
            DurationSec = Math.Round(result.DurationSec, 2),
            Bytes = result.Bytes,
            // the takes with a panel in the video, as the dialog's summary counts them; for audio, every take in the mix
            TakeIds = audioFormat != null
                ? project.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaId).Distinct().ToList()
                : project.Tracks.Where(t => t.Video).SelectMany(t => t.Clips)
                    .Where(c => project.FindMedia(c.MediaId)?.Video != null).Select(c => c.MediaId).Distinct().ToList(),
        });
        if (project.Exports.Count > 50)
            project.Exports.RemoveRange(50, project.Exports.Count - 50);
        project.AddLog("exported", result.FileName);
    }

    IBrush Res(string key) => (IBrush)this.FindResource(key)!;
}
