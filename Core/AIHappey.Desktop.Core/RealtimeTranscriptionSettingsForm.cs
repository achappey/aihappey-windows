using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native transcription controls; preserves unknown options independently of batch and voice preferences.</summary>
public sealed class RealtimeTranscriptionSettingsForm : ITranscriptionProviderForm
{
    private readonly string provider;
    private readonly JsonObject options;
    private readonly List<Action> commits = [];
    public string Title { get; }
    public FrameworkElement View { get; }
    public RealtimeTranscriptionSettingsForm(string provider, RealtimeTranscriptionPreferences preferences)
    {
        this.provider = provider; options = (JsonObject)(preferences.ProviderOptions[provider] as JsonObject ?? new()).DeepClone();
        Title = (provider == "openai" ? "OpenAI" : provider == "google" ? "Google" : provider == "smallestai" ? "Smallest AI" : provider == "mistral" ? "Mistral" : "Gladia") + " · " + DesktopResources.Get("TranscriptionRealtime");
        var panel = new StackPanel { Name = "RealtimeTranscription" + provider + "Form", Spacing = 16 }; View = panel;
        NativeSettingsSurface.Card(panel, "RealtimeTranscriptionSettingsCard", Title, out var fields);
        fields.Children.Add(new TextBlock { Text = DesktopResources.Get("TranscriptionRealtimeHint"), TextWrapping = TextWrapping.Wrap });
        if (provider == "openai")
        {
            var input = Object(Object(Object(options, "session"), "audio"), "input");
            var transcription = Object(input, "transcription");
            Text(fields, "TranscriptionLanguage", transcription, "language"); Text(fields, "TranscriptionPrompt", transcription, "prompt", true);
            var vad = Object(input, "turn_detection"); vad["type"] = "server_vad";
            Number(fields, "RealtimeThreshold", vad, "threshold", .5, 0, 1, .05);
            Number(fields, "RealtimeSilence", vad, "silence_duration_ms", 500, 100, 10000, 50);
            Number(fields, "RealtimePrefix", vad, "prefix_padding_ms", 300, 0, 5000, 50);
            Choice(fields, "TranscriptionRealtimeNoise", Object(input, "noise_reduction"), "type", "near_field", ["near_field", "far_field"]);
            Number(fields, "TranscriptionRealtimeTokenSeconds", Object(options, "expires_after"), "seconds", 600, 10, 7200, 10);
            Object(options, "expires_after")["anchor"] = "created_at";
        }
        else if (provider == "google")
        {
            var input = Object(Object(Object(options, "liveConnectConstraints"), "config"), "inputAudioTranscription");
            Text(fields, "TranscriptionLanguages", input, "languageCodes", list: true);
            Text(fields, "TranscriptionKeywords", input, "customVocabulary", true, true);
            Choice(fields, "TranscriptionRealtimeMode", input, "mode", "VERBATIM", ["VERBATIM", "SMART"]);
            Text(fields, "RealtimeExpiry", options, "expireTime", fallback: "30m");
            Text(fields, "TranscriptionRealtimeNewSessionExpiry", options, "newSessionExpireTime", fallback: "1m");
        }
        else if (provider == "mistral")
        {
            Text(fields, "TranscriptionLanguage", options, "language");
            Text(fields, "TranscriptionKeywords", options, "context_bias", true, true);
            Number(fields, "TranscriptionMistralStreamingDelay", options, "target_streaming_delay_ms", 480, 80, 2400, 1);
        }
        else if (provider == "smallestai")
        {
            Text(fields, "TranscriptionLanguage", options, "language", fallback: "en");
            Text(fields, "TranscriptionKeywords", options, "keywords");
            Toggle(fields, "TranscriptionDiarization", options, "diarize");
            Number(fields, "TranscriptionRealtimeTokenSeconds", options, "ttl_seconds", 300, 30, 900, 30);
        }
        else
        {
            var language = Object(options, "language_config");
            Text(fields, "TranscriptionLanguages", language, "languages", list: true);
            Toggle(fields, "TranscriptionRealtimeCodeSwitching", language, "code_switching");
            Toggle(fields, "TranscriptionRealtimeEnhancer", Object(options, "pre_processing"), "audio_enhancer");
            Number(fields, "TranscriptionRealtimeEndpointing", options, "endpointing", .05, .01, 10, .01);
            Number(fields, "TranscriptionRealtimeMaxDuration", options, "maximum_duration_without_endpointing", 5, 5, 60, 1);
        }
    }
    private static JsonObject Object(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject value) return value;
        var created = new JsonObject(); parent[key] = created; return created;
    }
    private void Text(StackPanel panel, string label, JsonObject target, string key, bool multiline = false, bool list = false, string fallback = "")
    {
        var value = list && target[key] is JsonArray array ? string.Join(multiline ? "\n" : ", ", array.Select(n => n?.ToString())) : target[key]?.ToString() ?? fallback;
        var field = new TextBox { Name = "Realtime" + provider + key, Header = DesktopResources.Get(label), Text = value, AcceptsReturn = multiline,
            TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 88 : 0, MaxHeight = multiline ? 200 : double.PositiveInfinity };
        ControlAppearance.Stock(field); ToolbarControls.Label(field, DesktopResources.Get(label)); panel.Children.Add(field);
        commits.Add(() =>
        {
            if (list) target[key] = new JsonArray(field.Text.Split(multiline ? '\n' : ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            else if (string.IsNullOrWhiteSpace(field.Text)) target.Remove(key); else target[key] = field.Text.Trim();
        });
    }
    private void Number(StackPanel panel, string label, JsonObject target, string key, double fallback, double min, double max, double step)
    {
        var field = new NumberBox { Name = "Realtime" + provider + key, Header = DesktopResources.Get(label), Value = target[key]?.GetValue<double>() ?? fallback,
            Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        ControlAppearance.Stock(field); ToolbarControls.Label(field, DesktopResources.Get(label)); panel.Children.Add(field);
        commits.Add(() => { if (!double.IsFinite(field.Value) || field.Value < min || field.Value > max) throw new ArgumentException(); target[key] = step >= 1 ? JsonValue.Create((int)field.Value) : JsonValue.Create(field.Value); });
    }
    private void Choice(StackPanel panel, string label, JsonObject target, string key, string fallback, string[] choices)
    {
        var field = new ComboBox { Name = "Realtime" + provider + key, Header = DesktopResources.Get(label), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var choice in choices) field.Items.Add(choice);
        field.SelectedItem = choices.Contains(target[key]?.ToString()) ? target[key]!.ToString() : fallback;
        ControlAppearance.Stock(field); ToolbarControls.Label(field, DesktopResources.Get(label)); panel.Children.Add(field);
        commits.Add(() => target[key] = (string)field.SelectedItem);
    }
    private void Toggle(StackPanel panel, string label, JsonObject target, string key)
    {
        var field = new ToggleSwitch { Name = "Realtime" + provider + key, Header = DesktopResources.Get(label), IsOn = target[key]?.GetValue<bool>() == true };
        ControlAppearance.Stock(field); ToolbarControls.Label(field, DesktopResources.Get(label)); panel.Children.Add(field); commits.Add(() => target[key] = field.IsOn);
    }
    public void Commit(TranscriptionPreferences preferences)
    {
        foreach (var commit in commits) commit();
        preferences.Realtime.ProviderOptions[provider] = options.DeepClone(); preferences.Realtime.Validate();
    }
}
