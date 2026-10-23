using System.Text.Json;
using System.Text.Json.Nodes;
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
        check(DesktopLocalTools.Plugins.Count == 15 && DesktopLocalTools.Plugins.Count(p => p.Id == DesktopLocalTools.WindowInspector) == 1
            && !DesktopLocalTools.Plugins.Any(p => p.Id.Contains("capture", StringComparison.Ordinal)), "desktop tools: only two new plugins, capture belongs to the existing inspector");
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
        Filter(check, outputs);
        await ReviewAsync(check);
    }

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
        foreach (var name in new[] { LocalAppLauncherTools.LaunchTool, LocalWindowTools.CaptureTool }) foreach (var approved in new[] { true, false })
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
