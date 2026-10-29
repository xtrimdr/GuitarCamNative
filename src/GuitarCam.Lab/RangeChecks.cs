using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.Lab;

/// <summary>
/// The rows of comprobar-dsp for exporting a stretch (<see cref="ExportRange"/>): inside A–B the mix is the song's
/// sample for sample, it comes in from silence at A, the dry sound stops at B while a reverb send rings past it, and
/// the video keeps only the takes that show there, trimmed and moved so A is 0:00 (checked on the FFmpeg command).
/// </summary>
static class RangeChecks
{
    const int Sr = 48000;

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        var tone = new float[Sr * 4];
        for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 220 * i / Sr));
        MixTrackSpec[] wet = [new MixTrackSpec { Clips = [new MixClipSpec { Left = tone, Right = tone, DurationSec = 4 }], Sends = [new MixSendSpec { To = 0, Level = 0.6 }] }];
        MixTrackSpec[] dry = [new MixTrackSpec { Clips = [new MixClipSpec { Left = tone, Right = tone, DurationSec = 4 }] }];
        var bus = BusCommands.DefaultBus(110);
        var range = new ExportRange(1, 3);

        // 1. between A and B (past the fades) it is the song itself, reverb included
        var (full, _) = MixRenderer.Render(wet, bus, 4, Sr, limit: false);
        var (cutL, cutR) = MixRenderer.Render(wet, bus, 5, Sr, limit: false, stopSec: range.EndSec);
        (cutL, cutR) = ExportRange.From(cutL, cutR, range.StartSec, Sr);
        int a = (int)(range.StartSec * Sr), inFade = (int)(ExportRange.StartFadeSec * Sr);
        int stop = (int)((range.LengthSec - ExportRange.StopFadeSec) * Sr);
        double diff = 0;
        for (int i = inFade; i < stop; i++) diff = Math.Max(diff, Math.Abs(cutL[i] - full[a + i]));
        Row("Rango: adentro suena como el tema", $"{diff:0.0e0}", "0", diff < 1e-6);
        Row("Rango: entra desde silencio en A", $"{Math.Abs(cutL[0]):0.0e0}", "0", cutL[0] == 0 && cutR[0] == 0);

        // 2. without a send the take goes quiet at B: only its chain's filters ring on, under -80 dB 50 ms later and
        // dying away (under -140 dB by 300 ms); with the reverb send the tail rings past it
        var (dryL, _) = MixRenderer.Render(dry, bus, 5, Sr, limit: false, stopSec: range.EndSec);
        float Peak(double fromSec)
        {
            float pk = 0;
            for (int i = (int)(fromSec * Sr); i < dryL.Length; i++) pk = Math.Max(pk, Math.Abs(dryL[i]));
            return pk;
        }
        float soon = Peak(range.EndSec + 0.05), late = Peak(range.EndSec + 0.3);
        Row("Rango: la toma calla en B", $"{soon:0.0e0}", "< 1e-4", soon < 1e-4 && late < 1e-7);
        var mastered = MixMaster.Render(wet, bus, 4, Sr, MixMaster.MaxTailSec, null, null, default, range);
        bool tailOk = mastered.TailSec > 0.2 && Math.Abs(mastered.DurationSec - range.LengthSec - mastered.TailSec) < 1e-3;
        Row("Rango: la reverb suena después de B", $"{mastered.TailSec:0.00} s", "> 0,2 s", tailOk);

        // 3. the range is kept inside the song, and one too short is none
        var fit = ExportRange.Fit(new ExportRange(5, 2), 4);
        Row("Rango: se acomoda al tema", fit is { } f ? $"{f.StartSec:0}-{f.EndSec:0}" : "nada", "2-4", fit == new ExportRange(2, 4));
        Row("Rango: muy corto no exporta", ExportRange.Fit(new ExportRange(3.8, 9), 4) is null ? "nada" : "algo", "nada", ExportRange.Fit(new ExportRange(3.8, 9), 4) is null);

        // 4. the takes: one over A (cut there), one past B (out), one inside with its fade; panels renumbered
        string tmp = Path.Combine(Path.GetTempPath(), $"gclab-rango-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            string video = Path.Combine(tmp, "toma.mp4");
            File.WriteAllBytes(video, []);
            ExportTakeSpec[] takes =
            [
                new() { VideoPath = video, DurationSec = 5, AtSec = 0, FadeInSec = 0.5, Transition = ClipTransitions.CrossfadeId },
                new() { VideoPath = video, DurationSec = 2, AtSec = 6 },
                new() { VideoPath = video, DurationSec = 4, AtSec = 2, FadeInSec = 0.5 },
            ];
            var kept = ExportRange.Takes(takes, range);
            bool takesOk = kept.Count == 2
                && kept[0].Window is { Start: 0, End: 2 } && kept[0].FadeInSec == 0 && kept[0].Transition == ClipTransitions.CutId && kept[0].Panel == 0
                && Math.Abs(kept[1].Window.Start - 1) < 1e-9 && Math.Abs(kept[1].Window.End - 2) < 1e-9 && kept[1].FadeInSec == 0.5 && kept[1].Panel == 1;
            Row("Rango: tomas recortadas y movidas", $"{kept.Count} tomas", "2", takesOk);

            var job = new ExportJobSpec { ProjectDir = tmp, Takes = takes, MixTracks = dry, MixBus = bus, Range = range };
            var built = Mp4Exporter.BuildFfmpegCommand(job, "mezcla.wav", "salida.part", audioSec: range.LengthSec);
            // the first take is cut at A: its video starts 1 s in, plus the camera latency (80 ms)
            bool cmdOk = Math.Abs(built.MaxDurationSec - range.LengthSec) < 1e-9 && built.FilterComplex.Contains("trim=start=1.080:end=3.080");
            Row("Rango: el video arranca en A", $"{built.MaxDurationSec:0.00} s", "2,00 s", cmdOk);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return allOk;
    }
}
