// Export only display metadata; importing the browser catalog preserves its icon fallback rules.
const fs = require('node:fs');
const path = require('node:path');
const Module = require('node:module');
const chat = path.resolve(__dirname, '../../aihappey-chat');
const esbuild = require(require.resolve('esbuild', { paths: [chat] }));
const source = path.join(chat, 'packages/aihappey-core/src/runtime/providers/providers.ts');
const built = esbuild.buildSync({ entryPoints: [source], bundle: true, platform: 'node', format: 'cjs', write: false });
const loaded = new Module(source);
loaded._compile(built.outputFiles[0].text, source);
const providers = Object.fromEntries(Object.entries(loaded.exports.PROVIDERS).sort(([a], [b]) => a.localeCompare(b))
    .map(([key, provider]) => [key, { name: provider.name, homepage: provider.urls?.homepage ?? null, icons: provider.icons ?? [] }]));
const destination = path.resolve(__dirname, '../Core/AIHappey.Desktop.Core/ModelProviders.json');
const json = JSON.stringify(providers, null, 2) + '\n';
if (process.argv.includes('--check')) {
    if (!fs.existsSync(destination) || fs.readFileSync(destination, 'utf8') !== json) {
        console.error('Desktop model provider metadata is out of sync. Run node Tests/sync-model-providers.cjs.');
        process.exitCode = 1;
    }
} else {
    fs.writeFileSync(destination, json);
    console.log(`Synced ${Object.keys(providers).length} model providers.`);
}
