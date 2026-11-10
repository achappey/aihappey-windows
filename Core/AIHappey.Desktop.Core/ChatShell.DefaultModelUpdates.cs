using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly InfoBar defaultModelNotice = new() { Name = "DefaultModelUpdatesNotice", Severity = InfoBarSeverity.Warning,
        IsOpen = false, IsClosable = true, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button applyDefaultModels = new() { Name = "ApplyDefaultModelUpdates" };
    private bool defaultModelNoticeDismissed;

    private void PrepareDefaultModelNotice(StackPanel notices)
    {
        defaultModelNotice.Title = DesktopResources.Get("AiDefaultModels");
        applyDefaultModels.Content = DesktopResources.Get("DefaultModelUpdatesApply");
        ToolbarControls.Label(applyDefaultModels, DesktopResources.Get("DefaultModelUpdatesApply"));
        ControlAppearance.Native(applyDefaultModels);
        defaultModelNotice.ActionButton = applyDefaultModels;
        ControlAppearance.Stock(defaultModelNotice);
        defaultModelNotice.CloseButtonClick += (_, _) => defaultModelNoticeDismissed = true;
        defaultModelNotice.Closed += (_, _) => defaultModelNotice.Visibility = Visibility.Collapsed;
        applyDefaultModels.Click += async (_, _) => await ApplyDefaultModelUpdatesAsync();
        notices.Children.Add(defaultModelNotice);
    }

    private void UpdateDefaultModelNotice()
    {
        var updates = closing || session.Host.RequiresSignIn || defaultModelNoticeDismissed
            ? new Dictionary<string, string[]>()
            : DefaultModelUpdates.Find(session.Settings.AiModels, session.DefaultModelsByType, aiModelTargets ?? [], DateTimeOffset.UtcNow);
        if (updates.Count == 0)
        {
            defaultModelNotice.IsOpen = false;
            defaultModelNotice.Visibility = Visibility.Collapsed;
            return;
        }
        defaultModelNotice.Message = DesktopResources.Format("DefaultModelUpdatesMessage",
            string.Join(", ", updates.Keys.Select(type => DesktopResources.Get("AiModelType_" + type))));
        applyDefaultModels.IsEnabled = !busy && pendingProviderSettingsWrites == 0 && !historyDialogOpen;
        defaultModelNotice.Visibility = Visibility.Visible;
        defaultModelNotice.IsOpen = true;
    }

    private async Task ApplyDefaultModelUpdatesAsync()
    {
        if (historyDialogOpen || session.Host.RequiresSignIn || defaultModelNoticeDismissed) return;
        await RunAsync(async ct =>
        {
            var updates = DefaultModelUpdates.Find(session.Settings.AiModels, session.DefaultModelsByType, aiModelTargets ?? [], DateTimeOffset.UtcNow);
            if (updates.Count == 0) { UpdateDefaultModelNotice(); return; }
            var next = session.Settings.Clone();
            foreach (var (type, ids) in updates) next.AiModels.SetDefaults(type, ids);
            // Publish only after the existing atomic settings write succeeds. Keep active selections/drafts.
            ct.ThrowIfCancellationRequested();
            await SettingsStore.SaveAsync(session.DataDirectory, next);
            session.Settings = next;
            defaultModelNoticeDismissed = true;
            UpdateDefaultModelNotice();
        });
    }
}
