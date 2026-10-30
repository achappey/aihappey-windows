using System.Reflection;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    // Traverse the owned panels, not control templates: collapsed expander details must
    // be testable too, and their validation must never depend on template realization.
    private static IEnumerable<DependencyObject> ProviderControls(DependencyObject view)
    {
        yield return view;
        IEnumerable<DependencyObject> children = view switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            SettingsExpander expander => new[] { expander.Content, expander.ItemsHeader }.OfType<DependencyObject>(),
            SettingsCard card => new[] { card.Content }.OfType<DependencyObject>(),
            _ => []
        };
        foreach (var child in children) foreach (var item in ProviderControls(child)) yield return item;
    }
    private static object ProviderForm(ChatSettingsDialog dialog) => typeof(ChatSettingsDialog).GetField("providerForm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
    private static FrameworkElement ProviderView(object form) => (FrameworkElement)form.GetType().GetProperty("View")!.GetValue(form)!;
    private static bool ProviderValid(object form) => (bool)form.GetType().GetProperty("IsValid")!.GetValue(form)!;
    private static string ProviderLabel(string key) => DesktopResources.Get("ChatForm_" + key.Replace('.', '_'));
    private static NavigationViewItem SelectChatSettingsTab(ChatSettingsDialog dialog, string name)
    {
        var navigation = Field<NavigationView>(dialog, "tabs");
        var tab = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Name == name);
        navigation.SelectedItem = tab; return tab;
    }
    private static void Choose(ComboBox control, string value) => control.SelectedItem = control.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == value);
    private async Task CheckNativeChatProvidersAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root; window.Activate(); await Task.Delay(80);
        var scale = root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200 * scale), (int)(900 * scale))); await Task.Delay(80);
        foreach (var provider in new[] { "deepseek", "anthropic" })
        {
            var context = $"Native provider {provider} / {theme}";
            var original = new ChatPreferences(); original.ProviderMetadata["anthropic"]["future"] = new JsonObject { ["keep"] = true };
            var originalJson = original.ProviderMetadata["anthropic"].ToJsonString(); ChatPreferences? saved = null;
            var dialog = new ChatSettingsDialog(original, provider) { XamlRoot = root.XamlRoot, RequestedTheme = theme,
                SaveAsync = next => { saved = next; return Task.CompletedTask; } };
            SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(100);
            var tab = SelectChatSettingsTab(dialog, "ChatProviderTab"); await Task.Delay(80);
            Check((string)tab.Content == (provider == "deepseek" ? "DeepSeek" : "Anthropic") && ReferenceEquals(Field<NavigationView>(dialog, "tabs").SelectedItem, tab), context + ": branded native provider tab");
            var form = ProviderForm(dialog); var view = ProviderView(form);
            var controls = ProviderControls(view).ToArray();
            Check(ProviderValid(form), context + ": initial default form is valid");
            Check(controls.OfType<TextBox>().All(c => c.Header is string { Length: > 0 }) && controls.OfType<ComboBox>().All(c => c.Header is string { Length: > 0 })
                && controls.OfType<ToggleSwitch>().All(c => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(c))), context + ": every native input has a localized accessible label");
            foreach (var expander in controls.OfType<SettingsExpander>()) expander.IsExpanded = true;
            dialog.UpdateLayout(); await Task.Delay(80);
            if (provider == "deepseek")
            {
                Check(controls.OfType<SettingsExpander>().Count() == 1 && controls.OfType<SettingsCard>().Count() == 1, context + ": reasoning expander and nonempty toggle-only search card");
                var reasoning = controls.OfType<ToggleSwitch>().Single(t => t.Name == "DeepSeek_reasoning_Enabled");
                var effort = controls.OfType<ComboBox>().Single();
                Check(reasoning.IsOn && (string)((ComboBoxItem)effort.SelectedItem).Tag == "high", context + ": implicit high reasoning default");
                reasoning.IsOn = false; Check(!effort.IsEnabled, context + ": reasoning toggle disables effort");
                reasoning.IsOn = true; Choose(effort, "max");
                controls.OfType<ToggleSwitch>().Single(t => t.Name == "DeepSeek_web_search_Enabled").IsOn = true;
            }
            else await ExerciseAnthropicFormAsync(dialog, form, view, context);
            Check(original.ProviderMetadata["anthropic"].ToJsonString() == originalJson && !original.ProviderMetadata.ContainsKey("deepseek"), context + ": all controls edit isolated drafts");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(500 * scale), (int)(900 * scale))); await Task.Delay(120); dialog.UpdateLayout();
            var page = (ScrollViewer)typeof(ChatSettingsDialog).GetField("page", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            Check(page.ScrollableWidth < 1 && view.ActualWidth <= page.ViewportWidth + 1, context + ": expanded native form fits narrow viewport without horizontal scrolling");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200 * scale), (int)(900 * scale))); await Task.Delay(80);
            InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
            Check(saved is not null && dialog.Result is not null, context + ": close commits through existing save boundary");
            if (provider == "deepseek")
                Check(saved!.ProviderMetadata[provider]["reasoning"]!["effort"]!.GetValue<string>() == "max"
                    && saved.ProviderMetadata[provider]["tools"]!.AsArray().Any(n => n?["type"]?.GetValue<string>() == "web_search"), context + ": DeepSeek controls save native fields");
            else Check(saved!.ProviderMetadata[provider]["future"]!["keep"]!.GetValue<bool>() && !saved.ProviderMetadata[provider].ContainsKey("web_search"), context + ": unknown configuration survives and UI aliases never persist");
            var reopen = new ChatSettingsDialog(saved!, provider) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
            shown = reopen.ShowAsync(); await Task.Delay(80); SelectChatSettingsTab(reopen, "ChatProviderTab"); await Task.Delay(60);
            Check(ProviderValid(ProviderForm(reopen)), context + ": saved provider form reopens valid");
            InvokeButton(Descendants(reopen).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(80);
            InvokeButton(Descendants(reopen).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
            Check(reopen.Result!.ProviderHeaders.Count == 0 && (provider == "deepseek" ? JsonNode.DeepEquals(reopen.Result.ProviderMetadata["deepseek"], new JsonObject())
                : JsonNode.DeepEquals(reopen.Result.ProviderMetadata["anthropic"], AnthropicChatConfig.Defaults())), context + ": restore defaults rebuilds and commits provider defaults");
        }
        // Registration must remain an additive extension, including case-insensitive lookup.
        foreach (var provider in new[] { "OPENAI", "ANTHROPIC", "DEEPSEEK" })
        {
            var dialog = new ChatSettingsDialog(new(), provider) { XamlRoot = root.XamlRoot };
            Check(ProviderForm(dialog) is not null, "Native provider factory: case-insensitive " + provider);
        }
    }
    private async Task ExerciseAnthropicFormAsync(ChatSettingsDialog dialog, object form, FrameworkElement view, string context)
    {
        var model = (AnthropicChatConfig)form.GetType().GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        DependencyObject[] All() => ProviderControls(view).ToArray();
        SettingsExpander Section(string name) => All().OfType<SettingsExpander>().Single(e => e.Name == "Anthropic_" + name);
        DependencyObject[] Body(string name) => ProviderControls((DependencyObject)Section(name).ItemsHeader).ToArray();
        ToggleSwitch Header(string name) => All().OfType<ToggleSwitch>().Single(t => t.Name == "Anthropic_" + name + "_Enabled");
        TextBox Input(string section, string label) => Body(section).OfType<TextBox>().Single(t => (string)t.Header == ProviderLabel(label));
        ComboBox Select(string section, string label) => Body(section).OfType<ComboBox>().Single(t => (string)t.Header == ProviderLabel(label));
        async Task EditTextAsync(TextBox box, string text)
        {
            box.Focus(FocusState.Programmatic); box.Text = text;
            // WinUI can defer TextChanged; observe validation after the native event.
            await Task.Delay(60);
        }
        Check(All().OfType<SettingsExpander>().Count() == 18 && All().OfType<SettingsCard>().Count() == 2, context + ": all nineteen cards plus maximum tokens are native");
        var maxTokens = All().OfType<TextBox>().Single(t => (string)t.Header == ProviderLabel("maxOutputTokens"));
        await EditTextAsync(maxTokens, "0"); Check(!ProviderValid(form), context + ": required token bound rejects zero");
        await EditTextAsync(maxTokens, "64000"); Check(ProviderValid(form), context + ": correcting tokens clears validation");
        Header("thinking").IsOn = false; Header("thinking").IsOn = true;
        var mode = Select("thinking", "mode"); Choose(mode, "enabled");
        var budget = Input("thinking", "budget"); await EditTextAsync(budget, "100");
        Check(!ProviderValid(form), context + ": thinking budget enforces minimum");
        Section("thinking").IsExpanded = false;
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await Task.Delay(100);
        Check(dialog.Result is null && Section("thinking").IsExpanded, context + ": invalid close keeps draft and expands invalid section");
        Choose(mode, "adaptive"); Check(ProviderValid(form) && !budget.IsEnabled, context + ": hidden enabled-only budget cannot block adaptive mode");
        Choose(mode, "enabled"); Check(!ProviderValid(form), context + ": invalid re-enabled budget is validated again"); await EditTextAsync(budget, "4096");
        Header("output_config").IsOn = true;
        var taskBudget = Input("output_config", "anthropic.outputConfig.taskBudget"); await EditTextAsync(taskBudget, "19999");
        Check(!ProviderValid(form), context + ": task budget uses chat form minimum"); await EditTextAsync(taskBudget, "25000");
        Header("container").IsOn = true;
        await EditTextAsync(Input("container", "anthropic.containerSettings.id"), " container-ui ");
        await EditTextAsync(Input("container", "anthropic.containerSettings.customSkillIds"), " custom-ui, custom-ui ");
        Body("container").OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == ProviderLabel("anthropic.containerSettings.skills.pdf")).IsOn = true;
        Check(model.ContainerId == "container-ui" && model.ContainerSkills("custom").SequenceEqual(["custom-ui"]), context + ": native container ID and skill normalization");
        foreach (var tool in AnthropicChatConfig.ToolTypes)
        {
            Header(tool).IsOn = true;
            Check(model.Enabled(tool), context + ": enables " + tool);
            if (tool != "advisor")
            {
                var strict = Body(tool).OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == ProviderLabel("anthropic.strictValidation")); strict.IsOn = true;
                Check(model.Boolean(tool + "/strict"), context + ": shared strict option " + tool);
                var versions = Select(tool, "anthropic.version");
                foreach (var version in AnthropicChatConfig.Versions(tool)) Choose(versions, version);
                Check(model.String(tool + "/type") == AnthropicChatConfig.Versions(tool)[^1], context + ": all current version choices " + tool);
            }
        }
        var examples = Input("bash", "anthropic.inputExamples"); await EditTextAsync(examples, "{broken");
        Check(!ProviderValid(form), context + $": malformed optional JSON retained and blocks saving (text={examples.Text}, enabled={examples.IsEnabled})");
        Header("bash").IsOn = false; Check(ProviderValid(form), context + ": disabling tool clears inactive JSON error");
        Header("bash").IsOn = true; await EditTextAsync(examples, "[]"); Check(ProviderValid(form), context + ": optional JSON correction restores validity");
        await EditTextAsync(examples, ""); Check(model.Get("bash/input_examples") is null, context + ": clearing JSON omits field instead of saving an empty object");
        Header("budget").IsOn = true; Check(!ProviderValid(form), context + ": newly enabled session budget requires positive USD amount");
        var money = Input("budget", "anthropic.session.maxListCostUsd"); await EditTextAsync(money, "12345678901234567890.99");
        Check(model.String("budget/max_list_cost/amount") == "1234567890123456789099" && ProviderValid(form),
            context + $": large native USD input serializes exact string cents (text={money.Text}, cents={model.String("budget/max_list_cost/amount")}, enabled={money.IsEnabled})");
        Header("speed").IsOn = true; Choose(Select("speed", "anthropic.other.speed.title"), "fast");
        Choose(Select("other", "anthropic.other.serviceTier.title"), "standard_only"); Choose(Select("other", "anthropic.other.inferenceGeo.title"), "us");
        var contextSection = Section("context_management"); contextSection.IsExpanded = true;
        void ActionButton(string key) => InvokeButton(Body("context_management").OfType<Button>().Last(b => (string)b.Content == ProviderLabel(key)));
        ActionButton("anthropic.contextManagement.add");
        Check(model.HasContextEdits && !Header("compaction").IsEnabled && model.BetaValues.Contains(AnthropicChatConfig.ContextBeta), context + ": context edits disable summarize compaction and ensure beta");
        var type = Select("context_management", "anthropic.contextManagement.type"); Choose(type, "clear_thinking_20251015");
        Choose(Select("context_management", "anthropic.contextManagement.keep"), "all_literal"); ActionButton("save");
        ActionButton("anthropic.contextManagement.add"); Choose(Select("context_management", "anthropic.contextManagement.type"), "compact_20260112");
        await EditTextAsync(Input("context_management", "anthropic.contextManagement.instructions"), " preserve facts "); ActionButton("save");
        Check((model.Get("context_management/edits") as JsonArray)?.Count == 2, context + ": context editor saves each type without replacing unrelated entries");
        ActionButton("up");
        Check(ProviderChatConfig.Text(model.Get("context_management/edits")?[0]?["type"]) == "compact_20260112", context + ": native context reorder preserves payload order");
        ActionButton("edit"); ActionButton("cancel"); ActionButton("delete"); ActionButton("delete");
        Check(!model.HasContextEdits && Header("compaction").IsEnabled && ProviderValid(form), context + ": context cancel/delete removes inactive editor validation");
        Header("advisor").IsOn = false;
        Check(!model.BetaValues.Contains(AnthropicChatConfig.AdvisorBeta), context + ": auto-added advisor beta is removed with tool");
    }
}
