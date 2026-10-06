# aihappey-windows

A native Windows AI client with streaming chat, BYOK/OAuth, provider switching, Agents, Skills, local history and local or remote runtime execution. Connect to the aihappey AI and Agents runtimes through a native WinUI 3 interface.

The desktop client supports:

- Native Windows UI built with WinUI 3
- Streaming chat with models and existing agents
- Provider and model switching through the aihappey AI runtime
- Agents and workflows through the aihappey Agents runtime
- Skills discovery, search and favorites
- Local conversation history
- Shared conversation behavior across models and agents
- Local and remote AI and Agents endpoints
- Header-based authentication for public and self-hosted use
- Entra authentication for enterprise gateways
- Managed standalone Windows runtimes for local execution
- Native Windows light, dark and high-contrast themes
- Responsive desktop layouts and accessibility through Windows UI Automation

The desktop UI reuses the existing aihappey contracts and runtimes. Provider integrations, inference, agent execution and orchestration remain in:

- [aihappey-ai](https://github.com/achappey/aihappey-ai)
- [aihappey-agents](https://github.com/achappey/aihappey-agents)

## Related projects

- [aihappey-ai](https://github.com/achappey/aihappey-ai) - unified multi-provider AI runtime
- [aihappey-agents](https://github.com/achappey/aihappey-agents) - agent and workflow runtime
- [aihappey-chat](https://github.com/achappey/aihappey-chat) - browser-native chat client
- [aihappey-mcp](https://github.com/achappey/aihappey-mcp) - MCP backend and integrations
- [aihappey-cli](https://github.com/achappey/aihappey-cli) - command-line client