using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private IReadOnlyList<ChatTarget>? aiModelTargets;
    private readonly ModelsOverviewPage modelsOverview = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel aiNavigation = new() { Spacing = 4 };
    private Button? aiCategory;
    private bool aiCategoryExpanded = true;
    private readonly ToggleButton modelFilters = ToolbarControls.CreateModeButton(DesktopIcons.Create(Icon.Filter), DesktopResources.Get("Filters"));
    private CatalogFavoritesStore modelFavoritesStore = null!;
    private HashSet<string> modelFavorites = new(StringComparer.Ordinal);
    private string? modelFavoritesPartition;

    private void PrepareModelsOverview()
    {
        modelFavoritesStore = new(Path.Combine(session.DataDirectory, "model-favorites"));
        modelFilters.Name = "ModelsFilters"; modelFilters.Visibility = Visibility.Collapsed;
        modelFilters.Checked += (_, _) => modelsOverview.FiltersOpen = true;
        modelFilters.Unchecked += (_, _) => modelsOverview.FiltersOpen = false;
        modelsOverview.FiltersOpenChanged = open =>
        {
            modelFilters.IsChecked = open;
            if (!open && activePage == DesktopPage.Models && modelFilters.IsLoaded) modelFilters.Focus(FocusState.Programmatic);
        };
        modelsOverview.RetryRequested = async () => await RunAsync(ct => LoadModelsOverviewAsync(ct));
        modelsOverview.CancelRequested = () => operation?.Cancel();
        modelsOverview.CopyRequested = model =>
        {
            if (!CanUseModelOverview) return;
            try { var data = new DataPackage(); data.SetText(ModelOverviewCatalog.DisplayId(model)); Clipboard.SetContent(data); }
            catch { Show(DesktopResources.Get("ClipboardUnavailable"), InfoBarSeverity.Warning); }
        };
        modelsOverview.FavoriteRequested = async model => await ToggleModelFavoriteAsync(model);
        modelsOverview.LaunchRequested = async model => await LaunchModelAsync(model);
        modelsOverview.WebsiteRequested = async uri =>
        {
            if (!CanUseModelOverview || ModelProviders.SafeWebUri(uri.AbsoluteUri) is null) return;
            await RunAsync(async ct =>
            {
                if (!await Launcher.LaunchUriAsync(uri).AsTask(ct)) throw new InvalidOperationException(DesktopResources.Get("ModelsWebsiteFailed"));
            });
        };
    }
    private bool CanUseModelOverview => activePage == DesktopPage.Models && initialized && !busy && !closing
        && !downloading && !historyDialogOpen && catalogDialog is null;

    private async Task LoadModelsOverviewAsync(CancellationToken ct, bool useCache = false)
    {
        var partition = session.HistoryPartition;
        modelsOverview.Loading();
        try
        {
            var saved = modelFavoritesPartition == partition ? modelFavorites : await modelFavoritesStore.LoadAsync(partition, ct);
            var available = useCache && aiModelTargets is not null ? aiModelTargets : await client.ListAsync(ServiceKind.Ai, ct);
            ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) return;
            aiModelTargets = available; modelFavorites = saved; modelFavoritesPartition = partition;
            modelsOverview.SetItems(available, saved);
            if (Service == ServiceKind.Ai) { targets = available; UpdateTargetSuggestions(); }
        }
        catch (OperationCanceledException) { modelsOverview.Error(DesktopResources.Get("CatalogCanceled")); throw; }
        catch (Exception error)
        {
            modelsOverview.Error(error is GatewayException or InvalidOperationException ? error.Message : DesktopResources.Get("AiModelsLoadFailed")); throw;
        }
    }
    private void InvalidateModelsOverview()
    {
        aiModelTargets = null; modelFavorites.Clear(); modelFavoritesPartition = null; modelsOverview.Reset();
    }
    private async Task ToggleModelFavoriteAsync(ChatTarget model)
    {
        if (!CanUseModelOverview || modelFavoritesPartition != session.HistoryPartition) return;
        await RunAsync(async ct =>
        {
            var partition = session.HistoryPartition;
            var next = new HashSet<string>(modelFavorites, StringComparer.Ordinal);
            var key = ModelOverviewCatalog.FavoriteKey(model); if (!next.Remove(key)) next.Add(key);
            await modelFavoritesStore.SaveAsync(partition, next, ct);
            if (closing || session.HistoryPartition != partition) return;
            modelFavorites = next; modelsOverview.SetFavorites(next);
        });
    }
    private async Task LaunchModelAsync(ChatTarget requested)
    {
        if (!CanUseModelOverview || !ModelOverviewCatalog.CanLaunch(requested)) return;
        await RunAsync(async ct =>
        {
            var partition = session.HistoryPartition;
            // Revalidate against current discovery, not against a label or a stale card from a previous endpoint.
            var available = await client.ListAsync(ServiceKind.Ai, ct);
            ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) return;
            var selected = available.FirstOrDefault(m => m.Id == requested.Id && m.ModelType == requested.ModelType)
                ?? throw new GatewayException(DesktopResources.Get("ModelsUnavailable"));
            aiModelTargets = available;
            if (selected.ModelType == "image")
            {
                await LoadImagesAsync(ct, useCache: true);
                ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) return;
                imageTarget.Text = selected.Id; imagesPage.SetModelAvailable(SelectedImageModel is not null);
                ShowPage(DesktopPage.Images); imagesPage.FocusPrompt();
            }
            else if (selected.ModelType == "video")
            {
                await LoadVideosAsync(ct, useCache: true);
                ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) return;
                videoTarget.Text = selected.Id; videosPage.SetModelAvailable(SelectedVideoModel is not null);
                ShowPage(DesktopPage.Videos); videosPage.FocusPrompt();
            }
            else if (selected.ModelType == "transcription")
            {
                await LoadTranscriptionsAsync(ct, useCache: true);
                ct.ThrowIfCancellationRequested(); if (closing || session.HistoryPartition != partition) return;
                transcriptionTarget.Text = selected.Id; transcriptionsPage.SetModelAvailable(SelectedTranscriptionModel is not null);
                ShowPage(DesktopPage.Transcriptions); transcriptionTarget.Focus(FocusState.Programmatic);
            }
            else
            {
                UpdateMode(ServiceKind.Ai); targets = available; UpdateTargetSuggestions(); target.Text = selected.Id;
                current = new() { Service = ServiceKind.Ai, Target = selected.Id }; input.Text = ""; ResetContext();
                suppress = true; chats.SelectedItem = null; suppress = false;
                ShowPage(DesktopPage.Chat); RenderTranscript(); input.Focus(FocusState.Programmatic);
            }
        });
    }

    private void UpdateTargetSuggestions(string query = "")
    {
        target.ItemsSource = (Service == ServiceKind.Ai
            ? AiModelCatalog.ChatSuggestions(targets, session.Settings.AiModels, query)
            : targets.Where(x => string.IsNullOrWhiteSpace(query) || x.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray()).Take(100).ToArray();
    }

    private void SelectNewChatModel()
    {
        if (Service != ServiceKind.Ai) return;
        target.Text = AiModelCatalog.NewChatModel(targets, session.Settings.AiModels);
        current.Target = target.Text;
        UpdateTargetSuggestions();
    }
}
