using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.Core;

/// <summary>Agent-only Realtime output controls. The standalone HTTP speech form stays unchanged.</summary>
public sealed class OpenAiRealtimeSpeechSettingsForm : ISpeechProviderForm
{
    private readonly JsonObject config;
    private readonly ComboBox voice = new() { Name = "OpenAiRealtimeSpeechVoice", Header = DesktopResources.Get("SpeechVoice"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox speed = new() { Name = "OpenAiRealtimeSpeechSpeed", Header = DesktopResources.Get("SpeechSpeed"), Minimum = .25, Maximum = 1.5, SmallChange = .05, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private bool voiceChanged, speedChanged;
    public string Title => "OpenAI";
    public FrameworkElement View { get; }
    public OpenAiRealtimeSpeechSettingsForm(JsonObject source)
    {
        config = (JsonObject)source.DeepClone();
        var panel = new StackPanel { Name = "OpenAiRealtimeSpeechForm", Spacing = 16 };
        NativeSettingsSurface.Card(panel, "OpenAiRealtimeSpeechGeneral", DesktopResources.Get("General"), out var body);
        var selected = config["voice"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : "marin";
        foreach (var item in OpenAiRealtimeSpeech.Voices.Append(selected).Distinct(StringComparer.Ordinal)) voice.Items.Add(new ComboBoxItem { Content = item, Tag = item });
        voice.SelectedItem = voice.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == selected);
        speed.Value = config["speed"] is JsonValue number && number.TryGetValue<double>(out var rate) && double.IsFinite(rate) ? Math.Clamp(rate, .25, 1.5) : 1;
        voice.SelectionChanged += (_, _) => voiceChanged = true;
        speed.ValueChanged += (_, _) => speedChanged = true;
        foreach (var control in new Control[] { voice, speed })
        {
            ControlAppearance.Stock(control); AutomationProperties.SetName(control, (control == voice ? voice.Header : speed.Header)?.ToString() ?? ""); body.Children.Add(control);
        }
        View = panel;
    }
    public void Commit(SpeechPreferences preferences)
    {
        if (voiceChanged) config["voice"] = (voice.SelectedItem as ComboBoxItem)?.Tag as string ?? "marin";
        if (speedChanged)
        {
            if (!double.IsFinite(speed.Value) || speed.Value is < .25 or > 1.5) throw new InvalidOperationException("OpenAI Realtime speech speed must be between 0.25 and 1.5.");
            config["speed"] = speed.Value;
        }
        preferences.ProviderOptions["openai"] = (JsonObject)config.DeepClone();
    }
}
