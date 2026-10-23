// Browser copy is the source of truth; --write updates only these native labels.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const write = process.argv.includes('--write');
const escape = text => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const mappings = {
    AgentsDescription: ['agents', 'description'], SkillsDescription: ['skillsPage', 'description'],
    PluginsDescription: ['pluginsPage', 'description'], McpDescription: ['mcpPage', 'description'],
    FilesDescription: ['filesPage', 'description'], TimeToFirstToken: ['timeToFirstToken'], TokensPerSecond: ['tokensPerSecond'],
    'SettingsShowMetrics.Header': ['settingsModal', 'showMetrics'],
    'SettingsShowMetrics.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name': ['settingsModal', 'showMetrics'],
};
for (const language of ['en', 'nl']) {
    const browser = JSON.parse(fs.readFileSync(path.join(root, 'aihappey-chat/packages/aihappey-i18n/src/locales', language, language + '.json'), 'utf8').replace(/^\uFEFF/, ''));
    const labels = Object.fromEntries(Object.entries(mappings).map(([key, parts]) => [key, parts.reduce((value, part) => value?.[part], browser)]));
    Object.assign(labels, language === 'en' ? {
        UsageTitle: 'Usage', OverviewDocumentation: 'Technical documentation', OverviewOfficialWebsite: 'Official website',
        'SettingsShowMetrics.OnContent': 'On', 'SettingsShowMetrics.OffContent': 'Off',
        FilesSharingNote: 'Only references are saved—your files stay where they are. Sharing a folder lets AI read its contents and subfolders and create new files there when the Files plugin is enabled. Existing files are never overwritten. File tools execute before results are reviewed.'
    } : {
        UsageTitle: 'Gebruik', OverviewDocumentation: 'Technische documentatie', OverviewOfficialWebsite: 'Officiële site',
        'SettingsShowMetrics.OnContent': 'Aan', 'SettingsShowMetrics.OffContent': 'Uit',
        FilesSharingNote: 'Alleen verwijzingen worden opgeslagen—je bestanden blijven op hun plek. Met een gedeelde map kan AI de inhoud en submappen lezen en daar nieuwe bestanden maken wanneer de Bestanden-plug-in aanstaat. Bestaande bestanden worden nooit overschreven. Bestandstools worden uitgevoerd voordat de resultaten worden beoordeeld.'
    });
    const file = path.join(root, 'aihappey-desktop/Core/AIHappey.Desktop.Core/Strings', language, 'Resources.resw');
    let xml = fs.readFileSync(file, 'utf8');
    for (const [key, value] of Object.entries(labels)) {
        assert.equal(typeof value, 'string', `Missing browser translation: ${language}/${key}`);
        const escapedKey = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const pattern = new RegExp(`<data name="${escapedKey}"[^>]*>\\s*<value>([\\s\\S]*?)<\\/value>\\s*<\\/data>`, 'g');
        if (write) {
            const item = `<data name="${key}" xml:space="preserve"><value>${escape(value)}</value></data>`;
            xml = pattern.test(xml) ? xml.replace(pattern, () => item) : xml.replace('</root>', `  ${item}\n</root>`);
        }
        const matches = [...xml.matchAll(pattern)];
        assert.equal(matches.length, 1, `Exactly one native label: ${language}/${key}`);
        assert.equal(matches[0][1], escape(value), `Native/browser parity: ${language}/${key}`);
    }
    if (write) fs.writeFileSync(file, xml);
    console.log(`PASS: ${Object.keys(labels).length} ${language} overview/metrics labels match.`);
}
