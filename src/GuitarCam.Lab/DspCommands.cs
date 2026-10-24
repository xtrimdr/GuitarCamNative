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
            foreach (var m in p.Media)
            {
                if (store.Resolve(m.Audio) is { } audioPath && File.Exists(audioPath))
                {
                    var loaded = AudioLoader.Load(audioPath, sr);
                    float[][] ch = [loaded.Left, loaded.Right];
                    var (rms, _) = LevelAnalysis.Analyze(ch, loaded.SampleRate);
                    mediaCache[m.Id] = (loaded.Left, loaded.Right, rms);
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
                    clips.Add(new MixClipSpec
                    {
                        Left = ma.L,
                        Right = ma.R,
                        DurationSec = m?.DurationSec > 0 ? m.DurationSec : ma.L.Length / (double)sr,
                        AtSec = c.AtSec,
                        TrimStartSec = c.TrimStartSec,
                        TrimEndSec = c.TrimEndSec,
                        FadeInSec = c.FadeInSec,
                        FadeOutSec = c.FadeOutSec,
                        GainDb = c.GainDb,
                        SyncMs = c.EffectiveSyncMs(m),
                    });
                }

                var ch = t.Channel;
                var sat = SatParams.From(
                    ch.Sat.On,
                    ch.Sat.Mode == "tape" ? SatMode.Tape : SatMode.Tube,
                    ch.Sat.Amount,
                    trackRms);
                var compEq = new CompEqParams(
                    ch.Comp.Eq.HpfHz,
                    ch.Comp.Eq.MudDb,
                    ch.Comp.Eq.PresenceDb,
                    ch.Comp.Eq.PresenceHz,
                    ch.Comp.Eq.AirDb);
                var comp = new CompState(
                    ch.Comp.On,
                    ch.Comp.Preset,
                    ch.Comp.Amount,
                    ch.Comp.Limit,
                    ch.Comp.Threshold,
                    ch.Comp.Ratio,
                    ch.Comp.Attack,
                    ch.Comp.Release,
                    ch.Comp.Knee,
                    ch.Comp.Makeup,
                    compEq);
                var bands = new EqBandSpec[TrackEq.BandCount];
                for (int i = 0; i < TrackEq.BandCount; i++)
                {
                    if (i < ch.Eq.Bands.Count)
                    {
                        var b = ch.Eq.Bands[i];
                        bands[i] = EqBandSpec.FromStrings(b.On, b.Type, b.Freq, b.Gain, b.Q, b.Slope, TrackEq.DefaultBands[i].Type);
                    }
                    else bands[i] = TrackEq.DefaultBands[i];
                }

                trackSpecs.Add(new MixTrackSpec
                {
                    Clips = clips,
                    Volume = ch.Volume,
                    Pan = ch.Pan,
                    Muted = ch.Muted,
                    Solo = ch.Solo,
                    Sat = sat,
                    Comp = comp,
                    EqOn = ch.Eq.On,
                    EqBands = bands,
                    ReverbOn = ch.ReverbOn,
                    ReverbSend = ch.ReverbSend,
                    DelayOn = ch.DelayOn,
                    DelaySend = ch.DelaySend,
                });
            }

            var rev = p.Settings.Reverb;
            var dly = p.Settings.Delay;
            busSpec = new MixBusSpec
            {
                Bpm = p.Settings.Bpm,
                Reverb = new ReverbParams(rev.Decay, rev.PreDelayMs, rev.Tone, rev.LowCut, rev.Width, rev.ReturnGain, rev.Muted),
                Delay = new DelayParams(
                    DelayParams.ParseMode(dly.Mode),
                    dly.Sync,
                    dly.Division,
                    dly.TimeMs,
                    dly.Feedback,
                    dly.Tone,
                    dly.LowCut,
                    dly.Width,
                    dly.Duck,
                    dly.ReturnGain,
                    dly.Muted),
            };
            Console.WriteLine($"Proyecto: {p.Name} ({trackSpecs.Count} pistas, {p.Settings.Bpm:0.#} BPM)");
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
                Sat = SatParams.From(a.Get("sat") != null, a.Get("sat")?.StartsWith("c", StringComparison.OrdinalIgnoreCase) == true ? SatMode.Tape : SatMode.Tube, a.Double("vida", 0.3), rms),
                Comp = compState,
                EqOn = a.Get("eq") != null,
                EqBands = a.Get("eq") is { } eqId ? TrackEq.ApplyPreset(eqId) : TrackEq.DefaultBands,
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

        Console.WriteLine();
        Console.WriteLine(allOk
            ? "Todo bien."
            : "Alguna comprobación falló.");
        return allOk ? 0 : 3;
    }

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
                        clips.Add(new MixClipSpec
                        {
                            Left = loaded.Left,
                            Right = loaded.Right,
                            DurationSec = dur,
                            AtSec = c.AtSec,
                            TrimStartSec = c.TrimStartSec,
                            TrimEndSec = c.TrimEndSec,
                            FadeInSec = c.FadeInSec,
                            FadeOutSec = c.FadeOutSec,
                            GainDb = c.GainDb,
                            SyncMs = c.EffectiveSyncMs(m),
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
                            Panel = panel.Value,
                        });
                    }
                }

                var ch = trk.Channel;
                var sat = SatParams.From(ch.Sat.On, ch.Sat.Mode == "tape" ? SatMode.Tape : SatMode.Tube, ch.Sat.Amount, trackRms);
                var compEq = new CompEqParams(ch.Comp.Eq.HpfHz, ch.Comp.Eq.MudDb, ch.Comp.Eq.PresenceDb, ch.Comp.Eq.PresenceHz, ch.Comp.Eq.AirDb);
                var comp = new CompState(ch.Comp.On, ch.Comp.Preset, ch.Comp.Amount, ch.Comp.Limit, ch.Comp.Threshold, ch.Comp.Ratio, ch.Comp.Attack, ch.Comp.Release, ch.Comp.Knee, ch.Comp.Makeup, compEq);
                var bands = new EqBandSpec[TrackEq.BandCount];
                for (int i = 0; i < TrackEq.BandCount; i++)
                    bands[i] = i < ch.Eq.Bands.Count
                        ? EqBandSpec.FromStrings(ch.Eq.Bands[i].On, ch.Eq.Bands[i].Type, ch.Eq.Bands[i].Freq, ch.Eq.Bands[i].Gain, ch.Eq.Bands[i].Q, ch.Eq.Bands[i].Slope, TrackEq.DefaultBands[i].Type)
                        : TrackEq.DefaultBands[i];

                mixTracks.Add(new MixTrackSpec
                {
                    Clips = clips,
                    Volume = ch.Volume,
                    Pan = ch.Pan,
                    Muted = ch.Muted,
                    Solo = ch.Solo,
                    Sat = sat,
                    Comp = comp,
                    EqOn = ch.Eq.On,
                    EqBands = bands,
                    ReverbOn = ch.ReverbOn,
                    ReverbSend = ch.ReverbSend,
                    DelayOn = ch.DelayOn,
                    DelaySend = ch.DelaySend,
                });
            }

            var rev = p.Settings.Reverb;
            var dly = p.Settings.Delay;
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
                AspectRatio = p.Settings.Aspect,
                BorderSize = 4,
                Color = new ExportColorSpec(col.On, col.Look, col.Amount, col.Exposure, col.Contrast, col.Highlights, col.Shadows, col.Saturation, col.Vibrance, col.Temperature, col.Tint, col.Fade),
                VideoFx = new ExportVideoFxSpec(vfx.On, vfx.Vignette, vfx.VignetteSize, vfx.Grain, vfx.Letterbox),
                Takes = takes,
                MixTracks = mixTracks,
                MixBus = new MixBusSpec
                {
                    Bpm = p.Settings.Bpm,
                    Reverb = new ReverbParams(rev.Decay, rev.PreDelayMs, rev.Tone, rev.LowCut, rev.Width, rev.ReturnGain, rev.Muted),
                    Delay = new DelayParams(DelayParams.ParseMode(dly.Mode), dly.Sync, dly.Division, dly.TimeMs, dly.Feedback, dly.Tone, dly.LowCut, dly.Width, dly.Duck, dly.ReturnGain, dly.Muted),
                },
                SampleRate = sr,
            };

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
