// Compare the production C# projection with the actual web aggregator on the same portable fixture.
// Build the nonvisual test executable first; no network, credentials, or UI required.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const chat = path.resolve(root, '../aihappey-chat');
const ts = require(require.resolve('typescript', { paths: [chat] }));
const source = fs.readFileSync(path.join(chat, 'packages/aihappey-core/src/features/usage/usageAnalytics.ts'), 'utf8');
const exportsObject = {};
vm.runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText,
    { exports: exportsObject, console });
process.env.TZ = 'UTC';
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'Fixtures/usage-conversations.json'), 'utf8'));
const web = exportsObject.buildUsageAnalytics({ conversations: fixture, providers: { openai: { name: 'OpenAI' }, custom: { name: 'Custom provider' } } });
const output = execFileSync('dotnet', ['run', '--no-build', '--project', path.join(__dirname, 'AIHappey.Desktop.Tests/AIHappey.Desktop.Tests.csproj'), '--', '--usage-snapshot'], { encoding: 'utf8', cwd: root });
const native = JSON.parse(output.trim());
const plain = value => JSON.parse(JSON.stringify(value, (_, v) => Object.prototype.toString.call(v) === '[object Map]' ? Object.fromEntries(v) : Object.prototype.toString.call(v) === '[object Set]' ? [...v] : v));
let checks = 0;
const compare = (actual, expected, name) => { assert.deepEqual(plain(actual), plain(expected), name); checks++; };
for (const key of ['conversations', 'messages', 'parts', 'textChars', 'totalCost', 'messagesWithModel', 'messagesWithCost', 'messagesWithTools',
    'messagesWithSources', 'messagesWithReasoning', 'messagesWithDataParts', 'messagesWithStructuredOutput', 'roleCounts', 'toolStateCounts',
    'toolResultTypeCounts', 'contentTypeCounts', 'attachmentTypeCounts', 'sourceTypeCounts', 'dailyMessages', 'dailyTokens', 'dailyCost',
    'hourCounts', 'weekdayHourCounts', 'temperatureCounts', 'finishReasonCounts', 'errorCounts']) compare(native[key], web[key], key);
compare([native.inputTokens, native.outputTokens, native.totalTokens, native.messagesWithUsage],
    [web.tokenTotals.input, web.tokenTotals.output, web.tokenTotals.total, web.tokenTotals.messagesWithUsage], 'token totals');
for (const key of ['modelStats', 'agentStats', 'providerStats']) compare(native[key], web[key], key);
compare(native.toolStats, Object.fromEntries([...web.toolStats].map(([key, value]) => [key, { label: value.label, count: value.count, success: value.success,
    error: value.error, pending: value.pending, resultTypes: value.resultTypes }])), 'tool stats');
const clean = rows => rows.map(row => Object.fromEntries(Object.entries(row).filter(([, v]) => v != null)
    .map(([key, value]) => [key,
        ['timestamp', 'firstTimestamp', 'lastTimestamp'].includes(key) ? new Date(value).toISOString() : value])));
compare(clean(native.messageFacts), clean(web.messageFacts), 'all message facts');
compare(clean(native.conversationFacts), clean(web.conversationFacts), 'all conversation facts');
console.log(`PASS: ${checks} desktop/web usage parity comparisons on shared portable conversations.`);
