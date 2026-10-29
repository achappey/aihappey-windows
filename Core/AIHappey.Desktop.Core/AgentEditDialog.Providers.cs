using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
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

    private void BuildProviders()
    {
        var selected = (tabs.SelectedItem as NavigationViewItem)?.Tag as string;
        foreach (var editor in providerEditors)
        {
            var old = tabs.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == editor.Key);
            if (old is not null) tabs.MenuItems.Remove(old);
            pages.Remove(editor.Key);
        }
        providerEditors.Clear();
        foreach (var settings in AgentProviderSettings.Groups(draft))
        {
            var preferences = new ChatPreferences();
            preferences.ProviderMetadata[settings.Provider] = (JsonObject)settings.Metadata.DeepClone();
            preferences.ProviderHeaders[settings.Provider] = settings.Headers.Where(pair => pair.Value is JsonValue value && value.TryGetValue<string>(out _))
                .ToDictionary(pair => pair.Key, pair => DesktopAgent.Text(pair.Value), StringComparer.OrdinalIgnoreCase);
            // Decision-only provider configurations are not language-model configurations.
            var form = settings.Models.Any(model => ModelType(DesktopAgent.Text(model["id"])) != "decision")
                ? ChatProviderForms.Create(settings.Provider, preferences) : null;
            var editor = new ProviderEditor(settings, form, preferences);
            var panel = Panel(); panel.Name = "AgentProvider_" + settings.Provider;
            if (form is not null) panel.Children.Add(form.View);
            else
            {
                editor.Fields.Json(panel, "providerMetadata", () => settings.Metadata, node => settings.Metadata = node.AsObject()).Header = DesktopResources.Get("AgentProviderMetadata");
                editor.Fields.Json(panel, "providerHeaders", () => settings.Headers, node => settings.Headers = node.AsObject()).Header = DesktopResources.Get("AgentProviderHeaders");
            }
            var (metadata, headers) = editor.Read(); settings.Baseline(metadata, headers);
            providerEditors.Add(editor);
            AddTab(editor.Key, settings.Provider.Length > 0 ? ProviderCatalog.Get(settings.Provider).Name : DesktopResources.Get("AgentProvider"), panel);
            editor.Fields.Watch(UpdateSave); form?.Watch(UpdateSave);
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
            if (invalid.Form is not null) invalid.Form.FocusInvalid();
            else if (invalid.Fields.FirstInvalid is { } control)
                DispatcherQueue.TryEnqueue(() => { control.Focus(FocusState.Programmatic); control.StartBringIntoView(); });
            return false;
        }
        foreach (var editor in providerEditors) editor.Commit();
        return true;
    }
}
