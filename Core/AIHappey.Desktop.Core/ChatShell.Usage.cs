namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly UsagePage usagePage = new() { Visibility = Microsoft.UI.Xaml.Visibility.Collapsed };

    private async Task LoadUsageAsync(CancellationToken ct)
    {
        var partition = session.HistoryPartition;
        bool Current() => !closing && session.HistoryPartition == partition && activePage == DesktopPage.Usage;
        usagePage.Loading();
        try
        {
            // Reload saved history, not the sidebar filter or mutable live draft. No network/discovery,
            // no history writes, and no cached snapshot crossing an account/endpoint boundary.
            var snapshot = await Task.Run(async () =>
            {
                var saved = await history.ListAsync(partition, ct);
                var providers = ProviderCatalog.All.ToDictionary(p => p.Key, p => p.Value.Name, StringComparer.Ordinal);
                return UsageAnalytics.Build(saved, providers, ct: ct);
            }, ct);
            ct.ThrowIfCancellationRequested();
            if (Current()) usagePage.SetItems(snapshot);
        }
        catch (OperationCanceledException)
        { if (Current()) usagePage.Error(DesktopResources.Get("OperationCanceled")); throw; }
        catch
        { if (Current()) usagePage.Error(DesktopResources.Get("UsageLoadFailed")); throw; }
    }
}
