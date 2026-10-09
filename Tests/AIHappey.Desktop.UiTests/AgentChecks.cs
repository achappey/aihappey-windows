using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckAgentsAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate(); await Task.Delay(100);
        var session = new DesktopSession(new AgentUiHost(), new AgentUiRuntime(), new());
        using var handler = new AgentUiHandler(); using var http = new HttpClient(handler);
        var catalog = new DesktopCatalogClient(new DesktopChatClient(session, http), http);
        var models = new ChatTarget[] { new("openai/fixture", "OpenAI fixture") { ModelType = "language" }, new("other/fixture", "Other fixture") { ModelType = "language" }, new("openai/image", "Image") { ModelType = "image" } };
        var original = DesktopAgent.Parse("""
            {"name":"FixtureAgent","description":"Fixture","instructions":"Be concise","model":{"id":"openai/fixture","providerMetadata":{"future":{"keep":true}}},
             "responseFormat":{"type":"json_schema","json_schema":{"name":"result","schema":{"type":"object"}}},
             "plugins":[{"type":"base64","media_type":"application/zip","data":"opaque"}],"future":{"keep":true},
             "skills":[{"type":"skill_reference","skill_id":"provider/missing","version":"pinned","future":7}]}
            """);
        var context = "Agents / " + theme;
        var dialog = new AgentEditDialog(original, true, session, catalog, http, models) { XamlRoot = root.XamlRoot };
        DesktopAgent? saved = null; dialog.SaveAsync = (agent, _) => { saved = agent; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.UpdateLayout();
        var tabs = Descendants(dialog).OfType<NavigationView>().Single();
        Check(tabs.MenuItems.Count == 6 && dialog.IsPrimaryButtonEnabled, context + ": six native tabs; editing valid agent opens without missing resources");
        Check(!Descendants(dialog).OfType<TextBox>().Single(b => b.Name == "AgentName").IsEnabled, context + ": immutable name while editing");
        Check(JsonNode.DeepEquals(dialog.Draft.Definition, original.Definition), context + ": opening leaves full JSON unchanged");
        var model = Descendants(dialog).OfType<AutoSuggestBox>().Single(b => b.Name == "AgentModel");
        model.Focus(FocusState.Programmatic); await Task.Delay(80);
        Check(model.IsSuggestionListOpen && ((IEnumerable<ChatTarget>)model.ItemsSource).Count() == 2, context + ": model picker opens on focus with language models only");
        model.Text = "other/fixture"; Descendants(dialog).OfType<TextBox>().Single(b => b.Name == "AgentDescription").Focus(FocusState.Programmatic); await Task.Delay(80);
        Check(dialog.Draft.ModelId == "other/fixture" && ((NavigationViewItem)tabs.MenuItems.Last()).Content.ToString() == "other", context + ": model commits on focus change and binds provider tab");
        model.Text = "openai/fixture"; model.Focus(FocusState.Programmatic);
        Descendants(dialog).OfType<TextBox>().Single(b => b.Name == "AgentDescription").Focus(FocusState.Programmatic); await Task.Delay(80);
        Check(dialog.Draft.ModelId == "openai/fixture", context + $": model can return to OpenAI without losing provider binding (draft={dialog.Draft.ModelId}; picker={model.Text})");
        SelectNativeTab(tabs, "modelContext"); await Task.Delay(60); dialog.UpdateLayout();
        var policy = Descendants(dialog).OfType<ToggleSwitch>().First(t => AutomationProperties.GetName(t) == "Read-only tools");
        Check(Within(policy, dialog), context + $": policy switch is inside dialog bounds, not clipped beyond right edge (width={policy.ActualWidth}; x={policy.TransformToVisual(dialog).TransformPoint(new Windows.Foundation.Point(0, 0)).X}; dialog={dialog.ActualWidth}; content={((FrameworkElement)dialog.Content).ActualWidth})");
        policy.IsOn = true; Check(DesktopAgent.Boolean(dialog.Draft.Definition["mcpClient"]?["policy"]?["readOnlyHint"]), context + ": native policy switch changes draft");
        var elicitation = Descendants(dialog).OfType<SettingsExpander>().Single(e => e.Name == "AgentElicitation");
        elicitation.IsExpanded = true; await Task.Delay(60);
        var elicitToggle = Descendants(elicitation).OfType<ToggleSwitch>().Single(t => t.Name == "CardEnabledSwitch"); elicitToggle.IsOn = true;
        var form = Descendants(elicitation).OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == "Forms"); form.IsOn = true;
        Check(dialog.Draft.Definition["mcpClient"]?["capabilities"]?["elicitation"]?["form"] is JsonObject, context + ": native elicitation parent/form toggles work");
        SelectNativeTab(tabs, "checks"); await Task.Delay(60);
        var nonempty = Descendants(dialog).OfType<SettingsExpander>().Single(e => e.Name == "AgentCheck_nonEmpty");
        var checkToggle = Descendants(nonempty).OfType<ToggleSwitch>().Single();
        Check(Within(checkToggle, dialog), context + ": Checks header switch stays visible" + (Within(checkToggle, dialog) ? "" : AgentGeometry(checkToggle, dialog))); checkToggle.IsOn = true; await Task.Delay(60);
        Check(nonempty.IsExpanded && dialog.Draft.Definition["evaluations"]?["localEvaluator"]?["nonEmpty"]?["minLength"]?.ToJsonString() == "1", context + ": enabling check expands native details and writes browser check shape");
        SelectNativeTab(tabs, "tools"); await Task.Delay(60);
        var tool = Descendants(dialog).OfType<ToggleSwitch>().First(); tool.IsOn = true;
        Check((dialog.Draft.Definition["tools"] as JsonArray)?.Any(t => DesktopAgent.Text(t?["type"]) == "tool_search") == true, context + ": native Tools toggle edits supported local tool");
        SelectNativeTab(tabs, "skills"); await Task.Delay(180); dialog.UpdateLayout();
        Check(handler.SkillLists == 1 && Descendants(dialog).OfType<TextBox>().Any(b => b.Name == "AgentSkillSearch"), context + ": Skills catalog loads automatically with one search field");
        var skill = Descendants(dialog).OfType<ToggleSwitch>().Single(t => t.Name == "AgentSkillEnabled" && t.Tag as string == "provider/sample"); skill.IsOn = true; await Task.Delay(80);
        Check((dialog.Draft.Definition["skills"] as JsonArray)?.Any(s => DesktopAgent.Text(s?["skill_id"]) == "provider/sample" && DesktopAgent.Text(s?["version"]) == "latest") == true,
            context + ": enabling catalog skill stores reference/latest without an add form");
        var row = Descendants(dialog).OfType<SettingsExpander>().Single(e => e.Tag as string == "provider/sample"); row.IsExpanded = true; await Task.Delay(100);
        var mode = Descendants(row).OfType<ComboBox>().Single(b => b.Name == "AgentSkillMode"); mode.SelectedIndex = 1; await Task.Delay(160);
        Check((dialog.Draft.Definition["skills"] as JsonArray)?.Any(s => DesktopAgent.Text(s?["type"]) == "inline" && DesktopAgent.Text(s?["source"]?["media_type"]) == "application/zip") == true,
            context + ": inline mode downloads a portable archive snapshot");
        var search = Descendants(dialog).OfType<TextBox>().Single(b => b.Name == "AgentSkillSearch"); search.Text = "Missing"; await Task.Delay(60);
        Check(Descendants(dialog).OfType<SettingsExpander>().Count(e => e.Name == "AgentSkillRow" && e.Visibility == Visibility.Visible) == 1, context + ": search retains unresolved imported skill row");
        search.Text = "";
        await Task.Delay(120);
        SelectNativeTab(tabs, "tools"); await Task.Delay(60);
        foreach (var width in new[] { 500, 1100 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900));
            await Task.Delay(100); dialog.UpdateLayout();
            Check(Descendants(dialog).OfType<ScrollViewer>().All(v => v.ScrollableWidth < 1) && Descendants(dialog).OfType<ToggleSwitch>().All(t => Within(t, dialog)),
                context + $": {width}px native dialog fits toggle controls with no horizontal clipping");
        }
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(saved is not null && original.Definition["tools"] is null && JsonNode.DeepEquals(saved.Definition["plugins"], original.Definition["plugins"])
            && JsonNode.DeepEquals(saved.Definition["responseFormat"], original.Definition["responseFormat"]), context + ": Save preserves deferred plugin/schema JSON and original draft isolation");
        var create = new AgentEditDialog(DesktopAgent.Empty(), false, session, catalog, http, models) { XamlRoot = root.XamlRoot };
        SystemAppearance.PrepareDialog(create); shown = create.ShowAsync(); await Task.Delay(100);
        Check(!create.IsPrimaryButtonEnabled && Descendants(create).OfType<TextBox>().Single(b => b.Name == "AgentName").IsEnabled, context + ": Create opens, enables name, disables incomplete Save");
        InvokeButton(Descendants(create).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(create.Result is null, context + ": Cancel never commits a draft");
        File.WriteAllLines(report, results); window.Content = null; await session.DisposeAsync();
    }
    private static bool Within(FrameworkElement control, FrameworkElement surface)
    {
        var point = control.TransformToVisual(surface).TransformPoint(new Windows.Foundation.Point(0, 0));
        return control.ActualWidth > 0 && point.X >= -1 && point.X + control.ActualWidth <= surface.ActualWidth + 1;
    }
    private static string AgentGeometry(FrameworkElement control, FrameworkElement surface)
    {
        var parts = new List<string>();
        for (DependencyObject? current = control; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement element)
                parts.Add($"{element.GetType().Name}/{element.Name}:w={element.ActualWidth},requested={element.Width},x={element.TransformToVisual(surface).TransformPoint(new Windows.Foundation.Point(0, 0)).X}");
        return string.Join("; ", parts);
    }
    private sealed class AgentUiHandler : HttpMessageHandler
    {
        public int SkillLists;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); HttpContent content;
            if (request.RequestUri!.AbsolutePath.EndsWith("/content"))
            {
                using var output = new MemoryStream();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(zip.CreateEntry("SKILL.md").Open())) writer.Write("---\nname: sample-skill\ndescription: Sample description\nversion: 2\n---\nInstructions");
                content = new ByteArrayContent(output.ToArray()); content.Headers.ContentType = new("application/zip");
            }
            else
            {
                var versions = request.RequestUri.AbsolutePath.EndsWith("/versions"); if (!versions) SkillLists++;
                content = new StringContent(versions ? """{"data":[{"id":"version-2","version":"2"}],"has_more":false}"""
                    : """{"data":[{"id":"provider/sample","name":"Sample skill","description":"Sample description","default_version":"1","latest_version":"2"},{"id":"provider/other","name":"Other skill","description":"Other description","default_version":"1"}],"has_more":false}""", Encoding.UTF8, "application/json");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class AgentUiHost : IDesktopHost
    {
        public string ProfileId => "agent-ui-fixture";
        public bool AllowLocal => true;
        public string AccountLabel => "Agent fixture";
        public string HistoryIdentity => "fixture";
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => Task.CompletedTask;
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class AgentUiRuntime : IRuntimeResolver
    {
        public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken ct) => Task.FromResult(new Uri("https://agent-fixture.invalid/"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
