// Run the browser's real request shaper and settings handlers; no browser runtime ships in desktop.
// --write refreshes the fixture, otherwise this is a read-only drift check.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const browser = path.join(root, 'aihappey-chat');
const requireBrowser = createRequire(path.join(browser, 'package.json'));
const ts = requireBrowser('typescript');
const React = requireBrowser('react');
const base = path.join(browser, 'packages/aihappey-core/src/features');
const theme = new Proxy({}, { get: (_, key) => key });
function load(file) {
  const module = { exports: {} };
  const code = ts.transpileModule(fs.readFileSync(file, 'utf8'), { fileName: file,
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.React } }).outputText;
  new Function('require', 'module', 'exports', code)(id => {
    if (id === 'react') return { ...React, useMemo: fn => fn() };
    if (id === 'aihappey-i18n') return { useTranslation: () => ({ t: key => key }) };
    if (id === 'aihappey-components') return { useTheme: () => theme };
    if (id === '../tools/toolRequestConfig') return shaper;
    throw new Error('Unexpected dependency: ' + id);
  }, module, module.exports);
  return module.exports;
}
const shaper = load(path.join(base, 'tools/toolRequestConfig.ts'));
const { ToolConfigurationTab } = load(path.join(base, 'chat-settings/ToolConfigurationTab.tsx'));
const tool = (name, source, extra = {}) => ({ name, inputSchema: { type: 'object', properties: {} }, source, ...extra });
const inherited = { allowed_callers: ['direct', 'programmatic'], defer_loading: true };
const tools = [
  tool('lookup', { kind: 'mcp', id: 'one', name: 'Shared Service!', requestOptions: inherited, namespace: true }, { title: 'Lookup title', annotations: { title: 'Lower precedence' } }),
  tool('other', { kind: 'mcp', id: 'two', name: 'Shared Service?', description: 'Second source' }, { description: '  Other description  ' }),
  tool('plugin', { kind: 'plugin', id: 'plugin-one', name: 'Shared Service!' }, { parameters: { type: 'object', required: ['id'] }, inputSchema: undefined }),
  tool('local', { kind: 'local', id: 'ignored', name: 'Local tools' }),
  tool('fallback', { kind: 'mcp', id: 'empty', name: '---___!!!' }, { inputSchema: undefined, annotations: { title: 'Annotation title' } }),
  tool('long', { kind: 'mcp', id: 'long', name: 'x'.repeat(90) }),
  tool('long_two', { kind: 'mcp', id: 'long-two', name: 'x'.repeat(91) }),
];
const clean = value => JSON.parse(JSON.stringify(value));
const withoutSource = values => values.map(({ source, ...rest }) => rest);
const shapes = [];
for (const namespaces of [false, true]) for (const [name, config] of Object.entries({
  inherited: {}, override: { lookup: { allowed_callers: ['programmatic'], defer_loading: false }, other: { allowed_callers: ['invalid', 'direct', 'direct'], defer_loading: true } },
  emptyOverride: { lookup: {} }, emptyCallers: { lookup: { allowed_callers: [], defer_loading: false } },
})) {
  shapes.push({ name: name + '/' + namespaces, tools, config, namespaces,
    expected: withoutSource(shaper.shapeToolsForRequest(tools, config, namespaces)) });
}
const flat = tools.map(t => ({ ...t, source: { ...t.source, namespace: false } }));
shapes.push({ name: 'flat-with-existing-options', tools: flat.map(t => ({ ...t, allowed_callers: ['invalid'], defer_loading: true })), config: {}, namespaces: false,
  expected: withoutSource(shaper.shapeToolsForRequest(flat, {}, false)) });
const edits = [];
let config = {};
function descendants(value, result = []) {
  if (Array.isArray(value)) { value.forEach(item => descendants(item, result)); return result; }
  if (!React.isValidElement(value)) return result;
  result.push(value); descendants(value.props.children, result); return result;
}
for (const op of [
  { kind: 'caller', name: 'lookup', value: 'direct' },
  { kind: 'caller', name: 'lookup', value: 'programmatic' },
  { kind: 'deferred', name: 'lookup', value: false },
  { kind: 'caller', name: 'all', value: 'programmatic' },
  { kind: 'caller', name: 'all', value: 'direct' },
  { kind: 'deferred', name: 'all', value: true },
  { kind: 'deferred', name: 'all', value: false },
  { kind: 'caller', name: 'lookup', value: 'direct' },
]) {
  const before = clean(config);
  const elements = descendants(ToolConfigurationTab({ tools, config, useNamespaces: false, onConfigChange: next => config = next, onUseNamespacesChange: () => {} }));
  if (op.kind === 'deferred') elements.find(e => e.type === 'Switch' && e.props.id === (op.name === 'all' ? 'defer-all-tools' : 'defer-tool-' + op.name)).props.onChange(op.value);
  else {
    // Select controls live in the bulk row followed by the sorted tool rows.
    const sorted = [...tools].sort((a, b) => (a.source.namespace || b.source.namespace ? shaper.namespaceNameForTool(a).localeCompare(shaper.namespaceNameForTool(b)) : 0) || a.name.localeCompare(b.name));
    elements.filter(e => e.type === 'Select')[op.name === 'all' ? 0 : 1 + sorted.findIndex(t => t.name === op.name)].props.onChange(op.value);
  }
  edits.push({ op, before, expected: clean(config) });
}
const fixture = clean({ shapes, editTools: tools, edits });
const file = path.join(__dirname, 'Fixtures/tool-configuration-parity.json');
if (process.argv.includes('--write')) fs.writeFileSync(file, JSON.stringify(fixture, null, 2) + '\n');
else assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), fixture, 'Tool configuration fixture drifted from browser behavior');
console.log(`Browser tool configuration parity: ${shapes.length} payloads and ${edits.length} native-edit expectations.`);
