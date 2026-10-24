const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const core = path.join(__dirname, '../Core/AIHappey.Desktop.Core');
const formDeclarations = fs.readdirSync(core).filter(file => file.endsWith('.cs')).flatMap(file =>
  [...fs.readFileSync(path.join(core, file), 'utf8').matchAll(/\bclass\s+RealtimeSettingsForm\b/g)].map(() => file));
assert.deepEqual(formDeclarations, ['RealtimeSettingsForm.cs'], 'Compile exactly one realtime settings form; remove obsolete copies rather than defining the form twice.');
const resources = language => new Map([...fs.readFileSync(path.join(core, `Strings/${language}/Resources.resw`), 'utf8').matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)].map(m => [m[1], m[2]]));
const en = resources('en'), nl = resources('nl');
for (const file of fs.readdirSync(core).filter(f => /Realtime/.test(f) && f.endsWith('.cs'))) {
  for (const match of fs.readFileSync(path.join(core, file), 'utf8').matchAll(/DesktopResources\.(?:Get|Format)\("([^"]+)"/g)) {
    assert(en.has(match[1]), `${file}: missing English ${match[1]}`);
    assert(nl.has(match[1]), `${file}: missing Dutch ${match[1]}`);
  }
}
for (const [key] of en) if (key.startsWith('Realtime')) assert(nl.has(key), key);
const form = fs.readFileSync(path.join(core, 'RealtimeSettingsForm.cs'), 'utf8');
assert(form.includes('public void RestoreDefaults()'), 'The current realtime form must support the shared settings restore-defaults action.');
assert(fs.readFileSync(path.join(core, 'ChatSettingsDialog.cs'), 'utf8').includes('realtimeForm?.RestoreDefaults()'), 'Chat Settings must retain the current realtime restore-defaults integration.');
for (const [, section] of form.matchAll(/Section\("([^"]+)"\)/g)) {
  for (const [language, strings] of [['English', en], ['Dutch', nl]])
    assert(strings.has('RealtimeSection' + section), `Missing ${language} realtime section ${section}`);
}
const audio = fs.readFileSync(path.join(core, 'RealtimeAudio.cs'), 'utf8');
assert(audio.includes('.WithCommunicationsMode()') && audio.includes('SetReferenceEndpoint(renderDevice)'), 'Capture must request communications processing with the real playback reference.');
assert(!audio.includes('WithLoopbackCapture(') && !audio.includes('WithRawMode('), 'Microphone input must not be loopback or bypass voice processing.');
assert(audio.includes('GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)'), 'Speech must play through the normal Windows output, not a possibly unused communications device.');
for (const sample of ['HeaderAuth', 'AzureAuth']) assert(fs.readFileSync(path.join(core, `../../Samples/AIHappey.Desktop.${sample}/Package.appxmanifest`), 'utf8').includes('Name="webcam"'), sample);
console.log('Single realtime form definition, settings integration, EN/NL resources, native capture configuration, and host camera permissions verified.');
