const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const core = path.resolve(__dirname, '../Core/AIHappey.Desktop.Core');
const files = fs.readdirSync(core).filter(file => /^(Agent.*|ChatShell\.Agents|OverviewPage)\.cs$/.test(file));
const references = new Map();
function requireKey(key, file) {
    if (!references.has(key)) references.set(key, new Set());
    references.get(key).add(file);
}
for (const file of files) {
    const source = fs.readFileSync(path.join(core, file), 'utf8');
    for (const match of source.matchAll(/DesktopResources\.(?:Get|Format)\("([^"]+)"/g)) requireKey(match[1], file);
    for (const match of source.matchAll(/ChatSettingsFields\.L\("([^"]+)"\)/g)) requireKey('ChatForm_' + match[1].replace(/[.:]/g, '_'), file);
    for (const match of source.matchAll(/(?:fields\.(?:Text|Select|Switch|Integer)|Card)\([^,]+,\s*"(agent\.[^"]+)"(?!\s*\+)/g)) requireKey('ChatForm_' + match[1].replace(/[.:]/g, '_'), file);
}
for (const key of ['AgentEdit', 'AgentCreate', 'ChatForm_agent_readOnlyHint', 'ChatForm_agent_destructiveHint',
    'ChatForm_agent_openWorldHint', 'ChatForm_agent_idempotentHint', 'ChatForm_agent_caller_direct', 'ChatForm_agent_caller_programmatic',
    'ChatForm_agent_tools_all', 'ChatForm_agent_tools_none', 'ChatForm_agent_tools_selected', 'ChatForm_agent_tool_search', 'ChatForm_agent_resource_search']) requireKey(key, 'dynamic agent label');
for (const language of ['en', 'nl']) {
    const xml = fs.readFileSync(path.join(core, 'Strings', language, 'Resources.resw'), 'utf8');
    const keys = [...xml.matchAll(/<data\s+name="([^"]+)"/g)].map(match => match[1]);
    assert.equal(keys.length, new Set(keys).size, `${language}: duplicate resource keys`);
    const missing = [...references].filter(([key]) => !keys.includes(key));
    assert.deepEqual(missing.map(([key, files]) => `${key}: ${[...files].join(', ')}`), [], `${language}: missing agent resources`);
    console.log(`${language}: ${references.size} agent resource keys verified.`);
}
