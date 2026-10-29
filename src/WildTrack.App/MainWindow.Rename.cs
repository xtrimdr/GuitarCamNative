using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WildTrack.Projects;

namespace WildTrack.App;

/// <summary>
/// Renaming a track, a folder or a marker right where it is drawn (native only, asked by the user), like a DAW: a
/// double click on the name (or "Renombrar…" in its menu) lays a text box over it. Enter or a click elsewhere keeps the name, Esc
/// drops it; a scroll keeps it too, since the box would no longer sit on its row. Each rename is one undo step.
/// </summary>
public partial class MainWindow
{
    readonly TextBox nameEditor = new()
    {
        Classes = { "field" }, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        FontSize = 12.5, FontWeight = FontWeight.SemiBold, Padding = new Thickness(4, 0), MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Center,
    };
    string? nameEditId;
    /// <summary>Narrowest box for a marker's name.</summary>
    const double MarkerEditorW = 140;

    void InitNameEditor()
    {
        TimelineHost.Children.Add(nameEditor);
        Timeline.NameEditRequested += BeginNameEdit;
        nameEditor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { EndNameEdit(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndNameEdit(false); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        nameEditor.LostFocus += (_, _) => EndNameEdit(true);
        Timeline.AddHandler(PointerWheelChangedEvent, (_, _) => EndNameEdit(true), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Renames in place when the row is on screen, otherwise falls back to <paramref name="elsewhere"/>.</summary>
    void RenameInPlace(string id, Action elsewhere)
    {
        // the menu closes first: the box would lose the focus to it
        Dispatcher.UIThread.Post(() =>
        {
            if (Timeline.IsEffectivelyVisible && Timeline.NameRectOf(id) is { } r) BeginNameEdit(id, r);
            else elsewhere();
        }, DispatcherPriority.Background);
    }

    void BeginNameEdit(string id, Rect r)
    {
        if (store == null || store.ReadOnly || restoring) return;
        EndNameEdit(true);
        var marker = store.Project.Markers.Find(m => m.Id == id);
        string? name = FolderById(id)?.Name ?? tracks.Find(t => t.Track.Id == id)?.Track.Name ?? marker?.Name;
        if (name == null) return;
        nameEditId = id;
        Timeline.EditingNameId = id;
        var origin = Timeline.TranslatePoint(default, TimelineHost) ?? default;
        nameEditor.Margin = new Thickness(origin.X + r.X, origin.Y + r.Y, 0, 0);
        // a marker's flag is as wide as its name: the box leaves room to write a longer one
        nameEditor.Width = marker != null ? Math.Max(r.Width, MarkerEditorW) : r.Width;
        nameEditor.Height = r.Height;
        nameEditor.Text = name;
        nameEditor.IsVisible = true;
        Dispatcher.UIThread.Post(() => { nameEditor.Focus(); nameEditor.SelectAll(); });
    }

    void EndNameEdit(bool keep)
    {
        if (nameEditId is not { } id) return;
        nameEditId = null;
        string text = nameEditor.Text?.Trim() ?? "";
        bool focused = nameEditor.IsFocused;
        nameEditor.IsVisible = false;
        Timeline.EditingNameId = null;
        if (focused) Timeline.Focus();
        if (!keep || text.Length == 0) return;
        if (FolderById(id) is { } f)
        {
            if (f.Name != text) FolderEdit(f, x => x.Name = text, "name");
        }
        else if (store?.Project.Markers.Find(m => m.Id == id) is { } m)
        {
            if (m.Name != text) MarkerEdit(p => ProjectMarkers.Rename(p, id, text), $"marker:{id}:name");
        }
        else if (tracks.Find(t => t.Track.Id == id) is { } t && t.Track.Name != text) EditTrack(t, x => x.Track.Name = text, "name");
    }
}
