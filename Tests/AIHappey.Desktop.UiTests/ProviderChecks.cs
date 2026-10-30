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
        foreach (var type in new[] { "language", "image" })
        {
            var label = DesktopResources.Get("AiModelType_" + type);
            var badge = Descendants(card).OfType<Border>().Single(border => AutomationProperties.GetName(border) == label);
            Check(Descendants(badge).OfType<FluentIcons.WinUI.FluentIcon>().Count() == 1
                && Descendants(badge).OfType<TextBlock>().Any(text => text.Text == label), context + ": provider " + type + " badge has a native icon and accessible text");
        }
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
        var detailModels = Enumerable.Range(1, 53).Select(i => new ChatTarget("openai/model-" + i, "Model " + i) { ModelType = "language", ProviderKey = "openai" })
            .Concat(models.Where(model => model.ModelType != "language"))
            .Concat(AiModelCatalog.Types.Where(type => type is not "language" and not "image").Select(type => new ChatTarget("openai/" + type, type) { ModelType = type }))
            .Append(new ChatTarget("openai/not-owned", "Other provider") { ModelType = "language", ProviderKey = "anthropic" })
            .Append(new ChatTarget("gateway/explicit-owner", "Explicit OpenAI owner") { ModelType = "language", ProviderKey = "openai", DisplayId = "provider-model-id" }).ToArray();
        var modelFavorites = new HashSet<string>();
        var dialog = (ContentDialog)Activator.CreateInstance(assembly.GetType("AIHappey.Desktop.Core.ProviderDetailsDialog")!, openai, detailModels, modelFavorites)!;
        dialog.XamlRoot = root.XamlRoot; dialog.RequestedTheme = theme; SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(100); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<TextBlock>().Any(t => t.Name == "ProviderFullDescription" && t.Text == openai.Description)
            && Descendants(dialog).OfType<Button>().Count(b => b.Name == "ProviderDetailLink") == 6, context + ": details show full description and all links");
        Check(!Descendants(dialog).OfType<PasswordBox>().Any(), context + ": provider configuration is intentionally absent");
        var detailTabs = Field<NavigationView>(dialog, "tabs"); CheckNativeTabs(detailTabs, context + " details");
        Check(detailTabs.MenuItems.Count == AiModelCatalog.Types.Count + 1 && ((NavigationViewItem)detailTabs.SelectedItem).Tag as string == "general",
            context + ": provider details open on General and expose every discovered type");
        var detailHeader = Descendants(dialog).OfType<Grid>().Single(grid => grid.Name == "ProviderDetailsHeader");
        var metadata = Descendants(detailHeader).OfType<Panel>().Single(panel => panel is FrameworkElement { Name: "ProviderDetailsMetadata" });
        Check(Grid.GetColumn(metadata) == 1 && Descendants(metadata).OfType<Border>().Any(border => border.Name == "ProviderDetailCategory")
            && Descendants(metadata).OfType<Border>().Any(border => border.Name == "ProviderDetailCountry")
            && Descendants(metadata).OfType<Border>().Count(border => border.Name == "ProviderDetailRegion") == openai.InferenceRegions.Count,
            context + ": category, country and inference-region badges sit beside the logo");
        Check(!Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == DesktopResources.Get("ProviderAvailableModelTypes")), context + ": redundant available-model-types field is absent");
        ChatTarget? copied = null, launched = null;
        dialog.GetType().GetProperty("CopyRequested")!.SetValue(dialog, (Action<ChatTarget>)(model => copied = model));
        dialog.GetType().GetProperty("LaunchRequested")!.SetValue(dialog, (Action<ChatTarget>)(model => launched = model));
        dialog.GetType().GetProperty("FavoriteRequested")!.SetValue(dialog, (Action<ChatTarget>)(model =>
        {
            if (!modelFavorites.Remove(ModelOverviewCatalog.FavoriteKey(model))) modelFavorites.Add(ModelOverviewCatalog.FavoriteKey(model));
            InvokeOverview(dialog, "SetFavorites", modelFavorites);
        }));
        foreach (var type in AiModelCatalog.Types)
        {
            SelectProviderDetailTab(detailTabs, type); await Task.Delay(60); dialog.UpdateLayout();
            var typeCards = Descendants(dialog).OfType<Border>().Where(border => border.Name == "ModelCard").ToArray();
            Check(typeCards.Length == (type == "language" ? 50 : 1) && typeCards.All(border => ((ChatTarget)border.Tag).ModelType == type
                && ModelOverviewCatalog.ProviderKey((ChatTarget)border.Tag) == "openai"), context + ": " + type + " tab contains only matching provider/type models with bounded rendering");
            Check(((NavigationViewItem)detailTabs.SelectedItem).Icon is FluentIcons.WinUI.FluentIcon
                && ((NavigationViewItem)detailTabs.SelectedItem).Content.ToString()!.Contains(type == "language" ? "(54)" : "(1)"), context + ": " + type + " tab has its native icon and full model count");
            Check(typeCards.All(border => Descendants(border).OfType<Button>().Any(button => button.Name == "ModelLaunch") == ModelOverviewCatalog.CanLaunch((ChatTarget)border.Tag)),
                context + ": " + type + " cards only expose supported desktop launch actions");
        }
        SelectProviderDetailTab(detailTabs, "language"); await Task.Delay(60);
        InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "ProviderModelsShowMore")); await Task.Delay(60);
        Check(CardCount(dialog, "ModelCard") == 54, context + ": Show more exposes the complete provider model list without prefix-based ownership leakage");
        var explicitCard = Descendants(dialog).OfType<Border>().Single(border => border.Name == "ModelCard" && ((ChatTarget)border.Tag).Id == "gateway/explicit-owner");
        var explicitButtons = Descendants(explicitCard).OfType<Button>().ToArray();
        InvokeButton(explicitButtons.Single(button => button.Name == "ModelCopy"));
        InvokeButton(explicitButtons.Single(button => button.Name == "ModelLaunch"));
        var favoriteAction = explicitButtons.Single(button => button.Name == "ModelFavorite"); favoriteAction.Focus(FocusState.Programmatic); InvokeButton(favoriteAction); await Task.Delay(50);
        Check(copied?.DisplayId == "provider-model-id" && launched?.Id == "gateway/explicit-owner"
            && modelFavorites.Contains(ModelOverviewCatalog.FavoriteKey(copied!)) && ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), favoriteAction),
            context + ": copy/launch preserve canonical model data and favorite updates retain keyboard focus");
        InvokeOverview(dialog, "SetActionsEnabled", false);
        Check(!detailTabs.IsEnabled && explicitButtons.All(button => !button.IsEnabled), context + ": busy gating disables all provider-model actions");
        InvokeOverview(dialog, "SetActionsEnabled", true);
        foreach (var width in new[] { 420, 1100 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 850)); await Task.Delay(120); dialog.UpdateLayout();
            var detailScroll = Field<ScrollViewer>(dialog, "page");
            Check(detailScroll.ScrollableWidth < 1 && Descendants(dialog).OfType<Border>().Where(border => border.Name == "ModelCard").All(border => border.ActualWidth <= detailScroll.ViewportWidth + 1),
                context + $": model cards fit the {width}px viewport without horizontal scrolling");
            SelectProviderDetailTab(detailTabs, "general"); await Task.Delay(60); dialog.UpdateLayout();
            Check(detailHeader.ActualWidth <= detailScroll.ViewportWidth + 1 && metadata.ActualWidth > 0, context + $": metadata wraps beside the logo at {width}px");
            SelectProviderDetailTab(detailTabs, "language"); await Task.Delay(60);
        }
        dialog.Hide(); await shown;
        var empty = (ContentDialog)Activator.CreateInstance(dialog.GetType(), openai, Array.Empty<ChatTarget>(), modelFavorites)!;
        empty.XamlRoot = root.XamlRoot; shown = empty.ShowAsync(); await Task.Delay(100);
        SelectProviderDetailTab(Field<NavigationView>(empty, "tabs"), "models"); await Task.Delay(60);
        Check(CardCount(empty, "ModelCard") == 0 && Descendants(empty).OfType<TextBlock>().Any(text => text.Name == "ProviderModelsEmpty"), context + ": offline/empty providers retain an explicit Models empty state");
        empty.Hide(); await shown;
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
        var logo = (Grid)Activator.CreateInstance(assembly.GetType("AIHappey.Desktop.Core.ProviderLogo")!, embeddedProvider, 48d, true)!;
        root.Children.Add(logo); await Task.Delay(180);
        Check(logo.Children.OfType<Image>().Single().Visibility == Visibility.Visible, context + ": bundled PNG logo decodes natively while offline");
        root.Children.Clear();
        await CheckProviderLogoLoadingAsync(root, theme);

        var session = new DesktopSession(new ProviderUiHost(), new AgentUiRuntime(), new()); var shell = new ChatShell(session) { RequestedTheme = theme };
        using var modelHandler = new ProviderModelsHandler(() => !Field<bool>(shell, "historyDialogOpen"));
        using var modelHttp = new HttpClient(modelHandler);
        typeof(ChatShell).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, new DesktopChatClient(session, modelHttp));
        try
        {
            // This fixture supplies discovery explicitly; realize the actual shell before
            // checking inherited theme brushes or invoking visible navigation controls.
            typeof(ChatShell).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, true);
            var shellRoot = (Grid)shell.Content;
            shellRoot.Children[0].Visibility = Visibility.Visible; shellRoot.Children[1].Visibility = Visibility.Collapsed;
            root.Children.Add(shell); await Task.Delay(120); shell.UpdateLayout();
            var ai = Field<StackPanel>(shell, "aiNavigation");
            Check(ai.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Select(b => b.Name).SequenceEqual(["NavigateModels", "NavigateProviders", "NavigateUsage"]), context + ": Providers and Usage are beside Models in the AI navigation group");
            var providersButton = ai.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Single(b => b.Name == "NavigateProviders");
            var mcpButton = Field<System.Collections.IDictionary>(shell, "pageButtons").Values.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Single(b => b.Name == "NavigateMcp");
            var providerIcon = ((StackPanel)providersButton.Content).Children.OfType<FluentIcons.WinUI.FluentIcon>().Single();
            var mcpIcon = ((StackPanel)mcpButton.Content).Children.OfType<FluentIcons.WinUI.FluentIcon>().Single();
            Check(providerIcon.Icon == FluentIcons.Common.Icon.Cloud && mcpIcon.Icon == FluentIcons.Common.Icon.Connector
                && providerIcon.FontSize == 20 && mcpIcon.FontSize == 20
                && providerIcon.ReadLocalValue(IconElement.ForegroundProperty) == DependencyProperty.UnsetValue
                && mcpIcon.ReadLocalValue(IconElement.ForegroundProperty) == DependencyProperty.UnsetValue
                && providerIcon.Foreground is SolidColorBrush providerFill && providersButton.Foreground is SolidColorBrush providerText && providerFill.Color == providerText.Color
                && mcpIcon.Foreground is SolidColorBrush mcpFill && mcpButton.Foreground is SolidColorBrush mcpText && mcpFill.Color == mcpText.Color,
                context + ": Providers cloud and MCP connector are distinct inherited-theme Fluent icons");
            Check(Field<MenuFlyoutItem>(shell, "manageMcp").Icon is FluentIcons.WinUI.FluentIcon menuIcon
                && menuIcon.Icon == FluentIcons.Common.Icon.Connector && !ReferenceEquals(menuIcon, mcpIcon)
                && AutomationProperties.GetName(providersButton) == DesktopResources.Get("Providers")
                && AutomationProperties.GetName(mcpButton) == DesktopResources.Get("McpTitle")
                && ToolTipService.GetToolTip(providersButton) is ToolTip && ToolTipService.GetToolTip(mcpButton) is ToolTip,
                context + ": MCP menu uses the matching connector while navigation keeps accessible labels and tooltips");
            var pageEnum = assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
            typeof(ChatShell).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [Enum.Parse(pageEnum, "Providers")]);
            Check(Field<UserControl>(shell, "providersOverview").Visibility == Visibility.Visible && Field<UserControl>(shell, "modelsOverview").Visibility == Visibility.Collapsed
                && Field<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(shell, "providerFilters").Visibility == Visibility.Visible, context + ": shell displays only provider page and provider toolbar");
            typeof(ChatShell).GetField("aiModelTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, models);
            typeof(ChatShell).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [Enum.Parse(pageEnum, "Providers")]);
            await (Task)typeof(ChatShell).GetMethod("LoadProvidersOverviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [CancellationToken.None, true])!;
            var shellPage = Field<UserControl>(shell, "providersOverview");
            var shellSearch = Field<TextBox>(shellPage, "SearchBox"); shellSearch.Text = "https://openai.com"; await Task.Delay(100); shell.UpdateLayout();
            Check(Field<Panel>(shellPage, "Cards").Children.Count == 1, context + $": shell catalog search selects the provider card (text={shellSearch.Text}, filter={Field<ProviderOverviewFilter>(shellPage, "Filter").Search}, enabled={shellSearch.IsEnabled}, cards={Field<Panel>(shellPage, "Cards").Children.Count})");
            var view = Descendants(shellPage).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "openai:View");
            view.Focus(FocusState.Programmatic); InvokeButton(view);
            await WaitForProviderUiAsync(() => ProviderDialogPresented(shell), "Provider dialog did not open");
            var shellDialog = Field<ContentDialog>(shell, "providerDialog");
            Check(shellDialog.IsLoaded && Field<bool>(shell, "historyDialogOpen"), context + ": shell View opens a native modal and guards other dialogs");
            SelectProviderDetailTab(Field<NavigationView>(shellDialog, "tabs"), "language"); await Task.Delay(80);
            await WaitForProviderUiAsync(() => Descendants(shellDialog).OfType<Button>().Any(button => button.Name == "ModelFavorite"), "Provider model card did not render");
            var shellFavorite = Descendants(shellDialog).OfType<Button>().Single(button => button.Name == "ModelFavorite");
            InvokeButton(shellFavorite);
            await WaitForProviderUiAsync(() => !Field<bool>(shell, "busy") && Field<HashSet<string>>(shell, "modelFavorites").Contains(ModelOverviewCatalog.FavoriteKey(models[0])), "Provider model favorite was not persisted");
            Check(Field<HashSet<string>>(shell, "modelFavorites").Contains(ModelOverviewCatalog.FavoriteKey(models[0]))
                && ((FluentIcons.WinUI.FluentIcon)shellFavorite.Content).IconVariant == FluentIcons.Common.IconVariant.Filled,
                context + ": provider model favorite persists through the shell and updates the existing card");
            shellDialog.Hide();
            for (var attempt = 0; attempt < 30 && Field<bool>(shell, "historyDialogOpen"); attempt++) await Task.Delay(50);
            Check(!Field<bool>(shell, "historyDialogOpen") && ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), view), context + ": closing details restores keyboard focus to the card action");
            InvokeButton(view);
            await WaitForProviderUiAsync(() => ProviderDialogPresented(shell), "Provider dialog did not reopen");
            shellDialog = Field<ContentDialog>(shell, "providerDialog");
            SelectProviderDetailTab(Field<NavigationView>(shellDialog, "tabs"), "language"); await Task.Delay(80);
            await WaitForProviderUiAsync(() => Descendants(shellDialog).OfType<Button>().Any(button => button.Name == "ModelLaunch"), "Provider launch action did not render");
            var launchAction = Descendants(shellDialog).OfType<Button>().Single(button => button.Name == "ModelLaunch");
            InvokeButton(launchAction);
            for (var attempt = 0; attempt < 60 && (Field<bool>(shell, "historyDialogOpen") || Field<bool>(shell, "busy") || Field<object>(shell, "activePage").ToString() != "Chat"); attempt++) await Task.Delay(50);
            Check(modelHandler.Requests == 1 && modelHandler.ModalClosedBeforeDiscovery && !Field<bool>(shell, "historyDialogOpen")
                && Field<object>(shell, "activePage").ToString() == "Chat" && Field<Conversation>(shell, "current").Target == models[0].Id,
                context + ": launch waits for modal teardown, revalidates discovery and uses the selected model in Chat");
            var beforeStale = Field<Conversation>(shell, "current");
            Call(shell, "ShowPage", Enum.Parse(pageEnum, "Providers"));
            typeof(ChatShell).GetField("aiModelTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, models);
            InvokeButton(view);
            await WaitForProviderUiAsync(() => ProviderDialogPresented(shell), "Provider dialog did not reopen for stale-model validation");
            shellDialog = Field<ContentDialog>(shell, "providerDialog");
            SelectProviderDetailTab(Field<NavigationView>(shellDialog, "tabs"), "language"); await Task.Delay(60);
            await WaitForProviderUiAsync(() => Descendants(shellDialog).OfType<Button>().Any(button => button.Name == "ModelLaunch"), "Stale-model launch action did not render");
            modelHandler.Available = false;
            InvokeButton(Descendants(shellDialog).OfType<Button>().Single(button => button.Name == "ModelLaunch"));
            await WaitForProviderUiAsync(() => modelHandler.Requests == 2 && !Field<bool>(shell, "historyDialogOpen") && !Field<bool>(shell, "busy"), "Stale-model discovery did not complete");
            Check(modelHandler.Requests == 2 && Field<object>(shell, "activePage").ToString() == "Providers"
                && ReferenceEquals(beforeStale, Field<Conversation>(shell, "current")), context + ": disappeared models cannot launch or replace the current conversation");
            root.Children.Clear();
        }
        finally { await shell.ShutdownAsync(); }
    }

    private static void SelectProviderDetailTab(NavigationView tabs, string key)
    {
        // Overflow items need not have realized automation peers until their menu opens.
        tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == key);
    }

    private static async Task WaitForProviderUiAsync(Func<bool> ready, string message)
    {
        for (var attempt = 0; attempt < 150 && !ready(); attempt++) await Task.Delay(30);
        if (!ready()) throw new InvalidOperationException(message);
    }

    private static bool ProviderDialogPresented(ChatShell shell) =>
        typeof(ChatShell).GetField("providerDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell) is ContentDialog dialog
        && Field<bool>(dialog, "IsPresented");

    private sealed class ProviderUiHost : IDesktopHost
    {
        public string ProfileId { get; } = "provider-ui-" + Guid.NewGuid().ToString("N");
        public bool AllowLocal => true;
        public string AccountLabel => "Provider fixture";
        public string HistoryIdentity => "provider-fixture";
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => Task.CompletedTask;
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ProviderModelsHandler(Func<bool> modalClosed) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool ModalClosedBeforeDiscovery { get; private set; } = true;
        public bool Available { get; set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/v1/models") throw new InvalidOperationException("Provider fixtures only serve model discovery.");
            Requests++; ModalClosedBeforeDiscovery &= modalClosed();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Available
                ? """{"data":[{"id":"openai/chat","name":"Chat","type":"language"},{"id":"openai/image","name":"Image","type":"image"}]}"""
                : """{"data":[]}""", System.Text.Encoding.UTF8, "application/json") });
        }
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
