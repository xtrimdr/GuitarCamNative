namespace GuitarCam.Audio.Dsp;

/// <summary>
/// Port of <c>GuitarCam/src/shared/clip.js</c>: clip timeline window, fade lengths and fade gain envelope
/// shared by playback, timeline and offline export render.
/// </summary>
public static class ClipEnvelope
{
    public const double MinClipSec = 0.1;

    public readonly record struct ClipWindow(
        double Origin,
        double Dur,
        double TrimStart,
        double TrimEnd,
        double Cut,
        double AnchorCut,
        double Anchor,
        double Start,
        double End);

    public readonly record struct FadePair(double FadeIn, double FadeOut);

    static double Num(double v, double fallback = 0) => double.IsFinite(v) ? v : fallback;

    /// <summary>
    /// Port of <c>clipWindow</c> in <c>shared/clip.js</c>. Note that in the native project model,
    /// <c>AtSec</c> is already in seconds (corresponding to <c>audioOffsetMs / 1000</c> in the web app),
    /// while <c>syncMs</c> is in milliseconds.
    /// </summary>
    public static ClipWindow Window(
        double durationSec,
        double atSec,
        double trimStartSec = 0,
        double trimEndSec = 0,
        double syncMs = 0)
    {
        double dur = Math.Max(0, Num(durationSec));
        double origin = Num(atSec);
        double trimStart = Math.Clamp(Num(trimStartSec), 0, dur);
        double trimEnd = Math.Clamp(Num(trimEndSec), 0, dur - trimStart);
        double end = origin + dur - trimEnd;
        double rawStart = origin + trimStart;
        double start = rawStart < 0 ? Math.Min(0, end) : rawStart;
        double anchor = Math.Max(rawStart, origin - Num(syncMs) / 1000.0);
        double cut = start - rawStart;
        double anchorCut = Math.Max(0, Math.Min(cut, -anchor));
        return new ClipWindow(origin, dur, trimStart, trimEnd, cut, anchorCut, anchor, start, end);
    }

    public static FadePair FitFades(double fadeInSec, double fadeOutSec, double visibleLenSec)
    {
        double fi = Math.Max(0, Num(fadeInSec));
        double fo = Math.Max(0, Num(fadeOutSec));
        double len = Math.Max(0, visibleLenSec);
        if (fi + fo > len && fi + fo > 0)
        {
            double k = len / (fi + fo);
            fi *= k;
            fo *= k;
        }
        return new FadePair(fi, fo);
    }

    public static (FadePair Audio, FadePair Video) Fades(
        ClipWindow w,
        double fadeInSec,
        double fadeOutSec,
        bool linkVideoFades = true,
        double videoFadeInSec = 0,
        double videoFadeOutSec = 0)
    {
        double len = Math.Max(0, w.End - w.Start);
        var audio = FitFades(fadeInSec, fadeOutSec, len);
        var video = linkVideoFades ? audio : FitFades(videoFadeInSec, videoFadeOutSec, len);
        return (audio, video);
    }

    public static double GainAt(double timeSec, double startSec, double endSec, double fadeInSec, double fadeOutSec)
    {
        if (timeSec < startSec || timeSec > endSec) return 0;
        double g = 1;
        if (fadeInSec > 0) g = Math.Min(g, (timeSec - startSec) / fadeInSec);
        if (fadeOutSec > 0) g = Math.Min(g, (endSec - timeSec) / fadeOutSec);
        return Math.Clamp(g, 0, 1);
    }
}
