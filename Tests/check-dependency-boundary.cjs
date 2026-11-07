const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const repo = path.resolve(__dirname, '..');
const vercel = path.resolve(repo, '../aihappey-vercel/AIHappey.Vercel.csproj');
const protocolModels = new Set([vercel, ...['responses/AIHappey.Responses', 'messages/AIHappey.Messages',
    'chatcompletions/AIHappey.ChatCompletions'].map(name => path.resolve(repo, '../aihappey-' + name + '.csproj'))]);
const excluded = new Set(['.git', '.vs', 'bin', 'obj', 'artifacts', 'Releases', 'node_modules']);

function* files(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        if (excluded.has(entry.name)) continue;
        const file = path.join(directory, entry.name);
        if (entry.isDirectory()) yield* files(file);
        else yield file;
    }
}

const visited = new Set();
function checkReferences(file) {
    if (visited.has(file)) return;
    visited.add(file);
    const source = fs.readFileSync(file, 'utf8');
    assert.doesNotMatch(source, /<PackageReference\b[^>]*\bInclude="(?:AgentHappey|Microsoft\.Agents)(?:\.|\")/i,
        `${file}: desktop must not compile against the Agents SDK`);
    for (const match of source.matchAll(/<(?:ProjectReference|Project)\b[^>]*\b(?:Include|Path)="([^"]+\.(?:csproj|slnx))"/gi)) {
        const target = path.resolve(path.dirname(file), match[1].replace(/[\\/]/g, path.sep));
        const local = path.relative(repo, target);
        assert.ok(protocolModels.has(target) || (!local.startsWith('..' + path.sep) && !path.isAbsolute(local)),
            `${file}: unexpected external compile-time dependency ${match[1]}`);
        assert.ok(fs.existsSync(target), `${file}: missing project ${match[1]}`);
        checkReferences(target);
    }
}

for (const file of files(repo)) {
    if (/\.(?:csproj|slnx|props|targets)$/i.test(file)) checkReferences(file);
    if (file.endsWith('.cs')) {
        assert.doesNotMatch(fs.readFileSync(file, 'utf8'),
            /^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?(?:AgentHappey|Microsoft\.Agents|AIHappey\.Unified|AIHappey\.(?:Responses|Vercel)\.Mapping)[.;\s]/m,
            `${file}: desktop protocol tests and client must not use server implementation types`);
    }
}

// Vercel is an intentional model dependency, not a substitute server dependency.
for (const relative of ['Core/AIHappey.Desktop.Core/AIHappey.Desktop.Core.csproj',
    'Tests/AIHappey.Desktop.Tests/AIHappey.Desktop.Tests.csproj', 'AIHappey.Desktop.slnx']) {
    const source = fs.readFileSync(path.join(repo, relative), 'utf8').replace(/\\/g, '/');
    assert.match(source, /aihappey-vercel\/AIHappey\.Vercel\.csproj/, `${relative}: retain the separate Vercel model project`);
}
// Runtime publishing references are deliberately outside this compile-time check.
console.log('Desktop dependency boundary: local projects and standalone protocol models only; no Agents or Unified coupling.');
