// Validate resource references before publishing: a missing MRT key can crash shell construction.
const fs = require('node:fs');
const path = require('node:path');
const core = path.join(__dirname, '../Core/AIHappey.Desktop.Core');
const sources = fs.readdirSync(core).filter(file => file.includes('Video') && file.endsWith('.cs'));
let failures = 0;
for (const language of ['en', 'nl']) {
  const text = fs.readFileSync(path.join(core, 'Strings', language, 'Resources.resw'), 'utf8');
  const keys = new Set([...text.matchAll(/<data name="([^"]+)"/g)].map(match => match[1]));
  const required = new Set();
  for (const file of sources) {
    const source = fs.readFileSync(path.join(core, file), 'utf8');
    for (const call of source.matchAll(/DesktopResources\.Get\(([^)]+)\)/g))
      for (const literal of call[1].matchAll(/"([^"]+)"/g)) required.add(literal[1]);
    // Label keys passed through the settings field/card helpers.
    for (const call of source.matchAll(/(?:Field|Card|Dimension)\([^;\n]+/g))
      for (const literal of call[0].matchAll(/"((?:Video|Image|General)[^"]*)"/g))
        if (!/(?:Card|Preset)$/.test(literal[1])) required.add(literal[1]);
  }
  // Control names can differ from their label keys; these are not MRT lookups.
  for (const name of ['VideoAspectRatio', 'VideoSeed']) required.delete(name);
  for (const key of required) {
    if (!keys.has(key)) { console.error(`${language}: missing video resource ${key}`); failures++; }
  }
  if (!failures) console.log(`${language}: all ${required.size} video resource keys exist`);
}
process.exitCode = failures ? 1 : 0;
