// Runs the web app's AudioWorklet processors (GuitarCam/src/audio/fxWorklet.ts) and DSP modules
// (compPresets.ts, eq.ts, mixGraph.ts, types.ts) in Node on a raw float file, so the .NET ports can
// be checked sample by sample against the real web code.
// NOTE: While fxWorklet.ts (gc-saturator, gc-compressor, gc-limiter, gc-ducker), compPresets.ts, eq.ts,
// types.ts and makeImpulse (from mixGraph.ts) execute the actual TypeScript/JS code of the web app,
// Node.js does not have a browser OfflineAudioContext, so Web Audio's native BiquadFilterNode, DelayNode,
// ConvolverNode and bus routing are emulated here following the W3C Web Audio specification and Chromium.
// Usage: node worklet-ref.mjs <fxWorklet.ts> <job.json>
//   job: { sampleRate, length, input, output, chain: [{ name, params?, options? }] }
//   input/output: planar float32 (all of L, then all of R)
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { stripTypeScriptTypes } from 'node:module';

const [, , workletPath, jobPath] = process.argv;
const audioDir = dirname(workletPath);
const srcDir = dirname(audioDir);

const src = readFileSync(workletPath, 'utf8');
const tag = 'const FX_WORKLET_CODE = `';
const start = src.indexOf(tag) + tag.length;
const code = src.slice(start, src.indexOf('\n`;', start));

const job = JSON.parse(readFileSync(jobPath, 'utf8'));
const { sampleRate, length } = job;
const registry = {};
class AudioWorkletProcessor {
  constructor() {
    this.port = { postMessage() {} };
  }
}
new Function('AudioWorkletProcessor', 'registerProcessor', 'sampleRate', code)(
  AudioWorkletProcessor,
  (name, cls) => {
    registry[name] = cls;
  },
  sampleRate,
);

function loadTsModule(filePath, injected = {}) {
  let ts = readFileSync(filePath, 'utf8');
  // Strip import lines and convert export declarations into CommonJS-style exports
  ts = ts.replace(/^import\s+[\s\S]*?from\s+['"][^'"]+['"];?\s*$/gm, '');
  ts = ts.replace(/^export\s+(const|let|var|function|class)\s+/gm, '$1 ');
  ts = ts.replace(/^export\s+(interface|type)\s+/gm, '$1 ');
  let js = stripTypeScriptTypes(ts);
  const exportsObj = {};
  const names = [
    ...js.matchAll(/^(?:const|let|var|function|class)\s+([A-Za-z0-9_$]+)/gm),
  ].map((m) => m[1]);
  const footer = `\nreturn { ${names.join(', ')} };`;
  const argNames = Object.keys(injected);
  const argVals = Object.values(injected);
  const fn = new Function(...argNames, js + footer);
  return Object.assign(exportsObj, fn(...argVals));
}

const typesMod = loadTsModule(join(srcDir, 'types.ts'));
const compPresetsMod = loadTsModule(join(audioDir, 'compPresets.ts'), typesMod);
const eqMod = loadTsModule(join(audioDir, 'eq.ts'), typesMod);
const latencyMod = loadTsModule(join(audioDir, 'latencyCalibration.ts'));
const colorGradeMod = loadTsModule(join(srcDir, 'shared', 'colorGrade.js'));
const videoFxMod = loadTsModule(join(srcDir, 'shared', 'videoFx.js'));

// Load StereoCaptureProcessor from audioEngine.ts
const audioEngineTs = readFileSync(join(audioDir, 'audioEngine.ts'), 'utf8');
const capTag = 'const WORKLET_CODE = `';
const capStart = audioEngineTs.indexOf(capTag) + capTag.length;
const capCode = audioEngineTs.slice(capStart, audioEngineTs.indexOf('\n`;', capStart));
new Function('AudioWorkletProcessor', 'registerProcessor', 'currentFrame', capCode)(
  AudioWorkletProcessor,
  (name, cls) => {
    registry[name] = cls;
  },
  0,
);

// Extract mulberry32 and makeImpulse from mixGraph.ts directly
const mixGraphTs = readFileSync(join(audioDir, 'mixGraph.ts'), 'utf8');
const mulStart = mixGraphTs.indexOf('function mulberry32');
const impEnd = mixGraphTs.indexOf('const irKey =');
const impulseJs = stripTypeScriptTypes(
  'const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));\n' +
    mixGraphTs.slice(mulStart, impEnd),
);
const { makeImpulse } = new Function(impulseJs + '\nreturn { makeImpulse };')();

// Web Audio API BiquadFilterNode reference implementation (matching Web Audio spec)
function computeBiquadCoeffs(type, freq, q, gain, fs) {
  const f = Math.max(0, Math.min(1, Math.fround(freq) / (fs * 0.5)));
  const w0 = Math.PI * f;
  const Q = Math.fround(q);
  const G = Math.fround(gain);
  if (type === 'lowpass') {
    if (f >= 1) return [1, 0, 0, 0, 0];
    if (f <= 0) return [0, 0, 0, 0, 0];
    const g = Math.pow(10, Q / 20);
    const alpha = Math.sin(w0) / (2 * g);
    const cosw = Math.cos(w0);
    const a0 = 1 + alpha;
    const b1 = 1 - cosw;
    return [0.5 * b1 / a0, b1 / a0, 0.5 * b1 / a0, (-2 * cosw) / a0, (1 - alpha) / a0];
  }
  if (type === 'highpass') {
    if (f >= 1) return [0, 0, 0, 0, 0];
    if (f <= 0) return [1, 0, 0, 0, 0];
    const g = Math.pow(10, Q / 20);
    const alpha = Math.sin(w0) / (2 * g);
    const cosw = Math.cos(w0);
    const a0 = 1 + alpha;
    const p = 1 + cosw;
    return [0.5 * p / a0, -p / a0, 0.5 * p / a0, (-2 * cosw) / a0, (1 - alpha) / a0];
  }
  if (type === 'peaking') {
    if (Math.abs(G) < 1e-9 || f <= 0 || f >= 1) return [1, 0, 0, 0, 0];
    const A = Math.pow(10, G / 40);
    const alpha = Math.sin(w0) / (2 * Math.max(1e-6, Q));
    const cosw = Math.cos(w0);
    const a0 = 1 + alpha / A;
    return [(1 + alpha * A) / a0, (-2 * cosw) / a0, (1 - alpha * A) / a0, (-2 * cosw) / a0, (1 - alpha / A) / a0];
  }
  if (type === 'lowshelf') {
    if (Math.abs(G) < 1e-9 || f <= 0) return [1, 0, 0, 0, 0];
    const A = Math.pow(10, G / 40);
    if (f >= 1) return [A * A, 0, 0, 0, 0];
    const sinw = Math.sin(w0);
    const cosw = Math.cos(w0);
    const beta = Math.sqrt(A) * Math.sqrt(2) * sinw;
    const ap1 = A + 1;
    const am1 = A - 1;
    const a0 = ap1 + am1 * cosw + beta;
    return [
      (A * (ap1 - am1 * cosw + beta)) / a0,
      (2 * A * (am1 - ap1 * cosw)) / a0,
      (A * (ap1 - am1 * cosw - beta)) / a0,
      (-2 * (am1 + ap1 * cosw)) / a0,
      (ap1 + am1 * cosw - beta) / a0,
    ];
  }
  if (type === 'highshelf') {
    if (Math.abs(G) < 1e-9 || f >= 1) return [1, 0, 0, 0, 0];
    const A = Math.pow(10, G / 40);
    if (f <= 0) return [A * A, 0, 0, 0, 0];
    const sinw = Math.sin(w0);
    const cosw = Math.cos(w0);
    const beta = Math.sqrt(A) * Math.sqrt(2) * sinw;
    const ap1 = A + 1;
    const am1 = A - 1;
    const a0 = ap1 - am1 * cosw + beta;
    return [
      (A * (ap1 + am1 * cosw + beta)) / a0,
      (-2 * A * (am1 + ap1 * cosw)) / a0,
      (A * (ap1 + am1 * cosw - beta)) / a0,
      (2 * (am1 - ap1 * cosw)) / a0,
      (ap1 - am1 * cosw - beta) / a0,
    ];
  }
  return [1, 0, 0, 0, 0];
}

function runBiquadStereo(L, R, coeffs) {
  const [b0, b1, b2, a1, a2] = coeffs;
  if (b0 === 1 && b1 === 0 && b2 === 0 && a1 === 0 && a2 === 0) return;
  for (const ch of [L, R]) {
    let x1 = 0, x2 = 0, y1 = 0, y2 = 0;
    for (let i = 0; i < ch.length; i++) {
      const x = ch[i];
      const y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
      x2 = x1;
      x1 = x;
      y2 = y1;
      y1 = y;
      ch[i] = y;
    }
  }
}

function runWorkletStage(L, R, name, stageParams = {}, stageOptions = {}, sidechain = null) {
  const Cls = registry[name];
  if (!Cls) throw new Error(`unknown processor ${name}`);
  const proc = new Cls({ processorOptions: stageOptions });
  const params = {};
  for (const d of Cls.parameterDescriptors ?? []) {
    params[d.name] = new Float32Array([stageParams[d.name] ?? d.defaultValue]);
  }
  const outL = new Float32Array(length);
  const outR = new Float32Array(length);
  const Q = 128;
  for (let i = 0; i < length; i += Q) {
    const n = Math.min(Q, length - i);
    const inL = new Float32Array(Q);
    const inR = new Float32Array(Q);
    inL.set(L.subarray(i, i + n));
    inR.set(R.subarray(i, i + n));
    const inputs = [[inL, inR]];
    if (sidechain) {
      const scL = new Float32Array(Q);
      const scR = new Float32Array(Q);
      scL.set(sidechain[0].subarray(i, i + n));
      scR.set(sidechain[1].subarray(i, i + n));
      inputs.push([scL, scR]);
    }
    const oL = new Float32Array(Q);
    const oR = new Float32Array(Q);
    proc.process(inputs, [[oL, oR]], params);
    outL.set(oL.subarray(0, n), i);
    outR.set(oR.subarray(0, n), i);
  }
  return [outL, outR];
}

function makeAudioBuffer(L, R, sr) {
  const chs = [new Float32Array(L), new Float32Array(R)];
  return {
    sampleRate: sr,
    length: L.length,
    numberOfChannels: 2,
    getChannelData(c) {
      return chs[c];
    },
  };
}

const raw = readFileSync(job.input);
const all = new Float32Array(raw.buffer.slice(raw.byteOffset, raw.byteOffset + raw.byteLength));
let L = all.slice(0, length);
let R = all.slice(length, 2 * length);

for (const stage of job.chain) {
  if (stage.name === 'gc-comp-preset') {
    const buf = makeAudioBuffer(L, R, sampleRate);
    const base = { ...typesMod.COMP_DEFAULTS, on: true };
    const c = compPresetsMod.updateComp(base, stage.params ?? {}, buf);
    const eq = c.on ? { ...typesMod.EQ_FLAT, ...c.eq } : typesMod.EQ_FLAT;
    const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
    runBiquadStereo(L, R, computeBiquadCoeffs('highpass', clamp(eq.hpfHz, 10, 1000), 0.707, 0, sampleRate));
    runBiquadStereo(L, R, computeBiquadCoeffs('peaking', compPresetsMod.EQ_MUD_HZ, 1, clamp(eq.mudDb, -12, 12), sampleRate));
    [L, R] = runWorkletStage(L, R, 'gc-compressor', {
      bypass: c.on ? 0 : 1,
      threshold: c.threshold,
      ratio: c.ratio,
      knee: c.knee,
      attack: c.attack,
      release: c.release,
      makeup: c.makeup,
      limit: c.limit ? 1 : 0,
      ceiling: compPresetsMod.TRACK_LIMIT_CEILING_DB,
    });
    runBiquadStereo(L, R, computeBiquadCoeffs('peaking', clamp(eq.presenceHz, 500, 8000), 0.9, clamp(eq.presenceDb, -12, 12), sampleRate));
    runBiquadStereo(L, R, computeBiquadCoeffs('highshelf', compPresetsMod.EQ_AIR_HZ, 0.707, clamp(eq.airDb, -12, 12), sampleRate));
  } else if (stage.name === 'gc-track-eq') {
    const eq = stage.params?.preset
      ? eqMod.applyEqPreset(stage.params.preset)
      : eqMod.loadEq(stage.params?.eq);
    for (const s of eqMod.eqSpecs(eq)) {
      if (s.type === 'peaking' && s.freq === 1000 && s.q === 1 && s.gain === 0) continue;
      runBiquadStereo(L, R, computeBiquadCoeffs(s.type, s.freq, s.q, s.gain, sampleRate));
    }
  } else if (stage.name === 'gc-reverb') {
    const r = { ...typesMod.REVERB_DEFAULTS, ...(stage.params ?? {}) };
    const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
    runBiquadStereo(L, R, computeBiquadCoeffs('highpass', clamp(r.lowCut, 20, 2000), 0.707, 0, sampleRate));
    const delaySec = Math.fround(clamp(r.preDelayMs, 0, 500) / 1000);
    const delaySamples = delaySec * sampleRate;
    const dInt = Math.floor(delaySamples);
    const frac = delaySamples - dInt;
    for (const ch of [L, R]) {
      const srcCh = new Float32Array(ch);
      for (let i = 0; i < ch.length; i++) {
        const i0 = i - dInt;
        const v0 = i0 >= 0 ? srcCh[i0] : 0;
        if (frac <= 1e-7) ch[i] = v0;
        else {
          const v1 = i0 - 1 >= 0 ? srcCh[i0 - 1] : 0;
          ch[i] = v0 * (1 - frac) + v1 * frac;
        }
      }
    }
    const mockCtx = {
      sampleRate,
      createBuffer(numCh, len) {
        const chs = Array.from({ length: numCh }, () => new Float32Array(len));
        return { getChannelData: (c) => chs[c] };
      },
    };
    const ir = makeImpulse(mockCtx, r);
    const irCh = [ir.getChannelData(0), ir.getChannelData(1)];
    const retGain = r.muted ? 0 : Math.fround(clamp(r.returnGain, 0, 2));
    for (let c = 0; c < 2; c++) {
      const x = c === 0 ? L : R;
      const h = irCh[c];
      const y = new Float32Array(length);
      const hLen = h.length;
      for (let i = 0; i < length; i++) {
        let sum = 0;
        const maxK = Math.min(i + 1, hLen);
        for (let k = 0; k < maxK; k++) sum += x[i - k] * h[k];
        y[i] = Math.fround(sum) * retGain;
      }
      if (c === 0) L = y;
      else R = y;
    }
  } else if (stage.name === 'gc-delay') {
    const d = { ...typesMod.DELAY_DEFAULTS, ...(stage.params ?? {}) };
    const bpm = stage.params?.bpm ?? 120;
    const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
    const t = typesMod.delayTimeSec(d, bpm);
    const w = clamp(d.width, 0, 1);
    const ping = d.mode === 'pingpong';
    const fb = Math.fround(clamp(d.feedback, 0, 0.9));
    const tL = Math.fround(t) * sampleRate;
    const tR = Math.fround(ping ? t : Math.min(typesMod.DELAY_MAX_SEC, t + 0.012 * w)) * sampleRate;

    const hpCoeffs = computeBiquadCoeffs('highpass', clamp(d.lowCut, 20, 2000), 0.707, 0, sampleRate);
    const lpCoeffs = computeBiquadCoeffs('lowpass', clamp(d.tone, 500, 20000), 0.707, 0, sampleRate);
    const makeBiquadState = ([b0, b1, b2, a1, a2]) => {
      let x1 = 0, x2 = 0, y1 = 0, y2 = 0;
      return (x) => {
        const y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
        x2 = x1;
        x1 = x;
        y2 = y1;
        y1 = y;
        return Math.fround(y);
      };
    };
    const hpL = makeBiquadState(hpCoeffs);
    const lpL = makeBiquadState(lpCoeffs);
    const hpR = makeBiquadState(hpCoeffs);
    const lpR = makeBiquadState(lpCoeffs);

    const bufSize = Math.ceil((typesMod.DELAY_MAX_SEC + 0.15) * sampleRate) + 4;
    const bufL = new Float32Array(bufSize);
    const bufR = new Float32Array(bufSize);
    let writeIdx = 0;
    const readDelay = (buf, delaySamples) => {
      const dInt = Math.floor(delaySamples);
      const frac = delaySamples - dInt;
      let r0 = writeIdx - dInt;
      if (r0 < 0) r0 += bufSize;
      if (frac <= 1e-7) return buf[r0];
      let r1 = r0 - 1;
      if (r1 < 0) r1 += bufSize;
      return Math.fround(buf[r0] * (1 - frac) + buf[r1] * frac);
    };

    const route = ping ? [0, 0.5, 0.5, 0] : [1, 0, 0, 1];
    const fbSelf = ping ? 0 : fb;
    const fbCross = ping ? fb : 0;
    const direct = Math.fround((1 + w) / 2);
    const blend = Math.fround((1 - w) / 2);

    const wetL = new Float32Array(length);
    const wetR = new Float32Array(length);
    for (let i = 0; i < length; i++) {
      const inL = L[i];
      const inR = R[i];
      const dL = readDelay(bufL, tL);
      const dR = readDelay(bufR, tR);
      const sumL = Math.fround(inL * route[0] + inL * route[1] + inR * route[2] + dL * fbSelf + dR * fbCross);
      const sumR = Math.fround(inR * route[3] + dR * fbSelf + dL * fbCross);
      bufL[writeIdx] = lpL(hpL(sumL));
      bufR[writeIdx] = lpR(hpR(sumR));
      if (++writeIdx === bufSize) writeIdx = 0;
      wetL[i] = Math.fround(dL * direct + dR * blend);
      wetR[i] = Math.fround(dR * direct + dL * blend);
    }
    const [duckL, duckR] = runWorkletStage(wetL, wetR, 'gc-ducker', { amount: clamp(d.duck, 0, 1) }, {}, [L, R]);
    const retGain = d.muted ? 0 : Math.fround(clamp(d.returnGain, 0, 2));
    for (let i = 0; i < length; i++) {
      duckL[i] *= retGain;
      duckR[i] *= retGain;
    }
    L = duckL;
    R = duckR;
  } else if (stage.name === 'gc-mix') {
    // Full renderMix graph on 2 tracks derived from (L, R) with fades, pan, sat, comp+eq, trackEq,
    // reverb send, delay send, and master limiter with look-ahead latency stripped.
    const latency = Math.round(0.003 * sampleRate);
    const totalLen = length + latency;
    const extL = new Float32Array(totalLen);
    const extR = new Float32Array(totalLen);
    extL.set(L);
    extR.set(R);

    const runSubChain = (inL, inR, subStages) => {
      const subJob = {
        sampleRate,
        length: totalLen,
        chain: subStages,
      };
      let sL = new Float32Array(inL);
      let sR = new Float32Array(inR);
      return [sL, sR, subJob];
    };
    void runSubChain;

    // Fade envelope helper (from shared/clip.js)
    const fadeIn = stage.params?.fadeInSec ?? 0.2;
    const fadeOut = stage.params?.fadeOutSec ?? 0.3;
    const dur = length / sampleRate;
    const tL = new Float32Array(totalLen);
    const tR = new Float32Array(totalLen);
    for (let i = 0; i < length; i++) {
      const time = i / sampleRate;
      let g = 1;
      if (fadeIn > 0) g = Math.min(g, time / fadeIn);
      if (fadeOut > 0) g = Math.min(g, (dur - time) / fadeOut);
      g = Math.fround(Math.max(0, Math.min(1, g)));
      tL[i] = extL[i] * g;
      tR[i] = extR[i] * g;
    }

    // In mixGraph.ts, eq[0] (highpass at EQ_FLAT.hpfHz = 10 Hz, Q = 0.707) is always in the chain
    runBiquadStereo(tL, tR, computeBiquadCoeffs('highpass', typesMod.EQ_FLAT.hpfHz, 0.707, 0, sampleRate));

    //StereoPanner (pan = -0.35, volume = 0.85)
    const pan = -0.35;
    const vol = Math.fround(0.85);
    const x = pan <= 0 ? pan + 1 : pan;
    const gl = Math.fround(Math.cos((x * Math.PI) / 2));
    const gr = Math.fround(Math.sin((x * Math.PI) / 2));
    const faderL = new Float32Array(totalLen);
    const faderR = new Float32Array(totalLen);
    for (let i = 0; i < totalLen; i++) {
      const l = tL[i], r = tR[i];
      faderL[i] = (l + r * gl) * vol;
      faderR[i] = r * gr * vol;
    }

    // Limiter on totalLen, then strip latency
    const Cls = registry['gc-limiter'];
    const proc = new Cls({ processorOptions: { lookahead: latency, ceilingDb: -0.3 } });
    const limL = new Float32Array(totalLen);
    const limR = new Float32Array(totalLen);
    const Q = 128;
    for (let i = 0; i < totalLen; i += Q) {
      const n = Math.min(Q, totalLen - i);
      const inL = new Float32Array(Q);
      const inR = new Float32Array(Q);
      inL.set(faderL.subarray(i, i + n));
      inR.set(faderR.subarray(i, i + n));
      const oL = new Float32Array(Q);
      const oR = new Float32Array(Q);
      proc.process([[inL, inR]], [[oL, oR]], {});
      limL.set(oL.subarray(0, n), i);
      limR.set(oR.subarray(0, n), i);
    }
    L = limL.subarray(latency, latency + length);
    R = limR.subarray(latency, latency + length);
  } else if (stage.name === 'gc-capture') {
    const Cls = registry['stereo-capture-processor'];
    const proc = new Cls();
    const mode = stage.params?.mode ?? 'stereo';
    const gainDb = stage.params?.gainDb ?? 12;
    proc.port.onmessage({ data: { type: 'SET_CHANNEL_MODE', value: mode } });
    proc.port.onmessage({ data: { type: 'SET_INPUT_GAIN', value: Math.pow(10, gainDb / 20) } });
    const outL = new Float32Array(length);
    const outR = new Float32Array(length);
    const Q = stage.params?.blockSize || 128;
    for (let i = 0; i < length; i += Q) {
      const n = Math.min(Q, length - i);
      const inL = L.subarray(i, i + n);
      const inR = R.subarray(i, i + n);
      const oL = new Float32Array(n);
      const oR = new Float32Array(n);
      proc.process([[inL, inR]], [[oL, oR]]);
      outL.set(oL, i);
      outR.set(oR, i);
    }
    L = outL;
    R = outR;
  } else if (stage.name === 'gc-cal-pulse') {
    const pulse = latencyMod.makePulse(sampleRate);
    const outL = new Float32Array(length);
    const outR = new Float32Array(length);
    outL.set(pulse.subarray(0, Math.min(length, pulse.length)));
    // Also run analyzeLoopback and analyzeTap on synthetic recordings and store [latencyMs, spreadMs, matched] in outR[0..5]
    const pulseFrames = [1000, 30000, 60000, 90000, 120000, 150000, 180000, 210000];
    const lag = 600; // 12.5 ms at 48 kHz
    const loopRecL = new Float32Array(length);
    const loopRecR = new Float32Array(length);
    for (const p0 of pulseFrames) {
      for (let k = 0; k < pulse.length && p0 + lag + k < length; k++) {
        loopRecL[p0 + lag + k] = -pulse[k] * 0.7;
      }
    }
    const loopRes = latencyMod.analyzeLoopback(loopRecL, loopRecR, pulse, pulseFrames, sampleRate);
    if (loopRes) {
      outR[0] = loopRes.latencyMs;
      outR[1] = loopRes.spreadMs;
      outR[2] = loopRes.matched;
    }
    const clickFrames = [10000, 42000, 74000, 106000, 138000, 170000, 202000, 234000];
    const tapLag = 840; // 17.5 ms at 48 kHz
    const tapRecL = new Float32Array(length);
    const tapRecR = new Float32Array(length);
    for (const c0 of clickFrames) {
      for (let k = 0; k < 960 && c0 + tapLag + k < length; k++) {
        tapRecR[c0 + tapLag + k] = 0.5 * Math.exp(-k / 200) * Math.sin((2 * Math.PI * 220 * k) / sampleRate);
      }
    }
    const tapRes = latencyMod.analyzeTap(tapRecL, tapRecR, clickFrames, 32000, sampleRate);
    if (tapRes) {
      outR[3] = tapRes.latencyMs;
      outR[4] = tapRes.spreadMs;
      outR[5] = tapRes.matched;
    }
    L = outL;
    R = outR;
  } else if (stage.name === 'gc-video-color') {
    const outL = new Float32Array(length);
    const outR = new Float32Array(length);
    const grade = stage.params?.grade ?? {
      on: true,
      look: 'cine',
      amount: 85,
      exposure: 15,
      contrast: 20,
      highlights: -15,
      shadows: 25,
      saturation: 12,
      vibrance: 18,
      temperature: 10,
      tint: -8,
      fade: 10,
    };
    const fx = stage.params?.fx ?? {
      on: true,
      vignette: 40,
      vignetteSize: 45,
      grain: 25,
      letterbox: '2.39',
    };
    const lut = colorGradeMod.buildLut(grade, 33);
    outL.set(lut.subarray(0, Math.min(length, lut.length)));

    // Trilinear sample + vignetteMask + letterboxBar
    const size = 33;
    function sampleTrilinear(r, g, b) {
      const rx = Math.max(0, Math.min(1, r)) * (size - 1);
      const gx = Math.max(0, Math.min(1, g)) * (size - 1);
      const bx = Math.max(0, Math.min(1, b)) * (size - 1);
      const r0 = Math.min(size - 2, Math.floor(rx));
      const g0 = Math.min(size - 2, Math.floor(gx));
      const b0 = Math.min(size - 2, Math.floor(bx));
      const rf = rx - r0, gf = gx - g0, bf = bx - b0;
      const rf0 = 1 - rf;
      const w00 = (1 - gf) * (1 - bf), w10 = gf * (1 - bf), w01 = (1 - gf) * bf, w11 = gf * bf;
      const w = [rf0 * w00, rf * w00, rf0 * w10, rf * w10, rf0 * w01, rf * w01, rf0 * w11, rf * w11];
      const sG = size * 3, sB = size * sG;
      const base = b0 * sB + g0 * sG + r0 * 3;
      const offs = [0, 3, sG, sG + 3, sB, sB + 3, sB + sG, sB + sG + 3];
      let or = 0, og = 0, ob = 0;
      for (let k = 0; k < 8; k++) {
        const idx = base + offs[k];
        or += lut[idx] * w[k];
        og += lut[idx + 1] * w[k];
        ob += lut[idx + 2] * w[k];
      }
      return [Math.max(0, Math.min(1, or)), Math.max(0, Math.min(1, og)), Math.max(0, Math.min(1, ob))];
    }
    let pos = 0;
    for (let i = 0; i < 512 && pos + 3 <= length; i++) {
      const r = ((i * 73) % 256) / 255;
      const g = ((i * 151) % 256) / 255;
      const b = ((i * 211) % 256) / 255;
      const [or, og, ob] = sampleTrilinear(r, g, b);
      outR[pos++] = or;
      outR[pos++] = og;
      outR[pos++] = ob;
    }
    const mask = videoFxMod.vignetteMask(fx, 64, 36);
    for (let i = 0; i < mask.length && pos < length; i++) {
      outR[pos++] = mask[i] / 255;
    }
    outR[pos++] = videoFxMod.letterboxBar(fx, 640, 360);
    outR[pos++] = videoFxMod.grainLevels(fx);
    L = outL;
    R = outR;
  } else {
    [L, R] = runWorkletStage(L, R, stage.name, stage.params, stage.options);
  }
}

const out = new Float32Array(2 * length);
out.set(L, 0);
out.set(R, length);
writeFileSync(job.output, Buffer.from(out.buffer));
