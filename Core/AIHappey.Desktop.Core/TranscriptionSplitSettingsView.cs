using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Edits only the user-settings dialog's transcription draft.</summary>
public sealed class TranscriptionSplitSettingsView : StackPanel
{
    public TranscriptionSplitSettingsView(TranscriptionPreferences draft)
    {
        Name = "TranscriptionSplitSettings"; Spacing = 16;
        var enabled = new ToggleSwitch { Name = "TranscriptionFileSplitEnabled", Header = DesktopResources.Get("TranscriptionFileSplitEnabled"), IsOn = draft.FileSplitEnabled };
        ToolbarControls.Label(enabled, DesktopResources.Get("TranscriptionFileSplitEnabled")); Children.Add(enabled);
        Slider Setting(string name, int minimum, int maximum, int current, string unit, Action<int> save)
        {
            var label = DesktopResources.Get(name);
            var panel = new StackPanel { Spacing = 4 };
            var value = new TextBlock { Text = $"{label} {current} {unit}", TextWrapping = TextWrapping.Wrap };
            var slider = new Slider { Name = name, Minimum = minimum, Maximum = maximum, StepFrequency = 1,
                Value = current, IsEnabled = enabled.IsOn, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolbarControls.Label(slider, label);
            slider.ValueChanged += (_, args) => { var next = (int)Math.Round(args.NewValue); save(next); value.Text = $"{label} {next} {unit}"; };
            ControlAppearance.Stock(slider); panel.Children.Add(value); panel.Children.Add(slider); Children.Add(panel);
            return slider;
        }
        var overlap = Setting("TranscriptionFileSplitOverlapSeconds", 0, 60, draft.FileSplitOverlapSeconds, "s", next => draft.FileSplitOverlapSeconds = next);
        var size = Setting("TranscriptionFileSplitMaxSizeMb", 1, 100, draft.FileSplitMaxSizeMb, "MB", next => draft.FileSplitMaxSizeMb = next);
        enabled.Toggled += (_, _) => { draft.FileSplitEnabled = enabled.IsOn; overlap.IsEnabled = size.IsEnabled = enabled.IsOn; };
    }
}
