// Runs the web app's AudioWorklet processors (GuitarCam/src/audio/fxWorklet.ts) in Node on a raw
// float file, so the .NET ports can be checked sample by sample against the real code.
// Usage: node worklet-ref.mjs <fxWorklet.ts> <job.json>
//   job: { sampleRate, length, input, output, chain: [{ name, params?, options? }] }
//   input/output: planar float32 (all of L, then all of R)
import { readFileSync, writeFileSync } from 'node:fs';

const [, , workletPath, jobPath] = process.argv;
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

const raw = readFileSync(job.input);
const all = new Float32Array(raw.buffer.slice(raw.byteOffset, raw.byteOffset + raw.byteLength));
let L = all.slice(0, length);
let R = all.slice(length, 2 * length);

const Q = 128;
for (const stage of job.chain) {
  const Cls = registry[stage.name];
  if (!Cls) throw new Error(`unknown processor ${stage.name}`);
  const proc = new Cls({ processorOptions: stage.options ?? {} });
  const params = {};
  for (const d of Cls.parameterDescriptors ?? []) params[d.name] = new Float32Array([stage.params?.[d.name] ?? d.defaultValue]);
  const outL = new Float32Array(length);
  const outR = new Float32Array(length);
  for (let i = 0; i < length; i += Q) {
    const n = Math.min(Q, length - i);
    const inL = new Float32Array(Q);
    const inR = new Float32Array(Q);
    inL.set(L.subarray(i, i + n));
    inR.set(R.subarray(i, i + n));
    const oL = new Float32Array(Q);
    const oR = new Float32Array(Q);
    proc.process([[inL, inR]], [[oL, oR]], params);
    outL.set(oL.subarray(0, n), i);
    outR.set(oR.subarray(0, n), i);
  }
  L = outL;
  R = outR;
}

const out = new Float32Array(2 * length);
out.set(L, 0);
out.set(R, length);
writeFileSync(job.output, Buffer.from(out.buffer));
