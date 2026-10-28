using FluentIcons.Common;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly SplitView conversationActivity = new()
    {
        Name = "ConversationActivitySplit", PanePlacement = SplitViewPanePlacement.Right,
        DisplayMode = SplitViewDisplayMode.Inline, IsPaneOpen = false, OpenPaneLength = 440, CompactPaneLength = 0
    };
    private readonly ToggleButton activityToggle = new()
    {
        Name = "ConversationActivityToggle", Content = DesktopIcons.Create(Icon.Wrench), Width = 40, Height = 40,
        Padding = new Thickness(0), CornerRadius = new CornerRadius(6), Visibility = Visibility.Collapsed
    };
    // Keep the tab content height bounded by the pane. A vertical StackPanel measures its
    // children with infinite height, so the tab's ScrollViewer would expand instead of scroll.
    private readonly Grid conversationActivityBody = new() { Name = "ConversationActivityBody" };
    private readonly TransientConversationData transientConversationData = new();
    private readonly Dictionary<string, string> toolExplanations = new(StringComparer.Ordinal);
    private readonly HashSet<string> explainingTools = new(StringComparer.Ordinal);
    private ToolOutputDialog? toolOutputDialog;

    private UIElement BuildConversationActivityLayout(UIElement content)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Padding = new Thickness(16, 12, 12, 12), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = DesktopResources.Get("Activities"), FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        });
        var close = ActivityButton(DesktopResources.Get("CloseActivities"), Icon.Dismiss); close.Name = "CloseConversationActivity";
        close.Click += (_, _) => conversationActivity.IsPaneOpen = false;
        Grid.SetColumn(close, 1); header.Children.Add(close); panel.Children.Add(header);
        Grid.SetRow(conversationActivityBody, 1); panel.Children.Add(conversationActivityBody);
        ControlAppearance.Apply(panel, (_, _) => { }, palette =>
        { panel.Background = new SolidColorBrush(palette.Panel); conversationActivity.PaneBackground = new SolidColorBrush(palette.Panel); });
        conversationActivity.PaneClosed += (_, _) => activityToggle.IsChecked = false;
        conversationActivity.SizeChanged += (_, args) =>
        {
            conversationActivity.DisplayMode = args.NewSize.Width < 900 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
            conversationActivity.OpenPaneLength = Math.Min(480, Math.Max(0, args.NewSize.Width));
        };
        conversationActivity.Pane = panel; conversationActivity.Content = content;
        ControlAppearance.Stock(activityToggle); ToolbarControls.Label(activityToggle, DesktopResources.Get("ToggleActivities"));
        activityToggle.Checked += (_, _) => { conversationActivity.IsPaneOpen = true; RefreshConversationActivity(); };
        activityToggle.Unchecked += (_, _) => conversationActivity.IsPaneOpen = false;
        return conversationActivity;
    }

    private void RefreshConversationActivity()
    {
        transientConversationData.SetScope(session.HistoryPartition, current.Id);
        if (canvasSelection.SetScope(session.HistoryPartition, current.Id)) conversationActivityBody.Children.Clear();
        var available = activePage == DesktopPage.Chat && current.Messages.Count > 0;
        activityToggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            canvasSelection.Synchronize([]);
            conversationActivity.IsPaneOpen = false;
            activityToggle.IsChecked = false;
            conversationActivityBody.Children.Clear();
            return;
        }
        if (!conversationActivity.IsPaneOpen) return;
        var selectedIndex = conversationActivityBody.Children.OfType<ToolReviewTabs>().FirstOrDefault()?.SelectedIndex ?? 0;
        var projected = ConversationActivityProjection.Project(current.Messages, transientConversationData.Items);
        var tabs = new ToolReviewTabs("ConversationActivityTabs");
        tabs.Add(DesktopResources.Format("ToolsCount", projected.Tools.Count), ActivityList(projected.Tools, true), "ConversationActivityTools");
        var canvas = ConversationCanvasProjection.Project(current.Messages);
        tabs.Add(DesktopResources.Format("CanvasCount", canvas.Count), CanvasActivityList(canvas), "ConversationActivityCanvas");
        tabs.Add(DesktopResources.Format("DataCount", projected.Data.Count), ActivityList(projected.Data, false), "ConversationActivityData");
        tabs.SelectedIndex = Math.Clamp(selectedIndex, 0, tabs.Count - 1);
        conversationActivityBody.Children.Clear(); conversationActivityBody.Children.Add(tabs);
    }

    private void CaptureConversationData(long scope, StreamEvent item, ConversationMessage output)
    {
        // Refresh the scope before accepting a callback: an old stream cannot leak across accounts/chats.
        transientConversationData.SetScope(session.HistoryPartition, current.Id);
        transientConversationData.Add(scope, item, output);
    }

    private UIElement ActivityList(IReadOnlyList<ConversationActivityItem> items, bool tools)
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(12) };
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Name = tools ? "NoToolActivities" : "NoDataActivities",
                Text = DesktopResources.Get(tools ? "NoToolActivities" : "NoDataActivities"),
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
            });
            return panel;
        }
        var snapshot = tools ? activeMcpTurn ?? Mcp.Capture(toolsOnly: true) : null;
        foreach (var item in items) panel.Children.Add(tools ? ToolActivityCard(item, snapshot!) : DataActivityCard(item));
        return panel;
    }

    private Border ToolActivityCard(ConversationActivityItem item, McpTurnSnapshot snapshot)
    {
        var raw = PortableConversations.Element(item.Part);
        var name = PortableConversations.ToolName(item.Part);
        var key = ToolActivityKey(item);
        var body = new StackPanel { Name = "ConversationToolContent", Spacing = 10, Padding = new Thickness(16) };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(ToolActivityArtwork(item, snapshot));
        var labels = new StackPanel { Spacing = 2 };
        labels.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var metadata = new NativeWrapPanel { Spacing = 6 };
        var stateText = new TextBlock { Name = "ConversationToolState", Text = DesktopResources.Get(ConversationActivityProjection.ToolStateResourceKey(raw)),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        NativeCardSurface.Secondary(stateText, metadata: true); metadata.Children.Add(stateText);
        if (ConversationActivityProjection.ToolOutputSize(raw) is { } size)
        {
            var sizeText = new TextBlock { Text = size }; NativeCardSurface.Secondary(sizeText, metadata: true);
            var badge = new Border { Name = "ConversationToolSize", Child = sizeText, Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center };
            NativeCardSurface.Badge(badge); ToolbarControls.Label(badge, DesktopResources.Format("ToolOutputSize", size));
            metadata.Children.Add(badge);
        }
        labels.Children.Add(metadata);
        Grid.SetColumn(labels, 1); header.Children.Add(labels); body.Children.Add(header);
        if (toolExplanations.TryGetValue(key, out var explanation))
            body.Children.Add(new ChatMarkdown { Name = "ToolExplanation", Text = explanation });
        else if (explainingTools.Contains(key))
            body.Children.Add(new ProgressRing { Name = "ToolExplanationProgress", IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left });
        else if (raw.TryGetProperty("input", out var inputValue)) body.Children.Add(StructuredActivityValue(DesktopResources.Get("Input"), inputValue));
        else if (raw.TryGetProperty("inputText", out var inputText)) body.Children.Add(StructuredActivityValue(DesktopResources.Get("StreamingInput"), inputText));
        else body.Children.Add(ToolOutputView.Text(DesktopResources.Get("NoToolInput")));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var output = new Button { Name = "ConversationToolOutput", Content = DesktopIcons.Create(Icon.Info, 16),
            Width = 32, Height = 32, Padding = new Thickness(0) };
        NativeCardSurface.Action(output); ToolbarControls.Label(output, DesktopResources.Format("ShowToolOutput", name));
        output.IsEnabled = DesktopToolApprovals.HasOutput(raw) || !string.IsNullOrWhiteSpace(PortableConversations.String(raw, "errorText"));
        output.Click += async (_, _) => await ShowToolOutputAsync(name, raw);
        actions.Children.Add(output);
        var explain = new Button { Name = "ExplainToolCall", Content = DesktopIcons.Create(Icon.Chat, 16), Width = 32, Height = 32, Padding = new Thickness(0) };
        NativeCardSurface.Action(explain); ToolbarControls.Label(explain, DesktopResources.Format("ExplainToolCall", name));
        explain.IsEnabled = !explainingTools.Contains(key);
        explain.Click += async (_, _) => await ExplainToolAsync(item, key);
        if (AssignedAppAgent(AppAgentRole.ExplainToolCall) is not null) actions.Children.Add(explain);
        var footer = new Border { Name = "ConversationToolFooter", Child = actions,
            Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        NativeCardSurface.Divider(footer);
        var layout = new StackPanel(); layout.Children.Add(body); layout.Children.Add(footer);
        var card = new Border { Name = "ConversationToolCard", Child = layout };
        NativeCardSurface.Card(card); AutomationProperties.SetName(card, name); return card;
    }

    private static FrameworkElement ToolActivityArtwork(ConversationActivityItem item, McpTurnSnapshot snapshot)
    {
        var raw = PortableConversations.Element(item.Part);
        var providerExecuted = raw.TryGetProperty("providerExecuted", out var executed) && executed.ValueKind == JsonValueKind.True;
        var icons = McpIcons.Read(raw);
        if (icons.Count == 0 && !providerExecuted) icons = snapshot.IconsForTool(DesktopToolApprovals.CanonicalName(item.Part));
        if (icons.Count > 0) return new McpServerIcon(icons, 24, Icon.Wrench);
        if (providerExecuted && PortableConversations.String(raw, "providerId") is { } providerId
            && ProviderCatalog.All.TryGetValue(providerId.Trim().ToLowerInvariant().Split('/')[0], out var provider))
            return new ProviderLogo(provider, 24, true, Icon.Wrench) { VerticalAlignment = VerticalAlignment.Center };
        return DesktopIcons.Create(Icon.Wrench, 18);
    }

    private static Border DataActivityCard(ConversationActivityItem item)
    {
        var raw = PortableConversations.Element(item.Part);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = item.Part.Type, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (raw.TryGetProperty("data", out var data)) body.Children.Add(StructuredActivityValue(DesktopResources.Get("Data"), data));
        else body.Children.Add(StructuredActivityValue(DesktopResources.Get("Data"), raw));
        var card = new Border { Name = "ConversationDataCard", Child = body };
        NativeCardSurface.Card(card); AutomationProperties.SetName(card, item.Part.Type); return card;
    }

    private static Border StructuredActivityValue(string title, JsonElement value)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(ToolOutputView.Value(value));
        var border = new Border { Child = panel, Padding = new Thickness(12), CornerRadius = new CornerRadius(6) };
        ControlAppearance.TokenBadge(border); return border;
    }

    private async Task ShowToolOutputAsync(string toolName, JsonElement part)
    {
        if (toolOutputDialog is not null || closing) return;
        var dialog = new ToolOutputDialog(toolName, part) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); toolOutputDialog = dialog;
        try { await dialog.ShowAsync(); }
        finally { toolOutputDialog = null; }
    }

    private string ToolActivityKey(ConversationActivityItem item)
    {
        var raw = PortableConversations.Element(item.Part);
        return current.Id + ":" + (PortableConversations.String(raw, "toolCallId") ?? item.MessageId + ":" + item.PartIndex);
    }

    private async Task ExplainToolAsync(ConversationActivityItem item, string key)
    {
        if (closing || explainingTools.Contains(key)) return;
        var agent = AssignedAppAgent(AppAgentRole.ExplainToolCall);
        if (agent is null) return;
        var assignment = session.Settings.SideInferenceAgentNames.Get(AppAgentRole.ExplainToolCall);
        var conversationId = current.Id;
        var partition = session.HistoryPartition;
        var raw = PortableConversations.Element(item.Part).Clone();
        explainingTools.Add(key); RefreshConversationActivity();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(appAgentLifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var models = aiModelTargets ?? await client.ListAsync(ServiceKind.Ai, lifetime.Token);
            var language = session.ActiveLanguage == "nl" ? "Nederlands" : "English";
            var input = DesktopAppAgentClient.ToolExplanationInput(raw, null, language);
            var explanation = await appAgentClient.InvokeAsync(agent, models, input, DesktopResources.Get("ToolExplanationFallback"), lifetime.Token);
            if (!closing && !lifetime.IsCancellationRequested && current.Id == conversationId && session.HistoryPartition == partition
                && session.Settings.SideInferenceAgentNames.Get(AppAgentRole.ExplainToolCall) == assignment)
                toolExplanations[key] = explanation;
        }
        catch (OperationCanceledException) { }
        finally
        {
            explainingTools.Remove(key);
            if (!closing && current.Id == conversationId) RefreshConversationActivity();
        }
    }
}
