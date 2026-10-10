// Extract the data-only browser contracts without running React or its tool runtimes.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../..');
const tools = path.join(root, 'aihappey-chat/packages/aihappey-core/src/features/tools/toolcalls');
function staticTools(file) {
  const source = fs.readFileSync(path.join(tools, file), 'utf8');
  return [...source.matchAll(/export const \w+: Tool = (\{[\s\S]*?\n\});/g)]
    .map(m => vm.runInNewContext('(' + m[1] + ')', { INFERENCE_REGION_OPTIONS: ['World', 'Europe', 'Americas', 'Asia', 'Africa', 'Oceania'] }));
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
};
const output = path.join(root, 'aihappey-desktop/Core/AIHappey.Desktop.Core/LocalToolDefinitions.json');
const json = JSON.stringify(contracts, null, 2) + '\n';
if (process.argv.includes('--check')) {
  if (fs.readFileSync(output, 'utf8').replace(/\r\n/g, '\n') !== json) throw new Error('Local tool contracts differ from browser; run sync-local-tool-contracts.cjs');
  console.log('PASS: all 13 local tool contracts match browser definitions');
} else { fs.writeFileSync(output, json); console.log('Synced 13 browser local tool contracts.'); }
