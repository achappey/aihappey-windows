using System.Reflection;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckExplorerAsync(ElementTheme theme)
    {
        var shell = new ChatShell(new DesktopSession(new UiHost(true), new UiRuntime(), new())) { RequestedTheme = theme };
        var directory = Path.Combine(Path.GetTempPath(), "AIHappey-Explorer-UI-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var shutDown = false;
        var first = Path.Combine(directory, "first 日本語.txt"); var second = Path.Combine(directory, "second file.txt");
        await File.WriteAllTextAsync(first, "first"); await File.WriteAllTextAsync(second, "second");
        void Set(string field, object value) => typeof(ChatShell).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, value);
        try
        {
            window!.Content = shell; window.Activate();
            var cold = shell.EnqueueExplorerFilesAsync([first]);
            Check(await cold.WaitAsync(TimeSpan.FromSeconds(10)) == "attached", "Explorer cold-start delivery");
            await HistoryIdleAsync(shell);
            var input = Field<TextBox>(shell, "input"); input.Text = "Keep my draft exactly";
            var chat = Field<Conversation>(shell, "current"); var messages = chat.Messages.Count;
            var attachments = Field<List<ComposerAttachment>>(shell, "contextAttachments");
            Check(attachments.Count == 1 && attachments[0].Name == Path.GetFileName(first), "Explorer draft attachment");
            Check(await shell.EnqueueExplorerFilesAsync([second, Path.Combine(directory, "missing.txt"), directory]) == "partial"
                && attachments.Count == 2 && input.Text == "Keep my draft exactly" && ReferenceEquals(chat, Field<Conversation>(shell, "current")), "Explorer mixed batch preserves prompt and conversation");
            Check(chat.Messages.Count == messages, "Explorer never auto-submits");
            Set("busy", true); var queued = shell.EnqueueExplorerFilesAsync([first]); await Task.Delay(80);
            Check(!queued.IsCompleted && Field<InfoBar>(shell, "explorerNotice").IsOpen, "Explorer busy delivery queues visibly");
            Set("busy", false); Call(shell, "ScheduleExplorerDrain");
            Check(await queued == "attached" && attachments.Count == 3, "Explorer idle drain");
            var modal = typeof(ChatShell).GetProperty("historyDialogOpen", BindingFlags.Instance | BindingFlags.NonPublic)!;
            modal.SetValue(shell, true); var canceled = shell.EnqueueExplorerFilesAsync([first]); Call(shell, "CancelExplorerBatches");
            Check(await canceled == "canceled" && attachments.Count == 3, "Explorer pending cancel during modal operation"); modal.SetValue(shell, false);
            var page = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
            Call(shell, "ShowPage", Enum.Parse(page, "Files"));
            Check(await shell.EnqueueExplorerFilesAsync([first]) == "attached" && Field<object>(shell, "activePage").ToString() == "Chat"
                && input.Text == "Keep my draft exactly", "Explorer navigation preserves draft");
            Set("busy", true); var stale = shell.EnqueueExplorerFilesAsync([second]);
            Call(shell, "ResetContext"); Set("busy", false); Call(shell, "ScheduleExplorerDrain");
            Check(await stale == "canceled" && attachments.Count == 0, "Explorer stale-context cancellation");
            Set("busy", true); var closing = shell.EnqueueExplorerFilesAsync([first]);
            Set("busy", false);
            await shell.ShutdownAsync(); shutDown = true;
            Check(await closing == "canceled" && chat.Messages.Count == messages, "Explorer shutdown without sending");
        }
        finally { if (!shutDown) { Set("busy", false); await shell.ShutdownAsync(); } window!.Content = null; Directory.Delete(directory, true); }
    }
}
