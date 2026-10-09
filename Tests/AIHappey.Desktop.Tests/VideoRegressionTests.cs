using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class VideoRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        root = Path.Combine(root, "videos"); Directory.CreateDirectory(root);
        var preferences = new VideoPreferences { Resolution = "1920x1080", AspectRatio = "16:9", N = 5, MaxVideosPerCall = 2, Duration = 8, Fps = 24, Seed = -3, GenerateAudio = true };
        preferences.ProviderOptions["provider"] = new() { ["future"] = new JsonObject { ["keep"] = true } };
        var image = ComposerAttachment.Local("reference.png", "image/png", Convert.FromBase64String(ImageRegressionTests.Png));
        var video = ComposerAttachment.Local("source.mp4", "video/mp4", new byte[] { 1, 2, 3 });
        var link = ComposerAttachment.Link("https://media.example/ref.mp4", "video/mp4");
        var model = new ChatTarget("provider/video", "Video") { ModelType = "video" };
        var requests = preferences.Requests(model, "A lighthouse", video, [image, link], image, image);
        check(requests.Select(r => r["n"]!.GetValue<int>()).SequenceEqual([2, 2, 1]), "video count splits into bounded batches with final remainder");
        var request = requests[0];
        check(VideoDisk.Text(request["model"]) == model.Id && VideoDisk.Text(request["resolution"]) == "1920x1080" && request["duration"]!.GetValue<int>() == 8
            && request["fps"]!.GetValue<int>() == 24 && request["generateAudio"]!.GetValue<bool>() && request["seed"]!.GetValue<int>() == -3, "video settings use browser-equivalent wire fields");
        check(VideoDisk.Text(request["image"]?["data"]) == "AQID" && VideoDisk.Text(request["inputReferences"]?[1]?["url"]) == link.RemoteUrl
            && VideoDisk.Text(request["frameImages"]?[0]?["frameType"]) == "first_frame" && VideoDisk.Text(request["frameImages"]?[1]?["frameType"]) == "last_frame", "video file, media URL, image references and both frames have shared contract shapes");
        var clone = preferences.Clone(); clone.ProviderOptions["provider"]["future"]!["keep"] = false;
        check(preferences.ProviderOptions["provider"]["future"]!["keep"]!.GetValue<bool>(), "video provider options clone deeply");
        var settings = new DesktopSettings { Videos = preferences }; var settingsClone = settings.Clone(); settingsClone.Videos.N = 1;
        check(settings.Videos.N == 5 && new DesktopSettings().Videos.PollingIntervalSeconds == 10, "desktop settings isolate video drafts and default polling to ten seconds");
        await SettingsStore.SaveAsync(Path.Combine(root, "settings"), settings);
        var loaded = await SettingsStore.LoadAsync(Path.Combine(root, "settings"), new());
        check(loaded.Videos.N == 5 && loaded.Videos.Duration == 8 && loaded.Videos.ProviderOptions["provider"]["future"]!["keep"]!.GetValue<bool>(), "video settings persist and preserve future provider metadata");
        foreach (var invalid in new[] { new VideoPreferences { N = 11 }, new VideoPreferences { MaxVideosPerCall = 0 }, new VideoPreferences { Duration = 0 }, new VideoPreferences { Fps = -1 }, new VideoPreferences { PollingIntervalSeconds = 4 }, new VideoPreferences { PollingIntervalSeconds = 61 }, new VideoPreferences { Resolution = "../x" }, new VideoPreferences { StorageRoot = "relative" } })
            await RejectAsync(() => { invalid.Validate(); return Task.CompletedTask; }, check, "invalid video preferences rejected");
        await RejectAsync(() => { preferences.Request(model with { ModelType = "image" }, "x", null, [], null, null, 1); return Task.CompletedTask; }, check, "video requests require a type-video model");
        check(DesktopVideoClient.StatusPath("p/token/with+=?") == "api/videos/p/token%2Fwith%2B%3D%3F", "video operation provider and opaque task are encoded separately");
        await RejectAsync(() => { DesktopVideoClient.StatusPath("invalid"); return Task.CompletedTask; }, check, "invalid operation is not polled");
        using var handler = new WireHandler(); using var http = new HttpClient(handler); var host = new VideoHost();
        var session = new DesktopSession(host, new VideoRuntime(), new()); var partition = ImageLibraryStore.Partition(session);
        var wire = new DesktopVideoClient(new(session, http), http);
        var started = await wire.StartAsync(request, partition, CancellationToken.None); await wire.StatusAsync(VideoDisk.Text(started["operation"])!, partition, CancellationToken.None);
        check(handler.Paths.SequenceEqual(["https://test.invalid/ai/api/videos", "https://test.invalid/ai/api/videos/p/task%2Fone"]) && handler.Authenticated && host.Calls == 2, "video start and status use gateway routes and fresh host authentication");
        host.Identity = "other";
        await RejectAsync(async () => await wire.StatusAsync("p/id", partition, CancellationToken.None), check, "video request cannot use credentials from a different account"); host.Identity = "account";
        using var anonymous = new HttpClient(new AnonymousHandler());
        var journal = Path.Combine(root, "journal"); var output = Path.Combine(root, "output"); var fake = new FakeClient(journal);
        await using (var coordinator = new VideoJobCoordinator(journal, fake, anonymous, () => partition, () => 10))
        {
            await coordinator.SubmitAsync(requests, output);
            var pending = await coordinator.SnapshotAsync();
            check(pending.Count == 3 && pending.All(j => j.State == "pending" && j.Operation is not null) && fake.SawDurableDraft && fake.SawPreviousAccepted,
                "video request saved before POST and every accepted operation saved before the next batch");
            check((await new VideoJobStore(journal, partition).LoadAsync()).Count == 3, "all accepted operations are available to a new app instance immediately");
            await coordinator.PollOnceAsync();
            check((await coordinator.SnapshotAsync()).All(j => j.State == "pending"), "queued operations survive polling without disappearing");
        }
        fake.Complete = true;
        await using (var resumed = new VideoJobCoordinator(journal, fake, anonymous, () => partition, () => 10))
        {
            await resumed.StartAsync(); // Startup resumes saved jobs without opening a video page.
            for (var i = 0; i < 100 && (await resumed.SnapshotAsync()).Any(j => j.Pending); i++) await Task.Delay(20);
            check((await resumed.SnapshotAsync()).All(j => j.State == "completed") && fake.Starts == 3, "app startup resumes polling only, never resubmits accepted video generation");
            var library = new VideoLibraryStore(output, partition); var results = await library.ListAsync();
            check(results.Count == 3 && results.All(v => v.Model == "p/finished" && v.Cost == .5), "completed videos commit original files with model and per-output cost metadata");
            var manifest = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(results[0].Path)!, "generation.json"));
            check(!manifest.Contains("host-secret") && !manifest.Contains("headers") && !manifest.Contains("AQID"), "video library metadata excludes credentials, response headers and large input blobs");
            var completed = (await resumed.SnapshotAsync())[0]; await library.SaveAsync(completed, fake.Result(), anonymous);
            check((await library.ListAsync()).Count == 3, "completion commit is idempotent if polled again after a crash");
            await library.DeleteAsync(results[0]); await library.SaveAsync(completed, fake.Result(), anonymous);
            check((await library.ListAsync()).Count == 2, "deletion retains a commit tombstone and cannot be resurrected by repeated completion");
            var bad = Path.Combine(library.Folder, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(bad); await File.WriteAllTextAsync(Path.Combine(bad, "generation.json"), "not json");
            check((await library.ListAsync()).Count == 2, "externally corrupt video manifest does not break library browsing");
            await RejectAsync(() => library.DeleteAsync(results[1] with { Path = Path.Combine(root, "outside.mp4") }), check, "video deletion rejects paths outside its app-owned output");
        }
        var uncertainJournal = Path.Combine(root, "uncertain"); var uncertain = new VideoJob { Partition = partition, OutputRoot = output, RequestedVideos = 1, Request = requests[2] };
        await new VideoJobStore(uncertainJournal, partition).SaveAsync(uncertain);
        var recovered = await new VideoJobStore(uncertainJournal, partition).LoadAsync();
        check(recovered.Single().State == "uncertain" && recovered.Single().Error is not null, "interrupted start with no operation is durable and never automatically resubmitted");
        var partialFake = new FakeClient(Path.Combine(root, "partial")) { FailStartAt = 2 };
        await using (var partial = new VideoJobCoordinator(partialFake.Journal, partialFake, anonymous, () => partition, () => 10))
        {
            await RejectAsync(() => partial.SubmitAsync(requests, output), check, "later start batch failure is surfaced");
            check((await partial.SnapshotAsync()).Count(j => j.State == "pending") == 1 && (await partial.SnapshotAsync()).Count(j => j.State == "uncertain") == 1 && partialFake.Starts == 2,
                "partial batching failure retains accepted first batch and never retries a paid POST");
        }
        var retryFake = new FakeClient(Path.Combine(root, "retry")); var activePartition = partition;
        await using (var retry = new VideoJobCoordinator(retryFake.Journal, retryFake, anonymous, () => activePartition, () => 10))
        {
            await retry.SubmitAsync([requests[2]], output); retryFake.FailStatus = true; await retry.PollOnceAsync();
            check((await retry.SnapshotAsync()).Single().State == "pending" && (await retry.SnapshotAsync()).Single().Error is not null, "transient polling failures preserve saved operation for retry");
            retryFake.FailStatus = false; retryFake.Block = new(TaskCreationOptions.RunContinuationsAsynchronously); var firstPoll = retry.PollOnceAsync();
            await retryFake.Entered.Task; var before = retryFake.Statuses; await retry.PollOnceAsync();
            check(retryFake.Statuses == before, "overlapping polling passes cannot issue duplicate requests"); retryFake.Block.SetResult(); await firstPoll; retryFake.Block = null;
            activePartition = "different-account"; await retry.PollOnceAsync();
            check((await retry.SnapshotAsync()).Count == 0 && retryFake.Statuses == before, "different account cannot see or poll previous account jobs");
            activePartition = partition; retryFake.Error = true; await retry.PollOnceAsync();
            check((await retry.SnapshotAsync()).Single().State == "error", "terminal provider error persists and stops the pending placeholder");
            var count = retryFake.Statuses; await retry.PollOnceAsync(); check(retryFake.Statuses == count, "terminal error is not polled forever");
        }
        var inputStore = new VideoInputStore(Path.Combine(root, "inputs")); var savedInput = await inputStore.AddAsync(image); var readInput = await inputStore.ReadAsync(savedInput);
        check(readInput.Content.Span.SequenceEqual(image.Content.Span), "reference/frame inputs survive restart as owned immutable files");
        await RejectAsync(() => inputStore.ReadAsync(savedInput with { File = "../outside.png" }), check, "saved frame input cannot escape its owned directory");
        var storageFake = new FakeClient(Path.Combine(root, "storage"));
        await using (var storage = new VideoJobCoordinator(storageFake.Journal, storageFake, anonymous, () => partition, () => 10))
        {
            var capturedRoot = Path.Combine(root, "captured"); await storage.SubmitAsync([requests[2]], capturedRoot); storageFake.Complete = true;
            var store = new VideoLibraryStore(capturedRoot, partition); Directory.Delete(store.Folder); await File.WriteAllTextAsync(store.Folder, "blocked storage");
            await storage.PollOnceAsync(); check((await storage.SnapshotAsync()).Single().Pending, "temporary output storage failure keeps the accepted operation");
            File.Delete(store.Folder); await storage.PollOnceAsync();
            check((await store.ListAsync()).Count == 1 && (await new VideoLibraryStore(Path.Combine(root, "new-root"), partition).ListAsync()).Count == 0,
                "storage retry commits to captured destination, never a later chosen folder");
        }
        foreach (var language in new[] { "en", "nl" })
        {
            using var resource = typeof(VideoRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + language)!;
            var values = System.Xml.Linq.XDocument.Load(resource).Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
            foreach (var key in new[] { "Videos", "VideoSettings", "VideoStartUncertain", "VideoProcessing", "VideoStorage", "VideoFirstFrame", "VideoLastFrame" })
                check(values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value), language + " video resource: " + key);
        }
    }
    private static async Task RejectAsync(Func<Task> action, Action<bool, string> check, string label)
    { try { await action(); } catch (Exception e) when (e is InvalidOperationException or IOException or GatewayException or OperationCanceledException) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    private sealed class FakeClient(string journal) : IDesktopVideoClient
    {
        public string Journal => journal;
        public int Starts, Statuses, FailStartAt;
        public bool Complete, FailStatus, Error, SawDurableDraft = true, SawPreviousAccepted = true;
        public TaskCompletionSource? Block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<JsonObject> StartAsync(JsonObject request, string partition, CancellationToken ct)
        {
            Starts++;
            // Read raw records: recovery must not mark a currently submitting draft uncertain.
            var paths = Directory.GetFiles(new VideoJobStore(journal, partition).Folder, "*.json");
            var records = paths.Select(p => JsonNode.Parse(File.ReadAllText(p))!.AsObject()).ToArray();
            SawDurableDraft &= records.Any(r => VideoDisk.Text(r["state"]) == "submitting" && r["request"] is JsonObject);
            SawPreviousAccepted &= records.Count(r => VideoDisk.Text(r["state"]) == "pending") == Starts - 1;
            await Task.Yield(); ct.ThrowIfCancellationRequested(); if (Starts == FailStartAt) throw new GatewayException("failed start");
            return new() { ["operation"] = "p/job-" + Starts, ["warnings"] = new JsonArray("start warning") };
        }
        public async Task<JsonObject> StatusAsync(string operation, string partition, CancellationToken ct)
        {
            Statuses++; if (FailStatus) throw new HttpRequestException(); if (Block is not null) { Entered.TrySetResult(); await Block.Task.WaitAsync(ct); }
            return Error ? new() { ["status"] = "error", ["error"] = "provider failed" } : Complete ? Result() : new() { ["status"] = "pending", ["warnings"] = new JsonArray("queue warning") };
        }
        public JsonObject Result() => JsonNode.Parse("""{"status":"completed","videos":[{"type":"base64","mediaType":"video/mp4","data":"AQID"}],"providerMetadata":{"gateway":{"cost":0.5}},"response":{"modelId":"p/finished","headers":{"set-cookie":"host-secret"}}}""")!.AsObject();
    }
    private sealed class VideoHost : IDesktopHost
    {
        public string ProfileId => "video-tests"; public bool AllowLocal => true; public string AccountLabel => "Test";
        public string Identity = "account"; public string HistoryIdentity => Identity; public int Calls;
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) { Calls++; request.Headers.Add("x-provider-key", "host-secret"); return Task.CompletedTask; }
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class VideoRuntime : IRuntimeResolver
    { public Task<Uri> ResolveAsync(ServiceKind kind, DesktopSettings settings, CancellationToken ct) => Task.FromResult(new Uri("https://test.invalid/ai/")); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class WireHandler : HttpMessageHandler
    {
        public List<string> Paths = []; public bool Authenticated = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsoluteUri); Authenticated &= request.Headers.Contains("x-provider-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Post ? "{\"operation\":\"p/task/one\"}" : "{\"status\":\"pending\"}", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class AnonymousHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Inline output must not contact a media URL."); }
}
