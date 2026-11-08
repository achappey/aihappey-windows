using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Focused MCP discovery, sharing its server rows with composer search and management.</summary>
internal sealed class McpCatalogDialog : ContentDialog, IResponsiveDialog
{
    private readonly Func<CancellationToken, Task<McpCatalogResult>> loadCatalog;
    private readonly Func<IReadOnlyList<DesktopMcpServer>> installed;
    private readonly Func<McpCatalogItem, bool, CancellationToken, Task> changeServer;
    private readonly bool hasSources;
    private readonly McpDialogState state;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly TextBox search = new() { Name = "McpCatalogQuery", PlaceholderText = DesktopResources.Get("SearchPlaceholder") };
    private readonly ProgressBar progress = new() { Name = "McpCatalogProgress", Height = 4, Visibility = Visibility.Collapsed };
    private readonly ScrollViewer viewer = new() { HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel body = new() { Spacing = 12 };
    private readonly StackPanel rows = new() { Name = "McpCatalogRows", Spacing = 12 };
    private readonly TextBlock status = new() { Name = "McpCatalogStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { Name = "McpCatalogError", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button retry = new() { Name = "McpCatalogRetry", Content = DesktopResources.Get("Retry"), Visibility = Visibility.Collapsed };
    private readonly Button more = new() { Name = "McpCatalogMore", Content = DesktopResources.Get("ShowMore"), Visibility = Visibility.Collapsed };
    private bool loading, pending, closed;
    private string? warning;
    public DesktopMcpServer? ServerConfigurationRequested { get; private set; }

    public McpCatalogDialog(Func<CancellationToken, Task<McpCatalogResult>> loadCatalog,
        Func<IReadOnlyList<DesktopMcpServer>> installed, Func<McpCatalogItem, bool, CancellationToken, Task> changeServer,
        bool hasSources, McpDialogState state)
    {
        this.loadCatalog = loadCatalog; this.installed = installed; this.changeServer = changeServer;
        this.hasSources = hasSources; this.state = state;
        Name = "McpCatalogDialog"; Title = DesktopResources.Get("PluginMcpCatalog"); CloseButtonText = DesktopResources.Get("Close");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        search.Text = state.Query; ControlAppearance.Stock(search); ToolbarControls.Label(search, DesktopResources.Get("McpSearch"));
        layout.Children.Add(search); Grid.SetRow(progress, 1); layout.Children.Add(progress);
        body.Children.Add(status); body.Children.Add(retry); body.Children.Add(rows); body.Children.Add(more);
        viewer.Content = body; Grid.SetRow(viewer, 2); layout.Children.Add(viewer);
        Grid.SetRow(error, 3); layout.Children.Add(error); Content = layout;
        AutomationProperties.SetName(progress, DesktopResources.Get("Loading"));
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        ControlAppearance.Stock(retry); ControlAppearance.Stock(more);
        search.TextChanged += (_, _) =>
        {
            state.Query = search.Text; state.Visible = 20; state.Offset = 0; state.FocusId = null;
            Render(); viewer.ChangeView(null, 0, null, true);
        };
        retry.Click += async (_, _) => await LoadAsync();
        more.Click += (_, _) => { state.Visible += 20; Render(); };
        Opened += (_, _) =>
        {
            SizeToRoot(); XamlRoot.Changed += RootChanged;
            if (state.FocusId is null) search.Focus(FocusState.Programmatic);
            RestorePresentation(state.FocusId, state.Offset);
            if (state.Catalog is null) _ = LoadAsync();
        };
        Closing += (_, args) =>
        {
            if (pending && !closed) { args.Cancel = true; return; }
            state.Offset = viewer.VerticalOffset;
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); StopProgress(); XamlRoot.Changed -= RootChanged; };
        warning = state.Catalog?.FailedSources.Count > 0 ? DesktopResources.Get("McpRegistryPartialFailure") : null;
        Render();
    }

    public void Shutdown() { closed = true; lifetime.Cancel(); StopProgress(); Hide(); }
    public void RefreshInstalled() { if (!closed && !pending) Render(); }
    private async Task LoadAsync()
    {
        if (closed || loading || pending) return;
        loading = true; Render();
        try
        {
            var catalog = await loadCatalog(lifetime.Token); lifetime.Token.ThrowIfCancellationRequested();
            state.Catalog = catalog;
            warning = catalog.FailedSources.Count > 0 ? DesktopResources.Get("McpRegistryPartialFailure") : null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { warning = DesktopResources.Get("McpRegistryPartialFailure"); }
        finally { loading = false; if (!closed) Render(); }
    }
    private void Render()
    {
        if (closed) return;
        var offset = viewer.VerticalOffset;
        var focus = XamlRoot is null ? null : (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is Control control
            ? AutomationProperties.GetAutomationId(control) : null);
        rows.Children.Clear();
        var query = search.Text.Trim();
        var items = (state.Catalog?.Items ?? []).Where(item => string.Join(" ", item.Id, item.Name, item.Description,
            item.Version, item.Url, item.RegistryUrl).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var servers = installed();
        foreach (var item in items.Take(state.Visible))
        {
            var existing = McpServerRow.Installed(servers, item);
            McpServerRow.Catalog(rows, "McpCatalogRow", "McpCatalog", item, existing, state.Expanded,
                async install => await ChangeAsync(item, install), () => Configure(item, existing));
        }
        status.Text = warning ?? (loading ? DesktopResources.Get("Loading") : items.Length == 0
            ? DesktopResources.Get(hasSources ? "NoResults" : "McpNoRegistries") : "");
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        retry.Visibility = warning is not null && !loading ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = items.Length > state.Visible ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = loading || pending;
        progress.Visibility = progress.IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;
        search.IsEnabled = viewer.IsEnabled = !pending;
        if (IsLoaded) RestorePresentation(focus, offset);
    }
    private void Configure(McpCatalogItem item, DesktopMcpServer? existing)
    {
        if (closed || pending || existing is null || existing.PluginId is not null) return;
        ServerConfigurationRequested = existing.Clone();
        if (existing.Url == item.Url && item.HeaderDefinitions.Count > 0)
        { ServerConfigurationRequested.HeaderDefinitions = item.HeaderDefinitions.ToArray(); ServerConfigurationRequested.OAuthManaged = item.OAuthManaged; }
        state.FocusId = "McpCatalogConfigure_" + item.Id; Hide();
    }
    private async Task ChangeAsync(McpCatalogItem item, bool install)
    {
        if (closed || pending || McpServerRow.Installed(installed(), item)?.PluginId is not null) return;
        state.FocusId = "McpCatalogAction_" + item.Id;
        if (install && McpHeaderConfiguration.RequiresConfiguration(item))
        { ServerConfigurationRequested = item.CreateServer(); Hide(); return; }
        pending = true; error.Visibility = Visibility.Collapsed; Render();
        try { await changeServer(item, install, lifetime.Token); lifetime.Token.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (!closed) { error.Text = DesktopMcpManager.SafeError(e); error.Visibility = Visibility.Visible; }
        }
        finally
        {
            pending = false;
            if (!closed) { Render(); RestorePresentation(state.FocusId, viewer.VerticalOffset); }
        }
    }
    private void RestorePresentation(string? id, double offset) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed || pending) return;
        layout.UpdateLayout(); viewer.ChangeView(null, offset, null, true);
        if (string.IsNullOrEmpty(id)) return;
        if (ControlAppearance.Descendants(this).OfType<Control>().FirstOrDefault(control =>
            AutomationProperties.GetAutomationId(control) == id) is { } target)
        { target.Focus(FocusState.Programmatic); return; }
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is not Control focused
            || !ControlAppearance.Descendants(this).Contains(focused)) search.Focus(FocusState.Programmatic);
    });
    private void StopProgress() { progress.IsIndeterminate = false; progress.Visibility = Visibility.Collapsed; }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    protected override void OnApplyTemplate() { if (XamlRoot is not null) SizeToRoot(); base.OnApplyTemplate(); }
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96));
        layout.MaxHeight = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240));
    }
}
