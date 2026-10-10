using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

internal sealed class ProviderDetailsDialog : ContentDialog
{
    private readonly InfoBar notice = new() { Severity = InfoBarSeverity.Warning, IsOpen = false };
    public Action<Uri>? LinkRequested { get; set; }
    public void Notice(string message) { notice.Message = message; notice.IsOpen = true; }
    public ProviderDetailsDialog(CatalogProvider provider, IReadOnlyList<string> modelTypes)
    {
        Title = provider.Name; Name = "ProviderDetails"; CloseButtonText = DesktopResources.Get("Close"); DefaultButton = ContentDialogButton.Close;
        var body = new StackPanel { Spacing = 16, MaxWidth = 700 };
        body.Children.Add(notice);
        body.Children.Add(new ProviderLogo(provider, 64) { HorizontalAlignment = HorizontalAlignment.Left });
        body.Children.Add(new TextBlock { Name = "ProviderFullDescription", Text = provider.Description ?? provider.Id, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (provider.Category is { } category) Field(body, "ProviderCategory", ProvidersOverviewPage.FacetLabel("category", category));
        if (provider.Experimental) body.Children.Add(ProvidersOverviewPage.Badge(DesktopResources.Get("ProviderExperimental")));
        if (provider.ProviderCountry is { } country) Field(body, "ProviderCountry", ProvidersOverviewPage.FacetLabel("country", country));
        if (provider.InferenceRegions.Count > 0) Field(body, "ProviderInferenceRegions", string.Join(", ", provider.InferenceRegions.Select(r => ProvidersOverviewPage.FacetLabel("region", r))));
        Field(body, "ProviderAvailableModelTypes", modelTypes.Count > 0 ? string.Join(", ", modelTypes.Select(t => ProvidersOverviewPage.FacetLabel("modelType", t))) : DesktopResources.Get("ProviderNoDiscoveredModels"));
        var links = new NativeWrapPanel { Spacing = 8 };
        foreach (var link in ProviderCatalog.Links(provider))
        {
            var button = new Button { Name = "ProviderDetailLink", Content = DesktopResources.Get(link.ResourceKey), Tag = link };
            ControlAppearance.Native(button); ToolbarControls.Label(button, DesktopResources.Get(link.ResourceKey));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "ProviderDetails:" + link.Key);
            ToolTipService.SetToolTip(button, link.Uri.AbsoluteUri); button.Click += (_, _) => LinkRequested?.Invoke(link.Uri); links.Children.Add(button);
        }
        body.Children.Add(links);
        Content = new ScrollViewer { Content = body, MaxHeight = 560, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static void Field(StackPanel body, string label, string value)
    {
        var field = new StackPanel { Spacing = 4 }; field.Children.Add(new TextBlock { Text = DesktopResources.Get(label), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        field.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }); body.Children.Add(field);
    }
}
