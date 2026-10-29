namespace WildTrack.Audio;

/// <summary>
/// Runs bands of work (rows or columns of a video frame) on a few threads of its own plus the caller's, and returns
/// when all of them are done. It exists instead of <see cref="Parallel.For(int, int, Action{int})"/> because that
/// allocates on every call, and the preview filters frames on the UI tick, where garbage turns into audio gaps.
/// The body is a delegate the caller keeps (no closure per call); one run at a time.
/// </summary>
public static class BandPool
{
    /// <summary>Helper threads besides the caller: half the cores, enough for a 640-wide frame without crowding the decoders.</summary>
    static readonly int Helpers = Math.Clamp(Environment.ProcessorCount / 2 - 1, 0, 5);

    static readonly object Gate = new();
    static readonly SemaphoreSlim Start = new(0);
    static readonly ManualResetEventSlim Done = new(false);
    static bool started;
    static Action<int>? body;
    static int count, next, pending;

    /// <summary>Calls <paramref name="work"/> once for each band 0..<paramref name="bands"/>-1, spread over the threads.</summary>
    public static void Run(int bands, Action<int> work)
    {
        if (bands <= 1 || Helpers == 0)
        {
            for (int i = 0; i < bands; i++) work(i);
            return;
        }
        lock (Gate)
        {
            if (!started)
            {
                for (int i = 0; i < Helpers; i++)
                    new Thread(Loop) { IsBackground = true, Name = "video detail" }.Start();
                started = true;
            }
            int helpers = Math.Min(Helpers, bands - 1);
            body = work;
            count = bands;
            next = -1;
            pending = helpers;
            Done.Reset();
            Start.Release(helpers);
            Drain();
            // every woken helper checks in, so none is still reading body when the next run sets it
            Done.Wait();
            body = null;
        }
    }

    static void Drain()
    {
        var b = body!;
        int i;
        while ((i = Interlocked.Increment(ref next)) < count) b(i);
    }

    static void Loop()
    {
        while (true)
        {
            Start.Wait();
            Drain();
            if (Interlocked.Decrement(ref pending) == 0) Done.Set();
        }
    }
}
