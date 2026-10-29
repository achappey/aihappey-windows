using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class AgentEditDialog
{
    private readonly Dictionary<string, Action> audioCommits = [];
    private void CommitAudioProviders() { foreach (var commit in audioCommits.Values) commit(); }
    private StackPanel AudioModels(IReadOnlyList<ChatTarget> models)
    {
        var panel = Panel();
        foreach (var type in new[] { "transcription", "speech" })
        {
            var title = DesktopResources.Get(type == "transcription" ? "Transcriptions" : "Speech");
            NativeSettingsSurface.Expander(panel, "AgentAudio_" + type, title, null, out var body);
            var configuration = type == "transcription" ? draft.TranscriptionModel : draft.SpeechModel;
            var id = DesktopAgent.Text(configuration?["id"]);
            var options = ProviderPreferences.Filter(models, session.Settings).Where(m => m.ModelType == type
                && (type != "transcription" || m.Tags.Contains("real-time")))
                .Concat(id.Length > 0 ? new[] { new ChatTarget(id, id) { ModelType = type } } : []).DistinctBy(m => m.Id).ToArray();
            var picker = new ChatTargetPicker { Name = "AgentAudioModel_" + type, AllowEmptySelection = true,
                PlaceholderText = DesktopResources.Get("SelectModel"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = options };
            picker.Configure(false, false, new HashSet<string>(), false, ""); picker.SetSelection(id.Length > 0 ? [id] : []);
            var label = new TextBlock { Text = ChatSettingsFields.L("model") };
            body.Children.Add(label);
            ToolbarControls.Label(picker, label.Text); AutomationProperties.SetLabeledBy(picker, label); body.Children.Add(picker);
            picker.SelectionChanged += () =>
            {
                try
                {
                    if (audioCommits.TryGetValue(type, out var commit)) commit();
                    draft.SelectAudioModel(type, picker.Selection.Ids.FirstOrDefault() ?? "");
                    BuildAudioProvider(type); UpdateSave();
                }
                catch (Exception error) { Message(error is InvalidOperationException ? error.Message : DesktopResources.Get("ChatSettingsInvalid")); }
            };
            BuildAudioProvider(type);
        }
        return panel;
    }
    private void BuildAudioProvider(string type)
    {
        var key = "audio-" + type;
        var selected = (tabs.SelectedItem as NavigationViewItem)?.Tag as string == key;
        var old = tabs.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == key);
        if (old is not null) tabs.MenuItems.Remove(old);
        pages.Remove(key); audioCommits.Remove(type);
        var model = type == "transcription" ? draft.TranscriptionModel : draft.SpeechModel;
        if (model is null) return;
        var provider = DesktopAgent.Provider(DesktopAgent.Text(model["id"]));
        var metadata = (JsonObject)(model["providerMetadata"] as JsonObject ?? new()).DeepClone();
        var panel = Panel(); Action? commit = null;
        if (type == "transcription" && provider is "openai" or "google" or "gladia")
        {
            var preferences = new TranscriptionPreferences { Realtime = new() { ProviderOptions = metadata } };
            var form = new RealtimeTranscriptionSettingsForm(provider, preferences.Realtime);
            form.Commit(preferences); var baseline = (JsonObject)preferences.Realtime.ProviderOptions.DeepClone();
            panel.Children.Add(form.View);
            commit = () => { form.Commit(preferences); Apply(baseline, preferences.Realtime.ProviderOptions); };
        }
        else if (type == "speech")
        {
            var preferences = new SpeechPreferences { ProviderOptions = metadata.Where(pair => pair.Value is JsonObject)
                .ToDictionary(pair => pair.Key, pair => (JsonObject)pair.Value!.DeepClone(), StringComparer.OrdinalIgnoreCase) };
            if (SpeechProviderForms.Create(provider, preferences) is { } form)
            {
                form.Commit(preferences); var baseline = Options(); panel.Children.Add(form.View);
                commit = () => { form.Commit(preferences); Apply(baseline, Options()); };
            }
            JsonObject Options() => new(preferences.ProviderOptions.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value.DeepClone())));
        }
        if (type == "transcription" && provider is not ("openai" or "google" or "gladia"))
            panel.Children.Add(new TextBlock { Text = DesktopResources.Get("AgentRealtimeUnsupported"), TextWrapping = TextWrapping.Wrap });
        if (commit is null && panel.Children.Count == 0) return;
        if (commit is not null) audioCommits[type] = commit;
        AddTab(key, DesktopResources.Get(type == "transcription" ? "Transcriptions" : "Speech"), panel);
        if (selected) { tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == key); page.Content = panel; }
        void Apply(JsonObject before, JsonObject after)
        {
            var target = model["providerMetadata"] as JsonObject ?? new();
            DesktopAgent.ApplyChanges(target, before, after);
            if (target.Count > 0 || model.ContainsKey("providerMetadata")) model["providerMetadata"] = target;
        }
    }
}
