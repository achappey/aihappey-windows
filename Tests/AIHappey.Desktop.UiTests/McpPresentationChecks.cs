using System.Reflection;
using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckMcpPresentationAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme };
        window!.Content = root; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800)); window.Activate();
        await Task.Delay(100);
        var context = "MCP presentation / " + theme;
        var reads = 0;
        var entry = new McpResourceEntry("server", "Example server", "https://mcp.example/", Json("""
            {"title":"Resource title","name":"resource","description":"Resource description","uri":"resource://example/secret","mimeType":"text/plain"}
            """), false, (_, _, _, _) => { reads++; return Task.FromResult(Json("""{"contents":[{"uri":"resource://example/secret","text":"Resource content","mimeType":"text/plain"}]}""")); }, null);
        var template = entry with { IsTemplate = true, Resource = Json("""
            {"title":"Template title","uriTemplate":"resource://example/{name}","description":"Template description","mimeType":"text/plain"}
            """) };
        var picker = new McpResourcesDialog([entry, template]) { XamlRoot = root.XamlRoot };
        var pickerTask = picker.ShowAsync();
        await Task.Delay(200); picker.UpdateLayout();
        var badges = Descendants(picker).OfType<Border>().Where(border => border.Name == "McpResourceMetadataBadge").ToArray();
        Check(badges.Length == 6 && badges.All(badge => badge.Child is TextBlock { FontSize: 12 })
            && Descendants(picker).OfType<TextBlock>().Any(text => text.Text == entry.Name)
            && Descendants(picker).OfType<TextBlock>().Any(text => text.Text == entry.Description), context + ": picker titles, separate metadata badges and descriptions");
        Check(!Descendants(picker).OfType<TextBlock>().Any(text => text.Text == entry.Uri || text.Text == template.Uri)
            && reads == 0, context + ": resource picker hides URIs without reading content");
        typeof(McpResourcesDialog).GetMethod("ShowArguments", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, [template]);
        await Task.Delay(100);
        var fields = (StackPanel)typeof(McpResourcesDialog).GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
        Check(Descendants(fields).OfType<Border>().Count(border => border.Name == "McpResourceMetadataBadge") == 3
            && Descendants(fields).OfType<TextBox>().Single().Header?.ToString() == "name"
            && !Descendants(fields).OfType<TextBlock>().Any(text => text.Text == template.Uri), context + ": template metadata and arguments without displayed URI");
        var arguments = (TextBox)Descendants(fields).OfType<TextBox>().Single();
        Check(arguments.Focus(FocusState.Keyboard), context + ": template argument is keyboard focusable");
        picker.Hide(); await pickerTask;

        var minimal = new McpResourceView { DataContext = entry with { Resource = Json("""{"name":"Minimal","uri":"resource://minimal"}""") } };
        root.Children.Add(minimal); await Task.Delay(80);
        Check(Descendants(minimal).OfType<Border>().Count(border => border.Name == "McpResourceMetadataBadge") == 2,
            context + ": missing MIME metadata omits its badge");
        minimal.DataContext = entry;
        Check(Descendants(minimal).OfType<Border>().Count(border => border.Name == "McpResourceMetadataBadge") == 3,
            context + ": recycled resource row refreshes metadata");
        root.Children.Clear();

        // Use native embedded images only; no fixture can make a network request.
        var iconType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.McpServerIcon")!;
        var svg = new McpIcon("data:image/svg+xml," + Uri.EscapeDataString("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\"><rect width=\"32\" height=\"32\" fill=\"green\"/></svg>"));
        var icon = (UserControl)Activator.CreateInstance(iconType, new object[] { new McpIcon[] { svg }, 32d })!;
        root.Children.Add(icon); await Task.Delay(80);
        Check(!Descendants(icon).OfType<Image>().Any() && Descendants(icon).OfType<FluentIcons.WinUI.FluentIcon>().Single().Visibility == Visibility.Visible,
            context + ": disabled-image test switch keeps generic MCP artwork");
        AppContext.SetSwitch("AIHappey.Desktop.DisableRemoteImages", false);
        try
        {
            root.Children.Clear(); root.Children.Add(icon); await Task.Delay(150);
            var image = Descendants(icon).OfType<Image>().Single();
            Check(image.Source is SvgImageSource && image.Visibility == Visibility.Visible && image.Stretch == Stretch.Uniform
                && Descendants(icon).OfType<FluentIcons.WinUI.FluentIcon>().Single().Visibility == Visibility.Collapsed, context + ": embedded SVG replaces fallback with aspect ratio preserved");
            root.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            await Task.Delay(150);
            Check(Descendants(icon).OfType<Image>().Single().Visibility == Visibility.Visible, context + ": native image survives runtime theme switch");
            root.Children.Clear();
            var broken = (UserControl)Activator.CreateInstance(iconType, new object[] { new McpIcon[] { new("data:image/png;base64,not-base64") }, 16d })!;
            root.Children.Add(broken); await Task.Delay(100);
            Check(Descendants(broken).OfType<FluentIcons.WinUI.FluentIcon>().Single().Visibility == Visibility.Visible
                && Descendants(broken).OfType<Image>().Single().Visibility == Visibility.Collapsed, context + ": invalid image retains generic artwork");
        }
        finally { AppContext.SetSwitch("AIHappey.Desktop.DisableRemoteImages", true); root.Children.Clear(); root.RequestedTheme = theme; }

        var approval = new ToolApprovalDialog(new("approval", "call", "example", "Example tool", Json("""
            {"input":{"query":"Example input"},"output":{"structuredContent":{"answer":42},"content":[{"type":"text","text":"Example output"}]}}
            """))) { XamlRoot = root.XamlRoot };
        var approvalTask = approval.ShowAsync(); await Task.Delay(200);
        var navigation = Descendants(approval).OfType<NavigationView>().Single(view => view.Name == "ToolApprovalTabsNavigation");
        Check(navigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Top
            && ((NavigationViewItem)navigation.SelectedItem).Name == "ToolApprovalOutput"
            && !Descendants(approval).OfType<TabView>().Any(), context + ": native top-navigation approval tabs initially show Output");
        var sections = Descendants(navigation).OfType<NavigationView>().Single(view => view.Name == "ToolApprovalOutputTabsNavigation");
        Check(sections.MenuItems.Count == 3 && sections.PaneDisplayMode == NavigationViewPaneDisplayMode.Top,
            context + ": structured, content and raw output use consistent native navigation");
        navigation.SelectedItem = navigation.MenuItems[0]; await Task.Delay(80);
        Check(Descendants(navigation).OfType<TextBlock>().Any(text => text.Text.Contains("Example input")), context + ": selecting Input shows read-only tool input");
        var inputTab = (NavigationViewItem)navigation.MenuItems[0];
        Check(inputTab.Focus(FocusState.Keyboard) && new NavigationViewItemAutomationPeer(inputTab).GetName() == "Input",
            context + ": review tabs expose native keyboard focus and accessible names");
        navigation.SelectedItem = navigation.MenuItems[1]; await Task.Delay(80);
        foreach (var width in new[] { 520, 1000 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 800)); await Task.Delay(120); approval.UpdateLayout();
            Check(Descendants(approval).OfType<ScrollViewer>().All(view => view.ScrollableWidth < 1)
                && ((FrameworkElement)approval.Content).ActualWidth <= root.ActualWidth, context + $": review navigation fits {width}px window without horizontal scrolling");
        }
        Check(approval.PrimaryButtonText == "Allow" && approval.SecondaryButtonText == "Deny" && approval.CloseButtonText == "Stop"
            && Descendants(approval).OfType<SplitButton>().Any(button => button.Name == "AutomaticToolApproval")
            && approval.Decision is null, context + ": approval actions remain available without implicit decision");
        approval.Hide(); await approvalTask;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800));
        await CheckSystemContextToolsAsync(root, context);
        await CheckMcpHeaderEditorAsync(root, context);
        File.WriteAllLines(report, results); window.Content = null;
    }

    private async Task CheckSystemContextToolsAsync(Grid root, string context)
    {
        var calls = 0;
        Task<JsonElement> Call(string name, JsonElement input, CancellationToken ct)
        { calls++; return Task.FromResult(Json("{}")); }
        var local = McpTurnSnapshot.Empty;
        foreach (var plugin in DesktopLocalTools.Plugins.Where(plugin => plugin.Id != DesktopLocalTools.SkillSearch))
            DesktopLocalTools.Register(local, plugin.Id, Call);
        await InspectAsync(local, "local plugins without MCP");

        var resourceDiscovery = new McpDiscovery(Json("{}"), Json("{}"), null, [])
        {
            Resources = [Json("""{"name":"Resource","uri":"resource://example"}""")]
        };
        var server = new McpCatalogItem("context-server", "Context server", "", "https://mcp.example/");
        var resources = McpTurnSnapshot.Create([new McpConnectedServer(server, resourceDiscovery,
            (name, input, id, locale, ct) => Call(name, input, ct),
            (uri, cursor, limit, ct) => Call(uri, Json("{}"), ct))]);
        Check(resources.Tools.Count == 1 && CatalogProjection.Text(resources.Tools[0], "name") == DesktopMcpResources.ToolName,
            context + ": resource-only fixture exposes read_resource");
        await InspectAsync(resources, "resource-only MCP connection");

        var skill = new DesktopSkill("context-skill", "Context skill", "Test skill", "local");
        var skillTurn = new DesktopSkillTurn([(skill, ct =>
        {
            calls++; throw new InvalidOperationException("Context inspection must not load skill content.");
        })]);
        var skills = McpTurnSnapshot.Empty; skillTurn.Register(skills);
        Check(skills.Tools.Count == 2, context + ": enabled skill fixture exposes activation and resource tools");
        await InspectAsync(skills, "enabled skill without MCP");
        var search = McpTurnSnapshot.Empty;
        new DesktopSkillTurn([], searchEnabled: true).Register(search);
        await InspectAsync(search, "skill search tools without enabled skills");

        var collisionName = CatalogProjection.Text(local.Tools[0], "name")!;
        var collision = JsonSerializer.SerializeToElement(new
        {
            name = collisionName, title = "Server collision tool", description = "Server tool description",
            inputSchema = new { type = "object", properties = new { query = new { type = "string" } } },
            outputSchema = new { type = "object", properties = new { answer = new { type = "string" } } },
            annotations = new { readOnlyHint = true, idempotentHint = true }
        });
        var mixed = McpTurnSnapshot.Create([new McpConnectedServer(server, resourceDiscovery with { Tools = [collision] },
            (name, input, id, locale, ct) => Call(name, input, ct))]);
        foreach (var plugin in DesktopLocalTools.Plugins.Where(plugin => plugin.Id != DesktopLocalTools.SkillSearch))
            DesktopLocalTools.Register(mixed, plugin.Id, Call);
        skillTurn.Register(mixed);
        var alias = CatalogProjection.Text(mixed.Tools[0], "name")!;
        Check(alias != collisionName && alias.StartsWith("mcp_"), context + ": mixed fixture uses MCP collision alias");
        await InspectAsync(mixed, "mixed MCP, resource, local and skill tools");
        await InspectAsync(McpTurnSnapshot.Empty, "disabled tools and no connected servers");

        // Custom composers may include descriptive tool blocks. They must never override request availability.
        var omitted = McpTurnSnapshot.Empty;
        omitted.AddContext(Json("""{"modelContextProtocolServer":{"name":"Stale server"},"tools":[{"name":"disabled_tool","inputSchema":{"type":"object"}}]}"""));
        await InspectAsync(omitted, "context-only tool is not in request");
        Check(calls == 0, context + ": inspecting tools does not execute tools or read resources/skills");

        async Task InspectAsync(McpTurnSnapshot snapshot, string scenario)
        {
            var message = new DesktopSystemContextComposer().Compose(new(new() { AppName = "Context test", ChatbotInstructions = "Test instructions" },
                new(), null, "en", false, DateTimeOffset.UtcNow) { Mcp = snapshot });
            var request = new ChatPreferences().RequestBody("test/model", "context-test", [message], null, snapshot);
            var expected = JsonSerializer.Deserialize<JsonElement>(request["tools"]!.ToJsonString()).EnumerateArray().ToArray();
            var dialogType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.SystemContextDialog")!;
            var dialog = (ContentDialog)Activator.CreateInstance(dialogType, new object[] { message, "Context test", false, snapshot.Tools })!;
            dialog.XamlRoot = root.XamlRoot;
            var dialogTask = dialog.ShowAsync(); await Task.Delay(120); dialog.UpdateLayout();
            var prefix = context + " / " + scenario;
            var displayed = (List<JsonElement>)dialogType.GetField("tools", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            Check(displayed.Select(tool => tool.GetRawText()).SequenceEqual(expected.Select(tool => tool.GetRawText())),
                prefix + ": complete definitions match request exactly, including schemas and aliases");
            var toolTab = Descendants(dialog).OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>()
                .SingleOrDefault(button => button.Name == "SystemContextToolsTab");
            Check(expected.Length == 0 ? toolTab is null : toolTab is { IsChecked: true }
                && toolTab.Content?.ToString() == "Tools (" + expected.Length + ")", prefix + ": correct Tools count and initial selection");
            var cards = Descendants(dialog).OfType<Border>().Where(border => border.Name == "SystemContextToolCard").ToArray();
            Check(cards.Length == expected.Length && cards.Select((card, index) =>
                Descendants(card).OfType<TextBlock>().Any(text => text.Text == CatalogProjection.Text(expected[index], "name"))).All(match => match),
                prefix + ": every request tool is rendered once with its exact name");
            foreach (var (card, tool) in cards.Zip(expected))
            {
                Check(Descendants(card).OfType<Expander>().Count() >= 1, prefix + ": input schema remains inspectable");
                if (tool.TryGetProperty("outputSchema", out _))
                    Check(Descendants(card).OfType<Expander>().Any(expander => expander.Header?.ToString() == "Output"), prefix + ": output schema remains inspectable");
                if (CatalogProjection.Text(tool, "title") == "Server collision tool")
                    Check(Descendants(card).OfType<TextBlock>().Any(text => text.Text == "Server tool description")
                        && Descendants(card).OfType<TextBlock>().Any(text => text.Text == "Read-only"), prefix + ": descriptions and annotation badges preserved");
            }
            var tabs = Descendants(dialog).OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>()
                .Where(button => button.Name.StartsWith("SystemContextTab")).ToArray();
            Check(tabs.Length == message.Parts.Count && tabs.Any(button => button.Content?.ToString() == "Context test"),
                prefix + ": all original context tabs retained");
            dialogType.GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [expected.Length > 0 ? 1 : 0]);
            dialog.UpdateLayout();
            Check(!Descendants(dialog).OfType<Border>().Any(border => border.Name == "SystemContextToolCard"),
                prefix + ": other context tabs remain separately navigable");
            dialog.Hide(); await dialogTask;
        }
    }

    private async Task CheckMcpHeaderEditorAsync(Grid root, string context)
    {
        root.Children.Clear();
        var server = new DesktopMcpServer { Name = "Header fixture", Url = "https://mcp.example/images", HeaderDefinitions = [new()
        {
            Name = "Authorization", Description = "Header-level credential guidance", Value = "Bearer {key}", IsRequired = true,
            Variables = new() { ["key"] = new() { IsSecret = true, IsRequired = true, Placeholder = "Provider key" } }
        }, new() { Name = "X-Region", Choices = ["eu", "us"], Default = "eu" }] };
        var type = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.McpHeadersEditor")!;
        var editor = (UserControl)Activator.CreateInstance(type, [server])!;
        root.Children.Add(editor); await Task.Delay(100);
        var tabs = Descendants(editor).OfType<TabView>().Single(view => view.Name == "McpHeaderTabs");
        var secret = Descendants(editor).OfType<PasswordBox>().Single();
        Check(((TabViewItem)tabs.SelectedItem).Tag?.ToString() == "form" && tabs.TabItems.Count == 3 && !tabs.IsAddTabButtonVisible
            && tabs.TabItems.OfType<TabViewItem>().All(item => !item.IsClosable) && secret.PasswordRevealMode == PasswordRevealMode.Hidden
            && secret.PlaceholderText == "Provider key" && Descendants(editor).OfType<ComboBox>().Single().SelectedItem?.ToString() == "eu",
            context + ": registry editor defaults to native Form/Headers/Raw tabs with secret input and declared choices/defaults");
        Check(Descendants(editor).OfType<Border>().Count(card => card.Name == "McpRegistryHeaderCard") == 2
            && Descendants(editor).OfType<TextBlock>().Any(text => text.Text == "Header-level credential guidance"),
            context + ": each header has a card including its own description");
        var incomplete = (Dictionary<string, string>)type.GetMethod("ReadHeaders")!.Invoke(editor, null)!;
        Check(incomplete["Authorization"] == "Bearer {key}" && !McpHeaderConfiguration.CanConnect(new()
            { HeaderDefinitions = server.HeaderDefinitions, Headers = incomplete }), context + ": incomplete form can be saved but cannot connect");
        secret.Password = "fixture-key";
        var resolved = (Dictionary<string, string>)type.GetMethod("ReadHeaders")!.Invoke(editor, null)!;
        Check(resolved["Authorization"] == "Bearer fixture-key" && server.Headers.Count == 0,
            context + ": registry credential resolved in isolated draft only");
        type.GetMethod("Switch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, ["raw"]); await Task.Delay(80);
        var raw = Descendants(editor).OfType<TextBox>().Single(box => box.Name == "McpHeadersJson");
        Check(raw.Visibility == Visibility.Visible && McpHeaderConfiguration.Parse(raw.Text)["Authorization"] == "Bearer fixture-key",
            context + ": form values available in raw JSON tab");
        raw.Text = "{\"Authorization\":\"Bearer changed\",\"X-Region\":\"us\",\"X-Extra\":\"retain\"}";
        type.GetMethod("Switch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, ["form"]); await Task.Delay(80);
        resolved = (Dictionary<string, string>)type.GetMethod("ReadHeaders")!.Invoke(editor, null)!;
        Check(resolved["Authorization"] == "Bearer changed" && resolved["X-Extra"] == "retain"
            && Descendants(editor).OfType<PasswordBox>().All(box => box.PasswordRevealMode == PasswordRevealMode.Hidden),
            context + ": JSON edits and unknown headers round-trip into masked form fields");
        root.Children.Clear();

        var customEditor = (UserControl)Activator.CreateInstance(type, [new DesktopMcpServer { Name = "Custom", Url = "https://custom.example/mcp" }])!;
        root.Children.Add(customEditor); await Task.Delay(80);
        var customTabs = Descendants(customEditor).OfType<TabView>().Single(view => view.Name == "McpHeaderTabs");
        Check(customTabs.TabItems.Count == 2 && ((TabViewItem)customTabs.SelectedItem).Tag?.ToString() == "headers"
            && customTabs.TabItems.OfType<TabViewItem>().Select(item => item.Header?.ToString()).SequenceEqual(["Headers", "Raw"]),
            context + ": URL-only server shows Headers/Raw native tabs without empty Form tab");
        root.Children.Clear();

        var storeRoot = Path.Combine(Path.GetTempPath(), "AIHappey-McpHeaderUi-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var manager = new DesktopMcpManager(new DesktopMcpClientFactory(), new DesktopMcpStore(storeRoot));
            await manager.LoadAsync("fixture", default);
            var dialogType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.McpServersDialog")!;
            var dialog = (ContentDialog)Activator.CreateInstance(dialogType, [manager, false, server])!;
            dialog.XamlRoot = root.XamlRoot; var opened = dialog.ShowAsync(); await Task.Delay(120);
            Check(!Descendants(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("Explicit headers take precedence")),
                context + ": verbose MCP edit description removed");
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "Cancel"));
            Check(manager.Servers.Count == 0, context + ": cancel configuration never installs a server");
            dialog.Hide(); await opened;
            dialog = (ContentDialog)Activator.CreateInstance(dialogType, [manager, false, server])!;
            dialog.XamlRoot = root.XamlRoot; opened = dialog.ShowAsync(); await Task.Delay(120);
            Descendants(dialog).OfType<PasswordBox>().Single().Password = "saved-fixture-key";
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "Save"));
            for (var attempt = 0; manager.Servers.Count == 0 && attempt < 30; attempt++) await Task.Delay(50);
            Check(manager.Servers.Count == 1 && manager.Servers[0].Server.Headers["Authorization"] == "Bearer saved-fixture-key"
                && manager.Servers[0].State == McpConnectionState.Disabled, context + ": valid form saves server disabled without initiating OAuth");
            dialog.Hide(); await opened;
        }
        finally { if (Directory.Exists(storeRoot)) Directory.Delete(storeRoot, true); }
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
