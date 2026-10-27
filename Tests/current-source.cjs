const path = require('node:path');
const fs = require('node:fs');
const { execFileSync } = require('node:child_process');

// Build and execute are one operation. A failed build MUST NOT fall back to an old binary.
function buildAndRun(project, args, execute = execFileSync) {
  project = path.resolve(project);
  const framework = fs.readFileSync(project, 'utf8').match(/<TargetFramework>([^<]+)<\/TargetFramework>/)?.[1];
  if (!framework) throw new Error(`Missing single target framework: ${project}`);
  execute('dotnet', ['build', project, '-c', 'Debug', '-v:quiet', '-clp:ErrorsOnly'],
    { stdio: ['ignore', 2, 2], cwd: path.dirname(project) });
  const binary = path.join(path.dirname(project), 'bin', 'Debug', framework, path.basename(project, '.csproj') + '.dll');
  return execute('dotnet', [binary, ...args], { encoding: 'utf8', cwd: path.dirname(project) });
}
module.exports = { buildAndRun };
