using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Persistent native checkbox content: toggles never invoke a dismissing menu command.</summary>
internal sealed class ProviderSelectionView : StackPanel
{
    private readonly ComboBox types = new() { Name = "ProviderChooserType", Header = DesktopResources.Get("Providers"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer scroll = new() { Name = "ProviderChooserScroll", MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
    private readonly InfoBar error = new() { Name = "ProviderChooserError", Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly Dictionary<string, StackPanel> pages = [];
    private readonly IReadOnlyDictionary<string, string[]> groups;
    private readonly Func<DesktopSettings> settings;
    public Func<string, string, bool, Task>? SaveRequested { get; set; }
    public ProviderSelectionView(IReadOnlyDictionary<string, string[]> groups, Func<DesktopSettings> settings, string selectedType)
    {
        this.groups = groups; this.settings = settings;
        Name = "ProviderSelectionView"; Width = 300; Spacing = 12;
        ToolbarControls.Label(types, DesktopResources.Get("Providers")); ControlAppearance.Native(types);
        Children.Add(types); Children.Add(error); Children.Add(scroll);
        foreach (var type in new[] { "language", "image", "audio", "transcription", "speech", "decision", "reranking", "video", "embedding" })
        {
            if (!groups.TryGetValue(type, out var providers)) continue;
            types.Items.Add(new ComboBoxItem { Tag = type });
            var page = new StackPanel { Spacing = 2 }; pages.Add(type, page);
            foreach (var provider in providers)
            {
                var checkbox = new CheckBox { Name = "ProviderChooserToggle", Content = provider, Tag = provider,
                    IsChecked = ProviderPreferences.IsEnabled(settings(), type, provider), MinHeight = 40,
                    HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(checkbox, DesktopResources.Get("AiModelType_" + type) + ": " + provider);
                AutomationProperties.SetAutomationId(checkbox, "Provider:" + type + ":" + provider);
                ControlAppearance.Native(checkbox);
                var revision = 0;
                checkbox.Click += async (_, _) =>
                {
                    var request = ++revision;
                    var enabled = checkbox.IsChecked == true;
                    error.IsOpen = false;
                    try { if (SaveRequested is { } save) await save(type, provider, enabled); }
                    catch (Exception) { error.Message = DesktopResources.Get("ProviderSettingsSaveFailed"); error.IsOpen = true; }
                    finally
                    {
                        if (revision == request) checkbox.IsChecked = ProviderPreferences.IsEnabled(settings(), type, provider);
                        UpdateCounts();
                    }
                };
                page.Children.Add(checkbox);
            }
        }
        UpdateCounts();
        types.SelectionChanged += (_, _) =>
        {
            if (types.SelectedItem is ComboBoxItem { Tag: string type })
            {
                scroll.Content = pages[type]; scroll.ChangeView(null, 0, null, true);
            }
        };
        types.SelectedItem = types.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selectedType) ?? types.Items.FirstOrDefault();
    }
    private void UpdateCounts()
    {
        foreach (var item in types.Items.OfType<ComboBoxItem>())
        {
            var type = (string)item.Tag; var providers = groups[type];
            item.Content = DesktopResources.Format("ProviderTypeCount", DesktopResources.Get("AiModelType_" + type),
                providers.Count(provider => ProviderPreferences.IsEnabled(settings(), type, provider)), providers.Length);
        }
    }
}
