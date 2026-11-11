using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class ChatProviderFormRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        using var stream = typeof(ChatProviderFormRegressionTests).Assembly.GetManifestResourceStream("Desktop.ChatProviderFixture")!;
        var fixture = JsonNode.Parse(stream)!.AsObject();
        void Same(JsonNode? actual, JsonNode? expected, string name) => check(JsonNode.DeepEquals(actual, expected), "provider forms: " + name
            + (JsonNode.DeepEquals(actual, expected) ? "" : $"\nExpected: {expected}\nActual: {actual}"));
        Same(AnthropicChatConfig.Defaults(), fixture["defaults"], "Anthropic defaults match current chat app");
        foreach (var (name, tool) in fixture["tools"]!.AsObject())
        {
            Same(AnthropicChatConfig.SectionDefault(name), tool!["defaults"], name + " exact defaults");
            Same(ProviderChatConfig.Strings(AnthropicChatConfig.Versions(name)), tool["versions"], name + " exact versions");
        }
        Same(ProviderChatConfig.Strings(AnthropicChatConfig.Betas), fixture["betas"], "all current beta choices");
        foreach (var entry in fixture["contextDefaults"]!.AsArray().OfType<JsonObject>())
            Same(AnthropicChatConfig.ContextDefault(ProviderChatConfig.Text(entry["type"])!), entry, "context edit default " + entry["type"]);
        foreach (var scenario in fixture["anthropic"]!.AsArray().OfType<JsonObject>())
        {
            var initial = scenario["initial"]!.AsObject(); var before = initial.ToJsonString();
            var headers = scenario["initialHeaders"]!.Deserialize<Dictionary<string, string>>()!;
            var model = new AnthropicChatConfig(initial, headers);
            var ops = scenario["ops"]!.AsArray(); var expected = scenario["expected"]!.AsArray();
            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i]!;
                switch (ProviderChatConfig.Text(op["kind"]))
                {
                    case "toggle": model.Toggle(ProviderChatConfig.Text(op["section"])!, op["enabled"]!.GetValue<bool>()); break;
                    case "beta": model.Beta(ProviderChatConfig.Text(op["flag"])!, op["enabled"]!.GetValue<bool>()); break;
                    case "context": model.ContextEntries(op["entries"]!.AsArray()); break;
                    default: model.Set(ProviderChatConfig.Text(op["path"])!, op["value"], op["remove"]?.GetValue<bool>() == true); break;
                }
                Same(model.Commit(), expected[i]!["config"], $"browser parity {scenario["name"]} operation {i}");
                Same(JsonSerializer.SerializeToNode(model.Headers), expected[i]!["headers"], $"browser header parity {scenario["name"]} operation {i}");
            }
            check(initial.ToJsonString() == before, "provider forms: editing never mutates original Anthropic JSON");
        }
        foreach (var scenario in fixture["deepseek"]!.AsArray().OfType<JsonObject>())
        {
            var initial = scenario["initial"]!.AsObject();
            Same(DeepSeekChatConfig.Normalize(initial), scenario["normalized"], "DeepSeek legacy reasoning normalization");
            foreach (var enabled in new[] { true, false })
            {
                var draft = new DeepSeekChatConfig(initial); draft.WebSearch(enabled);
                Same(draft.Root, scenario[enabled ? "searchOn" : "searchOff"], "DeepSeek web-search keeps unrelated tools and explicit empty arrays");
            }
        }
        var deep = new DeepSeekChatConfig(new());
        check(deep.ReasoningOn && deep.Effort == "high" && deep.Root.Count == 0, "provider forms: implicit DeepSeek reasoning does not insert defaults");
        deep.ToggleReasoning(false); check(deep.String("reasoning/effort") == "none", "provider forms: DeepSeek disabled reasoning is explicit none");
        deep.Set("reasoning/future", JsonValue.Create(true)); deep.ToggleReasoning(true);
        check(deep.String("reasoning/effort") == "high" && deep.Boolean("reasoning/future"), "provider forms: re-enabling DeepSeek uses high and preserves nested unknowns");
        foreach (var entry in fixture["money"]!.AsArray())
        {
            var usd = entry!["usd"]!.GetValue<string>(); var expected = ProviderChatConfig.Text(entry["cents"]);
            var valid = AnthropicChatConfig.TryUsdCents(usd, out var cents);
            check(valid == (expected is not null) && cents == expected, "provider forms: exact browser money conversion " + usd);
            if (valid) check(AnthropicChatConfig.TryUsdCents(AnthropicChatConfig.FormatUsd(cents), out var roundTrip) && roundTrip == cents, "provider forms: money round-trip has no floating point loss");
        }
        check(!ProviderChatConfig.TryNumber("NaN", out _) && !ProviderChatConfig.TryNumber("Infinity", out _)
            && ProviderChatConfig.TryNumber("1.5", out var number) && number == 1.5, "provider forms: finite native numeric parsing");
        var config = new AnthropicChatConfig(AnthropicChatConfig.Defaults());
        config.ThinkingMode("enabled"); config.PrefixMismatch("error"); config.ThinkingMode("adaptive");
        check(config.String("thinking/block_binding/prefix_mismatch_behavior") == "error" && config.Get("thinking/budget_tokens") is null, "provider forms: adaptive mode preserves binding and drops enabled-only budget");
        config.PrefixMismatch(""); check(config.Get("thinking/block_binding") is null, "provider forms: empty binding object is omitted");
        config.ThinkingMode("disabled"); Same(config.Get("thinking"), JsonNode.Parse("""{"type":"disabled"}"""), "disabled thinking mode differs from disabling the card");
        config.Version("web_fetch", "web_fetch_20250910"); config.Set("web_fetch/use_cache", JsonValue.Create(false));
        check(config.Get("web_fetch/use_cache") is null, "provider forms: old fetch versions omit unsupported cache option");
        config.Toggle("text_editor", true); config.Set("text_editor/max_characters", JsonValue.Create(1000)); config.Version("text_editor", "text_editor_20250124");
        check(config.String("text_editor/name") == "str_replace_editor" && config.Get("text_editor/max_characters") is null, "provider forms: version switches update native text-editor name and omit unsupported fields");
        var container = new AnthropicChatConfig(new()); container.Set("container", JsonValue.Create(" id "));
        check(container.ContainerId == "id", "provider forms: legacy container strings become native container objects");
        container.SetContainerSkills([" custom ", "custom", ""], ["pdf", "docx"]);
        check(container.ContainerSkills("custom").SequenceEqual(["custom"]) && container.ContainerSkills("anthropic").SequenceEqual(["docx", "pdf"]), "provider forms: container skill identity and built-in order match browser");
        var editor = AnthropicChatConfig.ContextDefault("clear_tool_uses_20250919");
        check(AnthropicChatConfig.ContextValid(editor), "provider forms: context defaults valid");
        editor["trigger"]!["value"] = null; check(!AnthropicChatConfig.ContextValid(editor), "provider forms: selected context quantity requires finite value");
        var manual = new AnthropicChatConfig(new()); manual.Toggle("advisor", true); manual.Beta("custom-beta", true); manual.Toggle("advisor", false);
        check(manual.BetaValues.Contains(AnthropicChatConfig.AdvisorBeta) && manual.BetaValues.Contains("custom-beta"), "provider forms: explicit beta editing relinquishes automatic ownership");

        var preferences = new ChatPreferences(); var openAI = preferences.ProviderMetadata["openai"].DeepClone();
        preferences.ProviderMetadata["anthropic"] = config.Commit(); preferences.ProviderMetadata["deepseek"] = deep.Root;
        preferences.ProviderHeaders["anthropic"] = new() { ["AnThRoPiC-BeTa"] = "custom-beta", ["anthropic-version"] = "2023-06-01", ["Authorization"] = "must-not-leak", ["X-Anthropic-Key"] = "must-not-leak", ["Content-Type"] = "must-not-leak", ["anthropic-bad"] = "bad\r\nvalue" };
        preferences.ProviderHeaders["deepseek"] = new() { ["deepseek-version"] = "future", ["X-DeepSeek-Key"] = "must-not-leak" };
        var clone = preferences.Clone(); clone.ProviderMetadata["anthropic"]["future"] = true; clone.ProviderHeaders["anthropic"]["anthropic-version"] = "changed";
        check(!preferences.ProviderMetadata["anthropic"].ContainsKey("future") && preferences.ProviderHeaders["anthropic"]["anthropic-version"] == "2023-06-01", "provider forms: metadata and header drafts are deeply isolated");
        foreach (var provider in new[] { "anthropic", "deepseek" })
        {
            var body = preferences.RequestBody(provider + "/model", "fixture", [], provider);
            Same(body["providerMetadata"]![provider], preferences.ProviderMetadata[provider], "raw native request object for " + provider);
            check(body["providerMetadata"]!.AsObject().Count == 1 && !body.ContainsKey("providerHeaders"), "provider forms: only selected provider is sent; headers remain outside JSON");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/chat"); preferences.ApplyHeaders(request, provider);
            check(!request.Headers.Contains("Authorization") && !request.Headers.Contains("X-Anthropic-Key") && !request.Headers.Contains("X-DeepSeek-Key")
                && !request.Headers.Contains("Content-Type") && !request.Headers.Contains("anthropic-bad"), "provider forms: credential/transport/control-character headers blocked");
            check(provider == "anthropic" ? request.Headers.Contains("anthropic-beta") && !request.Headers.Contains("deepseek-version")
                : request.Headers.Contains("deepseek-version") && !request.Headers.Contains("anthropic-beta"), "provider forms: safe headers are case-insensitive and provider isolated");
        }
        Same(preferences.ProviderMetadata["openai"], openAI, "new provider edits leave OpenAI unchanged");
        var directory = Path.Combine(root, "native-provider-forms"); await SettingsStore.SaveAsync(directory, new() { Chat = preferences });
        var reloaded = await SettingsStore.LoadAsync(directory, new());
        Same(JsonSerializer.SerializeToNode(reloaded.Chat.ProviderMetadata), JsonSerializer.SerializeToNode(preferences.ProviderMetadata), "all native configs survive persistence");
        Same(JsonSerializer.SerializeToNode(reloaded.Chat.ProviderHeaders), JsonSerializer.SerializeToNode(preferences.ProviderHeaders), "all provider headers survive persistence");
        Same(preferences.InferenceDefaults().ProviderMetadata["anthropic"], AnthropicChatConfig.Defaults(), "inference reset restores Anthropic chat defaults");
        check(preferences.InferenceDefaults().ProviderHeaders.Count == 1
            && preferences.InferenceDefaults().ProviderHeaders["anthropic"][AnthropicChatConfig.VersionHeader] == AnthropicChatConfig.LatestApiVersion
            && !preferences.InferenceDefaults().ProviderMetadata.ContainsKey("deepseek"), "provider forms: explicit reset adopts latest Anthropic version, clears custom headers, and keeps DeepSeek implicit defaults");
        await AnthropicVersions(check, root);
        await OpenAIChatSettingsRegressionTests.RunAsync(check, root);
    }

    private static async Task AnthropicVersions(Action<bool, string> check, string root)
    {
        var directory = Path.Combine(root, "anthropic-versions");
        var fresh = await SettingsStore.LoadAsync(directory, new());
        check(fresh.Chat.ProviderHeaders["anthropic"][AnthropicChatConfig.VersionHeader] == "2026-10-21", "Anthropic version: first run opts into 2026");
        Directory.CreateDirectory(directory);
        foreach (var json in new[] { "{}", """{"chat":{}}""", """{"chat":{"providerHeaders":{"anthropic":{"anthropic-beta":"custom"}}}}""" })
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), json);
            var saved = await SettingsStore.LoadAsync(directory, new());
            var legacy = new AnthropicChatConfig(new(), saved.Chat.ProviderHeaders.GetValueOrDefault("anthropic"));
            check(legacy.ApiVersion == "2023-06-01" && !legacy.Headers.ContainsKey(AnthropicChatConfig.VersionHeader), "Anthropic version: existing files keep historical fallback without inserting defaults");
        }
        foreach (var version in new[] { "2023-06-01", "2026-10-21", "future-version" })
        {
            Dictionary<string, string> headers = new() { ["AnThRoPiC-VeRsIoN"] = version, ["anthropic-beta"] = "custom" };
            var model = new AnthropicChatConfig(new() { ["max_tokens"] = 100 }, headers);
            check(model.ApiVersion == version && model.Headers.Count == 2, "Anthropic version: saved selection is case-insensitive and opening does not mutate it");
            model.SetApiVersion("2026-10-21");
            check(model.Headers.Count == 2 && headers["AnThRoPiC-VeRsIoN"] == version && model.Headers["anthropic-beta"] == "custom", "Anthropic version: draft changes are isolated and preserve beta");
            model.SetApiVersion(version);
            var body = model.Commit();
            check(body.Count == 1 && !body.ContainsKey(AnthropicChatConfig.VersionHeader), "Anthropic version: header never enters native JSON");
            var preferences = new ChatPreferences { ProviderHeaders = new() { ["anthropic"] = model.Headers } };
            using var request = new HttpRequestMessage(HttpMethod.Post, DesktopChatRoutes.DirectUrl("anthropic", "/v1/messages"));
            preferences.ApplyHeaders(request, "anthropic");
            DesktopDirectChatAuthentication.Apply(request, new Dictionary<string, string> { ["X-Anthropic-Key"] = "secret" }, "anthropic", "/v1/messages");
            check(request.Headers.GetValues(AnthropicChatConfig.VersionHeader).Single() == version, "Anthropic version: direct auth respects selected header");
            await SettingsStore.SaveAsync(directory, new() { Chat = preferences });
            var reloaded = await SettingsStore.LoadAsync(directory, new());
            check(new AnthropicChatConfig(new(), reloaded.Chat.ProviderHeaders["anthropic"]).ApiVersion == version, "Anthropic version: explicit choice survives persistence");
        }
    }
}
