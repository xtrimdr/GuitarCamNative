using System.Reflection;
using System.Runtime.CompilerServices;
using GuitarCam.App.Model;

namespace GuitarCam.App.Audio;

/// <summary>
/// Gets the audio code ready before anything plays, like a DAW does on load. The ASIO callback is managed code:
/// whatever only runs while playing or recording (clip mixing, saturation, compressor, EQ, loudness, the take
/// writer) was JIT-compiled the first time it was called, inside the callback, and the first play of a session
/// overran its buffer (a 4 ms block took 5.8 ms). <see cref="Start"/> compiles every method of the audio engine,
/// the DSP and the live track model on a background thread at startup and runs their static constructors.
/// <see cref="Compact"/> does a full collection once a project has loaded, while stopped, so the garbage the
/// load left does not come due during the first play.
/// </summary>
public static class AudioWarmUp
{
    public static void Start() => new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "audio warm-up" }.Start();

    static void Run()
    {
        // the engine assembly (DSP, recording) and the window's real-time half (PlaybackEngine, LiveTrack)
        var types = typeof(GuitarCam.Audio.Dsp.Compressor).Assembly.GetTypes()
            .Concat(typeof(LiveTrack).Assembly.GetTypes().Where(t => t.Namespace is "GuitarCam.App.Audio" or "GuitarCam.App.Model"));
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var t in types)
        {
            if (t.ContainsGenericParameters) continue;
            try
            {
                RuntimeHelpers.RunClassConstructor(t.TypeHandle);
            }
            catch
            {
                // a type that can't initialize here will do it (and fail the same way) when it is used
            }
            foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(m.MethodHandle);
                }
                catch
                {
                    // not compilable ahead (e.g. needs a generic context): it compiles on first call as before
                }
            }
        }
    }

    /// <summary>A full, compacting collection; call it only while stopped (a blocking GC stalls the callback).</summary>
    public static void Compact() => GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
}
