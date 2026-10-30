using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>One native model-card presentation shared by model browsing and provider details.</summary>
internal sealed class ModelCardFactory
{
    public bool ActionsEnabled { get; set; } = true;
    public IReadOnlySet<string> Favorites { get; set; } = new HashSet<string>();
    public Action<ChatTarget>? CopyRequested { get; set; }
    public Action<ChatTarget>? FavoriteRequested { get; set; }
    public Action<ChatTarget>? LaunchRequested { get; set; }
    public Action<Uri>? WebsiteRequested { get; set; }
    public Border Create(ChatTarget model)
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(16, 16, 16, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(ProviderIcon(model));
        var labels = new StackPanel { Spacing = 6 };
        var name = new TextBlock { Name = "ModelName", Text = model.Label, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(name, model.Label); labels.Children.Add(name);
        // Wrap badges at narrow widths instead of introducing horizontal scrolling.
        var badges = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 3 };
        if (model.ContextWindow is > 0) badges.Children.Add(Badge(ModelOverviewCatalog.CompactNumber(model.ContextWindow.Value), Icon.Folder, DesktopResources.Format("ModelsContextBadge", model.ContextWindow.Value.ToString("N0"))));
        if (model.MaxTokens is > 0) badges.Children.Add(Badge(ModelOverviewCatalog.CompactNumber(model.MaxTokens.Value), Icon.ArrowDownload, DesktopResources.Format("ModelsOutputBadge", model.MaxTokens.Value.ToString("N0"))));
        if (model.Tags.Contains("real-time")) badges.Children.Add(Badge(DesktopResources.Get("AiModelType_audio"), Icon.Headphones, DesktopResources.Get("AiModelType_audio")));
        labels.Children.Add(badges); Grid.SetColumn(labels, 1); header.Children.Add(labels);
        if (ModelOverviewCatalog.IsNew(model, DateTimeOffset.UtcNow))
        { var badge = Badge(DesktopResources.Get("New"), null, DesktopResources.Get("New")); badge.Name = "ModelNew"; Grid.SetColumn(badge, 2); header.Children.Add(badge); }
        grid.Children.Add(header);
        var text = new TextBlock { Name = "ModelDescription", Text = model.Description ?? "", FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 18, 16, 16), MinHeight = 36 };
        ToolTipService.SetToolTip(text, model.Description); NativeCardSurface.Secondary(text); Grid.SetRow(text, 1); grid.Children.Add(text);
        var actions = new NativeWrapPanel { Spacing = 4 };
        var copy = ActionButton(model, "Copy", Icon.Copy); ToolTipService.SetToolTip(copy, ModelOverviewCatalog.DisplayId(model)); copy.Click += (_, _) => CopyRequested?.Invoke(model); actions.Children.Add(copy);
        if (ModelOverviewCatalog.CanLaunch(model))
        { var launch = ActionButton(model, "ModelsLaunch", DesktopIcons.ModelType(model.ModelType)); launch.Name = "ModelLaunch"; launch.Click += (_, _) => LaunchRequested?.Invoke(model); actions.Children.Add(launch); }
        var saved = Favorites.Contains(ModelOverviewCatalog.FavoriteKey(model));
        var favorite = ActionButton(model, saved ? "RemoveFavorite" : "AddFavorite", Icon.Star, saved ? IconVariant.Filled : IconVariant.Regular); favorite.Name = "ModelFavorite";
        AutomationProperties.SetAutomationId(favorite, ModelOverviewCatalog.FavoriteKey(model) + ":Favorite"); favorite.Click += (_, _) => FavoriteRequested?.Invoke(model); actions.Children.Add(favorite);
        var footer = new Border { Child = actions, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) }; NativeCardSurface.Divider(footer); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var card = new Border { Name = "ModelCard", Tag = model, Child = grid }; NativeCardSurface.Card(card); AutomationProperties.SetName(card, model.Label); return card;
    }
    private Button ActionButton(ChatTarget model, string action, Icon icon, IconVariant variant = IconVariant.Regular)
    {
        var button = new Button { Name = "Model" + action, Content = DesktopIcons.Create(icon, 18, variant), Width = 36, Height = 36, Padding = new Thickness(0), IsEnabled = ActionsEnabled };
        NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", DesktopResources.Get(action), model.Label));
        AutomationProperties.SetAutomationId(button, ModelOverviewCatalog.FavoriteKey(model) + ":" + action); return button;
    }
    private static Border Badge(string text, Icon? icon, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (icon is not null) content.Children.Add(DesktopIcons.Create(icon.Value, 12));
        content.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        var badge = new Border { Child = content, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 4) };
        NativeCardSurface.Badge(badge); ToolbarControls.Label(badge, label); return badge;
    }
    private UIElement ProviderIcon(ChatTarget model)
    {
        var provider = ModelProviders.Get(ModelOverviewCatalog.ProviderKey(model));
        var icon = new ProviderLogo(ProviderCatalog.Get(ModelOverviewCatalog.ProviderKey(model)), 32) { VerticalAlignment = VerticalAlignment.Top };
        if (ModelProviders.SafeWebUri(provider.Homepage) is not { } homepage) { ToolTipService.SetToolTip(icon, provider.Name); return icon; }
        var button = new Button { Name = "ModelProviderWebsite", Content = icon, Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, IsEnabled = ActionsEnabled };
        NativeCardSurface.Action(button); ToolbarControls.Label(button, DesktopResources.Format("ModelsProviderWebsite", provider.Name));
        AutomationProperties.SetAutomationId(button, ModelOverviewCatalog.FavoriteKey(model) + ":Website"); button.Click += (_, _) => WebsiteRequested?.Invoke(homepage); return button;
    }
}
