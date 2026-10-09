using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class SettingsDialog : ContentDialog, IResponsiveDialog
{
    private readonly bool allowLocal;
    private readonly ChatPreferences chatPreferences;
    private readonly List<string> allowedTools;
    private readonly AiModelPreferences aiModelPreferences;
    private readonly ImagePreferences imagePreferences;
    private readonly TranscriptionPreferences transcriptionPreferences;
    private readonly ImageStorageSettingsView imageStorage;
    private readonly CancellationTokenSource catalogLifetime = new();
    public AiModelSettingsView AiModelView { get; }
    public DesktopSettings? Result { get; private set; }

    public SettingsDialog(DesktopSettings settings, bool allowLocal, string activeLanguage,
        IReadOnlyList<ChatTarget>? aiModels = null, Func<CancellationToken, Task<IReadOnlyList<ChatTarget>>>? loadAiModels = null)
    {
        this.allowLocal = allowLocal;
        chatPreferences = settings.Chat.Clone();
        allowedTools = settings.AllowedToolList.ToList();
        aiModelPreferences = settings.AiModels.Clone();
        imagePreferences = settings.Images.Clone();
        transcriptionPreferences = settings.Transcriptions.Clone();
        InitializeComponent();
        imageStorage = new(imagePreferences, catalogLifetime.Token);
        AiModelView = new(aiModelPreferences, aiModels, imageStorage);
        ArtificialIntelligencePage.Children.Add(AiModelView);
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
        FormElicitation.IsOn = settings.ModelContext.EnableFormElicitation;
        ToolTimeout.Value = settings.ModelContext.ToolTimeoutMinutes;
        ResetTimeoutOnProgress.IsOn = settings.ModelContext.ResetTimeoutOnProgress;
        EnableMcpApps.IsOn = settings.ModelContext.EnableApps;
        EnableMcpSkills.IsOn = settings.ModelContext.EnableSkills;
        ModelContextPage.SelectedItem = McpClientTab;
        Tabs.SelectedItem = GeneralTab;
        PrimaryButtonClick += ValidateAndSave;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Opened += async (_, _) =>
        {
            if (aiModels is not null) return;
            if (loadAiModels is null) { AiModelView.SetCatalogStatus(DesktopResources.Get("AiModelsLoadFailed")); return; }
            try
            {
                var catalog = await loadAiModels(catalogLifetime.Token);
                if (!catalogLifetime.IsCancellationRequested) AiModelView.SetCatalog(catalog);
            }
            catch (OperationCanceledException) when (catalogLifetime.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!catalogLifetime.IsCancellationRequested) AiModelView.SetCatalogStatus(DesktopResources.Get("AiModelsLoadFailed"));
            }
        };
        Closed += (_, _) => { catalogLifetime.Cancel(); XamlRoot.Changed -= RootChanged; };
    }

    private void TabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Selection can fire while InitializeComponent is still creating the page controls.
        if (GeneralPage is null || EndpointsPage is null || AttachmentsPage is null || ModelContextPage is null || ArtificialIntelligencePage is null) return;
        GeneralPage.Visibility = ReferenceEquals(Tabs.SelectedItem, GeneralTab) ? Visibility.Visible : Visibility.Collapsed;
        ArtificialIntelligencePage.Visibility = ReferenceEquals(Tabs.SelectedItem, ArtificialIntelligenceTab) ? Visibility.Visible : Visibility.Collapsed;
        EndpointsPage.Visibility = ReferenceEquals(Tabs.SelectedItem, EndpointsTab) ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsPage.Visibility = ReferenceEquals(Tabs.SelectedItem, AttachmentsTab) ? Visibility.Visible : Visibility.Collapsed;
        ModelContextPage.Visibility = ReferenceEquals(Tabs.SelectedItem, ModelContextTab) ? Visibility.Visible : Visibility.Collapsed;
        if (Layout.Width < 600) Tabs.IsPaneOpen = false;
        PageScroll.ChangeView(null, 0, null, true);
    }

    private void ModelContextTabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (McpClientPage is null || McpExtensionsPage is null) return;
        McpClientPage.Visibility = ReferenceEquals(sender.SelectedItem, McpClientTab) ? Visibility.Visible : Visibility.Collapsed;
        McpExtensionsPage.Visibility = ReferenceEquals(sender.SelectedItem, McpExtensionsTab) ? Visibility.Visible : Visibility.Collapsed;
        PageScroll?.ChangeView(null, 0, null, true);
    }

    private void ValidateAndSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Result = null;
        if (imageStorage.PickerOpen) { args.Cancel = true; return; }
        var next = new DesktopSettings
        {
            Language = (string)((ComboBoxItem)LanguageChoice.SelectedItem).Tag,
            Ai = new() { Location = allowLocal && AiLocation.SelectedIndex == 0 ? RuntimeLocation.Local : RuntimeLocation.Remote, RemoteUrl = AiUrl.Text.Trim() },
            Agents = new() { Location = allowLocal && AgentsLocation.SelectedIndex == 0 ? RuntimeLocation.Local : RuntimeLocation.Remote, RemoteUrl = AgentsUrl.Text.Trim() },
            ConvertAttachmentsToText = DocumentTextExtraction.IsOn,
            ModelContext = new()
            {
                EnableFormElicitation = FormElicitation.IsOn,
                ToolTimeoutMinutes = double.IsFinite(ToolTimeout.Value) ? (int)Math.Round(ToolTimeout.Value) : 5,
                ResetTimeoutOnProgress = ResetTimeoutOnProgress.IsOn,
                EnableApps = EnableMcpApps.IsOn,
                EnableSkills = EnableMcpSkills.IsOn
            },
            Chat = chatPreferences.Clone(),
            Images = imagePreferences.Clone(),
            Transcriptions = transcriptionPreferences.Clone(),
            AiModels = aiModelPreferences.Clone(),
            AllowedToolList = allowedTools.ToList()
        };
        try { next.Validate(allowLocal); }
        catch (InvalidOperationException error)
        {
            args.Cancel = true;
            Tabs.SelectedItem = EndpointsTab;
            Validation.Text = error.Message;
            PageScroll.ChangeView(null, PageScroll.ScrollableHeight, null, true);
            return;
        }
        try { imageStorage.ValidateAndPrepare(); Result = next; }
        catch (Exception error)
        {
            args.Cancel = true;
            Tabs.SelectedItem = ArtificialIntelligenceTab;
            AiModelView.SelectedItem = AiModelView.MenuItems.OfType<NavigationViewItem>().Single(item => (string)item.Tag == "image");
            imageStorage.ShowError(error is InvalidOperationException ? error.Message : DesktopResources.Get("ImageFolderFailed"));
            imageStorage.UpdateLayout();
            imageStorage.StartBringIntoView();
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
