using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class SpeechRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var model = new ChatTarget("alias/speech", "OpenAI speech alias") { ModelType = "speech", ProviderKey = "openai" };
        var preferences = new SpeechPreferences
        {
            Voice = "nova", OutputFormat = "wav", Instructions = "Speak warmly", Speed = 1.25, Language = "en"
        };
        preferences.ProviderOptions["openai"] = new JsonObject
        {
            ["voice"] = new JsonObject { ["id"] = "voice_1234", ["futureVoice"] = true },
            ["response_format"] = "pcm", ["speed"] = .75, ["future"] = new JsonObject { ["keep"] = true }
        };
        preferences.ProviderOptions["other"] = new JsonObject { ["unrelated"] = true };

        var request = preferences.Request(model, " Hello ");
        var wire = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        check(wire.GetProperty("model").GetString() == model.Id && wire.GetProperty("text").GetString() == "Hello"
            && wire.GetProperty("voice").GetString() == "nova" && wire.GetProperty("outputFormat").GetString() == "wav"
            && wire.GetProperty("speed").GetSingle() == 1.25f && wire.GetProperty("language").GetString() == "en",
            "speech request uses the contract-locked browser field names and canonical gateway types");
        check(wire.GetProperty("providerOptions").EnumerateObject().Select(value => value.Name).SequenceEqual(["openai"])
            && wire.GetProperty("providerOptions").GetProperty("openai").GetProperty("voice").GetProperty("id").GetString() == "voice_1234"
            && wire.GetProperty("providerOptions").GetProperty("openai").GetProperty("future").GetProperty("keep").GetBoolean(),
            "speech options are scoped to the catalog provider and preserve unknown OpenAI fields");

        var other = preferences.Request(model with { ProviderKey = "other" }, "Hello");
        check(other.ProviderOptions!.Keys.SequenceEqual(["other"]), "speech generation never leaks OpenAI options to another provider");
        var defaults = new SpeechPreferences().Request(model, "Hello");
        check(defaults.Speed == 1f, "speech request serializes the displayed default speed of 1 instead of null");
        var providerSpeed = new SpeechPreferences();
        providerSpeed.ProviderOptions["openai"] = new JsonObject { ["speed"] = .75 };
        check(providerSpeed.Request(model, "Hello").Speed is null,
            "explicit OpenAI provider speed remains authoritative over the general default");

        var options = (JsonObject)preferences.ProviderOptions["openai"].DeepClone();
        check(OpenAISpeechOptions.VoiceSelection(options) == OpenAISpeechOptions.CustomVoice
            && OpenAISpeechOptions.CustomVoiceId(options) == "voice_1234", "OpenAI custom voice objects load into the native form model");
        OpenAISpeechOptions.SetVoice(options, "coral");
        OpenAISpeechOptions.SetFormat(options, "opus");
        OpenAISpeechOptions.SetSpeed(options, 2.5);
        check(options["voice"]!.GetValue<string>() == "coral" && options["response_format"]!.GetValue<string>() == "opus"
            && options["speed"]!.GetValue<double>() == 2.5 && options["future"]?["keep"]?.GetValue<bool>() == true,
            "native OpenAI edits preserve unrelated provider properties");
        OpenAISpeechOptions.SetVoice(options, OpenAISpeechOptions.CustomVoice, " voice_9876 ");
        check(options["voice"]?["id"]!.GetValue<string>() == "voice_9876", "native OpenAI custom voice maps to the exact object contract");
        OpenAISpeechOptions.SetVoice(options, OpenAISpeechOptions.DefaultVoice);
        OpenAISpeechOptions.SetFormat(options, null);
        check(options["voice"] is null && options["response_format"] is null && options["future"] is not null,
            "provider defaults omit known OpenAI keys without deleting unknown fields");

        var clone = preferences.Clone();
        clone.ProviderOptions["openai"]["future"]!["keep"] = false;
        check(preferences.ProviderOptions["openai"]["future"]?["keep"]?.GetValue<bool>() == true, "speech preference drafts deep clone provider options");
        var settings = new DesktopSettings { Speech = preferences };
        var directory = Path.Combine(root, "speech-settings");
        await SettingsStore.SaveAsync(directory, settings);
        var saved = await SettingsStore.LoadAsync(directory, new());
        check(saved.Speech.OutputFormat == "wav" && saved.Speech.ProviderOptions["openai"]["voice"]?["id"]?.GetValue<string>() == "voice_1234",
            "speech settings and custom OpenAI voice objects persist losslessly");

        using var handler = new SpeechHandler();
        using var http = new HttpClient(handler);
        var session = new DesktopSession(new SpeechHost(), new SpeechRuntime(), new());
        var result = await new DesktopSpeechClient(new DesktopChatClient(session, http), http)
            .GenerateAsync(model, "Hello", new SpeechPreferences(), CancellationToken.None);
        check(handler.Path == "https://test.invalid/ai/api/speech"
            && handler.Body.GetProperty("model").GetString() == model.Id && handler.Body.GetProperty("text").GetString() == "Hello"
            && handler.Body.GetProperty("speed").GetSingle() == 1f
            && !handler.Body.TryGetProperty("voice", out _) && !handler.Body.TryGetProperty("providerOptions", out _),
            "speech client sends browser-equivalent JSON, defaults speed to 1, and omits other unset optional properties");
        check(result.Audio.SequenceEqual(new byte[] { 1, 2, 3 }) && result.MediaType == "audio/mpeg" && result.Format == "mp3",
            "speech client decodes the canonical gateway audio object");
    }

    private sealed class SpeechHost : IDesktopHost
    {
        public string ProfileId => "speech-tests";
        public bool AllowLocal => true;
        public string AccountLabel => "Test";
        public string HistoryIdentity => "account";
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => Task.CompletedTask;
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SpeechRuntime : IRuntimeResolver
    {
        public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken ct) => Task.FromResult(new Uri("https://test.invalid/ai/"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SpeechHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public JsonElement Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri?.AbsoluteUri;
            Body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"audio":{"base64":"AQID","mimeType":"audio/mpeg","format":"mp3"},"warnings":[],"response":{"modelId":"gpt-4o-mini-tts"}}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
