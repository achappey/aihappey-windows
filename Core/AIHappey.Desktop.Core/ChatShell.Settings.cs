using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Button chatSettings = new() { Name = "ChatSettingsButton", Content = new SymbolIcon(Symbol.Setting), Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(6) };
    private ChatSettingsDialog? chatSettingsDialog;

    private void PrepareChatSettings()
    {
        ToolbarControls.Subtle(chatSettings); ToolbarControls.Label(chatSettings, DesktopResources.Get("ChatSettings"));
        chatSettings.Click += async (_, _) => await EditChatSettingsAsync();
    }
    private async Task EditChatSettingsAsync()
    {
        if (busy || closing || historyDialogOpen || catalogDialog is not null || Service != ServiceKind.Ai) return;
        historyDialogOpen = true;
        var selected = targets.FirstOrDefault(t => t.Id == target.Text.Trim());
        var provider = selected is null ? null : ChatPreferences.ResolveProvider(Service, selected.Id, selected.ProviderKey);
        var dialog = new ChatSettingsDialog(session.Settings.Chat, provider) { XamlRoot = XamlRoot };
        chatSettingsDialog = dialog;
        dialog.SaveAsync = async preferences =>
        {
            // Save first so a disk error retains both the open draft and previous live preferences.
            var next = session.Settings.Clone();
            next.Chat = preferences.Clone();
            await SettingsStore.SaveAsync(session.DataDirectory, next);
            session.Settings = next;
        };
        SystemAppearance.PrepareDialog(dialog);
        try { await dialog.ShowAsync(); }
        finally
        {
            chatSettingsDialog = null; historyDialogOpen = false;
            if (!closing) chatSettings.Focus(FocusState.Programmatic);
        }
    }
}
