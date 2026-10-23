const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const resources = lang => new Map([...fs.readFileSync(path.join(root, 'Core/AIHappey.Desktop.Core/Strings', lang, 'Resources.resw'), 'utf8')
    .matchAll(/<data name="(Usage[^"]+)"[^>]*>\s*<value>([^<]*)<\/value>/g)].map(m => [m[1], m[2]]));
const en = resources('en'), nl = resources('nl');
assert.deepEqual([...en.keys()].sort(), [...nl.keys()].sort(), 'English and Dutch usage keys must match');
assert(en.size >= 90 && [...en.values(), ...nl.values()].every(v => v.trim()), 'All usage labels must be present');
for (const file of ['Samples/AIHappey.Desktop.HeaderAuth/App.xaml', 'Samples/AIHappey.Desktop.AzureAuth/App.xaml', 'Tests/AIHappey.Desktop.UiTests/App.xaml'])
    assert(fs.readFileSync(path.join(root, file), 'utf8').includes('<XamlChartsResources xmlns="using:Microsoft.UI.Xaml.Controls.Charts" />'), file);
for (const file of ['UsagePage.cs', 'UsageDashboardVisuals.cs']) {
    const source = fs.readFileSync(path.join(root, 'Core/AIHappey.Desktop.Core', file), 'utf8');
    assert(!/WebView2|ChartJs|LiveCharts/.test(source), 'Usage must remain first-party native UI');
    for (const match of source.matchAll(/(?:Text|Kpi|Counts|Bars|Trend)\("([A-Z][A-Za-z]+)"/g)) assert(en.has('Usage' + match[1]), match[1]);
}
console.log(`PASS: ${en.size} usage resource keys in both languages, native chart resources in all hosts.`);
