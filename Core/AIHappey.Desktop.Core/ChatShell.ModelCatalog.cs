namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private DesktopModelCatalog? modelCatalog;

    private Task<IReadOnlyList<ChatTarget>> AiModelsAsync(CancellationToken ct, bool fresh = false)
    {
        if (modelCatalog is null)
        {
            modelCatalog = new(() => session.ModelCatalogScope, token => client.ReadAiModelsAsync(token),
                new DesktopModelCatalogStore(Path.Combine(session.DataDirectory, "models")), downloadLifetime.Token);
            modelCatalog.Changed += (scope, available) => DispatcherQueue.TryEnqueue(() =>
            {
                if (closing || session.Host.RequiresSignIn || scope != session.ModelCatalogScope) return;
                ApplyModelCatalog(available);
            });
        }
        return modelCatalog.ListAsync(ct, fresh);
    }

    private void ApplyModelCatalog(IReadOnlyList<ChatTarget> available)
    {
        aiModelTargets = available;
        if (Service == ServiceKind.Ai) targets = available;
        imageModels = AiModelCatalog.OfType(available, "image"); videoModels = AiModelCatalog.OfType(available, "video");
        transcriptionModels = AiModelCatalog.OfType(available, "transcription"); speechModels = AiModelCatalog.OfType(available, "speech");
        realtimeModels = ProviderPreferences.Filter(AiModelCatalog.OfType(available, "audio"), session.Settings)
            .Where(model => RealtimeConfiguration.Supported(model.Id)).ToArray();
        RefreshProviderFiltering();
        if (activePage == DesktopPage.Providers && providerFavoritesPartition == session.HistoryPartition)
            providersOverview.SetItems(ProviderCatalog.All.Values, providerFavorites, available);
        // RefreshProviderFiltering preserves active drafts and valid explicit selections.
        realtimeTarget.ItemsSource = PickerChoices(realtimeModels); UpdateRealtimeControls();
    }
}
