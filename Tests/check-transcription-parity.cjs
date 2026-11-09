// Execute the browser's actual splitter and merger against a fake codec, never real uploads.
// --write intentionally refreshes the checked-in oracle; normal runs are read-only.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const browser = path.resolve(__dirname, '../../aihappey-chat');
const ts = createRequire(path.join(browser, 'package.json'))('typescript');
const base = path.join(browser, 'packages/aihappey-core/src/features/transcriptions');
const MB = 1024 * 1024;
let duration, commands;
class FakeFFmpeg {
  on() {}
  async load() {}
  async writeFile() {}
  async deleteFile() {}
  async ffprobe() {}
  async exec(args) { commands.push(args); }
  async readFile(name) { return name.endsWith('.txt') ? String(duration) : new Uint8Array([1, 2, 3]); }
}
function load(source) {
  const module = { exports: {} };
  const code = ts.transpileModule(source, { compilerOptions: {
    module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022,
  } }).outputText;
  new Function('require', 'module', 'exports', 'window', 'document', 'fetch', 'console', code)(id => {
    if (id === '@ffmpeg/ffmpeg') return { FFmpeg: FakeFFmpeg };
    if (id === '@ffmpeg/util') return { fetchFile: async () => new Uint8Array(), toBlobURL: async x => x };
    throw new Error('Unexpected dependency: ' + id);
  }, module, module.exports, { setTimeout, clearTimeout }, { baseURI: 'https://test.invalid/' },
  async () => ({ ok: true, headers: new Headers({ 'content-type': 'application/wasm' }) }),
  { log() {}, debug() {} });
  return module.exports;
}
const splitterSource = fs.readFileSync(path.join(base, 'transcriptionFileSplit.ts'), 'utf8');
const splitter = load(splitterSource);
const page = fs.readFileSync(path.join(base, 'TranscriptionsPage.tsx'), 'utf8');
const mergeSource = page.slice(page.indexOf('const mergeChunkTranscriptions ='), page.indexOf('export const TranscriptionsPage'));
const { mergeChunkTranscriptions } = load(mergeSource + '\nexport { mergeChunkTranscriptions };');
const clean = value => JSON.parse(JSON.stringify(value));
const inputs = [
  { name: '100mb.wav', size: 100 * MB, duration: 600, maxSize: 15, overlap: 5, enabled: true },
  { name: 'video.mp4', size: 41 * MB, duration: 123.45, maxSize: 7, overlap: 0, enabled: true, type: 'video/mp4' },
  { name: 'short.wav', size: 30 * MB, duration: 2, maxSize: 1, overlap: 60, enabled: true },
  { name: 'long.flac', size: 80 * MB, duration: 3600, maxSize: 25, overlap: 12, enabled: true },
  { name: 'boundary.wav', size: 15 * MB, duration: 60, maxSize: 15, overlap: 5, enabled: true },
  { name: 'above-boundary.wav', size: 15 * MB + 1, duration: 60, maxSize: 15, overlap: 5, enabled: true },
  { name: 'subsecond.wav', size: 16 * MB, duration: 0.5, maxSize: 15, overlap: 5, enabled: true },
  { name: 'disabled.wav', size: 100 * MB, duration: 600, maxSize: 15, overlap: 5, enabled: false },
  { name: 'wide.wav', size: 140 * MB, duration: 1000, maxSize: 100, overlap: 5, enabled: true },
];
(async () => {
  const plans = [];
  for (const input of inputs) {
    duration = input.duration; commands = [];
    const result = await splitter.splitTranscriptionFile({ name: input.name, size: input.size, type: input.type ?? 'audio/wav' },
      { enabled: input.enabled, maxChunkSizeMb: input.maxSize, overlapSeconds: input.overlap });
    plans.push({ input, expected: { split: result.split, durationInSeconds: result.durationInSeconds,
      chunks: result.chunks.map(({ file, ...chunk }) => ({ ...chunk, name: file.name, size: file.size, mediaType: file.type })),
      bitrates: commands.map(args => Number(args[args.indexOf('-b:a') + 1].replace('k', ''))) } });
  }
  const original = { name: 'original.wav', size: 100 * MB };
  const chunks = [1, 0, 2].map(index => ({
    chunk: { index, total: 3, startSecond: index * 10, endSecond: index * 10 + 15,
      file: { name: `original.part-00${index + 1}-of-003.m4a`, size: 3, type: 'audio/mp4' } },
    result: { text: ['  First repeated  ', ' repeated next\n', '   '][index],
      language: index === 0 ? 'nl' : 'en', durationInSeconds: 15,
      segments: [{ text: 'segment ' + index, startSecond: 1.25, endSecond: 2.5, future: index }],
      warnings: [{ type: 'other', message: 'warning ' + index }], future: { last: index },
      request: { body: 'replace', future: 'request ' + index },
      response: { modelId: 'provider/model-' + index, body: { index }, headers: { secret: 'not copied' } } },
  }));
  const expected = clean(mergeChunkTranscriptions(original, chunks, 35));
  delete expected.response.timestamp; expected.request.body = JSON.parse(expected.request.body);
  const fixture = clean({ plans, merge: { original, chunks, duration: 35, expected } });
  const file = path.join(__dirname, 'Fixtures/transcription-parity.json');
  if (process.argv.includes('--write')) fs.writeFileSync(file, JSON.stringify(fixture, null, 2) + '\n');
  else assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), fixture, 'Transcription fixture drifted from browser behavior');
  console.log(`Browser transcription parity: ${plans.length} split plans and ordered text/timestamp/metadata merge.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
