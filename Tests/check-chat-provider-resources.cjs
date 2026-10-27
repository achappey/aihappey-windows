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
  const browserProviders = JSON.parse(fs.readFileSync(path.join(browser, 'providers.json'), 'utf8'));
  const labels = Object.assign({}, ...['anthropic', 'google', 'mistral'].map(name => flatten(browserProviders[name], name)));
  const xml = fs.readFileSync(path.join(core, 'Strings', lang, 'Resources.resw'), 'utf8');
  const resources = new Map([...xml.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>\s*<\/data>/g)].map(m => [m[1], decode(m[2])]));
  for (const [name, text] of Object.entries(labels))
    assert.equal(resources.get(key(name)), text.replace(/\s*\(\{\{effort\}\}\)/g, ''), `${lang}: native label parity ${name}`);
  const required = ['reasoning', 'reasoningEffort', 'reasoningSummary', 'webSearch', 'webFetch', 'maxOutputTokens', 'budget', 'mode', 'other', 'code_execution', 'image_generation', 'bash', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max', 'none', 'auto', 'add', 'delete', 'save', 'cancel', 'up', 'down', 'edit', 'inherit', 'enabled', 'latitude', 'longitude', 'seed', 'presencePenalty', 'frequencyPenalty', 'mistral.webSearchPremium', 'google.url_context', 'google.googleMaps', 'google.enable_widget', 'openai.file_search', 'openai.vector_store_ids'];
  for (const file of fs.readdirSync(core).filter(name => /^(Anthropic|Google|Mistral)ChatSettingsForm.*\.cs$/.test(name))) {
    const source = fs.readFileSync(path.join(core, file), 'utf8');
    for (const match of source.matchAll(/\bA\("([^"]+)"\)/g)) {
      const name = 'anthropic.' + match[1];
      // optionLabels passes a namespace to Select, not a resource identifier.
      if (/optionLabels:\s*$/.test(source.slice(0, match.index))) {
        const children = Object.keys(labels).filter(key => key.startsWith(name + '.'));
        assert.ok(children.length, `${lang}: missing option-label namespace ${name}`);
        required.push(...children);
      } else required.push(name);
    }
    for (const match of source.matchAll(/\bG\("([^"]+)"\)/g)) required.push('google.' + match[1]);
  }
  // These names are composed from constants and therefore are not found by literal-call scans.
  for (const type of ['web_search', 'image_search', 'enterprise_web_search']) required.push('google.' + type);
  for (const type of ['text', 'audio', 'image', 'video']) required.push('google.interactions.responseFormat.types.' + type);
  for (const type of ['repository', 'gcs', 'inline']) required.push('google.environment.sourceTypes.' + type);
  for (const mode of ['default', 'disabled', 'allowlist']) required.push('google.environment.networkModes.' + mode);
  for (const tier of ['flex', 'standard', 'priority']) required.push('google.serviceTiers.' + tier);
  for (const task of ['text_to_video', 'image_to_video', 'reference_to_video', 'edit', 'extend']) required.push('google.videoConfig.tasks.' + task);
  for (const field of ['mimeType', 'sampleRate', 'bitRate', 'aspectRatio', 'imageSize', 'resolution', 'duration']) required.push('google.interactions.responseFormat.' + field);
  for (const provider of ['parallel', 'exa']) required.push('google.interactions.retrieval.' + provider + '.title');
  required.push('google.interactions.retrieval.apiKey');
  for (const name of required) assert.ok(resources.get(key(name))?.trim(), `${lang}: missing native form label ${name}`);
  assert.ok(!resources.get(key('anthropic.outputConfig.effort')).includes('{{'), `${lang}: slider header must not expose web interpolation syntax`);
  console.log(`${lang}: ${Object.keys(labels).length} Anthropic/Google/Mistral labels match the browser; all native provider-form headers exist.`);
}
