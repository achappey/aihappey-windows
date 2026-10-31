using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class GitHubSkillRegressionTests
{
    private static readonly string Commit = new('a', 40);
    private const string Manifest = "---\nname: sample\ndescription: >-\n  Use this sample when testing.\nmetadata:\n  version: \"1\"\n---\nRead references/info.md.";
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        foreach (var url in new[] { "https://github.com/owner/repo", "https://github.com/owner/repo.git/", "https://github.com/owner/repo/tree/feature/branch/skills/sample", "https://github.com/owner/repo/blob/main/skills/sample/SKILL.md", "https://raw.githubusercontent.com/owner/repo/refs/heads/main/skills/sample/SKILL.md" })
            check(GitHubSkillsClient.ParseUrl(url).Repo == "repo", "GitHub: supported URL " + url);
        foreach (var url in new[] { "http://github.com/owner/repo", "https://github.com.evil.test/owner/repo", "https://secret@github.com/owner/repo", "https://github.com/owner/repo/blob/main/other.md", "https://github.com/owner/repo/tree/main/%2e%2e/private", "https://github.com/owner/repo/tree/main/a%2fb" })
            await RejectAsync(() => Task.FromResult(GitHubSkillsClient.ParseUrl(url)), "InvalidUrl", check);

        using var handler = new Handler(); using var http = new HttpClient(handler); var client = new GitHubSkillsClient(http);
        var updates = 0; var found = await client.DiscoverAsync("https://github.com/owner/repo", default, _ => updates++);
        check(handler.Calls.Count == 3 && handler.ZipCount == 1 && handler.Calls.All(u => !u.Contains("/git/") && !u.Contains("/contents/")),
            "GitHub: Windows resolves commit and downloads one ZIP, never trees/contents/blobs per file");
        check(updates > 1 && found.Candidates.Any(c => c.Path == "skills/broken") && found.Candidates.Any(c => c.Path == "skills/.curated/nested")
            && !found.Candidates.Any(c => c.Path == "skills/.curated/nested/scripts") && !found.Candidates.Any(c => c.Path == "src/ignored"),
            "GitHub: local discovery lists unvalidated folder candidates and respects grouping boundaries");
        var sample = found.Candidates.Single(c => c.Path == "skills/sample"); var requests = handler.Calls.Count;
        var bytes = await client.DownloadAsync(found, sample, default);
        var parsed = await DesktopSkillPackages.ImportAsync(bytes, default); var draft = parsed.Skills.Single();
        check(parsed.Diagnostics.Count == 0 && draft.Description == "Use this sample when testing." && draft.Files.Count == 4
            && draft.Files.Single(f => f.Path == "assets/logo.bin").Data.SequenceEqual(new byte[] { 0, 255, 128, 1 })
            && draft.Files.Single(f => f.Path == "assets/a file #é.bin").Data.SequenceEqual(new byte[] { 255, 0, 123 }),
            "GitHub: complete selected directory, real YAML, scripts and binary/Unicode assets survive existing import");
        await client.DownloadAsync(found, sample, default);
        check(handler.Calls.Count == requests && handler.ZipCount == 1, "GitHub: repeated selected downloads are entirely local and reuse cached ZIP");
        await RejectAsync(() => client.DownloadAsync(found, found.Candidates.Single(c => c.Path == "skills/broken"), default), "MissingManifest", check);

        var invalid = found.Candidates.Single(c => c.Path == "skills/invalid");
        var invalidImport = await DesktopSkillPackages.ImportAsync(await client.DownloadAsync(found, invalid, default), default);
        check(invalidImport.Skills.Count == 0 && invalidImport.Diagnostics.Count > 0, "GitHub: invalid candidate is rejected strictly only after selection");
        var store = new DesktopLocalSkillStore(Path.Combine(root, "github-local-skills"));
        var first = await store.SaveAsync("account", draft, null, default);
        var previous = (await store.ListAsync("account", default)).Single(s => s.Name == draft.Name);
        var second = await store.SaveAsync("account", draft, previous.Id, default);
        check(first.Id == second.Id && first.Version == "1" && second.Version == "2" && (await store.ReadAsync("account", first, default)).Files.Count == 4,
            "GitHub: existing name-based duplicate import appends a version and preserves old contents");

        var folder = await client.DiscoverAsync("https://github.com/owner/repo/tree/main/skills/sample", default);
        check(folder.Candidates.Select(c => c.Path).Order().SequenceEqual(new[] { "skills/sample", "skills/sample/assets", "skills/sample/references", "skills/sample/scripts" }.Order())
            && handler.ZipCount == 1, "GitHub: explicit folder only lists itself and immediate children using cached ZIP");
        var direct = await client.DiscoverAsync("https://github.com/owner/repo/blob/feature/branch/skills/sample/SKILL.md", default);
        check(direct.Candidates.Count == 1 && direct.Candidates[0].Path == "skills/sample" && direct.Commit == Commit && handler.ZipCount == 1,
            "GitHub: slash-containing ref resolves and direct manifest targets a single pinned directory");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel(); var count = handler.Calls.Count;
            try { await client.DownloadAsync(found, sample, stop.Token); check(false, "GitHub: canceled selection rejected"); }
            catch (OperationCanceledException) { check(handler.Calls.Count == count, "GitHub: canceled selection does not download or publish a package"); }
        }
        foreach (var status in new[] { 429, 503 })
        {
            using var failing = new Handler { ZipStatus = status }; using var failingHttp = new HttpClient(failing);
            var retry = new GitHubSkillsClient(failingHttp);
            await RejectAsync(() => retry.DiscoverAsync("https://github.com/owner/repo", default), status == 429 ? "RateLimit" : "Download", check);
            failing.ZipStatus = 200;
            var recovered = await retry.DiscoverAsync("https://github.com/owner/repo", default);
            check(recovered.Candidates.Count > 0 && failing.ZipCount == 2, "GitHub: failed/rate-limited ZIP is never cached; retry succeeds");
        }
        using (var corrupt = new Handler { Bytes = [0, 1, 2] })
        using (var corruptHttp = new HttpClient(corrupt))
        {
            var retry = new GitHubSkillsClient(corruptHttp);
            await RejectAsync(() => retry.DiscoverAsync("https://github.com/owner/repo", default), "Download", check);
            corrupt.Bytes = Archive(); await retry.DiscoverAsync("https://github.com/owner/repo", default);
            check(corrupt.ZipCount == 2, "GitHub: invalid ZIP is not cached");
        }
        foreach (var lang in new[] { "en", "nl" })
        {
            var document = System.Xml.Linq.XDocument.Load(typeof(GitHubSkillRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + lang)!);
            var values = document.Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
            check(new[] { "SkillFromGitHub", "SkillGitHubHint", "SkillGitHubRateLimit", "SkillGitHubMissingManifest", "SkillGitHubInvalidUrl" }.All(k => values[k].Length > 0), "GitHub: localized selector and errors " + lang);
        }
    }
    private static async Task RejectAsync(Func<Task> action, string code, Action<bool, string> check)
    {
        try { await action(); check(false, "GitHub: expected rejection " + code); }
        catch (GitHubSkillException error) { check(error.Code == code, "GitHub: error category " + code); }
    }
    private static byte[] Archive()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void File(string path, byte[] data) { using var stream = zip.CreateEntry("repo-" + Commit + "/" + path).Open(); stream.Write(data); }
            void Text(string path, string text) => File(path, Encoding.UTF8.GetBytes(text));
            Text("skills/sample/SKILL.md", Manifest); Text("skills/sample/scripts/run.py", "print('never run')");
            Text("skills/sample/references/info.md", "A reference"); File("skills/sample/assets/logo.bin", [0, 255, 128, 1]);
            File("skills/sample/assets/a file #é.bin", [255, 0, 123]);
            Text("skills/.curated/nested/SKILL.md", Manifest.Replace("name: sample", "name: nested"));
            Text("skills/.curated/nested/scripts/run.py", "print('no')"); Text("skills/broken/not-a-manifest.txt", "invalid candidate");
            Text("skills/invalid/SKILL.md", Manifest.Replace("name: sample", "name: Invalid"));
            Text("src/ignored/nested/file.txt", "never import this"); Text("direct/SKILL.md", Manifest.Replace("name: sample", "name: direct"));
        }
        return output.ToArray();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public int ZipCount { get; private set; }
        public int ZipStatus { get; set; } = 200;
        public byte[] Bytes { get; set; } = Archive();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var url = request.RequestUri!; Calls.Add(url.AbsoluteUri);
            if (request.Method != HttpMethod.Get || request.Headers.Authorization is not null) throw new InvalidOperationException("Only anonymous GET expected");
            if (url.Host == "codeload.github.com")
            {
                if (url.AbsolutePath != "/owner/repo/zip/" + Commit) throw new InvalidOperationException("Archive must be commit-pinned");
                ZipCount++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)ZipStatus) { Content = new ByteArrayContent(Bytes) });
            }
            var path = Uri.UnescapeDataString(url.AbsolutePath);
            object? json = path == "/repos/owner/repo" ? new { @private = false, default_branch = "main" }
                : path is "/repos/owner/repo/commits/main" or "/repos/owner/repo/commits/feature/branch" ? new { sha = Commit } : null;
            return Task.FromResult(new HttpResponseMessage(json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(json)) });
        }
    }
}
