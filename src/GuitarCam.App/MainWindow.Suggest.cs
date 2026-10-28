using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GuitarCam.App.Audio;
using GuitarCam.App.Controls;
using GuitarCam.App.Dialogs;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App;

/// <summary>
/// "Sugerime" in the Look panel (asked by the user: it shows what it would do and you confirm). It listens to the
/// whole mix before the master in the background (<see cref="MasterSuggest.Analyze"/>, the mix built from what is in
/// memory, like the Destino's measurement) and opens a card under the button with a line per proposal and Aplicar /
/// Cancelar. Aplicar is one edit (one undo) and leaves the look as "by hand".
/// </summary>
public partial class MainWindow
{
    CancellationTokenSource? suggestCts;

    async Task Suggest(Button button)
    {
        var st = store;
        if (st == null || st.ReadOnly) return;
        if (RecordingBusy || !MixInMemory())
        {
            Status(Loc.T("master.suggest.wait"));
            return;
        }
        if (!st.Project.Tracks.Any(t => t.HasContent))
        {
            Status(Loc.T("master.suggest.empty"));
            return;
        }
        var (mixTracks, bus) = ExportDialog.BuildMix(st, tracks);
        double content = Math.Max(1, MixRenderer.MixDurationSec(mixTracks, PlaybackEngine.SampleRate));
        suggestCts?.Cancel();
        var cts = suggestCts = new CancellationTokenSource();
        var current = st.Project.Settings.Master.ToParams();
        button.IsEnabled = false;
        button.Content = Loc.T("master.suggest.busy");
        try
        {
            var sug = await Task.Run(() => MasterSuggest.Suggest(
                MasterSuggest.Analyze(mixTracks, bus, content, PlaybackEngine.SampleRate, cts.Token), current), cts.Token);
            if (store != st || cts.IsCancellationRequested) return;
            ShowSuggestion(button, sug);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Status(Loc.T("master.suggest.failed", e.Message));
        }
        finally
        {
            if (suggestCts == cts) suggestCts = null;
            cts.Dispose();
            button.Content = Loc.T("master.suggest");
            button.IsEnabled = store is { ReadOnly: false };
        }
    }

    /// <summary>The proposal under the button: what it heard and would change, a line each, and Aplicar / Cancelar.</summary>
    void ShowSuggestion(Control anchor, MasterSuggestion sug)
    {
        var card = new StackPanel { Spacing = 8, Width = 300, Margin = new Thickness(4) };
        card.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("master.suggest.title").ToUpperInvariant() });
        foreach (var line in sug.Lines)
            card.Children.Add(new TextBlock { Text = "· " + line.Text, FontSize = 12, Foreground = Palette.Dim, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var cancel = new Button { Classes = { "btn" }, Content = Loc.T("master.suggest.cancel") };
        var apply = new Button { Classes = { "btn", "primary" }, Content = Loc.T("master.suggest.apply"), Padding = new Thickness(14, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        card.Children.Add(buttons);
        var fly = new Flyout { Content = card, Placement = PlacementMode.BottomEdgeAlignedLeft };
        cancel.Click += (_, _) => fly.Hide();
        apply.Click += (_, _) =>
        {
            fly.Hide();
            MasterEdit(m => m.ApplySuggestion(sug.Params), "suggest");
        };
        fly.ShowAt(anchor);
    }
}
