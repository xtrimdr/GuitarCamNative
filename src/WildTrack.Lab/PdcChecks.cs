using WildTrack.Audio.Dsp;
using WildTrack.Audio.Vst;
using WildTrack.Projects;

namespace WildTrack.Lab;

/// <summary>
/// The plugin delay compensation's rows of comprobar-dsp: <see cref="LatencyDelay"/> fed in blocks like the audio thread
/// (an exact delay, a change of delay that lands exact after its crossfade, a ring that grows without losing its place),
/// <see cref="MixLatency"/> on a small mix (a latent track, a latent bus, a send), and a VST3 slot with no process yet,
/// which has to let the sound through untouched. The plugin itself is tried with <c>vst efecto</c>.
/// </summary>
static class PdcChecks
{
    const int Block = 128;

    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        // 1. a ramp through 1000 frames of delay: every sample comes out exactly that much later, from the first one
        int n = 20000;
        var ramp = new float[n];
        for (int i = 0; i < n; i++) ramp[i] = i + 1;
        var d = new LatencyDelay();
        d.Set(1000);
        var got = Run(d, ramp, _ => { });
        double err = 0;
        for (int i = 0; i < n; i++) err = Math.Max(err, Math.Abs(got[i] - (i >= 1000 ? ramp[i - 1000] : 0)));
        Row("PDC: demora exacta (1000 muestras)", $"{err:0.##}", "0", err == 0);

        // 2. from 1000 to 300 while it runs: once its crossfade is over, exact again
        var d2 = new LatencyDelay();
        d2.Set(1000);
        int at = 8192;
        var moved = Run(d2, ramp, i => { if (i == at) d2.Set(300); });
        double err2 = 0;
        for (int i = at + Block + LatencyDelay.Fade; i < n; i++) err2 = Math.Max(err2, Math.Abs(moved[i] - ramp[i - 300]));
        Row("PDC: cambio de demora con fundido", $"{err2:0.##}", "0", err2 == 0);

        // 3. a demand past its ring (16384): a bigger one takes over with its history, and the new delay is exact
        int m = 90000;
        var longRamp = new float[m];
        for (int i = 0; i < m; i++) longRamp[i] = i + 1;
        var d3 = new LatencyDelay();
        d3.Set(100);
        int grow = 10240;
        var grown = Run(d3, longRamp, i => { if (i == grow) d3.Set(20000); });
        double err3 = 0;
        for (int i = grow + Block + LatencyDelay.Fade; i < m; i++)
            err3 = Math.Max(err3, Math.Abs(grown[i] - (i >= 20000 ? longRamp[i - 20000] : 0)));
        Row("PDC: el anillo crece sin perderse", $"{err3:0.##}", "0", err3 == 0);

        // 4. the plan: A (300 late) and C straight to the master, B into a bus that is 100 late, C sends to that bus too
        var plan = MixLatency.Compute(
            [new(300, -1, []), new(0, 0, []), new(0, -1, [0])],
            [new(100, -1, [])]);
        bool planOk = plan.Master == 300 && plan.TrackOut is [0, 0, 300] && plan.TrackSends[2] is [0] && plan.NodeOut is [200];
        Row("PDC: plan (pista, bus y envío)", $"{plan.Master} / {plan.TrackOut[2]} / {plan.NodeOut[0]}", "300/300/200", planOk);

        // 5. a VST3 slot whose plugin isn't up: the sound goes through untouched, and it adds no latency
        var vst = new VstInsert(48000);
        vst.Set(new VstSlotSpec(new VstInstance("x.vst3", "0", "Nada", null), true), 120);
        var l = new float[Block];
        var r = new float[Block];
        double err5 = 0;
        for (int b = 0; b < 20; b++)
        {
            for (int i = 0; i < Block; i++) { l[i] = MathF.Sin(b * Block + i); r[i] = -l[i]; }
            vst.Process(l, r);
            for (int i = 0; i < Block; i++) err5 = Math.Max(err5, Math.Abs(l[i] - MathF.Sin(b * Block + i)) + Math.Abs(r[i] + MathF.Sin(b * Block + i)));
        }
        Row("VST3 sin abrir: pasa el sonido", $"{err5:0.0e0}", "0", err5 == 0 && vst.LatencySamples == 0);

        // 6. the model: a VST3 slot is saved, read back, and turned into the engine's spec offline (the export's)
        var ch = new Channel();
        var slot = ChannelInserts.AddVst(ch.Slots, new PluginRef { Path = @"C:\x\Amp.vst3", ClassId = "ABC", Name = "Amp", Vendor = "Yo", State = "no" });
        slot!.Vst!.Plugin.State = "estado";
        var back = System.Text.Json.JsonSerializer.Deserialize<Channel>(System.Text.Json.JsonSerializer.Serialize(ch, ProjectStore.Json), ProjectStore.Json)!;
        var spec = ChannelDsp.Specs(back, -24, 90).SingleOrDefault();
        bool modelOk = spec is { Kind: TrackInserts.Vst, Vst: { Offline: true, On: true, Plugin: { Name: "Amp", State: "estado" } }, Bpm: 90 }
            && ChannelInserts.Add(ch.Slots, InsertSlot.VstKind) == null
            && !new InsertSlot { Type = InsertSlot.VstKind }.Known;
        Row("VST3: slot guardado y leído", modelOk ? "bien" : "mal", "bien", modelOk);
        return allOk;
    }

    /// <summary>The signal through the delay in blocks, with <paramref name="before"/> called at each block's start (UI edits).</summary>
    static float[] Run(LatencyDelay d, float[] x, Action<int> before)
    {
        var outL = new float[x.Length];
        var outR = new float[x.Length];
        for (int at = 0; at < x.Length; at += Block)
        {
            int len = Math.Min(Block, x.Length - at);
            before(at);
            d.Add(x.AsSpan(at, len), x.AsSpan(at, len), 1f, outL.AsSpan(at, len), outR.AsSpan(at, len));
        }
        return outL;
    }
}
