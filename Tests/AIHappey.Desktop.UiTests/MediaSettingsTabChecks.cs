using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckMediaSettingsTabsSuiteAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); window.Activate(); await Task.Delay(120);
        var directory = Path.Combine(Path.GetTempPath(), "aihappey-settings-tabs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            await CheckSpeechSettingsAsync(root, theme);
            await CheckTranscriptionSettingsAsync(root, theme);
            await CheckImageSettingsTabsAsync(root, theme, directory);
        }
        finally { window.Content = null; Directory.Delete(directory, true); }
    }

    private async Task CheckImageSettingsTabsAsync(Grid root, ElementTheme theme, string directory)
    {
        var context = "Image settings tabs / " + theme;
        var original = new ImagePreferences { StorageRoot = directory };
        original.ProviderOptions["openai"]["future"] = new JsonObject { ["keep"] = true };
        ImagePreferences? saved = null;
        ImageSettingsDialog Create(ImagePreferences preferences, string? provider = "openai") => new(preferences, provider, directory)
        { XamlRoot = root.XamlRoot, RequestedTheme = theme, SaveAsync = next => { saved = next; return Task.CompletedTask; } };
        var dialog = Create(original); SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync();
        try
        {
            await Task.Delay(120); dialog.UpdateLayout();
            await CheckMediaSettingsTabsAsync(dialog, ["ImageGeneralTab", "ImageProviderTab"], context);
            var navigation = Field<NavigationView>(dialog, "tabs");
            var output = Descendants(dialog).OfType<SettingsExpander>().Single(section => section.Name == "ImageOutputCard");
            output.IsExpanded = true; await Task.Delay(60);
            var count = Descendants(dialog).OfType<Slider>().Single(field => field.Name == "ImageCount"); count.Value = 4;
            var batch = Descendants(dialog).OfType<TextBox>().Single(field => field.Name == "ImageBatchLimit"); batch.Text = "0";
            output.IsExpanded = false;
            var providerTab = (NavigationViewItem)navigation.MenuItems[1];
            ((ISelectionItemProvider)new NavigationViewItemAutomationPeer(providerTab).GetPattern(PatternInterface.SelectionItem)).Select(); await Task.Delay(60);
            Descendants(dialog).OfType<SettingsExpander>().Single(section => section.Name == "OpenAI_ImageGeneral").IsExpanded = true; await Task.Delay(60);
            var quality = Descendants(dialog).OfType<ComboBox>().Single(field => field.Name == "ImageOpenAIquality"); quality.SelectedIndex = 3;
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton")); await Task.Delay(100);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Started && saved is null && navigation.SelectedItem == navigation.MenuItems[0]
                && !providerTab.IsSelected && output.IsExpanded && batch.FocusState != FocusState.Unfocused && count.Value == 4,
                context + ": invalid hidden field selects General, expands and focuses it without losing edits");
            batch.Text = "2";
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton")); await shown;
            Check(saved?.N == 4 && saved.MaxImagesPerCall == 2 && saved.ProviderOptions["openai"]["quality"]!.GetValue<string>() == "high"
                && saved.ProviderOptions["openai"]["future"]!["keep"]!.GetValue<bool>() && original.N == 1,
                context + ": both forms save their retained draft edits and preserve unknown options");
        }
        finally { dialog.DiscardOnShutdown = true; dialog.Hide(); await shown; }

        dialog = Create(saved!); shown = dialog.ShowAsync();
        try
        {
            await Task.Delay(100);
            var navigation = Field<NavigationView>(dialog, "tabs"); navigation.SelectedItem = navigation.MenuItems[1]; await Task.Delay(60);
            var previous = navigation.MenuItems[1];
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton")); await Task.Delay(80);
            Check(navigation.MenuItems.Count == 2 && navigation.SelectedItem == navigation.MenuItems[0] && navigation.MenuItems[1] != previous
                && ReferenceEquals(Field<ScrollViewer>(dialog, "page").Content, ((NavigationViewItem)navigation.SelectedItem).Tag),
                context + ": defaults rebuild both tabs and select the new General form");
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton")); await shown;
            Check(saved?.N == 1 && saved.MaxImagesPerCall is null && saved.StorageRoot == directory, context + ": defaults save and retain storage location");
        }
        finally { dialog.DiscardOnShutdown = true; dialog.Hide(); await shown; }

        foreach (var provider in new string?[] { "other", null })
        {
            dialog = Create(original, provider); shown = dialog.ShowAsync();
            try
            {
                await Task.Delay(80);
                var navigation = Field<NavigationView>(dialog, "tabs");
                Check(navigation.MenuItems.Count == 1 && navigation.SelectedItem is NavigationViewItem { Name: "ImageGeneralTab" },
                    context + ": unsupported or missing provider retains only the native General tab");
            }
            finally { dialog.DiscardOnShutdown = true; dialog.Hide(); await shown; }
        }
    }

    private async Task CheckMediaSettingsTabsAsync(ContentDialog dialog, string[] names, string context)
    {
        var navigation = Field<NavigationView>(dialog, "tabs");
        var tabs = navigation.MenuItems.OfType<NavigationViewItem>().ToArray();
        var page = Field<ScrollViewer>(dialog, "page");
        CheckNativeTabs(navigation, context);
        Check(tabs.Select(tab => tab.Name).SequenceEqual(names) && navigation.SelectedItem == tabs[0]
            && ReferenceEquals(page.Content, tabs[0].Tag), context + ": native tab order and initial selection are unchanged");
        Check(tabs.All(tab => AutomationProperties.GetAutomationId(tab) == tab.Name
            && AutomationProperties.GetName(tab) == tab.Content.ToString()), context + ": native tabs expose stable identities and accessible labels");
        Check(tabs[0].Focus(FocusState.Keyboard) && new NavigationViewItemAutomationPeer(tabs[0]).GetName() == tabs[0].Content.ToString(),
            context + ": native tab supports keyboard focus and its selection automation peer");
        ((ISelectionItemProvider)new NavigationViewItemAutomationPeer(tabs[0]).GetPattern(PatternInterface.SelectionItem)).Select();

        foreach (var width in new[] { 480, 720, 1280 })
        {
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(100); dialog.UpdateLayout();
            var layout = Field<Grid>(dialog, "layout");
            Check(layout.ActualWidth > 0 && layout.ActualWidth <= dialog.XamlRoot.Size.Width
                && navigation.ActualWidth <= layout.ActualWidth + 1, context + $": native top navigation is bounded at {width}px");
            foreach (var tab in tabs)
            {
                // Overflow items are not necessarily realized until their native menu opens.
                navigation.SelectedItem = tab; await Task.Delay(40); dialog.UpdateLayout();
                Check(navigation.SelectedItem == tab && tabs.Count(item => item.IsSelected) == 1
                    && ReferenceEquals(page.Content, tab.Tag) && page.ScrollableWidth < 1,
                    context + $": {tab.Name} stays selectable with one selected tab and no horizontal page scrolling at {width}px");
            }
        }
        navigation.SelectedItem = tabs[0]; await Task.Delay(60); dialog.UpdateLayout();
    }

    private async Task CheckSpeechSettingsAsync(Grid root, ElementTheme theme)
    {
        var context = "Speech settings / " + theme;
        var original = new SpeechPreferences { Voice = "original" };
        original.ProviderOptions["openai"] = new JsonObject { ["future"] = new JsonObject { ["keep"] = true } };
        SpeechPreferences? saved = null;
        SpeechSettingsDialog Create(SpeechPreferences preferences, string? provider = "openai") => new(preferences, provider)
        { XamlRoot = root.XamlRoot, RequestedTheme = theme, SaveAsync = next => { saved = next; return Task.CompletedTask; } };
        var dialog = Create(original);
        // Show directly as well as through PrepareDialog in the image/transcription checks.
        var shown = dialog.ShowAsync();
        try
        {
            await Task.Delay(140); dialog.UpdateLayout();
            await CheckMediaSettingsTabsAsync(dialog, ["SpeechGeneralTab", "SpeechProviderTab"], context);
            var navigation = Field<NavigationView>(dialog, "tabs");
            var voice = Descendants(dialog).OfType<TextBox>().Single(field => field.Name == "SpeechVoice");
            voice.Text = "draft voice";
            navigation.SelectedItem = navigation.MenuItems[1]; await Task.Delay(60);
            var providerVoice = Descendants(dialog).OfType<ComboBox>().Single(field => field.Name == "OpenAISpeechVoice");
            providerVoice.SelectedIndex = 1;
            var selectedVoice = (string)((ComboBoxItem)providerVoice.SelectedItem).Tag;
            var speed = Descendants(dialog).OfType<NumberBox>().Single(field => field.Name == "OpenAISpeechSpeed"); speed.Value = 1.5;
            navigation.SelectedItem = navigation.MenuItems[0]; await Task.Delay(60);
            Check(ReferenceEquals(Descendants(dialog).OfType<TextBox>().Single(field => field.Name == "SpeechVoice"), voice)
                && voice.Text == "draft voice" && original.Voice == "original", context + ": switching tabs retains edits without changing source preferences");
            navigation.SelectedItem = navigation.MenuItems[1]; await Task.Delay(60);
            Check(speed.Value == 1.5 && providerVoice.SelectedIndex == 1, context + ": provider edits survive tab navigation");
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton")); await shown;
            Check(saved?.Voice == "draft voice" && saved.ProviderOptions["openai"]["voice"]!.GetValue<string>() == selectedVoice
                && saved.ProviderOptions["openai"]["speed"]!.GetValue<double>() == 1.5
                && saved.ProviderOptions["openai"]["future"]!["keep"]!.GetValue<bool>() && original.Voice == "original",
                context + ": closing commits both forms and preserves unknown provider options");
        }
        finally { dialog.DiscardOnShutdown = true; dialog.Hide(); await shown; }

        dialog = Create(saved!); shown = dialog.ShowAsync();
        try
        {
            await Task.Delay(100);
            var navigation = Field<NavigationView>(dialog, "tabs"); navigation.SelectedItem = navigation.MenuItems[1]; await Task.Delay(60);
            var previous = navigation.MenuItems[0];
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton")); await Task.Delay(80);
            Check(navigation.MenuItems.Count == 2 && navigation.SelectedItem == navigation.MenuItems[0] && navigation.SelectedItem != previous
                && Descendants(dialog).OfType<TextBox>().Single(field => field.Name == "SpeechVoice").Text == "",
                context + ": defaults rebuild native tabs and select the new General form");
            navigation.SelectedItem = navigation.MenuItems[1]; await Task.Delay(60);
            Check(Descendants(dialog).OfType<ComboBox>().Single(field => field.Name == "OpenAISpeechVoice").SelectedIndex == 0
                && Descendants(dialog).OfType<NumberBox>().Single(field => field.Name == "OpenAISpeechSpeed").Value == 1,
                context + ": rebuilt provider form shows restored defaults");
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton")); await shown;
            Check(saved?.Voice is null, context + ": restored defaults save successfully");
        }
        finally { dialog.DiscardOnShutdown = true; dialog.Hide(); await shown; }

        foreach (var provider in new string?[] { "other", null })
        {
            dialog = Create(original, provider); shown = dialog.ShowAsync();
            try
            {
                await Task.Delay(80);
                var navigation = Field<NavigationView>(dialog, "tabs");
                Check(navigation.MenuItems.Count == 1 && navigation.SelectedItem is NavigationViewItem { Name: "SpeechGeneralTab" },
                    context + ": unsupported or missing provider retains only the native General tab");
            }
            finally { dialog.DiscardOnShutdown = true; dialog.Hide(); await shown; }
        }
    }
}
