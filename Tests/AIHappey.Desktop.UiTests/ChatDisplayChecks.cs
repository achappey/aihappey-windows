using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckChatDisplayAsync(ChatShell shell, DesktopSession session, Conversation conversation,
        DateTimeOffset timestamp, string context)
    {
        var transcript = Field<StackPanel>(shell, "transcript");
        conversation.Service = ServiceKind.Ai; conversation.Target = "anthropic/current-model";
        var metadata = conversation.Messages[1].Message.Metadata!;
        metadata.Clear();
        foreach (var entry in FinishMessageMetadata.Create("openai/gpt-fixture", timestamp,
            totalTokens: 17, gateway: new FinishGatewayMetadata { Cost = .0123m }).ToDictionary())
            metadata[entry.Key] = entry.Value!;
        var original = PortableConversations.Write(conversation);
        Call(shell, "RenderTranscript"); await Task.Delay(100); shell.UpdateLayout();
        var streamingMessage = conversation.Messages[1];
        var originalParts = streamingMessage.Message.Parts.ToArray();
        var originalStatus = streamingMessage.Status;
        streamingMessage.Status = "streaming";
        streamingMessage.Message.Parts.Clear();
        Call(shell, "RenderTranscript"); await Task.Delay(60); shell.UpdateLayout();
        var streamingCard = Descendants(transcript).OfType<Border>().Where(b => b.Name == "MessageCard").Last();
        var streamingBody = Descendants(streamingCard).OfType<Border>().Single(b => b.Name == "MessageBody");
        var streamingContent = (StackPanel)streamingBody.Child;
        var indicator = streamingContent.Children.OfType<ProgressBar>().Single();
        Check(indicator.Name == "AssistantStreamingProgress" && indicator.IsIndeterminate
            && streamingContent.Children.Count == 1,
            context + ": empty streaming answer shows only an indeterminate progress bar inside its message");
        streamingMessage.Message.Parts.Add(new TextUIPart { Text = "Streaming answer fixture" });
        Call(shell, "RenderTranscript"); await Task.Delay(60); shell.UpdateLayout();
        streamingCard = Descendants(transcript).OfType<Border>().Where(b => b.Name == "MessageCard").Last();
        streamingBody = Descendants(streamingCard).OfType<Border>().Single(b => b.Name == "MessageBody");
        streamingContent = (StackPanel)streamingBody.Child;
        Check(streamingContent.Children.OfType<ChatMarkdown>().Any(m => m.Text == "Streaming answer fixture")
            && streamingContent.Children.Last() is ProgressBar { Name: "AssistantStreamingProgress", IsIndeterminate: true },
            context + ": streaming progress remains below partial assistant content");
        foreach (var status in new[] { "complete", "failed", "interrupted" })
        {
            streamingMessage.Status = status;
            Call(shell, "RenderTranscript"); await Task.Delay(40);
            Check(!Descendants(transcript).OfType<ProgressBar>().Any(b => b.Name == "AssistantStreamingProgress"),
                context + ": streaming progress is removed when assistant status is " + status);
        }
        streamingMessage.Status = originalStatus;
        streamingMessage.Message.Parts.Clear();
        streamingMessage.Message.Parts.AddRange(originalParts);
        Call(shell, "RenderTranscript"); await Task.Delay(60); shell.UpdateLayout();
        Grid[] Logos() => Descendants(transcript).OfType<Grid>().Where(g => g.Name == "ProviderLogo").ToArray();
        string[] Labels() => Descendants(transcript).OfType<TextBlock>().Where(t => t.Name == "MessageModel").Select(t => t.Text).ToArray();
        bool Tokens() => Descendants(transcript).OfType<Border>().Any(b => b.Name == "TokenUsage");
        bool Price() => Descendants(transcript).OfType<Border>().Any(b => b.Name == "MessagePrice");
        Check(Descendants(transcript).OfType<Border>().Where(b => b.Name == "MessagePrice").All(b =>
            ToolTipService.GetToolTip(b) is ToolTip { Content: string warning } && warning == DesktopResources.Get("MessagePriceWarning")
            && AutomationProperties.GetHelpText(b) == warning), context + ": pricing tooltip gives the localized indicative-charge warning");
        Check(!Tokens() && Price(), context + ": default tokens off leaves assistant pricing visible");
        Check(Logos().Length == 2 && Labels().All(label => label == "gpt-fixture"), context + ": answer and activity headers use each assistant's provider logo and abbreviated model");
        Check(Logos().All(logo => logo.Width == 20 && AutomationProperties.GetName(logo).Contains("OpenAI")
            && ToolTipService.GetToolTip(logo) is ToolTip), context + ": compact theme-aware logos have provider-specific accessibility labels and tooltips");
        Check(Logos().All(logo => logo.Children.OfType<Image>().Single().Visibility == Visibility.Collapsed
            && logo.Children.OfType<IconElement>().Single().Visibility == Visibility.Visible), context + ": offline remote-image switch keeps safe native placeholders");
        var userCard = Descendants(transcript).OfType<Border>().First(b => b.Name == "MessageCard");
        Check(!Descendants(userCard).OfType<Grid>().Any(g => g.Name == "ProviderLogo"), context + ": user messages do not get logos");

        var input = Field<TextBox>(shell, "input"); input.Text = "Unsent display-settings draft";
        var dictate = Field<Button>(shell, "dictate");
        Check(Descendants(shell).Contains(dictate) && dictate.IsEnabled
            && AutomationProperties.GetName(dictate) == DesktopResources.Get("SpeechDictate")
            && ToolTipService.GetToolTip(dictate) is ToolTip, context + ": chat composer has an accessible Dictate action");
        var send = Field<Button>(shell, "send");
        Check(VisualTreeHelper.GetParent(dictate) == VisualTreeHelper.GetParent(send)
            && Grid.GetColumn(dictate) + 2 == Grid.GetColumn(send), context + ": Dictate is in the right-hand composer actions immediately before Stop/Send");
        input.Select(7, 4);
        var voiceTyping = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.WindowsVoiceTyping")!;
        var nativeInput = voiceTyping.GetNestedType("Input", System.Reflection.BindingFlags.NonPublic)!;
        Check(System.Runtime.InteropServices.Marshal.SizeOf(nativeInput) == (IntPtr.Size == 8 ? 40 : 28)
            && System.Runtime.InteropServices.Marshal.OffsetOf(nativeInput, "Data").ToInt32() == (IntPtr.Size == 8 ? 8 : 4),
            context + ": voice typing uses the native SendInput union size and alignment");
        var startDictation = voiceTyping.GetMethod("TryStart")!;
        input.IsEnabled = false;
        Check(!(bool)startDictation.Invoke(null, [input])!, context + ": disabled composer cannot launch global voice typing");
        input.IsEnabled = true;
        Call(shell, "SetBusy", true, true);
        Check(!dictate.IsEnabled && !(bool)startDictation.Invoke(null, [input])!
            && input.Text == "Unsent display-settings draft" && input.SelectionStart == 7 && input.SelectionLength == 4,
            context + ": busy/read-only composer blocks dictation without altering its draft or selection");
        Call(shell, "SetBusy", false, false);
        Check(dictate.IsEnabled && !input.IsReadOnly, context + ": Dictate and editing return after inference");
        var speechPage = new SpeechPage();
        speechPage.ReplacePrompt("Unsent speech draft"); speechPage.SetBusy(true, true);
        Check(!speechPage.StartDictation() && speechPage.PromptText == "Unsent speech draft",
            context + ": speech dictation also respects the editable prompt boundary");
        var resolutions = ((UiRuntime)session.Runtime).Resolutions;
        async Task<SettingsDialog> OpenSettings()
        {
            var task = (Task)Call(shell, "EditSettingsAsync")!;
            await Task.Delay(150);
            if (task.IsFaulted) await task;
            var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(shell.XamlRoot).SelectMany(p => Descendants(p.Child)).OfType<SettingsDialog>().Single();
            dialog.Tag = task;
            var tabs = (NavigationView)dialog.FindName("Tabs");
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(item => (string)item.Tag == "chat");
            Descendants(dialog).OfType<CommunityToolkit.WinUI.Controls.SettingsExpander>().Single(e => e.Name == "ToolOutputContentCard").IsExpanded = true;
            await Task.Delay(80); dialog.UpdateLayout();
            Check(((StackPanel)dialog.FindName("ChatPage")).Visibility == Visibility.Visible
                && ((StackPanel)dialog.FindName("GeneralPage")).Visibility == Visibility.Collapsed,
                context + ": native Chat tab shows only its settings page");
            return dialog;
        }
        var cancelled = await OpenSettings();
        var controls = ((StackPanel)cancelled.FindName("ChatPage")).Children.OfType<ToggleSwitch>().ToArray();
        Check(controls.Select(t => t.Name).SequenceEqual(["ShowTokens", "ShowProviderLogo", "ApiDebugEnabled"])
            && !controls[0].IsOn && controls[1].IsOn && !controls[2].IsOn && controls.All(t => AutomationProperties.GetName(t).Length > 0),
            context + ": Chat has three accessible toggles with debug off by default");
        ToggleSwitch[] OutputToggles(SettingsDialog dialog) => ((StackPanel)dialog.FindName("ToolOutputContentPanel")).Children.OfType<ToggleSwitch>().ToArray();
        var outputControls = OutputToggles(cancelled);
        Check(outputControls.Select(t => t.Name).SequenceEqual(["ToolOutputText", "ToolOutputImages", "ToolOutputAudio", "ToolOutputVideo", "ToolOutputDocuments"])
            && outputControls.Select(t => t.IsOn).SequenceEqual([true, false, false, false, false])
            && outputControls.All(t => AutomationProperties.GetName(t).Length > 0), context + ": tool-output card has five localized accessible toggles with browser defaults");
        foreach (var toggle in outputControls) toggle.IsOn = !toggle.IsOn;
        controls[0].IsOn = true; controls[1].IsOn = false; controls[2].IsOn = true;
        InvokeButton(Descendants(cancelled).OfType<Button>().Single(b => b.Name == "CloseButton")); await (Task)cancelled.Tag;
        Check(!session.Settings.Chat.ShowTokens && session.Settings.Chat.ShowProviderLogo && !session.Settings.Chat.ApiDebugEnabled && !Tokens() && Logos().Length == 2,
            context + ": Cancel discards display draft and leaves transcript unchanged");
        Check(session.Settings.Chat.ToolOutputContentSettings.Text && !session.Settings.Chat.ToolOutputContentSettings.Images
            && !session.Settings.Chat.ToolOutputContentSettings.Audio && !session.Settings.Chat.ToolOutputContentSettings.Video
            && !session.Settings.Chat.ToolOutputContentSettings.Documents, context + ": Cancel discards all five tool-output draft values");

        var saved = await OpenSettings();
        ((ToggleSwitch)saved.FindName("ShowTokens")).IsOn = true;
        ((ToggleSwitch)saved.FindName("ShowProviderLogo")).IsOn = false;
        ((ToggleSwitch)saved.FindName("ApiDebugEnabled")).IsOn = true;
        foreach (var toggle in OutputToggles(saved)) toggle.IsOn = !toggle.IsOn;
        window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 900)); await Task.Delay(100); saved.UpdateLayout();
        Check(((NavigationView)saved.FindName("Tabs")).IsPaneToggleButtonVisible
            && ((Grid)saved.FindName("Layout")).ActualWidth <= shell.XamlRoot.Size.Width,
            context + ": narrow Chat settings retains responsive native navigation");
        InvokeButton(Descendants(saved).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await (Task)saved.Tag; await Task.Delay(80);
        Check(session.Settings.Chat.ShowTokens && !session.Settings.Chat.ShowProviderLogo && Tokens() && Logos().Length == 0
            && Labels().All(label => label == "openai/gpt-fixture") && Price(), context + ": Save immediately shows tokens and restores model prefixes without changing prices");
        Check(input.Text == "Unsent display-settings draft" && ((UiRuntime)session.Runtime).Resolutions == resolutions
            && Field<Conversation>(shell, "current") == conversation && PortableConversations.Write(conversation) == original,
            context + ": display Save preserves composer draft, runtime, current conversation and metadata");
        var persisted = await SettingsStore.LoadAsync(session.DataDirectory, new());
        Check(persisted.Chat.ShowTokens && !persisted.Chat.ShowProviderLogo && persisted.Chat.ApiDebugEnabled, context + ": native settings Save persists display and debug choices");
        Check(!persisted.Chat.ToolOutputContentSettings.Text && persisted.Chat.ToolOutputContentSettings.Images
            && persisted.Chat.ToolOutputContentSettings.Audio && persisted.Chat.ToolOutputContentSettings.Video
            && persisted.Chat.ToolOutputContentSettings.Documents, context + ": Save persists all five tool-output values");

        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); await Task.Delay(80);
        var restored = await OpenSettings();
        Check(((ToggleSwitch)restored.FindName("ShowTokens")).IsOn && !((ToggleSwitch)restored.FindName("ShowProviderLogo")).IsOn
            && ((ToggleSwitch)restored.FindName("ApiDebugEnabled")).IsOn,
            context + ": reopening settings restores saved display values");
        Check(OutputToggles(restored).Select(t => t.IsOn).SequenceEqual([false, true, true, true, true]), context + ": reopening restores all tool-output selections");
        foreach (var toggle in OutputToggles(restored)) toggle.IsOn = !toggle.IsOn;
        ((ToggleSwitch)restored.FindName("ShowTokens")).IsOn = false;
        ((ToggleSwitch)restored.FindName("ShowProviderLogo")).IsOn = true;
        ((ToggleSwitch)restored.FindName("ApiDebugEnabled")).IsOn = false;
        InvokeButton(Descendants(restored).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await (Task)restored.Tag; await Task.Delay(80);
        Check(!Tokens() && Logos().Length == 2 && Labels().All(label => label == "gpt-fixture") && Price(),
            context + ": restoring defaults refreshes existing assistant cards without reopening history");
        foreach (var width in new[] { 520, 1280 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(80); shell.UpdateLayout();
            Check(Field<ScrollViewer>(shell, "scroll").ScrollableWidth < 1, context + $": provider headers stay within the viewport at {width}px");
            foreach (var header in Descendants(transcript).OfType<Border>().Where(b => b.Name == "MessageHeader" && Descendants(b).OfType<Border>().Any(b => b.Name == "AiGeneratedBadge")))
            {
                var badge = Descendants(header).OfType<Border>().Single(b => b.Name == "AiGeneratedBadge");
                var time = Descendants(header).OfType<TextBlock>().Single(t => t.Name == "MessageTime");
                var badgeBounds = badge.TransformToVisual(header).TransformBounds(new(0, 0, badge.ActualWidth, badge.ActualHeight));
                var timeBounds = time.TransformToVisual(header).TransformBounds(new(0, 0, time.ActualWidth, time.ActualHeight));
                Check(Math.Abs((badgeBounds.Top + badgeBounds.Height / 2) - (timeBounds.Top + timeBounds.Height / 2)) < 2
                    && timeBounds.Left >= badgeBounds.Right && timeBounds.Left - badgeBounds.Right < 10,
                    context + $": timestamp is centered on the same row directly after the AI badge at {width}px");
            }
        }
        conversation.Messages[1].Message.Metadata!["model"] = "unknown-provider/model";
        Call(shell, "RenderTranscript"); await Task.Delay(60);
        Check(Logos().Length == 0 && Labels().All(label => label == "unknown-provider/model"), context + ": unknown provider uses full label without a misleading logo");
        conversation.Messages[1].Message.Metadata!["model"] = "echo/embedded-model";
        Call(shell, "RenderTranscript"); await Task.Delay(200);
        Check(Logos().Length == 2 && Logos().All(logo => logo.Children.OfType<Image>().Single().Visibility == Visibility.Visible)
            && Labels().All(label => label == "embedded-model"), context + ": embedded provider logos render in offline assistant headers");
        shell.RequestedTheme = shell.RequestedTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light; await Task.Delay(150);
        Check(Logos().All(logo => logo.ActualTheme == shell.ActualTheme) && Labels().All(label => label == "embedded-model"),
            context + ": provider logos follow live native theme changes");
        shell.RequestedTheme = shell.RequestedTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
        conversation.Service = ServiceKind.Agents; conversation.Target = "test-agent";
        conversation.Messages[1].Message.Metadata!["model"] = "test-agent";
        input.Text = ""; Call(shell, "RenderTranscript");
    }
}
