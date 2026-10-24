using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

internal sealed class RealtimeSettingsDialog : ContentDialog
{
    private RealtimePreferences preferences;
    private readonly StackPanel fields = new() { Spacing = 16, MinWidth = 360, MaxWidth = 640 };
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly string provider;
    public Func<RealtimePreferences, Task>? SaveAsync { get; set; }
    public RealtimeSettingsDialog(RealtimePreferences current, string selectedProvider)
    {
        preferences = current.Clone(); provider = selectedProvider == "google" ? "google" : "openai";
        Title = DesktopResources.Get("RealtimeSettings") + " · " + (provider == "google" ? "Google" : "OpenAI");
        PrimaryButtonText = DesktopResources.Get("Save"); CloseButtonText = DesktopResources.Get("Cancel");
        DefaultButton = ContentDialogButton.Primary;
        Content = new ScrollViewer { Content = fields, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Build();
        PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); args.Cancel = true;
            try { preferences.Validate(); if (SaveAsync is not null) await SaveAsync(preferences.Clone()); args.Cancel = false; }
            catch { error.Message = DesktopResources.Get("RealtimeSettingsFailed"); error.IsOpen = true; }
            finally { deferral.Complete(); }
        };
    }
    private JsonObject Metadata => preferences.ProviderOptions[provider] as JsonObject ?? (JsonObject)(preferences.ProviderOptions[provider] = new JsonObject());
    private JsonObject Config
    {
        get
        {
            if (provider == "openai") return Object(Metadata, "session");
            return Object(Object(Metadata, "liveConnectConstraints"), "config");
        }
    }
    private static JsonObject Object(JsonObject parent, string key)
    { if (parent[key] is JsonObject result) return result; var value = new JsonObject(); parent[key] = value; return value; }
    private void Build()
    {
        fields.Children.Clear(); fields.Children.Add(error);
        fields.Children.Add(new TextBlock { Text = DesktopResources.Get("RealtimeSettingsHint"), TextWrapping = TextWrapping.Wrap });
        if (provider == "openai")
        {
            Metadata["session"] = RealtimeConfiguration.Merge(JsonNode.Parse("""{"type":"realtime","audio":{"output":{"voice":"marin"},"input":{"turn_detection":{"type":"semantic_vad","eagerness":"auto","create_response":true,"interrupt_response":true}}}}""")!.AsObject(), Config);
            var input = Object(Object(Config, "audio"), "input"); var output = Object(Object(Config, "audio"), "output");
            Choice("RealtimeVoice", ["alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"], output["voice"]?.ToString() ?? "marin", v => output["voice"] = v);
            var detection = Object(input, "turn_detection");
            Choice("RealtimeVad", ["semantic_vad", "server_vad"], detection["type"]?.ToString() ?? "semantic_vad", v => detection["type"] = v);
            Choice("RealtimeEagerness", ["auto", "low", "medium", "high"], detection["eagerness"]?.ToString() ?? "auto", v => detection["eagerness"] = v);
            Toggle("RealtimeInterrupt", detection["interrupt_response"]?.GetValue<bool>() ?? true, v => detection["interrupt_response"] = v);
            Toggle("RealtimeAutomaticResponse", detection["create_response"]?.GetValue<bool>() ?? true, v => detection["create_response"] = v);
            Toggle("RealtimeInputTranscription", input["transcription"] is not null, v => { if (v) input["transcription"] = new JsonObject { ["model"] = "gpt-4o-mini-transcribe" }; else input.Remove("transcription"); });
            Number("RealtimeThreshold", detection["threshold"]?.GetValue<double>() ?? .5, 0, 1, .1, v => detection["threshold"] = v);
            Number("RealtimeSilence", detection["silence_duration_ms"]?.GetValue<double>() ?? 500, 100, 10000, 50, v => detection["silence_duration_ms"] = (int)v);
            Number("RealtimePrefix", detection["prefix_padding_ms"]?.GetValue<double>() ?? 300, 0, 5000, 50, v => detection["prefix_padding_ms"] = (int)v);
        }
        else
        {
            var config = Config;
            if (config.Count == 0) { config["inputAudioTranscription"] = new JsonObject(); config["outputAudioTranscription"] = new JsonObject(); config["sessionResumption"] = new JsonObject(); config["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() }; }
            var voice = Object(Object(Object(config, "speechConfig"), "voiceConfig"), "prebuiltVoiceConfig");
            Choice("RealtimeVoice", ["Achernar", "Aoede", "Charon", "Fenrir", "Kore", "Leda", "Orus", "Puck", "Zephyr"], voice["voiceName"]?.ToString() ?? "Kore", v => voice["voiceName"] = v);
            Toggle("RealtimeInputTranscription", config["inputAudioTranscription"] is not null, v => config["inputAudioTranscription"] = v ? new JsonObject() : null);
            Toggle("RealtimeOutputTranscription", config["outputAudioTranscription"] is not null, v => config["outputAudioTranscription"] = v ? new JsonObject() : null);
            Toggle("RealtimeGoogleSearch", (config["tools"] as JsonArray)?.Any(t => t?["googleSearch"] is not null) == true, v =>
            {
                var tools = new JsonArray((config["tools"] as JsonArray ?? new()).Where(t => t?["googleSearch"] is null).Select(t => t?.DeepClone()).ToArray());
                if (v) tools.Add(new JsonObject { ["googleSearch"] = new JsonObject() }); config["tools"] = tools;
            });
            var vad = Object(Object(config, "realtimeInputConfig"), "automaticActivityDetection");
            Toggle("RealtimeAutomaticVad", vad["disabled"]?.GetValue<bool>() != true, v => vad["disabled"] = !v);
            Number("RealtimeSilence", vad["silenceDurationMs"]?.GetValue<double>() ?? 500, 100, 10000, 50, v => vad["silenceDurationMs"] = (int)v);
            Number("RealtimePrefix", vad["prefixPaddingMs"]?.GetValue<double>() ?? 0, 0, 5000, 50, v => vad["prefixPaddingMs"] = (int)v);
            Toggle("RealtimeCompression", config["contextWindowCompression"] is not null, v => config["contextWindowCompression"] = v ? new JsonObject { ["slidingWindow"] = new JsonObject() } : null);
            Toggle("RealtimeResumption", config["sessionResumption"] is not null, v => config["sessionResumption"] = v ? new JsonObject() : null);
            Number("RealtimeCameraFps", preferences.CameraFrameRate, .1, 1, .1, v => preferences.CameraFrameRate = v);
            Number("RealtimeJpegQuality", preferences.JpegQuality, .1, 1, .1, v => preferences.JpegQuality = v);
            var expiry = new TextBox { Header = DesktopResources.Get("RealtimeExpiry"), Text = Metadata["expireTime"]?.ToString() ?? "30m" };
            expiry.TextChanged += (_, _) => Metadata["expireTime"] = expiry.Text.Trim(); ControlAppearance.Native(expiry); fields.Children.Add(expiry);
        }
        var reset = new Button { Content = DesktopResources.Get("RealtimeReset") };
        reset.Click += (_, _) => { preferences.ProviderOptions.Remove(provider); preferences.CameraFrameRate = 1; preferences.JpegQuality = .8; Build(); };
        ControlAppearance.Native(reset); fields.Children.Add(reset);
    }
    private void Choice(string label, string[] values, string selected, Action<string> changed)
    {
        var choice = new ComboBox { Header = DesktopResources.Get(label), ItemsSource = values, SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        choice.SelectionChanged += (_, _) => { if (choice.SelectedItem is string value) changed(value); }; ControlAppearance.Native(choice); fields.Children.Add(choice);
    }
    private void Toggle(string label, bool selected, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { Header = DesktopResources.Get(label), IsOn = selected };
        toggle.Toggled += (_, _) => changed(toggle.IsOn); ControlAppearance.Native(toggle); fields.Children.Add(toggle);
    }
    private void Number(string label, double value, double min, double max, double step, Action<double> changed)
    {
        var number = new NumberBox { Header = DesktopResources.Get(label), Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        number.ValueChanged += (_, args) => { if (double.IsFinite(args.NewValue)) changed(args.NewValue); }; ControlAppearance.Native(number); fields.Children.Add(number);
    }
}
