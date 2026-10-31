using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

public sealed class GitHubSkillException(string code, string? detail = null) : Exception(
    DesktopResources.Get("SkillGitHub" + code) + (detail is null ? "" : " " + detail))
{
    public string Code { get; } = code;
}
public sealed record GitHubSkillUrl(string Owner, string Repo, string Kind, string[] Segments);
public sealed record GitHubSkillCandidate(string Path, string Name, string Tree);
public sealed record GitHubSkillDiscovery(string Owner, string Repo, string Commit, IReadOnlyList<GitHubSkillCandidate> Candidates);

/// <summary>One bounded public repository ZIP per commit. Discovery uses local directory metadata only.</summary>
public sealed class GitHubSkillsClient(HttpClient http)
{
    private const int MaxJsonBytes = 8 * 1024 * 1024;
    private const int MaxCandidates = 500;
    private const int MaxDiscoveryRequests = 32;
    private static readonly string[] Collections = ["skills", ".agents/skills", ".claude/skills", ".github/skills", ".copilot/skills"];
    private sealed record Entry(string Path, int Index, int Mode, long Size);
    private sealed record Repository(string Key, byte[] Bytes, Entry[] Files, HashSet<string> Directories);
    private Repository? repository;
    private static bool Sha(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{40}\\z");
    public static GitHubSkillUrl ParseUrl(string input)
    {
        try
        {
            if (Regex.IsMatch(input, "[\\\\\\x00-\\x1f\\x7f]|(?:/|%2f)(?:\\.|%2e){1,2}(?:/|%2f|$)", RegexOptions.IgnoreCase)) throw new FormatException();
            var url = new Uri(input.Trim(), UriKind.Absolute);
            if (url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo) || !url.IsDefaultPort
                || url.Host is not ("github.com" or "raw.githubusercontent.com")) throw new FormatException();
            var parts = url.AbsolutePath.TrimEnd('/').TrimStart('/').Split('/').Select(Uri.UnescapeDataString).ToList();
            if (parts.Count < 2) throw new FormatException();
            var owner = parts[0]; var repo = Regex.Replace(parts[1], "\\.git$", ""); parts.RemoveRange(0, 2);
            if (!Regex.IsMatch(owner, "\\A[A-Za-z0-9-]+\\z") || !Regex.IsMatch(repo, "\\A[A-Za-z0-9_.-]+\\z") || repo is "." or "..") throw new FormatException();
            if (parts.Count == 0 && url.Host == "github.com") return new(owner, repo, "repository", []);
            var mode = "raw";
            if (url.Host == "github.com") { mode = parts[0]; parts.RemoveAt(0); }
            if (mode is not ("tree" or "blob" or "raw") || parts.Count == 0) throw new FormatException();
            foreach (var part in parts) { if (part.Contains('/')) throw new FormatException(); SkillFiles.RelativePath(part); }
            var kind = mode == "tree" ? "folder" : "file";
            if (kind == "file" && parts[^1] != "SKILL.md") throw new FormatException();
            if (parts.Count >= 2 && parts[0] == "refs" && parts[1] is "heads" or "tags") parts.RemoveRange(0, 2);
            if (parts.Count > 32) throw new FormatException();
            return new(owner, repo, kind, parts.ToArray());
        }
        catch (Exception error) when (error is FormatException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        { throw new GitHubSkillException("InvalidUrl"); }
    }
    private async Task<byte[]> BytesAsync(string url, int limit, CancellationToken ct, bool archive = false)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AIHappey-Skills");
        request.Headers.Accept.ParseAdd(archive ? "application/zip" : "application/vnd.github+json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden
                && (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0") || response.Headers.RetryAfter is not null))
                throw new GitHubSkillException("RateLimit");
            if (!response.IsSuccessStatusCode) throw new GitHubSkillException(response.StatusCode == HttpStatusCode.NotFound ? "NotFound" : "Download", "HTTP " + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > limit) throw new GitHubSkillException("Limit");
            return await DesktopCatalogClient.ReadBoundedAsync(response.Content, limit, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new GitHubSkillException("Download"); }
        catch (HttpRequestException) { throw new GitHubSkillException("Download"); }
        catch (GatewayException) { throw new GitHubSkillException("Limit"); }
    }
    private async Task<JsonElement> JsonAsync(string url, CancellationToken ct)
    {
        try { using var json = JsonDocument.Parse(await BytesAsync(url, MaxJsonBytes, ct)); return json.RootElement.Clone(); }
        catch (JsonException) { throw new GitHubSkillException("Download"); }
    }
    private async Task<Repository> RepositoryAsync(string owner, string repo, string commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (!Sha(commit)) throw new GitHubSkillException("Download");
        var key = owner + "/" + repo + "/" + commit;
        if (repository?.Key == key) return repository;
        var bytes = await BytesAsync($"https://codeload.github.com/{owner}/{repo}/zip/{commit}", DesktopCatalogClient.MaxDownloadBytes, ct, true);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count is 0 or > 100000) throw new GitHubSkillException("DiscoveryLimit");
            var files = new List<Entry>(); var directories = new HashSet<string>(StringComparer.Ordinal) { "" };
            string? root = null; long pathBytes = 0;
            for (var index = 0; index < zip.Entries.Count; index++)
            {
                ct.ThrowIfCancellationRequested(); var entry = zip.Entries[index];
                if ((pathBytes += entry.FullName.Length) > MaxJsonBytes) throw new GitHubSkillException("DiscoveryLimit");
                var slash = entry.FullName.IndexOf('/'); if (slash <= 0) throw new GitHubSkillException("Download");
                root ??= entry.FullName[..(slash + 1)];
                if (!entry.FullName.StartsWith(root, StringComparison.Ordinal)) throw new GitHubSkillException("UnsafePath");
                var path = entry.FullName[root.Length..]; if (path.Length == 0) continue;
                var directory = path.EndsWith('/'); path = SkillFiles.RelativePath(directory ? path.TrimEnd('/') : path);
                var parts = path.Split('/');
                for (var i = 1; i < parts.Length; i++) directories.Add(string.Join('/', parts.Take(i)));
                if (directory) directories.Add(path);
                else files.Add(new(path, index, (entry.ExternalAttributes >> 16) & 0xf000, entry.Length));
            }
            ct.ThrowIfCancellationRequested();
            // Publish the cache only after the complete download and archive index succeeded.
            return repository = new(key, bytes, files.ToArray(), directories);
        }
        catch (InvalidDataException) { throw new GitHubSkillException("Download"); }
    }
    public async Task<GitHubSkillDiscovery> DiscoverAsync(string input, CancellationToken ct, Action<GitHubSkillDiscovery>? changed = null)
    {
        var target = ParseUrl(input); var baseUrl = $"https://api.github.com/repos/{target.Owner}/{target.Repo}";
        var requests = 0;
        void Budget() { if (++requests > MaxDiscoveryRequests) throw new GitHubSkillException("DiscoveryLimit"); }
        Budget(); var repo = await JsonAsync(baseUrl, ct);
        if (!repo.TryGetProperty("private", out var privateValue) || privateValue.ValueKind != JsonValueKind.False) throw new GitHubSkillException("NotFound");
        JsonElement commit = default; var path = "";
        if (target.Kind == "repository")
        { Budget(); commit = await JsonAsync(baseUrl + "/commits/" + Uri.EscapeDataString(repo.GetProperty("default_branch").GetString()!), ct); }
        else
        {
            // Longest matching ref supports slash-containing branch and tag names.
            for (var i = Math.Min(target.Segments.Length - (target.Kind == "file" ? 1 : 0), 16); i > 0; i--)
            {
                try
                {
                    Budget(); commit = await JsonAsync(baseUrl + "/commits/" + Uri.EscapeDataString(string.Join('/', target.Segments.Take(i))), ct);
                    path = string.Join('/', target.Segments.Skip(i)); break;
                }
                catch (GitHubSkillException error) when (error.Code == "NotFound") { }
            }
        }
        if (commit.ValueKind != JsonValueKind.Object || !commit.TryGetProperty("sha", out var id) || !Sha(id.GetString()))
            throw new GitHubSkillException("NotFound");
        var commitSha = id.GetString()!;
        var source = await RepositoryAsync(target.Owner, target.Repo, commitSha, ct);
        var candidates = new List<GitHubSkillCandidate>();
        GitHubSkillDiscovery Snapshot() => new(target.Owner, target.Repo, id.GetString()!, candidates.ToArray());
        void Add(string folder)
        {
            ct.ThrowIfCancellationRequested();
            if (candidates.Any(c => c.Path == folder)) return;
            if (candidates.Count >= MaxCandidates) throw new GitHubSkillException("DiscoveryLimit");
            candidates.Add(new(folder, folder.Length == 0 ? target.Repo : folder.Split('/')[^1], commitSha)); changed?.Invoke(Snapshot());
        }
        if (target.Kind == "file")
        {
            var folder = string.Join('/', path.Split('/').SkipLast(1));
            if (!source.Directories.Contains(folder)) throw new GitHubSkillException("NotFound");
            Add(folder); return Snapshot();
        }
        if (target.Kind == "folder")
        {
            if (!source.Directories.Contains(path)) throw new GitHubSkillException("NotFound"); Add(path);
            var prefix = path.Length == 0 ? "" : path + "/";
            foreach (var directory in source.Directories.Order(StringComparer.Ordinal))
                if (directory.StartsWith(prefix, StringComparison.Ordinal) && directory.Length > prefix.Length && !directory[prefix.Length..].Contains('/')) Add(directory);
            return Snapshot();
        }
        Add("");
        foreach (var directory in source.Directories.Order(StringComparer.Ordinal))
            if (directory.Length > 0 && !directory.Contains('/') && !Collections.Any(c => c.Split('/')[0] == directory)) Add(directory);
        foreach (var collection in Collections)
        {
            var prefix = collection + "/";
            foreach (var directory in source.Directories.Order(StringComparer.Ordinal))
                if (directory.StartsWith(prefix, StringComparison.Ordinal) && directory[prefix.Length..].Split('/').Length <= 2) Add(directory);
        }
        return Snapshot();
    }
    public async Task<byte[]> DownloadAsync(GitHubSkillDiscovery discovery, GitHubSkillCandidate candidate, CancellationToken ct)
    {
        if (!discovery.Candidates.Contains(candidate)) throw new GitHubSkillException("InvalidSkill");
        var source = await RepositoryAsync(discovery.Owner, discovery.Repo, discovery.Commit, ct);
        var prefix = candidate.Path.Length == 0 ? "" : candidate.Path + "/";
        var files = source.Files.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (!files.Any(e => e.Path == prefix + "SKILL.md")) throw new GitHubSkillException("MissingManifest");
        if (files.Length > SkillFiles.MaxFiles) throw new GitHubSkillException("Limit");
        long total = 0; var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested(); var relative = SkillFiles.RelativePath(file.Path[prefix.Length..]);
            if (!paths.Add(relative) || file.Mode is not (0 or 0x8000)) throw new GitHubSkillException("UnsafePath");
            if (file.Size < 0 || (total += file.Size) > SkillFiles.MaxBytes) throw new GitHubSkillException("Limit");
            if (relative != "SKILL.md") DesktopSkillPackages.ResourcePath(relative);
        }
        if (paths.Any(p => p.Split('/').SkipLast(1).Select((_, i) => string.Join('/', p.Split('/').Take(i + 1))).Any(paths.Contains))) throw new GitHubSkillException("UnsafePath");
        using var output = new MemoryStream();
        using var repositoryZip = new ZipArchive(new MemoryStream(source.Bytes), ZipArchiveMode.Read);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await using var input = repositoryZip.Entries[file.Index].Open(); using var content = new StreamContent(input);
                var bytes = await DesktopCatalogClient.ReadBoundedAsync(content, (int)file.Size, ct);
                if (bytes.LongLength != file.Size) throw new GitHubSkillException("Download", file.Path);
                using var stream = zip.CreateEntry(candidate.Name + "/" + file.Path[prefix.Length..]).Open(); await stream.WriteAsync(bytes, ct);
            }
        }
        ct.ThrowIfCancellationRequested(); return output.ToArray();
    }
}
