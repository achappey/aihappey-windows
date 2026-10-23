using System.Globalization;
using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class UsageRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        using var stream = typeof(UsageRegressionTests).Assembly.GetManifestResourceStream("Desktop.UsageFixture")!;
        var conversations = (await JsonSerializer.DeserializeAsync<Conversation[]>(stream, PortableConversations.Json))!;
        var before = conversations.Select(PortableConversations.Write).ToArray();
        var zone = TimeZoneInfo.CreateCustomTimeZone("Usage test +02", TimeSpan.FromHours(2), "Test +02", "Test +02");
        var a = UsageAnalytics.Build(conversations, new Dictionary<string, string> { ["openai"] = "OpenAI", ["custom"] = "Custom provider" }, zone);
        check(a.Conversations == 2 && a.Messages == 5 && a.Parts == 14 && a.TextChars == 24, "usage: portable conversation, message, part and UTF-16 text counts");
        check(a.InputTokens == 20 && a.OutputTokens == 17 && a.TotalTokens == 34 && a.MessagesWithUsage == 3, "usage: merged token aliases and explicit zero/total precedence");
        check(a.TotalCost == .375 && a.MessagesWithCost == 2, "usage: recorded cost precedence without price estimation");
        check(a.ModelStats.Count == 2 && a.ModelStats["openai/gpt-test"].Count == 1 && a.ModelStats["bare-model"].ProviderKey == "unknown", "usage: normalized provider prefix and bare model identities");
        check(a.AgentStats.Count == 1 && a.AgentStats["ResearchAgent"].Tokens == 10 && a.AgentStats["ResearchAgent"].ModelId == "org/model", "usage: author identifies agent; explicit provider retains organization/model ID");
        check(a.ProviderStats["openai"].Label == "OpenAI" && a.ProviderStats["custom"].Models.SetEquals(["custom/org/model"]), "usage: offline provider labels and distinct models");
        check(a.DailyMessages["2026-10-20"] == 2 && a.DailyMessages["2026-10-22"] == 1 && a.HourCounts["01"] == 1 && a.HourCounts["00"] == 1, "usage: UTC days and local hours; invalid/missing dates excluded");
        check(a.MessageFacts[3].Timestamp is null && a.MessageFacts[4].Timestamp is null, "usage: no invented timestamps from desktop wrapper defaults");
        check(a.ToolStats["search"].Count == 3 && a.ToolStats["search"].Success == 2 && a.ToolStats["search"].Error == 1 && a.ToolStats["pending"].Pending == 1 && a.MessagesWithTools == 2, "usage: web tool scope, state and null output semantics");
        check(a.ToolResultTypeCounts["structuredContent"] == 1 && a.ToolResultTypeCounts["none"] == 2 && a.ToolResultTypeCounts["object"] == 1, "usage: structured/MCP/absent/null result types");
        check(a.ContentTypeCounts["dynamic-tool"] == 1 && a.AttachmentTypeCounts["image"] == 1 && a.AttachmentTypeCounts["pdf"] == 1 && a.SourceTypeCounts.Count == 2, "usage: content and attachment/source classification");
        check(a.MessagesWithReasoning == 1 && a.MessagesWithSources == 1 && a.MessagesWithDataParts == 1 && a.MessagesWithStructuredOutput == 1, "usage: rich content message flags");
        check(a.TemperatureCounts["0.7"] == 1 && a.FinishReasonCounts["length"] == 1 && a.ErrorCounts["Recorded error"] == 1, "usage: patterns, finish reason and recorded errors");
        var ttft = UsageAnalytics.Summarize(a.MessageFacts.Select(f => f.Ttft));
        check(ttft is { Count: 2, Mean: 1, Median: 1, P95: 2 } && a.MessageFacts[3].Ttft is null && a.MessageFacts[3].Tps is null, "usage: valid zero metrics kept; numeric strings and negative metrics excluded");
        check(UsageAnalytics.Summarize([]) is null && UsageAnalytics.Summarize([null, double.NaN, -1]) is null && UsageAnalytics.Summarize([1, 2, 3])?.Median == 2
            && UsageAnalytics.Summarize(Enumerable.Range(1, 20).Select(n => (double?)n))?.P95 == 19, "usage: missing, odd/even median and nearest-rank P95");
        check(conversations.Select(PortableConversations.Write).SequenceEqual(before), "usage: aggregation never mutates portable messages or metadata");
        var empty = UsageAnalytics.Build([]);
        check(empty.Messages == 0 && empty.MessageFacts.Count == 0 && empty.TotalCost == 0, "usage: empty history");
        var fallback = PortableConversations.Read("""{"id":"nested","metadata":{"model":"fallback"},"messages":[{"id":"m","role":"assistant","parts":[],"metadata":{"providerMetadata":{"openai":{}},"deep":{"model":"ignored"}}}]}""");
        check(UsageAnalytics.Build([fallback], new Dictionary<string, string> { ["openai"] = "OpenAI" }).ModelStats.ContainsKey("openai/fallback"), "usage: conversation model fallback and known provider metadata");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { UsageAnalytics.Build(conversations, ct: canceled.Token); check(false, "usage cancellation"); }
        catch (OperationCanceledException) { check(true, "usage: cancellation observed"); }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "en-US", "nl-NL" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                check(UsageFormatting.Cost(.125) == .125.ToString("0.####", CultureInfo.CurrentCulture) && UsageFormatting.Metric(null) == "—"
                    && !UsageFormatting.Cost(10).Any(c => c is '$' or '€'), "usage: localized currency-free cost and absent metrics / " + name);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var store = new HistoryStore(Path.Combine(root, "usage")); var partition = HistoryStore.Partition("usage-one"); var other = HistoryStore.Partition("usage-two");
        await store.SaveAsync(partition, conversations[0]); await store.SaveAsync(other, conversations[1]);
        var path = Directory.GetFiles(Path.Combine(root, "usage", partition)).Single(); var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(Path.Combine(root, "usage", partition, new string('A', 64) + ".json"), "broken document");
        check(UsageAnalytics.Build(await store.ListAsync(partition)).Conversations == 1 && UsageAnalytics.Build(await store.ListAsync(other)).Messages == 2, "usage: partition isolation and invalid-document isolation");
        var after = await File.ReadAllBytesAsync(path);
        check(bytes.SequenceEqual(after), "usage: loading and aggregating history does not write stored documents");
    }
}
