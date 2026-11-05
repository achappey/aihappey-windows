using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class AgentEditDialog
{
    private sealed class ProviderEditor(AgentProviderSettings settings, IChatProviderForm? form, ChatPreferences preferences)
    {
        public AgentProviderSettings Settings { get; } = settings;
        public IChatProviderForm? Form { get; } = form;
        public ChatSettingsFields Fields { get; } = new();
        public string Key => "provider-" + Settings.Provider;
        public StackPanel View { get; } = Panel();
        public Action SelectModel { get; set; } = () => { };
        public bool IsValid => Fields.IsValid && Form?.IsValid != false;
        public (JsonObject Metadata, JsonObject Headers) Read()
        {
            if (Form is null) return (Settings.Metadata, Settings.Headers);
            Form.Commit(preferences);
            return (preferences.ProviderMetadata.GetValueOrDefault(Settings.Provider) ?? new(),
                JsonSerializer.SerializeToNode(preferences.ProviderHeaders.GetValueOrDefault(Settings.Provider) ?? [])!.AsObject());
        }
        public void Commit() { var (metadata, headers) = Read(); Settings.Commit(metadata, headers); }
    }

    private readonly List<ProviderEditor> providerEditors = [];
    private readonly Dictionary<string, string> selectedProviderModels = new(StringComparer.OrdinalIgnoreCase);

    private void BuildProviders()
    {
        var selected = (tabs.SelectedItem as NavigationViewItem)?.Tag as string;
        foreach (var key in providerEditors.Select(editor => editor.Key).Distinct())
        {
            var old = tabs.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == key);
            if (old is not null) tabs.MenuItems.Remove(old);
            pages.Remove(key);
        }
        providerEditors.Clear();
        foreach (var settings in AgentProviderSettings.ForModels(draft))
        {
            var preferences = new ChatPreferences();
            preferences.ProviderMetadata[settings.Provider] = (JsonObject)settings.Metadata.DeepClone();
            preferences.ProviderHeaders[settings.Provider] = settings.Headers.Where(pair => pair.Value is JsonValue value && value.TryGetValue<string>(out _))
                .ToDictionary(pair => pair.Key, pair => DesktopAgent.Text(pair.Value), StringComparer.OrdinalIgnoreCase);
            // Decision-only provider configurations are not language-model configurations.
            var form = ModelType(settings.ModelId) != "decision"
                ? ChatProviderForms.Create(settings.Provider, preferences) : null;
            var editor = new ProviderEditor(settings, form, preferences);
            var panel = editor.View; panel.Name = "AgentModel_" + settings.ModelId.Replace('/', '_');
            AutomationProperties.SetAutomationId(panel, "AgentModel_" + settings.ModelId);
            if (form is not null) panel.Children.Add(form.View);
            else
            {
                editor.Fields.Json(panel, "providerMetadata", () => settings.Metadata, node => settings.Metadata = node.AsObject()).Header = DesktopResources.Get("AgentProviderMetadata");
                editor.Fields.Json(panel, "providerHeaders", () => settings.Headers, node => settings.Headers = node.AsObject()).Header = DesktopResources.Get("AgentProviderHeaders");
            }
            var (metadata, headers) = editor.Read(); settings.Baseline(metadata, headers);
            providerEditors.Add(editor);
            editor.Fields.Watch(UpdateSave); form?.Watch(UpdateSave);
        }
        foreach (var group in providerEditors.GroupBy(editor => editor.Settings.Provider))
        {
            var editors = group.ToArray();
            FrameworkElement view = editors[0].View;
            if (editors.Length > 1)
            {
                var grid = new Grid { ColumnSpacing = 12, Name = "AgentProvider_" + group.Key };
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 200 });
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
                var models = new ListView { Name = "AgentProviderModels_" + group.Key, SelectionMode = ListViewSelectionMode.Single,
                    IsItemClickEnabled = false, VerticalAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(models, DesktopResources.Get("AgentModels"));
                AutomationProperties.SetAutomationId(models, models.Name);
                var content = new Grid(); Grid.SetColumn(content, 1);
                grid.Children.Add(models); grid.Children.Add(content);
                foreach (var editor in editors)
                {
                    var label = editor.Settings.ModelId[(editor.Settings.ModelId.IndexOf('/') + 1)..];
                    var item = new ListViewItem { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Tag = editor };
                    AutomationProperties.SetName(item, label);
                    AutomationProperties.SetAutomationId(item, "AgentModelTab_" + editor.Settings.ModelId);
                    models.Items.Add(item);
                    content.Children.Add(editor.View);
                    editor.SelectModel = () => models.SelectedItem = item;
                }
                models.SelectionChanged += (_, _) =>
                {
                    if (models.SelectedItem is not ListViewItem { Tag: ProviderEditor active }) return;
                    selectedProviderModels[group.Key] = active.Settings.ModelId;
                    foreach (var editor in editors) editor.View.Visibility = ReferenceEquals(editor, active) ? Visibility.Visible : Visibility.Collapsed;
                    page.ChangeView(null, 0, null, true);
                };
                (editors.FirstOrDefault(editor => editor.Settings.ModelId == selectedProviderModels.GetValueOrDefault(group.Key)) ?? editors[0]).SelectModel();
                view = grid;
            }
            AddTab(editors[0].Key, group.Key.Length > 0 ? ProviderCatalog.Get(group.Key).Name : DesktopResources.Get("AgentProvider"), view);
        }
        if (selected?.StartsWith("provider-", StringComparison.Ordinal) == true)
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == selected) ?? tabs.MenuItems[0];
        UpdateSave();
    }

    private bool CommitProviders()
    {
        if (providerEditors.FirstOrDefault(editor => !editor.IsValid) is { } invalid)
        {
            Message(DesktopResources.Get("ChatSettingsInvalid"));
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == invalid.Key);
            invalid.SelectModel();
            layout.UpdateLayout();
            if (invalid.Form is not null) invalid.Form.FocusInvalid();
            else if (invalid.Fields.FirstInvalid is { } control)
                DispatcherQueue.TryEnqueue(() => { control.Focus(FocusState.Programmatic); control.StartBringIntoView(); });
            return false;
        }
        foreach (var editor in providerEditors) editor.Commit();
        return true;
    }
}
