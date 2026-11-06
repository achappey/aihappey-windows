using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class ModelCatalogRegressionTests
{
    private static JsonElement Response(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var store = new DesktopModelCatalogStore(root);
        var original = Response("""{"object":"list","futureList":true,"data":[{"id":"p/model","type":"language","created":5,"future":{"nested":[1,2]},"pricing":{"input":"0.1","future":7}},{"id":"p/model","type":"decision"}]}""");
        await store.SaveAsync("account/endpoint", original, default);
        var saved = await store.LoadAsync("account/endpoint", default);
        check(saved.HasValue && JsonElement.DeepEquals(saved.Value, original), "model cache: complete V1 response and unknown fields round-trip");
        var projected = DesktopModelCatalog.Project(saved!.Value);
        check(projected.Count == 2 && projected[0].ModelMetadata!.Value.GetProperty("future").GetProperty("nested").GetArrayLength() == 2,
            "model cache: UI projection preserves raw model metadata and distinct model types");
        check(await store.LoadAsync("other-account/endpoint", default) is null, "model cache: missing partition is a miss, not an empty catalog");

        foreach (var text in new[] { "{broken", "{\"version\":2}", "{\"version\":\"1\"}",
            """{"version":1,"sources":{"gateway":{"updatedAt":1,"response":{"data":[{"id":""}]}}}}""" })
        {
            var corrupt = "corrupt-" + text;
            await File.WriteAllTextAsync(Path.Combine(root, McpValidation.Hash(corrupt) + ".json"), text);
            check(await store.LoadAsync(corrupt, default) is null, "model cache: corrupt/unsupported snapshot is ignored");
        }
        var oversized = Path.Combine(root, McpValidation.Hash("oversized") + ".json");
        using (var stream = File.Create(oversized)) stream.SetLength(DesktopModelCatalogStore.MaxBytes + 1L);
        check(await store.LoadAsync("oversized", default) is null, "model cache: oversized storage is bounded before parsing");
        await store.ClearAsync("account/endpoint", default);
        check(await store.LoadAsync("account/endpoint", default) is null, "model cache: clear removes only the requested scope");

        var blocked = Path.Combine(root, "not-a-directory"); await File.WriteAllTextAsync(blocked, "file");
        var unavailable = new DesktopModelCatalogStore(blocked);
        await unavailable.SaveAsync("scope", original, default);
        check(await unavailable.LoadAsync("scope", default) is null, "model cache: unavailable storage remains best effort");

        var started = Signal<bool>(); var remote = Signal<JsonElement>(); var changed = Signal<IReadOnlyList<ChatTarget>>(); var calls = 0;
        await store.SaveAsync("repeat", original, default);
        using (var catalog = new DesktopModelCatalog(() => "repeat", _ => { Interlocked.Increment(ref calls); started.TrySetResult(true); return remote.Task; }, store, default))
        {
            catalog.Changed += (_, items) => changed.TrySetResult(items);
            var cached = await catalog.ListAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(cached.Count == 2 && !remote.Task.IsCompleted, "model cache: repeat visit returns local models without waiting for discovery");
            await catalog.ListAsync(default); await catalog.ListAsync(default);
            check(calls == 1, "model cache: concurrent views share one background refresh");
            remote.SetResult(Response("""{"data":[{"id":"p/fresh","type":"language","future":42}]}"""));
            var refreshed = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(refreshed.Single().Id == "p/fresh" && (await catalog.ListAsync(default)).Single().Id == "p/fresh",
                "model cache: successful background refresh publishes and updates memory");
            check((await store.LoadAsync("repeat", default))!.Value.GetProperty("data")[0].GetProperty("future").GetInt32() == 42,
                "model cache: successful refresh persists full JSON");
        }

        var failed = Signal<bool>(); var failureRemote = Signal<JsonElement>();
        using (var catalog = new DesktopModelCatalog(() => "repeat", _ => failureRemote.Task, store, default))
        {
            catalog.RefreshFailed += _ => failed.TrySetResult(true);
            await catalog.ListAsync(default); failureRemote.SetException(new IOException("offline"));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check((await catalog.ListAsync(default)).Single().Id == "p/fresh", "model cache: failed background refresh retains last usable catalog");
            try { await catalog.ListAsync(default, fresh: true); throw new Exception("Expected explicit refresh failure"); }
            catch (IOException) { check((await catalog.ListAsync(default)).Single().Id == "p/fresh", "model cache: explicit refresh reports failure without destroying cached models"); }
        }

        using (var catalog = new DesktopModelCatalog(() => "repeat", _ => Task.FromResult(Response("{\"data\":[]}")), store, default))
        {
            check((await catalog.ListAsync(default, fresh: true)).Count == 0
                && (await store.LoadAsync("repeat", default))!.Value.GetProperty("data").GetArrayLength() == 0,
                "model cache: successful empty catalog authoritatively replaces old models");
        }

        var coldStarted = Signal<bool>(); var coldRemote = Signal<JsonElement>(); calls = 0;
        using (var catalog = new DesktopModelCatalog(() => "cold", _ => { Interlocked.Increment(ref calls); coldStarted.TrySetResult(true); return coldRemote.Task; }, unavailable, default))
        using (var caller = new CancellationTokenSource())
        {
            var first = catalog.ListAsync(caller.Token); var second = catalog.ListAsync(default);
            await coldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            try { await first; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            coldRemote.SetResult(original);
            check((await second.WaitAsync(TimeSpan.FromSeconds(5))).Count == 2 && calls == 1,
                "model cache: cold requests coalesce and canceling one caller does not cancel another or fail on a cache write");
        }

        var activeScope = "old"; var oldStarted = Signal<bool>(); var oldRemote = Signal<JsonElement>(); var updates = new List<string>();
        await store.SaveAsync("new", original, default);
        using (var catalog = new DesktopModelCatalog(() => activeScope, _ => { oldStarted.TrySetResult(true); return oldRemote.Task; }, store, default))
        {
            catalog.Changed += (key, _) => updates.Add(key);
            var old = catalog.ListAsync(default); await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            activeScope = "new";
            check((await catalog.ListAsync(default)).Count == 2, "model cache: new context hydrates only its own snapshot");
            oldRemote.SetResult(original);
            try { await old; throw new Exception("Expected obsolete context cancellation"); }
            catch (OperationCanceledException) { check(await store.LoadAsync("old", default) is null, "model cache: obsolete discovery cannot publish or persist"); }
        }

        var host = new ScopeHost(); var settings = new DesktopSettings { Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ai.test" } };
        var session = new DesktopSession(host, new TestRuntime(), settings); var scope = session.ModelCatalogScope;
        host.Credentials = "other-fingerprint"; check(scope != session.ModelCatalogScope, "model cache: credential fingerprints isolate catalogs without changing history identity");
        host.Credentials = "fingerprint"; host.Identity = "other"; check(scope != session.ModelCatalogScope, "model cache: account changes isolate catalogs");
        host.Identity = "account"; settings.Ai.RemoteUrl = "https://other.test"; check(scope != session.ModelCatalogScope, "model cache: AI endpoint changes isolate catalogs");
        settings.Ai.RemoteUrl = "https://ai.test"; settings.Agents.RemoteUrl = "https://agents.test";
        check(scope == session.ModelCatalogScope, "model cache: unrelated agent endpoint changes do not change AI cache identity");
    }

    private sealed class ScopeHost : IDesktopHost
    {
        public string ProfileId => "model-cache-tests";
        public string Identity = "account", Credentials = "fingerprint";
        public string HistoryIdentity => Identity;
        public string ModelCatalogCredentialScope => Credentials;
        public string AccountLabel => "fixture";
        public bool AllowLocal => true;
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => Task.CompletedTask;
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
}
