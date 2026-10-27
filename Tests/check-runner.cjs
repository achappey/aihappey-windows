const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync, spawnSync } = require('node:child_process');
const { buildAndRun } = require('./current-source.cjs');
const project = path.join(__dirname, 'AIHappey.Desktop.Tests/AIHappey.Desktop.Tests.csproj');
let calls = [];
assert.throws(() => buildAndRun(project, ['--usage-snapshot'], (cmd, args) => {
  calls.push(args); throw new Error('fixture build failure');
}), /fixture build failure/);
assert.equal(calls.length, 1, 'failed build must never execute an older binary');
assert.equal(calls[0][0], 'build');
calls = [];
assert.equal(buildAndRun(project, ['--usage-snapshot'], (cmd, args) => {
  calls.push(args); return calls.length === 2 ? 'fixture-output' : '';
}), 'fixture-output');
assert.equal(calls.length, 2);
assert.equal(calls[1].at(-1), '--usage-snapshot');
assert.ok(calls[1][0].endsWith('AIHappey.Desktop.Tests.dll'));
assert.ok(!calls.flat().includes('--no-build'));

// Actual integration: clean JSON, independent selection, and invalid selections fail closed.
const snapshot = buildAndRun(project, ['--usage-snapshot']);
assert.equal(typeof JSON.parse(snapshot).conversations, 'number');
assert.ok(!snapshot.includes('PASS:') && !snapshot.includes('SUITE START:'));
const selected = buildAndRun(project, ['--canvas-only']);
assert.match(selected, /SUITE PASS: canvas/);
assert.equal((selected.match(/SUITE START:/g) ?? []).length, 1);
assert.match(selected, /0 failed/);
const framework = fs.readFileSync(project, 'utf8').match(/<TargetFramework>([^<]+)<\/TargetFramework>/)[1];
const binary = path.join(path.dirname(project), 'bin/Debug', framework, 'AIHappey.Desktop.Tests.dll');
const invalid = spawnSync('dotnet', [binary, '--does-not-exist-only'], { encoding: 'utf8' });
assert.notEqual(invalid.status, 0);
assert.match(invalid.stderr, /Unknown suite/);

const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, 'ui-suites.json'), 'utf8'));
const registry = fs.readFileSync(path.join(__dirname, 'AIHappey.Desktop.UiTests/UiSuites.cs'), 'utf8');
assert.deepEqual([...registry.matchAll(/^\s*\["([^"]+)"\] =/gm)].map(m => m[1]), manifest, 'all UI entries must be in the isolated full run');

const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aihappey-runner-'));
try {
  const report = path.join(directory, 'report.txt');
  const helper = path.join(__dirname, 'RunnerTools.ps1').replaceAll("'", "''");
  const quoted = report.replaceAll("'", "''");
  function validate(text, exit) {
    fs.writeFileSync(report, text);
    return spawnSync('powershell', ['-NoProfile', '-Command', `. '${helper}'; Assert-UiReport -Report '${quoted}' -ExitCode ${exit}`], { encoding: 'utf8' });
  }
  assert.notEqual(validate('PASS: started\n', 0).status, 0, 'partial report is not success');
  assert.notEqual(validate('UI COMPLETE: passed\n', 1).status, 0, 'process failure wins over report text');
  assert.notEqual(validate('UNHANDLED: fixture\nUI COMPLETE: passed\n', 0).status, 0, 'unhandled failures cannot be masked');
  assert.notEqual(validate('SUITE FAIL: fixture\nUI COMPLETE: passed\n', 0).status, 0);
  assert.equal(validate('PASS: fixture\nUI COMPLETE: passed\n', 0).status, 0);
} finally { fs.rmSync(directory, { recursive: true, force: true }); }
console.log('PASS: runner build refusal, snapshot isolation, selection, UI manifest coverage, completion validation, and failure exit codes.');
