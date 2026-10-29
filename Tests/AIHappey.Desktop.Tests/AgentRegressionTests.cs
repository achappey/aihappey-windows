using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class AgentRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        AgentOverviewRegressionTests.Run(check);
        CheckSharedProviders(check);
        var optionalSelection = new ChatSelection(); optionalSelection.Replace(["provider/model"]);
        optionalSelection.Toggle("provider/model", multiple: false, allowEmpty: true);
        check(optionalSelection.Ids.Count == 0, "optional single-select agent model pickers can clear their current model");
        optionalSelection.Toggle("provider/model", multiple: false, allowEmpty: false);
        optionalSelection.Toggle("provider/model", multiple: false, allowEmpty: false);
        check(optionalSelection.Ids.SequenceEqual(["provider/model"]), "ordinary single-select chat model pickers retain their required selection");
        var original = DesktopAgent.Parse("""
            {"name":"PortableAgent","description":"Portable fixture","instructions":"Be concise","argumentHint":"Enter a goal","owned_by":"Team","tags":["support","internal"],
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
        foreach (var key in new[] { "owned_by", "tags", "model", "responseFormat", "plugins", "skills", "mcpClient", "mcpServers", "tools", "evaluations", "future" })
            check(JsonNode.DeepEquals(roundtrip.Definition[key], original.Definition[key]), "agent edit/export/import preserves " + key + " including nested unknown fields");
        check(!roundtrip.Definition.ContainsKey("id") && roundtrip.SelectionKey == "local:PortableAgent", "portable agent has browser name identity and no Windows-only ID");
        var targets = DesktopAgentTargets.Project([original.CatalogItem(), new CatalogItem(CatalogKind.Agent, original.Name, original.Name, "Backend same name")]);
        check(targets.Select(t => t.Id).Distinct().Count() == 2 && targets.Single(t => t.LocalAgentName is not null).Id == "local:PortableAgent"
            && targets.Single(t => t.RemoteAgentId is not null).Id == "remote:PortableAgent", "local/backend name collisions have separate browser-compatible selection identities");
        check(DesktopAgentTargets.Restore("PortableAgent", targets) == "remote:PortableAgent" && DesktopAgentTargets.Restore("local:PortableAgent", targets) == "local:PortableAgent"
            && DesktopAgentTargets.Restore("deleted", targets) == "deleted", "conversation restore migrates legacy backend IDs without redirecting local or missing selections");
        var localTarget = targets.Single(t => t.LocalAgentName is not null);
        var remoteTarget = targets.Single(t => t.RemoteAgentId is not null);
        check(localTarget is { AgentModelId: "openai/fixture", ProviderKey: "openai" }
            && remoteTarget.ProviderKey is null, "agent provider identity comes from the underlying model, not its name or routing key");
        check(remoteTarget.Label == "PortableAgent" && localTarget.Label == "PortableAgent",
            "agent picker labels contain neither internal routing prefixes nor a local suffix");
        check(DesktopAgentTargets.Selection(localTarget.Label, localTarget.Id, targets) == localTarget
            && DesktopAgentTargets.Selection(remoteTarget.Label, remoteTarget.Id, targets) == remoteTarget,
            "displaying a label preserves the selected local/remote identity for same-name agents");
        check(DesktopAgentTargets.Selection("PortableAgent", null, targets) == remoteTarget
            && DesktopAgentTargets.Selection("remote:PortableAgent", null, targets) == remoteTarget
            && DesktopAgentTargets.Selection("local:PortableAgent", null, targets) == localTarget,
            "typed labels, explicit keys and legacy backend IDs resolve to their existing routes");
        check(localTarget.OwnedBy == "Team" && localTarget.Tags.SequenceEqual(new[] { "support", "internal" })
            && DesktopAgentMetadata.Groups(localTarget).SequenceEqual(new[] { "support", "internal" }), "local agent metadata is preserved and tags take precedence over owner");
        using (var listing = JsonDocument.Parse("""
            {"id":"outer","name":"Outer","owned_by":" Listing owner ","tags":[" shared ","",3,"shared"],
             "agent":{"name":"Nested","owned_by":"Nested owner","tags":["shared","nested"]}}
            """))
        {
            var projected = DesktopAgentTargets.Project([CatalogProjection.Agent(listing.RootElement)!]).Single();
            check(projected.OwnedBy == "Listing owner" && projected.Tags.SequenceEqual(new[] { "shared", "nested" }),
                "remote agent projection merges model-list and embedded tags and prefers nonblank listing owner");
        }
        using (var listing = JsonDocument.Parse("""{"id":"standalone","owned_by":" Team ","tags":[" "]}"""))
        {
            var projected = DesktopAgentTargets.Project([CatalogProjection.Agent(listing.RootElement)!]).Single();
            check(projected.ModelMetadata is null && projected.OwnedBy == "Team" && DesktopAgentMetadata.Groups(projected).SequenceEqual(new[] { "Team" }),
                "remote model without embedded agent retains owner grouping and ignores blank tags");
        }
        using (var listing = JsonDocument.Parse("""{"id":"nested-owner","owned_by":" ","agent":{"owned_by":" Nested "}}"""))
            check(DesktopAgentTargets.Project([CatalogProjection.Agent(listing.RootElement)!]).Single().OwnedBy == "Nested",
                "blank listing owner falls back to embedded agent owner");
        check(DesktopAgentMetadata.Groups(new ChatTarget("empty", "Empty") { Tags = [" ", ""], OwnedBy = " " }).Count == 0
            && DesktopAgentMetadata.Groups(remoteTarget).Count == 0, "missing and whitespace-only metadata leaves agents for local/remote fallback");
        var duplicateLabels = new ChatTarget[] { new("remote:one", "Same") { RemoteAgentId = "one" }, new("remote:two", "Same") { RemoteAgentId = "two" } };
        check(DesktopAgentTargets.Selection("Same", null, duplicateLabels) is null
            && DesktopAgentTargets.Selection("Same", "remote:two", duplicateLabels)?.Id == "remote:two"
            && DesktopAgentTargets.Selection("unknown", "remote:two", duplicateLabels) is null,
            "ambiguous or edited labels never silently select a different agent");
        check(DesktopAgentTargets.DisplayName("remote:OpenAIAgent") == "OpenAIAgent"
            && DesktopAgentTargets.DisplayName("local:Agent") == "Agent"
            && DesktopAgentTargets.DisplayName("remote:openai/Agent") == "openai/Agent"
            && DesktopAgentTargets.DisplayName("Agent:With:Colons") == "Agent:With:Colons"
            && DesktopAgentTargets.DisplayName("remote:") == "remote:",
            "legacy display fallback strips exactly one nonempty routing prefix without damaging names");
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
        foreach (var json in new[] {
            """{"name":"Decision","description":"D","instructions":"I","model":{"id":"router/model","type":"decision","providerMetadata":{"keep":7},"providerHeaders":{"x-keep":"yes"},"future":true}}""",
            """{"name":"Decision","description":"D","instructions":"I","models":[{"id":"router/model","type":"decision","providerMetadata":{"keep":7},"providerHeaders":{"x-keep":"yes"},"future":true}]}""" })
        {
            var decisionAgent = DesktopAgent.Parse(json);
            var before = decisionAgent.Definition.DeepClone();
            var hasLegacy = decisionAgent.Definition["model"] is not null;
            decisionAgent.SyncLegacyModel();
            check(decisionAgent.IsValid && decisionAgent.ModelId == "router/model" && JsonNode.DeepEquals(decisionAgent.Definition, before),
                "decision-only primary and legacy synchronization preserve an unchanged document");
            decisionAgent.SelectModels([("router/model", "decision")]);
            check(JsonNode.DeepEquals(decisionAgent.Models[0], (before[hasLegacy ? "model" : "models"] is JsonArray array ? array[0] : before["model"]))
                && (decisionAgent.Definition["model"] is not null) == hasLegacy,
                "decision-only selection retains metadata, headers, unknown fields, and legacy-field presence");
            decisionAgent.SelectModels([("router/model", "decision"), ("openai/first", "language"), ("other/second", "language")]);
            var router = decisionAgent.Models[0].DeepClone();
            decisionAgent.SelectModels([("router/model", "decision"), ("openai/first", "language")]);
            check(decisionAgent.ModelIds.SequenceEqual(["router/model", "openai/first"]) && JsonNode.DeepEquals(decisionAgent.Models[0], router),
                "explicit model changes retain a routing decision when answer count drops");
            decisionAgent.SelectModels([("router/model", "decision")]); decisionAgent.SyncLegacyModel();
            check(decisionAgent.IsValid && JsonNode.DeepEquals(decisionAgent.Models[0], router)
                && (!hasLegacy || JsonNode.DeepEquals(decisionAgent.Definition["model"], router)),
                "returning to a standalone decision preserves its full primary configuration");
            check(JsonNode.DeepEquals(DesktopAgent.Parse(decisionAgent.Definition.ToJsonString()).Definition, decisionAgent.Definition),
                "standalone decision export/import retains its serialization");
        }
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
        using (var call = JsonDocument.Parse("""{"type":"tool-search","toolCallId":"call","input":{"query":"models"},"output":{"items":["test/model"]}}"""))
        using (var tool = JsonDocument.Parse("""{"name":"search","description":"Search models"}"""))
        {
            var explain = DesktopAppAgentClient.ToolExplanationInput(call.RootElement, tool.RootElement, "English");
            check(explain["language"]?.ToString() == "English" && explain["toolcall"]?.ToString().Contains("\"toolcall\"") == true
                && explain["toolcall"]?.ToString().Contains("\"tool\"") == true,
                "tool explanation input matches browser nested string payload");
        }
        await foreach (var _ in client.StreamAsync(ServiceKind.Agents, "backend-id", "conversation", [], CancellationToken.None)) { }
        check(handler.Body.GetProperty("model").GetString() == "backend-id" && (!handler.Body.TryGetProperty("agents", out var agents) || agents.ValueKind == JsonValueKind.Null),
            "backend agent execution retains its existing model-based request");
        await session.DisposeAsync();
    }
    private static void CheckSharedProviders(Action<bool, string> check)
    {
        var agent = DesktopAgent.Parse("""
            {"name":"Shared","description":"D","instructions":"I","models":[
              {"id":"openai/one","type":"language","providerMetadata":{"reasoning":{"effort":"low","opaque":1},"one":true},"providerHeaders":{"X-Shared":"old","X-One":"keep"}},
              {"id":"OPENAI/two","type":"language","providerMetadata":{"reasoning":{"effort":"medium","opaque":2},"two":true},"providerHeaders":{"X-Shared":"different","X-Two":"keep"},"options":{"keep":7}},
              {"id":"anthropic/one","type":"language","providerMetadata":{"thinking":{"type":"adaptive"}},"future":true},
              {"id":"router/one","type":"decision","providerMetadata":{"opaque":7},"providerHeaders":{"X-Router":"keep"}}]}
            """);
        var original = agent.Definition.DeepClone();
        var groups = AgentProviderSettings.Groups(agent);
        check(groups.Select(group => group.Provider).SequenceEqual(["openai", "anthropic", "router"]) && groups[0].Models.Count == 2,
            "agent settings have one ordered group per provider, independent of model count and provider casing");
        var shared = groups[0];
        // A native form may project aliases/defaults differently from the stored document.
        var canonical = OpenAIChatConfig.Canonical(shared.Metadata);
        shared.Baseline(canonical, shared.Headers); shared.Commit(canonical, shared.Headers);
        check(JsonNode.DeepEquals(agent.Definition, original), "opening and committing an unedited shared form preserves differing imported model settings exactly");
        var edited = (JsonObject)canonical.DeepClone(); edited["reasoning"]!["effort"] = "high";
        var headers = (JsonObject)shared.Headers.DeepClone(); headers["X-Shared"] = "new";
        shared.Commit(edited, headers);
        check(agent.Models.Take(2).All(model => DesktopAgent.Text(model["providerMetadata"]?["reasoning"]?["effort"]) == "high"
            && DesktopAgent.Text(model["providerHeaders"]?["X-Shared"]) == "new"), "shared provider edits reach every matching model");
        check(agent.Models[0]["providerMetadata"]?["reasoning"]?["opaque"]?.ToJsonString() == "1"
            && agent.Models[1]["providerMetadata"]?["reasoning"]?["opaque"]?.ToJsonString() == "2"
            && agent.Models[0]["providerMetadata"]?["one"] is not null && agent.Models[1]["providerMetadata"]?["two"] is not null
            && agent.Models[0]["providerHeaders"]?["X-One"] is not null && agent.Models[1]["providerHeaders"]?["X-Two"] is not null
            && agent.Models[1]["options"]?["keep"]?.ToJsonString() == "7", "shared edits preserve each model's unedited metadata, headers and options");
        check(JsonNode.DeepEquals(agent.Models[2], original["models"]?[2]) && JsonNode.DeepEquals(agent.Models[3], original["models"]?[3]),
            "shared edits never affect a different provider or its routing decision");
        var once = agent.Definition.DeepClone(); shared.Commit(edited, headers);
        check(JsonNode.DeepEquals(agent.Definition, once), "repeated provider commits are idempotent");
        headers.Remove("X-Shared"); shared.Commit(edited, headers);
        check(agent.Models.Take(2).All(model => model["providerHeaders"]?["X-Shared"] is null)
            && agent.Models[0]["providerHeaders"]?["X-One"] is not null && agent.Models[1]["providerHeaders"]?["X-Two"] is not null,
            "removing a shared header retains unrelated per-model headers");
        agent.SelectModels(agent.Models.Select(model => (DesktopAgent.Text(model["id"]), (string?)DesktopAgent.Text(model["type"]))).Append(("openai/new", "language")));
        foreach (var group in groups) group.InitializeNewModels(agent.Models);
        check(JsonNode.DeepEquals(agent.Models.Last()["providerMetadata"], agent.Models[0]["providerMetadata"])
            && JsonNode.DeepEquals(agent.Models.Last()["providerHeaders"], agent.Models[0]["providerHeaders"]), "new same-provider models inherit the shared configuration");
        check(JsonNode.DeepEquals(DesktopAgent.Parse(agent.Definition.ToJsonString()).Definition, agent.Definition), "shared provider configuration survives export/import");
        check(AgentProviderSettings.Groups(DesktopAgent.Empty()).Count == 0, "an empty model selection creates no spurious provider tab");
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
