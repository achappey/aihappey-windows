using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class OpenAIChatSettingsRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        const string flag = "responses_multi_agent=v1";
        var initial = JsonNode.Parse("""{"inputImageDetail":"high","service_tier":"flex","future":{"keep":true},"multi_agent":{"enabled":true,"max_concurrent_subagents":3}}""")!.AsObject();
        var original = initial.ToJsonString();
        var headers = new Dictionary<string, string> { ["oPeNaI-bEtA"] = "custom=v1,custom=v1", ["openai-organization"] = "fixture" };
        var model = new OpenAIChatConfig(initial, headers);
        check(model.Headers["OpenAI-Beta"] == "custom=v1," + flag && headers["oPeNaI-bEtA"] == "custom=v1,custom=v1",
            "OpenAI settings: loading an enabled standalone card repairs missing beta without mutating saved headers");
        for (var i = 0; i < 2; i++) model.Toggle("multi_agent", true);
        check(model.Headers["OpenAI-Beta"] == "custom=v1," + flag, "OpenAI settings: repeated enabling never duplicates beta flags");
        model.Toggle("multi_agent", false);
        check(model.Get("multi_agent") is null && model.Headers["OpenAI-Beta"] == "custom=v1" && model.Headers["openai-organization"] == "fixture",
            "OpenAI settings: disabling removes only the owned beta flag and preserves unrelated headers");
        var canonical = OpenAIChatConfig.Canonical(model.Root);
        check(!canonical.ContainsKey("inputImageDetail") && canonical["service_tier"]!.GetValue<string>() == "flex"
            && canonical["future"]!["keep"]!.GetValue<bool>() && initial.ToJsonString() == original,
            "OpenAI settings: obsolete image detail is omitted without mutating input or unrelated provider options");

        foreach (var (json, enabled) in new (string, bool)[] {
            ("{}", false), ("""{"multi_agent":null}""", false), ("""{"multi_agent":{"enabled":false}}""", false),
            ("""{"multi_agent":{}}""", false), ("""{"multi_agent":{"enabled":true}}""", true),
            ("""{"agent":{"multi_agent":{"enabled":true}}}""", false),
            ("""{"multi_agent":{"enabled":true},"agent":{"multi_agent":{"enabled":false}}}""", true)
        })
        {
            var preferences = new ChatPreferences(); preferences.ProviderMetadata["openai"] = JsonNode.Parse(json)!.AsObject();
            preferences.ProviderHeaders["openai"] = new() { ["openai-beta"] = flag + ",custom=v1," + flag };
            var before = JsonSerializer.Serialize(preferences);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/chat");
            preferences.ApplyHeaders(request, "openai");
            var flags = OpenAIChatConfig.Split(request.Headers.GetValues("OpenAI-Beta").Single());
            check(flags.Contains(flag) == enabled && flags.Count(value => value == flag) == (enabled ? 1 : 0) && flags.Contains("custom=v1"),
                "OpenAI settings: request headers follow only standalone enabled state / " + json);
            check(JsonSerializer.Serialize(preferences) == before, "OpenAI settings: preparing headers never mutates live preferences");
            using var other = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/chat");
            preferences.ApplyHeaders(other, "anthropic");
            check(!other.Headers.Contains("OpenAI-Beta"), "OpenAI settings: automatic beta never leaks to another provider");
        }
        var enabledPreferences = new ChatPreferences(); enabledPreferences.ProviderMetadata["openai"] = initial;
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/chat"))
        {
            enabledPreferences.ApplyHeaders(request, "openai");
            check(request.Headers.GetValues("OpenAI-Beta").Single() == flag, "OpenAI settings: enabled legacy config needs no preexisting provider-header dictionary");
        }
        foreach (var enabled in new[] { true, false })
        {
            var preferences = new ChatPreferences();
            preferences.ProviderMetadata["openai"] = enabled ? initial : new JsonObject();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/chat");
            request.Headers.Add("OpenAI-Beta", "host=v1," + flag);
            preferences.ApplyHeaders(request, "openai");
            check(request.Headers.GetValues("OpenAI-Beta").Single() == (enabled ? "host=v1," + flag : "host=v1"),
                "OpenAI settings: host beta flags survive while standalone state owns the multi-agent flag / " + enabled);
        }
        var unsafeHeaders = OpenAIChatConfig.HeadersFor(initial, new() { ["OpenAI-Beta"] = "unsafe\r\nvalue" });
        check(unsafeHeaders["OpenAI-Beta"] == flag, "OpenAI settings: beta normalization cannot launder control characters into provider flags");
        var loneBeta = new OpenAIChatConfig(initial); loneBeta.Toggle("multi_agent", false);
        check(!loneBeta.Headers.ContainsKey("OpenAI-Beta"), "OpenAI settings: disabling the last beta removes the header entirely");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/chat"))
        {
            request.Headers.Add("OpenAI-Beta", flag); new ChatPreferences().ApplyHeaders(request, "openai");
            check(!request.Headers.Contains("OpenAI-Beta"), "OpenAI settings: disabled standalone state removes a stale host-only beta header");
        }

        var directory = Path.Combine(root, "openai-multi-agent-settings");
        foreach (var enabled in new[] { true, false })
        {
            model.Toggle("multi_agent", enabled);
            var preferences = new ChatPreferences();
            preferences.ProviderMetadata["openai"] = OpenAIChatConfig.Canonical(model.Root);
            preferences.ProviderHeaders["openai"] = OpenAIChatConfig.HeadersFor(model.Root, model.Headers);
            await SettingsStore.SaveAsync(directory, new() { Chat = preferences });
            var loaded = (await SettingsStore.LoadAsync(directory, new())).Chat;
            var reopened = new OpenAIChatConfig(loaded.ProviderMetadata["openai"], loaded.ProviderHeaders.GetValueOrDefault("openai"));
            check(reopened.Boolean("multi_agent/enabled") == enabled && reopened.Headers["OpenAI-Beta"].Contains(flag) == enabled,
                "OpenAI settings: standalone config and beta survive save/reload / " + enabled);
            var body = loaded.RequestBody("openai/fixture", "fixture", [], "openai");
            check(!body["providerMetadata"]!["openai"]!.AsObject().ContainsKey("inputImageDetail") && !body.ContainsKey("providerHeaders")
                && !body.ToJsonString().Contains("OpenAI-Beta"), "OpenAI settings: obsolete option and HTTP headers stay outside the request JSON");
        }
        var reset = enabledPreferences.InferenceDefaults();
        using var resetRequest = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/chat"); reset.ApplyHeaders(resetRequest, "openai");
        check(reset.ProviderHeaders.Count == 0 && !resetRequest.Headers.Contains("OpenAI-Beta"), "OpenAI settings: restore defaults clears standalone multi-agent and its beta");
    }
}
