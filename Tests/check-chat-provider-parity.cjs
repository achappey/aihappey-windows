// Execute the actual chat-app normalizers in a test-only TS loader. Nothing from React
// or TypeScript is shipped in the native application. --write refreshes the golden fixture.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '../..');
const chat = path.join(root, 'aihappey-chat');
const chatRequire = createRequire(path.join(chat, 'package.json'));
const ts = chatRequire('typescript');
const React = chatRequire('react');
const providers = path.join(chat, 'packages/aihappey-components/src/forms/providers');
const cache = new Map();
let refs = [], refIndex = 0;
const theme = new Proxy({}, { get: (_, key) => key });
function load(file) {
  file = path.resolve(file);
  if (!path.extname(file)) file = ['.ts', '.tsx'].map(ext => file + ext).find(fs.existsSync);
  if (cache.has(file)) return cache.get(file);
  const source = fs.readFileSync(file, 'utf8');
  const internals = [...source.matchAll(/^const (\w+)\s*=/gm)].map(m => m[1]);
  const output = ts.transpileModule(source + `\nexport const __internals = {${internals.join(',')}};`, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.React }, fileName: file,
  }).outputText;
  const module = { exports: {} }; cache.set(file, module.exports);
  new Function('require', 'module', 'exports', 'React', output)(id => {
    if (id === 'react') return { ...React, useRef: value => { const i = refIndex++; return refs[i] ??= { current: value }; } };
    if (id === 'aihappey-i18n') return { useTranslation: () => ({ t: key => key }) };
    if (id.endsWith('/ThemeContext')) return { useTheme: () => theme };
    if (id.startsWith('.')) return load(path.resolve(path.dirname(file), id));
    throw new Error(`Unexpected browser dependency: ${id}`);
  }, module, module.exports, React);
  return module.exports;
}
const clean = value => JSON.parse(JSON.stringify(value));
const toolConfig = load(path.join(providers, 'anthropic/anthropicToolConfig.ts'));
const deepseek = load(path.join(providers, 'deepseek/DeepSeekChatConfigForm.tsx'));
const form = load(path.join(providers, 'anthropic/AnthropicChatConfigForm.tsx'));
const cardNames = ['Advisor', 'Bash', 'CodeExecution', 'Memory', 'TextEditor', 'WebFetch', 'WebSearch', 'ToolSearchBm25', 'ToolSearchRegex'];
const tools = {};
for (let i = 0; i < cardNames.length; i++) {
  const card = load(path.join(providers, `anthropic/cards/Anthropic${cardNames[i]}Card.tsx`)).__internals;
  const factory = Object.entries(card).find(([key]) => /^createDefault\w+Tool$/.test(key))[1];
  const versions = Object.entries(card).find(([key]) => key.endsWith('_VERSIONS'))?.[1] ?? [factory().type];
  tools[toolConfig.ANTHROPIC_TOOL_TYPES[i]] = { defaults: clean(factory()), versions };
}
const defaults = load(path.join(chat, 'packages/aihappey-state/src/slices/defaultProviderMetadata.ts')).defaultProviderMetadata.anthropic;
const beta = load(path.join(providers, 'anthropic/cards/AnthropicBetaCard.tsx')).__internals.BETA_OPTIONS;
const ctx = load(path.join(providers, 'anthropic/cards/AnthropicContextManagementCard.tsx')).__internals;
const session = load(path.join(providers, 'anthropic/cards/AnthropicSessionCard.tsx')).__internals;
function pathSet(config, op) {
  const keys = op.path.split('/'); let obj = config;
  for (const key of keys.slice(0, -1)) {
    if (!obj[key] || typeof obj[key] !== 'object' || Array.isArray(obj[key])) {
      if (op.remove) return;
      obj[key] = {};
    }
    obj = obj[key];
  }
  if (op.remove) delete obj[keys.at(-1)]; else obj[keys.at(-1)] = structuredClone(op.value);
}
function anthropic(name, initial, ops, initialHeaders = {}) {
  refs = []; let config = structuredClone(initial), headers = structuredClone(initialHeaders);
  const expected = [];
  for (const op of ops) {
    refIndex = 0;
    const tree = form.AnthropicChatConfigForm({ config, headers, updateConfig: val => config = val, updateHeaders: val => headers = val ?? {} });
    const cards = React.Children.toArray(tree.props.children);
    const regular = cards.find(node => node.props?.updateConfig);
    const next = structuredClone(regular.props.config);
    if (op.kind === 'beta') {
      const enabled = String(headers['anthropic-beta'] ?? config['anthropic-beta'] ?? '').split(',').filter(Boolean);
      const values = op.enabled ? [...new Set([...enabled, op.flag])] : enabled.filter(v => v !== op.flag);
      cards.at(-1).props.updateHeaders({ ...headers, 'anthropic-beta': values.join(',') || undefined });
    } else {
      if (op.kind === 'toggle') next[op.section] = op.enabled ? structuredClone(tools[op.section]?.defaults ??
        ({ thinking: { type: 'enabled', budget_tokens: 4096, display: 'summarized' }, output_config: { effort: 'medium' }, compaction: { type: 'summarize' }, cache_control: { type: 'ephemeral' }, container: {} })[op.section]) : undefined;
      else if (op.kind === 'context') next.context_management = op.entries.length ? { ...next.context_management, edits: op.entries.map(ctx.sanitizeContextManagementEdit) } : undefined;
      else pathSet(next, op);
      regular.props.updateConfig(next);
    }
    expected.push(clean({ config, headers }));
  }
  return { name, initial, initialHeaders, ops, expected };
}
const cases = [];
const set = (path, value) => ({ kind: 'set', path, value });
const toggle = (section, enabled) => ({ kind: 'toggle', section, enabled });
for (const section of toolConfig.ANTHROPIC_TOOL_TYPES) cases.push(anthropic(`tool ${section}`, { future: { retained: true }, tools: [{ type: 'custom_tool', x: 1 }] }, [
  toggle(section, true), set(`${section}/future`, 'kept'), toggle(section, false), toggle(section, true),
]));
cases.push(anthropic('all core sections and explicit false', {}, [set('max_tokens', 64000), toggle('cache_control', true), set('cache_control/ttl', '1h'),
  toggle('thinking', true), set('thinking/display', 'updates'), set('thinking/block_binding/prefix_mismatch_behavior', 'error'),
  toggle('output_config', true), set('output_config/task_budget', { type: 'tokens', total: 25000 }),
  toggle('container', true), set('container/id', '  container-id  '), set('container/skills', [{ type: 'custom', skill_id: ' custom ', version: '  v1 ' }, { type: 'anthropic', skill_id: 'pdf', version: 'latest' }]),
  set('budget', { type: 'limit', max_list_cost: { amount: '123456789012345678901', currency: 'USD' } }), set('speed', 'fast'), set('service_tier', 'standard_only'), set('inference_geo', 'us'),
  toggle('web_fetch', true), set('web_fetch/use_cache', false), set('web_fetch/response_inclusion', 'excluded'), set('web_fetch/type', 'web_fetch_20250910'),
  toggle('web_search', true), set('web_search/allowed_domains', null), set('web_search/blocked_domains', null), set('web_search/user_location', null),
]));
const contextEntry = ctx.createContextManagementEdit();
cases.push(anthropic('automatic and manually owned betas', {}, [toggle('advisor', true), toggle('advisor', false),
  { kind: 'beta', flag: 'advisor-tool-2026-03-01', enabled: true }, toggle('advisor', true), toggle('advisor', false),
  { kind: 'context', entries: [contextEntry] }, { kind: 'context', entries: [] },
  { kind: 'beta', flag: 'context-management-2025-06-27', enabled: true }, { kind: 'context', entries: [contextEntry] }, { kind: 'context', entries: [] },
]));
cases.push(anthropic('compaction coupling and context exclusion', {}, [toggle('compaction', true), toggle('compaction', false),
  { kind: 'beta', flag: 'compact-2026-09-04', enabled: false }, { kind: 'beta', flag: 'compact-2026-09-04', enabled: true },
  { kind: 'context', entries: [contextEntry] }, { kind: 'context', entries: [] },
]));
cases.push(anthropic('legacy aliases and headers', { 'anthropic-beta': ['custom-beta', 'custom-beta'], web_search: { type: 'web_search_20250305', name: 'web_search', max_uses: 2 }, future: null }, [set('max_tokens', 1024)]));
cases.push(anthropic('all context editor shapes', {}, [{ kind: 'context', entries: [
  { type: 'clear_tool_uses_20250919', trigger: { type: 'tool_uses', value: 4 }, keep: { type: 'tool_uses', value: 2 }, clear_at_least: { type: 'input_tokens', value: 200 }, clear_tool_inputs: false, exclude_tools: [' x ', ''] },
  { type: 'clear_thinking_20251015', keep: 'all' }, { type: 'clear_thinking_20251015', keep: { type: 'all' } },
  { type: 'clear_thinking_20251015', keep: { type: 'thinking_turns', value: 1 } },
  { type: 'compact_20260112', trigger: { type: 'input_tokens', value: 150000 }, instructions: '  preserve facts  ', pause_after_compaction: false },
] }]));
const deepInputs = [{}, { thinking: { type: 'disabled' }, reasoning_effort: 'max', future: 1 }, { thinking: { type: 'enabled' } },
  { reasoning: { effort: 'low', future: true }, thinking: { type: 'disabled' }, reasoning_effort: 'max' },
  { reasoning: null, thinking: { type: 'enabled' } }, { tools: [{ type: 'web_search', future: true }, { type: 'function', name: 'keep' }] }];
const fixture = clean({ defaults, tools, betas: beta, contextDefaults: ctx.CONTEXT_MANAGEMENT_TYPES.map(ctx.createContextManagementEdit),
  anthropic: cases, deepseek: deepInputs.map(initial => ({ initial, normalized: deepseek.normalizeDeepSeekChatConfig(initial),
    searchOn: deepseek.setDeepSeekWebSearch(initial, true), searchOff: deepseek.setDeepSeekWebSearch(initial, false) })),
  money: ['', '0', '0.00', '0.01', '1', '1.2', '1.23', '01.02', '1.234', '-1', '1e3', ' 2.50 ', '123456789012345678901234567890.99'].map(usd => ({ usd, cents: session.parseUsdAsCents(usd) ?? null })),
});
const file = path.join(__dirname, 'Fixtures/chat-provider-parity.json');
if (process.argv.includes('--write')) fs.writeFileSync(file, JSON.stringify(fixture, null, 2) + '\n');
else assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), fixture, 'Native golden fixture differs from the current chat forms; regenerate and review.');
console.log(`Browser parity fixture verified: ${cases.length} Anthropic sequences, ${fixture.deepseek.length} DeepSeek cases, all tool defaults/versions, betas and exact-money cases.`);
