using Microsoft.UI.Xaml.Controls;
using ModelContextProtocol.Protocol;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly SemaphoreSlim elicitationGate = new(1, 1);
    private readonly CancellationTokenSource elicitationLifetime = new();
    private ElicitationDialog? elicitationDialog;

    private Task<ElicitResult> ShowElicitationAsync(string origin, ElicitRequestParams request, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested || elicitationLifetime.IsCancellationRequested) return Task.FromCanceled<ElicitResult>(
            ct.IsCancellationRequested ? ct : elicitationLifetime.Token);
        // SDK callbacks arrive off-thread; all controls and dialog presentation belong to the shell's UI thread.
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            // Legacy server callbacks may carry the server-request lifetime rather than the original tool token.
            // Always also observe the active UI operation so Stop cannot leave such a prompt open.
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, elicitationLifetime.Token, operation?.Token ?? default);
            var token = lifetime.Token;
            var entered = false;
            var ownsDialog = false;
            try
            {
                await elicitationGate.WaitAsync(token); entered = true;
                // Other native dialogs must close first. Cancellation also removes queued prompts.
                while (historyDialogOpen || catalogDialog is not null || approvalDialog is not null || resourcesDialog is not null)
                    await Task.Delay(50, token);
                token.ThrowIfCancellationRequested();
                if (closing || !session.Settings.ModelContext.EnableFormElicitation)
                { completion.TrySetResult(new() { Action = "decline" }); return; }
                var dialog = new ElicitationDialog(origin, request) { XamlRoot = XamlRoot };
                historyDialogOpen = true; ownsDialog = true;
                elicitationDialog = dialog; SystemAppearance.PrepareDialog(dialog);
                using var cancellation = token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
                token.ThrowIfCancellationRequested();
                await dialog.ShowAsync();
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(dialog.Result);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
            finally
            {
                if (ownsDialog) historyDialogOpen = false;
                if (entered) { elicitationDialog = null; elicitationGate.Release(); }
            }
        })) completion.TrySetCanceled();
        return completion.Task;
    }
}
