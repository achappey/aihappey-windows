const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const core = path.join(root, 'aihappey-desktop/Core/AIHappey.Desktop.Core');
function flatten(value, prefix = '', result = {}) {
  for (const [key, item] of Object.entries(value)) {
    const name = prefix ? prefix + '.' + key : key;
    if (typeof item === 'string') result[name] = item;
    else if (item && typeof item === 'object') flatten(item, name, result);
  }
  return result;
}
const decode = value => value.replace(/&quot;/g, '"').replace(/&gt;/g, '>').replace(/&lt;/g, '<').replace(/&amp;/g, '&');
const key = value => 'ChatForm_' + value.replace(/\./g, '_');
for (const lang of ['en', 'nl']) {
  const browser = path.join(root, 'aihappey-chat/packages/aihappey-i18n/src/locales', lang);
  const labels = flatten(JSON.parse(fs.readFileSync(path.join(browser, 'providers.json'), 'utf8')).anthropic, 'anthropic');
  const xml = fs.readFileSync(path.join(core, 'Strings', lang, 'Resources.resw'), 'utf8');
  const resources = new Map([...xml.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>\s*<\/data>/g)].map(m => [m[1], decode(m[2])]));
  for (const [name, text] of Object.entries(labels))
    assert.equal(resources.get(key(name)), text.replace(/\s*\(\{\{effort\}\}\)/g, ''), `${lang}: native label parity ${name}`);
  const required = ['reasoning', 'reasoningEffort', 'webSearch', 'webFetch', 'maxOutputTokens', 'budget', 'mode', 'other', 'code_execution', 'bash', 'low', 'medium', 'high', 'xhigh', 'max', 'add', 'delete', 'save', 'cancel', 'up', 'down', 'edit', 'inherit', 'enabled'];
  for (const file of fs.readdirSync(core).filter(name => name.startsWith('AnthropicChatSettingsForm') && name.endsWith('.cs'))) {
    const source = fs.readFileSync(path.join(core, file), 'utf8');
    for (const match of source.matchAll(/\bA\("([^"]+)"\)/g)) required.push('anthropic.' + match[1]);
  }
  for (const name of required) assert.ok(resources.get(key(name))?.trim(), `${lang}: missing native form label ${name}`);
  assert.ok(!resources.get(key('anthropic.outputConfig.effort')).includes('{{'), `${lang}: slider header must not expose web interpolation syntax`);
  console.log(`${lang}: ${Object.keys(labels).length} Anthropic labels match the browser; all native provider-form headers exist.`);
}
