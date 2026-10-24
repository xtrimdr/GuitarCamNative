namespace GuitarCam.Audio;

/// <summary>
/// Which driver inputs an armed track records from (native only: the web records one input pair). A mono
/// input goes to both sides of the take; a pair goes left / right. Saved on the track as "3" or "3-4"
/// (1-based, like the interface's labels); no route means the input of Configuración (1-2 and its mode).
/// </summary>
public readonly record struct InputRoute(int First, bool Stereo)
{
    /// <summary>"3" or "3-4" (1-based); anything else is no route.</summary>
    public static InputRoute? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split('-');
        if (!int.TryParse(parts[0], out int a) || a < 1) return null;
        if (parts.Length == 1) return new InputRoute(a - 1, false);
        return parts.Length == 2 && int.TryParse(parts[1], out int b) && b == a + 1 ? new InputRoute(a - 1, true) : null;
    }

    public string Key => Stereo ? $"{First + 1}-{First + 2}" : $"{First + 1}";
    public string Label => Stereo ? $"Entradas {Key}" : $"Entrada {Key}";
    /// <summary>The last driver input it reads; a route past the driver's inputs records silence.</summary>
    public int Last => First + (Stereo ? 1 : 0);

    /// <summary>Every input on its own, then the pairs (1-2, 3-4…), for the selector.</summary>
    public static List<InputRoute> All(int inputs)
    {
        var list = new List<InputRoute>();
        for (int i = 0; i < inputs; i++) list.Add(new InputRoute(i, false));
        for (int i = 0; i + 1 < inputs; i += 2) list.Add(new InputRoute(i, true));
        return list;
    }
}

/// <summary>
/// One armed track's input in the audio callback: its own <see cref="InputProcessor"/> (pre-amp gain and
/// soft limit, like input 1-2), the processed block, a peak meter and, while recording, its
/// <see cref="TakeRecorder"/>. Without a route it carries the main input (1-2 with Configuración's mode),
/// processed once by the engine. Buffers are allocated here, never in the callback.
/// </summary>
public sealed class InputTap
{
    public const int MaxBlock = 8192;

    readonly InputProcessor processor = new();
    readonly float[] bufL = new float[MaxBlock];
    readonly float[] bufR = new float[MaxBlock];
    float peakL, peakR;
    float[] blockL, blockR;
    int blockN;

    public InputRoute? Route { get; }
    /// <summary>False for a tap that hears the same inputs as an earlier one: software monitoring plays each input once.</summary>
    public bool Monitored { get; init; } = true;
    public volatile TakeRecorder? Recorder;

    public InputTap(InputRoute? route, double gainDb)
    {
        Route = route;
        (blockL, blockR) = (bufL, bufR);
        processor.InputGainDb = gainDb;
        processor.ChannelMode = route is { Stereo: true } ? InputChannelMode.Stereo : InputChannelMode.Ch1Mono;
    }

    public double GainDb
    {
        get => processor.InputGainDb;
        set => processor.InputGainDb = value;
    }

    /// <summary>
    /// Audio thread: processes this tap's inputs for the block (a routed tap) or takes the main input's
    /// processed block; <see cref="BlockL"/> / <see cref="BlockR"/> hold it until the next call. No allocation.
    /// </summary>
    public void Process(float[][] input, int n, float[] mainL, float[] mainR)
    {
        blockN = n;
        if (Route is not { } r)
        {
            (blockL, blockR) = (mainL, mainR);
            Peaks(mainL, mainR, n);
            return;
        }
        (blockL, blockR) = (bufL, bufR);
        if (r.Last >= input.Length)
        {
            Array.Clear(bufL, 0, n);
            Array.Clear(bufR, 0, n);
            return;
        }
        var a = input[r.First].AsSpan(0, n);
        var b = r.Stereo ? input[r.First + 1].AsSpan(0, n) : a;
        processor.Process(a, b, bufL.AsSpan(0, n), bufR.AsSpan(0, n), out float ml, out float mr, out _);
        if (ml > peakL) peakL = ml;
        if (mr > peakR) peakR = mr;
    }

    public ReadOnlySpan<float> BlockL => blockL.AsSpan(0, blockN);
    public ReadOnlySpan<float> BlockR => blockR.AsSpan(0, blockN);

    void Peaks(float[] l, float[] r, int n)
    {
        float ml = peakL, mr = peakR;
        for (int i = 0; i < n; i++)
        {
            float al = Math.Abs(l[i]), ar = Math.Abs(r[i]);
            if (al > ml) ml = al;
            if (ar > mr) mr = ar;
        }
        peakL = ml;
        peakR = mr;
    }

    /// <summary>Peak levels since the last call (UI thread).</summary>
    public (float L, float R) TakePeaks()
    {
        var p = (peakL, peakR);
        peakL = peakR = 0;
        return p;
    }
}
