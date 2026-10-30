using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private void RenderLocalPluginTags()
    {
        if (Service != ServiceKind.Ai) return;
        var plugins = DesktopLocalTools.Plugins.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var id in session.Settings.Chat.ActivePlugins.Distinct(StringComparer.Ordinal))
        {
            var label = plugins.TryGetValue(id, out var plugin) ? DesktopResources.Get(plugin.ResourceKey) : id;
            var content = new Grid { ColumnSpacing = 6, MaxWidth = 320 };
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.Children.Add(DesktopIcons.Create(Icon.Wrench, 14));
            var text = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1); content.Children.Add(text);
            var remove = new Button { Name = "DisableLocalPlugin", Tag = id, Content = DesktopIcons.Create(Icon.Dismiss, 10),
                Width = 24, Height = 24, Padding = new Thickness(0), IsEnabled = !busy };
            ToolbarControls.Subtle(remove); ToolbarControls.Label(remove, DesktopResources.Format("RemoveContext", label));
            remove.Click += async (_, _) =>
            {
                if (busy || closing) return;
                var partition = session.HistoryPartition;
                await RunAsync(async ct =>
                {
                    var next = session.Settings.Clone(); next.Chat.ActivePlugins.RemoveAll(p => p == id);
                    await SettingsStore.SaveAsync(session.DataDirectory, next);
                    ct.ThrowIfCancellationRequested();
                    if (closing || session.HistoryPartition != partition) return;
                    session.Settings = next;
                    RenderContextTags();
                    chatSettingsDialog?.RefreshAvailableTools();
                });
                if (!closing) input.Focus(FocusState.Programmatic);
            };
            Grid.SetColumn(remove, 2); content.Children.Add(remove);
            var badge = new Border { Name = "EnabledLocalPluginBadge", Tag = id, Child = content,
                Padding = new Thickness(10, 2, 4, 2), CornerRadius = new CornerRadius(16) };
            ControlAppearance.TokenBadge(badge); ToolbarControls.Label(badge, label); contextTags.Children.Add(badge);
        }
    }

    private LocalConversationTools? activeConversationTools;
    private void RegisterLocalPlugins(McpTurnSnapshot snapshot, ChatPreferences preferences, string partition, bool includePluginContext = true)
    {
        RegisterAgentPluginTools(snapshot, preferences, includePluginContext);
        RegisterAuthoringPlugins(snapshot, preferences, partition);
        if (preferences.PluginEnabled(DesktopLocalTools.Conversations))
        {
            var tools = new LocalConversationTools(history, partition, documentExtractor,
                () => !closing && session.HistoryPartition == partition,
                async (_, ct) => await LoadHistoryAsync(ct));
            snapshot.ConversationTools = tools;
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.Conversations, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.ArtificialIntelligence))
        {
            var tools = new LocalArtificialIntelligenceTools(aiModelTargets ?? []);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.ArtificialIntelligence, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.WindowsSearch))
        {
            var tools = new LocalWindowsSearchTools(isCurrent: () => !closing && session.HistoryPartition == partition);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.WindowsSearch, tools.CallAsync);
        }
        bool Current() => !closing && session.HistoryPartition == partition;
        if (preferences.PluginEnabled(DesktopLocalTools.Clipboard))
        {
            var clipboard = new NativeWindowsClipboard(DispatcherQueue, Current);
            var tools = new LocalClipboardTools(clipboard.ReadAsync, clipboard.WriteAsync, Current,
                clipboard.ReadImageAsync, clipboard.WriteImageAsync, clipboard.ReadFilesAsync, clipboard.WriteFilesAsync);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.Clipboard, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.WindowInspector))
        {
            var windows = new NativeWindowsWindowInspector(Current);
            var capture = new NativeWindowsCapture(DispatcherQueue,
                () => Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId), Current);
            var tools = new LocalWindowTools(windows.ActiveAsync, windows.ListAsync, windows.InspectAsync, Current, windows.ScreensAsync, capture.CaptureAsync, windows.ManageAsync);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.WindowInspector, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.SystemInformation))
        {
            var tools = new LocalSystemInformationTools(Current);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.SystemInformation, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.AppLauncher))
        {
            var launcher = new NativeWindowsAppLauncher(Current);
            var tools = new LocalAppLauncherTools(launcher.ListAsync, launcher.LaunchAsync, Current);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.AppLauncher, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.Notifications))
        {
            var notifications = new NativeWindowsNotifications(DispatcherQueue, Current);
            var tools = new LocalNotificationTools(notifications.ShowAsync, Current);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.Notifications, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.Ocr))
        {
            var tools = new LocalOcrTools(Current);
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.Ocr, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.Files))
        {
            activeSharedFileTools?.Dispose();
            var filesPartition = session.FilesPartition;
            var tools = new LocalSharedFileTools(sharedFileStore, filesPartition, documentExtractor,
                () => !closing && session.HistoryPartition == partition && session.FilesPartition == filesPartition,
                async ct => { if (!closing && session.FilesPartition == filesPartition && activePage == DesktopPage.Files) await LoadFilesAsync(ct); });
            activeSharedFileTools = tools;
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.Files, tools.CallAsync);
        }
    }
    private void RegisterAuthoringPlugins(McpTurnSnapshot snapshot, ChatPreferences preferences, string partition)
    {
        var filesPartition = session.FilesPartition; var agentPartition = session.AgentPartition;
        var aiPartition = ImageLibraryStore.Partition(session); var imageSettings = session.Settings.Images.Clone();
        bool Current() => !closing && session.HistoryPartition == partition;
        bool FilesCurrent() => Current() && session.FilesPartition == filesPartition;
        var files = new LocalToolFiles(sharedFileStore, filesPartition, Path.Combine(session.DataDirectory, "tool-files", filesPartition),
            FilesCurrent, async ct => { if (FilesCurrent() && activePage == DesktopPage.Files) await LoadFilesAsync(ct); });
        var responses = new LocalResponsesClient(client, http);
        if (preferences.PluginEnabled(DesktopLocalTools.AgentsEditor) || preferences.PluginEnabled(DesktopLocalTools.AgentsRuntime))
        {
            bool AgentsCurrent() => FilesCurrent() && session.AgentPartition == agentPartition;
            var tools = new LocalAgentTools(agentStore, agentPartition, session.DefaultAgents, AgentsCurrent,
                async ct => { if (AgentsCurrent()) await RefreshLocalAgentsAsync(ct); },
                ct => client.ListAsync(ServiceKind.Agents, ct), files,
                (body, ct) => responses.CreateAsync(ServiceKind.Agents, body, AgentsCurrent, ct));
            if (preferences.PluginEnabled(DesktopLocalTools.AgentsEditor)) DesktopLocalTools.Register(snapshot, DesktopLocalTools.AgentsEditor, tools.CallAsync);
            if (preferences.PluginEnabled(DesktopLocalTools.AgentsRuntime)) DesktopLocalTools.Register(snapshot, DesktopLocalTools.AgentsRuntime, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.SkillEditor))
        {
            var tools = new LocalSkillEditorTools(localSkillStore, skillStore, partition, files, FilesCurrent,
                async ct => { if (FilesCurrent()) await RefreshLocalSkillsAsync(ct); });
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.SkillEditor, tools.CallAsync);
        }
        if (preferences.PluginEnabled(DesktopLocalTools.Images))
        {
            bool ImagesCurrent() => FilesCurrent() && ImageLibraryStore.Partition(session) == aiPartition
                && session.Settings.Images.EffectiveRoot == imageSettings.EffectiveRoot;
            var imageClient = new DesktopImageClient(client, http);
            var tools = new LocalImageTools(new ImageLibraryStore(imageSettings.EffectiveRoot, aiPartition), files, imageSettings,
                async (settings, model, prompt, inputs, mask, completed, ct) =>
                {
                    if (!ImagesCurrent()) throw new OperationCanceledException(ct);
                    await imageClient.GenerateAsync(model, prompt, settings, inputs, mask, completed, ct, preferences, ImagesCurrent);
                }, (body, model, ct) => responses.CreateAsync(ServiceKind.Ai, body, ImagesCurrent, ct, preferences, model), ImagesCurrent,
                async ct => { if (ImagesCurrent() && activePage == DesktopPage.Images) await ReloadImageLibraryAsync(ct); });
            DesktopLocalTools.Register(snapshot, DesktopLocalTools.Images, tools.CallAsync);
        }
    }
    private Task SaveTurnHistoryAsync(string partition, Conversation conversation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return activeConversationTools is { } tools ? tools.SaveAsync(conversation, ct) : history.SaveAsync(partition, conversation, ct);
    }
    private bool NeedsSkillCatalog(ChatPreferences preferences) => preferences.PluginEnabled(DesktopLocalTools.SkillSearch)
        || preferences.EnabledSkillIds.Any(id => !id.StartsWith("mcp:", StringComparison.Ordinal));
}
