using Avalonia.Controls;
using Avalonia.Input;

namespace WildTrack.App.Dialogs;

/// <summary>
/// "¿Guardo los cambios?", asked when the window closes with edits that haven't reached guitarcam.json (only
/// possible when <see cref="SaveMode"/> saves every few minutes or by hand, or after a failed save). Same frame as
/// <see cref="AboutDialog"/>. Takes are always saved already, so "Cerrar sin guardar" only drops edits. Enter saves,
/// Escape or a click outside cancels: the safe choices are the easy ones.
/// </summary>
public sealed partial class UnsavedDialog : UserControl
{
    public enum Choice { Save, Discard, Cancel }

    TaskCompletionSource<Choice>? pending;

    public UnsavedDialog()
    {
        InitializeComponent();
        SaveButton.Click += (_, _) => Answer(Choice.Save);
        DiscardButton.Click += (_, _) => Answer(Choice.Discard);
        CancelButton.Click += (_, _) => Answer(Choice.Cancel);
        PointerPressed += (_, e) => { if (e.Source == this) Answer(Choice.Cancel); };
    }

    /// <summary>Shows the question for <paramref name="project"/> and waits for the answer.</summary>
    public Task<Choice> Ask(string project)
    {
        pending?.TrySetResult(Choice.Cancel);
        pending = new TaskCompletionSource<Choice>();
        Body.Text = Loc.T("unsaved.body", project);
        IsVisible = true;
        SaveButton.Focus();
        return pending.Task;
    }

    void Answer(Choice c)
    {
        IsVisible = false;
        var p = pending;
        pending = null;
        p?.TrySetResult(c);
    }

    /// <summary>Enter saves, Escape cancels; every other key stays here while it is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Escape) Answer(Choice.Cancel);
        else if (e.Key is Key.Enter or Key.Space)
            Answer(e.Source == DiscardButton ? Choice.Discard : e.Source == CancelButton ? Choice.Cancel : Choice.Save);
        e.Handled = true;
        return true;
    }
}
