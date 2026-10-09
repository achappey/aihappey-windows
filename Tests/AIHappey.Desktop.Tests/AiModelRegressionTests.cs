using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class AiModelRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root, DesktopSession session)
    {
        var legacy = JsonSerializer.Deserialize<DesktopSettings>("{}", JsonSerializerOptions.Web)!;
        var explicitNull = JsonSerializer.Deserialize<DesktopSettings>("{\"aiModels\":null}", JsonSerializerOptions.Web)!;
        check(AiModelCatalog.Types.All(t => legacy.AiModels.DefaultFor(t) is null)
            && AiModelCatalog.Types.Where(t => t != "language").All(t => !legacy.AiModels.AllowsChat(t))
            && explicitNull.AiModels.AllowsChat("language"), "legacy/null AI preferences retain language-only defaults");

        var preferences = new AiModelPreferences
        {
            ChatWithImageModels = true, ChatWithVideoModels = true,
            ChatWithSpeechModels = true, ChatWithTranscriptionModels = true
        };
        foreach (var type in AiModelCatalog.Types) preferences.SetDefault(type, " provider/" + type + " ");
        var settings = session.Settings.Clone();
        settings.AiModels = preferences;
        settings.ModelContext.ToolTimeoutMinutes = 19;
        settings.AllowedToolList = ["keep-tool"];
        var directory = Path.Combine(root, "ai-preferences");
        await SettingsStore.SaveAsync(directory, settings);
        var loaded = await SettingsStore.LoadAsync(directory, new());
        check(AiModelCatalog.Types.All(t => loaded.AiModels.DefaultFor(t) == "provider/" + t)
            && new[] { "image", "video", "speech", "transcription" }.All(loaded.AiModels.AllowsChat), "all nine defaults and four switches round trip through settings storage");
        var draft = loaded.Clone();
        draft.AiModels.LanguageModel = "changed"; draft.AiModels.ChatWithImageModels = false;
        draft.ModelContext.ToolTimeoutMinutes = 2; draft.AllowedToolList.Clear(); draft.Ai.RemoteUrl = "changed";
        check(loaded.AiModels.LanguageModel == "provider/language" && loaded.AiModels.ChatWithImageModels
            && loaded.ModelContext.ToolTimeoutMinutes == 19 && loaded.AllowedToolList.SequenceEqual(["keep-tool"])
            && loaded.Ai.RemoteUrl != "changed", "complete settings clones isolate AI/MCP/tool/connection drafts");
        var unrelated = loaded.Clone(); unrelated.Chat = loaded.Chat.Clone();
        await SettingsStore.SaveAsync(directory, unrelated);
        loaded = await SettingsStore.LoadAsync(directory, new());
        check(loaded.AiModels.VideoModel == "provider/video" && loaded.ModelContext.ToolTimeoutMinutes == 19
            && loaded.AllowedToolList.SequenceEqual(["keep-tool"]), "unrelated chat-setting saves preserve model preferences and existing settings");
        var blockedDirectory = Path.Combine(root, "ai-settings-blocked");
        await File.WriteAllTextAsync(blockedDirectory, "not a directory");
        var beforeFailure = JsonSerializer.Serialize(loaded, JsonSerializerOptions.Web);
        try { await SettingsStore.SaveAsync(blockedDirectory, draft); throw new Exception("Expected settings write failure"); }
        catch (IOException)
        {
            check(JsonSerializer.Serialize(loaded, JsonSerializerOptions.Web) == beforeFailure
                && (await SettingsStore.LoadAsync(directory, new())).AiModels.LanguageModel == "provider/language",
                "failed draft persistence leaves live and previously persisted model settings unchanged");
        }
        preferences.SetDefault("video", "  ");
        check(preferences.VideoModel is null, "clearing a default does not persist whitespace");

        using var handler = new ModelsHandler();
        using var http = new HttpClient(handler);
        var client = new DesktopChatClient(session, http);
        var models = await client.ListAsync(ServiceKind.Ai, CancellationToken.None);
        check(models.Count == 7 && models[0].Id == "image" && models[0].Created == 1000
            && models[0].ModelType == "image" && models[0].ProviderKey == "openai", "AI model projection retains type, created date and provider metadata");
        check(models.Select(x => x.Id).SequenceEqual(["image", "new", "tie-z", "tie-a", "old", "no-date", "invalid-date"]), "created dates sort descending with stable ties and missing/invalid dates at zero");
        var off = new AiModelPreferences();
        check(AiModelCatalog.ChatSuggestions(models, off).Select(x => x.Id).SequenceEqual(["new", "tie-z", "tie-a", "old", "no-date"]), "chat picker is language-only by default");
        check(AiModelCatalog.ChatSuggestions(models, off, "IMAGE").Count == 0
            && AiModelCatalog.ChatSuggestions(models, off, "nEw").Single().Id == "new", "chat search cannot expose excluded types and matches IDs case-insensitively");
        off.ChatWithImageModels = true;
        check(AiModelCatalog.ChatSuggestions(models, off)[0].Id == "image", "enabled image models join newest-first chat suggestions");
        var everyType = AiModelCatalog.Types.Select((t, i) => new ChatTarget("provider/" + t, t) { ModelType = t, Created = i }).ToArray();
        check(AiModelCatalog.ChatSuggestions(everyType, new()
        {
            ChatWithImageModels = true, ChatWithVideoModels = true, ChatWithSpeechModels = true, ChatWithTranscriptionModels = true
        }).Select(x => x.ModelType).ToHashSet().SetEquals(["language", "image", "video", "speech", "transcription"]),
            "all four chat switches expose only their types, never audio/embedding/decision/reranking");
        foreach (var type in AiModelCatalog.Types)
            check(AiModelCatalog.OfType(everyType, type).Single().ModelType == type, "settings model picker filters type " + type);
        var many = Enumerable.Range(0, 150).Select(i => new ChatTarget("p/" + i, "Model " + i) { ModelType = "language", Created = i }).ToArray();
        check(AiModelCatalog.ChatSuggestions(many, off, "p/0").Single().Id == "p/0"
            && AiModelCatalog.ChatSuggestions(many, off).Take(100).First().Id == "p/149", "search and ordering apply before suggestion truncation");

        off.LanguageModel = "old";
        check(AiModelCatalog.NewChatModel(models, off) == "old", "available preferred language model wins over newest model");
        off.LanguageModel = "missing";
        check(AiModelCatalog.NewChatModel(models, off) == "new" && off.LanguageModel == "missing", "missing default falls back to newest language without overwriting preference");
        off.LanguageModel = "image";
        check(AiModelCatalog.NewChatModel(models, off) == "new", "wrong-type default cannot select a non-language model for a new chat");
        check(AiModelCatalog.NewChatModel([], off) == "" && AiModelCatalog.NewChatModel([models[0]], off) == "", "no language catalog leaves selection empty");
        var agents = await client.ListAsync(ServiceKind.Agents, CancellationToken.None);
        check(agents.Select(x => x.Label).SequenceEqual(agents.Select(x => x.Label).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            && agents.All(x => x.ModelType is null), "agent catalog stays alphabetical and is not subject to AI type inference");
        foreach (var (id, type) in new[]
        {
            ("openai/whisper", "transcription"), ("mistral/voxtral", "transcription"), ("p/voxtral-tts", "speech"),
            ("p/embed", "embedding"), ("p/rerank", "reranking"), ("p/flux", "image"), ("p/sora", "video"),
            ("p/realtime", "audio"), ("p/plain", "language")
        }) check(AiModelCatalog.ResolveType(id, null) == type, "legacy browser-compatible type inference: " + id);
        check(AiModelCatalog.ResolveType("p/image", " LANGUAGE ") == "language"
            && AiModelCatalog.ResolveType("p/embed", "unknown") == "embedding"
            && AiModelCatalog.ResolveType("decision", null) == "decision", "valid normalized type overrides ID guessing; invalid type falls back");
        foreach (var type in AiModelCatalog.Types)
            foreach (var language in new[] { "en", "nl" })
            {
                using var stream = typeof(AiModelRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + language)!;
                var resources = System.Xml.Linq.XDocument.Load(stream).Root!.Elements("data");
                check(resources.Any(x => (string?)x.Attribute("name") == "AiModelType_" + type
                    && !string.IsNullOrWhiteSpace(x.Element("value")?.Value)), "model type resource " + language + "/" + type);
            }
    }

    private sealed class ModelsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"data":[
                  {"id":"old","name":"Old","type":"language","created":1},
                  {"id":"image","name":"Image","type":"image","created":1000,"sourceProviderKey":"openai"},
                  {"id":"tie-z","name":"Z tie","type":"language","created":5},
                  {"id":"new","name":"Newest","type":"language","created":10},
                  {"id":"tie-a","name":"A tie","type":"language","created":5},
                  {"id":"no-date","type":"language","created":null},
                  {"id":"invalid-date","type":"embedding","created":"not a date"},
                  {"id":null},{"id":" "},null
                ]}
                """, Encoding.UTF8, "application/json")
            });
        }
    }
}
