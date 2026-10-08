using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class SettingsDialog : ContentDialog, IResponsiveDialog
{
    private readonly bool allowLocal;
    private readonly ChatPreferences chatPreferences;
    public DesktopSettings? Result { get; private set; }

    public SettingsDialog(DesktopSettings settings, bool allowLocal, string activeLanguage)
    {
        this.allowLocal = allowLocal;
        chatPreferences = settings.Chat.Clone();
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
        Tabs.SelectedItem = GeneralTab;
        PrimaryButtonClick += ValidateAndSave;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }

    private void TabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Selection can fire while InitializeComponent is still creating the page controls.
        if (GeneralPage is null || EndpointsPage is null || AttachmentsPage is null) return;
        GeneralPage.Visibility = Tabs.SelectedItem == GeneralTab ? Visibility.Visible : Visibility.Collapsed;
        EndpointsPage.Visibility = Tabs.SelectedItem == EndpointsTab ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsPage.Visibility = Tabs.SelectedItem == AttachmentsTab ? Visibility.Visible : Visibility.Collapsed;
        if (Layout.Width < 600) Tabs.IsPaneOpen = false;
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
            ConvertAttachmentsToText = DocumentTextExtraction.IsOn,
            Chat = chatPreferences.Clone()
        };
        try { next.Validate(allowLocal); Result = next; }
        catch (InvalidOperationException error)
        {
            args.Cancel = true;
            Tabs.SelectedItem = EndpointsTab;
            Validation.Text = error.Message;
            PageScroll.ChangeView(null, PageScroll.ScrollableHeight, null, true);
        }
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        Layout.Width = Math.Max(0, Math.Min(760, XamlRoot.Size.Width - 96));
        Layout.Height = Math.Max(0, Math.Min(440, XamlRoot.Size.Height - 240));
        var narrow = Layout.Width < 600;
        Tabs.IsPaneToggleButtonVisible = narrow;
        Tabs.IsPaneOpen = !narrow;
    }
}
