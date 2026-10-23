using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class OverviewConsistencyRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var directory = Path.Combine(root, "overview-consistency"); Directory.CreateDirectory(directory);
        var settings = await SettingsStore.LoadAsync(directory, new());
        check(!settings.Chat.ShowMetrics && !JsonSerializer.Deserialize<ChatPreferences>("{}")!.ShowMetrics, "metrics: new and legacy settings default off");
        settings.Chat.ShowMetrics = true;
        check(settings.Chat.Clone().ShowMetrics && settings.Chat.InferenceDefaults().ShowMetrics && !settings.Chat.ShowTokens,
            "metrics: cloning and inference reset preserve the independent display preference");
        var clone = settings.Clone(); clone.Chat.ShowMetrics = false;
        check(settings.Chat.ShowMetrics, "metrics: editing a cloned draft cannot change live settings");
        await SettingsStore.SaveAsync(directory, settings);
        check((await SettingsStore.LoadAsync(directory, new())).Chat.ShowMetrics, "metrics: enabled setting survives persistence");
        settings.Chat.ShowMetrics = false; await SettingsStore.SaveAsync(directory, settings);
        check(!(await SettingsStore.LoadAsync(directory, new())).Chat.ShowMetrics, "metrics: explicitly disabled setting survives persistence");
        check(!settings.Chat.RequestBody("custom/model", "fixture", [], "custom").ContainsKey("showMetrics"), "metrics: display preference never changes inference requests");
        check(NavigationPreferences.AgentKeys.SequenceEqual(["agents", "plugins", "servers", "skills"]), "overview: browser-aligned stable navigation keys");
        var nav = new NavigationPreferences { HiddenItemKeys = ["plugins", "servers"] };
        check(nav.Clone().HiddenItemKeys.SequenceEqual(["plugins", "servers"]), "overview: navigation visibility choices survive reorder and cloning");
        var skill = new CatalogItem(CatalogKind.Skill, "openai/skill", "Skill", "Description");
        check(ProviderCatalog.SkillProvider(skill)?.Id == "openai" && ProviderCatalog.SkillProvider(skill with { Id = "/OpenAI/skill" })?.Id == "openai",
            "skills: remote skill provider mapping matches browser normalization");
        check(ProviderCatalog.SkillProvider(skill with { Origin = CatalogOrigin.Local }) is null
            && ProviderCatalog.SkillProvider(skill with { Id = "unknown/skill" }) is null
            && ProviderCatalog.SkillProvider(skill with { Id = "skill" }) is null
            && ProviderCatalog.SkillProvider(new(CatalogKind.Agent, "openai/agent", "Agent", "")) is null,
            "skills: local, unknown and providerless skills do not get misleading provider logos");
        Dictionary<string, object> Metadata(string json) => JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var language in new[] { "en-US", "nl-NL" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var expected = language == "en-US" ? ("1.95 s", "4,558 tok/s") : ("1,95 s", "4.558 tok/s");
                var data = Metadata("""{"providerMetadata":{"gateway":{"ttft":1.9497438,"tps":4557.565558827654,"cost":0.01}},"gateway":{"ttft":99,"tps":99}}""");
                var before = JsonSerializer.Serialize(data);
                check(MessageUsage.ReadPerformance(data) == expected, "metrics: nested precedence and culture-aware browser rounding / " + language);
                check(JsonSerializer.Serialize(data) == before && MessageUsage.ReadPerformance(Metadata(before)) == expected,
                    "metrics: source metadata is unchanged and restored values match / " + language);
            }
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            foreach (var json in new[] { """{"gateway":{"ttft":0,"tps":0}}""", """{"providerMetadata":{"gateway":{"ttft":0,"tps":0}}}""" })
                check(MessageUsage.ReadPerformance(Metadata(json)) == ("0 s", "0 tok/s"), "metrics: valid zero values and legacy metadata work");
            check(MessageUsage.ReadPerformance(Metadata("""{"providerMetadata":{"gateway":{"ttft":null}},"gateway":{"ttft":1.5,"tps":42}}""")) == ("1.5 s", "42 tok/s"),
                "metrics: null/missing nested fields fall back independently to legacy gateway metadata");
            foreach (var invalid in new[] { "null", "true", "\"1.95\"", "{}", "[]", "-1", "1e999" })
                check(MessageUsage.ReadPerformance(Metadata("{\"providerMetadata\":{\"gateway\":{\"ttft\":" + invalid + ",\"tps\":42}}}")) == (null, "42 tok/s"),
                    "metrics: invalid optional TTFT does not hide valid throughput / " + invalid);
            check(MessageUsage.ReadPerformance(Metadata("""{"providerMetadata":{"gateway":{"ttft":"invalid"}},"gateway":{"ttft":12}}""")) == (null, null),
                "metrics: malformed non-null nested values are not replaced with unrelated legacy values");
            foreach (var invalid in new object?[] { null, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, "1", true, new { }, Array.Empty<int>() })
                check(MessageUsage.FormatMetric(invalid, true) is null, "metrics: native malformed and non-finite values are rejected");
            var nodes = new Dictionary<string, object> { ["providerMetadata"] = new JsonObject { ["gateway"] = new JsonObject { ["ttft"] = 1.5, ["tps"] = 42 } } };
            check(MessageUsage.ReadPerformance(nodes) == ("1.5 s", "42 tok/s"), "metrics: live JSON-node metadata is supported");
            check(MessageUsage.ReadPerformance(null) == (null, null) && MessageUsage.ReadPerformance(Metadata("""{"providerMetadata":[],"gateway":"invalid"}""")) == (null, null),
                "metrics: absent or malformed containers are safely ignored");
            var output = new ConversationMessage { Message = new() { Id = "metrics-stream", Role = Role.assistant }, Status = "streaming" };
            var conversation = new Conversation { Service = ServiceKind.Ai, Messages = [output] };
            var events = new[] {
                """{"type":"start","messageMetadata":{"model":"openai/fixture","providerMetadata":{"openai":{"keep":true},"gateway":{"tps":42}}}}""",
                """{"type":"text-start","id":"answer"}""",
                """{"type":"text-delta","id":"answer","delta":"Answer"}""",
                """{"type":"finish","messageMetadata":{"usage":{"totalTokens":17},"providerMetadata":{"gateway":{"ttft":1.4,"cost":0.001}}}}""",
                """{"type":"message-metadata","messageMetadata":{"providerMetadata":{"gateway":{"tps":123.4}}}}""",
                """{"type":"text-delta","id":"answer","delta":"Must not be appended after finish"}"""
            };
            var saves = new List<(string? Ttft, string? Tps)>();
            async IAsyncEnumerable<StreamEvent> Stream(List<UIMessage> _, CancellationToken token)
            {
                foreach (var item in events) { token.ThrowIfCancellationRequested(); yield return StreamEvent.Parse(item); await Task.Yield(); }
            }
            await DesktopChatTurn.RunAsync(conversation, output, Stream, (_, _) => throw new Exception("Unexpected approval"),
                (finished, _) => { if (finished) saves.Add(MessageUsage.ReadPerformance(output.Message.Metadata)); return Task.CompletedTask; }, McpTurnSnapshot.Empty, "en", default);
            check(saves.First() == ("1.4 s", "42 tok/s"), "metrics: finish metadata enrichment preserves previously received throughput");
            check(MessageUsage.ReadPerformance(output.Message.Metadata) == ("1.4 s", "123 tok/s") && saves.Last() == ("1.4 s", "123 tok/s"),
                "metrics: metadata arriving after finish is consumed and reaches the saved/rendered message");
            check(output.Text == "Answer" && output.Status == "complete", "metrics: draining metadata tails does not append late content or change completion status");
            var restored = PortableConversations.Read(PortableConversations.Write(conversation));
            check(MessageUsage.ReadPerformance(restored.Messages.Single().Message.Metadata) == ("1.4 s", "123 tok/s"),
                "metrics: both streamed performance values survive portable conversation reload");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
