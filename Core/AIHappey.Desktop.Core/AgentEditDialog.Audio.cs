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
            var configuration = type == "transcription" ? draft.Transcription : draft.SpeechModel;
            var id = DesktopAgent.Text(configuration?["id"]);
            var options = ProviderPreferences.Filter(models, session.Settings).Where(m => (m.ModelType == type || type == "speech" && OpenAiRealtimeSpeech.Supports(m))
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
            if (type == "speech") SpeechOptions(body);
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
        var model = type == "transcription" ? draft.Transcription : draft.SpeechModel;
        if (model is null) return;
        var provider = DesktopAgent.Provider(DesktopAgent.Text(model["id"]));
        var metadata = (JsonObject)(model["providerMetadata"] as JsonObject ?? new()).DeepClone();
        var panel = Panel(); Action? commit = null;
        if (type == "transcription" && provider is "openai" or "google" or "gladia" or "smallestai")
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
            if ((provider == "openai" && OpenAiRealtimeSpeech.IsModel(DesktopAgent.Text(model["id"]))
                ? new OpenAiRealtimeSpeechSettingsForm(metadata["openai"] as JsonObject ?? new())
                : SpeechProviderForms.Create(provider, preferences)) is { } form)
            {
                form.Commit(preferences); var baseline = Options(); panel.Children.Add(form.View);
                commit = () => { form.Commit(preferences); Apply(baseline, Options()); };
            }
            JsonObject Options() => new(preferences.ProviderOptions.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value.DeepClone())));
        }
        if (type == "transcription" && provider is not ("openai" or "google" or "gladia" or "smallestai"))
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

    private void SpeechOptions(StackPanel body)
    {
        Text("voice", "SpeechVoice", "SpeechVoiceHint");
        Text("instructions", "SpeechInstructions", "SpeechInstructionsHint", true);
        Text("language", "SpeechLanguage", "SpeechLanguageHint");
        var speed = new NumberBox { Name = "AgentSpeechSpeed", Header = DesktopResources.Get("SpeechSpeed"),
            Minimum = .01, SmallChange = .1, Value = AgentSpeechConfiguration.Number(draft.Speech?["speed"]) ?? double.NaN,
            PlaceholderText = DesktopResources.Get("ProviderDefault") };
        ControlAppearance.Stock(speed); AutomationProperties.SetName(speed, DesktopResources.Get("SpeechSpeed")); body.Children.Add(speed);
        speed.ValueChanged += (_, _) => Set("speed", double.IsNaN(speed.Value) ? null : JsonValue.Create(speed.Value));
        var format = new TextBox { Name = "AgentSpeechOutputFormat", Header = DesktopResources.Get("SpeechOutputFormat"),
            PlaceholderText = "audio/mpeg", Text = DesktopAgent.Text(draft.Speech?["outputFormat"]?["type"]) };
        var rate = new NumberBox { Name = "AgentSpeechSampleRate", Header = DesktopResources.Get("AgentSpeechSampleRate"), Minimum = 1, SmallChange = 1,
            Value = AgentSpeechConfiguration.Number(draft.Speech?["outputFormat"]?["rate"]) ?? double.NaN,
            IsEnabled = !string.IsNullOrWhiteSpace(format.Text), PlaceholderText = DesktopResources.Get("ProviderDefault") };
        ControlAppearance.Stock(format); ControlAppearance.Stock(rate);
        AutomationProperties.SetName(format, DesktopResources.Get("SpeechOutputFormat")); AutomationProperties.SetName(rate, DesktopResources.Get("AgentSpeechSampleRate"));
        body.Children.Add(format); body.Children.Add(rate);
        format.TextChanged += (_, _) =>
        {
            rate.IsEnabled = !string.IsNullOrWhiteSpace(format.Text);
            if (!rate.IsEnabled) { rate.Value = double.NaN; Set("outputFormat", null); }
            else { var value = (JsonObject)(draft.Speech?["outputFormat"] as JsonObject ?? new()).DeepClone(); value["type"] = format.Text.Trim(); Set("outputFormat", value); }
        };
        rate.ValueChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(format.Text)) return;
            var value = (JsonObject)(draft.Speech?["outputFormat"] as JsonObject ?? new()).DeepClone(); value["type"] = format.Text.Trim();
            if (double.IsNaN(rate.Value)) value.Remove("rate"); else value["rate"] = rate.Value;
            Set("outputFormat", value);
        };

        void Set(string key, JsonNode? value)
        {
            var speech = DesktopAgent.Object(draft.Definition, "speech");
            if (value is null) speech.Remove(key); else speech[key] = value;
            if (speech.Count == 0) draft.Definition.Remove("speech");
            UpdateSave();
        }
        void Text(string key, string label, string hint, bool multiline = false)
        {
            var box = new TextBox { Name = "AgentSpeech_" + key, Header = DesktopResources.Get(label), PlaceholderText = DesktopResources.Get(hint),
                Text = DesktopAgent.Text(draft.Speech?[key]), AcceptsReturn = multiline, MinHeight = multiline ? 96 : 0,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
            ControlAppearance.Stock(box); AutomationProperties.SetName(box, DesktopResources.Get(label)); body.Children.Add(box);
            box.TextChanged += (_, _) => Set(key, string.IsNullOrWhiteSpace(box.Text) ? null : JsonValue.Create(box.Text));
        }
    }
}
