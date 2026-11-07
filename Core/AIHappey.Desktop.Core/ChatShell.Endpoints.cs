using FluentIcons.Common;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private void BuildChatEndpointMenu()
    {
        var preferences = session.Settings.Chat;
        var model = Service == ServiceKind.Ai ? targets.FirstOrDefault(m => m.Id == TargetId) : null;
        var provider = model is null ? null : ChatPreferences.ResolveProvider(ServiceKind.Ai, model.Id, model.ProviderKey);
        var canEdit = CanEditProviders && Service == ServiceKind.Ai && pendingProviderSettingsWrites == 0;
        var directAvailable = DesktopChatRoutes.DirectAvailable(session.Host, provider);
        void Group(string mode, string label, IReadOnlyList<string> paths, bool available)
        {
            var selected = mode == "direct" ? DesktopChatRoutes.PreferredDirectEndpoint(provider, preferences.Endpoint)
                : DesktopChatRoutes.GatewayEndpoints.Contains(preferences.Endpoint) ? preferences.Endpoint : "/api/chat";
            var active = preferences.EndpointMode == mode && (mode != "direct" || directAvailable);
            var group = new MenuFlyoutSubItem { Name = mode == "direct" ? "DirectChatEndpoints" : "GatewayChatEndpoints",
                Text = active ? label + " (" + selected + ")" : label,
                Icon = DesktopIcons.Create(Icon.Connector), IsEnabled = canEdit && available, RequestedTheme = ActualTheme };
            ControlAppearance.Native(group);
            foreach (var path in paths)
            {
                var item = new ToggleMenuFlyoutItem { Name = "ChatEndpoint_" + mode + "_" + path.Replace('/', '_'),
                    Text = path, Tag = path, IsChecked = active && selected == path, RequestedTheme = ActualTheme };
                ControlAppearance.Native(item);
                item.Click += async (_, _) => await SelectChatEndpointAsync(mode, path, provider);
                group.Items.Add(item);
            }
            providerSettings.Items.Add(group);
        }
        Group("gateway", DesktopResources.Get("ChatGateway"), DesktopChatRoutes.GatewayEndpoints, true);
        if (session.Host.SupportsDirectChat)
            Group("direct", DesktopResources.Get("ChatDirect"), DesktopChatRoutes.DirectEndpoints(provider), directAvailable);
        providerSettings.Items.Add(new MenuFlyoutSeparator());
    }

    private async Task SelectChatEndpointAsync(string mode, string path, string? provider)
    {
        if (!CanEditProviders || Service != ServiceKind.Ai || pendingProviderSettingsWrites > 0) return;
        if (mode == "direct" && (!DesktopChatRoutes.DirectAvailable(session.Host, provider) || !DesktopChatRoutes.DirectEndpoints(provider).Contains(path))) return;
        if (mode != "direct" && !DesktopChatRoutes.GatewayEndpoints.Contains(path)) return;
        var partition = session.HistoryPartition;
        pendingProviderSettingsWrites++;
        await providerSettingsWrite.WaitAsync();
        try
        {
            if (!CanEditProviders || partition != session.HistoryPartition) return;
            var next = session.Settings.Clone();
            next.Chat.EndpointMode = mode; next.Chat.Endpoint = path;
            await SettingsStore.SaveAsync(session.DataDirectory, next);
            session.Settings = next;
            if (path != "/api/chat") Show(DesktopResources.Get("NativeChatBasicOnly"), InfoBarSeverity.Informational);
        }
        catch { if (!closing) Show(DesktopResources.Get("OperationFailed"), InfoBarSeverity.Error); }
        finally { pendingProviderSettingsWrites--; providerSettingsWrite.Release(); }
        if (!closing) account.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }
}
