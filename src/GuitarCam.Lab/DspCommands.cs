using System.Diagnostics;
using System.Text.Json;
using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

static class DspCommands
{
    const int Block = 128; // a render quantum, like the worklets

    public static int Process(Args a)
    {
        if (a.Positional.Count == 0) throw new ArgumentException("Falta el WAV de entrada.");
        string inPath = a.Positional[0];
        var (ch, sr) = Wav.Read(inPath);
        var (rms, peak) = LevelAnalysis.Analyze(ch, sr);
        Console.WriteLine($"{Path.GetFileName(inPath)}: {sr} Hz, {ch[0].Length / (double)sr:0.0} s, nivel típico {rms:0.0} dB RMS, picos {peak:0.0} dB");

        var sat = new Saturator(sr);
        string? satArg = a.Get("sat");
        if (satArg != null)
        {
            var mode = satArg.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? SatMode.Tape : SatMode.Tube;
            var p = SatParams.From(true, mode, a.Double("vida", 0.3), rms);
            sat.Set(p);
            Console.WriteLine($"Saturación {mode}: drive {p.Drive:0.###}, bias {p.Bias:0.##}, tono {p.Tone:0} Hz, bump {p.Bump:0.#} dB");
        }

        var compEq = new CompEqFilter(sr);
        var comp = new Compressor(sr)
        {
            Bypass = !a.Flag("comp") && a.Get("comp-estilo") == null,
            Threshold = a.Double("umbral", -24),
            Ratio = a.Double("ratio", 4),
            Attack = a.Double("ataque", 10) / 1000,
            Release = a.Double("release", 150) / 1000,
            Knee = a.Double("knee", 6),
            Makeup = a.Double("makeup", 0),
            Limit = a.Flag("limit") || a.Get("comp-estilo") != null,
            Ceiling = CompPresets.TrackLimitCeilingDb,
        };
        string? compStyle = a.Get("comp-estilo");
        if (compStyle != null)
        {
            string presetId = compStyle.ToLowerInvariant() switch
            {
                "arpegio" or "arpeggio" => "arpeggio",
                "punteo" or "lead" => "lead",
                _ => "rhythm",
            };
            var state = CompPresets.UpdateComp(
                CompState.Defaults with { On = true, Preset = presetId, Amount = a.Double("comp-cant", 0.5), Limit = !a.Flag("sin-limit") },
                ch,
                sr);
            comp.Bypass = false;
            comp.Threshold = state.Threshold;
            comp.Ratio = state.Ratio;
            comp.Attack = state.Attack;
            comp.Release = state.Release;
            comp.Knee = state.Knee;
            comp.Makeup = state.Makeup;
            comp.Limit = state.Limit;
            comp.Ceiling = CompPresets.TrackLimitCeilingDb;
            compEq.Set(true, state.Eq);
            Console.WriteLine($"Compresor {CompPresets.PresetLabel(presetId)} ({state.Amount * 100:0}%): umbral {state.Threshold:0.#} dB, {state.Ratio:0.#}:1, makeup {state.Makeup:+0.#;-0.#;0} dB ({CompPresets.Summary(state)})");
        }

        var trackEq = new TrackEq(sr);
        string? eqArg = a.Get("eq");
        if (eqArg != null)
        {
            string eqId = eqArg.ToLowerInvariant() switch
            {
                "ritmica" or "rítmica" or "rhythm" => "rhythm",
                "punteo" or "lead" => "lead",
                "acustica" or "acústica" or "acoustic" => "acoustic",
                "frente" or "al-frente" or "front" => "front",
                _ => "flat",
            };
            trackEq.Set(true, TrackEq.ApplyPreset(eqId));
            Console.WriteLine($"EQ de pista: preset {eqId}");
        }

        ReverbBus? reverb = null;
        float revSend = (float)a.Double("reverb-envio", 0.35);
        string? revArg = a.Get("reverb");
        if (revArg != null || a.Flag("reverb"))
        {
            reverb = new ReverbBus(sr);
            var rp = ReverbParams.Defaults;
            if (revArg != null)
            {
                string label = revArg.ToLowerInvariant() switch
                {
                    "cuarto" => "Cuarto",
                    "sala" => "Sala",
                    "salon" or "salón" => "Salón",
                    "placa" => "Placa",
                    "catedral" => "Catedral",
                    _ => "",
                };
                if (label.Length > 0) rp = ReverbBus.ApplyPreset(rp, label);
            }
            reverb.Set(rp);
            Console.WriteLine($"Reverb ({(ReverbBus.PresetOf(rp) is { Length: > 0 } rl ? rl : "Personalizada")}): decay {rp.Decay:0.#} s, pre-delay {rp.PreDelayMs:0} ms, envío {revSend * 100:0}%");
        }

        DelayBus? delay = null;
        float dlySend = (float)a.Double("delay-envio", 0.35);
        string? dlyArg = a.Get("delay");
        if (dlyArg != null || a.Flag("delay"))
        {
            delay = new DelayBus(sr);
            var dp = DelayParams.Defaults;
            if (dlyArg != null)
            {
                string label = dlyArg.ToLowerInvariant() switch
                {
                    "slapback" => "Slapback",
                    "corchea" => "Corchea",
                    "punteada" => "Punteada",
                    "pingpong" or "ping-pong" => "Ping-pong",
                    "ambiente" => "Ambiente",
                    _ => "",
                };
                if (label.Length > 0) dp = DelayBus.ApplyPreset(dp, label);
            }
            double bpm = a.Double("bpm", 120);
            delay.Set(dp, bpm, glide: false);
            Console.WriteLine($"Delay ({(DelayBus.PresetOf(dp) is { Length: > 0 } dl ? dl : "Personalizado")}): {DelayBus.TimeLabel(dp)} a {bpm:0} BPM, fb {dp.Feedback * 100:0}%, envío {dlySend * 100:0}%");
        }

        var revL = new float[Block];
        var revR = new float[Block];
        var dlyL = new float[Block];
        var dlyR = new float[Block];

        var sw = Stopwatch.StartNew();
        double minGr = 0;
        int len = ch[0].Length;
        for (int i = 0; i < len; i += Block)
        {
            int n = Math.Min(Block, len - i);
            var l = ch[0].AsSpan(i, n);
            var r = ch[1].AsSpan(i, n);
            sat.Process(l, r);
            compEq.ProcessWith(comp, l, r);
            trackEq.Process(l, r);
            if (reverb != null)
            {
                var rl = revL.AsSpan(0, n);
                var rr = revR.AsSpan(0, n);
                for (int k = 0; k < n; k++) { rl[k] = l[k] * revSend; rr[k] = r[k] * revSend; }
                reverb.Process(rl, rr);
                for (int k = 0; k < n; k++) { l[k] += rl[k]; r[k] += rr[k]; }
            }
            if (delay != null)
            {
                var dl = dlyL.AsSpan(0, n);
                var dr = dlyR.AsSpan(0, n);
                for (int k = 0; k < n; k++) { dl[k] = l[k] * dlySend; dr[k] = r[k] * dlySend; }
                delay.Process(dl, dr);
                for (int k = 0; k < n; k++) { l[k] += dl[k]; r[k] += dr[k]; }
            }
            minGr = Math.Min(minGr, comp.TakeMeter());
        }
        sw.Stop();
        string outPath = a.Get("salida", Path.ChangeExtension(inPath, null) + "_proc.wav");
        Wav.Write(outPath, ch[0], ch[1], sr);
        double audioSec = len / (double)sr;
        Console.WriteLine($"Procesado en {sw.Elapsed.TotalMilliseconds:0} ms ({audioSec / sw.Elapsed.TotalSeconds:0}× tiempo real){(comp.Bypass ? "" : $", reducción máxima {minGr:0.0} dB")}");
        Console.WriteLine($"Escrito: {Path.GetFullPath(outPath)}");
        return 0;
    }

    /// <summary>
    /// Feeds the EQ editor's spectrum tap through a real track chain (flat, as the window hangs it after the EQ)
    /// and reads it every 16 ms like the window does. Without a WAV it checks a bin-centred sine against the Web
    /// Audio AnalyserNode spec: Blackman coherent gain 0.42, |X|/N gives 20·log10(A·0.42/2) once smoothing settles.
    /// </summary>
    public static int Spectrum(Args a)
    {
        const int sr = 48000, tickFrames = sr * 16 / 1000;
        float[] l, r;
        double hz = 0, amp = 0;
        if (a.Positional.Count > 0)
        {
            var (ch, wsr) = Wav.Read(a.Positional[0]);
            if (wsr != sr) Console.WriteLine($"Aviso: el WAV es de {wsr} Hz; lo leo como si fuera de {sr} Hz.");
            l = ch[0]; r = ch.Length > 1 ? ch[1] : ch[0];
        }
        else
        {
            double binHz = (double)sr / SpectrumTap.FftSize;
            hz = Math.Round(a.Double("hz", 1000) / binHz) * binHz;
            amp = Math.Pow(10, a.Double("nivel", -6) / 20);
            l = new float[sr * 2];
            for (int i = 0; i < l.Length; i++) l[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / sr));
            r = l;
        }

        var chain = new TrackDspChain(sr);
        var tap = new SpectrumTap();
        chain.Spectrum = tap;
        var bins = new float[SpectrumTap.Bins];
        var sink = new float[Block];
        int sinceRead = 0, reads = 0;
        for (int pos = 0; pos + Block <= l.Length; pos += Block)
        {
            l.AsSpan(pos, Block).CopyTo(chain.BufferL(Block));
            r.AsSpan(pos, Block).CopyTo(chain.BufferR(Block));
            chain.ProcessFilledBuffer(Block, false, sink, sink, sink, sink, sink, sink, out _, out _, out _);
            if ((sinceRead += Block) >= tickFrames) { sinceRead = 0; tap.Read(bins); reads++; }
        }

        var top = Enumerable.Range(1, bins.Length - 2).Where(k => bins[k] >= bins[k - 1] && bins[k] > bins[k + 1])
            .OrderByDescending(k => bins[k]).Take(5).ToArray();
        Console.WriteLine($"{reads} lecturas de {SpectrumTap.FftSize} muestras (suavizado {SpectrumTap.Smoothing}), picos:");
        foreach (int k in top) Console.WriteLine($"  {k * (double)sr / SpectrumTap.FftSize,8:0.0} Hz  {bins[k],7:0.00} dB");
        if (amp == 0) return 0;

        int bin = (int)Math.Round(hz * SpectrumTap.FftSize / sr);
        double expected = 20 * Math.Log10(amp * 0.42 / 2);
        bool ok = top.Length > 0 && top[0] == bin && Math.Abs(bins[bin] - expected) < 0.05;
        Console.WriteLine($"Seno de {hz:0.0} Hz: {bins[bin]:0.00} dB en su bin, esperado {expected:0.00} dB (AnalyserNode) → {(ok ? "bien" : "MAL")}");
        return ok ? 0 : 1;
    }

    public static int Click(Args a)
    {
        int sr = 48000;
        double beatLen = sr * 60 / a.Double("bpm", 100);
        var sounds = Enum.GetValues<ClickSound>();
        int beats = sounds.Length * 4 + 1;
        int len = (int)(beats * beatLen) + sr;
        var L = new float[len];
        for (int s = 0; s < sounds.Length; s++)
        {
            Console.WriteLine($"  compás {s + 1}: {sounds[s]}");
            for (int b = 0; b < 4; b++)
            {
                var c = ClickSynth.Render(sounds[s], b == 0, sr);
                int at = (int)Math.Round((s * 4 + b) * beatLen);
                for (int i = 0; i < c.Length && at + i < len; i++) L[at + i] += c[i];
            }
        }
        string path = a.Get("salida", "clicks.wav");
        Wav.Write(path, L, L, sr);
        Console.WriteLine($"Escrito: {Path.GetFullPath(path)}");
        return 0;
    }

    public static int Mix(Args a)
    {
        if (a.Positional.Count == 0) throw new ArgumentException("Falta la carpeta del proyecto nativo (o un WAV).");
        string target = Path.GetFullPath(a.Positional[0]);
        int sr = a.Int("sr", 48000);

        List<MixTrackSpec> trackSpecs = [];
        MixBusSpec busSpec = new();
        string defaultOut;

        if (Directory.Exists(target))
        {
            using var store = ProjectStore.Open(target);
            var p = store.Project;
            defaultOut = Path.Combine(target, "exports", "mezcla.wav");

            var mediaCache = new Dictionary<string, (float[] L, float[] R, double RmsDb)>();
            var mediaPaths = new Dictionary<string, string>();
            foreach (var m in p.Media)
            {
                if (store.Resolve(m.Audio) is { } audioPath && File.Exists(audioPath))
                {
                    var loaded = AudioLoader.Load(audioPath, sr);
                    float[][] ch = [loaded.Left, loaded.Right];
                    var (rms, _) = LevelAnalysis.Analyze(ch, loaded.SampleRate);
                    mediaCache[m.Id] = (loaded.Left, loaded.Right, rms);
                    mediaPaths[m.Id] = audioPath;
                }
            }

            foreach (var t in p.Tracks)
            {
                var clips = new List<MixClipSpec>();
                double trackRms = -24;
                bool hasRms = false;
                foreach (var c in t.Clips)
                {
                    if (!mediaCache.TryGetValue(c.MediaId, out var ma)) continue;
                    var m = p.FindMedia(c.MediaId);
                    if (!hasRms) { trackRms = ma.RmsDb; hasRms = true; }
                    var (tl, tr) = store.TrackAudio(t, mediaPaths[c.MediaId], ma.L, ma.R);
                    clips.Add(new MixClipSpec
                    {
                        Left = tl,
                        Right = tr,
                        DurationSec = m?.DurationSec > 0 ? m.DurationSec : ma.L.Length / (double)sr,
                        AtSec = c.AtSec,
                        TrimStartSec = c.TrimStartSec,
                        TrimEndSec = c.TrimEndSec,
                        FadeInSec = c.FadeInSec,
                        FadeOutSec = c.FadeOutSec,
                        GainDb = c.GainDb,
                        GainCurve = c.CurvePoints(),
                        SyncMs = c.EffectiveSyncMs(m),
                        Transition = c.Transition,
                        TransitionSec = c.TransitionSec,
                        TakeId = c.MediaId,
                    });
                }

                var ch = t.Channel;
                // its folder as in the app: a VCA on its fader, mute and solo, and a bus with the folder's slots
                var folder = TrackFolders.Mix(p, t);

                trackSpecs.Add(new MixTrackSpec
                {
                    Name = t.Name,
                    Clips = clips,
                    Volume = ch.Volume * folder.Gain,
                    Pan = ch.Pan,
                    Muted = ch.Muted || folder.Muted,
                    Solo = ch.Solo || folder.Solo,
                    Inserts = ChannelDsp.Specs(ch, trackRms),
                    Folder = ChannelDsp.FolderIndex(p, t),
                    ReverbOn = ch.ReverbOn,
                    ReverbSend = ch.ReverbSend,
                    DelayOn = ch.DelayOn,
                    DelaySend = ch.DelaySend,
                    ReverbPre = ch.ReverbPre,
                    DelayPre = ch.DelayPre,
                    Sends = ChannelDsp.Sends(p, ch),
                });
            }

            busSpec = ChannelDsp.Bus(p);
            Console.WriteLine($"Proyecto: {p.Name} ({trackSpecs.Count} pistas, {p.Settings.Bpm:0.#} BPM)");
            int buses = busSpec.Folders.Count(f => f.Inserts.Count > 0);
            if (buses > 0) Console.WriteLine($"Carpetas con efectos: {buses} (sus pistas suman en su bus)");
        }
        else
        {
            var loaded = AudioLoader.Load(target, sr);
            sr = loaded.SampleRate;
            float[][] ch = [loaded.Left, loaded.Right];
            var (rms, _) = LevelAnalysis.Analyze(ch, sr);
            defaultOut = Path.ChangeExtension(target, null) + "_mezcla.wav";
            var compState = CompPresets.UpdateComp(
                CompState.Defaults with { On = a.Flag("comp"), Preset = a.Get("comp-estilo", "rhythm"), Amount = a.Double("comp-cant", 0.5) },
                ch,
                sr);
            var satParams = SatParams.From(a.Get("sat") != null, a.Get("sat")?.StartsWith("c", StringComparison.OrdinalIgnoreCase) == true ? SatMode.Tape : SatMode.Tube, a.Double("vida", 0.3), rms);
            bool eqOn = a.Get("eq") != null;
            var eqBands = a.Get("eq") is { } eqId ? TrackEq.ApplyPreset(eqId) : TrackEq.DefaultBands;
            trackSpecs.Add(new MixTrackSpec
            {
                Clips =
                [
                    new MixClipSpec
                    {
                        Left = ch[0],
                        Right = ch[1],
                        DurationSec = ch[0].Length / (double)sr,
                        FadeInSec = a.Double("fade-in", 0),
                        FadeOutSec = a.Double("fade-out", 0),
                        GainDb = a.Double("ganancia", 0),
                    },
                ],
                Volume = a.Double("volumen", 1),
                Pan = a.Double("pan", 0),
                Sat = satParams,
                Comp = compState,
                EqOn = eqOn,
                EqBands = eqBands,
                Inserts = a.Get("cadena") is { } chainList ? InsertCommands.Chain(chainList, satParams, compState, eqOn, eqBands) : null,
                ReverbOn = true,
                ReverbSend = a.Double("reverb-envio", 0),
                DelayOn = true,
                DelaySend = a.Double("delay-envio", 0),
            });
        }

        double durationSec = a.Double("segundos", MixRenderer.MixDurationSec(trackSpecs, sr));
        var sw = Stopwatch.StartNew();
        var (outL, outR) = MixRenderer.Render(trackSpecs, busSpec, durationSec, sr);
        sw.Stop();

        float peak = 0;
        for (int i = 0; i < outL.Length; i++)
        {
            float pk = Math.Max(Math.Abs(outL[i]), Math.Abs(outR[i]));
            if (pk > peak) peak = pk;
        }
        double peakDb = peak > 1e-6 ? 20 * Math.Log10(peak) : -120;

        string outPath = a.Get("salida", defaultOut);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        Wav.Write(outPath, outL, outR, sr);
        double dspPct = 100.0 * sw.Elapsed.TotalSeconds / Math.Max(0.001, durationSec);
        Console.WriteLine($"Mezcla offline: {durationSec:0.0} s en {sw.Elapsed.TotalMilliseconds:0} ms ({durationSec / Math.Max(0.001, sw.Elapsed.TotalSeconds):0}× tiempo real · carga equivalente DSP {dspPct:0.0}%), pico {peakDb:0.0} dB (techo limitador {MasterLimiter.CeilingDb:0.0} dB)");
        Console.WriteLine($"Escrito: {Path.GetFullPath(outPath)}");
        return 0;
    }

    /// <summary>
    /// Self-contained DSP checks (no reference implementation needed): resampling, take recording and
    /// state reset of the track chain and its buses.
    /// </summary>
    public static int Check(Args a)
    {
        int sr = 48000;
        var ch = TestSignal(sr, 8);
        var (rms, _) = LevelAnalysis.Analyze(ch, sr);
        var env = CompPresets.Analyze(ch, sr);
        var tube30 = SatParams.From(true, SatMode.Tube, 0.3, rms);
        string tmp = Path.Combine(Path.GetTempPath(), "gclab-verify");
        Directory.CreateDirectory(tmp);
        bool allOk = true;

        // Verify 44.1 kHz -> 48 kHz resampling in AudioLoader (duration and pitch preservation)
        int sr44 = 44100, sr48 = 48000;
        int len44 = sr44 * 2;
        var s44L = new float[len44];
        var s44R = new float[len44];
        for (int i = 0; i < len44; i++)
        {
            double t = i / (double)sr44;
            s44L[i] = (float)(0.5 * Math.Sin(2.0 * Math.PI * 440.0 * t));
            s44R[i] = (float)(0.5 * Math.Cos(2.0 * Math.PI * 880.0 * t));
        }
        string wav44 = Path.Combine(tmp, "test_44k1.wav");
        Wav.Write(wav44, s44L, s44R, sr44);
        var loaded48 = AudioLoader.Load(wav44, sr48);
        int expected48 = sr48 * 2;
        double maxResampleErr = 0;
        for (int i = 64; i < expected48 - 64; i++)
        {
            double t = i / (double)sr48;
            float refL = (float)(0.5 * Math.Sin(2.0 * Math.PI * 440.0 * t));
            float refR = (float)(0.5 * Math.Cos(2.0 * Math.PI * 880.0 * t));
            maxResampleErr = Math.Max(maxResampleErr, Math.Max(Math.Abs(loaded48.Left[i] - refL), Math.Abs(loaded48.Right[i] - refR)));
        }
        double resDiffDb = maxResampleErr > 0 ? 20 * Math.Log10(maxResampleErr) : double.NegativeInfinity;
        double resRelDb = maxResampleErr > 0 ? 20 * Math.Log10(maxResampleErr / 0.5) : double.NegativeInfinity;
        bool resOk = loaded48.SampleRate == sr48 && loaded48.Left.Length == expected48 && resRelDb < -85;
        allOk &= resOk;
        Console.WriteLine($"{"AudioLoader 44.1→48 kHz (2.0 s, A4)",-36} {Db(resDiffDb),12} {Db(resRelDb),10}  {(resOk ? "igual" : "DISTINTO")}");

        // Verify B4-R1 & B4-R2: TakeRecorder.Dispose keeps takes > 0.3 s, Finish(discard: true) removes them
        string recKeepPath = Path.Combine(tmp, "rec_keep_on_dispose.wav");
        string recDiscardPath = Path.Combine(tmp, "rec_discard_explicit.wav");
        if (File.Exists(recKeepPath)) File.Delete(recKeepPath);
        if (File.Exists(recDiscardPath)) File.Delete(recDiscardPath);
        using (var rec = new TakeRecorder(recKeepPath, sr48))
        {
            var chunk = new float[4096];
            Array.Fill(chunk, 0.25f);
            for (int b = 0; b < 6; b++) // 24576 frames > 0.3 s
                rec.WriteFromCallback(chunk, chunk);
        }
        bool keepOk = File.Exists(recKeepPath) && new FileInfo(recKeepPath).Length > 44;
        RecordedTakeResult? discardRes;
        using (var rec = new TakeRecorder(recDiscardPath, sr48))
        {
            var chunk = new float[4096];
            Array.Fill(chunk, 0.25f);
            for (int b = 0; b < 6; b++)
                rec.WriteFromCallback(chunk, chunk);
            discardRes = rec.Finish(discard: true);
        }
        bool b4Ok = keepOk && discardRes == null && !File.Exists(recDiscardPath);
        allOk &= b4Ok;
        Console.WriteLine($"{"TakeRecorder: conservar al cerrar",-36} {"exacto",12} {"exacto",10}  {(b4Ok ? "igual" : "DISTINTO")}");

        // Verify B3-R1 & B3-R2: TrackDspChain.Reset zeroes state; MixGraphBus rings out reverb/delay tail when stopped
        var chainA = new TrackDspChain(sr48);
        var chainB = new TrackDspChain(sr48);
        var testTrackSpec = new MixTrackSpec
        {
            Sat = tube30,
            Comp = CompPresets.UpdateComp(CompState.Defaults with { On = true, Preset = "rhythm", Amount = 0.6 }, env),
            EqOn = true,
            EqBands = TrackEq.ApplyPreset("rhythm"),
        };
        chainA.Configure(testTrackSpec);
        chainB.Configure(testTrackSpec);
        var dummyM = new float[128];
        var dummyR = new float[128];
        var dummyD = new float[128];
        ch[0].AsSpan(0, 128).CopyTo(chainA.BufferL(128));
        ch[1].AsSpan(0, 128).CopyTo(chainA.BufferR(128));
        chainA.ProcessFilledBuffer(128, false, dummyM, dummyM, dummyR, dummyR, dummyD, dummyD, out _, out _, out _);
        chainA.Reset();
        var outAL = new float[128];
        var outAR = new float[128];
        var outBL = new float[128];
        var outBR = new float[128];
        ch[0].AsSpan(128, 128).CopyTo(chainA.BufferL(128));
        ch[1].AsSpan(128, 128).CopyTo(chainA.BufferR(128));
        chainA.ProcessFilledBuffer(128, false, outAL, outAR, dummyR, dummyR, dummyD, dummyD, out _, out _, out _);
        ch[0].AsSpan(128, 128).CopyTo(chainB.BufferL(128));
        ch[1].AsSpan(128, 128).CopyTo(chainB.BufferR(128));
        chainB.ProcessFilledBuffer(128, false, outBL, outBR, dummyR, dummyR, dummyD, dummyD, out _, out _, out _);
        double maxResetDiff = 0;
        for (int i = 0; i < 128; i++)
            maxResetDiff = Math.Max(maxResetDiff, Math.Max(Math.Abs(outAL[i] - outBL[i]), Math.Abs(outAR[i] - outBR[i])));
        bool b3Ok = maxResetDiff == 0;
        allOk &= b3Ok;
        Console.WriteLine($"{"TrackDspChain.Reset + colas buses",-36} {Db(maxResetDiff > 0 ? 20 * Math.Log10(maxResetDiff) : double.NegativeInfinity),12} {"exacto",10}  {(b3Ok ? "igual" : "DISTINTO")}");

        // Tuner: a guitar-like tone (harmonics, the fundamental not the loudest) reads the right note and cents
        foreach (var (hz, note) in new[] { (82.41, "E2"), (110.0, "A2"), (196.0, "G3"), (329.63, "E4"), (61.74, "B1"), (446.0, "A4") })
        {
            var tap = new PitchTap(sr48);
            var tone = new float[8192];
            for (int i = 0; i < tone.Length; i++)
            {
                double t = i / (double)sr48;
                tone[i] = (float)(0.1 * Math.Sin(2 * Math.PI * hz * t) + 0.2 * Math.Sin(4 * Math.PI * hz * t) + 0.08 * Math.Sin(6 * Math.PI * hz * t));
            }
            tap.Write(tone, tone);
            var got = tap.Read();
            double want = TunerReading.FromHz(hz).Cents;
            bool pitchOk = got is { } g && $"{g.Note}{g.Octave}" == note && Math.Abs(g.Cents - want) < 1.5;
            allOk &= pitchOk;
            Console.WriteLine($"{$"Afinador {hz:0.00} Hz",-36} {(got is { } r ? $"{r.Note}{r.Octave} {r.Cents:+0.0;-0.0}c" : "nada"),12} {$"{note} {want:+0.0;-0.0}c",10}  {(pitchOk ? "igual" : "DISTINTO")}");
        }
        var silent = new PitchTap(sr48);
        silent.Write(new float[8192], new float[8192]);
        // a tone at -60 dB RMS: nothing at the default gate (-50), a note at the most sensitive one (-70)
        var quiet = new float[8192];
        for (int i = 0; i < quiet.Length; i++) quiet[i] = (float)(Math.Pow(10, -60 / 20.0) * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 110 * i / sr48));
        var quietDefault = new PitchTap(sr48);
        quietDefault.Write(quiet, quiet);
        var quietSensitive = new PitchTap(sr48) { GateDb = PitchTap.MinGateDb };
        quietSensitive.Write(quiet, quiet);
        bool gateOk = silent.Read() == null && quietDefault.Read() == null && quietSensitive.Read() is { Midi: 45 };
        allOk &= gateOk;
        Console.WriteLine($"{"Afinador: silencio y umbral",-36} {"nada",12} {"nada",10}  {(gateOk ? "igual" : "DISTINTO")}");

        // LUFS (EBU Tech 3341 case 1): a 1 kHz sine at -23 dBFS on both channels reads -23 LUFS; with half of it
        // silent, the relative gate leaves the silence out and it still reads -23
        var sine = new float[sr48 * 20];
        for (int i = 0; i < sine.Length; i++) sine[i] = (float)(Math.Pow(10, -23 / 20.0) * Math.Sin(2 * Math.PI * 1000 * i / sr48));
        double lufs = LoudnessMeter.Measure(sine, sine, sr48);
        var gapped = (float[])sine.Clone();
        Array.Clear(gapped, 0, gapped.Length / 2);
        double lufsGated = LoudnessMeter.Measure(gapped, gapped, sr48);
        bool lufsOk = Math.Abs(lufs + 23) < 0.1 && Math.Abs(lufsGated + 23) < 0.1;
        allOk &= lufsOk;
        Console.WriteLine($"{"LUFS seno 1 kHz -23 dBFS (y con silencio)",-36} {$"{lufs:0.00} / {lufsGated:0.00}",12} {"-23.00",10}  {(lufsOk ? "igual" : "DISTINTO")}");

        // true peak (BS.1770 Annex 2): a tone at a quarter of the rate with its peaks between samples reads 0 dBTP
        // although no sample passes −3 dB; a 1 kHz tone at −6 dBFS reads −6
        var between = new float[sr48];
        var tone1k = new float[sr48];
        for (int i = 0; i < between.Length; i++)
        {
            between[i] = (float)Math.Sin(2 * Math.PI * (sr48 / 4.0) * i / sr48 + Math.PI / 4);
            tone1k[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / sr48));
        }
        double tpBetween = TruePeak.MeasureDb(between, between, between.Length), tp1k = TruePeak.MeasureDb(tone1k, tone1k, tone1k.Length);
        bool tpOk = Math.Abs(tpBetween) < 0.3 && Math.Abs(tp1k - 20 * Math.Log10(0.5)) < 0.1;
        allOk &= tpOk;
        Console.WriteLine($"{"Pico real: entre muestras / 1 kHz",-36} {$"{tpBetween:0.0} / {tp1k:0.0}",12} {"0 / -6.0",10}  {(tpOk ? "igual" : "DISTINTO")}");

        // the limiter holds the true peak: that tone 6 dB up (its samples under 0 dB, the peaks between them at +6)
        // plus a 220 Hz one comes out at or under −1 dBTP
        var hot = new float[sr48 * 2];
        for (int i = 0; i < hot.Length; i++)
            hot[i] = (float)(0.7 * Math.Sin(2 * Math.PI * (sr48 / 4.0) * i / sr48 + Math.PI / 4) + 0.6 * Math.Sin(2 * Math.PI * 220 * i / sr48));
        var hotR = (float[])hot.Clone();
        var tpLim = new MasterLimiter(sr48, MasterLimiter.LookaheadSamples(sr48));
        for (int pos = 0; pos < hot.Length; pos += 128) tpLim.Process(hot.AsSpan(pos, Math.Min(128, hot.Length - pos)), hotR.AsSpan(pos, Math.Min(128, hot.Length - pos)));
        double tpLimited = TruePeak.MeasureDb(hot[tpLim.Latency..], hotR[tpLim.Latency..], hot.Length - tpLim.Latency);
        bool tpLimOk = tpLimited <= MasterLimiter.CeilingDb + 0.1;
        allOk &= tpLimOk;
        Console.WriteLine($"{"Limitador: pico real a la salida",-36} {$"{tpLimited:0.00}",12} {$"<={MasterLimiter.CeilingDb:0.0}",10}  {(tpLimOk ? "igual" : "DISTINTO")}");

        // Export master (MixMaster): 2 s of a 220 Hz tone with a reverb send rings past its end, within the limit; a
        // −14 LUFS target lands there under the limiter's ceiling; with no tail and no target it is the plain render
        var tone220 = new float[sr48 * 2];
        for (int i = 0; i < tone220.Length; i++) tone220[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 220 * i / sr48));
        MixTrackSpec[] noteMix = [new MixTrackSpec { Clips = [new MixClipSpec { Left = tone220, Right = tone220, DurationSec = 2 }], ReverbSend = 0.6 }];
        var noteBus = new MixBusSpec();
        var mastered = MixMaster.Render(noteMix, noteBus, 2, sr48, MixMaster.MaxTailSec, -14);
        float ceiling = (float)Math.Pow(10, MasterLimiter.CeilingDb / 20);
        float masteredPeak = 0;
        for (int i = 0; i < mastered.Left.Length; i++) masteredPeak = Math.Max(masteredPeak, Math.Max(Math.Abs(mastered.Left[i]), Math.Abs(mastered.Right[i])));
        bool tailOk = mastered.TailSec > 0.2 && mastered.TailSec <= MixMaster.MaxTailSec && Math.Abs(mastered.DurationSec - 2 - mastered.TailSec) < 1e-6;
        double masteredTp = TruePeak.MeasureDb(mastered.Left, mastered.Right, mastered.Left.Length);
        bool masterLufsOk = Math.Abs(mastered.Lufs + 14) < 0.5 && masteredPeak <= ceiling + 1e-4 && masteredTp <= MasterLimiter.CeilingDb + 0.1;
        var plain = MixMaster.Render(noteMix, noteBus, 2, sr48, 0);
        var (plainRef, _) = MixRenderer.Render(noteMix, noteBus, 2, sr48);
        double plainDiff = 0;
        for (int i = 0; i < plainRef.Length - sr48 / 100; i++) plainDiff = Math.Max(plainDiff, Math.Abs(plainRef[i] - plain.Left[i]));
        bool plainOk = plain.Left.Length == plainRef.Length && plain.TailSec == 0 && plainDiff < 1e-6;
        allOk &= tailOk && masterLufsOk && plainOk;
        Console.WriteLine($"{"Exportar: cola de reverb (máx 8 s)",-36} {$"{mastered.TailSec:0.00} s",12} {"> 0,2 s",10}  {(tailOk ? "igual" : "DISTINTO")}");
        Console.WriteLine($"{"Exportar: a -14 LUFS, pico real",-36} {$"{mastered.Lufs:0.0} / {masteredTp:0.0}",12} {$"-14 / <={MasterLimiter.CeilingDb:0.0}",10}  {(masterLufsOk ? "igual" : "DISTINTO")}");
        Console.WriteLine($"{"Exportar: sin cola ni LUFS = mezcla",-36} {$"{plainDiff:0.0e0}",12} {"0",10}  {(plainOk ? "igual" : "DISTINTO")}");

        allOk &= MasterChecks(sr48);
        allOk &= TransposeCommands.Check();
        allOk &= InsertCommands.Check();
        allOk &= ReturnCommands.Check();
        allOk &= MeterChecks.Check();

        Console.WriteLine();
        Console.WriteLine(allOk
            ? "Todo bien."
            : "Alguna comprobación falló.");
        return allOk ? 0 : 3;
    }

    /// <summary>gclab lufs: the integrated loudness of a file, measured like the master's LUFS cell.</summary>
    /// <summary>
    /// The master on a WAV, like the window's Mastering view: runs <see cref="MasterChain"/> and the limiter with these
    /// settings and tells what each band reduced (on average and at most), what the limiter took and how loud it came
    /// in and out.
    /// </summary>
    public static int Master(Args a)
    {
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("masterizar: falta el archivo.");
        string output = a.Get("salida", Path.ChangeExtension(file, null) + "_master.wav");
        int sr = 48000;
        var audio = AudioLoader.Load(file, sr);
        double amount = a.Double("cuanto", MasterBands.DefaultAmount);
        var p = MasterParams.Off with
        {
            On = true,
            LowCutHz = a.Double("corte", 0),
            MudDb = a.Double("barro", 0),
            LowsDb = a.Double("graves", 0),
            PresenceDb = a.Double("presencia", 0),
            AirDb = a.Double("aire", 0),
            TiltDb = a.Double("inclinacion", 0),
            LowXHz = a.Double("cruce-graves", MasterBands.DefaultLowXHz),
            HighXHz = a.Double("cruce-agudos", MasterBands.DefaultHighXHz),
            Low = new MasterBandParams(a.Double("banda-graves", amount)),
            Mid = new MasterBandParams(a.Double("banda-medios", amount)),
            High = new MasterBandParams(a.Double("banda-agudos", amount)),
            LimOn = !a.Flag("sin-limitador"),
            LimDriveDb = a.Double("empuje", 0),
            LimCeilingDb = a.Double("techo", MasterLimiter.CeilingDb),
            LimReleaseMs = a.Double("release", MasterLimiter.ReleaseMs),
            TapeAmount = a.Double("cinta", 0),
            Width = a.Double("ancho", 1),
            MonoBassHz = a.Double("graves-mono", 0),
            ClipDb = a.Double("recorte", 0),
            PickAmount = a.Double("pua", 0),
        };
        // --look natural|glue|…: a look at --cuanto-look (0..1), over the knobs above
        if (a.Get("look") is { } lookId)
        {
            var look = MasterLooks.Get(lookId)
                ?? throw new ArgumentException($"--look: \"{lookId}\" no existe. Opciones: {string.Join(", ", MasterLooks.All.Select(l => l.Id))}.");
            double lookAmount = a.Double("cuanto-look", MasterLooks.DefaultAmount);
            p = MasterLooks.Apply(p, look, lookAmount);
            Console.WriteLine($"Look {look.Label} al {lookAmount * 100:0}%");
        }
        // --sugerime: what "Sugerime" hears in the file and proposes; it masters with that
        if (a.Flag("sugerime"))
        {
            var profile = MasterSuggest.Analyze(audio.Left, audio.Right, sr);
            var dev = MasterSuggest.Deviation(profile);
            Console.WriteLine("Octavas contra una mezcla terminada: " + string.Join("  ", MasterSuggest.Octaves.Select((f, i) =>
                $"{(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}")} {dev[i]:+0.0;-0.0;0.0}")));
            Console.WriteLine($"Picudez (dB): graves {profile.BandCrestDb[0]:0.0}, medios {profile.BandCrestDb[1]:0.0}, agudos {profile.BandCrestDb[2]:0.0}, púa {profile.PickCrestDb:0.0}");
            var sug = MasterSuggest.Suggest(profile, p);
            Console.WriteLine("Sugerime:");
            foreach (var line in sug.Lines) Console.WriteLine($"  {line.Text}");
            p = sug.Params;
        }
        // --volumen youtube|instagram|…: the Destino, measured on the whole file like the window does in the background
        if (a.Get("volumen") is { } vol)
        {
            var dest = MasterLoudness.Get(vol);
            if (dest.Lufs == null || dest.Id != vol)
                throw new ArgumentException($"--volumen: \"{vol}\" no existe. Opciones: {string.Join(", ", MasterLoudness.All.Skip(1).Select(t => t.Id))}.");
            MixTrackSpec[] mix = [new MixTrackSpec { Clips = [new MixClipSpec { Left = audio.Left, Right = audio.Right }] }];
            var measured = MixMaster.Render(mix, new MixBusSpec { Master = p with { TargetLufs = dest.Lufs } }, audio.DurationSec, sr, 0, dest.Lufs);
            p = p with { TargetLufs = dest.Lufs, TargetGainDb = measured.GainDb };
            Console.WriteLine($"Destino {dest.Label} ({dest.Lufs:0} LUFS): ganancia {measured.GainDb:+0.0;-0.0;0.0} dB, la exportación queda en {measured.Lufs:0.0} LUFS");
        }
        double gain = MasterLoudness.GainDb(p);
        var l = (float[])audio.Left.Clone();
        var r = (float[])audio.Right.Clone();
        var bus = new MixGraphBus(sr);
        bus.SetMaster(p);
        bus.Reset();
        double clipWorst = 0, pickWorst = 0;
        var (mean, worst, limWorst) = RunMaster(bus, l, r, () =>
        {
            clipWorst = Math.Max(clipWorst, -bus.Clip.TakeReductionDb());
            pickWorst = Math.Max(pickWorst, -bus.Master.TakePickReductionDb());
        });
        string[] names = ["Graves", "Medios", "Agudos"];
        for (int b = 0; b < MasterBands.Count; b++)
            Console.WriteLine(p.Band(b).Amount > 0
                ? $"{names[b],-8} {p.Band(b).Amount * 100,4:0}%  comprime ~{mean[b]:0.0} dB (pico {worst[b]:0.0} dB)"
                : $"{names[b],-8} {p.Band(b).Amount * 100,4:0}%  no comprime");
        if (p.PickAmount > 0) Console.WriteLine($"{"Púa",-8} {p.PickAmount * 100,4:0}%  baja 2-5 kHz hasta {pickWorst:0.0} dB");
        Console.WriteLine($"Soft clip: {(p.ClipDb > 0 ? $"recorte {p.ClipDb:0.0} dB + auto" : "auto")}, recorta hasta {clipWorst:0.0} dB");
        Console.WriteLine(p.LimOn ? $"Limitador: {(p.TargetLufs != null ? "ganancia del destino" : "empuje")} {gain:+0.0;-0.0;0.0} dB, techo {p.LimCeilingDb:0.0} dBTP, baja hasta {limWorst:0.0} dB" : "Limitador apagado");
        // the limiter's look-ahead: the output starts that many samples late
        int lat = bus.Limiter.Latency;
        Console.WriteLine($"LUFS: entra {LoudnessMeter.Measure(audio.Left, audio.Right, sr):0.0}, sale {LoudnessMeter.Measure(l[lat..], r[lat..], sr):0.0}; pico real sale {TruePeak.MeasureDb(l[lat..], r[lat..], l.Length - lat):0.0} dBTP");
        Wav.Write(output, l[lat..], r[lat..], sr);
        Console.WriteLine($"Escrito: {output}");
        return 0;
    }

    /// <summary>
    /// Runs the master and the limiter over the whole file in 128-sample blocks, like the engine: each band's mean
    /// reduction over the blocks where it compresses and its deepest one, and the limiter's deepest (dB, ≥ 0).
    /// </summary>
    static (double[] Mean, double[] Worst, double Limiter) RunMaster(MixGraphBus bus, float[] l, float[] r, Action? perBlock = null)
    {
        var sum = new double[MasterBands.Count];
        var count = new int[MasterBands.Count];
        var worst = new double[MasterBands.Count];
        double lim = 0;
        for (int i = 0; i < l.Length; i += Block)
        {
            int n = Math.Min(Block, l.Length - i);
            bus.ProcessReturnsAndLimiter(l.AsSpan(i, n), r.AsSpan(i, n), bus.PrepareReverbL(n), bus.PrepareReverbR(n),
                bus.PrepareDelayL(n), bus.PrepareDelayR(n));
            for (int b = 0; b < MasterBands.Count; b++)
            {
                double gr = -bus.Master.TakeReductionDb(b);
                if (gr > 0.05) { sum[b] += gr; count[b]++; }
                worst[b] = Math.Max(worst[b], gr);
            }
            lim = Math.Max(lim, -bus.Limiter.TakeReductionDb());
            perBlock?.Invoke();
        }
        var mean = new double[MasterBands.Count];
        for (int b = 0; b < MasterBands.Count; b++) mean[b] = count[b] > 0 ? sum[b] / count[b] : 0;
        return (mean, worst, lim);
    }

    public static int Lufs(Args a)
    {
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("lufs: falta el archivo.");
        var audio = AudioLoader.Load(file, 48000);
        double lufs = LoudnessMeter.Measure(audio.Left, audio.Right, audio.SampleRate);
        Console.WriteLine(double.IsFinite(lufs) ? $"{lufs:0.0} LUFS integrado ({audio.Left.Length / (double)audio.SampleRate:0.0} s)" : "Silencio: no llega a -70 LUFS.");
        return 0;
    }

    /// <summary>gclab afinar: what the tuner hears at a moment of a file (its first 4096 samples from --en), with the
    /// window's sensitivity (--umbral, the level gate in dB).</summary>
    public static int Tune(Args a)
    {
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("afinar: falta el archivo.");
        var audio = AudioLoader.Load(file, 48000);
        int from = (int)(a.Double("en", 0) * audio.SampleRate);
        if (from + 4096 > audio.Left.Length) throw new ArgumentException("afinar: el archivo termina antes.");
        var tap = new PitchTap(audio.SampleRate) { GateDb = a.Double("umbral", PitchTap.DefaultGateDb) };
        tap.Write(audio.Left.AsSpan(from, 4096), audio.Right.AsSpan(from, 4096));
        Console.WriteLine(tap.Read() is { } r
            ? $"{r.Note}{r.Octave} {r.Cents:+0.0;-0.0;0.0} cents ({r.Hz:0.00} Hz)"
            : $"No hay una nota clara (umbral {tap.GateDb:0} dB).");
        return 0;
    }

    /// <summary>--stems: one WAV per track that sounds, next to the export, like the window's "Stems" option.</summary>
    static void ExportStems(ExportJobSpec job, string exportPath, double durationSec)
    {
        string last = "";
        var (folder, count) = AudioExporter.ExportStemsAsync(job, exportPath, f =>
        {
            string line = $"  [Stems] {Math.Floor(f * 10) * 10:0}%";
            if (line != last) Console.WriteLine(last = line);
        }, durationSec: durationSec).GetAwaiter().GetResult();
        Console.WriteLine($"Stems: {count} en {folder}");
        foreach (var f in Directory.GetFiles(folder, "*.wav").Order()) Console.WriteLine($"  {Path.GetFileName(f)}");
    }

    /// <summary>The line after an export: the mix's integrated loudness, like the dialog's done screen, and the effect tail.</summary>
    /// <summary>--resolucion: "720", "1080p", "1440", "2160" or "4k"; null when not given.</summary>
    static int? ParseResolution(string? s)
    {
        if (s == null) return null;
        string v = s.Trim().ToLowerInvariant().TrimEnd('p');
        int r = v == "4k" ? 2160 : int.TryParse(v, out int n) ? n : 0;
        if (!Mp4Exporter.Resolutions.Contains(r))
            throw new ArgumentException($"--resolucion: \"{s}\" no existe. Opciones: 720, 1080, 1440, 2160 (4k).");
        return r;
    }

    static string Loudness(ExportResult r) =>
        (double.IsFinite(r.Lufs) ? $"Mezcla: {r.Lufs:0.0} LUFS integrado" : "Mezcla: silencio (no llega a -70 LUFS)")
        + (r.TailSec > 0 ? $", {r.TailSec:0.0} s de cola de efectos" : ", sin cola");

    public static int Export(Args a)
    {
        bool testMode = a.Flag("prueba") || a.Positional.Count == 0;
        string targetDir;
        string? tempTestDir = null;

        if (testMode)
        {
            tempTestDir = Path.Combine(Path.GetTempPath(), $"gclab-export-{Guid.NewGuid():N}"[..24]);
            targetDir = BuildSyntheticExportProject(tempTestDir);
            Console.WriteLine($"Proyecto de prueba generado en: {targetDir}");
        }
        else
        {
            targetDir = Path.GetFullPath(a.Positional[0]);
        }

        try
        {
            using var store = ProjectStore.Open(targetDir);
            var p = store.Project;
            var quality = Mp4Exporter.ParseQuality(a.Get("calidad") ?? p.Settings.Export.Quality);
            string? outDir = a.Get("carpeta") ?? p.Settings.Export.Dir;
            string fileName = a.Get("salida", string.IsNullOrWhiteSpace(p.Name) ? "GuitarCam" : p.Name);
            bool overwrite = a.Flag("reemplazar") || testMode;
            int sr = 48000;
            // --destino: a social preset like the dialog's "Destino" (its frame); the loudness is the master's Destino,
            // like the window (--volumen overrides it, --lufs gives a number), --cola how long the effects may ring after
            // the last clip (0 = cut there)
            var target = ExportTargets.Get(a.Get("destino") ?? ExportTargets.Project);
            if (a.Get("destino") is { } dest && target.Id != dest)
                throw new ArgumentException($"--destino: \"{dest}\" no existe. Opciones: {string.Join(", ", ExportTargets.All.Select(t => t.Id))}.");
            if (a.Get("volumen") is { } vol)
            {
                if (MasterLoudness.Get(vol).Id != vol && vol != "como-esta")
                    throw new ArgumentException($"--volumen: \"{vol}\" no existe. Opciones: como-esta, {string.Join(", ", MasterLoudness.All.Skip(1).Select(t => t.Id))}.");
                p.Settings.Master.Target = vol == "como-esta" ? MasterLoudness.AsIs : vol;
            }
            double? targetLufs = a.Get("lufs") != null ? a.Double("lufs", ExportTargets.StreamingLufs) : MasterLoudness.Get(p.Settings.Master.Target).Lufs;
            double tailSec = Math.Clamp(a.Double("cola", MixMaster.MaxTailSec), 0, MixMaster.MaxTailSec);

            var takes = new List<ExportTakeSpec>();
            var mixTracks = new List<MixTrackSpec>();
            int panels = 0;
            foreach (var trk in p.Tracks)
            {
                var clips = new List<MixClipSpec>();
                int? panel = null; // like the window: a panel when the first clip has a picture, shared by the rest
                double trackRms = -24;
                bool hasRms = false;
                foreach (var c in trk.Clips)
                {
                    var m = p.FindMedia(c.MediaId);
                    double dur = m?.DurationSec ?? 0;
                    if (store.Resolve(m?.Audio) is { } ap && File.Exists(ap))
                    {
                        var loaded = AudioLoader.Load(ap, sr);
                        if (dur <= 0) dur = loaded.DurationSec;
                        var (rms, _) = LevelAnalysis.Analyze([loaded.Left, loaded.Right], sr);
                        if (!hasRms) { trackRms = rms; hasRms = true; }
                        var (shiftedL, shiftedR) = store.TrackAudio(trk, ap, loaded.Left, loaded.Right);
                        clips.Add(new MixClipSpec
                        {
                            Left = shiftedL,
                            Right = shiftedR,
                            DurationSec = dur,
                            AtSec = c.AtSec,
                            TrimStartSec = c.TrimStartSec,
                            TrimEndSec = c.TrimEndSec,
                            FadeInSec = c.FadeInSec,
                            FadeOutSec = c.FadeOutSec,
                            GainDb = c.GainDb,
                            GainCurve = c.CurvePoints(),
                            SyncMs = c.EffectiveSyncMs(m),
                            Transition = c.Transition,
                            TransitionSec = c.TransitionSec,
                            TakeId = c.MediaId,
                        });
                    }
                    if (trk.Video && (panel != null || c == trk.Clips[0]) && store.Resolve(m?.Video) is { } vp && File.Exists(vp))
                    {
                        panel ??= panels++;
                        takes.Add(new ExportTakeSpec
                        {
                            Id = c.Id,
                            Name = string.IsNullOrWhiteSpace(trk.Name) ? (m?.Name ?? "") : trk.Name,
                            VideoPath = vp,
                            DurationSec = dur,
                            AtSec = c.AtSec,
                            SyncMs = c.EffectiveSyncMs(m),
                            VideoOffsetMs = c.EffectiveVideoOffsetMs(m),
                            TrimStartSec = c.TrimStartSec,
                            TrimEndSec = c.TrimEndSec,
                            FadeInSec = c.FadeInSec,
                            FadeOutSec = c.FadeOutSec,
                            LinkVideoFades = c.LinkVideoFades,
                            VideoFadeInSec = c.VideoFadeInSec,
                            VideoFadeOutSec = c.VideoFadeOutSec,
                            Zoom = c.Framing.Zoom,
                            PanX = c.Framing.PanX,
                            PanY = c.Framing.PanY,
                            Mirror = c.Framing.Mirror,
                            Transition = c.Transition,
                            TransitionSec = c.TransitionSec,
                            Panel = panel.Value,
                        });
                    }
                }

                var ch = trk.Channel;

                var folder = TrackFolders.Mix(p, trk);
                mixTracks.Add(new MixTrackSpec
                {
                    Name = trk.Name,
                    Clips = clips,
                    Volume = ch.Volume * folder.Gain,
                    Pan = ch.Pan,
                    Muted = ch.Muted || folder.Muted,
                    Solo = ch.Solo || folder.Solo,
                    Inserts = ChannelDsp.Specs(ch, trackRms),
                    Folder = ChannelDsp.FolderIndex(p, trk),
                    ReverbOn = ch.ReverbOn,
                    ReverbSend = ch.ReverbSend,
                    DelayOn = ch.DelayOn,
                    DelaySend = ch.DelaySend,
                    ReverbPre = ch.ReverbPre,
                    DelayPre = ch.DelayPre,
                    Sends = ChannelDsp.Sends(p, ch),
                });
            }

            var col = p.Settings.Color;
            var vfx = p.Settings.VideoFx;
            var job = new ExportJobSpec
            {
                ProjectDir = store.Dir,
                ProjectName = string.IsNullOrWhiteSpace(p.Name) ? "GuitarCam" : p.Name,
                OutDir = outDir,
                FileName = fileName,
                Overwrite = overwrite,
                Quality = quality,
                Layout = p.Settings.Layout,
                AspectRatio = ExportTargets.AspectFor(target.Id, p.Settings.Aspect),
                BorderSize = 4,
                Color = new ExportColorSpec(col.On, col.Look, col.Amount, col.Exposure, col.Contrast, col.Highlights, col.Shadows, col.Saturation, col.Vibrance, col.Temperature, col.Tint, col.Fade, col.Whites, col.Blacks, col.Light),
                VideoFx = new ExportVideoFxSpec(vfx.On, vfx.Vignette, vfx.VignetteSize, vfx.Grain, vfx.Letterbox, vfx.LetterboxHeight),
                Takes = takes,
                MixTracks = mixTracks,
                MixBus = ChannelDsp.Bus(p),
                SampleRate = sr,
                TailSec = tailSec,
                TargetLufs = targetLufs,
                // --codificador auto|cpu|nvenc|amf|qsv: who encodes the video (auto = the video card if it has one)
                Encoder = VideoEncoders.ParsePreference(a.Get("codificador")),
                // --resolucion 720|1080|1440|2160 (4k) and --bitrate Mbps: the dialog's "Avanzado" (default: the project's)
                Resolution = ParseResolution(a.Get("resolucion")) ?? p.Settings.Export.Resolution,
                BitrateMbps = a.Get("bitrate") != null ? Mp4Exporter.NormalizeBitrate(a.Double("bitrate", 0)) : p.Settings.Export.BitrateMbps,
            };
            Console.WriteLine($"Destino: {target.Label} ({job.AspectRatio}){(targetLufs is { } tl ? $", mezcla a {tl:0.0} LUFS" : "")}, cola hasta {tailSec:0.0} s");

            int lastPct = -1;
            // --audio wav|mp3, or a project in solo audio: the mix as an audio file, like the window's "Exportar audio"
            if (a.Get("audio") is { } fmtArg || p.Settings.AudioOnly)
            {
                var fmt = AudioExporter.ParseFormat(a.Get("audio") ?? p.Settings.Export.AudioFormat);
                string last = "";
                var ares = AudioExporter.ExportAsync(job, fmt, prog =>
                {
                    string line = $"  [{prog.Stage}] {Math.Floor(prog.Fraction * 10) * 10:0}%";
                    if (line != last) Console.WriteLine(last = line);
                }).GetAwaiter().GetResult();
                Console.WriteLine($"Exportado: {ares.FullPath} ({Mp4Exporter.FormatBytes(ares.Bytes)}, {ares.DurationSec:0.0} s en {ares.ElapsedSec:0.0} s)");
                Console.WriteLine(Loudness(ares));
                if (a.Flag("stems")) ExportStems(job, ares.FullPath, ares.DurationSec);
                return 0;
            }
            var res = Mp4Exporter.ExportAsync(job, prog =>
            {
                if (prog.Percent != lastPct && (prog.Percent % 10 == 0 || prog.Percent == 100))
                {
                    lastPct = prog.Percent;
                    Console.WriteLine($"  [{prog.Stage}] {prog.Percent}%");
                }
            }).GetAwaiter().GetResult();

            Console.WriteLine($"Exportado: {res.FullPath} ({Mp4Exporter.FormatBytes(res.Bytes)}, {res.DurationSec:0.0} s en {res.ElapsedSec:0.0} s)");
            Console.WriteLine(res.Encoder is { } enc && enc != VideoEncoderKind.Cpu
                ? $"Video codificado con la placa ({VideoEncoders.Brand(enc)}, {VideoEncoders.Id(enc)})"
                : "Video codificado con el procesador (libx264)");
            Console.WriteLine(Loudness(res));
            if (a.Flag("stems")) ExportStems(job, res.FullPath, res.DurationSec);

            if (testMode)
            {
                // Verify collision check suggests "(2)"
                var check2 = Mp4Exporter.CheckDestination(store.Dir, outDir, fileName);
                if (!check2.Exists || !check2.Suggested.EndsWith(" (2)", StringComparison.Ordinal))
                    throw new InvalidOperationException($"CheckDestination falló: Exists={check2.Exists}, Suggested={check2.Suggested}");
                Console.WriteLine($"Colisión verificada: sugiere «{check2.Suggested}»");

                // Verify cancellation kills FFmpeg and cleans up .part files
                using var cancelCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
                var cancelJob = job with { FileName = "Cancelado", Overwrite = true };
                bool cancelled = false;
                try
                {
                    Mp4Exporter.ExportAsync(cancelJob, null, cancelCts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                string exportsFolder = Path.Combine(store.Dir, "exports");
                bool anyPartLeft = Directory.Exists(exportsFolder) && Directory.GetFiles(exportsFolder, "*.part").Length > 0;
                bool cancelMp4Exists = File.Exists(Path.Combine(exportsFolder, "Cancelado.mp4"));
                if (!cancelled || anyPartLeft || cancelMp4Exists)
                    throw new InvalidOperationException($"Cancelación falló: cancelled={cancelled}, partLeft={anyPartLeft}, mp4Exists={cancelMp4Exists}");
                Console.WriteLine("Cancelación verificada: proceso terminado y archivo .part eliminado.");

                // Benchmark B7 preview color + grain + vignette + letterbox per frame
                var benchGrade = new ExportColorSpec(true, "cine", 85, 10, 15, -10, 20, 10, 15, 8, -5, 10);
                var benchFx = new ExportVideoFxSpec(true, 35, 50, 25, "2.39");
                var benchLut = new VideoColorProcessor.Lut3DTable(benchGrade);
                int benchGrain = VideoColorProcessor.GrainLevels(benchFx);
                foreach (var (bw, bh) in new[] { (640, 360), (1920, 1080) })
                {
                    byte[] srcBuf = new byte[bw * bh * 4];
                    byte[] dstBuf = new byte[bw * bh * 4];
                    for (int i = 0; i < srcBuf.Length; i += 4)
                    {
                        srcBuf[i] = (byte)(i & 255);
                        srcBuf[i + 1] = (byte)((i >> 3) & 255);
                        srcBuf[i + 2] = (byte)((i >> 6) & 255);
                        srcBuf[i + 3] = 255;
                    }
                    // Warmup
                    VideoColorProcessor.GradeAndGrainBgra(srcBuf, dstBuf, bw, bh, benchLut, benchGrain, 1);
                    var swLutOnly = System.Diagnostics.Stopwatch.StartNew();
                    const int iters = 30;
                    for (int it = 0; it < iters; it++)
                        VideoColorProcessor.GradeAndGrainBgra(srcBuf, dstBuf, bw, bh, benchLut, 0, it);
                    swLutOnly.Stop();
                    var swLutGrain = System.Diagnostics.Stopwatch.StartNew();
                    for (int it = 0; it < iters; it++)
                        VideoColorProcessor.GradeAndGrainBgra(srcBuf, dstBuf, bw, bh, benchLut, benchGrain, it);
                    swLutGrain.Stop();
                    byte[] parBuf = new byte[dstBuf.Length];
                    VideoColorProcessor.GradeAndGrainBgraParallel(srcBuf, parBuf, bw, bh, benchLut, benchGrain, iters - 1);
                    if (!parBuf.AsSpan().SequenceEqual(dstBuf))
                        throw new InvalidOperationException("El color en paralelo no da igual que en un solo hilo");
                    var swPar = System.Diagnostics.Stopwatch.StartNew();
                    for (int it = 0; it < iters; it++)
                        VideoColorProcessor.GradeAndGrainBgraParallel(srcBuf, parBuf, bw, bh, benchLut, benchGrain, it);
                    swPar.Stop();
                    Console.WriteLine($"Costo B7 ({bw}×{bh}): LUT 3D trilineal = {swLutOnly.Elapsed.TotalMilliseconds / iters:0.00} ms/cuadro · LUT + grano = {swLutGrain.Elapsed.TotalMilliseconds / iters:0.00} ms/cuadro · en paralelo = {swPar.Elapsed.TotalMilliseconds / iters:0.00} ms/cuadro (igual)");
                }
            }
            return 0;
        }
        finally
        {
            if (tempTestDir != null && !a.Flag("conservar"))
            {
                try { Directory.Delete(tempTestDir, recursive: true); } catch { }
            }
        }
    }

    static string BuildSyntheticExportProject(string rootDir)
    {
        Directory.CreateDirectory(rootDir);
        string mediaDir = Path.Combine(rootDir, "media");
        Directory.CreateDirectory(mediaDir);

        int sr = 48000;
        var sig = TestSignal(sr, 2.0);
        string wav1 = Path.Combine(mediaDir, "toma1.wav");
        string wav2 = Path.Combine(mediaDir, "toma2.wav");
        Wav.Write(wav1, sig[0], sig[1], sr);
        Wav.Write(wav2, sig[1], sig[0], sr);

        string mp4_1 = Path.Combine(mediaDir, "toma1.mp4");
        string mp4_2 = Path.Combine(mediaDir, "toma2.mp4");
        MakeTestVideo(mp4_1, "0x1d4ed8", 2.0);
        MakeTestVideo(mp4_2, "0x047857", 2.0);

        using var store = ProjectStore.Create(rootDir, new Project { Name = "Prueba Export" });
        var p = store.Project;
        p.Settings.Color = new ColorGrade { On = true, Look = "warm-amber", Amount = 80, Contrast = 10, Saturation = 5 };
        p.Settings.VideoFx = new VideoFx { On = true, Vignette = 25, VignetteSize = 50, Grain = 15, Letterbox = "2.39:1" };

        var m1 = new Media { Id = "med-1", Name = "Rítmica", Audio = "media/toma1.wav", Video = "media/toma1.mp4", DurationSec = 2.0, SampleRate = sr, Channels = 2, SyncMs = -20, VideoOffsetMs = 80 };
        var m2 = new Media { Id = "med-2", Name = "Punteo", Audio = "media/toma2.wav", Video = "media/toma2.mp4", DurationSec = 2.0, SampleRate = sr, Channels = 2, SyncMs = -15, VideoOffsetMs = 80 };
        p.Media.AddRange([m1, m2]);

        p.Tracks.Add(new Track
        {
            Id = "trk-1",
            Name = "Rítmica",
            Video = true,
            Channel = new Channel { Volume = 0.9, Pan = -0.35, Sat = new SatSettings { On = true, Mode = "tube", Amount = 0.3 } },
            Clips = [new Clip { Id = "clp-1", MediaId = "med-1", AtSec = -0.02, SyncMs = -25, VideoOffsetMs = 70, FadeInSec = 0.1, FadeOutSec = 0.2, Framing = new Framing { Zoom = 1.2, PanX = 0.1, PanY = -0.1, Mirror = false } }],
        });
        p.Tracks.Add(new Track
        {
            Id = "trk-2",
            Name = "Punteo",
            Video = true,
            Channel = new Channel { Volume = 0.85, Pan = 0.35, ReverbOn = true, ReverbSend = 0.25 },
            Clips = [new Clip { Id = "clp-2", MediaId = "med-2", AtSec = 0.1, FadeInSec = 0.1, FadeOutSec = 0.2, Framing = new Framing { Zoom = 1.0, Mirror = true } }],
        });
        store.Save();
        return rootDir;
    }

    static void MakeTestVideo(string outPath, string colorHex, double seconds)
    {
        var psi = new ProcessStartInfo("ffmpeg",
        [
            "-y", "-v", "error",
            "-f", "lavfi", "-i", $"color=c={colorHex}:s=640x360:r=30:d={seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            outPath,
        ])
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("No pude iniciar ffmpeg.");
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new InvalidOperationException($"ffmpeg falló creando video de prueba: {err}");
    }

    static string Db(double db) => double.IsNegativeInfinity(db) ? "exacto" : $"{db:0.0} dB";

    /// <summary>
    /// The master (<see cref="MasterChain"/>): with the EQ flat and the bands at 0 the crossover adds back to the input;
    /// fitted to the mix, each band reduces about what its "Cuánto" asks and the level stays; the limiter holds the
    /// card's Techo; off, it does not touch a sample.
    /// </summary>
    static bool MasterChecks(int sr)
    {
        bool ok = true;
        static double Rms(float[] x, int from)
        {
            double s = 0;
            for (int i = from; i < x.Length; i++) s += x[i] * (double)x[i];
            return 10 * Math.Log10(Math.Max(1e-20, s / Math.Max(1, x.Length - from)));
        }
        static MasterChain Chain(int sr, MasterParams p)
        {
            var c = new MasterChain(sr);
            c.Set(p);
            c.Reset();
            return c;
        }

        // flat: tones across the crossovers come out at their level
        var flat = MasterParams.Off with { On = true };
        double worst = 0;
        foreach (double f in new[] { 40.0, 150, 600, 2500, 5000, 12000 })
        {
            var x = new float[sr];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.3 * Math.Sin(2 * Math.PI * f * i / sr));
            var l = (float[])x.Clone();
            var r = (float[])x.Clone();
            var chain = Chain(sr, flat);
            Run(l, r, chain.Process);
            worst = Math.Max(worst, Math.Abs(Rms(l, sr / 2) - Rms(x, sr / 2)));
        }
        bool flatOk = worst < 0.05;
        ok &= flatOk;
        Console.WriteLine($"{"Master plano: suma de bandas",-36} {$"{worst:0.000} dB",12} {"< 0,05 dB",10}  {(flatOk ? "igual" : "DISTINTO")}");

        // the live path, as the window does it: project settings → a bus that was never reset → +6 dB of air (a shelf
        // at 10 kHz, so ~+4.5 dB at 12 kHz)
        {
            var settings = new GuitarCam.Projects.MasterSettings { On = true, AirDb = 6 };
            var bus = new MixGraphBus(sr);
            bus.Master.Set(settings.ToParams());
            var x = new float[sr];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 12000 * i / sr));
            var l = (float[])x.Clone();
            var r = (float[])x.Clone();
            var empty = new float[TrackDspChain.MaxBlockSize];
            for (int i = 0; i < l.Length; i += 256)
            {
                int n = Math.Min(256, l.Length - i);
                bus.ProcessReturnsAndLimiter(l.AsSpan(i, n), r.AsSpan(i, n), bus.PrepareReverbL(n), bus.PrepareReverbR(n),
                    bus.PrepareDelayL(n), bus.PrepareDelayR(n), limit: false);
            }
            double gain = Rms(l, sr / 2) - Rms(x, sr / 2);
            bool liveOk = gain is > 3.5 and < 6;
            ok &= liveOk;
            Console.WriteLine($"{"Master en vivo: Aire +6 a 12 kHz",-36} {$"{gain:+0.0} dB",12} {"+3,5 a +6",10}  {(liveOk ? "igual" : "DISTINTO")}");
        }

        // the limiter as the card sets it, in the export: Empuje pushes the mix in, Techo holds the peaks
        {
            var tone = new float[sr * 2];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 220 * i / sr));
            MixTrackSpec[] mix = [new MixTrackSpec { Clips = [new MixClipSpec { Left = tone, Right = tone, DurationSec = 2 }] }];
            var pushed = MixMaster.Render(mix, new MixBusSpec { Master = MasterParams.Off with { LimDriveDb = 12, LimCeilingDb = -1 } }, 2, sr, 0);
            float pk = 0;
            for (int i = 0; i < pushed.Left.Length; i++) pk = Math.Max(pk, Math.Abs(pushed.Left[i]));
            double pkDb = 20 * Math.Log10(pk);
            bool limOk = pkDb <= -1 + 1e-3 && pkDb > -1.5 && Math.Abs(pushed.GainDb - 12) < 1e-9;
            ok &= limOk;
            Console.WriteLine($"{"Limitador: empuje +12, techo -1",-36} {$"{pkDb:0.00} dB",12} {"-1,00 dB",10}  {(limOk ? "igual" : "DISTINTO")}");
        }

        // Cinta adds harmonics: a 200 Hz tone gets energy at 400 / 600 Hz (Goertzel), more with more Cinta
        {
            double Harm(double amount)
            {
                var l = new float[sr];
                for (int i = 0; i < l.Length; i++) l[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 200 * i / sr));
                var r = (float[])l.Clone();
                Run(l, r, Chain(sr, flat with { TapeAmount = amount }).Process);
                double Bin(double f)
                {
                    double w = 2 * Math.PI * f / sr, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
                    for (int i = sr / 2; i < sr; i++) { double v = l[i] + c * s1 - s2; s2 = s1; s1 = v; }
                    return s1 * s1 + s2 * s2 - c * s1 * s2;
                }
                return 10 * Math.Log10((Bin(400) + Bin(600) + 1e-20) / Bin(200));
            }
            double none = Harm(0), some = Harm(0.5), full = Harm(1);
            bool tapeOk = none < -90 && some > none + 20 && full > some;
            ok &= tapeOk;
            Console.WriteLine($"{"Cinta: armónicos 0 / 50 / 100 %",-36} {$"{none:0} / {some:0} / {full:0}",12} {"suben",10}  {(tapeOk ? "igual" : "DISTINTO")}");
        }

        // Ancho: 0 = mono; graves mono at 150 Hz takes the side out at 50 Hz and leaves it at 2 kHz
        {
            float[] SideTone(double f)
            {
                var x = new float[sr];
                for (int i = 0; i < x.Length; i++) x[i] = (float)(0.2 * Math.Sin(2 * Math.PI * f * i / sr));
                return x;
            }
            var l = SideTone(1000);
            var r = l.Select(v => -v).ToArray();
            Run(l, r, Chain(sr, flat with { Width = 0 }).Process);
            double diff = 0;
            for (int i = sr / 2; i < sr; i++) diff = Math.Max(diff, Math.Abs(l[i] - r[i]));
            double Side(double f)
            {
                var a = SideTone(f);
                var b = a.Select(v => -v).ToArray();
                var src = (float[])a.Clone();
                Run(a, b, Chain(sr, flat with { MonoBassHz = 150 }).Process);
                return Rms(a, sr / 2) - Rms(src, sr / 2);
            }
            double low = Side(50), high = Side(2000);
            bool widthOk = diff < 1e-4 && low < -15 && Math.Abs(high) < 0.2;
            ok &= widthOk;
            Console.WriteLine($"{"Ancho 0 = mono; graves mono 50/2k",-36} {$"{low:0} / {high:0.0} dB",12} {"< -15 / 0",10}  {(widthOk ? "igual" : "DISTINTO")}");
        }

        // Ancho keeps the level: two different guitars hard left and right (as much side as mid), widened or
        // narrowed, come out as loud as they went in
        {
            var song2 = TestSignal(sr, 4);
            var srcL = song2[0];
            var srcR = new float[srcL.Length];
            for (int i = 0; i < srcR.Length; i++) srcR[i] = srcL[(i + sr / 3) % srcL.Length]; // a different take
            static double Both(float[] l, float[] r, int from) => 10 * Math.Log10(Math.Pow(10, Rms(l, from) / 10) + Math.Pow(10, Rms(r, from) / 10));
            double inDb = Both(srcL, srcR, sr);
            double worstW = 0;
            foreach (double w in new[] { 0.5, 1.3, 2.0 })
            {
                var l = (float[])srcL.Clone();
                var r = (float[])srcR.Clone();
                Run(l, r, Chain(sr, flat with { Width = w }).Process);
                worstW = Math.Max(worstW, Math.Abs(Both(l, r, sr) - inDb));
            }
            bool keepOk = worstW < 0.5;
            ok &= keepOk;
            Console.WriteLine($"{"Ancho 50/130/200 %: mismo volumen",-36} {$"{worstW:0.00} dB",12} {"< 0,5 dB",10}  {(keepOk ? "igual" : "DISTINTO")}");
        }

        // off: bit for bit
        var song = TestSignal(sr, 8);
        var offL = (float[])song[0].Clone();
        var offR = (float[])song[1].Clone();
        Run(offL, offR, Chain(sr, MasterParams.Off).Process);
        bool offOk = offL.AsSpan().SequenceEqual(song[0]) && offR.AsSpan().SequenceEqual(song[1]);
        ok &= offOk;
        Console.WriteLine($"{"Master apagado: no toca la mezcla",-36} {(offOk ? "exacto" : "cambia"),12} {"exacto",10}  {(offOk ? "igual" : "DISTINTO")}");

        // soft clip: with Empuje +8 dB, Recorte 3 takes most of the work off the limiter and the peak stays at the Techo;
        // with the knob at 0, Auto shares the hottest peaks (never cutting more than its most), and peaks only a little
        // over the Techo it leaves to the limiter alone, bit for bit
        {
            double clipWorst = 0;
            double Lim(MasterParams p, out float[] outL, float scale = 1)
            {
                var l = song[0].Select(v => v * scale).ToArray();
                var r = song[1].Select(v => v * scale).ToArray();
                var bus = new MixGraphBus(sr);
                bus.SetMaster(p);
                bus.Reset();
                clipWorst = 0;
                var (_, _, lim) = RunMaster(bus, l, r, () => clipWorst = Math.Max(clipWorst, -bus.Clip.TakeReductionDb()));
                outL = l;
                float pk = 0;
                for (int i = 0; i < l.Length; i++) pk = Math.Max(pk, Math.Abs(l[i]));
                if (20 * Math.Log10(pk) > MasterLimiter.CeilingDb + 1e-3) return double.NaN;
                return lim;
            }
            var pushed = flat with { LimDriveDb = 8 };
            double without = Lim(pushed with { ClipOn = false }, out _), with = Lim(pushed with { ClipDb = 3 }, out _);
            bool clipOk = with < without - 2;
            ok &= clipOk;
            Console.WriteLine($"{"Soft clip 3 dB: lo que baja el lim.",-36} {$"{without:0.0} → {with:0.0} dB",12} {"menos",10}  {(clipOk ? "igual" : "DISTINTO")}");
            double auto = Lim(pushed, out _);
            double autoCut = clipWorst;
            bool autoOk = auto < without - 1 && autoCut > 0.5 && autoCut <= SoftClipper.AutoMaxCutDb + 0.1;
            // the song with its loudest peak 1,5 dB over the Techo: the limiter's alone
            float pkSong = 0;
            foreach (var ch in song) foreach (var v in ch) pkSong = Math.Max(pkSong, Math.Abs(v));
            float near = (float)(Math.Pow(10, (MasterLimiter.CeilingDb + SoftClipper.AutoFreeDb - 0.5) / 20) / pkSong);
            Lim(flat with { ClipOn = false }, out var smallOff, near);
            Lim(flat, out var smallOn, near);
            bool smallOk = smallOn.AsSpan().SequenceEqual(smallOff);
            ok &= autoOk && smallOk;
            Console.WriteLine($"{"Empuje +8, Auto: lim. / clip (máx)",-36} {$"{without:0.0} → {auto:0.0} / {autoCut:0.0}",12} {$"clip <={SoftClipper.AutoMaxCutDb:0}",10}  {(autoOk ? "igual" : "DISTINTO")}");
            Console.WriteLine($"{"Pico 1,5 dB sobre el techo: sin clip",-36} {(smallOk ? "exacto" : "cambia"),12} {"exacto",10}  {(smallOk ? "igual" : "DISTINTO")}");
        }

        // Destino: the gain the export finds, applied in playback, gives the export's same samples; exporting again from
        // it keeps that gain (nothing changed); and the result is on the target
        {
            MixTrackSpec[] mix = [new MixTrackSpec { Clips = [new MixClipSpec { Left = song[0], Right = song[1], DurationSec = 8 }] }];
            var toTarget = flat with { TargetLufs = -14 };
            var export = MixMaster.Render(mix, new MixBusSpec { Master = toTarget }, 8, sr, 0, -14);
            var measured = toTarget with { TargetGainDb = export.GainDb };
            var (liveL, _) = MixRenderer.Render(mix, new MixBusSpec { Master = measured }, 8, sr);
            double diff = 0;
            for (int i = 0; i < export.Left.Length - sr / 50; i++) diff = Math.Max(diff, Math.Abs(liveL[i] - export.Left[i]));
            var again = MixMaster.Render(mix, new MixBusSpec { Master = measured }, 8, sr, 0, -14);
            bool targetOk = diff < 1e-6 && again.GainDb == export.GainDb && Math.Abs(export.Lufs + 14) < 0.3;
            ok &= targetOk;
            Console.WriteLine($"{"Destino -14: reproducir = exportar",-36} {$"{export.GainDb:+0.0} dB · {diff:0.0e0}",12} {"0",10}  {(targetOk ? "igual" : "DISTINTO")}");
        }

        // compressing: every band works at 70 %, the same 10 dB louder or quieter (it follows its own level), and the
        // loudness stays
        var band = new MasterBandParams(0.7);
        var bandsOn = flat with { Low = band, Mid = band, High = band, LimOn = false };
        double[] Mean(float gain, out double lufsIn, out double lufsOut)
        {
            var l = song[0].Select(v => v * gain).ToArray();
            var r = song[1].Select(v => v * gain).ToArray();
            lufsIn = LoudnessMeter.Measure(l, r, sr);
            var bus = new MixGraphBus(sr);
            bus.SetMaster(bandsOn);
            bus.Reset();
            var (mean, _, _) = RunMaster(bus, l, r);
            lufsOut = LoudnessMeter.Measure(l, r, sr);
            return mean;
        }
        var normal = Mean(1, out double lufsIn, out double lufsOut);
        var quiet = Mean(0.316f, out _, out _);
        bool grOk = true, sameOk = true;
        for (int b = 0; b < MasterBands.Count; b++)
        {
            grOk &= normal[b] is > 0.8 and < 8;
            sameOk &= Math.Abs(normal[b] - quiet[b]) < 1;
        }
        bool levelOk = Math.Abs(lufsOut - lufsIn) < 1.5;
        ok &= grOk && sameOk && levelOk;
        Console.WriteLine($"{"Multibanda 70%: reducción por banda",-36} {string.Join(" / ", normal.Select(v => $"{v:0.0}")),12} {"1 a 8 dB",10}  {(grOk ? "igual" : "DISTINTO")}");
        Console.WriteLine($"{"Multibanda 70%: igual con -10 dB",-36} {string.Join(" / ", quiet.Select(v => $"{v:0.0}")),12} {"± 1 dB",10}  {(sameOk ? "igual" : "DISTINTO")}");
        Console.WriteLine($"{"Multibanda 70%: LUFS entra / sale",-36} {$"{lufsIn:0.0} / {lufsOut:0.0}",12} {"± 1,5",10}  {(levelOk ? "igual" : "DISTINTO")}");

        // limiter off: the export's loudness gain stops where the true peak touches the Techo instead of clipping
        {
            var tone = new float[sr * 2];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 220 * i / sr));
            MixTrackSpec[] mix = [new MixTrackSpec { Clips = [new MixClipSpec { Left = tone, Right = tone, DurationSec = 2 }] }];
            var free = MixMaster.Render(mix, new MixBusSpec { Master = MasterParams.Off with { LimOn = false } }, 2, sr, 0, targetLufs: 0);
            double pkDb = TruePeak.MeasureDb(free.Left, free.Right, free.Left.Length);
            bool offLimOk = pkDb <= MasterLimiter.CeilingDb + 0.01 && pkDb > MasterLimiter.CeilingDb - 0.5;
            ok &= offLimOk;
            Console.WriteLine($"{"Sin limitador: LUFS 0 frena en techo",-36} {$"{pkDb:0.00} dBTP",12} {$"{MasterLimiter.CeilingDb:0.00} dBTP",10}  {(offLimOk ? "igual" : "DISTINTO")}");
        }

        // looks: Cuánto 0 = Natural (Natural's own 0 = nothing at all), 1 = the whole look
        {
            static double[] V(MasterLook l) => [l.LowCutHz, l.MudDb, l.LowsDb, l.PresenceDb, l.AirDb, l.TiltDb, l.Low, l.Mid, l.High, l.Tape, l.Width, l.MonoBassHz, l.Pick];
            static bool Same(MasterLook a, MasterLook b) => V(a).Zip(V(b)).All(z => Math.Abs(z.First - z.Second) < 1e-9);
            var nothing = MasterLooks.Natural with { LowCutHz = 0, MudDb = 0, AirDb = 0, Low = 0, Mid = 0, High = 0 };
            bool looksOk = MasterLooks.All.All(l => Same(MasterLooks.At(l, 1), l)
                && Same(MasterLooks.At(l, 0), l.Id == MasterLooks.Natural.Id ? nothing : MasterLooks.Natural));
            ok &= looksOk;
            Console.WriteLine($"{"Looks: Cuánto 0 = Natural, 1 = look",-36} {(looksOk ? "exacto" : "cambia"),12} {"exacto",10}  {(looksOk ? "igual" : "DISTINTO")}");
        }

        // Púa: off, bit for bit; sustained tones (500 Hz and 3 kHz) pass; pick-like 3 kHz bursts over a bed come down
        {
            float[] Tamed(float[] x, double amount)
            {
                var l = (float[])x.Clone();
                var r = (float[])x.Clone();
                var t = new MasterPickTamer(sr);
                t.Set(amount);
                Run(l, r, t.Process);
                return l;
            }
            var tones = new float[sr * 2];
            for (int i = 0; i < tones.Length; i++) tones[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 500 * i / sr) + 0.2 * Math.Sin(2 * Math.PI * 3000 * i / sr));
            bool pickOffOk = Tamed(tones, 0).AsSpan().SequenceEqual(tones);
            var sustained = Tamed(tones, 0.7);
            double sustainDb = Rms(sustained, sr / 2) - Rms(tones, sr / 2);
            var picks = new float[sr * 2];
            int burst = sr / 200, every = sr / 4;
            for (int i = 0; i < picks.Length; i++)
                picks[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 500 * i / sr) + (i % every < burst ? 0.5 * Math.Sin(2 * Math.PI * 3000 * i / sr) : 0));
            var tamed = Tamed(picks, 0.7);
            // the bursts' energy (what stings): without look-ahead the very first half cycle gets through, the clip and
            // the limiter are there for that
            double eIn = 0, eOut = 0;
            for (int i = sr / 2; i < picks.Length; i++)
                if (i % every < burst)
                {
                    eIn += picks[i] * (double)picks[i];
                    eOut += tamed[i] * (double)tamed[i];
                }
            double burstDb = 10 * Math.Log10(eOut / eIn);
            bool pickOk = pickOffOk && Math.Abs(sustainDb) < 1 && burstDb < -3;
            ok &= pickOk;
            Console.WriteLine($"{"Púa 70%: tonos quietos / púa",-36} {$"{sustainDb:0.0} / {burstDb:0.0} dB",12} {"0 / < -3",10}  {(pickOk ? "igual" : "DISTINTO")}");
        }

        // Sugerime: tones at the octaves balanced like a finished mix ask for no colour; with 250–500 Hz 6 dB up it cuts mud
        {
            MasterSuggestion Hear(double mudBoostDb)
            {
                var x = new float[sr * 4];
                for (int o = 0; o < MasterSuggest.Octaves.Length; o++)
                {
                    double db = MasterSuggest.Reference[o] + (o is 3 or 4 ? mudBoostDb : 0);
                    double amp = 0.05 * Math.Pow(10, db / 20);
                    for (int i = 0; i < x.Length; i++) x[i] += (float)(amp * Math.Sin(2 * Math.PI * MasterSuggest.Octaves[o] * i / sr + o));
                }
                return MasterSuggest.Suggest(MasterSuggest.Analyze(x, x, sr), flat);
            }
            var even = Hear(0).Params;
            var muddy = Hear(6).Params;
            bool sugOk = even.MudDb == 0 && even.LowsDb == 0 && even.PresenceDb == 0 && even.AirDb == 0 && muddy.MudDb <= -1;
            ok &= sugOk;
            Console.WriteLine($"{"Sugerime: pareja / con barro",-36} {$"{even.MudDb:0.0} / {muddy.MudDb:0.0} dB",12} {"0 / <= -1",10}  {(sugOk ? "igual" : "DISTINTO")}");
        }

        return ok;
    }

    static void Run(float[] l, float[] r, SpanAction process, int blockSize = Block)
    {
        for (int i = 0; i < l.Length; i += blockSize)
        {
            int n = Math.Min(blockSize, l.Length - i);
            process(l.AsSpan(i, n), r.AsSpan(i, n));
        }
    }

    delegate void SpanAction(Span<float> l, Span<float> r);

    /// <summary>Plucked notes with harmonics and a bit of pick noise, at a typical guitar level.</summary>
    static float[][] TestSignal(int sr, double seconds)
    {
        int len = (int)(sr * seconds);
        var L = new float[len];
        var R = new float[len];
        double[] notes = [82.41, 110, 146.83, 196, 246.94, 329.63, 220, 164.81];
        var rnd = new Random(7);
        double step = 0.5;
        for (int k = 0; k * step * sr < len; k++)
        {
            double f = notes[k % notes.Length];
            double amp = 0.25 + 0.5 * rnd.NextDouble();
            int at = (int)(k * step * sr);
            for (int i = 0; at + i < len && i < sr * 1.5; i++)
            {
                double t = i / (double)sr;
                double env = Math.Exp(-t * 3);
                double v = 0;
                for (int h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * f * h * t) / h;
                if (i < 200) v += (rnd.NextDouble() * 2 - 1) * (1 - i / 200.0);
                v *= amp * env * 0.4;
                L[at + i] += (float)v;
                R[at + i] += (float)(v * 0.9);
            }
        }
        return [L, R];
    }
}
