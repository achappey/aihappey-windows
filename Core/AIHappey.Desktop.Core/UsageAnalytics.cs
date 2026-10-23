using System.Globalization;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

public sealed class UsageEntityStat(string key, string label, string providerKey, string providerLabel, string modelId)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string ProviderKey { get; } = providerKey;
    public string ProviderLabel { get; } = providerLabel;
    public string ModelId { get; } = modelId;
    public int Count { get; set; }
    public double Tokens { get; set; }
    public double Cost { get; set; }
}

public sealed class UsageProviderStat(string key, string label)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public int Count { get; set; }
    public HashSet<string> Models { get; } = new(StringComparer.Ordinal);
    public double Tokens { get; set; }
    public double Cost { get; set; }
}

public sealed class UsageToolStat(string name)
{
    public string Label { get; } = name;
    public int Count { get; set; }
    public int Success { get; set; }
    public int Error { get; set; }
    public int Pending { get; set; }
    public Dictionary<string, double> ResultTypes { get; } = new(StringComparer.Ordinal);
}

public sealed record UsageMessageFact(string ConversationId, string ConversationName, string MessageId, string Role,
    DateTimeOffset? Timestamp, string? Day, int? Hour, string? ModelKey, string? ModelLabel,
    string ProviderKey, string ProviderLabel, double Tokens, double InputTokens, double OutputTokens, double Cost,
    int TextChars, int PartCount, int ToolCount, string? EntityKind, string? EntityKey, string? EntityLabel,
    double? Ttft, double? Tps);

public sealed class UsageConversationFact(string id, string name, int messageCount)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public int MessageCount { get; } = messageCount;
    public int PartCount { get; set; }
    public long TextChars { get; set; }
    public double Tokens { get; set; }
    public double Cost { get; set; }
    public DateTimeOffset? FirstTimestamp { get; set; }
    public DateTimeOffset? LastTimestamp { get; set; }
}

/// <summary>Read-only projection after construction. No WinUI, network access, inference or history writes.
/// Rules mirror chat/features/usage/usageAnalytics.ts, including UTC days and local hour buckets.</summary>
public sealed class UsageAnalytics
{
    public int Conversations { get; private set; }
    public int Messages { get; private set; }
    public int Parts { get; private set; }
    public long TextChars { get; private set; }
    public double InputTokens { get; private set; }
    public double OutputTokens { get; private set; }
    public double TotalTokens { get; private set; }
    public double TotalCost { get; private set; }
    public int MessagesWithUsage { get; private set; }
    public int MessagesWithModel { get; private set; }
    public int MessagesWithCost { get; private set; }
    public int MessagesWithTools { get; private set; }
    public int MessagesWithSources { get; private set; }
    public int MessagesWithReasoning { get; private set; }
    public int MessagesWithDataParts { get; private set; }
    public int MessagesWithStructuredOutput { get; private set; }
    public Dictionary<string, UsageEntityStat> ModelStats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, UsageEntityStat> AgentStats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, UsageProviderStat> ProviderStats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, UsageToolStat> ToolStats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> RoleCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> ToolStateCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> ToolResultTypeCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> ContentTypeCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> AttachmentTypeCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> SourceTypeCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> DailyMessages { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> DailyTokens { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> DailyCost { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> HourCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> WeekdayHourCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> TemperatureCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> FinishReasonCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> ErrorCounts { get; } = new(StringComparer.Ordinal);
    public List<UsageMessageFact> MessageFacts { get; } = [];
    public List<UsageConversationFact> ConversationFacts { get; } = [];

    public static UsageAnalytics Build(IEnumerable<Conversation> conversations,
        IReadOnlyDictionary<string, string>? providers = null, TimeZoneInfo? timeZone = null, CancellationToken ct = default)
    {
        providers ??= new Dictionary<string, string>(); timeZone ??= TimeZoneInfo.Local;
        var a = new UsageAnalytics();
        foreach (var conversation in conversations)
        {
            ct.ThrowIfCancellationRequested(); a.Conversations++;
            var cm = JsonSerializer.SerializeToElement(conversation.Metadata, PortableConversations.Json);
            var name = String(Get(cm, "name")) ?? conversation.Title;
            var cf = new UsageConversationFact(conversation.Id, name, conversation.Messages.Count);
            foreach (var wrapper in conversation.Messages)
            {
                ct.ThrowIfCancellationRequested();
                var message = wrapper.Message;
                var m = JsonSerializer.SerializeToElement(message.Metadata, PortableConversations.Json);
                var role = message.Role.ToString(); var assistant = message.Role == Role.assistant;
                // Deliberately use portable metadata, not the wrapper's inferred timestamp or target.
                var dateValue = FirstDefined(Get(m, "timestamp"), Get(m, "createdAt"), Get(m, "updatedAt"));
                DateTimeOffset? timestamp = DateTimeOffset.TryParse(String(dateValue), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
                var day = timestamp?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var local = timestamp is { } stamp ? TimeZoneInfo.ConvertTime(stamp, timeZone) : (DateTimeOffset?)null;
                var rawModel = assistant ? FirstString(m, "model", "modelId", "selectedModel", "gateway.model", "request.model", "response.model", "body.model")
                    ?? String(Get(cm, "model")) ?? FindString(m, ["modelId", "selectedModel", "model"], 3) : null;
                var rawProvider = assistant ? FirstString(m, "providerId", "providerKey", "provider", "gateway.provider", "request.provider", "response.provider")
                    ?? Properties(Get(m, "providerMetadata")).Select(p => p.Name).FirstOrDefault(k => k != "gateway" && providers.ContainsKey(k))
                    ?? FindString(m, ["providerId", "providerKey", "provider"], 3) : null;
                var prefix = rawModel?.Contains('/') == true ? rawModel.Split('/')[0].ToLowerInvariant() : null;
                var provider = rawProvider?.ToLowerInvariant() ?? prefix ?? "unknown";
                var modelId = prefix == provider ? rawModel![(rawModel!.IndexOf('/') + 1)..] : rawModel;
                var modelKey = modelId is { Length: > 0 } ? provider == "unknown" ? modelId : provider + "/" + modelId : null;
                var providerLabel = provider == "unknown" ? "—" : providers.GetValueOrDefault(provider) ?? provider;
                var author = Get(m, "author").ValueKind == JsonValueKind.String ? Get(m, "author").GetString()?.Trim() : null;
                var kind = assistant ? !string.IsNullOrEmpty(author) ? "agent" : "model" : null;
                var entityKey = kind == "agent" ? author : modelKey;
                var entityLabel = kind == "agent" ? author : rawModel;
                var usage = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var path in new[] { "response.usage", "gateway.usage", "usage" })
                    foreach (var prop in Properties(Get(m, path))) usage[prop.Name] = prop.Value;
                var merged = JsonSerializer.SerializeToElement(usage);
                var input = FirstNumber(merged, "inputTokens", "promptTokens", "prompt_tokens", "input_tokens", "tokens.input", "tokens.prompt") ?? 0;
                var output = FirstNumber(merged, "outputTokens", "completionTokens", "completion_tokens", "output_tokens", "tokens.output", "tokens.completion") ?? 0;
                var explicitTotal = Number(Get(m, "totalTokens")) ?? FirstNumber(merged, "totalTokens", "total_tokens", "tokens.total");
                var total = explicitTotal ?? input + output;
                var cost = FirstNumber(m, "providerMetadata.gateway.cost", "gateway.cost", "cost", "response.cost") ?? 0;
                var gateway = FirstDefined(Get(m, "providerMetadata.gateway"), Get(m, "gateway"));
                a.Messages++; a.Parts += message.Parts.Count; Add(a.RoleCounts, role);
                if (day is not null) { Add(a.DailyMessages, day); Add(a.DailyTokens, day, total); Add(a.DailyCost, day, cost); }
                if (local is { } l) { Add(a.HourCounts, l.Hour.ToString("D2", CultureInfo.InvariantCulture)); Add(a.WeekdayHourCounts, $"{(int)l.DayOfWeek}-{l.Hour}"); }
                if (timestamp is { } t)
                {
                    if (cf.FirstTimestamp is null || t < cf.FirstTimestamp) cf.FirstTimestamp = t;
                    if (cf.LastTimestamp is null || t > cf.LastTimestamp) cf.LastTimestamp = t;
                }
                if (explicitTotal is not null || input > 0 || output > 0)
                { a.MessagesWithUsage++; a.InputTokens += input; a.OutputTokens += output; a.TotalTokens += total; }
                if (cost > 0) a.MessagesWithCost++;
                a.TotalCost += cost; cf.Tokens += total; cf.Cost += cost;
                if (!string.IsNullOrEmpty(entityKey))
                {
                    a.MessagesWithModel++;
                    var stats = kind == "agent" ? a.AgentStats : a.ModelStats;
                    if (!stats.TryGetValue(entityKey, out var stat)) stats[entityKey] = stat = new(entityKey, entityLabel ?? entityKey, provider, providerLabel, modelId ?? "");
                    stat.Count++; stat.Tokens += total; stat.Cost += cost;
                }
                if (assistant && provider != "unknown")
                {
                    if (!a.ProviderStats.TryGetValue(provider, out var stat)) a.ProviderStats[provider] = stat = new(provider, providerLabel);
                    stat.Count++; stat.Tokens += total; stat.Cost += cost;
                    if (modelKey is not null) stat.Models.Add(modelKey);
                }
                if (Number(Get(m, "temperature")) is { } temperature) Add(a.TemperatureCounts, temperature.ToString("F1", CultureInfo.InvariantCulture));
                if (String(FirstDefined(Get(m, "finishReason"), Get(m, "finish_reason"), Get(m, "response.finishReason"))) is { } finish) Add(a.FinishReasonCounts, finish);
                if (String(FirstDefined(Get(m, "error.message"), Get(m, "error"), Get(m, "gateway.error"))) is { } error) Add(a.ErrorCounts, error[..Math.Min(80, error.Length)]);
                int chars = 0, tools = 0; bool sources = false, reasoning = false, data = false, structured = false;
                foreach (var part in message.Parts)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = PortableConversations.Element(part); var type = part.Type;
                    var tool = type.StartsWith("tool-", StringComparison.Ordinal);
                    var dataPart = type.StartsWith("data-", StringComparison.Ordinal);
                    var source = type is "source-url" or "source-document";
                    Add(a.ContentTypeCounts, tool ? "tool" : dataPart ? "data" : source ? "source" : type);
                    cf.PartCount++;
                    var length = type is "text" or "reasoning" ? (String(FirstDefined(Get(p, "text"), type == "reasoning" ? Get(p, "reasoning") : default), trim: false) ?? "").Length : 0;
                    chars += length; a.TextChars += length; cf.TextChars += length;
                    if (type == "file")
                    {
                        var media = (String(FirstDefined(Get(p, "mediaType"), Get(p, "mimeType"))) ?? "").ToLowerInvariant();
                        Add(a.AttachmentTypeCounts, media.StartsWith("image/") ? "image" : media.StartsWith("audio/") ? "audio" : media.StartsWith("video/") ? "video" : media.Contains("pdf") ? "pdf" : media.Contains("json") ? "json" : "file");
                    }
                    if (source) { sources = true; Add(a.SourceTypeCounts, type == "source-url" ? "url" : "document"); }
                    reasoning |= type == "reasoning"; data |= dataPart;
                    structured |= dataPart && (Get(p, "data").ValueKind != JsonValueKind.Undefined || Get(p, "output").ValueKind != JsonValueKind.Undefined);
                    // Match the web dashboard's tool-* scope (dynamic-tool remains a content type).
                    if (!tool) continue;
                    tools++;
                    var toolName = String(Get(p, "toolName")) ?? (type.Length > 5 ? type[5..] : "unknown-tool");
                    var state = (String(Get(p, "state")) ?? "").ToLowerInvariant();
                    var result = Get(p, "output");
                    state = Truthy(Get(p, "error")) || Truthy(Get(p, "output.isError")) || state.Contains("error") ? "error"
                        : state.Contains("output") || state.Contains("result") || result.ValueKind != JsonValueKind.Undefined ? "success" : "pending";
                    Add(a.ToolStateCounts, state);
                    if (!a.ToolStats.TryGetValue(toolName, out var ts)) a.ToolStats[toolName] = ts = new(toolName);
                    ts.Count++; if (state == "success") ts.Success++; else if (state == "error") ts.Error++; else ts.Pending++;
                    var resultTypes = new List<string>();
                    if (Get(result, "structuredContent").ValueKind != JsonValueKind.Undefined) resultTypes.Add("structuredContent");
                    if (Get(result, "content") is { ValueKind: JsonValueKind.Array } content)
                        foreach (var item in content.EnumerateArray()) resultTypes.Add(String(Get(item, "type")) ?? "content");
                    if (resultTypes.Count == 0) resultTypes.Add(result.ValueKind switch { JsonValueKind.Undefined => "none", JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "object" });
                    foreach (var rt in resultTypes) { Add(ts.ResultTypes, rt); Add(a.ToolResultTypeCounts, rt); }
                }
                if (tools > 0) a.MessagesWithTools++;
                if (sources) a.MessagesWithSources++;
                if (reasoning) a.MessagesWithReasoning++;
                if (data) a.MessagesWithDataParts++;
                if (structured) a.MessagesWithStructuredOutput++;
                double? Metric(string key) => assistant && Get(gateway, key).ValueKind == JsonValueKind.Number && Number(Get(gateway, key)) is { } v && v >= 0 ? v : null;
                a.MessageFacts.Add(new(conversation.Id, name, message.Id, role, timestamp, day, local?.Hour, modelKey, rawModel,
                    provider, providerLabel, total, input, output, cost, chars, message.Parts.Count, tools, kind, entityKey, entityLabel, Metric("ttft"), Metric("tps")));
            }
            a.ConversationFacts.Add(cf);
        }
        return a;
    }

    private static void Add(Dictionary<string, double> map, string key, double delta = 1)
    { key = string.IsNullOrWhiteSpace(key) ? "unknown" : key.Trim(); map[key] = map.GetValueOrDefault(key) + delta; }
    private static JsonElement Get(JsonElement obj, string path)
    {
        foreach (var segment in path.Split('.'))
        { if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(segment, out obj)) return default; }
        return obj;
    }
    private static JsonElement FirstDefined(params JsonElement[] values) => values.FirstOrDefault(v => v.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null);
    private static IEnumerable<JsonProperty> Properties(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject() : [];
    private static string? String(JsonElement e, bool trim = true)
    {
        var s = e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Number && Number(e) is not null ? e.GetRawText() : null;
        return trim ? string.IsNullOrWhiteSpace(s) ? null : s.Trim() : s;
    }
    private static double? Number(JsonElement e) => (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n)
        || e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) && double.IsFinite(n) ? n : null;
    private static string? FirstString(JsonElement e, params string[] paths) => paths.Select(p => String(Get(e, p))).FirstOrDefault(s => s is not null);
    private static double? FirstNumber(JsonElement e, params string[] paths) => paths.Select(p => Number(Get(e, p))).FirstOrDefault(n => n is not null);
    private static string? FindString(JsonElement e, string[] keys, int depth)
    {
        if (depth < 0 || e.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array) return null;
        if (FirstString(e, keys) is { } direct) return direct;
        var children = e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToArray() : Properties(e).Select(p => p.Value);
        foreach (var child in children) if (FindString(child, keys, depth - 1) is { } found) return found;
        return null;
    }
    private static bool Truthy(JsonElement e) => e.ValueKind switch { JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false, JsonValueKind.String => e.GetString()?.Length > 0, JsonValueKind.Number => Number(e) is { } n && n != 0, _ => true };

    public static UsageMetricSummary? Summarize(IEnumerable<double?> values)
    {
        var samples = values.Where(v => v is { } n && double.IsFinite(n) && n >= 0).Select(v => v!.Value).Order().ToArray();
        if (samples.Length == 0) return null;
        int middle = samples.Length / 2;
        return new(samples.Length, samples.Average(), samples.Length % 2 == 1 ? samples[middle] : (samples[middle - 1] + samples[middle]) / 2,
            samples[(int)Math.Ceiling(samples.Length * .95) - 1]);
    }
    public static IEnumerable<KeyValuePair<string, double>> Top(IReadOnlyDictionary<string, double> values, int limit = 12) =>
        values.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.CurrentCulture).Take(limit);
    public static IEnumerable<UsageEntityStat> TopEntities(IReadOnlyDictionary<string, UsageEntityStat> values, int limit = 12) =>
        values.Values.OrderByDescending(p => p.Count).ThenBy(p => p.Label, StringComparer.CurrentCulture).Take(limit);
}

public sealed record UsageMetricSummary(int Count, double Mean, double Median, double P95);

public static class UsageFormatting
{
    public static string Number(double value) => value.ToString("N0", CultureInfo.CurrentCulture);
    public static string Compact(double value) => Math.Abs(value) >= 1_000_000_000 ? (value / 1_000_000_000).ToString("0.#", CultureInfo.CurrentCulture) + "B"
        : Math.Abs(value) >= 1_000_000 ? (value / 1_000_000).ToString("0.#", CultureInfo.CurrentCulture) + "M"
        : Math.Abs(value) >= 1_000 ? (value / 1_000).ToString("0.#", CultureInfo.CurrentCulture) + "K" : Number(value);
    // The portable metadata does not establish a currency. Never infer USD or EUR.
    public static string Cost(double value) => value.ToString(value < 1 ? "#,0.####" : "#,0.##", CultureInfo.CurrentCulture);
    public static string Metric(double? value, string unit = "") => value is null ? "—" : value.Value.ToString("#,0.##", CultureInfo.CurrentCulture) + (unit.Length > 0 ? " " + unit : "");
}
