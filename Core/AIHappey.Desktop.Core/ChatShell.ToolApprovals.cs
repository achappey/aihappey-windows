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
        approvalBadgeScroll.Content = approvalBadges;
        composerBadgeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        composerBadgeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        composerBadgeRow.Children.Add(mcpTagScroll);
        Grid.SetColumn(approvalBadgeScroll, 1); composerBadgeRow.Children.Add(approvalBadgeScroll);
        RenderApprovalBadges();
    }

    private void UpdateComposerBadgeRow()
    {
        if (composerBadgeRow.ColumnDefinitions.Count == 0) return;
        var left = mcpTagScroll.Visibility == Visibility.Visible;
        var right = approvalBadgeScroll.Visibility == Visibility.Visible;
        composerBadgeRow.Visibility = left || right ? Visibility.Visible : Visibility.Collapsed;
        composerBadgeRow.ColumnDefinitions[0].Width = left ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        composerBadgeRow.ColumnDefinitions[1].Width = right ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        composerBadgeRow.ColumnSpacing = left && right ? 12 : 0;
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
            ToolbarControls.Subtle(button); ToolbarControls.Label(button, DesktopResources.Format("ManageAutoApproval", label));
            var menu = new MenuFlyout();
            var remove = new MenuFlyoutItem { Name = "DisableAutoApprovalRule", Text = DesktopResources.Get("DisableAutoApproval") };
            remove.Click += async (_, _) => await DisableAutomaticApprovalsAsync(tool);
            var clear = new MenuFlyoutItem { Name = "DisableAllAutoApprovals", Text = DesktopResources.Get("DisableAllAutoApprovals") };
            clear.Click += async (_, _) => await DisableAutomaticApprovalsAsync(null, all: true);
            ControlAppearance.Native(remove); ControlAppearance.Native(clear);
            menu.Items.Add(remove); menu.Items.Add(clear); button.Flyout = menu;
            var badge = new Border { Child = button, CornerRadius = new CornerRadius(16) };
            ControlAppearance.TokenBadge(badge); approvalBadges.Children.Add(badge);
        }
        if (session.ToolApprovals.ApproveAll) AddBadge(DesktopResources.Get("AutoApproveAllBadge"), null);
        foreach (var tool in session.Settings.AllowedToolList.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            AddBadge(DesktopResources.Format("AutoApproveToolBadge", tool), tool);
        approvalBadgeScroll.Visibility = approvalBadges.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateComposerBadgeRow();
    }
}
