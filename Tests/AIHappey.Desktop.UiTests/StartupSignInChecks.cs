using System.Net;
using System.Reflection;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckStartupSignInAsync(ElementTheme theme)
    {
        Check(!((IDesktopHost)new UiHost(true)).RequiresSignIn,
            $"Startup sign-in / {theme}: HeaderAuth host defaults to no interactive sign-in");
        foreach (var scenario in new[] { "sign in", "cancel", "cached account", "sign-in failure", "close modal" })
        {
            var host = new StartupSignInHost { SignedIn = scenario == "cached account", FailSignIn = scenario == "sign-in failure" };
            var runtime = new StartupSignInRuntime();
            var settings = new DesktopSettings
            {
                Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://startup-auth.example/" },
                Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://startup-agents.example/" }
            };
            var session = new DesktopSession(host, runtime, settings);
            using var handler = new StartupSignInHandler();
            using var http = new HttpClient(handler);
            var shell = new ChatShell(session) { RequestedTheme = theme };
            // Use the real shell and real gateway client, with a deterministic in-memory catalog transport.
            typeof(ChatShell).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(shell, new DesktopChatClient(session, http));
            var context = $"Startup sign-in / {scenario} / {theme}";
            try
            {
                window!.Content = shell; window.Activate();
                await host.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario != "cached account")
                {
                    var dialog = await OpenDialogAsync(shell);
                    Check(host.AccountActions == 1 && handler.Requests == 0 && host.AuthenticationAttempts == 0,
                        context + ": one modal opens before any protected request");
                    var root = (Grid)shell.Content;
                    Check(root.Children[0].Visibility == Visibility.Visible && root.Children[1].Visibility == Visibility.Collapsed
                        && !Field<InfoBar>(shell, "notice").IsOpen,
                        context + ": sign-in modal appears over the shell without a red startup bar");
                    if (scenario == "close modal")
                    {
                        await shell.ShutdownAsync();
                        Check(host.RequiresSignIn && handler.Requests == 0, context + ": shutdown closes the modal without gateway work");
                        continue;
                    }
                    if (scenario == "cancel") dialog.Hide();
                    else Confirm(dialog);
                }
                await Task.Delay(80);
                await HistoryIdleAsync(shell);
                if (scenario == "sign-in failure")
                {
                    Check(Field<InfoBar>(shell, "notice").IsOpen && handler.Requests == 0
                        && Field<Button>(shell, "account").IsEnabled,
                        context + ": genuine sign-in failure stays visible and account recovery remains available");
                    continue;
                }
                Check(!Field<InfoBar>(shell, "notice").IsOpen, context + ": expected sign-in state does not show an error bar");
                if (scenario == "cancel")
                {
                    Check(host.RequiresSignIn && host.AccountActions == 1 && handler.Requests == 0
                        && Field<Button>(shell, "account").IsEnabled,
                        context + ": cancellation is quiet, does not re-prompt, and leaves account sign-in available");
                    await (Task)Call(shell, "DiscoverAsync", CancellationToken.None)!;
                    Check(handler.Requests == 0 && !Field<InfoBar>(shell, "notice").IsOpen,
                        context + ": signed-out model refresh also skips protected requests");
                    Invoke(Field<MenuFlyoutItem>(shell, "manageAccount"));
                    var retry = await OpenDialogAsync(shell);
                    Confirm(retry);
                    await Task.Delay(80); await HistoryIdleAsync(shell);
                    Check(!host.RequiresSignIn && host.AccountActions == 2 && handler.Requests == 1
                        && !Field<InfoBar>(shell, "notice").IsOpen,
                        context + ": later account-button sign-in loads models without restarting");
                }
                Check(!host.RequiresSignIn && handler.Requests == 1
                    && Field<IReadOnlyList<ChatTarget>>(shell, "targets").Count == 1,
                    context + ": authenticated startup loads the model list once");
                if (scenario == "cached account")
                    Check(host.AccountActions == 0, context + ": cached signed-in startup never opens a modal");
            }
            finally
            {
                if (!runtime.Disposed) await shell.ShutdownAsync();
                window!.Content = null;
                if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true);
            }
        }
    }

    private sealed class StartupSignInHost : IDesktopHost
    {
        public string ProfileId { get; } = "StartupSignInTests-" + Guid.NewGuid().ToString("N");
        public bool AllowLocal => false;
        public bool SignedIn { get; set; }
        public bool FailSignIn { get; init; }
        public bool RequiresSignIn => !SignedIn;
        public string AccountLabel => SignedIn ? "startup-user@example.com" : "Sign in";
        public string HistoryIdentity => SignedIn ? "startup-user" : "signed-out";
        public int AccountActions { get; private set; }
        public int AuthenticationAttempts { get; private set; }
        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken ct) { Initialized.TrySetResult(); return Task.CompletedTask; }
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct)
        {
            AuthenticationAttempts++;
            if (RequiresSignIn) throw new InvalidOperationException("Protected request attempted before sign-in.");
            return Task.CompletedTask;
        }
        public async Task ManageAccountAsync(object xamlRoot, CancellationToken ct)
        {
            AccountActions++;
            var dialog = new ContentDialog
            {
                XamlRoot = (XamlRoot)xamlRoot, Title = "Enterprise account",
                Content = "Sign in with your enterprise account.", PrimaryButtonText = "Sign in", CloseButtonText = "Cancel"
            };
            SystemAppearance.PrepareDialog(dialog);
            using var registration = ct.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            var choice = await dialog.ShowAsync();
            ct.ThrowIfCancellationRequested();
            if (choice != ContentDialogResult.Primary) return;
            if (FailSignIn) throw new InvalidOperationException("Sign-in failure fixture");
            SignedIn = true;
        }
    }

    private sealed class StartupSignInRuntime : IRuntimeResolver
    {
        public bool Disposed { get; private set; }
        public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken ct) =>
            Task.FromResult(DesktopSettings.RemoteUri(settings.For(service).RemoteUrl));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class StartupSignInHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"fixture/model","type":"language","owned_by":"fixture"}]}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
