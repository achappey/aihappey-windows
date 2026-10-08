using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed class ChatSettingsDialog : ContentDialog, IResponsiveDialog
{
    private ChatPreferences draft;
    private readonly string? provider;
    private readonly Grid layout = new() { RowSpacing = 16 };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private TextBox tokens = null!;
    private StackPanel general = null!;
    private IChatProviderForm? providerForm;
    public ChatPreferences? Result { get; private set; }
    public Func<ChatPreferences, Task>? SaveAsync { get; set; }
    public bool DiscardOnShutdown { get; set; }

    public ChatSettingsDialog(ChatPreferences preferences, string? provider)
    {
        Name = "ChatSettingsDialog"; this.provider = provider; draft = preferences.Clone();
        Title = DesktopResources.Get("ChatSettings"); CloseButtonText = DesktopResources.Get("Close");
        PrimaryButtonText = DesktopResources.Get("ChatRestoreDefaults");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var navigation = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.Children.Add(navigation); Grid.SetRow(page, 1); layout.Children.Add(page);
        Grid.SetRow(validation, 2); layout.Children.Add(validation); Content = layout;
        AutomationProperties.SetLiveSetting(validation, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        Build();
        PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true; draft = new ChatPreferences(); Result = null; validation.Visibility = Visibility.Collapsed; Build();
        };
        Closing += CommitOnClose;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; tokens.Focus(FocusState.Programmatic); };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }

    private void Build()
    {
        tabs.Children.Clear(); general = new StackPanel { Spacing = 16 };
        ChatSettingsFields.Card(general, "GeneralChatCard", "artificialIntelligence", out var body, out _);
        tokens = new TextBox { Name = "MaxOutputTokens", Header = DesktopResources.Get("ChatMaxOutputTokens"), PlaceholderText = DesktopResources.Get("ChatOptional"), Text = draft.MaxOutputTokens?.ToString() ?? "" };
        ControlAppearance.Stock(tokens); AutomationProperties.SetName(tokens, DesktopResources.Get("ChatMaxOutputTokens")); body.Children.Add(tokens);
        var tokenError = new TextBlock { Text = DesktopResources.Get("ChatTokensInvalid"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }; body.Children.Add(tokenError);
        tokens.TextChanged += (_, _) => tokenError.Visibility = ChatPreferences.TryTokenLimit(tokens.Text, out _) ? Visibility.Collapsed : Visibility.Visible;
        providerForm = ChatProviderForms.Create(provider, draft);
        var generalTab = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Name = "ChatGeneralTab", Content = DesktopResources.Get("General"), IsChecked = true };
        ControlAppearance.Stock(generalTab); tabs.Children.Add(generalTab);
        Microsoft.UI.Xaml.Controls.Primitives.ToggleButton? providerTab = null;
        if (providerForm is not null)
        {
            providerTab = new() { Name = "ChatProviderTab", Content = provider == "openai" ? "OpenAI" : provider };
            ControlAppearance.Stock(providerTab); tabs.Children.Add(providerTab);
            providerTab.Click += (_, _) => { generalTab.IsChecked = false; providerTab.IsChecked = true; page.Content = providerForm.View; page.ChangeView(null, 0, null, true); };
        }
        generalTab.Click += (_, _) => { generalTab.IsChecked = true; if (providerTab is not null) providerTab.IsChecked = false; page.Content = general; page.ChangeView(null, 0, null, true); };
        page.Content = general;
    }

    private async void CommitOnClose(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (DiscardOnShutdown) return;
        if (!ChatPreferences.TryTokenLimit(tokens.Text, out var limit) || providerForm?.IsValid == false)
        {
            args.Cancel = true; validation.Text = DesktopResources.Get("ChatSettingsInvalid"); validation.Visibility = Visibility.Visible; return;
        }
        var deferral = args.GetDeferral();
        try
        {
            draft.MaxOutputTokens = limit; providerForm?.Commit(draft);
            if (SaveAsync is not null) await SaveAsync(draft.Clone());
            Result = draft.Clone();
        }
        catch (Exception)
        {
            args.Cancel = true; validation.Text = DesktopResources.Get("ChatSettingsSaveFailed"); validation.Visibility = Visibility.Visible;
        }
        finally { deferral.Complete(); }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240));
    }
}
