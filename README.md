# aihappey-windows

A native Windows AI client built with WinUI 3. Chat with AI models and agents, connect MCP servers, use tools and skills and manage conversations through a native Windows interface.

## Features

- Native WinUI 3 interface with Windows light, dark and high-contrast themes
- Streaming chat with AI models and agents
- Multi-provider model discovery, switching and default preferences
- Offline provider catalog with native cards, favorites, filters, details and provider links
- MCP server discovery, installation and management
- MCP Tools and Resources, including resource templates
- Native MCP form elicitation and human-in-the-loop tool approval
- Skills discovery, native creation/editing, ZIP import/export, activation and resource access
- File attachments, URL inputs and shared PDF/plain-text extraction
- Local conversation history and configurable chat preferences
- Toggleable built-in tools
- Header-based/BYOK and Microsoft Entra authentication
- Local or remote AI and Agents runtimes
- English and Dutch localization

## Architecture

The application uses native Windows components and connects to the existing aihappey runtimes. Provider integrations, inference, agent execution and orchestration remain separate from the desktop client.

- [aihappey-ai](https://github.com/achappey/aihappey-ai) - multi-provider AI runtime
- [aihappey-agents](https://github.com/achappey/aihappey-agents) - agent and workflow runtime

## Built-in local tools

Chat settings → Tools offers three plugins, all off by default. Selections are saved
when the dialog closes; Restore defaults switches them off again.

- **Chat history:** list/get/search/delete conversations in the current account and
  endpoint partition, and read inline PDF/plain-text attachments. Conversation reads
  omit attachment bytes without changing stored files. Search matches all query words
  within one text part. Enabling this plugin permits tool-driven deletion; deleting the
  active chat does not allow the running turn's saves to recreate it.
- **Skill discovery:** keyword-ranked search across available backend, local and
  connected MCP skills, with lazy activation and bundled resource reads. Disabling
  discovery does not disable separately selected skills. MCP skills remain subject to
  the existing connection and capability preferences. Skill files are data, not executed scripts.
- **Artificial Intelligence:** offline provider/country discovery and search of the
  full loaded AI model catalog, including non-language models and their metadata.

These 13 tools use the browser contracts and existing MCP/local execution pipeline;
no additional server, connection or package is required. They are attached to AI-model
chat requests, not agent-service requests. Local names remain stable if MCP servers
offer colliding names; those server tools receive deterministic aliases.

### Document formats and parity checks

The shared [extraction dispatcher](Core/AIHappey.Desktop.Core/DocumentTextExtraction.cs:17)
is used by automatic composer conversion and explicit attachment reads. It currently
supports PDF and text MIME types, plus plain-text filename fallback, with bounded size,
text length and cancellation. Explicit reads do not depend on the automatic-conversion
setting. They never fetch HTTP attachments or perform OCR. Add future formats through
the [extractor interface](Core/AIHappey.Desktop.Core/DocumentTextExtraction.cs:9) and
register their handlers once in the dispatcher.

The [contract sync/check script](Tests/sync-local-tool-contracts.cjs) copies static
browser definitions; its check mode detects name/schema/annotation drift. Dedicated
[runtime checks](Tests/AIHappey.Desktop.Tests/LocalToolRegressionTests.cs) and
[native UI checks](Tests/AIHappey.Desktop.UiTests/LocalToolChecks.cs) cover the plugins,
shared extraction, persistence, isolation, deletion, collisions and turn continuation.

## Related projects

- [aihappey-chat](https://github.com/achappey/aihappey-chat) - browser-based AI client
- [aihappey-mcp](https://github.com/achappey/aihappey-mcp) - MCP backend and integrations
- [aihappey-cli](https://github.com/achappey/aihappey-cli) - command-line client
