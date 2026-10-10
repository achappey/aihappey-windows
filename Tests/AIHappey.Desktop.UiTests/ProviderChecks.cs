using System.Reflection;
using AIHappey.Desktop.Core;
using Flags.Icons;
using Flags.Icons.WinUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckProvidersAsync(ElementTheme theme)
    {
        var assembly = typeof(ChatShell).Assembly;
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850)); window.Activate(); await Task.Delay(80);
        var page = (UserControl)Activator.CreateInstance(assembly.GetType("AIHappey.Desktop.Core.ProvidersOverviewPage")!)!;
        root.Children.Add(page);
        var openai = ProviderCatalog.Get("openai"); var favorites = new HashSet<string>();
        var models = new[] { new ChatTarget("openai/chat", "Chat") { ModelType = "language", ProviderKey = "openai" }, new ChatTarget("openai/image", "Image") { ModelType = "image", ProviderKey = "openai" } };
        InvokeOverview(page, "SetItems", ProviderCatalog.All.Values, favorites, models); await Task.Delay(160); page.UpdateLayout();
        var context = "Providers / " + theme;
        Check(CardCount(page, "ProviderCard") == 50, context + ": initial catalog batch contains 50 native cards");
        var tabs = Descendants(page).OfType<NavigationView>().Single(v => v.Name == "ProviderTabs"); CheckNativeTabs(tabs, context);
        var search = Descendants(page).OfType<TextBox>().Single(v => v.Name == "ProviderSearch");
        Check(AutomationProperties.GetName(search).Length > 0, context + ": search has an accessible label");
        var more = Descendants(page).OfType<Button>().Single(v => v.Name == "ProvidersShowMore"); InvokeButton(more); await Task.Delay(80);
        Check(CardCount(page, "ProviderCard") == 100, context + ": Show more extends the complete sorted catalog");
        search.Text = "https://openai.com"; await Task.Delay(80); page.UpdateLayout();
        var card = Descendants(page).OfType<Border>().Single(c => c.Name == "ProviderCard" && ((CatalogProvider)c.Tag).Id == "openai");
        CheckProviderFlag(card, LipisFlag.US, context);
        var actions = Descendants(card).OfType<Button>().ToArray();
        Check(actions.Length == 8 && actions.All(b => AutomationProperties.GetName(b).Contains("OpenAI")), context + ": View, six links and Favorite are accessible native actions");
        Uri? requested = null;
        page.GetType().GetProperty("LinkRequested")!.SetValue(page, (Action<Uri>)(uri => requested = uri));
        InvokeButton(actions.Single(b => AutomationProperties.GetAutomationId(b) == "openai:pricing")); await Task.Delay(30);
        Check(requested?.AbsoluteUri == openai.Urls.Pricing, context + ": pricing action uses the catalog URL");
        page.GetType().GetProperty("FavoriteRequested")!.SetValue(page, (Action<CatalogProvider>)(p => { favorites.Add(p.Id); InvokeOverview(page, "SetFavorites", favorites); }));
        InvokeButton(actions.Single(b => AutomationProperties.GetAutomationId(b) == "openai:Favorite")); await Task.Delay(80);
        tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(v => v.Name == "ProvidersFavorites"); await Task.Delay(80);
        Check(CardCount(page, "ProviderCard") == 1, context + ": Favorites displays saved provider identity");
        InvokeOverview(page, "SetActionsEnabled", false); InvokeOverview(page, "SetFavorites", favorites);
        Check(!tabs.IsEnabled && !search.IsEnabled && Descendants(page).OfType<Button>().Where(b => b.Name == "ProviderAction" || b.Name == "ProviderFavorite").All(b => !b.IsEnabled), context + ": refresh preserves busy action gating");
        InvokeOverview(page, "SetActionsEnabled", true);
        tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(v => v.Name == "ProvidersAll"); search.Text = "";
        page.GetType().GetProperty("FiltersOpen")!.SetValue(page, true); await Task.Delay(80); page.UpdateLayout();
        var filter = Field<ProviderOverviewFilter>(page, "Filter"); filter.Countries.Add("US"); filter.ModelTypes.Add("image");
        InvokeOverview(page, "SetItems", ProviderCatalog.All.Values, favorites, models); await Task.Delay(80);
        Check(CardCount(page, "ProviderCard") == 1, context + ": metadata and discovered-model filters combine correctly");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 850)); await Task.Delay(100); page.UpdateLayout();
        page.GetType().GetProperty("FiltersOpen")!.SetValue(page, false); await Task.Delay(50);
        Check(Descendants(page).OfType<Border>().Where(c => c.Name == "ProviderCard").All(c => c.ActualWidth <= root.ActualWidth), context + ": narrow cards and wrapped footer fit the viewport");
        CheckProviderFlag(Descendants(page).OfType<Border>().Single(c => c.Name == "ProviderCard"), LipisFlag.US, context + " / narrow");
        var nativeLogo = Descendants(page).OfType<Grid>().Single(g => g.GetType().Name == "ProviderLogo");
        Check(nativeLogo.Children.OfType<Image>().Single().Visibility == Visibility.Collapsed && nativeLogo.Children.OfType<IconElement>().Single().Visibility == Visibility.Visible,
            context + ": offline remote-image switch keeps native placeholder visible");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850)); await Task.Delay(80);
        var dialog = (ContentDialog)Activator.CreateInstance(assembly.GetType("AIHappey.Desktop.Core.ProviderDetailsDialog")!, openai, new[] { "image", "language" })!;
        dialog.XamlRoot = root.XamlRoot; dialog.RequestedTheme = theme; SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(100); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<TextBlock>().Any(t => t.Name == "ProviderFullDescription" && t.Text == openai.Description)
            && Descendants(dialog).OfType<Button>().Count(b => b.Name == "ProviderDetailLink") == 6, context + ": details show full description and all links");
        Check(!Descendants(dialog).OfType<PasswordBox>().Any(), context + ": provider configuration is intentionally absent");
        dialog.Hide(); await shown;
        InvokeOverview(page, "Reset"); Check(CardCount(page, "ProviderCard") == 0 && filter.Countries.Count == 0 && filter.ModelTypes.Count == 0, context + ": account invalidation clears favorites and filters");
        string?[] countryCodes = [" nl ", null, "", " ", "ZZ", "12", "None", "US,NL"];
        var fixtures = countryCodes.Select((country, index) => new CatalogProvider { Id = "flag-fixture-" + index,
            Name = "Country flag fixture " + index, ProviderCountry = country }).ToArray();
        InvokeOverview(page, "SetItems", fixtures, new HashSet<string>(), Array.Empty<ChatTarget>()); await Task.Delay(100); page.UpdateLayout();
        var fixtureCards = Descendants(page).OfType<Border>().Where(c => c.Name == "ProviderCard").ToArray();
        CheckProviderFlag(fixtureCards.Single(c => ((CatalogProvider)c.Tag).Id == fixtures[0].Id), LipisFlag.NL, context + " / normalized");
        Check(fixtureCards.Where(c => ((CatalogProvider)c.Tag).Id != fixtures[0].Id).All(c => !Descendants(c).OfType<FlagIcon>().Any()),
            context + ": missing, empty, unsupported, numeric and multiple country codes do not create flags");
        InvokeOverview(page, "Reset");
        root.Children.Clear();

        // Embedded images are still permitted with all remote requests disabled.
        var embeddedProvider = ProviderCatalog.Get("echo");
        var logo = (Grid)Activator.CreateInstance(assembly.GetType("AIHappey.Desktop.Core.ProviderLogo")!, embeddedProvider, 48d)!;
        root.Children.Add(logo); await Task.Delay(180);
        Check(logo.Children.OfType<Image>().Single().Visibility == Visibility.Visible, context + ": bundled PNG logo decodes natively while offline");
        root.Children.Clear();

        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new()); var shell = new ChatShell(session) { RequestedTheme = theme };
        try
        {
            var ai = Field<StackPanel>(shell, "aiNavigation");
            Check(ai.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Select(b => b.Name).SequenceEqual(["NavigateModels", "NavigateProviders"]), context + ": Providers is beside Models in the AI navigation group");
            var providersButton = ai.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Single(b => b.Name == "NavigateProviders");
            var mcpButton = Field<StackPanel>(shell, "expandedPageNavigation").Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Single(b => b.Name == "NavigateMcp");
            var providerIcon = ((StackPanel)providersButton.Content).Children.OfType<PathIcon>().Single();
            var mcpIcon = ((StackPanel)mcpButton.Content).Children.OfType<PathIcon>().Single();
            Check(providerIcon.Data is PathGeometry { Figures.Count: 2 } && mcpIcon.Data is PathGeometry { Figures.Count: 4 }
                && providerIcon.Width == 20 && providerIcon.Height == 20 && mcpIcon.Width == 20 && mcpIcon.Height == 20
                && providerIcon.Foreground is SolidColorBrush providerFill && providersButton.Foreground is SolidColorBrush providerText && providerFill.Color == providerText.Color
                && mcpIcon.Foreground is SolidColorBrush mcpFill && mcpButton.Foreground is SolidColorBrush mcpText && mcpFill.Color == mcpText.Color,
                context + ": Providers cloud and MCP connector are distinct theme-colored native vector icons");
            Check(Field<MenuFlyoutItem>(shell, "manageMcp").Icon is PathIcon menuIcon
                && menuIcon.Data is PathGeometry { Figures.Count: 4 } && !ReferenceEquals(menuIcon, mcpIcon)
                && AutomationProperties.GetName(providersButton) == DesktopResources.Get("Providers")
                && AutomationProperties.GetName(mcpButton) == DesktopResources.Get("McpTitle")
                && ToolTipService.GetToolTip(providersButton) is ToolTip && ToolTipService.GetToolTip(mcpButton) is ToolTip,
                context + ": MCP menu uses the matching connector while navigation keeps accessible labels and tooltips");
            var pageEnum = assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
            typeof(ChatShell).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [Enum.Parse(pageEnum, "Providers")]);
            Check(Field<UserControl>(shell, "providersOverview").Visibility == Visibility.Visible && Field<UserControl>(shell, "modelsOverview").Visibility == Visibility.Collapsed
                && Field<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(shell, "providerFilters").Visibility == Visibility.Visible, context + ": shell displays only provider page and provider toolbar");
            typeof(ChatShell).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, true);
            typeof(ChatShell).GetField("aiModelTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, models);
            root.Children.Add(shell); await Task.Delay(120);
            typeof(ChatShell).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [Enum.Parse(pageEnum, "Providers")]);
            await (Task)typeof(ChatShell).GetMethod("LoadProvidersOverviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [CancellationToken.None, true])!;
            var shellPage = Field<UserControl>(shell, "providersOverview");
            var shellSearch = Field<TextBox>(shellPage, "SearchBox"); shellSearch.Text = "https://openai.com"; await Task.Delay(100); shell.UpdateLayout();
            Check(Field<Panel>(shellPage, "Cards").Children.Count == 1, context + $": shell catalog search selects the provider card (text={shellSearch.Text}, filter={Field<ProviderOverviewFilter>(shellPage, "Filter").Search}, enabled={shellSearch.IsEnabled}, cards={Field<Panel>(shellPage, "Cards").Children.Count})");
            var view = Descendants(shellPage).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "openai:View");
            view.Focus(FocusState.Programmatic); InvokeButton(view); await Task.Delay(150);
            var shellDialog = Field<ContentDialog>(shell, "providerDialog");
            Check(shellDialog.IsLoaded && Field<bool>(shell, "historyDialogOpen"), context + ": shell View opens a native modal and guards other dialogs");
            shellDialog.Hide();
            for (var attempt = 0; attempt < 30 && Field<bool>(shell, "historyDialogOpen"); attempt++) await Task.Delay(50);
            Check(!Field<bool>(shell, "historyDialogOpen") && ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), view), context + ": closing details restores keyboard focus to the card action");
            root.Children.Clear();
        }
        finally { await shell.ShutdownAsync(); }
    }

    private void CheckProviderFlag(Border card, LipisFlag expected, string context)
    {
        var flag = Descendants(card).OfType<FlagIcon>().Single(f => f.Name == "ProviderCountryFlag");
        var header = (Grid)flag.Parent;
        var labels = header.Children.OfType<StackPanel>().Single();
        var flagPoint = flag.TransformToVisual(header).TransformPoint(new());
        var labelsPoint = labels.TransformToVisual(header).TransformPoint(new());
        Check(flag.Lipis == expected && flag.ActualWidth == 24 && flag.ActualHeight == 18
            && Descendants(flag).OfType<Image>().Any(i => i.Source is Microsoft.UI.Xaml.Media.Imaging.SvgImageSource),
            context + ": country uses the compact embedded rectangular SVG flag");
        Check(Grid.GetColumn(flag) == 2 && flag.VerticalAlignment == VerticalAlignment.Top
            && Math.Abs(flagPoint.Y) < 1 && Math.Abs(flagPoint.X + flag.ActualWidth - header.ActualWidth) < 1
            && labelsPoint.X + labels.ActualWidth <= flagPoint.X && Within(flag, card),
            context + ": flag fits the top-right header corner without overlapping provider labels");
        Check(labels.Children.Count == 2 && labels.Children[0] is TextBlock { Name: "ProviderName" },
            context + ": provider header retains name and badges without a third country text row");
        Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(flag))
            && ToolTipService.GetToolTip(flag) as string == AutomationProperties.GetName(flag),
            context + ": flag retains the country in its accessible label and native tooltip");
    }
}
