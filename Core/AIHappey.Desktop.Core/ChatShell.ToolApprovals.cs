using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Grid composerBadgeRow = new() { Name = "ComposerBadgeRow", ColumnSpacing = 12, Visibility = Visibility.Collapsed };
    private readonly MessageFooterPanel approvalBadges = new() { Name = "ToolApprovalBadges", AlignRight = true };
    private readonly ScrollViewer approvalBadgeScroll = new() { Name = "ToolApprovalBadgeScroll", MaxHeight = 128,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled, Visibility = Visibility.Collapsed };
    private readonly SemaphoreSlim approvalSettingsWrite = new(1, 1);
    private ToolApprovalDialog? approvalDialog;

    private void PrepareToolApprovals()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///AIHappey.Desktop.Core/ComposerStyles.xaml") });
        PrepareComposerUsage();
        approvalBadgeScroll.Content = approvalBadges;
        composerBadgeRow.RowDefinitions.Add(new() { Height = GridLength.Auto });
        composerBadgeRow.RowDefinitions.Add(new() { Height = GridLength.Auto });
        composerBadgeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        composerBadgeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var selections = new StackPanel { Spacing = 6 };
        selections.Children.Add(contextTagScroll); selections.Children.Add(mcpTagScroll);
        composerBadgeRow.Children.Add(selections);
        Grid.SetColumn(approvalBadgeScroll, 1); composerBadgeRow.Children.Add(approvalBadgeScroll);
        composerBadgeRow.SizeChanged += (_, _) => UpdateComposerBadgeRow();
        RenderApprovalBadges();
    }

    private void UpdateComposerBadgeRow()
    {
        if (composerBadgeRow.ColumnDefinitions.Count == 0) return;
        var left = mcpTagScroll.Visibility == Visibility.Visible || contextTagScroll.Visibility == Visibility.Visible;
        var right = approvalBadges.Children.Count > 0;
        approvalBadgeScroll.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        var stacked = composerBadgeRow.ActualWidth is > 0 and < 640 && left && right;
        composerBadgeRow.Visibility = left || right ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(approvalBadgeScroll, stacked ? 1 : 0);
        Grid.SetColumn(approvalBadgeScroll, stacked ? 0 : 1);
        composerBadgeRow.ColumnDefinitions[0].Width = left || stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        composerBadgeRow.ColumnDefinitions[1].Width = right && !stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        composerBadgeRow.ColumnSpacing = left && right && !stacked ? 12 : 0;
        composerBadgeRow.RowSpacing = stacked ? 8 : 0;
    }

    private async Task<ToolApprovalDecision> ApproveToolAsync(PendingToolApproval pending, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.ToolApprovals.Automatic(pending, session.Settings) is { } automatic) return automatic;
        var dialog = new ToolApprovalDialog(pending) { XamlRoot = XamlRoot };
        approvalDialog = dialog; SystemAppearance.PrepareDialog(dialog);
        try
        {
            using var cancel = ct.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
            await dialog.ShowAsync();
            ct.ThrowIfCancellationRequested();
            if (closing || dialog.Decision is not { } decision)
            {
                operation?.Cancel();
                throw new OperationCanceledException(ct);
            }
            if (decision.Mode == ToolApprovalMode.ThisTool)
            {
                if (!await ChangeApprovalRulesAsync(list => list.Append(pending.ToolName).Distinct(StringComparer.Ordinal).ToList(), enabling: true))
                    throw new InvalidOperationException(DesktopResources.Get("ApprovalSettingsSaveFailed"));
            }
            else if (decision.Mode == ToolApprovalMode.AllTools) session.ToolApprovals.ApproveAll = true;
            RenderApprovalBadges();
            return decision;
        }
        finally { approvalDialog = null; }
    }

    private async Task<bool> ChangeApprovalRulesAsync(Func<List<string>, List<string>> update, bool enabling = false)
    {
        await approvalSettingsWrite.WaitAsync();
        var before = session.Settings.AllowedToolList.ToList();
        try
        {
            session.Settings.AllowedToolList = update(before.ToList());
            RenderApprovalBadges();
            await SettingsStore.SaveAsync(session.DataDirectory, session.Settings);
            return true;
        }
        catch
        {
            // A failed enable must never silently grant future approvals. A failed revoke stays revoked in memory.
            if (enabling) session.Settings.AllowedToolList = before;
            RenderApprovalBadges();
            Show(DesktopResources.Get("ApprovalSettingsSaveFailed"), InfoBarSeverity.Error);
            return false;
        }
        finally { approvalSettingsWrite.Release(); }
    }

    private async Task DisableAutomaticApprovalsAsync(string? tool, bool all = false)
    {
        if (closing) return;
        if (all || tool is null) session.ToolApprovals.ApproveAll = false;
        RenderApprovalBadges();
        if (all) await ChangeApprovalRulesAsync(_ => []);
        else if (tool is not null) await ChangeApprovalRulesAsync(list => list.Where(name => name != tool).ToList());
    }

    private void RenderApprovalBadges()
    {
        approvalBadges.Children.Clear();
        void AddBadge(string label, string? tool)
        {
            var text = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var button = new Button { Name = tool is null ? "AutoApproveAllBadge" : "AutoApproveToolBadge", Tag = tool,
                Content = text, MaxWidth = 360, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16) };
            if (tool is null)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                content.Children.Add(DesktopIcons.Create(Icon.Shield, 14)); content.Children.Add(text);
                button.Content = content; button.Style = (Style)Resources["ComposerBrrrStyle"];
                ControlAppearance.Stock(button);
            }
            else ToolbarControls.Subtle(button);
            ToolbarControls.Label(button, DesktopResources.Format("ManageAutoApproval", label));
            var menu = new MenuFlyout();
            var remove = new MenuFlyoutItem { Name = "DisableAutoApprovalRule", Text = DesktopResources.Get("DisableAutoApproval") };
            remove.Click += async (_, _) => await DisableAutomaticApprovalsAsync(tool);
            var clear = new MenuFlyoutItem { Name = "DisableAllAutoApprovals", Text = DesktopResources.Get("DisableAllAutoApprovals") };
            clear.Click += async (_, _) => await DisableAutomaticApprovalsAsync(null, all: true);
            ControlAppearance.Native(remove); ControlAppearance.Native(clear);
            menu.Items.Add(remove); menu.Items.Add(clear); button.Flyout = menu;
            var badge = new Border { Child = button, CornerRadius = new CornerRadius(16) };
            if (tool is not null) ControlAppearance.TokenBadge(badge);
            approvalBadges.Children.Add(badge);
        }
        if (session.ToolApprovals.ApproveAll) AddBadge(DesktopResources.Get("AutoApproveAllBadge"), null);
        foreach (var tool in session.Settings.AllowedToolList.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            AddBadge(DesktopResources.Format("AutoApproveToolBadge", tool), tool);
        UpdateComposerUsage();
    }
}
