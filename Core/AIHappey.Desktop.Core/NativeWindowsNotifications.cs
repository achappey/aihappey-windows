using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;

namespace AIHappey.Desktop.Core;

/// <summary>Only process-lifetime SDK registration is retained; no notification history or tool state.</summary>
internal sealed class NativeWindowsNotifications(DispatcherQueue dispatcher, Func<bool> isCurrent)
{
    private static readonly object gate = new();
    private static bool registered, stopped;

    public async Task<uint> ShowAsync(LocalNotificationRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        Check(token);
        if (!dispatcher.TryEnqueue(() =>
        {
            try
            {
                lock (gate)
                {
                    Check(token);
                    if (stopped || !AppNotificationManager.IsSupported()) throw Unavailable();
                    var manager = AppNotificationManager.Default;
                    if (!registered)
                    {
                        // SDK registration is lazy and shared by turns. No custom activation routing.
                        manager.NotificationInvoked += OnInvoked;
                        try { manager.Register(); registered = true; }
                        catch { manager.NotificationInvoked -= OnInvoked; throw; }
                    }
                    if (manager.Setting != AppNotificationSetting.Enabled) throw Unavailable();
                    var notification = new AppNotification(LocalNotificationTools.Payload(request));
                    Check(token); manager.Show(notification);
                    completion.TrySetResult(notification.Id);
                }
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception) { completion.TrySetException(Unavailable()); }
        })) throw Unavailable();
        try { return await completion.Task.WaitAsync(token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested) { throw Unavailable(); }
    }

    // No buttons, arguments, commands, or tool calls can be triggered by notification activation.
    private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) { }

    internal static void Stop()
    {
        lock (gate)
        {
            stopped = true;
            if (!registered) return;
            try { AppNotificationManager.Default.Unregister(); }
            catch { /* Notification unavailability must not block application shutdown. */ }
            finally { AppNotificationManager.Default.NotificationInvoked -= OnInvoked; registered = false; }
        }
    }
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new OperationCanceledException(ct);
    }
    private static LocalToolException Unavailable() => new(DesktopResources.Get("LocalNotificationUnavailable"));
}
