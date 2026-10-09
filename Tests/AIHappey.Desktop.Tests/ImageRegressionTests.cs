using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ImageRegressionTests
{
    internal const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6zGAAAAAASUVORK5CYII=";
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var model = new ChatTarget("alias/image", "OpenAI image alias") { ModelType = "image", ProviderKey = "openai" };
        var preferences = new ImagePreferences { Size = "1024x1536", AspectRatio = "2:3", N = 5, MaxImagesPerCall = 2, Seed = -7 };
        preferences.ProviderOptions["openai"]["future"] = new System.Text.Json.Nodes.JsonObject { ["keep"] = true };
        preferences.ProviderOptions["other"] = new() { ["unrelated"] = true };
        var bytes = Convert.FromBase64String(Png); var file = ComposerAttachment.Local("source.png", "image/png", bytes);
        var link = ComposerAttachment.Link("https://media.example/source.png", "image/png");
        var body = JsonSerializer.SerializeToElement(preferences.Request(model, "A lighthouse", [file, link], file, 2), JsonSerializerOptions.Web);
        check(body.GetProperty("model").GetString() == model.Id && body.GetProperty("prompt").GetString() == "A lighthouse"
            && body.GetProperty("n").GetInt32() == 2 && body.GetProperty("seed").GetInt32() == -7, "image wire prompt/model/count/seed");
        check(body.GetProperty("size").GetString() == "1024x1536" && body.GetProperty("aspectRatio").GetString() == "2:3", "image size and aspect retain Vercel casing");
        var wireFile = body.GetProperty("files")[0]; var wireUrl = body.GetProperty("files")[1];
        check(wireFile.GetProperty("type").GetString() == "file" && wireFile.GetProperty("mediaType").GetString() == "image/png"
            && wireFile.GetProperty("data").GetString() == Png && !wireFile.TryGetProperty("url", out _), "image local inputs use plain base64 file union, not chat data URLs");
        check(wireUrl.GetProperty("type").GetString() == "url" && wireUrl.GetProperty("url").GetString() == link.RemoteUrl
            && !wireUrl.TryGetProperty("data", out _) && body.GetProperty("mask").GetProperty("data").GetString() == Png, "image URL union and independent mask");
        check(body.GetProperty("providerOptions").EnumerateObject().Select(p => p.Name).SequenceEqual(["openai"])
            && body.GetProperty("providerOptions").GetProperty("openai").GetProperty("future").GetProperty("keep").GetBoolean()
            && !body.TryGetProperty("providerMetadata", out _), "catalog provider takes priority over alias prefix, options are scoped, unknown fields retained");
        var otherModel = model with { ProviderKey = "other" };
        check(preferences.Request(otherModel, "x", [], null, 1).ProviderOptions!.Keys.SequenceEqual(["other"]), "non-OpenAI generation does not send OpenAI settings");
        check(new ImagePreferences().N == 1 && new ImagePreferences().ProviderOptions["openai"]["moderation"]!.GetValue<string>() == "auto"
            && new ImagePreferences().EffectiveRoot.EndsWith(Path.Combine("aihappey", "Images")), "image defaults match browser and approved Pictures app name");
        var defaults = JsonSerializer.Deserialize<DesktopSettings>("{}", JsonSerializerOptions.Web)!;
        var nullSettings = JsonSerializer.Deserialize<DesktopSettings>("{\"images\":null}", JsonSerializerOptions.Web)!;
        check(defaults.Images.N == 1 && nullSettings.Images.N == 1, "legacy/null image settings are backward compatible");
        preferences.StorageRoot = Path.Combine(root, "images");
        var settings = new DesktopSettings { Images = preferences }; var clone = settings.Clone(); clone.Images.ProviderOptions["openai"]["quality"] = "high";
        check(settings.Images.ProviderOptions["openai"]["quality"]!.GetValue<string>() == "auto", "image settings drafts deep clone provider objects");
        await SettingsStore.SaveAsync(Path.Combine(root, "image-settings"), settings);
        var saved = await SettingsStore.LoadAsync(Path.Combine(root, "image-settings"), new());
        check(saved.Images.Seed == -7 && saved.Images.MaxImagesPerCall == 2 && saved.Images.StorageRoot == preferences.StorageRoot
            && saved.Images.ProviderOptions.ContainsKey("other") && saved.Chat.MaxOutputTokens is null, "image preferences persist independently of chat");
        foreach (var invalid in new ImagePreferences[] { new() { N = 0 }, new() { N = 21 }, new() { MaxImagesPerCall = 0 }, new() { Size = "0x1" }, new() { AspectRatio = "1.5:2" }, new() { StorageRoot = "relative" } })
            Reject(invalid.Validate, check, "invalid image preferences rejected");
        Reject(() => ImageAttachments.ToFile(ComposerAttachment.Local("text.txt", "text/plain", bytes)), check, "non-image attachment rejected");
        Reject(() => preferences.Request(model, "x", Enumerable.Repeat(file, 21).ToArray(), null, 1), check, "aggregate image attachment count bounded");
        foreach (var data in new[] { "https://image.example/output.png", "data:image/png;base64,%%%", "data:image/svg+xml;base64,AA==", "data:image/png;base64," })
            Reject(() => ImageAttachments.Decode(data), check, "malformed/non-raster output rejected");
        check(ImageAttachments.Decode("data:image/png;base64," + Png).Bytes.SequenceEqual(bytes), "original image bytes decode without re-encoding");

        var host = new ImageHost(); var session = new DesktopSession(host, new ImageRuntime(), new());
        var partition = ImageLibraryStore.Partition(session); var store = new ImageLibraryStore(preferences.EffectiveRoot, partition);
        using var handler = new ImageHandler(); using var http = new HttpClient(handler); var client = new DesktopImageClient(new(session, http), http);
        var batches = new List<ImageGenerationBatch>();
        await client.GenerateAsync(model, "A lighthouse", preferences, [file, link], file, async batch =>
        { batches.Add(batch); await store.SaveAsync(batch, [file, link], file); preferences.N = 1; }, CancellationToken.None);
        check(handler.Counts.SequenceEqual([2, 2, 1]) && batches.Count == 3, "image batches snapshot preferences and honor maximum images per call");
        check(handler.Paths.All(p => p == "https://test.invalid/ai/api/images") && handler.Authenticated && host.Calls == 3, "every image batch uses host authentication and AI backend route");
        var images = await store.ListAsync();
        check(images.Count == 5 && images.All(i => File.Exists(i.Path) && File.ReadAllBytes(i.Path).SequenceEqual(bytes)), "all generated originals saved as ordinary disk files and reloaded");
        check(images.All(i => i.Cost == 0.25) && batches.SelectMany(b => b.Warnings).Count() == 3, "per-batch gateway cost correctly allocated per image and warnings retained");
        check(images.All(i => i.Generation.Response["usage"]?["totalTokens"]?.GetValue<int>() == 7
            && i.Generation.Response["providerMetadata"]?["openai"]?["future"]?.GetValue<string>() == "kept"), "usage and opaque provider metadata persist for every batch");
        var manifest = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(images[0].Path)!, "generation.json"));
        check(!manifest.Contains("host-secret") && !manifest.Contains("set-cookie") && !manifest.Contains(Png)
            && images.All(i => Directory.GetFiles(Path.GetDirectoryName(i.Path)!, "input-*.png").Length == 1), "library excludes credentials, response headers and inline image payloads; sources saved as files");
        check((await new ImageLibraryStore(preferences.EffectiveRoot, "other-account").ListAsync()).Count == 0, "image disk libraries isolate accounts");
        session.Settings.Agents.RemoteUrl = "https://other.invalid/agents";
        check(ImageLibraryStore.Partition(session) == partition, "unrelated agent backend does not change image partition");
        host.Identity = "other"; check(ImageLibraryStore.Partition(session) != partition, "image partition changes with account"); host.Identity = "account";
        session.Settings.Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://other.invalid/ai/" };
        check(ImageLibraryStore.Partition(session) != partition, "image partition changes with AI backend");
        var secondRoot = new ImageLibraryStore(Path.Combine(root, "second-images"), partition);
        check((await secondRoot.ListAsync()).Count == 0 && (await store.ListAsync()).Count == 5, "folder override switches library without migrating/deleting originals");
        var group = images.GroupBy(i => i.Generation.Id).First(g => g.Count() == 2).ToArray();
        await store.DeleteAsync(group[0]); check((await store.ListAsync()).Count == 4 && File.Exists(group[1].Path), "delete removes selected image and retains other outputs");
        await store.DeleteAsync(group[1]); check((await store.ListAsync()).Count == 3 && !Directory.Exists(Path.GetDirectoryName(group[1].Path)), "last output deletion cleans its app-owned generation directory");
        var corrupt = Path.Combine(store.Folder, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(corrupt); await File.WriteAllTextAsync(Path.Combine(corrupt, "generation.json"), "not json");
        check((await store.ListAsync()).Count == 3, "externally corrupt manifest does not break image library");
        File.Delete((await store.ListAsync())[0].Path); check((await store.ListAsync()).Count == 2, "externally deleted image files do not break reload");

        using var failHandler = new ImageHandler { FailAt = 2 }; using var failHttp = new HttpClient(failHandler);
        var failStore = new ImageLibraryStore(Path.Combine(root, "partial"), partition); var multi = new ImagePreferences { N = 3, MaxImagesPerCall = 1 };
        try { await new DesktopImageClient(new(session, failHttp), failHttp).GenerateAsync(model, "x", multi, [], null, b => failStore.SaveAsync(b, [], null), CancellationToken.None); throw new Exception("Expected failure"); }
        catch (GatewayException) { check(failHandler.Counts.Count == 2 && (await failStore.ListAsync()).Count == 1, "later failed batch keeps completed outputs and does not retry inference"); }
        using var cancel = new CancellationTokenSource(); using var cancelHandler = new ImageHandler(); using var cancelHttp = new HttpClient(cancelHandler);
        var cancelStore = new ImageLibraryStore(Path.Combine(root, "cancel"), partition);
        try { await new DesktopImageClient(new(session, cancelHttp), cancelHttp).GenerateAsync(model, "x", multi, [], null, async b => { cancel.Cancel(); await cancelStore.SaveAsync(b, [], null); }, cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(cancelHandler.Counts.Count == 1 && (await cancelStore.ListAsync()).Count == 1, "Stop prevents next paid request but commits completed batch"); }
        var invalidBatch = new ImageGenerationBatch(new ImageRequest { Model = model.Id, Prompt = "x" }, JsonSerializer.SerializeToElement(new { images = new[] { "invalid" } }));
        try { await cancelStore.SaveAsync(invalidBatch, [], null); throw new Exception("Expected invalid image"); }
        catch (InvalidOperationException) { check((await cancelStore.ListAsync()).Count == 1 && !Directory.EnumerateDirectories(cancelStore.Folder).Any(d => Path.GetFileName(d).StartsWith('.')), "invalid outputs never commit partial generation folders"); }
    }
    private static void Reject(Action action, Action<bool, string> check, string name)
    {
        try { action(); } catch (InvalidOperationException) { check(true, name); return; } throw new Exception("Expected rejection: " + name);
    }
    private sealed class ImageHost : IDesktopHost
    {
        public string ProfileId => "image-tests"; public bool AllowLocal => true; public string AccountLabel => "Test";
        public string Identity { get; set; } = "account"; public string HistoryIdentity => Identity; public int Calls;
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) { Calls++; request.Headers.Add("x-openai-key", "host-secret"); return Task.CompletedTask; }
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class ImageRuntime : IRuntimeResolver
    { public Task<Uri> ResolveAsync(ServiceKind kind, DesktopSettings settings, CancellationToken ct) => Task.FromResult(new Uri("https://test.invalid/ai/")); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class ImageHandler : HttpMessageHandler
    {
        public List<int> Counts { get; } = []; public List<string> Paths { get; } = []; public bool Authenticated = true; public int FailAt;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var n = document.RootElement.GetProperty("n").GetInt32(); Counts.Add(n); Paths.Add(request.RequestUri!.AbsoluteUri);
            Authenticated &= request.Headers.Contains("x-openai-key");
            if (Counts.Count == FailAt) return new(HttpStatusCode.TooManyRequests);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                images = Enumerable.Repeat("data:image/png;base64," + Png, n).ToArray(), warnings = new[] { new { type = "unsupported", feature = "seed" } },
                providerMetadata = new { gateway = new { cost = n * 0.25 }, openai = new { future = "kept" } },
                response = new { modelId = "openai/image", timestamp = DateTimeOffset.UtcNow, headers = new Dictionary<string, string> { ["set-cookie"] = "host-secret" } }, usage = new { totalTokens = 7 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
