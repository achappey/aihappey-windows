using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Button chatSettings = new() { Name = "ChatSettingsButton", Content = DesktopIcons.Create(Icon.Settings), Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(6) };
    private ChatSettingsDialog? chatSettingsDialog;

    private void PrepareChatSettings()
    {
        ToolbarControls.Subtle(chatSettings); ToolbarControls.Label(chatSettings, DesktopResources.Get("ChatSettings"));
        chatSettings.Click += async (_, _) => { if (Service == ServiceKind.Agents && activePage != DesktopPage.Realtime) await EditAgentWorkflowAsync(); else await EditChatSettingsAsync(); };
    }
    private async Task EditChatSettingsAsync()
    {
        var isRealtime = activePage == DesktopPage.Realtime;
        if (busy || closing || historyDialogOpen || catalogDialog is not null || !isRealtime && Service != ServiceKind.Ai) return;
        historyDialogOpen = true;
        var partition = session.HistoryPartition;
        var selected = targets.FirstOrDefault(t => t.Id == target.Text.Trim());
        var provider = isRealtime ? RealtimeConfiguration.Provider(realtime?.Model ?? realtimeTarget.Text)
            : selected is null ? null : ChatPreferences.ResolveProvider(Service, selected.Id, selected.ProviderKey);
        var dialog = new ChatSettingsDialog(session.Settings.Chat, provider, LoadSkillSelectionAsync, PrefetchSkillAsync, agentPlugins,
            AvailableChatTools, isRealtime ? ServiceKind.Ai : Service,
            isRealtime ? session.Settings.Realtime : null, realtime is not null, aiModelTargets, AgentTargetsAsync) { XamlRoot = XamlRoot };
        chatSettingsDialog = dialog;
        async Task SaveAsync(ChatPreferences preferences, RealtimePreferences? realtimePreferences)
        {
            // Save first so a disk error retains both the open draft and previous live preferences.
            if (closing || partition != session.HistoryPartition) throw new OperationCanceledException();
            var next = session.Settings.Clone();
            next.Chat = preferences.Clone();
            if (realtimePreferences is not null) next.Realtime = realtimePreferences.Clone();
            await SettingsStore.SaveAsync(session.DataDirectory, next);
            if (closing || partition != session.HistoryPartition) throw new OperationCanceledException();
            session.Settings = next;
            await LoadPluginsAsync(downloadLifetime.Token);
            RenderContextTags();
            UpdateComposerUsage();
        }
        dialog.SaveAsync = preferences => SaveAsync(preferences, null);
        dialog.SaveWithRealtimeAsync = (preferences, realtimePreferences) => SaveAsync(preferences, realtimePreferences);
        SystemAppearance.PrepareDialog(dialog);
        try { await dialog.ShowAsync(); }
        finally
        {
            chatSettingsDialog = null; historyDialogOpen = false;
            if (!closing) chatSettings.Focus(FocusState.Programmatic);
        }
    }

    private async Task EditAgentWorkflowAsync()
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null) return;
        historyDialogOpen = true; var partition = session.HistoryPartition;
        try
        {
            var selected = target.Selection.Ids.Select(id => targets.FirstOrDefault(t => t.Id == id)).OfType<ChatTarget>().ToArray();
            var dialog = new AgentWorkflowDialog(session.Settings.AgentWorkflow, selected, aiModelTargets ?? [], session.Settings.Chat) { XamlRoot = XamlRoot };
            dialog.SaveAsync = async (workflow, ids) =>
            {
                if (closing || partition != session.HistoryPartition) throw new OperationCanceledException();
                var next = session.Settings.Clone(); next.AgentWorkflow = workflow;
                // The Tools tab shares model-chat selections, but must not replace
                // unrelated preferences with the dialog's older draft.
                next.Chat.ActivePlugins = dialog.ToolPreferences.ActivePlugins.ToList();
                await SettingsStore.SaveAsync(session.DataDirectory, next);
                if (closing || partition != session.HistoryPartition) throw new OperationCanceledException();
                session.Settings = next; target.SetSelection(ids, target.Selection.FavoriteRouter); RememberSelection();
            };
            SystemAppearance.PrepareDialog(dialog); await dialog.ShowAsync();
        }
        finally { historyDialogOpen = false; if (!closing) chatSettings.Focus(FocusState.Programmatic); }
    }

    private IReadOnlyList<ChatToolOption> AvailableChatTools(ChatPreferences preferences)
    {
        var enabled = EnabledPlugins(preferences);
        var selected = preferences.EnabledSkillIds.ToHashSet(StringComparer.Ordinal);
        var hasSkills = AvailableSkillDescriptors().Any(skill => selected.Contains(skill.Id))
            || enabled.Any(plugin => plugin.Skills().Any());
        return Mcp.Capture().SettingsTools(preferences, hasSkills, enabled.Count > 0);
    }
}
