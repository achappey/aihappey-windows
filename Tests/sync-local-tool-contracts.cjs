// Extract the data-only browser contracts without running React or its tool runtimes.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../..');
const tools = path.join(root, 'aihappey-chat/packages/aihappey-core/src/features/tools/toolcalls');
function staticTools(file, constants = []) {
  const source = fs.readFileSync(path.join(tools, file), 'utf8');
  const context = { INFERENCE_REGION_OPTIONS: ['World', 'Europe', 'Americas', 'Asia', 'Africa', 'Oceania'] };
  for (const name of constants) {
    const body = source.match(new RegExp('const ' + name + ' = ([\\s\\S]*?);\\r?\\n'))?.[1];
    if (!body) throw new Error('Missing static contract constant: ' + name);
    context[name] = vm.runInNewContext('(' + body.replace(/ as const/g, '') + ')', context);
  }
  return [...source.matchAll(/export const \w+: Tool = (\{[\s\S]*?\n\});/g)]
    .map(m => vm.runInNewContext('(' + m[1] + ')', context));
}
const skills = fs.readFileSync(path.join(tools, 'useSkillToolCall.ts'), 'utf8');
function skillTool(name) {
  const start = skills.indexOf('export function ' + name + '(');
  const body = skills.slice(start).match(/return (\{[\s\S]*?\n  \});/)[1];
  return vm.runInNewContext('(' + body + ')');
}
const contracts = {
  'local-conversations': staticTools('useLocalConversationsToolCall.ts'),
  'skill-search': ['buildSearchSkillsTool', 'buildActivateSkillTool', 'buildReadSkillResourceTool'].map(skillTool),
  'local-artificial-intelligence': staticTools('useLocalArtificialIntelligenceToolCall.ts'),
  'local-images': [...staticTools('useLocalImagesToolCall.ts'), ...staticTools('localImages.ts', ['imageReferenceSchema', 'readAnnotations'])],
  'local-agents-editor': staticTools('useLocalAgentsToolCall.ts').filter(t => t.name !== 'local_agents_run'),
  'local-agents-runtime': staticTools('useLocalAgentsToolCall.ts').filter(t => t.name === 'local_agents_run'),
  'local-skill-editor': staticTools('useLocalSkillEditorToolCall.ts', ['skillIdSchema']),
};
const output = path.join(root, 'aihappey-desktop/Core/AIHappey.Desktop.Core/LocalToolDefinitions.json');
const json = JSON.stringify(contracts, null, 2) + '\n';
const count = Object.values(contracts).flat().length;
if (process.argv.includes('--check')) {
  if (fs.readFileSync(output, 'utf8').replace(/\r\n/g, '\n') !== json) throw new Error('Local tool contracts differ from browser; run sync-local-tool-contracts.cjs');
  console.log(`PASS: all ${count} local tool contracts match browser definitions`);
} else { fs.writeFileSync(output, json); console.log(`Synced ${count} browser local tool contracts.`); }
