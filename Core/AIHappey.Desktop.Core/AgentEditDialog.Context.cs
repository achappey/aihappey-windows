using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class AgentEditDialog
{
    private static string[] Strings(JsonNode? node) => node is JsonArray array ? array.Select(DesktopAgent.Text).Where(s => s.Length > 0).ToArray() : [];
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private JsonObject Client() => DesktopAgent.Object(draft.Definition, "mcpClient");
    private StackPanel ModelContext(McpTurnSnapshot connected)
    {
        var panel = Panel();
        NativeSettingsSurface.Expander(panel, "AgentPolicy", ChatSettingsFields.L("agent.policy"), null, out var policy);
        foreach (var key in new[] { "readOnlyHint", "destructiveHint", "openWorldHint", "idempotentHint" })
            fields.Switch(policy, "agent." + key, () => DesktopAgent.Boolean(draft.Definition["mcpClient"]?["policy"]?[key]),
                on => DesktopAgent.Object(Client(), "policy")[key] = on);
        bool Elicit() => draft.Definition["mcpClient"]?["capabilities"]?["elicitation"] is JsonObject;
        var elicitationHeader = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        fields.HeaderSwitch(elicitationHeader, "agent.elicitation", Elicit, on =>
        {
            var capabilities = DesktopAgent.Object(Client(), "capabilities");
            if (on) capabilities["elicitation"] = new JsonObject();
            else { capabilities.Remove("elicitation"); if (capabilities.Count == 0) Client().Remove("capabilities"); }
        });
        NativeSettingsSurface.Expander(panel, "AgentElicitation", ChatSettingsFields.L("agent.elicitation"), elicitationHeader, out var elicitation);
        fields.Switch(elicitation, "agent.elicitationForm", () => draft.Definition["mcpClient"]?["capabilities"]?["elicitation"]?["form"] is JsonObject,
            on => { var e = DesktopAgent.Object(DesktopAgent.Object(Client(), "capabilities"), "elicitation"); if (on) e["form"] = new JsonObject(); else e.Remove("form"); }, Elicit);
        fields.Switch(elicitation, "agent.elicitationUrl", () => draft.Definition["mcpClient"]?["capabilities"]?["elicitation"]?["url"] is JsonObject, _ => { }, () => false);
        var servers = Panel(); panel.Children.Add(servers);
        void RenderServers()
        {
            fields.Forget(servers); servers.Children.Clear();
            if (draft.Definition["mcpServers"] is not JsonObject configured) return;
            foreach (var (key, value) in configured.ToArray())
            {
                if (value is not JsonObject server) continue;
                var header = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
                fields.HeaderSwitch(header, "agent.enabled", () => !DesktopAgent.Boolean(server["disabled"]), on => { server["disabled"] = !on; RefreshCheckToolOptions?.Invoke(); });
                NativeSettingsSurface.Expander(servers, "AgentServer", key, header, out var body);
                Func<bool> enabled = () => !DesktopAgent.Boolean(server["disabled"]);
                fields.Text(body, "agent.url", () => DesktopAgent.Text(server["url"]), v => server["url"] = v.Trim(), validate: v =>
                { try { McpValidation.Endpoint(v); return true; } catch { return false; } });
                // Header objects are extensible in the portable contract. The native key/value rows
                // edit just one entry and do not erase opaque imported values.
                Headers(body, server);
                var (left, right) = ChatSettingsFields.Pair(body);
                fields.Switch(left, "agent.required", () => DesktopAgent.Boolean(server["required"]), on => server["required"] = on, enabled);
                fields.Switch(right, "agent.deferLoading", () => DesktopAgent.Boolean(server["defer_loading"]), on => { if (on) server["defer_loading"] = true; else server.Remove("defer_loading"); }, enabled);
                fields.Switch(left, "agent.namespace", () => DesktopAgent.Boolean(server["namespace"]), on => { if (on) server["namespace"] = true; else server.Remove("namespace"); }, enabled);
                foreach (var caller in new[] { "direct", "programmatic" })
                    fields.Switch(body, "agent.caller." + caller, () => Strings(server["allowed_callers"]).Contains(caller), on =>
                    {
                        var list = Strings(server["allowed_callers"]).Where(c => c != caller).ToList(); if (on) list.Add(caller);
                        if (list.Count > 0) server["allowed_callers"] = Array(list); else server.Remove("allowed_callers");
                    }, enabled);
                var known = discoveredAgentTools.GetValueOrDefault(DesktopAgent.Text(server["url"])) ?? ConnectedToolNames(connected, DesktopAgent.Text(server["url"]));
                fields.Select(body, "agent.allowedTools", () => !server.ContainsKey("allowed_tools") ? "all" : Strings(server["allowed_tools"]).Length == 0 ? "none" : "selected",
                    ["all", "none", "selected"], choice => { if (choice == "all") server.Remove("allowed_tools"); else if (choice == "none") server["allowed_tools"] = new JsonArray();
                        else if (!server.ContainsKey("allowed_tools") || Strings(server["allowed_tools"]).Length == 0) server["allowed_tools"] = Array(known); }, enabled,
                    label: v => ChatSettingsFields.L("agent.tools." + v));
                if (known.Count == 0) body.Children.Add(new TextBlock { Text = DesktopResources.Get("AgentToolsNotConnected"), TextWrapping = TextWrapping.Wrap });
                var discover = new Button { Content = DesktopResources.Get("AgentDiscoverTools") }; ControlAppearance.Stock(discover); body.Children.Add(discover);
                discover.Click += async (_, _) => await WorkAsync(async ct =>
                {
                    var url = DesktopAgent.Text(server["url"]); McpValidation.Endpoint(url);
                    var descriptor = new DesktopMcpServer { Id = "agent:" + key, Name = key, Url = url,
                        Headers = (server["headers"] as JsonObject ?? new()).ToDictionary(p => p.Key, p => DesktopAgent.Text(p.Value)) };
                    // Temporary discovery never installs/changes globally configured servers or
                    // negotiates elicitation into another modal while this editor is open.
                    var factory = session.McpClientFactory is DesktopMcpClientFactory ? new DesktopMcpClientFactory() : session.McpClientFactory;
                    await using var connection = await factory.ConnectAsync(descriptor, ct);
                    var discovery = await connection.DiscoverAsync(ct); ct.ThrowIfCancellationRequested();
                    discoveredAgentTools[url] = discovery.Tools.Select(t => CatalogProjection.Text(t, "name") ?? "").Where(n => n.Length > 0).Distinct().ToArray();
                    RenderServers(); RefreshCheckToolOptions?.Invoke();
                });
                foreach (var name in known.Concat(Strings(server["allowed_tools"])).Distinct().Order(StringComparer.Ordinal))
                    fields.Switch(body, name, () => !server.ContainsKey("allowed_tools") || Strings(server["allowed_tools"]).Contains(name), on =>
                    {
                        var selected = server.ContainsKey("allowed_tools") ? Strings(server["allowed_tools"]).ToList() : known.ToList();
                        selected.RemoveAll(n => n == name); if (on) selected.Add(name); server["allowed_tools"] = Array(selected);
                    }, enabled, label: name);
                var remove = new Button { Content = DesktopResources.Get("Delete") }; ControlAppearance.Stock(remove); body.Children.Add(remove);
                remove.Click += (_, _) => { configured.Remove(key); RenderServers(); };
            }
            fields.Refresh();
        }
        RenderServers();
        NativeSettingsSurface.Expander(panel, "AgentAddServer", ChatSettingsFields.L("agent.addServer"), null, out var add);
        var serverName = new TextBox { Header = ChatSettingsFields.L("agent.serverName") };
        var serverUrl = new TextBox { Header = ChatSettingsFields.L("agent.url"), PlaceholderText = "https://" };
        ControlAppearance.Stock(serverName); ControlAppearance.Stock(serverUrl); add.Children.Add(serverName); add.Children.Add(serverUrl);
        var install = new Button { Content = DesktopResources.Get("Add") }; ControlAppearance.Stock(install); add.Children.Add(install);
        void AddServer(string key, string url)
        {
            key = key.Trim().ToLowerInvariant(); if (key.Length == 0) throw new InvalidOperationException(DesktopResources.Get("AgentNameInvalid"));
            McpValidation.Endpoint(url); var configured = DesktopAgent.Object(draft.Definition, "mcpServers");
            if (configured.ContainsKey(key)) throw new InvalidOperationException(DesktopResources.Get("AgentDuplicateName"));
            configured[key] = new JsonObject { ["type"] = "http", ["url"] = url, ["disabled"] = true }; RenderServers();
        }
        install.Click += (_, _) => { try { AddServer(serverName.Text, serverUrl.Text.Trim()); serverName.Text = serverUrl.Text = ""; } catch (Exception error) { Message(error.Message); } };
        var browse = new Button { Content = DesktopResources.Get("McpCatalog") }; ControlAppearance.Stock(browse); add.Children.Add(browse);
        var search = new AutoSuggestBox { Header = DesktopResources.Get("McpCatalog"), DisplayMemberPath = "Name", Visibility = Visibility.Collapsed };
        ControlAppearance.Stock(search); add.Children.Add(search); IReadOnlyList<McpCatalogItem> catalogItems = [];
        browse.Click += async (_, _) => await WorkAsync(async ct =>
        {
            var result = await new DesktopMcpCatalogClient(registryHttp).ListAsync(session.ContextOptions.McpCatalogUrls ?? [], ct);
            ct.ThrowIfCancellationRequested(); catalogItems = result.Items; search.ItemsSource = catalogItems.Take(100).ToArray(); search.Visibility = Visibility.Visible;
            if (result.FailedSources.Count > 0) Message(DesktopResources.Get("McpRegistryPartialFailure")); search.Focus(FocusState.Programmatic);
        });
        search.TextChanged += (_, _) => search.ItemsSource = catalogItems.Where(i => (i.Name + " " + i.Description).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
        search.SuggestionChosen += (_, args) => { if (args.SelectedItem is McpCatalogItem item) { try { AddServer(item.Name, item.Url); } catch (Exception error) { Message(error.Message); } } };
        return panel;
    }
    private readonly Dictionary<string, IReadOnlyList<string>> discoveredAgentTools = [];
    private Action? RefreshCheckToolOptions;
    private static IReadOnlyList<string> ConnectedToolNames(McpTurnSnapshot connected, string url)
    {
        return connected.Context.Where(c => c.TryGetProperty("modelContextProtocolServer", out var s) && CatalogProjection.Text(s, "mcpServerUrl") == url)
            .SelectMany(c => c.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array ? tools.EnumerateArray().Select(t => CatalogProjection.Text(t, "name") ?? "").ToArray() : [])
            .Where(n => n.Length > 0).Distinct().ToArray();
    }
    private void Headers(StackPanel parent, JsonObject server)
    {
        var rows = Panel(); parent.Children.Add(rows);
        void Render()
        {
            fields.Forget(rows); rows.Children.Clear();
            if (server["headers"] is not JsonObject headers) return;
            foreach (var (key, value) in headers.ToArray())
            {
                var row = Panel(); rows.Children.Add(row);
                var box = fields.Text(row, "agent.headerValue", () => DesktopAgent.Text(headers[key]), v => headers[key] = v, validate: v => !v.Any(char.IsControl));
                box.Header = key; box.IsEnabled = value is JsonValue;
                var remove = new Button { Content = DesktopResources.Get("Delete") }; ControlAppearance.Stock(remove); row.Children.Add(remove);
                remove.Click += (_, _) => { headers.Remove(key); if (headers.Count == 0) server.Remove("headers"); Render(); };
            }
        }
        Render();
        var name = new TextBox { Header = ChatSettingsFields.L("agent.headerName") }; var valueBox = new TextBox { Header = ChatSettingsFields.L("agent.headerValue") };
        ControlAppearance.Stock(name); ControlAppearance.Stock(valueBox); parent.Children.Add(name); parent.Children.Add(valueBox);
        var add = new Button { Content = ChatSettingsFields.L("agent.addHeader") }; ControlAppearance.Stock(add); parent.Children.Add(add);
        add.Click += (_, _) =>
        {
            try
            {
                var validated = McpValidation.Headers(new Dictionary<string, string> { [name.Text.Trim()] = valueBox.Text });
                var headers = DesktopAgent.Object(server, "headers"); if (headers.ContainsKey(name.Text.Trim())) throw new InvalidOperationException(DesktopResources.Get("McpInvalidHeaders"));
                foreach (var (key, value) in validated) headers[key] = value; name.Text = valueBox.Text = ""; Render();
            }
            catch (Exception error) { Message(error.Message); }
        };
    }
    private StackPanel Tools()
    {
        var panel = Panel();
        foreach (var type in new[] { "tool_search", "resource_search" })
        {
            var header = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            fields.HeaderSwitch(header, "agent." + type, () => (draft.Definition["tools"] as JsonArray)?.Any(t => DesktopAgent.Text(t?["type"]) == type) == true,
                on => draft.ToggleTool(type, on));
            NativeSettingsSurface.ToggleCard(panel, "AgentTool_" + type, ChatSettingsFields.L("agent." + type), header);
        }
        return panel;
    }
    private JsonObject Evaluator() => DesktopAgent.Object(DesktopAgent.Object(draft.Definition, "evaluations"), "localEvaluator");
    private JsonNode? Check(string key) => draft.Definition["evaluations"]?["localEvaluator"]?[key];
    private void SetCheck(string key, JsonNode? value)
    {
        var evaluations = DesktopAgent.Object(draft.Definition, "evaluations"); var evaluator = DesktopAgent.Object(evaluations, "localEvaluator");
        if (value is null) evaluator.Remove(key); else evaluator[key] = value;
        if (evaluator.Count == 0) evaluations.Remove("localEvaluator"); if (evaluations.Count == 0) draft.Definition.Remove("evaluations");
    }
    private StackPanel Checks(McpTurnSnapshot connected)
    {
        var panel = Panel();
        StackPanel Section(string key, JsonNode initial, bool expandable = true)
        {
            var header = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            fields.HeaderSwitch(header, "agent." + key, () => Check(key) is not null,
                on => SetCheck(key, on ? initial.DeepClone() : null));
            if (!expandable) { NativeSettingsSurface.ToggleCard(panel, "AgentCheck_" + key, ChatSettingsFields.L("agent." + key), header); return Panel(); }
            var expander = NativeSettingsSurface.Expander(panel, "AgentCheck_" + key, ChatSettingsFields.L("agent." + key), header, out var content);
            fields.Watch(() => { if (Check(key) is not null) expander.IsExpanded = true; });
            return content;
        }
        var body = Section("nonEmpty", new JsonObject { ["minLength"] = 1 });
        fields.Integer(body, "agent.minLength", () => Check("nonEmpty")?["minLength"]?.ToJsonString() ?? "1",
            n => DesktopAgent.Object(Evaluator(), "nonEmpty")["minLength"] = n, () => Check("nonEmpty") is not null, optional: false);
        body = Section("keywordCheck", new JsonObject { ["keywords"] = new JsonArray(), ["caseSensitive"] = false });
        fields.Text(body, "agent.keywords", () => string.Join("\n", Strings(Check("keywordCheck")?["keywords"])),
            v => DesktopAgent.Object(Evaluator(), "keywordCheck")["keywords"] = Array(OpenAIChatConfig.Split(v, true)), () => Check("keywordCheck") is not null, multiline: true);
        fields.Switch(body, "agent.caseSensitive", () => DesktopAgent.Boolean(Check("keywordCheck")?["caseSensitive"]),
            on => DesktopAgent.Object(Evaluator(), "keywordCheck")["caseSensitive"] = on, () => Check("keywordCheck") is not null);
        Section("toolCallsPresent", JsonValue.Create(true)!, false);
        body = Section("toolCalledCheck", new JsonObject { ["toolNames"] = new JsonArray(), ["mode"] = "All" });
        fields.Text(body, "agent.toolNames", () => string.Join("\n", Strings(Check("toolCalledCheck")?["toolNames"])),
            v => DesktopAgent.Object(Evaluator(), "toolCalledCheck")["toolNames"] = Array(OpenAIChatConfig.Split(v, true)), () => Check("toolCalledCheck") is not null, multiline: true);
        var available = new ComboBox { Header = ChatSettingsFields.L("agent.availableTools"), HorizontalAlignment = HorizontalAlignment.Stretch };
        RefreshCheckToolOptions = () =>
        {
            available.Items.Clear();
            foreach (var name in (draft.Definition["mcpServers"] as JsonObject ?? new()).Where(s => !DesktopAgent.Boolean(s.Value?["disabled"]))
                .SelectMany(s => discoveredAgentTools.GetValueOrDefault(DesktopAgent.Text(s.Value?["url"])) ?? ConnectedToolNames(connected, DesktopAgent.Text(s.Value?["url"])))
                .Distinct().Order(StringComparer.Ordinal)) available.Items.Add(name);
        };
        RefreshCheckToolOptions();
        ControlAppearance.Stock(available); body.Children.Add(available);
        available.SelectionChanged += (_, _) => { if (available.SelectedItem is string name && Check("toolCalledCheck") is not null)
            fields.Changed(() => DesktopAgent.Object(Evaluator(), "toolCalledCheck")["toolNames"] = Array(Strings(Check("toolCalledCheck")?["toolNames"]).Append(name).Distinct())); };
        fields.Select(body, "agent.mode", () => DesktopAgent.Text(Check("toolCalledCheck")?["mode"]) is { Length: > 0 } mode ? mode : "All", ["All", "Any"],
            mode => DesktopAgent.Object(Evaluator(), "toolCalledCheck")["mode"] = mode, () => Check("toolCalledCheck") is not null);
        Section("hasImageContent", JsonValue.Create(true)!, false);
        return panel;
    }
}
