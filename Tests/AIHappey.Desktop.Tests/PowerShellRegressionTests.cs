using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class PowerShellRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        Directory.CreateDirectory(root);
        var engine = new PowerShellExecutor();
        async Task<PowerShellCommandResult> Run(string script, int? timeout = null, int? maximum = null)
            => (await engine.ExecuteAsync(new[] { script }, timeout, maximum, root, default)).Single();
        var success = await Run("[Console]::Out.Write('héllo 世界'); [Console]::Error.Write('warning')");
        check(success.Stdout == "héllo 世界" && success.Stderr == "warning" && success.ExitCode == 0, "PowerShell captures Unicode and separate stderr");
        check((await Run("exit 7")).ExitCode == 7, "PowerShell preserves explicit non-zero exits");
        check((await Run("cmd.exe /d /c exit 9")).ExitCode == 9, "PowerShell preserves native-program failures");
        var error = await Run("throw 'deliberate failure'");
        check(error.ExitCode == 1 && error.Stderr.Contains("deliberate failure"), "PowerShell reports terminating errors");
        check((await Run("Write-Error 'non-terminating error'")).ExitCode == 1, "PowerShell reports cmdlet errors");
        var limit = await Run("[Console]::Out.Write(('x' * 100000)); [Console]::Error.Write(('e' * 100000))", maximum: 120);
        check(limit.Stdout.Length + limit.Stderr.Length == 120 && limit.Truncated && limit.ExitCode == 0, "PowerShell bounds and drains combined output");
        var timeout = await Run("[Console]::Out.Write('partial'); [Console]::Out.Flush(); Start-Sleep -Seconds 30", timeout: 2000);
        check(timeout.TimedOut && timeout.ExitCode is null && timeout.Stdout == "partial", "PowerShell timeout retains partial output");
        using (var cancellation = new CancellationTokenSource(1500))
        {
            try { await engine.ExecuteAsync(new[] { "Start-Sleep -Seconds 30" }, null, null, root, cancellation.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { check(true, "PowerShell cancellation is not a model timeout"); }
        }
        var child = await Run("$p = Start-Process -FilePath $PSHOME\\powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30' -PassThru; [Console]::Out.Write($p.Id)");
        var childId = int.Parse(child.Stdout);
        bool Alive() { try { using var process = Process.GetProcessById(childId); return !process.HasExited; } catch (ArgumentException) { return false; } }
        for (var i = 0; i < 20 && Alive(); i++) await Task.Delay(50);
        check(!Alive(), "PowerShell closes descendants even when their parent exits normally");
        var file = Path.Combine(root, "ordered.txt").Replace("'", "''");
        var results = await engine.ExecuteAsync(new[] { $"[IO.File]::WriteAllText('{file}', 'first')", $"[IO.File]::ReadAllText('{file}')", "exit 4" }, null, null, root, default);
        check(results.Count == 3 && results[1].Stdout.Trim() == "first" && results[2].ExitCode == 4, "PowerShell executes ordered command batches and returns one outcome per command");
        check((await Run("[Console]::Out.Write((Get-Location).Path)")).Stdout == root, "PowerShell uses explicit working directory");
        var generic = new LocalPowerShellTools(engine);
        var native = new NativeShellTools(engine);
        var genericResult = await generic.CallAsync(LocalPowerShellTools.ToolName, JsonSerializer.SerializeToElement(new { script = "[Console]::Out.Write('shared')" }), default);
        var nativeResult = await native.CallAsync(JsonSerializer.SerializeToElement(new { commands = new[] { "[Console]::Out.Write('shared')" }, max_output_length = 40 }), default);
        check(genericResult.GetProperty("structuredContent").GetProperty("stdout").GetString() == "shared"
            && nativeResult.GetProperty("structuredContent").GetProperty("output")[0].GetProperty("stdout").GetString() == "shared", "both adapters use the same engine");
        check(nativeResult.GetProperty("structuredContent").GetProperty("max_output_length").GetInt32() == 40, "native adapter echoes max_output_length");
        foreach (var json in new[] { "{}", "{\"commands\":[]}", "{\"commands\":[1]}", "{\"commands\":[\"echo hi\"],\"timeout_ms\":0}" })
            check((await native.CallAsync(JsonSerializer.Deserialize<JsonElement>(json), default)).GetProperty("isError").GetBoolean(), "native shell rejects malformed arguments without execution");
        check(!new ChatPreferences().PluginEnabled(DesktopLocalTools.PowerShell), "PowerShell plugin defaults off");
        var definition = DesktopLocalTools.Definition(LocalPowerShellTools.ToolName);
        check(!definition.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()
            && definition.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()
            && definition.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean(), "PowerShell annotations accurately describe unrestricted command access");
        await ReviewAsync(check, generic, root);
        await TransportAsync(check);
    }

    private static async Task ReviewAsync(Action<bool, string> check, LocalPowerShellTools generic, string root)
    {
        var marker = Path.Combine(root, "review.txt");
        var snapshot = McpTurnSnapshot.Empty;
        DesktopLocalTools.Register(snapshot, DesktopLocalTools.PowerShell, generic.CallAsync);
        var output = new ConversationMessage { Message = new() { Id = "review", Role = Role.assistant } };
        output.Message.Parts.Add(PortableConversations.Part(new JsonObject
        {
            ["type"] = "tool-" + LocalPowerShellTools.ToolName, ["toolCallId"] = "review-call", ["state"] = "approval-requested",
            ["approval"] = new JsonObject { ["id"] = "review-id" },
            ["input"] = new JsonObject { ["script"] = $"[IO.File]::WriteAllText('{marker.Replace("'", "''")}', 'executed')" }
        }));
        var executed = new HashSet<string>();
        await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, executed, "en", default);
        var pending = DesktopToolApprovals.Pending(output.Message).Single();
        check(File.Exists(marker) && pending.Part.TryGetProperty("output", out _), "PowerShell executes before gateway-requested result review");
        check(await DesktopMcpToolExecution.ExecutePendingAsync(output, snapshot, executed, "en", default) == 0, "PowerShell result review never executes the command twice");
        DesktopToolApprovals.Respond(output.Message, pending, new(false, "withheld"));
        check(File.Exists(marker), "PowerShell result denial does not undo command side effects");
    }

    private static async Task TransportAsync(Action<bool, string> check)
    {
        // A non-OpenAI provider key deliberately proves there is no hardcoded client provider gate.
        const string provider = "compatible-provider";
        foreach (var explicitEnvironment in new[] { false, true })
        {
            var preferences = new ChatPreferences { ProviderMetadata = new() { [provider] = JsonNode.Parse("""{"tools":[{"type":"shell","environment":{"type":"local"}}]}""")!.AsObject() } };
            var snapshot = McpTurnSnapshot.Empty;
            NativeShellTools.Register(snapshot, preferences, provider, "/v1/responses", () => true);
            check(snapshot.Contains(NativeShellTools.ToolName) && snapshot.Tools.Count == 0 && !preferences.PluginEnabled(DesktopLocalTools.PowerShell), "native shell is independently enabled without a duplicate function tool");
            var disabled = McpTurnSnapshot.Empty;
            NativeShellTools.Register(disabled, new(), provider, "/v1/responses", () => true);
            check(!disabled.Contains(NativeShellTools.ToolName), "disabled native shell has no execution route");
            var wrongEndpoint = McpTurnSnapshot.Empty;
            NativeShellTools.Register(wrongEndpoint, preferences, provider, "/v1/chat/completions", () => true);
            check(!wrongEndpoint.Contains(NativeShellTools.ToolName), "native shell is not enabled on Chat Completions");
            using var handler = new Handler(explicitEnvironment);
            using var http = new HttpClient(handler);
            var client = new DesktopChatClient(new DesktopSession(new Host(), new Runtime(), new()), http);
            var conversation = new Conversation();
            conversation.Messages.Add(new() { Message = new() { Id = "user", Role = Role.user, Parts = [PortableConversations.Part(new JsonObject { ["type"] = "text", ["text"] = "Execute the commands." })] } });
            var output = new ConversationMessage { Message = new() { Id = "assistant", Role = Role.assistant }, Status = "streaming" };
            conversation.Messages.Add(output);
            var route = new DesktopChatRoute("/v1/responses", provider + "/test", provider);
            await DesktopChatTurn.RunAsync(conversation, output,
                (messages, ct) => client.StreamAsync(ServiceKind.Ai, route.Model, conversation.Id, messages, ct, preferences, provider, mcp: snapshot, route: route),
                (_, _) => throw new Exception("No approval was sent; a dialog must not be shown."), (_, _) => Task.CompletedTask, snapshot, "en", default);
            check(handler.Bodies.Count == 2 && output.Text == "Done", "native shell transport executes and continues without fabricated approval");
            var replay = handler.Bodies[1]["input"]!.AsArray();
            var call = replay.Single(i => i?["type"]?.ToString() == "shell_call")!;
            var result = replay.Single(i => i?["type"]?.ToString() == "shell_call_output")!;
            check(call["id"]!.ToString() == "sh-native" && call["call_id"]!.ToString() == "call-native"
                && call["action"]!["timeout_ms"]!.GetValue<int>() == 10000
                && call["action"]!["commands"]!.AsArray().Count == 2, "native stateless replay preserves IDs, ordered commands, and action limits");
            check(result["call_id"]!.ToString() == "call-native" && result["output"]![0]!["stdout"]!.ToString() == "first"
                && result["output"]![1]!["outcome"]!["exit_code"]!.GetValue<int>() == 6
                && result["max_output_length"]!.GetValue<int>() == 512, "native continuation sends structured ordered outcomes with original call ID and output limit");
            check(!handler.Bodies[1].ToJsonString().Contains("function_call_output") && (call["environment"] is not null) == explicitEnvironment,
                "native replay neither invents function outputs nor adds an omitted environment");
            check(DesktopToolApprovals.Pending(output.Message).Count == 0, "native Responses shell never invents approval metadata");
            var portable = JsonSerializer.Deserialize<UIMessage>(JsonSerializer.Serialize(output.Message, PortableConversations.Json), PortableConversations.Json)!;
            check(NativeChatRequests.Build(route, preferences, new[] { portable })["input"]!.AsArray().Count(i => i?["type"]?.ToString() == "shell_call_output") == 1,
                "native shell stateless replay survives portable history serialization");
        }
    }
    private sealed class Handler(bool explicitEnvironment) : HttpMessageHandler
    {
        public List<JsonObject> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            var call = JsonNode.Parse("""{"type":"shell_call","id":"sh-native","call_id":"call-native","status":"completed","action":{"commands":["[Console]::Out.Write('first')","exit 6"],"timeout_ms":10000,"max_output_length":512}}""")!.AsObject();
            if (explicitEnvironment) call["environment"] = new JsonObject { ["type"] = "local" };
            var packets = Bodies.Count == 1 ? new[] {
                new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = call }.ToJsonString(),
                """{"type":"response.completed","response":{"id":"resp-native","object":"response","status":"completed","output":[]}}""" }
                : NativeChatToolRegressionTests.TextPackets("/v1/responses").Select(p => p.Replace("Final answer", "Done")).ToArray();
            return new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(packets.Select(p => "data: " + p + "\n\n")), Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class Host : IDesktopHost
    {
        public string ProfileId => "shell-tests";
        public bool AllowLocal => true;
        public string AccountLabel => "fixture";
        public string HistoryIdentity => "fixture";
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => Task.CompletedTask;
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Runtime : IRuntimeResolver
    {
        public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken ct) => Task.FromResult(new Uri("https://gateway.example/"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
