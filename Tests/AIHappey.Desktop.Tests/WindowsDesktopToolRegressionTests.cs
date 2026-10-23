using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class WindowsDesktopToolRegressionTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static bool Error(JsonElement value) => value.GetProperty("isError").GetBoolean();
    private static JsonElement TextData(JsonElement value) => JsonDocument.Parse(value.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    private static readonly byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aLJ8AAAAASUVORK5CYII=");

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var ids = new[] { DesktopLocalTools.SystemInformation, DesktopLocalTools.AppLauncher };
        check(DesktopLocalTools.Plugins.Count == 16 && DesktopLocalTools.Plugins.Count(p => p.Id == DesktopLocalTools.WindowInspector) == 1
            && !DesktopLocalTools.Plugins.Any(p => p.Id.Contains("capture", StringComparison.Ordinal)), "desktop tools: notifications adds one plugin; capture/management stay in the existing inspector");
        var definitions = DesktopLocalTools.Plugins.Where(p => ids.Contains(p.Id)).SelectMany(p => p.Tools).ToArray();
        check(definitions.Length == 4 && definitions.All(d => !d.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean()), "desktop tools: four closed-schema system/app contracts");
        check(DesktopLocalTools.Definition(LocalAppLauncherTools.LaunchTool).GetProperty("annotations").GetProperty("openWorldHint").GetBoolean()
            && !DesktopLocalTools.Definition(LocalAppLauncherTools.LaunchTool).GetProperty("annotations").GetProperty("idempotentHint").GetBoolean(), "launcher: external browser/app side effects are accurately annotated");
        check(ids.All(id => !new ChatPreferences().PluginEnabled(id) && !JsonSerializer.Deserialize<ChatPreferences>("{}")!.PluginEnabled(id)), "desktop tools: new and migrated settings default both plugins off");
        var preferences = new ChatPreferences { ActivePlugins = ids.ToList() };
        await SettingsStore.SaveAsync(Path.Combine(root, "desktop-tool-settings"), new() { Chat = preferences });
        check((await SettingsStore.LoadAsync(Path.Combine(root, "desktop-tool-settings"), new())).Chat.ActivePlugins.SequenceEqual(ids), "desktop tools: opt-in selections persist");
        foreach (var id in ids)
        {
            var snapshot = McpTurnSnapshot.Empty; var calls = 0;
            DesktopLocalTools.Register(snapshot, id, (_, _, _) => { calls++; return Task.FromResult(DesktopLocalTools.Result(null)); });
            check(calls == 0 && snapshot.Tools.Count == 2, "desktop tools: independent lazy registration of " + id);
        }
        var outputs = new List<(string Name, JsonElement Output)>();
        await CaptureAsync(check, root, outputs);
        await SystemAsync(check, outputs);
        await LauncherAsync(check, root, outputs);
        await WindowActionsAsync(check, outputs);
        await RichClipboardAsync(check, root, outputs);
        await NotificationsAsync(check, root, outputs);
        await NativeWindowActionsAsync(check);
        Filter(check, outputs);
        await ReviewAsync(check);
    }

    private static async Task WindowActionsAsync(Action<bool, string> check, List<(string, JsonElement)> outputs)
    {
        var calls = 0; var current = true; LocalWindowActionRequest? observed = null;
        var names = new[] { LocalWindowTools.ActivateTool, LocalWindowTools.MinimizeTool, LocalWindowTools.RestoreTool, LocalWindowTools.RepositionTool };
        var tools = new LocalWindowTools(_ => Task.FromResult<LocalWindowInfo?>(null), (_, _) => Task.FromResult(new LocalWindowList([], false)),
            (_, _) => throw new Exception("unexpected inspection"), () => current, manage: (request, _) =>
            { observed = request; calls++; return Task.FromResult(new LocalWindowActionResult(false, false, false, false, -1920, 20, 800, 600)); });
        foreach (var name in names)
        {
            var result = await tools.CallAsync(name, Args(new { windowHandle = "0x123", processId = 42, x = -1920, y = -50 }), default);
            check(!Error(result) && observed is { WindowHandle: "0x123", ProcessId: 42 } && !TextData(result).GetProperty("submitted").GetBoolean(), "window actions: exact identity and native refusal are preserved for " + name);
            outputs.Add((name, result));
            check(!DesktopLocalTools.Definition(name).GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "window actions: mutation is not annotated read-only");
        }
        await tools.CallAsync(LocalWindowTools.RepositionTool, Args(new { windowHandle = "0x123", processId = 42, x = -1920, y = -50, width = 800, height = 600 }), default);
        check(observed is { Action: "reposition", X: -1920, Y: -50, Width: 800, Height: 600 }, "window actions: signed coordinates and paired size reach native boundary");
        await tools.CallAsync(LocalWindowTools.RepositionTool, Args(new { windowHandle = "0x123", processId = 42, x = 0, y = 0 }), default);
        check(observed is { Width: null, Height: null }, "window actions: move-only request preserves size");
        var before = calls;
        foreach (var name in names) foreach (var invalid in new[] { Args(new { windowHandle = "0x0", processId = 42 }), Args(new { windowHandle = "0x123" }), Args(new { windowHandle = "0x123", processId = "42" }), Args(new { windowHandle = "0x123", processId = 0 }) })
            check(Error(await tools.CallAsync(name, invalid, default)) && calls == before, "window actions: invalid HWND/PID never reaches native boundary");
        foreach (var invalid in new[]
        {
            Args(new { windowHandle = "0x123", processId = 42, x = "0", y = 0 }), Args(new { windowHandle = "0x123", processId = 42, x = 0.5, y = 0 }),
            Args(new { windowHandle = "0x123", processId = 42, x = 0, y = 0, width = 100 }),
            Args(new { windowHandle = "0x123", processId = 42, x = 0, y = 0, width = 0, height = 1 }),
            Args(new { windowHandle = "0x123", processId = 42, x = 0, y = 0, width = 32769, height = 1 }),
            Args(new { windowHandle = "0x123", processId = 42, x = int.MaxValue, y = 0, width = 1, height = 1 }),
            Args(new { windowHandle = "0x123", processId = 42, x = 0, y = int.MaxValue, width = 1, height = 1 })
        }) check(Error(await tools.CallAsync(LocalWindowTools.RepositionTool, invalid, default)) && calls == before, "window actions: malformed/unpaired/overflow bounds rejected");
        current = false;
        await CanceledAsync(() => tools.CallAsync(LocalWindowTools.ActivateTool, Args(new { windowHandle = "0x123", processId = 42 }), default), check, "window actions: stale session performs no action");
        check(calls == before, "window actions: stale request has no effect");
        current = true; using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await CanceledAsync(() => tools.CallAsync(LocalWindowTools.MinimizeTool, Args(new { windowHandle = "0x123", processId = 42 }), canceled.Token), check, "window actions: pre-canceled request performs no action");
        var native = new NativeWindowsWindowInspector();
        foreach (var action in new[] { "activate", "minimize", "restore", "reposition" })
        {
            try { await native.ManageAsync(new(action, "0x1", int.MaxValue), default); check(false, "window actions: invalid native identity"); }
            catch (LocalToolException) { check(true, "window actions: invalid native identity rejected before effect"); }
        }
    }

    private static async Task RichClipboardAsync(Action<bool, string> check, string root, List<(string, JsonElement)> outputs)
    {
        var directory = Path.Combine(root, "rich-clipboard"); Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "image.png"); await File.WriteAllBytesAsync(imagePath, png);
        var filePath = Path.Combine(directory, "reference.txt"); await File.WriteAllTextAsync(filePath, "private file content never returned");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        var calls = 0; var current = true; string? copiedImage = null; IReadOnlyList<string>? copiedFiles = null;
        LocalClipboardImage? image = new(1, 1, png); LocalClipboardFiles files = new([filePath]);
        var tools = new LocalClipboardTools(_ => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask, () => current,
            _ => { calls++; return Task.FromResult(image); }, (path, _) => { calls++; copiedImage = path; return Task.CompletedTask; },
            _ => { calls++; return Task.FromResult(files); }, (paths, _) => { calls++; copiedFiles = paths; return Task.CompletedTask; });
        var result = await tools.CallAsync(LocalClipboardTools.GetImageTool, Args(new { }), default);
        check(!Error(result) && TextData(result).GetProperty("hasImage").GetBoolean() && result.GetProperty("content")[1].GetProperty("data").GetString() == Convert.ToBase64String(png), "rich clipboard: inline PNG and filterable metadata");
        outputs.Add((LocalClipboardTools.GetImageTool, result));
        var copied = await tools.CallAsync(LocalClipboardTools.SetImageTool, Args(new { path = imagePath }), default);
        check(!Error(copied) && copiedImage == imagePath && !copied.GetRawText().Contains("image.png"), "rich clipboard: valid local image path copied once without echo");
        outputs.Add((LocalClipboardTools.SetImageTool, copied));
        var references = await tools.CallAsync(LocalClipboardTools.GetFilesTool, Args(new { }), default);
        check(TextData(references).GetProperty("paths")[0].GetString() == filePath && !references.GetRawText().Contains("private file content"), "rich clipboard: references, never file contents");
        outputs.Add((LocalClipboardTools.GetFilesTool, references));
        var copiedReferences = await tools.CallAsync(LocalClipboardTools.SetFilesTool, Args(new { paths = new[] { filePath, imagePath } }), default);
        check(!Error(copiedReferences) && copiedFiles!.SequenceEqual([filePath, imagePath]) && !copiedReferences.GetRawText().Contains("reference.txt"), "rich clipboard: bounded copy-only references without echo");
        outputs.Add((LocalClipboardTools.SetFilesTool, copiedReferences));
        image = null; files = new([]);
        check(!TextData(await tools.CallAsync(LocalClipboardTools.GetImageTool, Args(new { }), default)).GetProperty("hasImage").GetBoolean()
            && !TextData(await tools.CallAsync(LocalClipboardTools.GetFilesTool, Args(new { }), default)).GetProperty("hasFiles").GetBoolean(), "rich clipboard: empty/unsupported formats explicit");
        foreach (var invalid in new[] { new LocalClipboardImage(0, 1, png), new(10000, 10000, png), new(1, 1, new byte[24]), new(2, 1, png), new(1, 1, new byte[LocalClipboardTools.MaximumImageBytes + 1]) })
        {
            image = invalid;
            check(Error(await tools.CallAsync(LocalClipboardTools.GetImageTool, Args(new { }), default)), "rich clipboard: malformed/oversized bitmap output rejected");
        }
        files = new([filePath, @"\\server\share\private.txt", directory, Path.Combine(directory, "missing.txt")], true, 2);
        var bounded = TextData(await tools.CallAsync(LocalClipboardTools.GetFilesTool, Args(new { }), default));
        check(bounded.GetProperty("count").GetInt32() == 1 && bounded.GetProperty("unsupportedCount").GetInt32() == 5 && bounded.GetProperty("hasMore").GetBoolean()
            && !bounded.GetRawText().Contains("server"), "rich clipboard: unsupported file references omitted without exposing remote paths");
        files = new(Enumerable.Repeat(filePath, 101).ToArray());
        bounded = TextData(await tools.CallAsync(LocalClipboardTools.GetFilesTool, Args(new { }), default));
        check(bounded.GetProperty("count").GetInt32() == 100 && bounded.GetProperty("hasMore").GetBoolean(), "rich clipboard: file reference read count bounded");
        var validCalls = calls;
        foreach (var path in new[] { "relative.png", @"\\server\share\image.png", @"\\?\C:\image.png", imagePath + ":data", directory, Path.Combine(directory, "missing.png"), filePath })
            check(Error(await tools.CallAsync(LocalClipboardTools.SetImageTool, Args(new { path }), default)) && calls == validCalls, "rich clipboard: unsupported image source never reaches native write");
        foreach (var invalid in new[] { Args(new { paths = Array.Empty<string>() }), Args(new { paths = Enumerable.Repeat(filePath, 101).ToArray() }), Args(new { paths = new[] { filePath, @"\\server\share\private.txt" } }), Args(new { paths = new[] { directory } }), Args(new { paths = new[] { imagePath + ":data" } }), Args(new { paths = new object[] { 1 } }), Args(new { paths = filePath }) })
            check(Error(await tools.CallAsync(LocalClipboardTools.SetFilesTool, invalid, default)) && calls == validCalls, "rich clipboard: invalid file list is atomic and never copied");
        using (var held = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check(!Error(await tools.CallAsync(LocalClipboardTools.GetFilesTool, Args(new { }), default)), "rich clipboard: locked file contents are not opened when reading references");
        validCalls = calls; current = false;
        foreach (var name in new[] { LocalClipboardTools.GetImageTool, LocalClipboardTools.SetImageTool, LocalClipboardTools.GetFilesTool, LocalClipboardTools.SetFilesTool })
            await CanceledAsync(() => tools.CallAsync(name, Args(new { path = imagePath, paths = new[] { filePath } }), default), check, "rich clipboard: stale request has no native access");
        check(calls == validCalls, "rich clipboard: stale calls have no effects");
        current = true; using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await CanceledAsync(() => tools.CallAsync(LocalClipboardTools.GetImageTool, Args(new { }), canceled.Token), check, "rich clipboard: pre-cancellation honored");
        var fault = new LocalClipboardTools(_ => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask,
            readImage: _ => throw new IOException("private bitmap details"));
        check(!(await fault.CallAsync(LocalClipboardTools.GetImageTool, Args(new { }), default)).GetRawText().Contains("private bitmap"), "rich clipboard: native failures sanitized");
        check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().SequenceEqual(before) && await File.ReadAllTextAsync(filePath) == "private file content never returned", "rich clipboard: no saved images, shares, copied files or content changes");
    }

    private static async Task NotificationsAsync(Action<bool, string> check, string root, List<(string, JsonElement)> outputs)
    {
        var id = DesktopLocalTools.Notifications;
        check(!new ChatPreferences().PluginEnabled(id) && !JsonSerializer.Deserialize<ChatPreferences>("{}")!.PluginEnabled(id), "notifications: new/migrated settings default off");
        var preferences = new ChatPreferences { ActivePlugins = [id] };
        await SettingsStore.SaveAsync(Path.Combine(root, "notification-settings"), new() { Chat = preferences });
        check((await SettingsStore.LoadAsync(Path.Combine(root, "notification-settings"), new())).Chat.PluginEnabled(id), "notifications: existing settings persist toggle");
        var snapshot = McpTurnSnapshot.Empty; var calls = 0; var current = true; LocalNotificationRequest? observed = null;
        var tools = new LocalNotificationTools((request, _) => { observed = request; calls++; return Task.FromResult(123u); }, () => current);
        DesktopLocalTools.Register(snapshot, id, tools.CallAsync);
        check(calls == 0 && snapshot.Tools.Count == 1 && !DesktopLocalTools.Definition(LocalNotificationTools.ShowTool).GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean(), "notifications: independent lazy registration and closed schema");
        var result = await tools.CallAsync(LocalNotificationTools.ShowTool, Args(new { title = "<tag>& héllo", body = "World\n世界" }), default);
        check(!Error(result) && calls == 1 && observed is { Title: "<tag>& héllo", Body: "World\n世界" }
            && TextData(result).GetProperty("submitted").GetBoolean() && TextData(result).GetProperty("notificationId").GetUInt32() == 123
            && !result.GetRawText().Contains("World"), "notifications: one immediate Unicode notification without payload echo or display claim");
        outputs.Add((LocalNotificationTools.ShowTool, result));
        var payload = XDocument.Parse(LocalNotificationTools.Payload(observed!));
        check(payload.Descendants("text").Select(t => t.Value).SequenceEqual([observed!.Title, observed.Body])
            && payload.Descendants("actions").Count() == 0 && !payload.Descendants("tag").Any() && payload.Root!.Attributes().Count() == 0, "notifications: escaped plain text cannot inject XML, actions, launch arguments or scheduling");
        foreach (var invalid in new[] { Args(new { title = "", body = "x" }), Args(new { title = "  ", body = "x" }), Args(new { title = new string('x', 201), body = "x" }), Args(new { title = "x", body = new string('x', 2001) }), Args(new { title = "a\0b", body = "x" }), Args(new { title = "x", body = "\u0001" }), Args(new { title = 1, body = "x" }), Args(new { title = "x" }) })
            check(Error(await tools.CallAsync(LocalNotificationTools.ShowTool, invalid, default)) && calls == 1, "notifications: invalid title/body never submitted");
        var unavailable = new LocalNotificationTools((_, _) => Task.FromResult(0u));
        check(Error(await unavailable.CallAsync(LocalNotificationTools.ShowTool, Args(new { title = "x", body = "y" }), default)), "notifications: zero native ID is not reported as success");
        var fault = new LocalNotificationTools((_, _) => throw new IOException("private notification details"));
        check(!(await fault.CallAsync(LocalNotificationTools.ShowTool, Args(new { title = "x", body = "y" }), default)).GetRawText().Contains("private notification"), "notifications: native errors sanitized");
        current = false;
        await CanceledAsync(() => tools.CallAsync(LocalNotificationTools.ShowTool, Args(new { title = "x", body = "y" }), default), check, "notifications: stale session cannot submit");
        current = true; using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await CanceledAsync(() => tools.CallAsync(LocalNotificationTools.ShowTool, Args(new { title = "x", body = "y" }), canceled.Token), check, "notifications: canceled request cannot submit");
        check(calls == 1, "notifications: invalid/stale/canceled requests produce no effects");
    }

    private static async Task NativeWindowActionsAsync(Action<bool, string> check)
    {
        var ready = new TaskCompletionSource<(nint Handle, uint ThreadId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var handle = CreateWindowEx(0, "STATIC", "AIHappey regression fixture", 0x10CF0000, 80, 80, 240, 160, 0, 0, 0, 0);
            if (handle == 0) { ready.TrySetException(new InvalidOperationException("Could not create test-owned window.")); return; }
            try
            {
                ready.TrySetResult((handle, GetCurrentThreadId()));
                while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
            finally { DestroyWindow(handle); }
        }) { IsBackground = true };
        thread.Start(); var fixture = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var native = new NativeWindowsWindowInspector();
        var handleText = "0x" + ((ulong)fixture.Handle).ToString("X");
        try
        {
            try { await native.ManageAsync(new("minimize", handleText, int.MaxValue), default); check(false, "window actions: wrong native PID"); }
            catch (LocalToolException) { check(true, "window actions: reused/mismatched HWND PID rejected"); }
            var moved = await native.ManageAsync(new("reposition", handleText, Environment.ProcessId, -100, 40, 320, 200), default);
            check(moved.Submitted && moved.Completed && moved.X == -100 && moved.Y == 40 && moved.Width == 320 && moved.Height == 200, "window actions: real Win32 move/resize only touches test-owned HWND");
            var minimized = await native.ManageAsync(new("minimize", handleText, Environment.ProcessId), default);
            check(minimized.Submitted && minimized.Completed && minimized.Minimized, "window actions: real test-owned window minimizes");
            var restored = await native.ManageAsync(new("restore", handleText, Environment.ProcessId), default);
            check(restored.Submitted && restored.Completed && !restored.Minimized, "window actions: real test-owned window restores");
            var active = await native.ManageAsync(new("activate", handleText, Environment.ProcessId), default);
            check(!active.Completed || active.Foreground, "window actions: real activation either confirms foreground or reports refusal");
        }
        finally { PostThreadMessage(fixture.ThreadId, 0x0012 /* WM_QUIT */, 0, 0); thread.Join(5000); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private static LocalWindowTools WindowTools(Func<LocalWindowCaptureRequest, CancellationToken, Task<LocalWindowCapture?>> capture, Func<bool>? current = null) => new(
        _ => Task.FromResult<LocalWindowInfo?>(null), (_, _) => Task.FromResult(new LocalWindowList([], false)),
        (_, _) => throw new InvalidOperationException("Unexpected inspection"), current,
        _ => Task.FromResult<IReadOnlyList<LocalScreenInfo>>([new(@"\\.\DISPLAY1", -1920, 0, 1920, 1080, false)]), capture);

    private static async Task CaptureAsync(Action<bool, string> check, string root, List<(string, JsonElement)> outputs)
    {
        LocalWindowCaptureRequest? observed = null; var calls = 0; var current = true;
        var tools = WindowTools((request, _) =>
        {
            observed = request; calls++;
            return Task.FromResult<LocalWindowCapture?>(new(request.Source, "Capture fixture", 1, 1, png, request.Source == "screen"));
        }, () => current);
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        var screens = await tools.CallAsync(LocalWindowTools.ScreensTool, Args(new { }), default);
        check(!Error(screens) && TextData(screens).GetProperty("screens")[0].GetProperty("x").GetInt32() == -1920 && calls == 0, "capture: screen metadata supports negative desktop coordinates without taking a frame");
        outputs.Add((LocalWindowTools.ScreensTool, screens));
        var result = await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { source = "window", windowHandle = "0x123", processId = 42 }), default);
        check(!Error(result) && observed is { Source: "window", WindowHandle: "0x123", ProcessId: 42 } && calls == 1, "capture: existing HWND/PID identity reaches the native boundary once");
        check(result.GetProperty("content")[1].GetProperty("mimeType").GetString() == "image/png"
            && Convert.FromBase64String(result.GetProperty("content")[1].GetProperty("data").GetString()!).SequenceEqual(png)
            && TextData(result).GetProperty("width").GetInt32() == 1 && !result.TryGetProperty("structuredContent", out _), "capture: PNG exists only in the image content block, metadata is filterable text");
        outputs.Add((LocalWindowTools.CaptureTool, result));
        var screen = await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { source = "screen", screenId = @"\\.\DISPLAY1" }), default);
        check(observed is { Source: "screen", ScreenId: @"\\.\DISPLAY1" } && TextData(screen).GetProperty("pickerFallback").GetBoolean(), "capture: direct screen identity and picker-fallback metadata are preserved");
        await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { }), default);
        check(observed is { Source: "picker" }, "capture: omitted source requests the native picker");
        await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { source = "picker", windowHandle = "0x23C0A08", processId = 82312, screenId = @"\\.\DISPLAY1" }), default);
        check(observed == new LocalWindowCaptureRequest("picker"), "capture: reported all-fields picker payload opens the picker without validating or forwarding unused window/screen identities");
        await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { source = "picker", windowHandle = "stale", processId = -1, screenId = "" }), default);
        check(observed == new LocalWindowCaptureRequest("picker"), "capture: picker ignores stale/malformed unused selectors");
        await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { windowHandle = "0x123", processId = 42, screenId = @"\\.\DISPLAY1" }), default);
        check(observed == new LocalWindowCaptureRequest("picker"), "capture: default picker also discards optional target selectors");
        await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { source = "window", windowHandle = "0x123", processId = 42, screenId = @"\\.\DISPLAY1" }), default);
        check(observed == new LocalWindowCaptureRequest("window", "0x123", 42), "capture: window source uses only HWND and PID even when screenId is populated");
        await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { source = "screen", screenId = @"\\.\DISPLAY1", windowHandle = "stale", processId = -1 }), default);
        check(observed == new LocalWindowCaptureRequest("screen", ScreenId: @"\\.\DISPLAY1"), "capture: screen source ignores unused window selectors");
        var validCalls = calls;
        foreach (var invalid in new[]
        {
            Args(new { source = "window", windowHandle = "0x123" }), Args(new { source = "window", windowHandle = "0x123", processId = "42" }),
            Args(new { source = "window", windowHandle = "0x0", processId = 42 }), Args(new { source = "window", windowHandle = "0x123", processId = 0 }),
            Args(new { source = "screen" }), Args(new { source = "screen", screenId = "" }), Args(new { source = "screen", screenId = 42 }),
            Args(new { source = "screen", screenId = "a\0b" }), Args(new { source = "all" }), Args(new { source = 42 }), Args(new { source = (string?)null })
        }) check(Error(await tools.CallAsync(LocalWindowTools.CaptureTool, invalid, default)) && calls == validCalls, "capture: invalid selected source/target never invokes native capture or picker");
        var invalidSource = await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { source = "all" }), default);
        var invalidScreen = await tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { source = "screen" }), default);
        check(invalidSource.GetProperty("content")[0].GetProperty("text").GetString() == DesktopResources.Get("LocalCaptureInvalidSource")
            && invalidScreen.GetProperty("content")[0].GetProperty("text").GetString() == DesktopResources.Get("LocalCaptureInvalidScreen"), "capture: source/screen errors are not misleading window-handle errors");
        var canceled = WindowTools((_, _) => Task.FromResult<LocalWindowCapture?>(null));
        check(TextData(await canceled.CallAsync(LocalWindowTools.CaptureTool, Args(new { }), default)).GetProperty("canceled").GetBoolean(), "capture: picker cancellation is explicit and has no image");
        foreach (var bad in new[] { new LocalWindowCapture("window", "x", 0, 1, png, false), new("screen", "x", 10000, 10000, png, false), new("window", "x", 1, 1, new byte[8], false) })
        {
            var invalid = WindowTools((_, _) => Task.FromResult<LocalWindowCapture?>(bad));
            check(Error(await invalid.CallAsync(LocalWindowTools.CaptureTool, Args(new { }), default)), "capture: invalid or oversized native result is never returned");
        }
        current = false;
        await CanceledAsync(() => tools.CallAsync(LocalWindowTools.CaptureTool, Args(new { }), default), check, "capture: stale session cannot capture");
        check(calls == validCalls, "capture: stale capture performs no native work");
        check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().SequenceEqual(before), "capture: no files or screenshot library entries are created");
        var fault = WindowTools((_, _) => throw new IOException("private screenshot detail"));
        check(!(await fault.CallAsync(LocalWindowTools.CaptureTool, Args(new { }), default)).GetRawText().Contains("private screenshot"), "capture: unexpected native failures are sanitized");
    }

    private static async Task SystemAsync(Action<bool, string> check, List<(string, JsonElement)> outputs)
    {
        var tools = new LocalSystemInformationTools();
        var summary = await tools.CallAsync(LocalSystemInformationTools.SummaryTool, Args(new { }), default);
        check(!Error(summary), "system: .NET/Win32 summary runs locally");
        var data = TextData(summary);
        check(data.GetProperty("cpu").GetProperty("logicalProcessors").GetInt32() > 0
            && data.GetProperty("memory").GetProperty("totalBytes").GetUInt64() > 0
            && data.GetProperty("memory").GetProperty("availableBytes").GetUInt64() <= data.GetProperty("memory").GetProperty("totalBytes").GetUInt64()
            && data.GetProperty("windows").GetProperty("build").GetUInt32() > 0, "system: CPU, memory and native Windows build fields are populated");
        outputs.Add((LocalSystemInformationTools.SummaryTool, summary));
        var processes = await tools.CallAsync(LocalSystemInformationTools.ProcessesTool, Args(new { limit = 1 }), default);
        check(!Error(processes) && TextData(processes).GetProperty("count").GetInt32() == 1 && TextData(processes).GetProperty("hasMore").GetBoolean(), "system: process results obey requested bounds and report more");
        outputs.Add((LocalSystemInformationTools.ProcessesTool, processes));
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var filtered = TextData(await tools.CallAsync(LocalSystemInformationTools.ProcessesTool, Args(new { query = self.ProcessName, limit = 200 }), default));
        check(filtered.GetProperty("processes").EnumerateArray().Any(p => p.GetProperty("processId").GetInt32() == Environment.ProcessId)
            && filtered.GetProperty("processes").EnumerateArray().All(p => p.GetProperty("name").GetString()!.Contains(self.ProcessName, StringComparison.OrdinalIgnoreCase)), "system: process substring query includes the test process without command lines");
        foreach (var input in new[] { Args(new { query = 1 }), Args(new { query = new string('x', 257) }), Args(new { query = "a\0b" }), Args(new { limit = 0 }) })
            check(Error(await tools.CallAsync(LocalSystemInformationTools.ProcessesTool, input, default)), "system: malformed query or bounds rejected");
        await CanceledAsync(() => new LocalSystemInformationTools(() => false).CallAsync(LocalSystemInformationTools.SummaryTool, Args(new { }), default), check, "system: stale session cannot query Windows");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await CanceledAsync(() => tools.CallAsync(LocalSystemInformationTools.ProcessesTool, Args(new { }), cancellation.Token), check, "system: canceled process request performs no work");
    }

    private static async Task LauncherAsync(Action<bool, string> check, string root, List<(string, JsonElement)> outputs)
    {
        var launches = 0; LocalAppLaunchRequest? observed = null; var current = true;
        var tools = new LocalAppLauncherTools((_, _, _) => Task.FromResult(new LocalInstalledAppList([new("Fixture.App!Main", "Fixture app")], false)),
            (request, _) => { observed = request; launches++; return Task.CompletedTask; }, () => current);
        var apps = await tools.CallAsync(LocalAppLauncherTools.ListTool, Args(new { limit = 1 }), default);
        check(!Error(apps) && launches == 0 && TextData(apps).GetProperty("apps")[0].GetProperty("appId").GetString() == "Fixture.App!Main", "launcher: listing returns exact installed IDs without launching");
        outputs.Add((LocalAppLauncherTools.ListTool, apps));
        var directory = Path.Combine(root, "launcher"); Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "fixture.exe"); await File.WriteAllBytesAsync(executable, [1, 2, 3]);
        var document = Path.Combine(directory, "fixture.txt"); await File.WriteAllTextAsync(document, "local document");
        foreach (var target in new[] { new LocalAppLaunchRequest("app", "Fixture.App!Main"), new("executable", executable), new("document", document), new("url", "https://example.invalid/local-tool-fixture") })
        {
            var output = await tools.CallAsync(LocalAppLauncherTools.LaunchTool, Args(new { kind = target.Kind, target = target.Target }), default);
            check(!Error(output) && observed == target && TextData(output).GetProperty("launched").GetBoolean(), "launcher: one typed target reaches native boundary: " + target.Kind);
            outputs.Add((LocalAppLauncherTools.LaunchTool, output));
        }
        var validCalls = launches;
        foreach (var input in new[]
        {
            Args(new { kind = "command", target = "echo secret" }), Args(new { kind = "url", target = "file:///C:/test.txt" }),
            Args(new { kind = "url", target = "ms-settings:privacy" }), Args(new { kind = "url", target = "javascript:alert(1)" }),
            Args(new { kind = "url", target = "https://user:pass@example.com" }), Args(new { kind = "url", target = "https://example.com\r\nx" }),
            Args(new { kind = "url", target = "https://example.com", arguments = "--private" }), Args(new { kind = "app", target = "shell:AppsFolder\\Fixture" }),
            Args(new { kind = "executable", target = executable + " /c whoami" }), Args(new { kind = "executable", target = "cmd.exe" }),
            Args(new { kind = "executable", target = @"\\server\share\app.exe" }), Args(new { kind = "executable", target = @"\\?\C:\app.exe" }),
            Args(new { kind = "document", target = document + ":script" }), Args(new { kind = "document", target = Path.Combine(directory, "missing.pdf") }),
            Args(new { kind = "app", target = "Fixture.App!Main", verb = "runas" })
        }) check(Error(await tools.CallAsync(LocalAppLauncherTools.LaunchTool, input, default)) && launches == validCalls, "launcher: invalid/command-like target never reaches native boundary");
        foreach (var extension in new[] { ".cmd", ".bat", ".ps1", ".vbs", ".js", ".hta", ".lnk", ".url", ".msi", ".msc", ".reg", ".scr", ".docm", ".exe" })
        {
            var path = Path.Combine(directory, "blocked" + extension); await File.WriteAllTextAsync(path, "not executable");
            check(Error(await tools.CallAsync(LocalAppLauncherTools.LaunchTool, Args(new { kind = "document", target = path }), default)) && launches == validCalls, "launcher: document route rejects executable/indirect formats " + extension);
        }
        var native = new NativeWindowsAppLauncher();
        var nativeTools = new LocalAppLauncherTools(native.ListAsync, native.LaunchAsync);
        var installed = await nativeTools.CallAsync(LocalAppLauncherTools.ListTool, Args(new { limit = 3 }), default);
        check(!Error(installed) && TextData(installed).GetProperty("count").GetInt32() <= 3, "launcher: real Windows Apps folder can be queried locally without launching");
        check(Error(await nativeTools.CallAsync(LocalAppLauncherTools.LaunchTool, Args(new { kind = "app", target = "AIHappey.Nonexistent.Fixture!Main" }), default)), "launcher: arbitrary non-installed IDs fail exact native discovery");
        check(Error(await nativeTools.CallAsync(LocalAppLauncherTools.LaunchTool, Args(new { kind = "executable", target = executable }), default)), "launcher: non-PE executable rejected before ShellExecute");
        current = false;
        await CanceledAsync(() => tools.CallAsync(LocalAppLauncherTools.LaunchTool, Args(new { kind = "url", target = "https://example.invalid" }), default), check, "launcher: stale handler cannot launch");
        check(launches == validCalls, "launcher: stale handler has no side effects");
        var fault = new LocalAppLauncherTools(native.ListAsync, (_, _) => throw new IOException("private shell error"));
        check(!(await fault.CallAsync(LocalAppLauncherTools.LaunchTool, Args(new { kind = "url", target = "https://example.invalid" }), default)).GetRawText().Contains("private shell error"), "launcher: native error details sanitized");
    }

    private static void Filter(Action<bool, string> check, List<(string Name, JsonElement Output)> outputs)
    {
        foreach (var (name, output) in outputs)
        {
            check(!output.TryGetProperty("structuredContent", out _), "new outputs: no duplicate data outside filterable content: " + name);
            var part = new JsonObject { ["type"] = "tool-" + name, ["toolCallId"] = "fixture", ["state"] = "output-available", ["providerExecuted"] = false, ["output"] = JsonNode.Parse(output.GetRawText()) };
            var message = new UIMessage { Id = "output", Role = Role.assistant, Parts = [PortableConversations.Part(part)] };
            var before = JsonSerializer.Serialize(message, PortableConversations.Json);
            foreach (var text in new[] { true, false }) foreach (var images in new[] { true, false })
            {
                var request = new ChatPreferences { ToolOutputContentSettings = new() { Text = text, Images = images } }.RequestBody("p/model", "chat", [message], "p");
                var filtered = request["messages"]![0]!["parts"]![0]!["output"]!;
                var blocks = filtered["content"]!.AsArray();
                for (var i = 0; i < blocks.Count; i++)
                {
                    var original = output.GetProperty("content")[i];
                    var image = original.GetProperty("type").GetString() == "image";
                    var enabled = image ? images : text;
                    check(enabled ? JsonNode.DeepEquals(blocks[i], JsonNode.Parse(original.GetRawText()))
                        : blocks[i]!["text"]!.GetValue<string>() == (image ? "Image" : "Text") + " content omitted by user settings.", "new outputs: request filter " + name + " Text=" + text + " Images=" + images + " block=" + i);
                }
                if (!images) check(!filtered.ToJsonString().Contains(Convert.ToBase64String(png)), "capture filter: PNG bytes cannot bypass Images through text or metadata");
                check(JsonSerializer.Serialize(message, PortableConversations.Json) == before, "new outputs: filtering preserves local history/preview: " + name);
            }
        }
    }

    private static async Task ReviewAsync(Action<bool, string> check)
    {
        foreach (var name in new[] { LocalAppLauncherTools.LaunchTool, LocalWindowTools.CaptureTool,
            LocalWindowTools.ActivateTool, LocalWindowTools.MinimizeTool, LocalWindowTools.RestoreTool, LocalWindowTools.RepositionTool,
            LocalClipboardTools.SetImageTool, LocalClipboardTools.SetFilesTool, LocalNotificationTools.ShowTool }) foreach (var approved in new[] { true, false })
        {
            var snapshot = McpTurnSnapshot.Empty; var calls = 0; var reviews = 0; var rounds = 0;
            snapshot.AddLocal(DesktopLocalTools.Definition(name), (_, _) => { calls++; return Task.FromResult(DesktopLocalTools.Result(null, DesktopLocalTools.Text("done"))); });
            var conversation = new Conversation();
            var output = new ConversationMessage { Message = new() { Id = "output", Role = Role.assistant }, Status = "streaming" };
            conversation.Messages.Add(output);
            async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> _, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield(); rounds++;
                if (rounds == 1)
                {
                    yield return StreamEvent.Parse(JsonSerializer.Serialize(new { type = "tool-input-available", toolCallId = "fixture", toolName = name, input = new { } }));
                    yield return StreamEvent.Parse("""{"type":"tool-approval-request","approvalId":"review","toolCallId":"fixture"}""");
                }
                else yield return StreamEvent.Parse("""{"type":"text-delta","id":"text","delta":"done"}""");
                yield return StreamEvent.Parse("""{"type":"finish"}""");
            }
            await DesktopChatTurn.RunAsync(conversation, output, Stream, (_, _) =>
            {
                reviews++; check(calls == 1, "new tools: existing review follows one execution of " + name);
                return Task.FromResult(new ToolApprovalDecision(approved));
            }, (_, _) => Task.CompletedTask, snapshot, "en", default);
            check(calls == 1 && reviews == 1, "new tools: approval=" + approved + " never repeats or undoes " + name);
        }
    }

    private static async Task CanceledAsync(Func<Task> call, Action<bool, string> check, string message)
    {
        try { await call(); check(false, message); }
        catch (OperationCanceledException) { check(true, message); }
    }
}
