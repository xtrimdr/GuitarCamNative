namespace WildTrack.Audio.Dsp;

/// <summary>
/// One leg of the plugin delay compensation (PDC): delays what goes from a track or a node to where it adds up (its
/// output, or one of its sends) by <see cref="Target"/> frames, so every path into a sum arrives as late as the latest
/// one (<see cref="MixLatency"/>). Live and in the export alike. While it never had a delay it is a plain add; its ring
/// is allocated on the UI thread (<see cref="Reserve"/>, <see cref="Set"/>) and handed over whole: the audio thread
/// copies the old ring's history into a bigger one, so growing it loses nothing. A new delay crossfades from the old
/// one over <see cref="Fade"/> frames instead of jumping. <see cref="Add"/> never allocates.
/// </summary>
public sealed class LatencyDelay
{
    sealed class Ring(int size)
    {
        public readonly float[] L = new float[size], R = new float[size];
        public readonly int Mask = size - 1;
    }

    /// <summary>5 ms at 48 kHz: how long a change of delay crossfades.</summary>
    public const int Fade = 240;
    /// <summary>The smallest ring: a third of a second, so most changes never need a bigger one.</summary>
    const int MinRing = 16384;

    volatile Ring? wanted;
    volatile int target;
    volatile int clear;
    /// <summary>What the UI asked for before, so the ring also holds the delay the audio thread may still be fading from.</summary>
    int lastSet;
    // audio thread only
    Ring? ring;
    int write, current, from, fadeLeft;
    bool ran;

    /// <summary>The delay the audio thread goes to (frames). Any thread; past what the ring holds it is cut short.</summary>
    public int Target { get => target; set => target = Math.Max(0, value); }

    /// <summary>UI thread: a ring big enough for <paramref name="frames"/> of delay plus a whole block.</summary>
    public void Reserve(int frames)
    {
        int need = Math.Max(frames, lastSet) + TrackDspChain.MaxBlockSize + 1;
        if (frames <= 0 && wanted == null) return;
        if (wanted is { } w && w.L.Length >= need) return;
        int size = MinRing;
        while (size < need) size <<= 1;
        wanted = new Ring(size);
    }

    /// <summary>UI thread: the delay this leg needs now.</summary>
    public void Set(int frames)
    {
        frames = Math.Max(0, frames);
        Reserve(frames);
        lastSet = frames;
        target = frames;
    }

    /// <summary>Forgets what it holds (the transport starts: what was in it from before the stop must not come out). Any thread.</summary>
    public void Reset() => clear = 1;

    /// <summary>
    /// Audio thread: adds <paramref name="l"/> / <paramref name="r"/> times <paramref name="level"/> into the destination,
    /// <see cref="Target"/> frames late.
    /// </summary>
    public void Add(ReadOnlySpan<float> l, ReadOnlySpan<float> r, float level, Span<float> dstL, Span<float> dstR)
    {
        var w = wanted;
        if (w != ring) Swap(w);
        var rg = ring;
        if (rg == null)
        {
            BusDspChain.Add(l, r, level, dstL, dstR);
            ran = true;
            return;
        }
        if (clear != 0)
        {
            clear = 0;
            Array.Clear(rg.L);
            Array.Clear(rg.R);
            fadeLeft = 0;
        }
        int t = Math.Min(target, rg.Mask - TrackDspChain.MaxBlockSize);
        // nothing went through it yet (the export, a new send): it starts at its delay, there is nothing to fade from
        if (!ran) current = t;
        ran = true;
        if (t != current && fadeLeft == 0)
        {
            from = current;
            current = t;
            fadeLeft = Fade;
        }
        int mask = rg.Mask, pos = write;
        float[] rl = rg.L, rr = rg.R;
        for (int i = 0; i < l.Length; i++)
        {
            rl[pos & mask] = l[i];
            rr[pos & mask] = r[i];
            float yl = rl[(pos - current) & mask], yr = rr[(pos - current) & mask];
            if (fadeLeft > 0)
            {
                float a = fadeLeft-- / (float)Fade;
                yl += (rl[(pos - from) & mask] - yl) * a;
                yr += (rr[(pos - from) & mask] - yr) * a;
            }
            dstL[i] += yl * level;
            dstR[i] += yr * level;
            pos++;
        }
        write = pos;
    }

    /// <summary>The UI's new ring takes over, with the last stretch of the old one copied in at the same places.</summary>
    void Swap(Ring? next)
    {
        if (next != null && ring != null)
        {
            int n = Math.Min(ring.L.Length, next.L.Length);
            for (int i = 1; i <= n; i++)
            {
                next.L[(write - i) & next.Mask] = ring.L[(write - i) & ring.Mask];
                next.R[(write - i) & next.Mask] = ring.R[(write - i) & ring.Mask];
            }
        }
        ring = next;
    }
}

/// <summary>
/// Plugin delay compensation (PDC), one calculation for the live engine and the export: given how late each track's and
/// each node's slots put out their sound (<see cref="ITrackInsert.LatencySamples"/>) and where each one goes, how much
/// each leg (an output or a send) has to wait so that everything adding up in a node, and in the master, lines up. A node
/// takes the latest of what reaches it; the master too, and that is how late the whole mix is heard
/// (<see cref="Plan.Master"/>): the click and a recorded take are moved by the same amount.
/// </summary>
public static class MixLatency
{
    /// <summary>A track or a node: its own latency, where it goes (a node index, -1 = the master) and its sends' nodes.</summary>
    public readonly record struct Source(int Latency, int Out, int[] Sends);

    /// <summary>How long each leg waits: each track's output and sends, each node's output and sends, and how late the master is.</summary>
    public sealed record Plan(int[] TrackOut, int[][] TrackSends, int[] NodeOut, int[][] NodeSends, int Master);

    /// <summary><paramref name="nodes"/> go in their run order: a node's output and sends only reach later ones (or the master).</summary>
    public static Plan Compute(IReadOnlyList<Source> tracks, IReadOnlyList<Source> nodes)
    {
        int count = nodes.Count;
        // when each node's input arrives (the latest of what feeds it), and the master's
        var arrive = new int[count];
        int master = 0;
        int Valid(int to) => to >= 0 && to < count ? to : -1;
        void Reach(int to, int at)
        {
            if (to < 0) master = Math.Max(master, at);
            else arrive[to] = Math.Max(arrive[to], at);
        }
        foreach (var t in tracks)
        {
            Reach(Valid(t.Out), t.Latency);
            foreach (int s in t.Sends) if (Valid(s) >= 0) Reach(s, t.Latency);
        }
        var outAt = new int[count];
        for (int j = 0; j < count; j++)
        {
            outAt[j] = arrive[j] + nodes[j].Latency;
            Reach(Valid(nodes[j].Out), outAt[j]);
            foreach (int s in nodes[j].Sends) if (Valid(s) >= 0) Reach(s, outAt[j]);
        }
        int Wait(int to, int at) => to < 0 ? master - at : arrive[to] - at;
        var trackOut = new int[tracks.Count];
        var trackSends = new int[tracks.Count][];
        for (int k = 0; k < tracks.Count; k++)
        {
            var t = tracks[k];
            trackOut[k] = Wait(Valid(t.Out), t.Latency);
            trackSends[k] = [.. t.Sends.Select(s => Valid(s) >= 0 ? Wait(s, t.Latency) : 0)];
        }
        var nodeOut = new int[count];
        var nodeSends = new int[count][];
        for (int j = 0; j < count; j++)
        {
            nodeOut[j] = Wait(Valid(nodes[j].Out), outAt[j]);
            nodeSends[j] = [.. nodes[j].Sends.Select(s => Valid(s) >= 0 ? Wait(s, outAt[j]) : 0)];
        }
        return new Plan(trackOut, trackSends, nodeOut, nodeSends, master);
    }
}
