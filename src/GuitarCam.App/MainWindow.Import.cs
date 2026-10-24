using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using GuitarCam.Projects;

namespace GuitarCam.App;

/// <summary>
/// Bringing outside audio into the project (native only, asked by the user): "Importar audio…" in the new-track
/// menu, or dropping files on the window. Each file is copied into the project's media/ in the background
/// (<see cref="AudioImport.CopyIn"/>) and lands on its own audio-only track at the playhead, as one undo step;
/// the selected empty track takes the first one, like a take does.
/// </summary>
public partial class MainWindow
{
    static readonly FilePickerFileType AudioFiles = new("Audio")
    {
        Patterns = AudioImport.Extensions.Select(e => "*" + e).ToList(),
    };

    void InitImport()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = CanImport() && DroppedFiles(e).Any() ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var files = DroppedFiles(e).ToList();
            e.Handled = true;
            if (files.Count > 0) await ImportAudio(files);
        });
    }

    static IEnumerable<string> DroppedFiles(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().Where(f => File.Exists(f));

    bool CanImport() => store != null && !store.ReadOnly && !restoring && !Projects.IsVisible && !RecordingBusy;

    async Task PickAudioToImport()
    {
        if (!CanImport()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar audio",
            AllowMultiple = true,
            FileTypeFilter = [AudioFiles, FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await ImportAudio(paths);
    }

    async Task ImportAudio(IReadOnlyList<string> paths)
    {
        if (!CanImport()) { Status("Ahora no puedo importar: terminá lo que estás haciendo y probá de nuevo."); return; }
        var st = store!;
        string dir = st.Dir;
        int done = 0;
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            if (!AudioImport.IsSupported(path))
            {
                Status($"No sé leer \"{name}\": probá con WAV, MP3, FLAC, OGG, M4A o AIFF.");
                continue;
            }
            Status($"Importando \"{name}\"…");
            AudioImport.Imported file;
            try { file = await Task.Run(() => AudioImport.CopyIn(dir, path)); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Status($"No pude importar \"{name}\": {e.Message.TrimEnd('.')}.");
                continue;
            }
            // the project may have changed while the file was copied
            if (store != st || !CanImport()) { Status($"No pude sumar \"{name}\" al timeline porque cambiaste de proyecto; quedó en la carpeta media/."); return; }
            double at = engine.PositionSec;
            string? selected = done == 0 ? sel?.Track.Id : null;
            await EditTimeline(p => AudioImport.AddClip(p, file, at, selected).Id, "audio-imported");
            done++;
        }
        if (done > 0) Status(done == 1 ? "Importé el audio y lo dejé en una pista nueva." : $"Importé {done} audios, cada uno en su pista.");
    }
}
