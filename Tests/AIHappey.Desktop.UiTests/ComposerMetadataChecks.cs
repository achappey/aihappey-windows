using System.Reflection;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckComposerMetadataAsync(ElementTheme theme)
    {
        var context = "Composer metadata / " + theme;
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme }; window!.Content = shell; window.Activate();
        await Task.Delay(200); await HistoryIdleAsync(shell);
        try
        {
            var scale = shell.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200 * scale), (int)(900 * scale)));
            var catalog = new ChatTarget[] { new("fixture/large", "Large") { ContextWindow = 10000, MaxTokens = 1000 },
                new("fixture/small", "Small") { ContextWindow = 5000 }, new("fixture/unknown", "Unknown") };
            typeof(ChatShell).GetField("targets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, catalog);
            var picker = Field<ChatTargetPicker>(shell, "target"); picker.ItemsSource = catalog;
            picker.Configure(false, false, new HashSet<string>(), false, ""); picker.Text = "fixture/large";
            var conversation = Field<Conversation>(shell, "current");
            var message = new ConversationMessage { Message = new UIMessage { Role = Role.assistant,
                Parts = [new TextUIPart { Text = "Composer fixture" }],
                Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>("""{"usage":{"totalTokens":2500},"providerMetadata":{"gateway":{"cost":0.001}}}""")! } };
            conversation.Messages.Add(message);
            session.Settings.Chat.ActivePlugins = [DesktopLocalTools.Conversations, DesktopLocalTools.Files];
            session.Settings.Chat.EnabledSkillIds = ["missing-fixture-skill"];
            session.ToolApprovals.ApproveAll = true;
            Call(shell, "RenderContextTags"); Call(shell, "RenderApprovalBadges"); Call(shell, "RenderTranscript");
            await Task.Delay(120); shell.UpdateLayout();
            var progress = Field<ProgressBar>(shell, "composerContextProgress");
            var count = Field<TextBlock>(shell, "composerContextCount");
            var badgeRow = Field<Grid>(shell, "composerBadgeRow");
            var right = Field<ScrollViewer>(shell, "approvalBadgeScroll");
            Check(!progress.IsIndeterminate && progress.Maximum == 100 && progress.Value == 25
                && count.Text == ModelOverviewCatalog.CompactNumber(2500) + "/" + ModelOverviewCatalog.CompactNumber(10000),
                context + $": native determinate progress and compact context counts agree (value={progress.Value}, count={count.Text}, selected={picker.Text})");
            Check(!Descendants(shell).OfType<Border>().Any(b => b.Name == "ConversationCostBadge"), context + ": one cost entry does not duplicate message price");
            Check(ToolTipService.GetToolTip(Field<StackPanel>(shell, "composerContextUsage")) is ToolTip { Content: string hint }
                && hint.Contains(2500.ToString("N0")) && hint.Contains(1000.ToString("N0")) && hint.Contains(6500.ToString("N0")), context + ": localized usage, reserved-output and available-token tooltip");
            var brrr = Descendants(shell).OfType<Button>().Single(b => b.Name == "AutoApproveAllBadge");
            Check(Descendants(brrr).OfType<FluentIcon>().Any(i => i.Icon == Icon.Shield)
                && brrr.Flyout is MenuFlyout { Items.Count: 2 } && brrr.Style is not null
                && brrr.Background is SolidColorBrush bg && bg.Color.A > 0
                && brrr.Foreground is SolidColorBrush fg && fg.Color != bg.Color,
                context + ": BRRR uses a native accent template with shield, contrasting foreground, and existing management menu");
            Border[] PluginBadges() => Descendants(shell).OfType<Border>().Where(b => b.Name == "EnabledLocalPluginBadge").ToArray();
            Check(PluginBadges().Length == 2 && PluginBadges().All(b => Descendants(b).OfType<FluentIcon>().Any(i => i.Icon == Icon.Wrench)
                && Descendants(b).OfType<Button>().Any(button => button.Name == "DisableLocalPlugin" && button.IsEnabled)),
                context + ": each enabled local plugin has its own icon and direct-disable button");
            Check(PluginBadges().All(b => AutomationProperties.GetName(b) == DesktopResources.Get(DesktopLocalTools.Plugins.Single(p => p.Id == (string)b.Tag).ResourceKey))
                && Descendants(shell).OfType<Border>().Any(b => b.Name == "EnabledSkillBadge"), context + ": localized plugin names preserve existing skill badges");
            Check(Grid.GetRow(right) == 0 && Grid.GetColumn(right) == 1 && badgeRow.ActualWidth > 640, context + ": wide metadata keeps selections left and usage/BRRR right");
            Field<ListView>(picker, "list").SelectedItem = catalog[1]; await Task.Delay(40);
            Check(progress.Value == 50, context + ": selecting another model immediately recalculates context capacity");
            message.Message.Metadata!["usage"] = JsonSerializer.SerializeToElement(new { totalTokens = 50 });
            message.Message.Parts.Add(PortableConversations.Part(JsonSerializer.Deserialize<JsonElement>("""{"type":"tool-fixture","toolCallId":"cost","state":"output-available","output":{"_meta":{"gateway":{"cost":0.002}}}}""")));
            Call(shell, "RenderTranscript"); await Task.Delay(60);
            Check(progress.Value == 1 && ReferenceEquals(progress, Field<ProgressBar>(shell, "composerContextProgress"))
                && Field<TextBlock>(shell, "composerCostText").Text == "0.00>"
                && Descendants(shell).OfType<Border>().Any(b => b.Name == "ConversationCostBadge"),
                context + ": late metadata updates retained progress and total price including tool costs");
            InvokeButton(Descendants(shell).OfType<Button>().Single(b => b.Name == "DisableLocalPlugin" && (string)b.Tag == DesktopLocalTools.Files));
            await HistoryIdleAsync(shell);
            var saved = await SettingsStore.LoadAsync(session.DataDirectory, new());
            Check(session.Settings.Chat.ActivePlugins.SequenceEqual([DesktopLocalTools.Conversations])
                && saved.Chat.ActivePlugins.SequenceEqual([DesktopLocalTools.Conversations]) && PluginBadges().Length == 1
                && saved.Chat.EnabledSkillIds.Contains("missing-fixture-skill"), context + ": direct removal persists only the selected plugin and preserves skill selections");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(500 * scale), (int)(900 * scale)));
            await Task.Delay(150); shell.UpdateLayout();
            brrr = Descendants(shell).OfType<Button>().Single(b => b.Name == "AutoApproveAllBadge");
            Check(Grid.GetRow(right) == 1 && Grid.GetColumn(right) == 0
                && Within(progress, shell) && Within(brrr, shell) && Within(PluginBadges().Single(), shell)
                && right.ScrollableWidth < 1, context + $": narrow metadata stacks and wraps without clipping badge actions (width={badgeRow.ActualWidth}, row={Grid.GetRow(right)}, column={Grid.GetColumn(right)}, progress={Within(progress, shell)}, BRRR={Within(brrr, shell)}, plugin={Within(PluginBadges().Single(), shell)}, scroll={right.ScrollableWidth})");
            Field<ListView>(picker, "list").SelectedItem = catalog[2]; await Task.Delay(40);
            Check(!Descendants(shell).Contains(progress), context + ": models without a context window omit progress");
            Call(shell, "NewConversation"); await Task.Delay(80);
            Check(!Descendants(shell).OfType<Border>().Any(b => b.Name == "ConversationCostBadge")
                && Field<ProgressBar>(shell, "composerContextProgress").Value == 0,
                context + ": new conversation clears old usage and cost");
        }
        finally { await shell.ShutdownAsync(); window.Content = null; if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true); }
    }
}
