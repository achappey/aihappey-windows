using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Grid chatModelToolbar = new() { Name = "ChatModelToolbar" };
    private readonly Grid imageModelToolbar = new() { Name = "ImageModelToolbar", Visibility = Visibility.Collapsed };
    private readonly Grid speechModelToolbar = new() { Name = "SpeechModelToolbar", Visibility = Visibility.Collapsed };
    private readonly Grid transcriptionModelToolbar = new() { Name = "TranscriptionModelToolbar", Visibility = Visibility.Collapsed };
    private readonly List<(Button Button, Func<ChatTarget?> Selected, Func<bool> Allowed)> modelFavoriteButtons = [];

    private void ModelHeader(Grid header, Control picker, Func<ChatTarget?> selected, Func<bool> allowed, Button? favorite = null)
    {
        // Horizontal StackPanels measure children at infinite width and clip the trailing button.
        // Reserve the star's width and let even very long model names shrink/ellipsize.
        header.ColumnSpacing = 8;
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        picker.MinWidth = 0;
        picker.HorizontalAlignment = HorizontalAlignment.Stretch;
        favorite ??= new Button { Name = picker.Name + "Favorite", Width = 40, Height = 40 };
        favorite.Padding = new Thickness(0);
        ToolbarControls.Subtle(favorite);
        Grid.SetColumn(favorite, 1);
        header.Children.Add(picker); header.Children.Add(favorite);
        modelFavoriteButtons.Add((favorite, selected, allowed));
        favorite.Click += async (_, _) =>
        {
            if (!allowed() || selected() is not { } model || busy || closing) return;
            await RunAsync(ct => SaveModelFavoriteAsync(model, ct));
        };
    }

    private async Task LoadModelFavoritesAsync(CancellationToken ct)
    {
        var partition = session.HistoryPartition;
        if (modelFavoritesPartition == partition) return;
        var saved = await modelFavoritesStore.LoadAsync(partition, ct);
        ct.ThrowIfCancellationRequested();
        if (closing || partition != session.HistoryPartition) throw new OperationCanceledException(ct);
        modelFavorites = saved; modelFavoritesPartition = partition;
        UpdateModelFavorites();
    }

    private async Task SaveModelFavoriteAsync(ChatTarget model, CancellationToken ct)
    {
        var partition = session.HistoryPartition;
        await LoadModelFavoritesAsync(ct);
        var next = new HashSet<string>(modelFavorites, StringComparer.Ordinal);
        var key = ModelOverviewCatalog.FavoriteKey(model); if (!next.Remove(key)) next.Add(key);
        await modelFavoritesStore.SaveAsync(partition, next, ct);
        ct.ThrowIfCancellationRequested(); if (closing || partition != session.HistoryPartition) return;
        modelFavorites = next; modelFavoritesPartition = partition;
        modelsOverview.SetFavorites(next);
        UpdateModelFavorites();
    }

    private void UpdateModelFavorites()
    {
        foreach (var (button, selected, allowed) in modelFavoriteButtons)
        {
            var model = selected();
            var favorite = model is not null && modelFavoritesPartition == session.HistoryPartition
                && modelFavorites.Contains(ModelOverviewCatalog.FavoriteKey(model));
            button.Content = DesktopIcons.Create(Icon.Star, variant: favorite ? IconVariant.Filled : IconVariant.Regular);
            ToolbarControls.Label(button, DesktopResources.Get(favorite ? "RemoveFavorite" : "AddFavorite"));
            button.IsEnabled = initialized && !busy && !closing && allowed() && model is not null;
        }
    }

    private bool PrimitiveRoutingAvailable => DecisionRouting.Model(
        DecisionRouting.Agent(session.Settings.SideInferenceAgentNames, localAgents, false), aiModelTargets ?? []) is not null;

    private void ConfigurePrimitivePicker(ChatTargetPicker picker, IReadOnlyList<ChatTarget> models, string type)
    {
        var available = PickerModels(models);
        if (!PrimitiveRoutingAvailable)
            picker.Selection.UseManualSelection(available, false, fallback: InitialModel(models, type));
        var favoriteIds = new HashSet<string>(available.Where(m => modelFavorites.Contains(ModelOverviewCatalog.FavoriteKey(m))).Select(m => m.Id));
        picker.Configure(false, PrimitiveRoutingAvailable, favoriteIds, PrimitiveRoutingAvailable, "");
        picker.ItemsSource = available;
        UpdateModelFavorites();
    }

    private async Task<ChatTarget> ResolvePrimitiveModelAsync(ChatTargetPicker picker, IReadOnlyList<ChatTarget> models, string type, string prompt, CancellationToken ct)
    {
        var ids = picker.Selection.Ids.ToArray();
        var available = PickerModels(models);
        var candidates = ids.Select(id => available.FirstOrDefault(m => m.Id == id && m.ModelType == type)
            ?? throw new GatewayException(DesktopResources.Get("SelectTarget"))).ToArray();
        if (candidates.Length == 0) throw new GatewayException(DesktopResources.Get("SelectTarget"));
        if (candidates.Length == 1) return candidates[0];
        var agent = DecisionRouting.Agent(session.Settings.SideInferenceAgentNames, localAgents, false);
        var decision = DecisionRouting.Model(agent, aiModelTargets ?? []) ?? throw new GatewayException(DesktopResources.Get("RoutingModelRequired"));
        var page = activePage; var signature = SelectionSignature();
        bool Current() => !closing && activePage == page && signature == SelectionSignature() && ids.SequenceEqual(picker.Selection.Ids);
        var body = DecisionRouting.PrimitiveRequest(decision, DecisionRouting.Instructions(agent, false), prompt, candidates);
        var winner = await new DesktopDecisionClient(client, http).DecideAsync(body, Current, ct);
        ct.ThrowIfCancellationRequested();
        if (!Current()) throw new GatewayException(DesktopResources.Get("RoutingContextChanged"));
        return candidates.Single(m => m.Id == winner);
    }
}
