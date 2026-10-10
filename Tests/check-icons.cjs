const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..');
const core = path.join(root, 'Core', 'AIHappey.Desktop.Core');
const legacy = /\bnew\s+(?:FontIcon|PathIcon|SymbolIcon)\b|<(?:(?!ic:)[\w]+:)?(?:FontIcon|PathIcon|SymbolIcon)\b|\bSymbol\.[A-Z]|\bGlyph\s*=|Icon="(?:Setting|Add|Send)"|\\uE[0-9a-f]{3}|&#xE[0-9a-f]{3};/i;
const failures = [];
for (const name of fs.readdirSync(core).filter(name => /\.(cs|xaml)$/.test(name))) {
  const lines = fs.readFileSync(path.join(core, name), 'utf8').split(/\r?\n/);
  lines.forEach((line, index) => {
    // Named package icons in XAML are intended; native Icon="Setting" shortcuts are not.
    const checked = line.replace(/<ic:FluentIcon\b[^>]*\/>/g, '');
    if (legacy.test(checked)) failures.push(`${name}:${index + 1}: ${line.trim()}`);
  });
}
for (const output of process.argv.slice(2)) {
  const font = path.resolve(output, 'FluentIcons.WinUI', 'Assets', 'FluentSystemIcons-Size20.otf');
  if (!fs.existsSync(font) || fs.statSync(font).size === 0) failures.push(`Missing published Fluent icon font: ${font}`);
}
if (failures.length) throw new Error(failures.join('\n'));
console.log('Fluent icon source audit and requested published font checks passed.');
