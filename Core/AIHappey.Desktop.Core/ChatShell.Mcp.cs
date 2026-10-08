using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly McpOverviewPage mcpOverview = new();
    private readonly MessageFooterPanel mcpTags = new() { Name = "McpConnectedServers" };
    private readonly ScrollViewer mcpTagScroll = new() { MaxHeight = 128, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
    private readonly MenuFlyoutItem manageMcp = new() { Name = "ManageMcpServers", Text = DesktopResources.Get("McpTitle"), Icon = new FontIcon { Glyph = "\uE774" } };
    private readonly HttpClient mcpCatalogHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private DesktopMcpManager Mcp => session.InitializeMcp();
    private McpServersDialog? mcpDialog;
    private McpCatalogResult? mcpCatalog;
    private McpTurnSnapshot? activeMcpTurn;

    private void PrepareMcp()
    {
        Mcp.Changed += McpChanged;
        mcpTagScroll.Content = mcpTags;
        ToolbarControls.Label(manageMcp, DesktopResources.Get("McpTitle"));
        manageMcp.Click += async (_, _) => await ManageMcpAsync();
        mcpOverview.RetryRequested = async () => await RunAsync(ct => LoadMcpOverviewAsync(ct));
        mcpOverview.AddRequested = async () => await ManageMcpAsync(add: true);
        mcpOverview.ManageRequested = async () => await ManageMcpAsync();
        mcpOverview.InstallRequested = async item => await RunAsync(ct => Mcp.InstallAsync(new DesktopMcpServer
        { Id = item.Id, Name = item.Name, Description = item.Description, Url = item.Url, RegistryUrl = item.RegistryUrl, Version = item.Version }, ct));
        mcpOverview.RemoveRequested = async item => await RunAsync(ct => Mcp.RemoveAsync(item.Id, ct));
    }
    private void McpChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (closing) return;
        RenderMcpTags(); mcpOverview.SetInstalled(Mcp.Servers);
        resourcesDialog?.SetCatalog(Mcp.Capture().Resources);
    });
    private void RenderMcpTags()
    {
        mcpTags.Children.Clear();
        var servers = Mcp.Servers.Where(s => s.State == McpConnectionState.Connected).ToArray();
        foreach (var view in servers)
        {
            var title = view.Discovery is { } discovery ? CatalogProjection.Text(discovery.ServerInfo, "title") : null;
            var displayName = string.IsNullOrWhiteSpace(title) ? view.Server.Name : title;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = "\uE774", FontSize = 14 });
            content.Children.Add(new TextBlock { Text = displayName, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Name = "DisconnectMcpServer", Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Width = 24, Height = 24, Padding = new Thickness(0), IsEnabled = !busy };
            ToolbarControls.Subtle(remove); ToolbarControls.Label(remove, DesktopResources.Format("McpDisconnect", displayName));
            remove.Click += async (_, _) => { await RunAsync(ct => Mcp.SetEnabledAsync(view.Server.Id, false, ct)); if (!closing) input.Focus(FocusState.Programmatic); };
            content.Children.Add(remove);
            var tag = new Border { Name = "McpConnectedBadge", Child = content, Padding = new Thickness(10, 2, 4, 2), CornerRadius = new CornerRadius(16) };
            ControlAppearance.TokenBadge(tag); ToolbarControls.Label(tag, displayName + " · " + DesktopResources.Format("McpToolsCount", view.Discovery?.Tools.Count ?? 0)); mcpTags.Children.Add(tag);
        }
        mcpTagScroll.Visibility = servers.Length > 0 && Service == ServiceKind.Ai ? Visibility.Visible : Visibility.Collapsed;
        manageMcp.Visibility = Service == ServiceKind.Ai ? Visibility.Visible : Visibility.Collapsed;
        UpdateResourceMenu();
    }
    private async Task LoadMcpOverviewAsync(CancellationToken ct, bool useCache = false)
    {
        mcpOverview.Loading();
        try
        {
            mcpCatalog = useCache && mcpCatalog is not null ? mcpCatalog
                : await new DesktopMcpCatalogClient(mcpCatalogHttp).ListAsync(session.ContextOptions.McpCatalogUrls ?? [], ct);
            ct.ThrowIfCancellationRequested();
            if (closing) return;
            mcpOverview.SetCatalog(mcpCatalog, session.ContextOptions.McpCatalogUrls ?? []);
            mcpOverview.SetInstalled(Mcp.Servers);
        }
        catch (OperationCanceledException) { mcpOverview.Error(DesktopResources.Get("CatalogCanceled")); throw; }
        catch { mcpOverview.Error(DesktopResources.Get("McpRegistryPartialFailure")); throw; }
    }
    private Task LoadActiveOverviewAsync(CancellationToken ct, bool useCache = false) => activePage == DesktopPage.Mcp
        ? LoadMcpOverviewAsync(ct, useCache) : LoadOverviewAsync(ActiveOverview, ct, useCache);

    private async Task ManageMcpAsync(bool add = false)
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null || mcpDialog is not null || !initialized) return;
        var dialog = new McpServersDialog(Mcp, add) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); mcpDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally
        {
            mcpDialog = null; historyDialogOpen = false; RenderMcpTags(); mcpOverview.SetInstalled(Mcp.Servers);
            if (!closing && activePage == DesktopPage.Chat) addContext.Focus(FocusState.Programmatic);
        }
    }
}
