using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
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
        var models = new ChatTarget[] { new("openai/fixture", "OpenAI fixture") { ModelType = "language" }, new("other/fixture", "Other fixture") { ModelType = "language" },
            new("router/fixture", "Decision fixture") { ModelType = "decision" }, new("router/alternative", "Alternative decision") { ModelType = "decision" },
            new("openai/image", "Image") { ModelType = "image" } };
        var original = DesktopAgent.Parse("""
            {"name":"FixtureAgent","description":"Fixture","instructions":"Be concise","model":{"id":"openai/fixture","providerMetadata":{"future":{"keep":true}}},
             "models":[{"id":"openai/fixture","type":"language","providerMetadata":{"future":{"keep":true}},"providerHeaders":{"X-Fixture":"keep"}},
                       {"id":"retired/fixture","providerMetadata":{"custom":true}}],"owned_by":"Imported team","tags":["saved"],
             "responseFormat":{"type":"json_schema","json_schema":{"name":"result","schema":{"type":"object"}}},
             "plugins":[{"type":"base64","media_type":"application/zip","data":"opaque"}],"future":{"keep":true},
             "skills":[{"type":"skill_reference","skill_id":"provider/missing","version":"pinned","future":7}]}
            """);
        var context = "Agents / " + theme;
        var dialog = new AgentEditDialog(original, true, session, catalog, http, models) { XamlRoot = root.XamlRoot };
        DesktopAgent? saved = null; dialog.SaveAsync = (agent, _) => { saved = agent; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120); dialog.UpdateLayout();
        var tabs = Descendants(dialog).OfType<NavigationView>().Single();
        Check(tabs.MenuItems.Count == 8 && !dialog.IsPrimaryButtonEnabled, context + ": eight native tabs; existing multi-answer agent requires a decision model");
        Check(tabs.MenuItems.OfType<NavigationViewItem>().Take(2).Select(t => t.Tag as string).SequenceEqual(["general", "details"])
            && ((NavigationViewItem)tabs.MenuItems[1]).Content.ToString() == "Details"
            && tabs.MenuItems.OfType<NavigationViewItem>().Single(t => t.Tag as string == "checks").Content.ToString() == "Tests",
            context + ": second tab is Details and evaluations are labeled Tests");
        Check(!Descendants(dialog).OfType<TextBox>().Single(b => b.Name == "AgentName").IsEnabled, context + ": immutable name while editing");
        Check(JsonNode.DeepEquals(dialog.Draft.Definition, original.Definition), context + ": opening leaves full JSON unchanged");
        Check(!Descendants(dialog).OfType<AutoSuggestBox>().Any(b => b.Name is "AgentOwnedBy" or "AgentTagInput"), context + ": General has no owner or tags fields");
        var model = Descendants(dialog).OfType<ChatTargetPicker>().Single(b => b.Name == "AgentModels");
        var decision = Descendants(dialog).OfType<ChatTargetPicker>().Single(b => b.Name == "AgentDecisionModel");
        var decisionField = Descendants(dialog).OfType<StackPanel>().Single(b => b.Name == "AgentDecisionModelField");
        Check(decisionField.Visibility == Visibility.Visible && decision.Selection.Ids.Count == 0
            && AutomationProperties.GetIsRequiredForForm(decision), context + ": required decision picker is visible with no automatic selection");
        Check(AutomationProperties.GetName(model) == "Models" && !Descendants(dialog).OfType<ListView>().Any()
            && !Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == DesktopResources.Get("AgentModelRoutingHint")),
            context + ": Models is a compact dropdown without an inline list or routing help text");
        model.Focus(FocusState.Programmatic); await Task.Delay(80);
        Check(!model.IsSuggestionListOpen, context + ": keyboard focus alone does not expand the model picker");
        InvokeButton(model); await Task.Delay(80);
        var popup = (StackPanel)((Flyout)model.Flyout).Content;
        var choices = Descendants(popup).OfType<ListView>().Single();
        var searchModels = Descendants(popup).OfType<TextBox>().Single(b => b.Name == "TargetSearch");
        var options = ((IEnumerable<ChatTarget>)model.ItemsSource).ToArray();
        Check(model.IsSuggestionListOpen && choices.IsMultiSelectCheckBoxEnabled && choices.SelectionMode == ListViewSelectionMode.Multiple
            && options.Select(t => t.Id).ToHashSet().SetEquals(["openai/fixture", "other/fixture", "retired/fixture"]),
            context + ": click opens checkboxes for language and imported models, excluding decision and image models");
        Check(Descendants(popup).OfType<Button>().Where(b => b.Name is "SelectAllFavorites" or "FavoriteAgentRouter").All(b => b.Visibility == Visibility.Collapsed),
            context + ": agent model picker hides chat-only routing actions");
        choices.SelectedItems.Add(options.Single(t => t.Id == "other/fixture")); await Task.Delay(60);
        model.IsSuggestionListOpen = false; InvokeButton(decision); await Task.Delay(60);
        var decisionChoices = Descendants((StackPanel)((Flyout)decision.Flyout).Content).OfType<ListView>().Single();
        var decisionOptions = ((IEnumerable<ChatTarget>)decision.ItemsSource).ToArray();
        Check(decisionChoices.SelectionMode == ListViewSelectionMode.Single && decisionOptions.All(t => t.ModelType == "decision"),
            context + ": decision picker offers only decision models with single selection");
        decisionChoices.SelectedItem = decisionOptions.Single(t => t.Id == "router/fixture"); await Task.Delay(60);
        InvokeButton(model); await Task.Delay(60);
        Check(dialog.Draft.ModelIds.SequenceEqual(["openai/fixture", "retired/fixture", "other/fixture", "router/fixture"])
            && DesktopAgent.Text(dialog.Draft.Models.Last()["type"]) == "decision" && model.IsSuggestionListOpen,
            context + ": separate answer and decision selections commit into the same ordered collection");
        Check(dialog.IsPrimaryButtonEnabled, context + ": selecting a decision enables Save");
        searchModels.Text = "Other"; await Task.Delay(60);
        Check(choices.Items.OfType<ChatTarget>().Select(t => t.Id).SequenceEqual(["other/fixture"])
            && dialog.Draft.ModelIds.Count == 4, context + ": searching filters rows without discarding hidden selections");
        choices.SelectedItems.Remove(options.Single(t => t.Id == "other/fixture")); await Task.Delay(60);
        searchModels.Text = ""; await Task.Delay(60);
        Check(choices.SelectedItems.OfType<ChatTarget>().Select(t => t.Id).ToHashSet().SetEquals(["openai/fixture", "retired/fixture"]),
            context + ": filtered uncheck changes only that model and restores the other checkmarks");
        model.IsSuggestionListOpen = false; InvokeButton(decision); await Task.Delay(40);
        decisionChoices.SelectedItem = null; await Task.Delay(40);
        Check(!dialog.IsPrimaryButtonEnabled, context + ": clearing the required decision disables Save");
        InvokeButton(model); await Task.Delay(40);
        choices.SelectedItems.Add(options.Single(t => t.Id == "other/fixture")); await Task.Delay(60);
        SelectNativeTab(tabs, "providers"); await Task.Delay(60);
        Check(!model.IsSuggestionListOpen, context + ": changing tabs closes the model flyout");
        var settingsModel = Descendants(dialog).OfType<ComboBox>().Single(b => b.Name == "AgentSettingsModel");
        settingsModel.SelectedItem = "other/fixture"; await Task.Delay(60);
        Check(((NavigationViewItem)tabs.SelectedItem).Content.ToString() == "other", context + ": provider tab follows the selected settings model");
        Descendants(dialog).OfType<ComboBox>().Single(b => b.Name == "AgentSettingsModel").SelectedItem = "openai/fixture"; await Task.Delay(60);
        Check(((NavigationViewItem)tabs.SelectedItem).Content.ToString() == "OpenAI", context + ": provider settings can return to the first model");
        SelectNativeTab(tabs, "general"); await Task.Delay(60);
        InvokeButton(model); await Task.Delay(60);
        choices.SelectedItems.Remove(options.Single(t => t.Id == "other/fixture"));
        Check(dialog.Draft.ModelIds.SequenceEqual(["openai/fixture", "retired/fixture"])
            && JsonNode.DeepEquals(dialog.Draft.Models[0]["providerMetadata"], original.Models[0]["providerMetadata"])
            && JsonNode.DeepEquals(dialog.Draft.Models[0]["providerHeaders"], original.Models[0]["providerHeaders"])
            && JsonNode.DeepEquals(dialog.Draft.Models[1]["providerMetadata"], original.Models[1]["providerMetadata"]),
            context + ": selection and provider-tab changes preserve existing and imported model configuration");
        model.IsSuggestionListOpen = false; InvokeButton(decision); await Task.Delay(40);
        decisionChoices.SelectedItem = decisionOptions.Single(t => t.Id == "router/fixture"); await Task.Delay(40);
        SelectNativeTab(tabs, "details"); await Task.Delay(60); dialog.UpdateLayout();
        var owner = Descendants(dialog).OfType<AutoSuggestBox>().Single(b => b.Name == "AgentOwnedBy");
        var tagInput = Descendants(dialog).OfType<AutoSuggestBox>().Single(b => b.Name == "AgentTagInput");
        Check(owner.Header.ToString() == "Owner" && owner.Text == "Imported team"
            && owner.TransformToVisual(dialog).TransformPoint(new()).Y < tagInput.TransformToVisual(dialog).TransformPoint(new()).Y,
            context + ": Details shows Owner above Tags and retains imported values");
        owner.Text = ""; await Task.Delay(40);
        Check(dialog.Draft.Definition["owned_by"] is null, context + ": clearing Owner removes only the stored ownership value");
        owner.Text = "New team"; tagInput.Text = "new tag"; await Task.Delay(40);
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "AgentTagAdd")); await Task.Delay(40);
        Check(DesktopAgent.Text(dialog.Draft.Definition["owned_by"]) == "New team"
            && (dialog.Draft.Definition["tags"] as JsonArray)!.Select(DesktopAgent.Text).SequenceEqual(["saved", "new tag"]),
            context + ": owner and tags remain editable on Details");
        SelectNativeTab(tabs, "general"); await Task.Delay(40); SelectNativeTab(tabs, "details"); await Task.Delay(40);
        Check(owner.Text == "New team" && Descendants(dialog).OfType<Button>().Count(b => b.Name == "AgentTagRemove") == 2,
            context + ": Details edits persist across tab switches");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "AgentTagRemove" && b.Tag as string == "new tag"));
        Check((dialog.Draft.Definition["tags"] as JsonArray)!.Select(DesktopAgent.Text).SequenceEqual(["saved"]), context + ": Details can remove a tag independently");
        var contextBefore = dialog.Draft.Definition["mcpClient"]?.DeepClone();
        SelectNativeTab(tabs, "modelContext"); await Task.Delay(60); dialog.UpdateLayout();
        var policySection = Descendants(dialog).OfType<SettingsExpander>().Single(e => e.Name == "AgentPolicy");
        Check(!policySection.IsExpanded && JsonNode.DeepEquals(dialog.Draft.Definition["mcpClient"], contextBefore), context + ": policy is initially collapsed without changing draft values");
        policySection.IsExpanded = true; await Task.Delay(80); dialog.UpdateLayout();
        var policy = Descendants(policySection).OfType<ToggleSwitch>().First(t => AutomationProperties.GetName(t) == "Read-only");
        Check(Within(policy, dialog), context + $": policy switch is inside dialog bounds, not clipped beyond right edge (width={policy.ActualWidth}; x={policy.TransformToVisual(dialog).TransformPoint(new Windows.Foundation.Point(0, 0)).X}; dialog={dialog.ActualWidth}; content={((FrameworkElement)dialog.Content).ActualWidth})");
        policy.IsOn = true; Check(DesktopAgent.Boolean(dialog.Draft.Definition["mcpClient"]?["policy"]?["readOnlyHint"]), context + ": native policy switch changes draft");
        foreach (var width in new[] { 500, 1100 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(100); dialog.UpdateLayout();
            Check(Descendants(policySection).OfType<ToggleSwitch>().Count() == 4 && Descendants(policySection).OfType<ToggleSwitch>().All(t => Within(t, policySection)),
                context + $": all four policy switches fit their section at {width}px");
        }
        policySection.IsExpanded = false; await Task.Delay(60); policySection.IsExpanded = true; await Task.Delay(60);
        Check(policy.IsOn, context + ": collapse/reopen retains policy changes");
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
            && JsonNode.DeepEquals(saved.Definition["responseFormat"], original.Definition["responseFormat"])
            && DesktopAgent.Text(saved.Definition["owned_by"]) == "New team" && DesktopAgent.Text(original.Definition["owned_by"]) == "Imported team",
            context + ": Save preserves deferred plugin/schema JSON and commits Details without changing the original");
        var create = new AgentEditDialog(DesktopAgent.Empty(), false, session, catalog, http, models) { XamlRoot = root.XamlRoot };
        SystemAppearance.PrepareDialog(create); shown = create.ShowAsync(); await Task.Delay(100);
        Check(!create.IsPrimaryButtonEnabled && Descendants(create).OfType<TextBox>().Single(b => b.Name == "AgentName").IsEnabled, context + ": Create opens, enables name, disables incomplete Save");
        Descendants(create).OfType<TextBox>().Single(b => b.Name == "AgentName").Text = "NewAgent";
        Descendants(create).OfType<TextBox>().Single(b => b.Name == "AgentDescription").Text = "Description";
        Descendants(create).OfType<TextBox>().Single(b => b.Name == "AgentInstructions").Text = "Instructions";
        var createModel = Descendants(create).OfType<ChatTargetPicker>().Single(b => b.Name == "AgentModels");
        InvokeButton(createModel); await Task.Delay(60);
        var createChoices = Descendants((StackPanel)((Flyout)createModel.Flyout).Content).OfType<ListView>().Single();
        var first = ((IEnumerable<ChatTarget>)createModel.ItemsSource).First();
        createChoices.SelectedItems.Add(first); await Task.Delay(40);
        Check(create.IsPrimaryButtonEnabled, context + ": selecting a model enables Save once required fields are complete");
        var createDecision = Descendants(create).OfType<ChatTargetPicker>().Single(b => b.Name == "AgentDecisionModel");
        var createDecisionField = Descendants(create).OfType<StackPanel>().Single(b => b.Name == "AgentDecisionModelField");
        Check(createDecisionField.Visibility == Visibility.Collapsed, context + ": one answer hides the decision field");
        var second = ((IEnumerable<ChatTarget>)createModel.ItemsSource).Single(t => t.Id == "other/fixture");
        createChoices.SelectedItems.Add(second); await Task.Delay(40);
        Check(!create.IsPrimaryButtonEnabled && createDecisionField.Visibility == Visibility.Visible && createDecision.Selection.Ids.Count == 0,
            context + ": a second answer reveals a required unselected decision field");
        createModel.IsSuggestionListOpen = false; InvokeButton(createDecision); await Task.Delay(40);
        var createDecisionChoices = Descendants((StackPanel)((Flyout)createDecision.Flyout).Content).OfType<ListView>().Single();
        var createDecisionOptions = ((IEnumerable<ChatTarget>)createDecision.ItemsSource).ToArray();
        createDecisionChoices.SelectedItem = createDecisionOptions.Single(t => t.Id == "router/fixture"); await Task.Delay(40);
        Check(create.IsPrimaryButtonEnabled, context + ": two answers and one decision enable Save");
        InvokeButton(createDecision); await Task.Delay(40);
        createDecisionChoices.SelectedItem = createDecisionOptions.Single(t => t.Id == "router/alternative"); await Task.Delay(40);
        Check(create.Draft.ModelIds.SequenceEqual([first.Id, second.Id, "router/alternative"]), context + ": replacing the router retains answers and stores exactly one decision");
        InvokeButton(createModel); await Task.Delay(40);
        createChoices.SelectedItems.Remove(second); await Task.Delay(40);
        Check(create.IsPrimaryButtonEnabled && createDecisionField.Visibility == Visibility.Collapsed
            && createDecision.Selection.Ids.Count == 0 && create.Draft.ModelIds.SequenceEqual([first.Id]),
            context + ": reducing to one answer clears the decision from both UI and saved collection");
        createChoices.SelectedItems.Remove(first); await Task.Delay(40);
        Check(create.Draft.ModelIds.Count == 0 && !create.IsPrimaryButtonEnabled, context + ": clearing the last checkbox empties the draft and disables Save");
        createChoices.SelectedItems.Add(first); await Task.Delay(40);
        Check(create.IsPrimaryButtonEnabled, context + ": selecting again restores a valid draft");
        SelectNativeTab(Descendants(create).OfType<NavigationView>().Single(), "details"); await Task.Delay(40);
        Descendants(create).OfType<AutoSuggestBox>().Single(b => b.Name == "AgentOwnedBy").Text = "Cancelled team";
        InvokeButton(Descendants(create).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(create.Result is null, context + ": Cancel never commits a draft");
        File.WriteAllLines(report, results); window.Content = null; await session.DisposeAsync();
        await CheckAgentHeaderAsync(theme);
    }
    private async Task CheckAgentHeaderAsync(ElementTheme theme)
    {
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme };
        try
        {
            window!.Content = shell; window.Activate(); await Task.Delay(180);
            var add = Field<Button>(shell, "addAgent"); var account = Field<Button>(shell, "account");
            var pageType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
            Call(shell, "ShowPage", Enum.Parse(pageType, "Agents"));
            foreach (var width in new[] { 500, 1100 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(350); shell.UpdateLayout();
                var plus = add.TransformToVisual(shell).TransformPoint(new Windows.Foundation.Point());
                var profile = account.TransformToVisual(shell).TransformPoint(new Windows.Foundation.Point());
                Check(add.Visibility == Visibility.Visible && VisualTreeHelper.GetParent(add) == VisualTreeHelper.GetParent(account)
                    && plus.X + add.ActualWidth <= profile.X && Math.Abs(plus.Y - profile.Y) < 1 && Within(add, shell),
                    $"Agents / {theme}: plus is in top header left of profile at {width}px" + (Within(add, shell) ? "" : AgentGeometry(add, shell)));
            }
            Check(((MenuFlyout)add.Flyout).Items.OfType<MenuFlyoutItem>().Select(i => i.Text).SequenceEqual(new[] { "Create new agent", "Import agent" }),
                $"Agents / {theme}: header plus retains Create/Import menu");
            Check(!Descendants(Field<UserControl>(shell, "agentsOverview")).OfType<Button>().Any(b => b.Name == "AddAgent"), $"Agents / {theme}: no duplicate plus in overview");
            Call(shell, "SetOverviewBusy", true); Check(!add.IsEnabled, $"Agents / {theme}: header plus disabled while busy");
            Call(shell, "SetOverviewBusy", false); Check(add.IsEnabled, $"Agents / {theme}: header plus enabled when idle");
            foreach (var page in new[] { "Chat", "Skills", "Models", "Mcp", "Images", "Videos", "Transcriptions" })
            {
                Call(shell, "ShowPage", Enum.Parse(pageType, page));
                Check(add.Visibility == Visibility.Collapsed, $"Agents / {theme}: header plus hidden on {page}");
            }
            Call(shell, "ShowPage", Enum.Parse(pageType, "Chat"));
            await CheckAgentPresentationAsync(shell, session, $"Agent presentation / {theme}");
        }
        finally { await shell.ShutdownAsync(); window!.Content = null; }
    }

    private async Task CheckAgentPresentationAsync(ChatShell shell, DesktopSession session, string context)
    {
        var catalog = DesktopAgentTargets.Project([
            new CatalogItem(CatalogKind.Agent, "backend-id", "OpenAIAgent", "") { Model = "openai/fixture" },
            new CatalogItem(CatalogKind.Agent, "OpenAIAgent", "OpenAIAgent", "") { Origin = CatalogOrigin.Local, Model = "echo/fixture" }]);
        typeof(ChatShell).GetField("targets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, catalog);
        Call(shell, "UpdateMode", ServiceKind.Agents);
        var selected = typeof(ChatShell).GetProperty("TargetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var picker = Field<AutoSuggestBox>(shell, "target");
        selected.SetValue(shell, "remote:backend-id"); Call(shell, "UpdateTargetSuggestions", "");
        Check(picker.Text == "OpenAIAgent" && (string)selected.GetValue(shell)! == "remote:backend-id"
            && ((IEnumerable<ChatTarget>)picker.ItemsSource).Count() == 2,
            context + ": remote picker displays the name while preserving its distinct routing key");
        selected.SetValue(shell, "local:OpenAIAgent");
        Check(picker.Text == catalog.Single(t => t.LocalAgentName is not null).Label
            && (string)selected.GetValue(shell)! == "local:OpenAIAgent",
            context + ": same-name local selection retains its own identity and readable local qualifier");
        selected.SetValue(shell, "backend-id");
        Check(picker.Text == "OpenAIAgent" && (string)selected.GetValue(shell)! == "remote:backend-id",
            context + ": restoring a legacy backend ID shows its catalog name, not the key");
        picker.Text = "not-an-agent";
        Check((string)selected.GetValue(shell)! == "not-an-agent", context + ": editing the label cannot send to the stale selected agent");
        selected.SetValue(shell, "remote:deleted-agent");
        Check(picker.Text == "deleted-agent" && (string)selected.GetValue(shell)! == "remote:deleted-agent",
            context + ": missing historical selections stay unavailable without exposing the prefix");
        selected.SetValue(shell, "remote:backend-id");

        var output = new ConversationMessage { Status = "streaming", Message = new UIMessage { Role = Role.assistant,
            Metadata = new() { ["model"] = "remote:backend-id", ["agentModel"] = "openai/fixture" },
            Parts = [new ReasoningUIPart { Text = "Reasoning fixture" }, new TextUIPart { Text = "Partial answer" }] } };
        var conversation = new Conversation { Service = ServiceKind.Agents, Target = "remote:backend-id", Messages = [
            new() { Message = new UIMessage { Role = Role.user, Parts = [new TextUIPart { Text = "Test" }] } }, output] };
        typeof(ChatShell).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, conversation);
        var transcript = Field<StackPanel>(shell, "transcript");
        string[] Labels() => Descendants(transcript).OfType<TextBlock>().Where(t => t.Name == "MessageModel").Select(t => t.Text).ToArray();
        Grid[] Logos() => Descendants(transcript).OfType<Grid>().Where(g => g.Name == "ProviderLogo").ToArray();
        session.Settings.Chat.ShowProviderLogo = true;
        foreach (var status in new[] { "streaming", "interrupted", "complete" })
        {
            output.Status = status;
            if (status == "complete") output.Message.Metadata!["model"] = "OpenAIAgent";
            Call(shell, "RenderTranscript"); await Task.Delay(70); shell.UpdateLayout();
            Check(Labels().Length == 2 && Labels().All(l => l == "OpenAIAgent") && Logos().Length == 2,
                context + ": answer and activity headers have clean names and known provider logos when " + status);
        }
        var before = PortableConversations.Write(conversation);
        session.Settings.Chat.ShowProviderLogo = false;
        Call(shell, "RenderTranscript"); await Task.Delay(70);
        Check(Logos().Length == 0 && Labels().All(l => l == "OpenAIAgent") && PortableConversations.Write(conversation) == before,
            context + ": disabling logos keeps clean agent names and does not mutate saved metadata");
        session.Settings.Chat.ShowProviderLogo = true;
        output.Message.Metadata!["providerKey"] = "unknown-provider";
        Call(shell, "RenderTranscript"); await Task.Delay(70);
        Check(Logos().Length == 0 && Labels().All(l => l == "OpenAIAgent"),
            context + ": unknown explicit provider metadata does not borrow a catalog logo");
        output.Message.Metadata!["providerKey"] = "echo";
        output.Message.Metadata!["model"] = "openai/AgentWithSlash";
        Call(shell, "RenderTranscript"); await Task.Delay(150);
        Check(Logos().Length == 2 && Labels().All(l => l == "openai/AgentWithSlash")
            && Logos().All(l => l.Children.OfType<Image>().Single().Visibility == Visibility.Visible),
            context + ": an explicit provider logo renders without interpreting or shortening the agent's own name");
        output.Message.Metadata!.Clear(); output.Message.Metadata["model"] = "openai/AgentWithSlash";
        Call(shell, "RenderTranscript"); await Task.Delay(70);
        Check(Logos().Length == 0 && Labels().All(l => l == "openai/AgentWithSlash"),
            context + ": provider-looking agent names alone never imply a logo");
        Call(shell, "UpdateMode", ServiceKind.Ai); selected.SetValue(shell, "openai/fixture");
        Check(picker.Text == "openai/fixture" && (string)selected.GetValue(shell)! == "openai/fixture",
            context + ": normal chat model picker retains its full model ID");
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
