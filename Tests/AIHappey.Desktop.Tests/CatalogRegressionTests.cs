using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class CatalogRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, Action<Action, string> reject, string root, DesktopSession session)
    {
        using var doc = JsonDocument.Parse("""
            {"id":"backend-id","name":"Display name","agent":{"name":"Definition name","description":"Explain documents","instructions":"Be concise","model":{"id":"openai/model"},"future":{"kept":true}},"created":null}
            """);
        var agent = CatalogProjection.Agent(doc.RootElement)!;
        check(agent.Id == "backend-id" && agent.Name == "Display name" && agent.Model == "openai/model" && agent.Description == "Explain documents", "catalog preserves backend ID separately from display name and underlying model");
        check(agent.Created is null && agent.CanDownload && agent.Definition!.Value.GetProperty("future").GetProperty("kept").GetBoolean(), "agent null metadata and unknown definition properties survive");
        check(CatalogProjection.Search([agent], "DOCUMENTS").Count == 1 && CatalogProjection.Search([agent], "openai/model").Count == 1, "overview search covers description and model case-insensitively");
        check(agent.Key != (agent with { Origin = CatalogOrigin.Local }).Key, "backend and future user-created local items have separate identities");
        using var noDefinition = JsonDocument.Parse("""{"id":"opaque-agent","created":"unknown"}""");
        check(!CatalogProjection.Agent(noDefinition.RootElement)!.CanDownload && CatalogProjection.Agent(noDefinition.RootElement)!.Created is null, "opaque agents remain browsable without fake definition export");
        foreach (var id in new[] { "../escape", "provider/..", "provider/a/b", "provider/a%2fb", "provider/a\\b", "provider/" })
            reject(() => CatalogRoutes.Skill(id), "unsafe skill route rejected: " + id);
        reject(() => CatalogRoutes.Version(".."), "skill version traversal rejected");
        check(CatalogRoutes.Skill("provider/skill name") == "v1/skills/provider/skill%20name" && CatalogRoutes.Version("release 1") == "release%201", "skill path segments are escaped independently");

        var store = new CatalogFavoritesStore(Path.Combine(root, "favorites"));
        var partition = HistoryStore.Partition("catalog", "account", "managed-ai", "managed-agents");
        await store.SaveAsync(partition, [agent.Key, agent.Key]);
        check((await store.LoadAsync(partition)).SetEquals([agent.Key]), "catalog favorites atomic round trip and de-duplication");
        check((await store.LoadAsync(HistoryStore.Partition("other-account"))).Count == 0, "catalog favorites isolate account/connection partitions");
        await store.SaveAsync(partition, []);
        check((await store.LoadAsync(partition)).Count == 0 && !Directory.EnumerateFiles(Path.Combine(root, "favorites"), "*.tmp").Any(), "unfavorite persists and atomic temporary files are cleaned");
        reject(() => store.SaveAsync("../escape", []).GetAwaiter().GetResult(), "favorites storage traversal rejected");

        using var handler = new CatalogHandler();
        using var http = new HttpClient(handler);
        var catalog = new DesktopCatalogClient(new DesktopChatClient(session, http), http);
        var agents = await catalog.ListAsync(CatalogKind.Agent, CancellationToken.None);
        check(agents.Single().Id == "backend-id" && handler.Paths.Single().EndsWith("/v1/models"), "agent overview reads agents backend model catalog");
        handler.Paths.Clear();
        var skills = await catalog.ListAsync(CatalogKind.Skill, CancellationToken.None);
        check(skills.Count == 2 && handler.Paths.Count == 2 && handler.Paths[1].Contains("after=provider%2Fone"), "skills pagination follows escaped qualified last ID");
        var versions = await catalog.VersionsAsync("provider/one", CancellationToken.None);
        check(versions.Single().Version == "1", "skill version projection");
        var bytes = await catalog.DownloadSkillAsync("provider/one", "1", CancellationToken.None);
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            check(zip.Entries.Single().FullName == "SKILL.md", "authenticated explicit skill archive download is retained without extraction");
        check(handler.SawAuth && handler.Paths.Last().EndsWith("/versions/1/content"), "catalog and content requests use existing host authentication and gateway routes");
        handler.Mode = "repeat";
        try { await catalog.ListAsync(CatalogKind.Skill, CancellationToken.None); throw new Exception("Expected repeated cursor refusal"); }
        catch (GatewayException) { check(true, "repeated pagination cursor is rejected"); }
        foreach (var mode in new[] { "redirect", "badzip", "oversize", "failure" })
        {
            handler.Mode = mode;
            try { await catalog.DownloadSkillAsync("provider/one", null, CancellationToken.None); throw new Exception("Expected archive refusal"); }
            catch (GatewayException error) { check(!error.Message.Contains("sensitive"), "skill download refuses " + mode + " with sanitized error"); }
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await catalog.ListAsync(CatalogKind.Agent, canceled.Token); throw new Exception("Expected catalog cancellation"); }
            catch (OperationCanceledException) { check(true, "catalog request cancellation"); }
        }

        var now = DateTimeOffset.UtcNow;
        var chats = Enumerable.Range(0, 65).Select(index => new Conversation
        {
            Title = "Chat " + index, Updated = now.AddMinutes(-index), Service = index == 1 ? ServiceKind.Agents : ServiceKind.Ai, Target = "target-" + index,
            Messages = [new() { Message = new UIMessage { Id = "m", Role = Role.user, Parts = [new TextUIPart { Text = new string('x', 120) + " SEARCHABLE message " + new string('y', 220) }] } }]
        }).ToArray();
        check(ConversationSearch.Find(chats, "").Count == 6 && ConversationSearch.Find(chats.Reverse(), "")[0].Conversation == chats[0], "search modal defaults to six latest conversations");
        check(ConversationSearch.Find(chats, "searchable").Count == 50, "chat text search caps distinct results at fifty");
        var hit = ConversationSearch.Find(chats, "Chat 1").First();
        check(hit.Conversation == chats[1] && hit.Conversation.Service == ServiceKind.Agents && hit.Conversation.Target == "target-1", "title search retains correct agent service and target");
        var snippet = ConversationSearch.Find([chats[0], chats[0]], "SEARCHABLE").Single().Snippet!;
        check(snippet.Contains("SEARCHABLE") && snippet.StartsWith('…') && snippet.EndsWith('…') && snippet.Length <= 202, "first message match yields a bounded snippet and one result per conversation");
        check(ConversationSearch.Find(chats, "not found").Count == 0 && ConversationSearch.Find([], "").Count == 0, "conversation search empty and no-results states");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { ConversationSearch.Find(chats, "searchable", canceled.Token); throw new Exception("Expected search cancellation"); }
            catch (OperationCanceledException) { check(true, "superseded conversation searches support cancellation"); }
        }
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string Mode { get; set; } = "normal";
        public bool SawAuth { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Paths.Add(request.RequestUri!.PathAndQuery); SawAuth = request.Headers.Contains("X-Test-Key");
            if (Mode is "redirect" or "failure") return Task.FromResult(new HttpResponseMessage(Mode == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.InternalServerError) { Content = new StringContent("sensitive") });
            if (request.RequestUri.AbsolutePath.EndsWith("/content"))
            {
                using var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(zip.CreateEntry("SKILL.md").Open())) writer.Write("Read-only fixture");
                var content = new ByteArrayContent(Mode == "badzip" ? Encoding.UTF8.GetBytes("not a ZIP") : buffer.ToArray());
                content.Headers.ContentType = new("application/zip");
                if (Mode == "oversize") content.Headers.ContentLength = DesktopCatalogClient.MaxDownloadBytes + 1L;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            var json = request.RequestUri.AbsolutePath.EndsWith("/models") ? """{"data":[{"id":"backend-id","agent":{"name":"Agent","model":{"id":"model"}}}]}"""
                : request.RequestUri.AbsolutePath.EndsWith("/versions") ? """{"data":[{"id":"version-id","version":"1"}],"has_more":false}"""
                : Mode != "repeat" && request.RequestUri.Query.Contains("after=") ? """{"data":[{"id":"provider/two","name":"Two","default_version":"1"}],"has_more":false}"""
                : """{"data":[{"id":"provider/one","name":"One","default_version":"1"}],"has_more":true,"last_id":"provider/one"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
