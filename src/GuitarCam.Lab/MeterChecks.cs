using GuitarCam.Audio.Dsp;

namespace GuitarCam.Lab;

/// <summary>
/// The mixer meters' RMS rows of comprobar-dsp: <see cref="RmsTap"/> fed in blocks like the audio thread does and read
/// like the window's tick, on a sine whose level is known (a sine's RMS is 3 dB under its peak).
/// </summary>
static class MeterChecks
{
    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        const int sr = 48000, block = 256;
        double amp = Math.Pow(10, -6 / 20.0);
        var l = new float[block];
        var r = new float[block];
        var tap = new RmsTap();
        double ms = 0;
        long k = 0;
        // 2 s of a -6 dBFS peak sine, read every 16 ms (as the tick does); silence on the right
        for (int b = 0; b < 2 * sr / block; b++)
        {
            for (int i = 0; i < block; i++, k++) l[i] = (float)(amp * Math.Sin(2 * Math.PI * 1000 * k / sr));
            tap.Add(l, r, block);
            if (b % 3 == 2) ms = tap.Take(3.0 * block / sr);
        }
        double db = RmsTap.Db(ms);
        bool sineOk = Math.Abs(db + 9.03) < 0.1;
        // then silence: after 1 s the meter has fallen well under -40 dB
        Array.Clear(l);
        for (int b = 0; b < sr / block; b++)
        {
            tap.Add(l, r, block);
            if (b % 3 == 2) ms = tap.Take(3.0 * block / sr);
        }
        double fallen = RmsTap.Db(ms);
        bool fallOk = fallen < -40;
        Console.WriteLine($"{"Medidor: RMS de un seno a -6 dBFS",-36} {$"{db:0.0} dB",12} {"-9.0 dB",10}  {(sineOk ? "igual" : "DISTINTO")}");
        Console.WriteLine($"{"Medidor: RMS 1 s después del silencio",-36} {$"{fallen:0.0} dB",12} {"< -40 dB",10}  {(fallOk ? "igual" : "DISTINTO")}");
        return sineOk && fallOk;
    }
}
