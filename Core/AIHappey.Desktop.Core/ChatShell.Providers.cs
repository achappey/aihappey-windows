using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly ProvidersOverviewPage providersOverview = new() { Visibility = Visibility.Collapsed };
    private readonly ToggleButton providerFilters = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Filter), DesktopResources.Get("Filters"));
    private CatalogFavoritesStore providerFavoritesStore = null!;
    private HashSet<string> providerFavorites = new(StringComparer.Ordinal);
    private string? providerFavoritesPartition;
    private ProviderDetailsDialog? providerDialog;
    private bool CanUseProviders => activePage == DesktopPage.Providers && initialized && !busy && !closing && !downloading && !historyDialogOpen && catalogDialog is null;
    private void PrepareProvidersOverview()
    {
        providerFavoritesStore = new(Path.Combine(session.DataDirectory, "provider-favorites"));
        providerFilters.Name = "ProvidersFilters"; providerFilters.Visibility = Visibility.Collapsed;
        providerFilters.Checked += (_, _) => providersOverview.FiltersOpen = true;
        providerFilters.Unchecked += (_, _) => providersOverview.FiltersOpen = false;
        providersOverview.FiltersOpenChanged = open =>
        {
            providerFilters.IsChecked = open;
            if (!open && activePage == DesktopPage.Providers && providerFilters.IsLoaded) providerFilters.Focus(FocusState.Programmatic);
        };
        providersOverview.FavoriteRequested = async provider => await ToggleProviderFavoriteAsync(provider);
        providersOverview.DetailsRequested = async (provider, owner) => await OpenProviderDetailsAsync(provider, owner);
        providersOverview.LinkRequested = async uri => await OpenProviderLinkAsync(uri);
    }
    private async Task LoadProvidersOverviewAsync(CancellationToken ct, bool useCache = false)
    {
        var partition = session.HistoryPartition;
        var saved = providerFavoritesPartition == partition ? providerFavorites : await providerFavoritesStore.LoadAsync(partition, ct);
        ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) return;
        providerFavorites = saved; providerFavoritesPartition = partition;
        providersOverview.SetItems(ProviderCatalog.All.Values, saved, aiModelTargets);
        if (useCache)
        {
            if (aiModelTargets is null) providersOverview.Notice(DesktopResources.Get("ProviderDiscoveryUnavailable"));
            return;
        }
        // The full metadata catalog is already usable even if the model gateway is offline.
        try
        {
            using var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discovery.CancelAfter(TimeSpan.FromSeconds(15));
            var available = await client.ListAsync(ServiceKind.Ai, discovery.Token);
            ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) return;
            aiModelTargets = available; providersOverview.SetItems(ProviderCatalog.All.Values, saved, available);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { providersOverview.Notice(DesktopResources.Get("ProviderDiscoveryUnavailable")); throw; }
        catch { providersOverview.Notice(DesktopResources.Get("ProviderDiscoveryUnavailable")); }
    }
    private async Task ToggleProviderFavoriteAsync(CatalogProvider provider)
    {
        if (!CanUseProviders || providerFavoritesPartition != session.HistoryPartition || !ProviderCatalog.All.ContainsKey(provider.Id)) return;
        await RunAsync(async ct =>
        {
            var partition = session.HistoryPartition; var next = new HashSet<string>(providerFavorites, StringComparer.Ordinal);
            if (!next.Remove(provider.Id)) next.Add(provider.Id);
            await providerFavoritesStore.SaveAsync(partition, next, ct);
            if (closing || session.HistoryPartition != partition) return;
            providerFavorites = next; providersOverview.SetFavorites(next);
        });
    }
    private async Task OpenProviderDetailsAsync(CatalogProvider provider, Button owner)
    {
        if (!CanUseProviders) return;
        var modelTypes = new ProviderOverviewCatalog([], aiModelTargets).Types(provider.Id);
        var dialog = new ProviderDetailsDialog(provider, modelTypes) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); providerDialog = dialog; historyDialogOpen = true;
        dialog.LinkRequested = async uri => await OpenProviderLinkAsync(uri, dialog);
        try { await dialog.ShowAsync(); }
        finally
        {
            providerDialog = null; historyDialogOpen = false;
            if (!closing && owner.IsLoaded) owner.Focus(FocusState.Programmatic);
        }
    }
    private async Task OpenProviderLinkAsync(Uri uri, ProviderDetailsDialog? owner = null)
    {
        if (closing || busy || activePage != DesktopPage.Providers || !initialized || ModelProviders.SafeWebUri(uri.AbsoluteUri) is null
            || owner is null && !CanUseProviders || owner is not null && providerDialog != owner) return;
        try
        {
            if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException();
        }
        catch
        {
            if (closing) return;
            if (owner is not null) owner.Notice(DesktopResources.Get("ProviderLinkFailed"));
            else Show(DesktopResources.Get("ProviderLinkFailed"), InfoBarSeverity.Warning);
        }
    }
    private void InvalidateProvidersOverview()
    {
        providerFavorites.Clear(); providerFavoritesPartition = null; providerDialog?.Hide(); providersOverview.Reset();
    }
}
