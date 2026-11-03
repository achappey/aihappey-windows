using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Button conversationStorageButton = new() { Name = "ConversationStorageSwitch", Width = 32, Height = 32,
        Padding = new Thickness(0), Visibility = Visibility.Collapsed };

    private void UpdateConversationStorageButton()
    {
        var available = session.Host is IDesktopConversationHost { ConversationsEndpoint: not null };
        conversationStorageButton.Visibility = available && session.Settings.ConversationStorage.CloudEnabled ? Visibility.Visible : Visibility.Collapsed;
        conversationStorageButton.IsEnabled = CanManageConversationHistory && !session.Host.RequiresSignIn && realtime is null;
        conversationStorageButton.Content = DesktopIcons.Create(session.UsesRemoteConversations ? Icon.Cloud : Icon.Database);
        ToolbarControls.Label(conversationStorageButton, DesktopResources.Get(session.UsesRemoteConversations ? "ConversationStorageCloud" : "ConversationStorageLocal"));
        Grid.SetColumn(conversationStorageButton, split.IsPaneOpen ? 1 : 2);
        Grid.SetRow(conversationStorageButton, split.IsPaneOpen ? 0 : 1);
        conversationStorageButton.Margin = split.IsPaneOpen ? new Thickness(0) : new Thickness(0, 8, 0, 0);
    }

    private async Task AuthorizeCloudConversationsAsync(CancellationToken ct)
    {
        var host = session.Host as IDesktopConversationHost;
        var endpoint = host?.ConversationsEndpoint ?? throw new InvalidOperationException(DesktopResources.Get("ConversationCloudUnavailable"));
        // Consent is permitted only from this explicit settings/storage gesture, never from background history IO.
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        await host.AuthenticateConversationsAsync(request, true, ct);
    }

    private async Task SwitchConversationStorageAsync()
    {
        if (!CanManageConversationHistory || realtime is not null || session.Host.RequiresSignIn
            || !session.Settings.ConversationStorage.CloudEnabled) return;
        await RunAsync(async ct =>
        {
            var next = session.Settings.Clone();
            next.ConversationStorage.Selected = session.UsesRemoteConversations ? "local" : "remote";
            await CommitConversationSettingsAsync(next, ct);
        });
    }

    private async Task CommitConversationSettingsAsync(DesktopSettings next, CancellationToken ct)
    {
        var previous = session.Settings.ConversationStorage;
        var nextRemote = next.ConversationStorage.CloudEnabled && next.ConversationStorage.Selected == "remote"
            && session.Host is IDesktopConversationHost { ConversationsEndpoint: not null };
        if (next.ConversationStorage.CloudEnabled && (!previous.CloudEnabled || nextRemote && !session.UsesRemoteConversations))
            await AuthorizeCloudConversationsAsync(ct);
        var changed = nextRemote != session.UsesRemoteConversations;
        IReadOnlyList<Conversation>? loaded = null; HashSet<string>? pins = null;
        var identity = session.Host.HistoryIdentity;
        var partition = nextRemote ? session.RemoteHistoryPartition : session.LocalHistoryPartition;
        if (changed)
        {
            if (nextRemote) history.PrepareRemoteSource();
            loaded = await history.ListAsync(partition, ct);
            pins = await conversationPinsStore.LoadAsync(partition, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (closing || identity != session.Host.HistoryIdentity) throw new OperationCanceledException(ct);
        await SettingsStore.SaveAsync(session.DataDirectory, next);
        session.Settings = next;
        if (changed)
        {
            DiscardDictation(); current = new() { Service = Service }; input.Text = ""; ResetContext();
            SelectNewChatModel(); conversations = loaded!; conversationPins = pins!; conversationPinsPartition = partition;
            FilterHistory(); RenderTranscript();
        }
        UpdateConversationStorageButton();
    }
}
