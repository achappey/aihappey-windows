using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly MenuFlyoutSubItem providerSettings = new() { Name = "ProviderSettings", Text = DesktopResources.Get("Providers"), Icon = DesktopIcons.Create(Icon.Connector) };
    private readonly SemaphoreSlim providerSettingsWrite = new(1, 1);
    private int pendingProviderSettingsWrites;
    private Flyout? providerSelectionFlyout;
    private bool CanEditProviders => initialized && !busy && !closing && !historyDialogOpen && catalogDialog is null && !downloading;
    private IReadOnlyList<ChatTarget> PickerModels(IEnumerable<ChatTarget> models) => ProviderPreferences.Filter(models,
        session.Settings, modelFavoritesPartition == session.HistoryPartition ? modelFavorites : null);
    private string InitialModel(IEnumerable<ChatTarget> models, string type, string? fallback = null)
    {
        var visible = PickerModels(models);
        return visible.FirstOrDefault(m => m.Id == session.Settings.AiModels.DefaultFor(type))?.Id
            ?? visible.FirstOrDefault(m => m.Id == fallback)?.Id ?? visible.FirstOrDefault()?.Id ?? "";
    }
    private void BuildProviderSettingsMenu()
    {
        providerSettings.Items.Clear();
        providerSettings.RequestedTheme = ActualTheme;
        ControlAppearance.Native(providerSettings);
        if (session.Host.HasProviderApiKeys)
        {
            var keys = new MenuFlyoutItem { Name = "ProviderApiKeys", Text = DesktopResources.Get("ApiKeys"),
                Icon = DesktopIcons.Create(Icon.Key), IsEnabled = CanEditProviders, RequestedTheme = ActualTheme };
            ControlAppearance.Native(keys); keys.Click += async (_, _) => await EditProviderApiKeysAsync();
            providerSettings.Items.Add(keys); providerSettings.Items.Add(new MenuFlyoutSeparator());
        }
        var groups = ProviderPreferences.Groups(aiModelTargets ?? []);
        // Match the browser's user-facing order, not the catalog/settings tab order.
        foreach (var type in new[] { "language", "image", "audio", "transcription", "speech", "decision", "reranking", "video", "embedding" })
        {
            if (!groups.TryGetValue(type, out var providers)) continue;
            var group = new MenuFlyoutItem { Name = "ProviderType_" + type, Tag = type, RequestedTheme = ActualTheme,
                IsEnabled = CanEditProviders };
            ControlAppearance.Native(group);
            void UpdateCount() => group.Text = DesktopResources.Format("ProviderTypeCount", DesktopResources.Get("AiModelType_" + type),
                providers.Count(provider => ProviderPreferences.IsEnabled(session.Settings, type, provider)), providers.Length);
            UpdateCount();
            group.Click += (_, _) => DispatcherQueue.TryEnqueue(() => OpenProviderSelection(type));
            providerSettings.Items.Add(group);
        }
        if (groups.Count == 0)
            providerSettings.Items.Add(new MenuFlyoutItem { Name = "ProviderDiscoveryState", IsEnabled = false,
                Text = DesktopResources.Get(aiModelTargets is null ? "ProviderDiscoveryUnavailable" : "AiNoModels") });
    }
    private void OpenProviderSelection(string type)
    {
        if (!CanEditProviders) return;
        var partition = session.HistoryPartition;
        var view = new ProviderSelectionView(ProviderPreferences.Groups(aiModelTargets ?? []), () => session.Settings, type)
            { RequestedTheme = ActualTheme };
        view.SaveRequested = async (modelType, provider, enabled) =>
        {
            pendingProviderSettingsWrites++;
            await providerSettingsWrite.WaitAsync();
            try
            {
                if (!CanEditProviders || partition != session.HistoryPartition) throw new InvalidOperationException();
                var next = session.Settings.Clone();
                ProviderPreferences.SetEnabled(next, modelType, provider, enabled);
                await SettingsStore.SaveAsync(session.DataDirectory, next);
                session.Settings = next;
                if (!closing) RefreshProviderFiltering();
            }
            finally { pendingProviderSettingsWrites--; providerSettingsWrite.Release(); }
        };
        providerSelectionFlyout?.Hide();
        var flyout = new Flyout { Content = view, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        providerSelectionFlyout = flyout;
        flyout.Closed += (_, _) => { if (ReferenceEquals(providerSelectionFlyout, flyout)) providerSelectionFlyout = null; };
        flyout.ShowAt(account);
    }
    private void RefreshProviderFiltering()
    {
        UpdateDictationAvailability();
        UpdateTargetSuggestions(); ImageModelSuggestions(); VideoModelSuggestions(); TranscriptionModelSuggestions(); SpeechModelSuggestions();
        if (aiModelTargets is not null) modelsOverview.SetItems(ProviderPreferences.Filter(aiModelTargets, session.Settings), modelFavorites);
        // Keep valid explicit selections and drafts. Only fill previously empty pickers from enabled providers.
        if (Service == ServiceKind.Ai && TargetId.Length == 0) SelectNewChatModel();
        if (imageTarget.Text.Length == 0) imageTarget.Text = InitialModel(imageModels, "image");
        if (videoTarget.Text.Length == 0) videoTarget.Text = InitialModel(videoModels, "video");
        if (transcriptionTarget.Text.Length == 0) transcriptionTarget.Text = InitialModel(transcriptionModels, "transcription");
        if (speechTarget.Text.Length == 0) speechTarget.Text = InitialModel(speechModels, "speech");
        imagesPage.SetModelAvailable(SelectedImageModel is not null); videosPage.SetModelAvailable(SelectedVideoModel is not null);
        transcriptionsPage.SetModelAvailable(SelectedTranscriptionModel is not null); speechPage.SetModelAvailable(SelectedSpeechModel is not null);
    }
    private async Task EditProviderApiKeysAsync()
    {
        if (!CanEditProviders || pendingProviderSettingsWrites > 0 || !session.Host.HasProviderApiKeys) return;
        await RunAsync(async ct =>
        {
            historyDialogOpen = true;
            try
            {
                await session.Host.ManageProviderApiKeysAsync(XamlRoot, async changes =>
                {
                    if (closing) throw new OperationCanceledException();
                    var next = session.Settings.Clone();
                    ProviderPreferences.ApplyCredentialChanges(next, changes);
                    await SettingsStore.SaveAsync(session.DataDirectory, next);
                    session.Settings = next;
                }, ct);
            }
            finally { historyDialogOpen = false; }
            // Credentials are changed without treating this as an identity change or deleting the current draft.
            var partition = session.HistoryPartition;
            var available = await client.ListAsync(ServiceKind.Ai, ct);
            ct.ThrowIfCancellationRequested(); if (closing || partition != session.HistoryPartition) return;
            aiModelTargets = available;
            if (Service == ServiceKind.Ai) targets = available;
            imageModels = AiModelCatalog.OfType(available, "image"); videoModels = AiModelCatalog.OfType(available, "video");
            transcriptionModels = AiModelCatalog.OfType(available, "transcription"); speechModels = AiModelCatalog.OfType(available, "speech");
            RefreshProviderFiltering();
        });
        RefreshProviderFiltering();
        if (!closing) account.Focus(FocusState.Programmatic);
    }
}
