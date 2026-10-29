using System.Text.Json;
using WildTrack.Audio.Dsp;
using WildTrack.Projects;

namespace WildTrack.Lab;

/// <summary>
/// The noise gate's rows of comprobar-dsp: <see cref="NoiseGate"/> fed in blocks like the audio thread does, on hum
/// under the threshold, a note over it and a note dying slowly through it (the hysteresis must keep it from fluttering),
/// plus the slot's trip through the project (saved, read back, turned into the engine's spec).
/// </summary>
static class GateChecks
{
    const int Sr = 48000, Block = 128;

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        // 1. hum at -60 dBFS, under the -50 dB threshold: after half a second it is 24 dB down (normal)
        var hum = Signal(Sr, i => Db(-60) * Math.Sin(2 * Math.PI * 50 * i / Sr));
        var humOut = Run(hum, new GateParams(true, GateParams.Normal, -50));
        double humDown = Rms(humOut, Sr / 2, Sr) - Rms(hum, Sr / 2, Sr);
        Row("Puerta: zumbido bajo el umbral", $"{humDown:0.0} dB", "-24 dB", Math.Abs(humDown + 24) < 0.5);
        var hardOut = Run(hum, new GateParams(true, GateParams.Hard, -50));
        double hardDown = Rms(hardOut, Sr / 2, Sr) - Rms(hum, Sr / 2, Sr);
        Row("Puerta firme: zumbido", $"{hardDown:0.0} dB", "< -60 dB", hardDown < -60);

        // 2. a note at -20 dBFS goes through untouched (after its first millisecond)
        var note = Signal(Sr, i => Db(-20) * Math.Sin(2 * Math.PI * 196 * i / Sr));
        var noteOut = Run(note, new GateParams(true, GateParams.Normal, -50));
        double noteDiff = 0;
        for (int i = Sr / 100; i < note.Length; i++) noteDiff = Math.Max(noteDiff, Math.Abs(noteOut[i] - note[i]));
        Row("Puerta: nota sobre el umbral pasa", $"{noteDiff:0.0e0}", "< 1e-4", noteDiff < 1e-4);

        // 3. a low E dying from -30 to -70 dB over 3 s: it closes once and stays closed (no flutter around the threshold)
        var dying = Signal(3 * Sr, i => Db(-30 - 40.0 * i / (3 * Sr)) * Math.Sin(2 * Math.PI * 82.4 * i / Sr));
        var gate = new NoiseGate(Sr);
        gate.Set(new GateParams(true, GateParams.Normal, -50));
        int closings = 0;
        bool wasDown = false;
        var l = new float[Block];
        var r = new float[Block];
        for (int at = 0; at + Block <= dying.Length; at += Block)
        {
            dying.AsSpan(at, Block).CopyTo(l);
            dying.AsSpan(at, Block).CopyTo(r);
            gate.Process(l, r);
            bool down = gate.TakeGrDb() < -6;
            if (down && !wasDown) closings++;
            wasDown = down;
        }
        Row("Puerta: nota que se apaga, sin aleteo", $"{closings} cierre{(closings == 1 ? "" : "s")}", "1", closings == 1);

        // 4. off it is bypassed exactly
        var offOut = Run(hum, new GateParams(false, GateParams.Hard, -20));
        double offDiff = 0;
        for (int i = 0; i < hum.Length; i++) offDiff = Math.Max(offDiff, Math.Abs(offOut[i] - hum[i]));
        Row("Puerta apagada = sin tocar", offDiff == 0 ? "exacto" : $"{offDiff:0.0e0}", "exacto", offDiff == 0);

        // 5. the slot is saved and read back, a broken one is repaired, and it reaches the engine as it was set
        var slot = InsertSlot.New(InsertSlot.GateKind);
        slot.On = true;
        slot.Gate!.Mode = GateParams.Hard;
        slot.Gate.Threshold = -42.5;
        var back = JsonSerializer.Deserialize<InsertSlot>(JsonSerializer.Serialize(slot, ProjectStore.Json), ProjectStore.Json)!;
        var broken = JsonSerializer.Deserialize<InsertSlot>("""{"type":"gate","gate":{"on":true,"mode":"rara","threshold":-300}}""", ProjectStore.Json)!;
        var spec = ChannelDsp.Specs([back, broken], -18);
        bool savedOk = spec.Length == 2 && spec[0].Kind == TrackInserts.Gate && spec[0].Gate == new GateParams(true, GateParams.Hard, -42.5)
            && spec[1].Gate == new GateParams(true, GateParams.Normal, GateParams.MinThresholdDb);
        Row("Puerta: se guarda y llega al motor", savedOk ? "sí" : "no", "sí", savedOk);

        // 6. as a slot in the export, the same as on its own
        var inserts = TrackInserts.Build([spec[0]], [], [], Sr, out _);
        var viaSlot = (float[])hum.Clone();
        var viaSlotR = (float[])hum.Clone();
        for (int at = 0; at + Block <= hum.Length; at += Block) inserts[0].Process(viaSlot.AsSpan(at, Block), viaSlotR.AsSpan(at, Block));
        var direct = Run(hum, spec[0].Gate);
        double slotDiff = 0;
        for (int i = 0; i < hum.Length; i++) slotDiff = Math.Max(slotDiff, Math.Abs(viaSlot[i] - direct[i]));
        Row("Puerta: slot = puerta sola", slotDiff == 0 ? "exacto" : $"{slotDiff:0.0e0}", "exacto", slotDiff == 0);
        return allOk;
    }

    static double Db(double db) => Math.Pow(10, db / 20);

    static float[] Signal(int n, Func<int, double> f)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)f(i);
        return x;
    }

    /// <summary>The gate over <paramref name="x"/> (both sides the same), in blocks; returns the left side.</summary>
    static float[] Run(float[] x, GateParams p)
    {
        var gate = new NoiseGate(Sr);
        gate.Set(p);
        var l = (float[])x.Clone();
        var r = (float[])x.Clone();
        for (int at = 0; at + Block <= x.Length; at += Block) gate.Process(l.AsSpan(at, Block), r.AsSpan(at, Block));
        return l;
    }

    static double Rms(float[] x, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++) s += x[i] * (double)x[i];
        return 10 * Math.Log10(s / (to - from) + 1e-30);
    }
}
