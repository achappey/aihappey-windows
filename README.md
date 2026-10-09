# aihappey-windows

A native Windows AI client built with WinUI 3. Chat with AI models and agents, connect MCP servers, use tools and skills and manage conversations through a native Windows interface.

## Features

- Native WinUI 3 interface with Windows light, dark and high-contrast themes
- Streaming chat with AI models and agents
- Multi-provider model discovery, switching and default preferences
- MCP server discovery, installation and management
- MCP Tools and Resources, including resource templates
- Native MCP form elicitation and human-in-the-loop tool approval
- Skills discovery, activation and resource access
- File attachments, URL inputs and PDF text extraction
- Local conversation history and configurable chat preferences
- Header-based/BYOK and Microsoft Entra authentication
- Local or remote AI and Agents runtimes
- English and Dutch localization

## Architecture

The application uses native Windows components and connects to the existing aihappey runtimes. Provider integrations, inference, agent execution and orchestration remain separate from the desktop client.

- [aihappey-ai](https://github.com/achappey/aihappey-ai) - multi-provider AI runtime
- [aihappey-agents](https://github.com/achappey/aihappey-agents) - agent and workflow runtime

## Related projects

- [aihappey-chat](https://github.com/achappey/aihappey-chat) - browser-based AI client
- [aihappey-mcp](https://github.com/achappey/aihappey-mcp) - MCP backend and integrations
- [aihappey-cli](https://github.com/achappey/aihappey-cli) - command-line client