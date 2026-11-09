# Desktop test runners

Run the complete available Windows test inventory from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-all-tests.ps1
```

The runner builds the current nonvisual projects before executing their assemblies, runs every standalone check and read-only synchronization check, runs the packaging source checks, and publishes the current native UI application. Build failures block dependent suites; old binaries are never a fallback. Independent checks continue after failures. A nonzero exit code means a failure, a timeout, or an explicitly reported coverage blocker. UTF-8 logs and machine-readable summaries are retained under the printed artifacts directory.

The native UI runner uses an isolated process for each entry in the UI manifest, exercises both light and dark themes, retains its reports, and requires both a successful process exit and a successful completion marker. It opens test windows using isolated test profiles. Its compatibility switch to skip builds no longer bypasses publication. Unknown suite names fail rather than running a different suite.

## Focused diagnostics

```powershell
dotnet run --project Tests/AIHappey.Desktop.Tests -- --list-suites
dotnet run --project Tests/AIHappey.Desktop.Tests -- --primitive-routing-only
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-ui-tests.ps1 -Suite startup
node Tests/check-runner.cjs
node Tests/check-usage-parity.cjs
```

Focused core suites do not run unrelated prerequisites. The full core run aggregates failures across suites while retaining strict, fail-fast assertions within each suite. The usage snapshot mode produces only its JSON document; the parity checker builds it before execution. Runner regression checks validate failed-build refusal, focused selection, aggregate failure accounting, snapshot isolation, manifest coverage, and rejection of incomplete or failed UI reports.

### MCP catalog and composer discovery

The `mcp-catalog` native UI suite covers installed-server expandable rows, header logos and enable switches, icon-only card footers, Catalogs navigation requests, configuration drafts, and the dedicated searchable catalog modal. It also checks pagination, partial-source retry, installation/removal failures, plugin ownership, configuration handoff/return state, cancellation, and narrow/short windows in both themes. The `context-search` suite covers the matching Model context rows and sequential composer-to-catalog navigation. Both suites use isolated profiles and credential-free fixtures.

Run `powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-ui-tests.ps1 -Suite mcp-catalog` or select `context-search` for composer coverage.

### Model-chat endpoints

The independent core and native UI suites are both named `chat-endpoints`:

```powershell
dotnet run --project Tests/AIHappey.Desktop.Tests -- --chat-endpoints-only
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-ui-tests.ps1 -Suite chat-endpoints
```

The [protocol regression suite](AIHappey.Desktop.Tests/ChatEndpointRegressionTests.cs) uses
deterministic native Responses, Messages, and Chat Completions packets and fake HTTP transports.
It covers gateway/direct routing, credential isolation, native request shapes, text/reasoning/source
parts, signatures/encrypted reasoning, late usage and cost metadata, attachments, incomplete streams,
errors, cancellation, no retries, and portable mixed-endpoint conversation storage/replay.
The [native menu suite](AIHappey.Desktop.UiTests/ChatEndpointChecks.cs) independently checks both
host capabilities and themes, browser-equivalent ordering, checked states, persistence, model/key
changes, busy/Agents gating, and preserving drafts and the current conversation.

#### Native reasoning and tools

All three model-chat protocols share the existing desktop transcript, activity cards, result review,
tool runtime and portable conversation storage in both gateway and supported direct-provider modes.
The standalone protocol projects remain the only external client compile-time dependencies;
gateway/Unified mappers are reference implementations, not desktop dependencies.

| Protocol | Client tools | Provider-executed tools and replay |
| --- | --- | --- |
| Responses | Captured local/MCP functions, Responses namespaces and the existing apply-patch handler | Web/file search, code interpreter, generated images, hosted shell, hosted MCP, hosted tool search, and program execution; separate native call/output items retain their identities and order |
| Messages | Captured local/MCP functions and the existing Anthropic text-editor handler | Server/MCP tool-use blocks and web search/fetch, code execution, bash/text-editor execution, tool-search and MCP results, including typed failures |
| Chat Completions | Indexed/interleaved function calls and legacy function-call streams | Endpoint-native configured function/custom declarations, plus explicit provider-executed/output extensions for display; completed provider calls are not resurrected as client functions in replay |

Signed/redacted Messages thinking, Responses encrypted reasoning and native item identifiers, and
provider-required Completions reasoning fields are retained in portable part metadata. Replaying
an assistant message reconstructs its model/tool steps rather than flattening all rounds together.
Local calls are replayed only with resolved results (including errors and denied result release).
Native provider items are replayed only for their compatible provider/protocol. Request-only output
filtering never edits the original result saved in history. Responses requests disable server storage
and request encrypted reasoning; continuation does not use a previous server response identifier.
Direct requests use provider-local model IDs while saved model selection remains canonical.

Provider-executed tools never enter local execution or local result review. Client tools retain the
desktop's execute-then-review behavior; review is not a new permission-to-execute policy. Unknown
native executable blocks, unsupported client-native harnesses (such as computer/local-shell or
client-executed hosted tool search), malformed arguments, and incomplete transports fail closed.
Custom string-call shapes can be retained, but do not bypass captured handlers' object-input
validation. Provider-hosted containers, files and similar resources still have their provider-defined
lifetimes; portable history does not create or extend those resources. Messages provider pauses use
an explicit continuation signal, not an inference retry, and empty pauses cannot create a busy loop.

The [multi-round regression fixtures](AIHappey.Desktop.Tests/NativeChatToolRegressionTests.cs),
[gateway/direct transport and hosted-tool matrix](AIHappey.Desktop.Tests/NativeChatToolTransportTests.cs),
and [policy/interruption fixtures](AIHappey.Desktop.Tests/NativeChatToolSafetyTests.cs) run as part of
the existing endpoint suite. They verify portable save/reload and cross-endpoint replay, fragmented
arguments/signatures, deduplicated snapshots, execution ownership, credential isolation, namespace
projection, tool limits/stops, denied results and request-only output filtering. The existing
[native activity-card checks](AIHappey.Desktop.UiTests/ToolActivityCardChecks.cs) also reduce native
provider streams into the ordinary cards and verify their logos, status and output actions.
These credential-free fixtures do not claim live provider capability or availability.

### Model-chat tool configuration

Tool configuration is session-only, just like the browser. Closing chat settings commits the isolated draft in memory; Restore defaults clears it, and restarting the app does not reload it. Agent-owned and realtime tool settings are outside this feature. Calling methods and search are raw endpoint options, not an execution/approval policy or a guarantee of provider support.

```powershell
dotnet run --project Tests/AIHappey.Desktop.Tests -- --chat-settings-only
node Tests/check-tool-configuration-parity.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-ui-tests.ps1 -Suite tool-configuration
```

The read-only parity check executes the browser's actual request shaper and settings handlers against the checked-in fixture. Use `node Tests/check-tool-configuration-parity.cjs --write` only when intentionally updating that contract. The native suite covers both themes, multi-select caller flyouts, bulk edits, source defaults, namespace headings, dynamic catalogs, isolated drafts, save failure/reopen/reset, automation labels, keyboard focus, and narrow/short-window scrolling.

### Composer keyboard checks

The `composer-keyboard` suite in the [native UI runner](run-ui-tests.ps1) exercises chat (both authentication hosts), images, video, and speech in both themes using real Windows keyboard input. It checks Enter-to-send, native Shift+Enter insertion and selection replacement, existing multiline drafts, and unavailable sends. Run it in an interactive desktop session with the test window in the foreground and no modifier keys held; the suite refuses to inject keys if those conditions are not met. Avoid typing or switching windows during the run. A failed focus/injection check is not evidence that keyboard behavior passed.

For manual IME coverage, compose text with a Japanese or Chinese input method in each prompt. Enter must confirm the active composition without submitting; a subsequent Enter sends. Also verify multiline text paste and chat attachment paste shortcuts remain native.

### Conversation Activities sizing

The activity and transcript native UI suites include the [Activities sizing checks](AIHappey.Desktop.UiTests/ActivitySizingChecks.cs).
They exercise compact (480 logical units), wider (75% of the available chat workspace, with compact as its minimum),
and full-workspace sizes in both themes and authentication hosts. Widths are capped to the workspace; full size
and layouts without enough room for chat use a native overlay that leaves the left navigation available.
Coverage includes accessible expand/shrink controls and endpoint-disabled states, size retention across close/reopen
and refresh, preserving tab/content instances and scrolling while resizing, explicit close during a layout transition,
window-size breakpoints, navigation collapse/expansion, and an unchanged composer draft and conversation history.
The selected size is shell-local, not a saved preference or conversation property.

## Large-file transcription splitting

AI settings → Transcription enables **Split large transcription files** by default, with a **5-second overlap** and **15 MB** configured chunk budget. The overlap slider accepts 0–60 seconds; the size slider accepts 1–100 MB. Disabling splitting disables both sliders and sends the original media unchanged. Explicit saved choices survive restart; older settings without these fields receive the new defaults.

Picker and drop transcription inputs use file-backed snapshots rather than chat attachment admission. The shared chat attachment limit is unchanged and does not restrict this path. The gateway JSON audio is streamed as base64. For split files, the browser's duration-derived ranges, overlap clamping, request headroom, bitrate target, names, text joining, segment offsets, language, duration, warnings, and split metadata are preserved. The size budget includes the browser's 1 MB JSON allowance and base64 expansion, so the encoded audio is smaller than the configured budget. As in the browser, overlapping transcript text is **not deduplicated**.

The local encoder uses Windows Media Foundation and the Windows media transcoder, not a bundled FFmpeg runtime. Supported formats depend on installed Windows codecs. Windows chooses a supported AAC bitrate from the browser-derived target; encoded bytes are not expected to match FFmpeg. Every encoded chunk must still fit the safe payload allowance. A splitting/codec failure does not fall back to sending the oversized original. Temporary originals and chunks are removed on completion, failure, and cancellation. All successful chunks produce one library item with the unchanged original media; partial chunk results are not saved as completed transcripts. Realtime transcription is unaffected.

Run the following from the desktop repository root:

```powershell
node Tests/check-transcription-parity.cjs
dotnet run --project Tests/AIHappey.Desktop.Tests -- --transcriptions-only
dotnet run --project Tests/AIHappey.Desktop.Tests -- --transcription-codecs-only
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-ui-tests.ps1 -Suite ai-models
```

The parity check executes the actual neighboring browser splitter and merger with a fake codec and compares a checked-in oracle. Its explicit refresh switch is only for intentional browser-contract updates. The deterministic core suite covers thresholds, overlap, ordering, streaming uploads, defaults/reload, disabled mode, a 100 MB source, merged persistence, errors, cancellation, cleanup, and completed-result commits. The Windows codec suite creates real local audio and video, splits a 100 MB WAV using the production encoder, checks duration and payload limits, verifies byte-identical original-media persistence, and tests cancellation and unsupported media. These tests make no live provider requests. The existing AI-model native UI suite covers accessible controls, disabled sliders, draft isolation, save/cancel, and both themes.

## Client/runtime dependency boundary

The [dependency check](check-dependency-boundary.cjs) runs automatically with the standalone checks. It follows desktop project and solution references, permits only the standalone Vercel, Responses, Messages, and Chat Completions protocol projects outside the desktop repository, rejects missing or server-side projects, and guards against Unified/server-mapper imports in desktop source. References used only to publish or launch the bundled runtimes are deliberately allowed.

The audit found no desktop use of Agents Common types or extension methods and no public desktop signatures exposing them. The existing [agent tests](AIHappey.Desktop.Tests/AgentRegressionTests.cs) cover JSON round-trips, nested unknown properties, omitted versus null/empty fields, storage, and complete inline HTTP request payloads. [Chat-error tests](AIHappey.Desktop.Tests/ChatErrorRegressionTests.cs) use the public Vercel wire models, not Unified. The three backend error-mapping assertions now live in the AI repository's backend test project instead of creating a desktop compile-time dependency.

To verify compilation without the Agents source repository, make a fresh sibling layout containing this desktop repository and the separate Vercel, Responses, Messages, and Chat Completions repositories. Do not copy previous build/intermediate directories or runtime binaries. On Windows with the required SDKs and package feeds, run from the desktop repository:

```powershell
dotnet build AIHappey.Desktop.slnx -c Debug
dotnet build Samples/AIHappey.Desktop.HeaderAuth/AIHappey.Desktop.HeaderAuth.csproj -c Debug -p:Platform=x64
dotnet build Samples/AIHappey.Desktop.AzureAuth/AIHappey.Desktop.AzureAuth.csproj -c Debug -p:Platform=x64
node Tests/check-dependency-boundary.cjs
dotnet run --project Tests/AIHappey.Desktop.Tests --no-build -- --agents-only
dotnet run --project Tests/AIHappey.Desktop.Tests --no-build -- --chat-errors-only
dotnet run --project Tests/AIHappey.Desktop.Tests --no-build -- --catalog-only
```

Use the solution's default platform; x64 is selected explicitly for direct host builds. Inspect the restored project graph to confirm that all project references resolve within those two repositories and that Agents/Unified implementation dependencies are absent. Ordinary builds can use the checked-in provider-catalog snapshot without a browser checkout. Full bundle publishing is a separate operation that intentionally builds the runtime services from source; it is not the source-independent client compilation check.

## Coverage limitations

The signed installer lifecycle is deliberately not installed on a development workstation. It requires signed release and upgrade packages, a compiled native Explorer probe, and an explicitly disposable, interactive Windows 11 environment. The consolidated summary records this as blocked, not passed. See the packaging documentation for its guarded lifecycle runner and additional manual deployment evidence.

The optional bundled-runtime integration requires an actual published runtime directory, supplied as the existing positional argument to the core runner. Credential-free protocol and in-memory transport tests do not prove live provider availability, microphone/camera permissions, or real enterprise authentication. These are not claimed as passed by a source audit.

No resource generators run as part of the consolidated checks: synchronization commands use their read-only check mode. The existing build target may synchronize the provider snapshot from the neighboring browser checkout, so review the working tree after a build.
