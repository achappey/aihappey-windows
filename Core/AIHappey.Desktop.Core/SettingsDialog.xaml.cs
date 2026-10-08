using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly bool allowLocal;
    public DesktopSettings? Result { get; private set; }

    public SettingsDialog(DesktopSettings settings, bool allowLocal, string activeLanguage)
    {
        this.allowLocal = allowLocal;
        InitializeComponent();
        Name = "SettingsDialog";
        Resources["ContentDialogMaxWidth"] = 840d;
        Resources["ContentDialogMinWidth"] = 0d;
        LanguageChoice.SelectedIndex = DesktopLanguage.Resolve(settings.Language, activeLanguage) == "nl" ? 1 : 0;
        foreach (var (location, url, config) in new[] { (AiLocation, AiUrl, settings.Ai), (AgentsLocation, AgentsUrl, settings.Agents) })
        {
            location.ItemsSource = allowLocal ? new[] { DesktopResources.Get("ManagedLocal"), DesktopResources.Get("Remote") } : new[] { DesktopResources.Get("Remote") };
            location.SelectedIndex = allowLocal && config.Location == RuntimeLocation.Remote ? 1 : 0;
            url.Text = config.RemoteUrl;
            void ToggleUrl() => url.IsEnabled = !allowLocal || location.SelectedIndex == 1;
            ToggleUrl();
            location.SelectionChanged += (_, _) => ToggleUrl();
        }
        DocumentTextExtraction.IsOn = settings.ConvertAttachmentsToText;
        foreach (var control in new Control[] { Tabs, LanguageChoice, AiLocation, AiUrl, AgentsLocation, AgentsUrl, DocumentTextExtraction }) ControlAppearance.Native(control);
        ControlAppearance.BorderlessItems(Tabs);
        ControlAppearance.Apply(NavigationSurface, (_, _) => { }, palette => NavigationSurface.Background = new SolidColorBrush(palette.Panel));
        PrimaryButtonClick += ValidateAndSave;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }

    private void TabChanged(object sender, SelectionChangedEventArgs args)
    {
        // Selection can fire while InitializeComponent is still creating the page controls.
        if (GeneralPage is null || EndpointsPage is null || AttachmentsPage is null) return;
        GeneralPage.Visibility = Tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        EndpointsPage.Visibility = Tabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsPage.Visibility = Tabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ChangeView(null, 0, null, true);
    }

    private void ValidateAndSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Result = null;
        var next = new DesktopSettings
        {
            Language = (string)((ComboBoxItem)LanguageChoice.SelectedItem).Tag,
            Ai = new() { Location = allowLocal && AiLocation.SelectedIndex == 0 ? RuntimeLocation.Local : RuntimeLocation.Remote, RemoteUrl = AiUrl.Text.Trim() },
            Agents = new() { Location = allowLocal && AgentsLocation.SelectedIndex == 0 ? RuntimeLocation.Local : RuntimeLocation.Remote, RemoteUrl = AgentsUrl.Text.Trim() },
            ConvertAttachmentsToText = DocumentTextExtraction.IsOn
        };
        try { next.Validate(allowLocal); Result = next; }
        catch (InvalidOperationException error)
        {
            args.Cancel = true;
            Tabs.SelectedIndex = 1;
            Validation.Text = error.Message;
            PageScroll.ChangeView(null, PageScroll.ScrollableHeight, null, true);
        }
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    private void SizeToRoot()
    {
        Layout.Width = Math.Max(0, Math.Min(760, XamlRoot.Size.Width - 96));
        Layout.Height = Math.Max(0, Math.Min(440, XamlRoot.Size.Height - 240));
        NavigationColumn.Width = new GridLength(Layout.Width < 500 ? 140 : 180);
        MaxWidth = Math.Max(0, Math.Min(840, XamlRoot.Size.Width - 32));
    }
}
