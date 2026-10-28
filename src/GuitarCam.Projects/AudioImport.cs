using GuitarCam.Audio;
using NAudio.Wave;

namespace GuitarCam.Projects;

/// <summary>
/// Brings an audio file from outside (a backing track, a loop, a take made elsewhere) into the project
/// (native only: the web only plays what it records). Two steps so the slow one can run off the UI thread:
/// <see cref="CopyIn"/> copies the file into media/ and reads its format, <see cref="AddClip"/> then puts it
/// on a track of the project. The original is never touched and the copy is never rewritten, like any media;
/// it keeps its own extension (the loaders pick the decoder by it) and gets a unique name in media/.
/// The track is audio only (no video panel) with a neutral strip: no compressor or saturation, because
/// an imported mix is already finished.
/// </summary>
public static class AudioImport
{
    /// <summary>What the file dialog and drag and drop accept; the same list <c>ProjectStore</c> adopts from media/.</summary>
    public static readonly string[] Extensions = [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aif", ".aiff"];

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>A file already copied into media/, ready for <see cref="AddClip"/>.</summary>
    public sealed record Imported(string Name, string RelativePath, double DurationSec, int SampleRate, int Channels);

    /// <summary>
    /// Copies <paramref name="source"/> into the project's media/ folder (through a temporary name, so a cut
    /// never leaves half a file that looks like media) and reads its length and format. Throws
    /// <see cref="InvalidDataException"/> with a message for the user when the file can't be read as audio;
    /// in that case nothing is left in media/.
    /// </summary>
    public static Imported CopyIn(string projectDir, string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("No encuentro ese archivo.");
        if (!IsSupported(source))
            throw new InvalidDataException(Texts.T("err.import.format", "No sé leer \"{0}\": probá con WAV, MP3, FLAC, OGG, M4A o AIFF.", Path.GetFileName(source)));
        string mediaDir = Path.Combine(projectDir, ProjectStore.MediaDir);
        Directory.CreateDirectory(mediaDir);
        string ext = Path.GetExtension(source).ToLowerInvariant();
        string stem = string.Concat(Path.GetFileNameWithoutExtension(source).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (stem.Length > 100) stem = stem[..100].TrimEnd();
        if (stem.Length == 0) stem = "Audio";
        string dest = UniquePath(mediaDir, stem, ext);
        string tmp = dest + ".importando";
        try
        {
            double dur;
            int rate, ch;
            try
            {
                // Read the original: the reader picks its decoder by extension, which the temp name doesn't have.
                // Measured by decoding, not from the header: some formats report a wrong length there.
                (dur, rate, ch) = GuitarCam.Audio.AudioLoader.Measure(source);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidDataException($"\"{Path.GetFileName(source)}\" no se pudo leer como audio ({e.Message.TrimEnd('.')}).");
            }
            if (dur <= 0) throw new InvalidDataException($"\"{Path.GetFileName(source)}\" no tiene audio.");
            File.Copy(source, tmp, overwrite: true);
            File.SetAttributes(tmp, FileAttributes.Normal);
            File.Move(tmp, dest);
            return new Imported(Path.GetFileNameWithoutExtension(source).Trim(), $"{ProjectStore.MediaDir}/{Path.GetFileName(dest)}", dur, rate, ch);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    static string UniquePath(string dir, string stem, string ext)
    {
        string path = Path.Combine(dir, stem + ext);
        for (int n = 2; File.Exists(path) || File.Exists(path + ".importando"); n++) path = Path.Combine(dir, $"{stem} ({n}){ext}");
        return path;
    }

    /// <summary>
    /// Adds the media and a clip of it at <paramref name="atSec"/> on a new audio-only track at the end; with
    /// <paramref name="trackId"/> of an empty track it takes that one instead (like a take does). Returns the track.
    /// </summary>
    public static Track AddClip(Project p, Imported file, double atSec, string? trackId = null)
    {
        var media = new Media
        {
            Name = file.Name,
            Kind = "import",
            Audio = file.RelativePath,
            DurationSec = file.DurationSec,
            SampleRate = file.SampleRate,
            Channels = file.Channels,
            SyncMs = 0,
        };
        p.Media.Add(media);
        var track = p.Tracks.Find(t => t.Id == trackId && t.Clips.Count == 0 && !t.IsMidi);
        if (track == null)
        {
            track = new Track { Video = false };
            p.Tracks.Add(track);
        }
        track.Name = file.Name;
        track.Video = false;
        track.Clips.Add(new Clip { MediaId = media.Id, AtSec = Math.Max(0, Math.Round(atSec, 3)) });
        p.AddLog("audio-imported", file.Name);
        return track;
    }
}
