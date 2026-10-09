const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const constants = fs.readFileSync(path.join(root, 'aihappey-chat/packages/aihappey-state/src/slices/defaultAgents.ts'), 'utf8');
const context = {};
for (const match of constants.matchAll(/export const (\w+_AGENT_NAME) = ("[^"]*");/g)) context[match[1]] = JSON.parse(match[2]);
const source = fs.readFileSync(path.join(root, 'aihappey-chat/samples/chathappey/src/defaultAgents.ts'), 'utf8')
    .replace(/import[\s\S]*?from[^;]*;/g, '').replace('export const defaultAgents: Agent[] =', 'result =');
vm.runInNewContext(source, context);
const expected = JSON.parse(JSON.stringify(context.result));
const destination = path.join(root, 'aihappey-desktop/Samples/Shared/default-agents.json');
if (process.argv.includes('--sync')) {
    fs.mkdirSync(path.dirname(destination), { recursive: true });
    fs.writeFileSync(destination, JSON.stringify(expected, null, 2) + '\n');
}
assert.deepEqual(JSON.parse(fs.readFileSync(destination, 'utf8')), expected);
console.log(`Browser/Windows sample parity: ${expected.length} default agents passed.`);
const providerContext = {};
vm.runInNewContext(fs.readFileSync(path.join(root, 'aihappey-chat/packages/aihappey-state/src/slices/defaultProviderMetadata.ts'), 'utf8')
    .replace('export const defaultProviderMetadata =', 'result ='), providerContext);
const providerDefaults = JSON.parse(JSON.stringify(providerContext.result));
const providerDestination = path.join(root, 'aihappey-desktop/Core/AIHappey.Desktop.Core/AgentProviderDefaults.json');
if (process.argv.includes('--sync')) fs.writeFileSync(providerDestination, JSON.stringify(providerDefaults, null, 2) + '\n');
assert.deepEqual(JSON.parse(fs.readFileSync(providerDestination, 'utf8')), providerDefaults);
console.log('Browser/Windows agent provider defaults parity passed.');
