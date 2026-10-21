using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckChatFilesAsync(ElementTheme theme)
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6zGAAAAAASUVORK5CYII=";
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme };
        var context = "Chat files / " + theme;
        UIMessagePart Part(object value) => PortableConversations.Part(JsonSerializer.SerializeToElement(value));
        UIMessagePart Generated(string filename, string data) => Part(new { type = "file", mediaType = "image/png",
            url = "data:image/png;base64," + data, providerMetadata = new { test = new { generated_image_id = "image", filename } } });
        async Task Open(Conversation chat) { await (Task)Call(shell, "OpenConversationAsync", chat)!; await Task.Delay(100); shell.UpdateLayout(); }
        async Task Wait(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 60 && !condition(); attempt++) await Task.Delay(50);
        }
        try
        {
            window!.Content = shell; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate();
            await HistoryIdleAsync(shell);
            var output = new ConversationMessage { Status = "streaming", Message = new UIMessage { Id = "image-answer", Role = Role.assistant,
                Parts = [Part(new { type = "tool-image_generation", toolCallId = "image-tool", state = "output-available", providerExecuted = true,
                    input = new { }, output = new { content = new[] { new { type = "image", mimeType = "image/png", data = png } } } }),
                    Generated("house.png", png), Part(new { type = "file", mediaType = "application/pdf", filename = "report.pdf", url = "data:application/pdf;base64,AQID" })] } };
            var chat = new Conversation { Messages = [output] };
            await Open(chat);
            var body = Field<StackPanel>(shell, "transcript");
            Image[] Images() => Descendants(body).OfType<Image>().Where(i => i.Name == "ChatGeneratedImage").ToArray();
            await Wait(() => Images().Length == 2 && Images().All(i => i.Source is not null));
            Check(Images().Length == 2 && Images().All(i => i.Source is not null), context + ": direct and tool-embedded images decode inline in native controls");
            Check(!Descendants(body).OfType<TextBlock>().Any(t => t.Text.Contains(png) || t.Text == DesktopResources.Get("AttachmentsSources")),
                context + ": image-only response does not show raw base64 or a generic attachment/source body");
            var old = Images().Single(i => AutomationProperties.GetName(i) == "house.png");
            var card = Descendants(body).OfType<Border>().Single(b => b.Name == "MessageCard" && Descendants(b).Contains(old));
            output.Message.Parts.Add(Generated("final.png", png));
            Call(shell, "RenderTranscript"); await Task.Delay(150);
            await Wait(() => Images().Any(i => AutomationProperties.GetName(i) == "final.png" && i.Source is not null));
            Check(Images().Length == 2 && Descendants(body).Contains(card)
                && Images().Any(i => AutomationProperties.GetName(i) == "final.png" && i.Source is not null) && old.Source is null,
                context + ": generated preview updates the stable streaming row and unloads the previous image");
            var attachments = Descendants(body).OfType<Button>().Single(b => b.Name == "AttachmentsButton");
            InvokeButton(attachments); await Task.Delay(120);
            var details = Field<StackPanel>(shell, "detailsBody");
            Check(Descendants(details).OfType<TextBlock>().Any(t => t.Text == "report.pdf")
                && Descendants(details).OfType<Button>().Where(b => b.Name == "DownloadAttachment").All(b => b.IsEnabled),
                context + ": original image and document downloads are enabled in the existing attachment drawer");
            Field<SplitView>(shell, "details").IsPaneOpen = false; await Task.Delay(100);

            output.Message.Parts.Add(Part(new { type = "file", mediaType = "image/png", url = "https://media.example/remote.png" }));
            Call(shell, "RenderTranscript"); await Task.Delay(150);
            var remote = Images().Single(i => AutomationProperties.GetName(i) == "remote.png");
            Check(remote.Source is null && Descendants(body).OfType<Button>().Any(b => b.Name == "LoadChatImagePreview" && b.Visibility == Visibility.Visible && b.IsEnabled),
                context + ": remote image has an explicit load action and no automatic network source");
            output.Message.Parts.Add(Generated("broken.png", "%%%"));
            Call(shell, "RenderTranscript"); await Task.Delay(150);
            await Wait(() => Descendants(body).OfType<TextBlock>().Any(t => t.Text == DesktopResources.Get("ImagePreviewFailed")));
            Check(Descendants(body).OfType<TextBlock>().Any(t => t.Text == DesktopResources.Get("ImagePreviewFailed")),
                context + ": invalid embedded image shows a clear preview failure without failing the chat");
            var unloaded = Images();
            await Open(new Conversation()); await Task.Delay(150);
            Check(unloaded.All(i => i.Source is null) && !Images().Any(),
                context + ": switching chats clears preview sources and rejects stale asynchronous publication");

            var document = new Conversation { Messages = [new() { Message = new UIMessage { Id = "document", Role = Role.assistant,
                Parts = [Part(new { type = "file", mediaType = "application/pdf", filename = "only.pdf", url = "data:application/pdf;base64,AQID" })] } }] };
            await Open(document);
            InvokeButton(Descendants(body).OfType<Button>().Single(b => b.Name == "AttachmentsButton")); await Task.Delay(120);
            Check(Descendants(details).OfType<TextBlock>().Any(t => t.Text == "only.pdf")
                && Descendants(details).OfType<Button>().Single(b => b.Name == "DownloadAttachment").IsEnabled,
                context + ": document-only reply retains a usable download without an image renderer");
            File.WriteAllLines(report, results);
        }
        finally { await shell.ShutdownAsync(); window!.Content = null; }
    }
}
