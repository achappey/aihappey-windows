// Compatibility entry point. The full JSON snapshot replaces the old partial esbuild export.
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const result = spawnSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
  path.join(__dirname, 'sync-provider-catalog.ps1'), ...(process.argv.includes('--check') ? ['-Check', '-RequireSource'] : [])], { stdio: 'inherit' });
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
