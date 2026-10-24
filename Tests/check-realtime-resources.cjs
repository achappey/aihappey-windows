const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const core = path.join(__dirname, '../Core/AIHappey.Desktop.Core');
const resources = language => new Map([...fs.readFileSync(path.join(core, `Strings/${language}/Resources.resw`), 'utf8').matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)].map(m => [m[1], m[2]]));
const en = resources('en'), nl = resources('nl');
for (const file of fs.readdirSync(core).filter(f => /Realtime/.test(f) && f.endsWith('.cs'))) {
  for (const match of fs.readFileSync(path.join(core, file), 'utf8').matchAll(/DesktopResources\.(?:Get|Format)\("([^"]+)"/g)) {
    assert(en.has(match[1]), `${file}: missing English ${match[1]}`);
    assert(nl.has(match[1]), `${file}: missing Dutch ${match[1]}`);
  }
}
for (const [key] of en) if (key.startsWith('Realtime')) assert(nl.has(key), key);
const audio = fs.readFileSync(path.join(core, 'RealtimeAudio.cs'), 'utf8');
assert(audio.includes('.WithCommunicationsMode()') && audio.includes('SetReferenceEndpoint(renderDevice)'), 'Capture must request communications processing with the real playback reference.');
assert(!audio.includes('WithLoopbackCapture(') && !audio.includes('WithRawMode('), 'Microphone input must not be loopback or bypass voice processing.');
assert(audio.includes('GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)'), 'Speech must play through the normal Windows output, not a possibly unused communications device.');
for (const sample of ['HeaderAuth', 'AzureAuth']) assert(fs.readFileSync(path.join(core, `../../Samples/AIHappey.Desktop.${sample}/Package.appxmanifest`), 'utf8').includes('Name="webcam"'), sample);
console.log('Realtime EN/NL resources, native capture configuration, and host camera permissions verified.');
