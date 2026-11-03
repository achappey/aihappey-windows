# aihappey-windows

A native Windows AI client built with WinUI 3. Chat with AI models and agents, connect MCP servers, use tools and skills and manage conversations through a native Windows interface.

![aihappey-windows](https://achappey.github.io/aihappey-chat/images/screenshot_windows.png)

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
- Dictate into the chat composer or speech prompt using Windows' native voice typing (Windows+H)
- Local conversation history and configurable chat preferences
- Toggleable built-in tools
- Header-based/BYOK and Microsoft Entra authentication
- Local or remote AI and Agents runtimes
- English and Dutch localization
- Optional Windows 11 MSIX installation with native Explorer file attachments to the chat draft (never auto-submit)

## Windows distributions

The portable ZIP remains installation-free and independently buildable/publishable without signing credentials. The Windows 11 MSIX distribution adds package-owned Explorer integration, upgrades, and clean uninstallation using the same application codebase. Installed and portable profiles are isolated.

See [Windows installation, publishing, signing, and lifecycle tests](Packaging/README.md) for deployment requirements and CI configuration. Production MSIX publication is blocked until signing and Windows 11 lifecycle validation are configured; portable ZIP publication is not blocked by those requirements.

## Architecture

The application uses native Windows components and connects to the existing aihappey runtimes. Provider integrations, inference, agent execution and orchestration remain separate from the desktop client.

- [aihappey-ai](https://github.com/achappey/aihappey-ai) - multi-provider AI runtime
- [aihappey-agents](https://github.com/achappey/aihappey-agents) - agent and workflow runtime

## Related projects

- [aihappey-chat](https://github.com/achappey/aihappey-chat) - browser-based AI client
- [aihappey-mcp](https://github.com/achappey/aihappey-mcp) - MCP backend and integrations
- [aihappey-cli](https://github.com/achappey/aihappey-cli) - command-line client
