using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class AgentRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var original = DesktopAgent.Parse("""
            {"name":"PortableAgent","description":"Portable fixture","instructions":"Be concise","argumentHint":"Enter a goal",
             "model":{"id":"openai/fixture","options":{"temperature":0.4,"future":true},"providerMetadata":{"reasoning":{"effort":"low","future":7},"futureProvider":{"keep":true}},"providerHeaders":{"OpenAI-Beta":"fixture=v1"},"futureModel":9},
             "responseFormat":{"type":"json_schema","json_schema":{"name":"result","schema":{"type":"object"},"strict":true}},
             "plugins":[{"type":"base64","media_type":"application/zip","data":"opaque","future":true}],
             "skills":[{"type":"skill_reference","skill_id":"provider/missing","version":"latest","future":1},{"type":"inline","name":"inline","description":"Snapshot","source":{"type":"base64","media_type":"application/zip","data":"opaque"}}],
             "mcpClient":{"policy":{"readOnlyHint":true,"futurePolicy":7},"capabilities":{"elicitation":{"form":{},"url":{"future":true}},"futureCapability":true}},
             "mcpServers":{"fixture":{"type":"http","url":"https://mcp.example/","headers":{"Authorization":"Bearer fixture"},"allowed_tools":[],"namespace":true,"future":5}},
             "tools":[{"type":"function","name":"fixture","allowed_callers":["programmatic"],"defer_loading":true,"future":6},{"type":"tool_search","future":7}],
             "evaluations":{"localEvaluator":{"keywordCheck":{"keywords":["OK"],"caseSensitive":true,"future":8},"futureCheck":true},"futureEvaluator":3},"future":{"keep":true}}
            """);
        var draft = original.Clone(); draft.Definition["description"] = "Edited";
        check(original.Description == "Portable fixture" && draft.Description == "Edited", "agent editor draft is isolated from committed definition");
        var roundtrip = DesktopAgent.Parse(draft.Definition.ToJsonString());
        foreach (var key in new[] { "model", "responseFormat", "plugins", "skills", "mcpClient", "mcpServers", "tools", "evaluations", "future" })
            check(JsonNode.DeepEquals(roundtrip.Definition[key], original.Definition[key]), "agent edit/export/import preserves " + key + " including nested unknown fields");
        check(!roundtrip.Definition.ContainsKey("id") && roundtrip.SelectionKey == "local:PortableAgent", "portable agent has browser name identity and no Windows-only ID");
        var targets = DesktopAgentTargets.Project([original.CatalogItem(), new CatalogItem(CatalogKind.Agent, original.Name, original.Name, "Backend same name")]);
        check(targets.Select(t => t.Id).Distinct().Count() == 2 && targets.Single(t => t.LocalAgentName is not null).Id == "local:PortableAgent"
            && targets.Single(t => t.RemoteAgentId is not null).Id == "remote:PortableAgent", "local/backend name collisions have separate browser-compatible selection identities");
        check(DesktopAgentTargets.Restore("PortableAgent", targets) == "remote:PortableAgent" && DesktopAgentTargets.Restore("local:PortableAgent", targets) == "local:PortableAgent"
            && DesktopAgentTargets.Restore("deleted", targets) == "deleted", "conversation restore migrates legacy backend IDs without redirecting local or missing selections");
        draft.ToggleTool("resource_search", true); draft.ToggleTool("tool_search", true);
        check((draft.Definition["tools"] as JsonArray)!.Count == 3 && draft.Definition["tools"]![1]!["future"]!.ToJsonString() == "7", "tool toggles preserve existing options and avoid duplicate enablement");
        draft.ToggleTool("resource_search", false);
        check(JsonNode.DeepEquals(draft.Definition["tools"], original.Definition["tools"]), "tool toggle off restores unrelated tools unchanged");
        draft.ChangeModel("openai/another");
        check(JsonNode.DeepEquals(draft.Definition["model"]?["providerMetadata"], original.Definition["model"]?["providerMetadata"])
            && JsonNode.DeepEquals(draft.Definition["model"]?["providerHeaders"], original.Definition["model"]?["providerHeaders"]), "same-provider model switch preserves agent-scoped metadata and headers");
        draft.ChangeModel("anthropic/fixture");
        check(draft.Definition["model"]?["providerHeaders"] is null && draft.Definition["model"]?["providerMetadata"]?["thinking"]?["type"]?.ToJsonString() == "\"adaptive\""
            && draft.Definition["model"]?["futureModel"]?.ToJsonString() == "9", "provider switch uses browser defaults and retains non-provider model fields");
        draft.ChangeModel("openai/fixture");
        check(draft.Definition["model"]?["providerMetadata"]?["reasoning"]?["effort"]?.ToJsonString() == "\"medium\"", "switching to OpenAI uses exact browser defaults");
        var legacy = DesktopAgent.Parse("""{"name":"Legacy","description":"D","instructions":"I","model":{"id":"openai/model","providerHeaders":{"openai":{" X-Test ":" value "}}},"mcpClient":{"capabilities":{"elicitation":true,"other":{"keep":true}}}}""");
        check(legacy.Definition["model"]?["providerHeaders"]?["X-Test"]?.ToJsonString() == "\"value\"" && legacy.Definition["model"]?["providerHeaders"]?["openai"] is null, "legacy keyed headers normalize to browser-unkeyed agent model headers");
        check(legacy.Definition["mcpClient"]?["capabilities"]?["elicitation"] is null && legacy.Definition["mcpClient"]?["capabilities"]?["other"] is JsonObject,
            "malformed elicitation is discarded without losing other capabilities");
        var target = JsonNode.Parse("""{"reasoning":{"effort":"low","future":1},"plugins":{"keep":true}}""")!.AsObject();
        DesktopAgent.ApplyChanges(target, JsonNode.Parse("""{"reasoning":{"effort":"low"}}""")!.AsObject(), JsonNode.Parse("""{"reasoning":{"effort":"high"}}""")!.AsObject());
        check(target["reasoning"]?["effort"]?.ToJsonString() == "\"high\"" && target["reasoning"]?["future"]?.ToJsonString() == "1" && target["plugins"] is JsonObject,
            "provider form delta changes only edited nested JSON fields");
        var rawTools = JsonNode.Parse("""{"tools":[{"type":"web_search","search_context_size":"low","opaque":9},{"type":"future_tool","keep":true},"opaque-entry"],"instructions":"keep"}""")!.AsObject();
        DesktopAgent.ApplyChanges(rawTools,
            JsonNode.Parse("""{"tools":[{"type":"web_search","search_context_size":"low"},{"type":"future_tool","keep":true}]}""")!.AsObject(),
            JsonNode.Parse("""{"tools":[{"type":"web_search","search_context_size":"high"},{"type":"future_tool","keep":true}]}""")!.AsObject());
        check(rawTools["tools"]?[0]?["opaque"]?.ToJsonString() == "9" && rawTools["tools"]?[2]?.ToJsonString() == "\"opaque-entry\"" && rawTools["instructions"]?.ToJsonString() == "\"keep\"",
            "editing one provider tool preserves other tools, opaque array entries, and unprojected options");
        var aliases = JsonNode.Parse("""{"web_search":{"search_context_size":"low","opaque":9},"shell":{"environment":{"type":"local"}}}""")!.AsObject();
        DesktopAgent.ApplyChanges(aliases, JsonNode.Parse("""{"tools":[{"type":"web_search","search_context_size":"low"}]}""")!.AsObject(),
            JsonNode.Parse("""{"tools":[{"type":"web_search","search_context_size":"high"}]}""")!.AsObject());
        check(aliases["web_search"] is null && aliases["tools"]?[0]?["opaque"]?.ToJsonString() == "9" && aliases["shell"] is JsonObject,
            "provider tool alias migration affects only explicitly edited tools");
        foreach (var json in new[] { "[]", "{}", "{\"name\":\"A\",\"description\":\"D\",\"instructions\":\"I\"}" })
        {
            try { DesktopAgent.Parse(json); throw new Exception("Expected invalid agent rejection"); }
            catch (Exception e) when (e is InvalidDataException or JsonException) { check(true, "malformed/incomplete imported agent rejected: " + json); }
        }
        var store = new DesktopAgentStore(Path.Combine(root, "agents")); var partition = "account-one";
        var initial = await store.ListAsync(partition, [original], CancellationToken.None);
        check(initial.Count == 1 && JsonNode.DeepEquals(initial[0].Definition, original.Definition), "sample defaults are copied into editable local storage");
        await store.SaveAsync(partition, roundtrip, original.Name, CancellationToken.None);
        check((await store.ListAsync(partition, [original], CancellationToken.None)).Single().Description == "Edited", "restart/default injection never overwrites user edits");
        try { await store.SaveAsync(partition, original, null, CancellationToken.None); throw new Exception("Expected duplicate refusal"); }
        catch (InvalidOperationException) { check(true, "duplicate import never overwrites existing agent"); }
        var renamed = original.Clone(); renamed.Definition["name"] = "Changed";
        try { await store.SaveAsync(partition, renamed, original.Name, CancellationToken.None); throw new Exception("Expected rename refusal"); }
        catch (InvalidOperationException) { check(true, "stored agent name is immutable"); }
        await store.DeleteAsync(partition, original.Name, CancellationToken.None);
        check((await store.ListAsync(partition, [original], CancellationToken.None)).Count == 0, "deleted defaults are not recreated and an empty initialized store stays empty");
        check((await store.ListAsync("account-two", [original], CancellationToken.None)).Count == 1, "local agent storage is account-isolated");
        var creates = Enumerable.Range(0, 8).Select(async index =>
        {
            var agent = original.Clone(); agent.Definition["name"] = "Concurrent" + index;
            await new DesktopAgentStore(Path.Combine(root, "agents")).SaveAsync(partition, agent, null, CancellationToken.None);
        });
        await Task.WhenAll(creates);
        check((await store.ListAsync(partition, [], CancellationToken.None)).Count == 8, "concurrent creates across store instances retain every agent");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await store.SaveAsync(partition, original, null, canceled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "canceled save does not publish a draft"); }
        check(!Directory.EnumerateFiles(Path.Combine(root, "agents"), "*.tmp").Any(), "atomic agent writes clean up temporary files");
        var wire = JsonSerializer.SerializeToElement(DesktopChatClient.LocalAgentRequest(original, "conversation", []), PortableConversations.Json);
        check(wire.GetProperty("agents").GetArrayLength() == 1 && JsonNode.DeepEquals(JsonNode.Parse(wire.GetProperty("agents")[0].GetRawText()), original.Definition)
            && !wire.TryGetProperty("model", out _), "local agent request sends the full inline browser contract, not its selection key or a narrowed SDK projection");
        using var handler = new AgentRequestHandler(); using var http = new HttpClient(handler);
        var session = new DesktopSession(new TestHost(), new TestRuntime(), new()); var client = new DesktopChatClient(session, http);
        await foreach (var _ in client.StreamAsync(ServiceKind.Agents, "local:PortableAgent", "conversation", [], CancellationToken.None, localAgent: original)) { }
        check(handler.Body.GetProperty("agents")[0].GetProperty("plugins")[0].GetProperty("data").GetString() == "opaque" && handler.Path.EndsWith("/api/chat"),
            "local agent execution forwards plugins/schema/options to the existing agents gateway unchanged");
        await foreach (var _ in client.StreamAsync(ServiceKind.Agents, "backend-id", "conversation", [], CancellationToken.None)) { }
        check(handler.Body.GetProperty("model").GetString() == "backend-id" && (!handler.Body.TryGetProperty("agents", out var agents) || agents.ValueKind == JsonValueKind.Null),
            "backend agent execution retains its existing model-based request");
        await session.DisposeAsync();
    }
    private sealed class AgentRequestHandler : HttpMessageHandler
    {
        public JsonElement Body;
        public string Path = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath; using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Body = doc.RootElement.Clone();
            return new(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
