// Import native labels from the browser's source of truth. No browser runtime is shipped.
// Run from any directory with: node Tests/sync-chat-settings-resources.cjs
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
const read = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const flatten = (value, prefix = '', result = {}) => {
  for (const [key, item] of Object.entries(value)) {
    const full = prefix ? `${prefix}.${key}` : key;
    if (typeof item === 'string') result[full] = item;
    else if (item && typeof item === 'object') flatten(item, full, result);
  }
  return result;
};
const escape = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const common = ['reasoning', 'reasoningEffort', 'reasoningSummary', 'reasoningContext', 'reasoningMode', 'webSearch', 'searchContextSize', 'country', 'region', 'city', 'timezone', 'image_generation', 'model', 'partial_images', 'quality', 'input_fidelity', 'moderation', 'background', 'size', 'code_execution', 'other', 'parallelToolCalls', 'add', 'delete', 'save', 'cancel', 'enabled', 'toolChoice', 'maxToolCalls', 'stopTools', 'none', 'auto', 'required', 'low', 'medium', 'high', 'xhigh', 'max', 'maxOutputTokens', 'budget', 'mode', 'webFetch', 'providerDefault'];
const custom = {
  en: {
    General: 'General', ChatSettings: 'Chat settings', ChatMaxOutputTokens: 'Maximum output tokens', ChatOptional: 'Optional', ChatRestoreDefaults: 'Restore defaults',
    ChatTokensInvalid: 'Enter a positive whole number, or leave this field empty.', ChatFieldInvalid: 'Enter a valid value. Check the required range or JSON syntax.',
    ChatSettingsInvalid: 'Correct the invalid fields before closing. Your changes have not been saved.', ChatSettingsSaveFailed: 'Chat settings could not be saved. Your draft is still open.',
    ChatToolRequiredHint: 'Required is available when a maximum tool-call count or a tool that ends the turn is configured.',
    ChatNoAvailableTools: 'No client tools are available with the current selections and policy.', ChatToolUnavailable: 'Currently unavailable',
    ChatShellSkillsDeferred: 'Skills catalog selection is not available in this version. Existing native shell skills are preserved.',
    ChatForm_artificialIntelligence: 'Artificial Intelligence', ChatForm_inherit: 'Not specified', ChatForm_on: 'On', ChatForm_off: 'Off', ChatForm_up: 'Up', ChatForm_down: 'Down', ChatForm_edit: 'Edit', ChatForm_bash: 'Bash'
  },
  nl: {
    General: 'Algemeen', ChatSettings: 'Chat instellingen', ChatMaxOutputTokens: 'Maximaal aantal uitvoertokens', ChatOptional: 'Optioneel', ChatRestoreDefaults: 'Standaardwaarden',
    ChatTokensInvalid: 'Voer een positief geheel getal in of laat dit veld leeg.', ChatFieldInvalid: 'Voer een geldige waarde in. Controleer het bereik of de JSON-syntaxis.',
    ChatSettingsInvalid: 'Corrigeer de ongeldige velden voordat u sluit. Uw wijzigingen zijn niet opgeslagen.', ChatSettingsSaveFailed: 'Chatinstellingen konden niet worden opgeslagen. Uw concept blijft geopend.',
    ChatToolRequiredHint: 'Verplicht is beschikbaar wanneer een maximaal aantal tool-aanroepen of een tool die de beurt beëindigt is ingesteld.',
    ChatNoAvailableTools: 'Er zijn geen clienttools beschikbaar met de huidige selecties en het huidige beleid.', ChatToolUnavailable: 'Momenteel niet beschikbaar',
    ChatShellSkillsDeferred: 'Selectie uit de skillcatalogus is nog niet beschikbaar. Bestaande OpenAI-shellskills blijven behouden.',
    ChatForm_artificialIntelligence: 'Artificial Intelligence', ChatForm_inherit: 'Niet opgegeven', ChatForm_on: 'Aan', ChatForm_off: 'Uit', ChatForm_up: 'Omhoog', ChatForm_down: 'Omlaag', ChatForm_edit: 'Bewerken', ChatForm_bash: 'Bash'
  }
};
for (const lang of ['en', 'nl']) {
  const browser = path.join(root, 'aihappey-chat/packages/aihappey-i18n/src/locales', lang);
  const general = read(path.join(browser, `${lang}.json`));
  const providerLabels = read(path.join(browser, 'providers.json'));
  const providers = { ...flatten(providerLabels.openai, 'openai'), ...flatten(providerLabels.anthropic, 'anthropic') };
  const labels = { ...custom[lang] };
  labels.BuiltInLocalTools = general.builtInLocalTools ?? (lang === 'nl' ? 'Ingebouwde lokale tools' : 'Built-in local tools');
  labels.LocalPluginConversations = general.plugins['local-conversations'];
  labels.LocalPluginSkills = general.plugins['skill-search'];
  labels.LocalPluginAi = general.plugins['local-artificial-intelligence'] ?? custom[lang].ChatForm_artificialIntelligence;
  labels.LocalToolFailed = lang === 'nl' ? 'De lokale tool kon niet worden uitgevoerd.' : 'The local tool could not be completed.';
  for (const key of common) {
    if (typeof general[key] !== 'string') throw new Error(`Missing browser label: ${lang}/${key}`);
    labels[`ChatForm_${key}`] = general[key].replace(/\s*\(\{\{reasoningEffort\}\}\)/g, '');
  }
  const generalLabels = flatten(general);
  for (const key of ['mcpPage.policy', 'annotations.openWorld', 'annotations.destructive', 'annotations.readOnly', 'annotations.idempotent']) {
    if (typeof generalLabels[key] !== 'string') throw new Error(`Missing browser label: ${lang}/${key}`);
    labels[`ChatForm_${key.replace(/\./g, '_')}`] = generalLabels[key];
  }
  for (const [key, value] of Object.entries(providers)) labels[`ChatForm_${key.replace(/\./g, '_')}`] = value.replace(/\s*\(\{\{effort\}\}\)/g, '');
  const file = path.join(root, 'aihappey-desktop/Core/AIHappey.Desktop.Core/Strings', lang, 'Resources.resw');
  let xml = fs.readFileSync(file, 'utf8');
  for (const [key, value] of Object.entries(labels)) {
    const item = `  <data name="${key}" xml:space="preserve"><value>${escape(value)}</value></data>`;
    const pattern = new RegExp(`  <data name="${key}"[^>]*>[\\s\\S]*?<\\/data>`);
    xml = pattern.test(xml) ? xml.replace(pattern, () => item) : xml.replace('</root>', `${item}\n</root>`);
  }
  fs.writeFileSync(file, xml);
  console.log(`Imported ${Object.keys(labels).length} ${lang} chat labels.`);
}
