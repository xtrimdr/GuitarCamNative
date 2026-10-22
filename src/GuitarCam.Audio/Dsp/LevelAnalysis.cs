namespace GuitarCam.Audio.Dsp;

/// <summary>
/// Port of analyze() in compPresets.ts: a 2 ms envelope of the take, and its typical playing level
/// (80th percentile of the 50 ms windows, silence between phrases left out).
/// </summary>
public static class LevelAnalysis
{
    const double BlockSec = 0.002;
    const int WinBlocks = 25; // 50 ms windows

    static double ToDb(double x) => x > 1e-12 ? 10 * Math.Log10(x) : -120; // from power

    static double Pct(List<double> arr, double p, double fallback)
    {
        if (arr.Count == 0) return fallback;
        var s = arr.ToArray();
        Array.Sort(s);
        return s[Math.Min(s.Length - 1, (int)Math.Floor(s.Length * p))];
    }

    public static (double RmsDb, double PeakDb) Analyze(float[][] chans, int sampleRate)
    {
        int bs = Math.Max(1, (int)Math.Round(sampleRate * BlockSec, MidpointRounding.AwayFromZero));
        int nb = chans[0].Length / bs;
        var peakDb = new float[nb];
        var pow = new float[nb];
        for (int b = 0; b < nb; b++)
        {
            double sum = 0;
            double pk = 0;
            foreach (var ch in chans)
            {
                for (int i = b * bs, end = i + bs; i < end; i++)
                {
                    double v = ch[i];
                    sum += v * v;
                    double a = v < 0 ? -v : v;
                    if (a > pk) pk = a;
                }
            }
            pow[b] = (float)(sum / (bs * chans.Length));
            peakDb[b] = (float)ToDb(pk * pk);
        }
        var winRms = new List<double>();
        var winPeak = new List<double>();
        for (int s = 0; s + WinBlocks <= nb; s += WinBlocks)
        {
            double sum = 0;
            double pk = -120;
            for (int b = s; b < s + WinBlocks; b++)
            {
                sum += pow[b];
                if (peakDb[b] > pk) pk = peakDb[b];
            }
            winRms.Add(ToDb(sum / WinBlocks));
            winPeak.Add(pk);
        }
        // silence and noise between phrases don't count: absolute floor, and 40 dB under the loud parts
        double loud = Pct(winRms, 0.95, -30);
        double floor = Math.Max(-55, loud - 40);
        var keepRms = new List<double>();
        var keepPeak = new List<double>();
        for (int i = 0; i < winRms.Count; i++)
        {
            if (winRms[i] <= floor) continue;
            keepRms.Add(winRms[i]);
            keepPeak.Add(winPeak[i]);
        }
        return (Pct(keepRms, 0.8, -30), Pct(keepPeak, 0.8, -18));
    }
}
