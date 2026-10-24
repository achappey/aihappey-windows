using System.Text.Json.Nodes;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Provider-specific real-time options hosted by the shared Chat Settings dialog.</summary>
internal sealed class RealtimeSettingsForm : StackPanel
{
    private RealtimePreferences preferences;
    private readonly RealtimePreferences original;
    private readonly StackPanel fields = new() { Spacing = 16 };
    private readonly List<NumberBox> numbers = [];
    private readonly List<(SettingsExpander Expander, StackPanel Body)> sections = [];
    private TextBox? expiry;
    private readonly string provider;
    private readonly bool readOnly;
    public RealtimePreferences Result => (readOnly ? original : preferences).Clone();
    public RealtimeSettingsForm(RealtimePreferences current, string selectedProvider, bool readOnly)
    {
        Name = "RealtimeSettingsForm"; Spacing = 16;
        original = current.Clone(); this.readOnly = readOnly;
        preferences = current.Clone(); provider = selectedProvider == "google" ? "google" : "openai";
        if (readOnly) Children.Add(new TextBlock { Text = DesktopResources.Get("RealtimeSettingsLocked"), TextWrapping = TextWrapping.Wrap });
        Children.Add(fields);
        Build();
    }
    public bool IsValid
    {
        get
        {
            if (readOnly) return true;
            try
            {
                preferences.Validate();
                if (numbers.Any(n => !double.IsFinite(n.Value) || n.Value < n.Minimum || n.Value > n.Maximum)) return false;
                if (expiry is not null) _ = RealtimeConfiguration.Expiry(expiry.Text, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));
                return true;
            }
            catch (ArgumentException) { return false; }
        }
    }
    public void FocusInvalid()
    {
        Control? invalid = numbers.FirstOrDefault(n => !double.IsFinite(n.Value) || n.Value < n.Minimum || n.Value > n.Maximum);
        invalid ??= expiry;
        if (invalid is null) return;
        foreach (var (expander, body) in sections)
            if (NativeSettingsSurface.Contains(body, invalid)) expander.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() => { UpdateLayout(); invalid.Focus(FocusState.Programmatic); invalid.StartBringIntoView(); });
    }
    public void RestoreDefaults()
    {
        if (readOnly) return;
        preferences = new RealtimePreferences { Model = preferences.Model };
        Build();
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
        fields.Children.Clear(); numbers.Clear(); sections.Clear(); expiry = null;
        var audio = Section("Audio");
        if (provider == "openai")
        {
            Metadata["session"] = RealtimeConfiguration.Merge(JsonNode.Parse("""{"type":"realtime","audio":{"output":{"voice":"marin"},"input":{"turn_detection":{"type":"semantic_vad","eagerness":"auto","create_response":true,"interrupt_response":true}}}}""")!.AsObject(), Config);
            var input = Object(Object(Config, "audio"), "input"); var output = Object(Object(Config, "audio"), "output");
            Choice(audio, "RealtimeVoice", ["alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"], output["voice"]?.ToString() ?? "marin", v => output["voice"] = v);
            Toggle(audio, "RealtimeInputTranscription", input["transcription"] is not null, v => { if (v) input["transcription"] = new JsonObject { ["model"] = "gpt-4o-mini-transcribe" }; else input.Remove("transcription"); });
            var turns = Section("TurnDetection");
            var detection = Object(input, "turn_detection");
            Choice(turns, "RealtimeVad", ["semantic_vad", "server_vad"], detection["type"]?.ToString() ?? "semantic_vad", v => detection["type"] = v);
            Choice(turns, "RealtimeEagerness", ["auto", "low", "medium", "high"], detection["eagerness"]?.ToString() ?? "auto", v => detection["eagerness"] = v);
            Number(turns, "RealtimeThreshold", detection["threshold"]?.GetValue<double>() ?? .5, 0, 1, .1, v => detection["threshold"] = v);
            Number(turns, "RealtimeSilence", detection["silence_duration_ms"]?.GetValue<double>() ?? 500, 100, 10000, 50, v => detection["silence_duration_ms"] = (int)v);
            Number(turns, "RealtimePrefix", detection["prefix_padding_ms"]?.GetValue<double>() ?? 300, 0, 5000, 50, v => detection["prefix_padding_ms"] = (int)v);
            var responses = Section("Responses");
            Toggle(responses, "RealtimeAutomaticResponse", detection["create_response"]?.GetValue<bool>() ?? true, v => detection["create_response"] = v);
            Toggle(responses, "RealtimeInterrupt", detection["interrupt_response"]?.GetValue<bool>() ?? true, v => detection["interrupt_response"] = v);
        }
        else
        {
            var config = Config;
            if (config.Count == 0) { config["inputAudioTranscription"] = new JsonObject(); config["outputAudioTranscription"] = new JsonObject(); config["sessionResumption"] = new JsonObject(); config["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() }; }
            var voice = Object(Object(Object(config, "speechConfig"), "voiceConfig"), "prebuiltVoiceConfig");
            Choice(audio, "RealtimeVoice", ["Achernar", "Aoede", "Charon", "Fenrir", "Kore", "Leda", "Orus", "Puck", "Zephyr"], voice["voiceName"]?.ToString() ?? "Kore", v => voice["voiceName"] = v);
            Toggle(audio, "RealtimeInputTranscription", config["inputAudioTranscription"] is not null, v => config["inputAudioTranscription"] = v ? new JsonObject() : null);
            Toggle(audio, "RealtimeOutputTranscription", config["outputAudioTranscription"] is not null, v => config["outputAudioTranscription"] = v ? new JsonObject() : null);
            var turns = Section("TurnDetection");
            var vad = Object(Object(config, "realtimeInputConfig"), "automaticActivityDetection");
            Toggle(turns, "RealtimeAutomaticVad", vad["disabled"]?.GetValue<bool>() != true, v => vad["disabled"] = !v);
            Number(turns, "RealtimeSilence", vad["silenceDurationMs"]?.GetValue<double>() ?? 500, 100, 10000, 50, v => vad["silenceDurationMs"] = (int)v);
            Number(turns, "RealtimePrefix", vad["prefixPaddingMs"]?.GetValue<double>() ?? 0, 0, 5000, 50, v => vad["prefixPaddingMs"] = (int)v);
            var toolsSection = Section("Tools");
            Toggle(toolsSection, "RealtimeGoogleSearch", (config["tools"] as JsonArray)?.Any(t => t?["googleSearch"] is not null) == true, v =>
            {
                var tools = new JsonArray((config["tools"] as JsonArray ?? new()).Where(t => t?["googleSearch"] is null).Select(t => t?.DeepClone()).ToArray());
                if (v) tools.Add(new JsonObject { ["googleSearch"] = new JsonObject() }); config["tools"] = tools;
            });
            var session = Section("Session");
            Toggle(session, "RealtimeCompression", config["contextWindowCompression"] is not null, v => config["contextWindowCompression"] = v ? new JsonObject { ["slidingWindow"] = new JsonObject() } : null);
            Toggle(session, "RealtimeResumption", config["sessionResumption"] is not null, v => config["sessionResumption"] = v ? new JsonObject() : null);
            expiry = new TextBox { Name = "RealtimeExpiry", Header = DesktopResources.Get("RealtimeExpiry"), Text = Metadata["expireTime"]?.ToString() ?? "30m" };
            expiry.IsEnabled = !readOnly; AutomationProperties.SetName(expiry, DesktopResources.Get("RealtimeExpiry"));
            expiry.TextChanged += (_, _) => Metadata["expireTime"] = expiry.Text.Trim(); ControlAppearance.Stock(expiry); session.Children.Add(expiry);
            var video = Section("Video");
            Number(video, "RealtimeCameraFps", preferences.CameraFrameRate, .1, 1, .1, v => preferences.CameraFrameRate = v);
            Number(video, "RealtimeJpegQuality", preferences.JpegQuality, .1, 1, .1, v => preferences.JpegQuality = v);
        }
    }
    private StackPanel Section(string name)
    {
        var key = "RealtimeSection" + name;
        var expander = NativeSettingsSurface.Expander(fields, "Realtime" + name + "Section", DesktopResources.Get(key), null, out var body);
        sections.Add((expander, body)); return body;
    }
    private void Choice(Panel parent, string label, string[] values, string selected, Action<string> changed)
    {
        var options = values.Contains(selected) ? values : [.. values, selected];
        var choice = new ComboBox { Name = label, Header = DesktopResources.Get(label), ItemsSource = options, SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !readOnly };
        AutomationProperties.SetName(choice, DesktopResources.Get(label));
        choice.SelectionChanged += (_, _) => { if (choice.SelectedItem is string value) changed(value); }; ControlAppearance.Stock(choice); parent.Children.Add(choice);
    }
    private void Toggle(Panel parent, string label, bool selected, Action<bool> changed)
    {
        var row = new Grid { Name = label + "Row", ColumnSpacing = 12, MinHeight = 32 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = DesktopResources.Get(label), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var toggle = new ToggleSwitch { Name = label, IsOn = selected, OnContent = "", OffContent = "", MinWidth = 0,
            IsEnabled = !readOnly, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, DesktopResources.Get(label));
        toggle.Toggled += (_, _) => changed(toggle.IsOn); ControlAppearance.Stock(toggle);
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle); parent.Children.Add(row);
    }
    private void Number(Panel parent, string label, double value, double min, double max, double step, Action<double> changed)
    {
        var number = new NumberBox { Name = label, Header = DesktopResources.Get(label), Value = value, Minimum = min, Maximum = max, SmallChange = step, IsEnabled = !readOnly, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        numbers.Add(number); AutomationProperties.SetName(number, DesktopResources.Get(label));
        number.ValueChanged += (_, args) => { if (double.IsFinite(args.NewValue)) changed(args.NewValue); }; ControlAppearance.Stock(number); parent.Children.Add(number);
    }
}
