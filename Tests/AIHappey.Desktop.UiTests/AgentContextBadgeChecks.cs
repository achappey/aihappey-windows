using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckAgentContextBadgesAsync(bool local, ElementTheme theme)
    {
        var context = $"Agent context badges / {(local ? "Local" : "Remote")} / {theme}";
        var session = new DesktopSession(new UiHost(local), new UiRuntime(), new()) { McpClientFactory = new BadgeMcpFactory() };
        var shell = new ChatShell(session) { RequestedTheme = theme }; window!.Content = shell; window.Activate();
        await Task.Delay(200); await HistoryIdleAsync(shell);
        try
        {
            var scale = shell.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200 * scale), (int)(900 * scale)));
            var package = new DesktopAgentPlugin
            {
                Manifest = JsonNode.Parse("""{"name":"mcp-badge-package"}""")!.AsObject(),
                Mcp = JsonNode.Parse("""{"mcpServers":{"Package":{"type":"streamable-http","url":"https://package.example/mcp"}}}""")!.AsObject()
            };
            var skillPackage = new DesktopAgentPlugin { Manifest = JsonNode.Parse("""{"name":"skill-badge-package"}""")!.AsObject() };
            var store = Field<DesktopAgentPluginStore>(shell, "pluginStore");
            await store.SaveAsync(session.PluginPartition, package, true);
            await store.SaveAsync(session.PluginPartition, skillPackage, true);
            session.Settings.Chat.ActivePlugins = [DesktopLocalTools.Conversations, DesktopLocalTools.Canvas];
            session.Settings.Chat.EnabledAgentPluginIds = [package.Id, skillPackage.Id];
            session.Settings.Chat.EnabledSkillIds = ["missing-badge-skill"];
            session.ToolApprovals.ApproveAll = true;
            await (Task)Call(shell, "LoadPluginsAsync", CancellationToken.None)!;
            var manager = session.InitializeMcp();
            await manager.InstallAsync(new DesktopMcpServer { Id = "manual-badge", Name = "Manual", Url = "https://manual.example/mcp" }, default);
            await Task.Delay(80);
            Call(shell, "UpdateMode", ServiceKind.Ai); Call(shell, "RenderApprovalBadges"); shell.UpdateLayout();
            Border[] Badges(string name) => Descendants(shell).OfType<Border>().Where(b => b.Name == name).ToArray();
            Check(Badges("EnabledLocalPluginBadge").Length == 2 && Badges("EnabledAgentPluginBadge").Length == 2
                && Badges("EnabledSkillBadge").Length == 1, context + ": normal chat retains all selected-context badges");

            Call(shell, "UpdateMode", ServiceKind.Agents); await Task.Delay(80); shell.UpdateLayout();
            Check(Badges("EnabledLocalPluginBadge").Length == 2 && Badges("McpConnectedBadge").Length == 2
                && Field<ScrollViewer>(shell, "mcpTagScroll").Visibility == Visibility.Visible
                && Field<ScrollViewer>(shell, "contextTagScroll").Visibility == Visibility.Visible
                && Field<Grid>(shell, "composerBadgeRow").Visibility == Visibility.Visible,
                context + ": agent composer shows local-plugin and connected MCP badges");
            Check(Badges("EnabledAgentPluginBadge").Single().Tag as string == package.Id && Badges("EnabledSkillBadge").Length == 0,
                context + ": MCP-contributing package is removable but skill-only context remains hidden");
            Check(Field<MenuFlyoutItem>(shell, "manageMcp").Visibility == Visibility.Collapsed
                && Field<MenuFlyoutItem>(shell, "selectResources").Visibility == Visibility.Collapsed
                && Field<MenuFlyoutItem>(shell, "searchContext").Visibility == Visibility.Collapsed,
                context + ": agent context menus remain unchanged");
            Check(Badges("EnabledLocalPluginBadge").All(b => AutomationProperties.GetName(b)
                == DesktopResources.Get(DesktopLocalTools.Plugins.Single(p => p.Id == (string)b.Tag).ResourceKey)),
                context + ": agent plugin labels reuse normal-chat localization");
            McpTurnSnapshot Snapshot() => (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", session.Settings.Chat, true)!;
            Check(Snapshot().Tools.Any(t => t.GetProperty("name").GetString() == "fixture_manual")
                && Snapshot().Tools.Any(t => t.GetProperty("name").GetString() == "fixture_package"),
                context + ": shown MCP sources contribute tools to the agent turn");

            typeof(ChatShell).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, true);
            Call(shell, "SetBusy", true, false);
            Check(Descendants(shell).OfType<Button>().Where(b => b.Name is "DisableLocalPlugin" or "DisableAgentPlugin" or "DisconnectMcpServer")
                .All(b => !b.IsEnabled), context + ": all badge removal actions are disabled while busy");
            typeof(ChatShell).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, false);
            Call(shell, "SetBusy", false, false);

            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(500 * scale), (int)(900 * scale)));
            await Task.Delay(150); shell.UpdateLayout();
            Check(Grid.GetRow(Field<ScrollViewer>(shell, "approvalBadgeScroll")) == 1
                && Badges("EnabledLocalPluginBadge").Concat(Badges("EnabledAgentPluginBadge")).Concat(Badges("McpConnectedBadge")).All(b => Within(b, shell))
                && Field<ScrollViewer>(shell, "mcpTagScroll").ScrollableWidth < 1
                && Field<ScrollViewer>(shell, "contextTagScroll").ScrollableWidth < 1,
                context + ": narrow agent composer wraps badges with reachable removal actions");

            InvokeButton(Descendants(shell).OfType<Button>().Single(b => b.Name == "DisableLocalPlugin" && (string)b.Tag == DesktopLocalTools.Canvas));
            await HistoryIdleAsync(shell);
            var saved = await SettingsStore.LoadAsync(session.DataDirectory, new());
            var canvasNames = DesktopLocalTools.Plugins.Single(p => p.Id == DesktopLocalTools.Canvas).Tools.Select(t => t.GetProperty("name").GetString()).ToHashSet();
            Check(saved.Chat.ActivePlugins.SequenceEqual([DesktopLocalTools.Conversations]) && Badges("EnabledLocalPluginBadge").Length == 1
                && !Snapshot().Tools.Any(t => canvasNames.Contains(t.GetProperty("name").GetString())),
                context + ": agent badge removal persists only its local plugin and excludes its next-turn tools");

            var manual = Badges("McpConnectedBadge").Single(b => AutomationProperties.GetName(b).StartsWith("Manual title", StringComparison.Ordinal));
            InvokeButton(Descendants(manual).OfType<Button>().Single(b => b.Name == "DisconnectMcpServer"));
            await HistoryIdleAsync(shell); await Task.Delay(80);
            var savedMcp = await new DesktopMcpStore(Path.Combine(session.DataDirectory, "mcp")).LoadAsync(session.McpPartition);
            Check(!savedMcp.Servers.Single(s => s.Id == "manual-badge").Enabled && Badges("McpConnectedBadge").Length == 1
                && !Snapshot().Tools.Any(t => t.GetProperty("name").GetString() == "fixture_manual"),
                context + ": disconnect persists the MCP selection and excludes its next-turn tools");

            InvokeButton(Descendants(shell).OfType<Button>().Single(b => b.Name == "DisableAgentPlugin" && (string)b.Tag == package.Id));
            await HistoryIdleAsync(shell); await Task.Delay(80);
            saved = await SettingsStore.LoadAsync(session.DataDirectory, new());
            Check(saved.Chat.EnabledAgentPluginIds.SequenceEqual([skillPackage.Id]) && Badges("EnabledAgentPluginBadge").Length == 0
                && Badges("McpConnectedBadge").Length == 0 && manager.Servers.All(s => s.Server.PluginId != package.Id)
                && !Snapshot().Tools.Any(t => t.GetProperty("name").GetString() == "fixture_package")
                && saved.Chat.EnabledSkillIds.SequenceEqual(["missing-badge-skill"]),
                context + ": package removal reconciles MCP tools without touching unrelated context");
            Call(shell, "UpdateMode", ServiceKind.Ai); shell.UpdateLayout();
            Check(Badges("EnabledLocalPluginBadge").Single().Tag as string == DesktopLocalTools.Conversations
                && Badges("EnabledAgentPluginBadge").Single().Tag as string == skillPackage.Id && Badges("EnabledSkillBadge").Length == 1
                && Field<ScrollViewer>(shell, "mcpTagScroll").Visibility == Visibility.Collapsed,
                context + ": switching back to chat preserves shared removals and restores chat-only context");
        }
        finally { await shell.ShutdownAsync(); window.Content = null; if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true); }
    }

    private sealed class BadgeMcpFactory : IDesktopMcpClientFactory
    {
        public Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct) => Task.FromResult<IDesktopMcpConnection>(new BadgeMcpConnection(server));
    }

    private sealed class BadgeMcpConnection(DesktopMcpServer server) : IDesktopMcpConnection
    {
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? ResourcesChanged { add { } remove { } }
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct) => Task.FromResult(new McpDiscovery(
            JsonSerializer.SerializeToElement(new { name = server.Name, title = server.Name + " title" }), JsonSerializer.SerializeToElement(new { tools = new { } }), null,
            [JsonSerializer.SerializeToElement(new { name = server.PluginId is null ? "fixture_manual" : "fixture_package",
                inputSchema = new { type = "object" }, annotations = new { readOnlyHint = true } })]));
        public Task<JsonElement> CallAsync(string name, JsonElement input, string callId, string locale, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> ReadAsync(string uri, string? cursor, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> CompleteAsync(string template, string name, string value, IReadOnlyDictionary<string, string> arguments, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
