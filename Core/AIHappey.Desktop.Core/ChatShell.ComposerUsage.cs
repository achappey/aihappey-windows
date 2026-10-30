using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly StackPanel composerContextUsage = new() { Name = "ComposerContextUsage", Spacing = 4, MinWidth = 100, MaxWidth = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar composerContextProgress = new() { Name = "ComposerContextProgress", Minimum = 0, Maximum = 100, Height = 4, IsIndeterminate = false };
    private readonly TextBlock composerContextCount = new() { Name = "ComposerContextCount", TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border composerCostBadge = new() { Name = "ConversationCostBadge", Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock composerCostText = new() { Name = "ConversationCost", VerticalAlignment = VerticalAlignment.Center };

    private void PrepareComposerUsage()
    {
        ControlAppearance.Stock(composerContextProgress);
        composerContextUsage.Children.Add(composerContextProgress);
        composerContextUsage.Children.Add(composerContextCount);
        var cost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        cost.Children.Add(DesktopIcons.Create(Icon.Money, 14)); cost.Children.Add(composerCostText);
        composerCostBadge.Child = cost; ControlAppearance.TokenBadge(composerCostBadge);
        AutomationProperties.SetHelpText(composerCostBadge, DesktopResources.Get("MessagePriceWarning"));
    }

    private void UpdateComposerUsage()
    {
        var usage = ComposerUsage.Read(current.Messages);
        var model = Service == ServiceKind.Ai && target.Selection.Ids.Count == 1
            ? targets.FirstOrDefault(m => m.Id == target.Text.Trim()) : null;
        var capacity = model?.ContextWindow;
        var showContext = capacity is > 0 && double.IsFinite(capacity.Value);
        if (showContext)
        {
            var used = usage.Tokens ?? 0;
            var total = capacity!.Value;
            var reserved = (double?)session.Settings.Chat.MaxOutputTokens ?? model?.MaxTokens ?? 0;
            if (!double.IsFinite(reserved) || reserved < 0) reserved = 0;
            composerContextProgress.Value = Math.Min(100, used / total * 100);
            composerContextCount.Text = ModelOverviewCatalog.CompactNumber(used) + "/" + ModelOverviewCatalog.CompactNumber(total);
            var hint = DesktopResources.Format("ComposerTokensUsed", used.ToString("N0"), total.ToString("N0"));
            if (reserved > 0) hint += "\n" + DesktopResources.Format("ComposerTokensReserved", reserved.ToString("N0"))
                + "\n" + DesktopResources.Format("ComposerTokensAvailable", Math.Max(0, total - reserved - used).ToString("N0"));
            ToolbarControls.Label(composerContextUsage, hint);
            AutomationProperties.SetName(composerContextProgress, hint);
        }
        else { composerContextProgress.Value = 0; composerContextCount.Text = ""; }
        var price = usage.Cost is { } value ? MessageUsage.FormatCost(value) : null;
        if (price is not null)
        {
            composerCostText.Text = price;
            ToolbarControls.Label(composerCostBadge, DesktopResources.Format("ComposerConversationCost", price) + "\n" + DesktopResources.Get("MessagePriceWarning"));
        }
        // Retain native progress controls across stream updates. Approval rendering may have cleared the panel.
        Place(composerContextUsage, showContext, 0);
        Place(composerCostBadge, price is not null, showContext ? 1 : 0);
        UpdateComposerBadgeRow();

        void Place(UIElement element, bool visible, int index)
        {
            if (!visible) { approvalBadges.Children.Remove(element); return; }
            if (!approvalBadges.Children.Contains(element)) approvalBadges.Children.Insert(index, element);
        }
    }
}
