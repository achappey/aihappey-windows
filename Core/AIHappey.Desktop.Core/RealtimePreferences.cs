using System.Globalization;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed class RealtimePreferences
{
    public string Model { get; set; } = "openai/gpt-realtime-2";
    public JsonObject ProviderOptions { get; set; } = new();
    // Client application state, never included in a native provisioning payload.
    public string? LiveClientTarget { get; set; }
    public bool LiveClientAgent { get; set; }
    public double CameraFrameRate { get; set; } = 1;
    public double JpegQuality { get; set; } = .8;
    public RealtimePreferences Clone() => new() { Model = Model, ProviderOptions = (JsonObject)ProviderOptions.DeepClone(), CameraFrameRate = CameraFrameRate, JpegQuality = JpegQuality, LiveClientTarget = LiveClientTarget, LiveClientAgent = LiveClientAgent };
    public void Validate()
    {
        if (!double.IsFinite(CameraFrameRate) || CameraFrameRate < .1 || CameraFrameRate > 1
            || !double.IsFinite(JpegQuality) || JpegQuality < .1 || JpegQuality > 1) throw new ArgumentException("Invalid realtime video settings.");
        if (ProviderOptions.ToJsonString().Length > 128_000) throw new ArgumentException("Realtime settings are too large.");
    }
}

/// <summary>Client-owned configuration, matching the browser token and connection payloads.</summary>
public static class RealtimeConfiguration
{
    public static string Provider(string model) => model.Split('/')[0].ToLowerInvariant();
    public static bool Supported(string model) => Provider(model) is "openai" or "google" or "smallestai"
        || model.StartsWith("fishaudio/agents/", StringComparison.Ordinal);
    public static bool UsesSessions(ChatTarget model) => Provider(model.Id) == "openai" && model.Tags.Contains("realtime-session");
    public static bool IsLive(ChatTarget model) => UsesSessions(model) && !model.Tags.Contains("realtime-token");
    public static JsonObject LiveSession(RealtimePreferences preferences, string instructions)
    {
        var source = preferences.ProviderOptions["openai"]?["session"] as JsonObject ?? new();
        var session = new JsonObject();
        foreach (var field in new[] { "instructions", "client", "delegation", "input", "store", "triggers" })
            if (source.ContainsKey(field)) session[field] = source[field]?.DeepClone();
        if (!session.ContainsKey("instructions")) session["instructions"] = instructions;
        if (source["audio"]?["output"]?["voice"] is { } voice) session["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = voice.DeepClone() } };
        if (session["delegation"]?["type"]?.ToString() == "responses" && string.IsNullOrWhiteSpace(session["delegation"]?["responses"]?["model"]?.ToString()))
            throw new ArgumentException("Live Responses delegation requires a backend model.");
        if (session["delegation"]?["responses"]?["max_output_tokens"] is { } maximum && maximum.GetValue<double>() < 16)
            throw new ArgumentException("Live Responses maximum output tokens must be at least 16.");
        return new() { ["providerOptions"] = new JsonObject { ["openai"] = new JsonObject { ["session"] = session } } };
    }
    public static string ModelName(string model) => model[(model.IndexOf('/') + 1)..];
    public static string GoogleModel(string model)
    {
        var name = model.StartsWith("google/", StringComparison.Ordinal) ? model[7..] : model;
        return name.StartsWith("models/", StringComparison.Ordinal) ? name : "models/" + name;
    }
    public static JsonObject Merge(params JsonObject[] objects)
    {
        var result = new JsonObject();
        foreach (var item in objects)
            foreach (var (key, value) in item)
                result[key] = value is JsonObject next && result[key] is JsonObject previous
                    ? Merge(previous, next) : value?.DeepClone();
        return result;
    }
    public static JsonObject Session(string model, RealtimePreferences preferences, string instructions, IEnumerable<JsonObject> tools, string? toolChoice, JsonObject? chat = null)
    {
        var provider = Provider(model);
        var options = preferences.ProviderOptions[provider] as JsonObject ?? new();
        var definitions = tools.ToArray();
        if (provider is "smallestai" or "fishaudio") return new(); // Hosted agent configuration lives at the provider.
        if (provider == "openai")
        {
            var defaults = JsonNode.Parse("""{"type":"realtime","output_modalities":["audio"],"audio":{"input":{"turn_detection":{"type":"semantic_vad","eagerness":"auto","create_response":true,"interrupt_response":true}},"output":{"voice":"marin"}}}""")!.AsObject();
            defaults["instructions"] = instructions;
            defaults["tools"] = new JsonArray(definitions.Select(t => (JsonNode)Merge(new() { ["type"] = "function" }, t)).ToArray());
            defaults["tool_choice"] = toolChoice == "none" ? "none" : "auto";
            var result = Merge(chat ?? new(), defaults, options["session"] as JsonObject ?? new());
            foreach (var field in new[] { "delegation", "client", "input", "store", "triggers" }) result.Remove(field);
            result["type"] = "realtime"; result["model"] = ModelName(model);
            return result;
        }
        if (provider != "google") throw new NotSupportedException("Unsupported realtime provider.");
        var config = options["liveConnectConstraints"]?["config"] as JsonObject ?? options["session"] as JsonObject ?? new();
        var googleDefaults = JsonNode.Parse("""{"responseModalities":["AUDIO"],"inputAudioTranscription":{},"outputAudioTranscription":{},"contextWindowCompression":{"slidingWindow":{}},"sessionResumption":{}}""")!.AsObject();
        var merged = Merge(googleDefaults, config);
        merged["responseModalities"] = new JsonArray("AUDIO");
        if (!string.IsNullOrWhiteSpace(instructions)) merged["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = instructions }) };
        var configuredTools = (config["tools"] as JsonArray ?? new()).Where(t => t?["functionDeclarations"] is null).Select(t => t?.DeepClone()).ToList();
        if (definitions.Length > 0 && toolChoice != "none") configuredTools.Add(new JsonObject { ["functionDeclarations"] = new JsonArray(definitions.Select(t => (JsonNode)new JsonObject
        { ["name"] = t["name"]?.DeepClone(), ["description"] = t["description"]?.DeepClone(), ["parametersJsonSchema"] = t["parameters"]?.DeepClone() }).ToArray()) });
        if (configuredTools.Count > 0) merged["tools"] = new JsonArray(configuredTools.ToArray()); else merged.Remove("tools");
        return merged;
    }
    private static readonly HashSet<string> GenerationFields = ["responseModalities", "temperature", "topP", "topK", "maxOutputTokens", "mediaResolution", "seed", "speechConfig", "thinkingConfig", "enableAffectiveDialog", "translationConfig"];
    public static JsonObject GoogleSetup(string model, JsonObject config)
    {
        var setup = new JsonObject { ["model"] = GoogleModel(model) };
        var generation = config["generationConfig"]?.DeepClone() as JsonObject ?? new();
        foreach (var (key, value) in config)
            if (GenerationFields.Contains(key)) generation[key] = value?.DeepClone();
            else if (key != "generationConfig" && key != "model") setup[key] = value?.DeepClone();
        if (generation.Count > 0) setup["generationConfig"] = generation;
        return setup;
    }
    public static JsonObject TokenRequest(string model, RealtimePreferences preferences, JsonObject config, DateTimeOffset now)
    {
        var provider = Provider(model);
        var metadata = preferences.ProviderOptions[provider] as JsonObject ?? new();
        JsonObject payload;
        if (provider == "openai") payload = Merge(metadata, new() { ["session"] = config.DeepClone() });
        else if (provider == "google")
        {
            payload = new() { ["uses"] = metadata["uses"]?.DeepClone() ?? JsonValue.Create(1),
                ["expireTime"] = Expiry(metadata["expireTime"]?.ToString(), now, TimeSpan.FromMinutes(30)),
                ["newSessionExpireTime"] = Expiry(metadata["newSessionExpireTime"]?.ToString(), now, TimeSpan.FromMinutes(1)),
                ["bidiGenerateContentSetup"] = GoogleSetup(model, config) };
            if (metadata["lockAdditionalFields"] is { } mask) payload["fieldMask"] = mask.DeepClone();
        }
        else if (provider == "smallestai")
        {
            payload = new();
            if (metadata["variables"] is { } variables) payload["variables"] = variables.DeepClone();
        }
        else if (provider == "fishaudio")
        {
            payload = (JsonObject)metadata.DeepClone();
            payload["client_timezone"] ??= TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var timezone) ? timezone : "UTC";
        }
        else throw new NotSupportedException();
        return new() { ["model"] = model, ["providerOptions"] = new JsonObject { [provider] = payload } };
    }
    public static string Expiry(string? value, DateTimeOffset now, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return (now + fallback).ToString("O");
        var match = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^(\d+(?:\.\d+)?)\s*(ms|s|m|h)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var milliseconds = amount * (match.Groups[2].Value.ToLowerInvariant() switch { "ms" => 1, "s" => 1000, "m" => 60_000, _ => 3_600_000 });
            return now.AddMilliseconds(milliseconds).ToString("O");
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var absolute)) return absolute.ToUniversalTime().ToString("O");
        throw new ArgumentException("Invalid Google Live expiry.");
    }
}
