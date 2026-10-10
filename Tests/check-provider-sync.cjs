const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const script = path.join(__dirname, 'sync-provider-catalog.ps1');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'provider-sync-'));
const source = path.join(root, 'source'), destination = path.join(root, 'snapshot');
function invoke(args = [], succeeds = true, src = source) {
  const result = spawnSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, '-Source', src, '-Destination', destination, ...args], { encoding: 'utf8' });
  assert.equal(result.status === 0, succeeds, result.stdout + result.stderr); return result.stdout;
}
function fixture(index = [{ id: 'one', file: 'one.json' }]) { fs.writeFileSync(path.join(source, 'index.json'), JSON.stringify(index)); }
try {
  fs.mkdirSync(source); fixture();
  const metadata = '{"name":"One","urls":{"homepage":"https://one.example"},"future":{"keep":true}}\r\n';
  fs.writeFileSync(path.join(source, 'one.json'), metadata);
  invoke(); assert.equal(fs.readFileSync(path.join(destination, 'one.json'), 'utf8'), metadata);
  const time = fs.statSync(path.join(destination, 'one.json')).mtimeMs;
  invoke(); assert.equal(fs.statSync(path.join(destination, 'one.json')).mtimeMs, time); invoke(['-Check', '-RequireSource']);
  const buildStamp = path.join(root, 'built.sha256');
  fs.copyFileSync(path.join(destination, 'ProviderCatalog.sha256'), buildStamp);
  invoke(['-Check', '-RequireSource', '-BuildStamp', buildStamp]);
  fs.writeFileSync(buildStamp, 'stale-build'); invoke(['-Check', '-RequireSource', '-BuildStamp', buildStamp], false);
  assert.match(invoke([], true, path.join(root, 'missing')), /WARNING.*snapshot/i);
  invoke(['-RequireSource'], false, path.join(root, 'missing'));
  invoke(['-Check', '-RequireSource'], false, path.join(root, 'missing'));
  fs.writeFileSync(path.join(source, 'one.json'), '{"name":"Updated"}'); invoke(['-Check'], false); invoke();
  fixture([{ id: 'two', file: 'two.json' }]); fs.writeFileSync(path.join(source, 'two.json'), '{"name":"Two"}'); invoke();
  assert.equal(fs.existsSync(path.join(destination, 'one.json')), false);
  const previous = fs.readFileSync(path.join(destination, 'index.json'), 'utf8');
  fixture([{ id: 'missing', file: 'missing.json' }]); invoke([], false);
  assert.equal(fs.readFileSync(path.join(destination, 'index.json'), 'utf8'), previous);
  fixture([{ id: 'two', file: 'two.json' }, { id: 'two', file: 'two.json' }]); invoke([], false);
  fixture([{ id: 'escape', file: '../two.json' }]); invoke([], false);
  fixture([{ id: 'two', file: 'two.json' }]); fs.writeFileSync(path.join(source, 'two.json'), '{not json'); invoke([], false);
  fs.writeFileSync(path.join(destination, 'two.json'), '{not json'); invoke([], false, path.join(root, 'missing'));
  console.log('Provider synchronization checks passed: bytes, unchanged writes, drift, deletion, validation, dev fallback and strict publish.');
} finally { fs.rmSync(root, { recursive: true, force: true }); }
