using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace AIHappey.Desktop.Core;

/// <summary>Transport-only WebView2. No XAML/WebView UI or web presentation; all UI remains WinUI.</summary>
public sealed class FishAudioRealtimeSession : RealtimeSession
{
    private const string Origin = "https://fishaudio-transport.aihappey.invalid";
    private readonly Func<CancellationToken, Task<JsonObject>> tokenFactory;
    private readonly string[] tools;
    private readonly DispatcherQueue dispatcher;
    private readonly string nonce = Guid.NewGuid().ToString("N");
    private readonly string profile = Path.Combine(Path.GetTempPath(), "AIHappey", "FishAudio", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> acknowledgements = new();
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Controller? controller;
    private CoreWebView2? core;
    private nint window;
    private long sequence;
    private bool disposed, initialized, started, mediaAllowed;
    private Task? disposal;

    public FishAudioRealtimeSession(Func<CancellationToken, Task<JsonObject>> tokenFactory, IEnumerable<string>? tools = null)
    {
        this.tokenFactory = tokenFactory;
        this.tools = tools?.Distinct(StringComparer.Ordinal).Take(256).ToArray() ?? [];
        dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("Fish Audio requires the native UI dispatcher.");
    }

    private Task Ui(Func<Task> action)
    {
        if (dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () => { try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } }))
            completion.TrySetException(new InvalidOperationException("The native dispatcher is unavailable."));
        return completion.Task;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token);
        try
        {
            await InitializeAsync(linked.Token);
            var token = await tokenFactory(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            mediaAllowed = true;
            await CommandAsync("start", new JsonObject { ["sessionToken"] = token.DeepClone(), ["toolNames"] = new JsonArray(tools.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) }, linked.Token);
        }
        catch { await DisposeAsync(); throw new RealtimeFailureException("fishaudio-start", "Fish Audio could not start. Check the WebView2 Runtime, microphone permission, and connection."); }
    }

    /// <summary>Explicit media smoke test, without minting a provider session or showing any web surface.</summary>
    public async Task ProbeMediaAsync(CancellationToken ct)
    {
        await InitializeAsync(ct); mediaAllowed = true;
        try { await CommandAsync("probe", null, ct); }
        finally { mediaAllowed = false; }
    }

    private Task InitializeAsync(CancellationToken ct) => Ui(async () =>
    {
        if (disposed || started) throw new InvalidOperationException("Fish Audio transport cannot be restarted.");
        started = true;
        ct.ThrowIfCancellationRequested();
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "FishAudio");
        if (!File.Exists(Path.Combine(assets, "runtime.js"))) throw new InvalidOperationException("Bundled Fish Audio assets are missing.");
        // Hidden top-level HWND, never shown or inserted into the native visual tree.
        window = CreateWindowExW(0x08000000, "STATIC", "", 0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
        if (window == 0) throw new InvalidOperationException("Could not create the transport host.");
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" });
        if (disposed || ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(FishAudioRealtimeSession)); }
        var created = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window));
        if (disposed || ct.IsCancellationRequested)
        {
            created.Close(); ct.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(FishAudioRealtimeSession));
        }
        controller = created;
        controller.Bounds = new Windows.Foundation.Rect(0, 0, 1, 1);
        controller.IsVisible = false; // Do not replace this with a visible or transparent web UI.
        core = controller.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.SetVirtualHostNameToFolderMapping(new Uri(Origin).Host, assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, args) => { if (!TrustedSource(args.Uri)) args.Cancel = true; };
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) =>
        {
            args.State = !disposed && mediaAllowed && TrustedSource(core.Source)
                && Uri.TryCreate(args.Uri, UriKind.Absolute, out var permissionUri) && permissionUri.GetLeftPart(UriPartial.Authority) == Origin
                && args.PermissionKind == CoreWebView2PermissionKind.Microphone
                ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
        };
        core.ProcessFailed += (_, _) => { if (!disposed) Fail(new RealtimeFailureException("fishaudio-runtime", "Fish Audio transport runtime stopped.")); };
        core.WebMessageReceived += Receive;
        core.Navigate(Origin + "/index.html#" + nonce);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        initialized = true;
    });

    private bool TrustedSource(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.GetLeftPart(UriPartial.Path) == Origin + "/index.html" && uri.Query.Length == 0 && uri.Fragment == "#" + nonce;

    private void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (disposed || !TrustedSource(args.Source)) return;
        try
        {
            var raw = args.WebMessageAsJson;
            if (raw.Length > 262144) throw new InvalidDataException();
            var message = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidDataException();
            if (message["version"]?.GetValue<int>() != 1 || message["nonce"]?.GetValue<string>() != nonce) return;
            switch (message["kind"]?.ToString())
            {
                case "ready": ready.TrySetResult(true); break;
                case "ack":
                    if (message["id"] is JsonValue id && id.TryGetValue<long>(out var number) && acknowledgements.TryRemove(number, out var pending))
                        pending.TrySetResult(message["ok"]?.GetValue<bool>() == true);
                    break;
                case "event":
                    if (message["event"] is not JsonObject value || value["type"] is not JsonValue) throw new InvalidDataException();
                    var type = value["type"]!.ToString();
                    string[] allowed = ["transcript.user", "transcript.user.delta", "transcript.agent", "transcript.agent.delta", "session.reconnecting", "session.resumed", "session.error", "response.output_item.done", "fishaudio.status", "fishaudio.mode", "fishaudio.session.closed", "fishaudio.tool.started", "fishaudio.tool.completed", "fishaudio.tool.failed", "fishaudio.media.probe"];
                    if (!allowed.Contains(type)) return;
                    Emit((JsonObject)value.DeepClone());
                    if (type == "fishaudio.session.closed") Complete();
                    break;
            }
        }
        catch { Fail(new RealtimeFailureException("fishaudio-bridge", "Fish Audio returned an invalid transport event.")); }
    }

    private async Task CommandAsync(string command, JsonObject? payload, CancellationToken ct)
    {
        if (disposed) throw new ObjectDisposedException(nameof(FishAudioRealtimeSession));
        long id = 0;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Ui(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (disposed) throw new ObjectDisposedException(nameof(FishAudioRealtimeSession));
                if (acknowledgements.Count >= 64) throw new InvalidOperationException("Fish Audio command queue exceeded its limit.");
                // Allocate sequence numbers on the same dispatcher that posts messages: concurrent native callers cannot reorder them.
                id = ++sequence;
                var data = new JsonObject { ["version"] = 1, ["nonce"] = nonce, ["id"] = id, ["command"] = command, ["payload"] = payload?.DeepClone() }.ToJsonString();
                if (data.Length > 262144) throw new InvalidDataException("Fish Audio command exceeded its limit.");
                acknowledgements[id] = completion;
                core!.PostWebMessageAsJson(data); return Task.CompletedTask;
            });
            if (!await completion.Task.WaitAsync(TimeSpan.FromSeconds(command == "start" ? 60 : command == "probe" ? 20 : 5), ct))
                throw new RealtimeFailureException("fishaudio-command", "Fish Audio could not complete the transport operation.");
        }
        finally { acknowledgements.TryRemove(id, out _); }
    }

    public override Task SendAsync(JsonObject message, CancellationToken ct) => CommandAsync("send", message, ct);
    public override Task SetMutedAsync(bool muted) => CommandAsync("mute", new() { ["muted"] = muted }, Lifetime.Token);
    public override ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        if (disposed) return;
        try { if (initialized) await CommandAsync("stop", null, CancellationToken.None); } catch { /* Closing the controller below tears down media even after script/network failure. */ }
        disposed = true; mediaAllowed = false; Lifetime.Cancel();
        ready.TrySetCanceled();
        foreach (var completion in acknowledgements.Values) completion.TrySetCanceled();
        acknowledgements.Clear();
        await Ui(() =>
        {
            if (core is not null) core.WebMessageReceived -= Receive;
            controller?.Close(); controller = null; core = null;
            if (window != 0) { DestroyWindow(window); window = 0; }
            return Task.CompletedTask;
        });
        Complete();
        // A session-specific WebView profile never stores credentials intentionally. Best-effort deletion after process exit.
        _ = Task.Run(async () => { for (var i = 0; i < 10; i++) { try { if (Directory.Exists(profile)) Directory.Delete(profile, true); return; } catch (IOException) { await Task.Delay(1000); } catch (UnauthorizedAccessException) { return; } } });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
